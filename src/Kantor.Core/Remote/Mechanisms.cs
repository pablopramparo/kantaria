// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Kantor.Core.Remote;

/// <summary>La respuesta a un comando, ya resuelta: se puede devolver igual ante un reintento con el mismo <c>commandId</c>.</summary>
public sealed record CachedResponse(bool Ok, long Rev, string? PayloadJson, string? ErrorCode, string? ErrorMessage,
    string? DetailsJson)
{
    public JsonNode? PayloadNode => PayloadJson is null ? null : JsonNode.Parse(PayloadJson);
}

/// <summary>Resultado de <see cref="CommandDeduplicator.Begin"/>.</summary>
public abstract record DedupeDecision
{
    /// <summary>Primera vez que llega ese <c>commandId</c>: hay que ejecutarlo.</summary>
    public sealed record Execute : DedupeDecision;

    /// <summary>Ya llegó con el mismo contenido: se espera (o se toma) la respuesta original.</summary>
    public sealed record Replay(Task<CachedResponse> Response) : DedupeDecision;

    /// <summary>El mismo <c>commandId</c> con otro comando o payload.</summary>
    public sealed record Reused : DedupeDecision;
}

/// <summary>
/// Deduplicación de mutaciones por <c>commandId</c> (hasta 1024 entradas o 15 minutos). La huella es el SHA-256 del
/// tipo y el payload canónico: un reintento idéntico devuelve la misma respuesta sin volver a ejecutar; otro
/// contenido con el mismo id se rechaza. Vive en memoria: un reinicio de Kantaria lo vacía.
/// </summary>
public sealed class CommandDeduplicator(Func<DateTimeOffset>? now = null, int capacity = 1024, TimeSpan? ttl = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    private sealed record Entry(string Fingerprint, TaskCompletionSource<CachedResponse> Completion, DateTimeOffset At);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public static string Fingerprint(string type, JsonObject payload)
    {
        var text = type + "\n" + Json.Canonical(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public DedupeDecision Begin(string commandId, string fingerprint)
    {
        lock (_gate)
        {
            Purge();
            if (_entries.TryGetValue(commandId, out var existing))
            {
                return existing.Fingerprint == fingerprint
                    ? new DedupeDecision.Replay(existing.Completion.Task)
                    : new DedupeDecision.Reused();
            }

            _entries[commandId] = new Entry(fingerprint,
                new TaskCompletionSource<CachedResponse>(TaskCreationOptions.RunContinuationsAsynchronously), _now());
            _order.Enqueue(commandId);
            while (_entries.Count > capacity && _order.TryDequeue(out var oldest))
            {
                _entries.Remove(oldest);
            }

            return new DedupeDecision.Execute();
        }
    }

    /// <summary>Resuelve a los que esperan. Con <paramref name="remember"/> falso (p. ej. no llegó a ejecutarse) el id queda libre.</summary>
    public void Complete(string commandId, CachedResponse response, bool remember = true)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(commandId, out entry))
            {
                return;
            }

            if (!remember)
            {
                _entries.Remove(commandId);
            }
        }

        entry.Completion.TrySetResult(response);
    }

    private void Purge()
    {
        var limit = _now() - _ttl;
        while (_order.TryPeek(out var key))
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                _order.Dequeue();
                continue;
            }

            if (entry.At > limit)
            {
                break;
            }

            _order.Dequeue();
            _entries.Remove(key);
        }
    }
}

public enum LivenessVerdict
{
    Alive,
    Dead,
}

/// <summary>
/// Decide si la conexión sigue viva. Muerta = 15 s sin ningún marco entrante más un ciclo de gracia de 5 s. Si el
/// propio vigilante se atrasó (suspensión, proceso congelado: más de <paramref name="stall"/> entre dos revisiones),
/// el atraso no se le cobra al cliente: el contador vuelve a empezar.
/// </summary>
public sealed class PipeLiveness(TimeSpan? timeout = null, TimeSpan? grace = null, TimeSpan? stall = null)
{
    private readonly TimeSpan _timeout = timeout ?? Protocol.LivenessTimeout;
    private readonly TimeSpan _grace = grace ?? Protocol.LivenessGrace;
    private readonly TimeSpan _stall = stall ?? TimeSpan.FromSeconds(5);
    private TimeSpan _lastActivity;
    private TimeSpan? _lastCheck;
    private readonly object _gate = new();

    /// <summary>Cantidad de veces que se reinició el contador por un atraso propio.</summary>
    public int StallResets { get; private set; }

    public PipeLiveness Start(TimeSpan now)
    {
        Touch(now);
        return this;
    }

    /// <summary>Llegó un marco (cualquiera): se registra antes de despacharlo.</summary>
    public void Touch(TimeSpan now)
    {
        lock (_gate)
        {
            _lastActivity = now;
        }
    }

    public LivenessVerdict Check(TimeSpan now)
    {
        lock (_gate)
        {
            if (_lastCheck is { } last && now - last > _stall)
            {
                _lastActivity = now;
                StallResets++;
            }

            _lastCheck = now;
            return now - _lastActivity > _timeout + _grace ? LivenessVerdict.Dead : LivenessVerdict.Alive;
        }
    }
}

/// <summary>Una sección del estado: lo que se publica y la clave con que se decide si cambió (sin campos volátiles).</summary>
public readonly record struct SectionValue(JsonNode? Node, string StableKey);

/// <summary>Un cambio publicado: la revisión nueva y las secciones que cambiaron (cada una completa).</summary>
public sealed record StatePublication(long Rev, JsonObject Changed);

/// <summary>
/// Estado publicado y su revisión. Cada actualización con al menos una sección distinta avanza la revisión en 1,
/// una sola vez aunque haya cambiado más de una sección.
/// </summary>
public sealed class StatePublisher
{
    private readonly Dictionary<string, string> _stable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonNode?> _nodes = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _rev;

    public long Rev
    {
        get
        {
            lock (_gate)
            {
                return _rev;
            }
        }
    }

    /// <summary>Todas las secciones (copias) y la revisión vigente.</summary>
    public (long Rev, JsonObject Sections) Snapshot()
    {
        lock (_gate)
        {
            var sections = new JsonObject();
            foreach (var (name, node) in _nodes)
            {
                sections[name] = node?.DeepClone();
            }

            return (_rev, sections);
        }
    }

    /// <summary>Aplica las secciones; devuelve la publicación o <c>null</c> si nada cambió.</summary>
    public StatePublication? Update(IReadOnlyDictionary<string, SectionValue> sections)
    {
        lock (_gate)
        {
            var changed = new JsonObject();
            foreach (var (name, value) in sections)
            {
                if (_stable.TryGetValue(name, out var previous) && previous == value.StableKey)
                {
                    // sin cambios: se refresca el nodo (campos volátiles) para los snapshots, sin publicar
                    _nodes[name] = value.Node?.DeepClone();
                    continue;
                }

                _stable[name] = value.StableKey;
                _nodes[name] = value.Node?.DeepClone();
                changed[name] = value.Node?.DeepClone();
            }

            if (changed.Count == 0)
            {
                return null;
            }

            _rev++;
            return new StatePublication(_rev, changed);
        }
    }
}
