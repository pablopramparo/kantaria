// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Security.Cryptography;
using System.Text;

namespace Kantor.Core.Guest;

/// <summary>
/// Credencial compartida del acceso de invitados: 128 bits aleatorios, válida desde que se emite hasta que se revoca.
/// Quien tenga el enlace del QR puede usar las funciones limitadas de invitados mientras el acceso esté activo.
/// No identifica a nadie ni da acceso a nada más. Nunca se escribe en registros.
/// </summary>
public sealed class GuestAccess
{
    private readonly object _gate = new();
    private byte[]? _bytes;
    private string? _text;

    /// <summary>Hay un token vigente.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _text is not null;
            }
        }
    }

    /// <summary>El token vigente (para armar el enlace del QR); <c>null</c> si el acceso está cerrado.</summary>
    public string? Current
    {
        get
        {
            lock (_gate)
            {
                return _text;
            }
        }
    }

    /// <summary>Emite un token nuevo; el anterior deja de valer.</summary>
    public string Issue()
    {
        var raw = RandomNumberGenerator.GetBytes(16);
        var text = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate)
        {
            _bytes = Encoding.ASCII.GetBytes(text);
            _text = text;
        }

        return text;
    }

    /// <summary>Revoca el token: desde ahora ninguna solicitud es válida.</summary>
    public void Revoke()
    {
        lock (_gate)
        {
            _bytes = null;
            _text = null;
        }
    }

    /// <summary>Comparación en tiempo constante; un token ausente, vacío o de otro largo no vale.</summary>
    public bool Validate(string? candidate)
    {
        byte[]? expected;
        lock (_gate)
        {
            expected = _bytes;
        }

        if (expected is null || string.IsNullOrEmpty(candidate) || candidate.Length > 64)
        {
            return false;
        }

        var given = Encoding.UTF8.GetBytes(candidate);
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }
}

/// <summary>
/// Límite de solicitudes por clave (la dirección del cliente) en una ventana deslizante. El reloj es inyectable
/// para las pruebas. Seguro entre hilos.
/// </summary>
public sealed class GuestRateLimiter(int limit, TimeSpan window, Func<DateTimeOffset>? clock = null)
{
    private const int SweepAbove = 1024;

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Registra un uso. <c>false</c> si la clave ya llegó al límite dentro de la ventana (el intento no cuenta).</summary>
    public bool TryAcquire(string key)
    {
        lock (_gate)
        {
            var now = _clock();
            var queue = Prune(key, now);
            if (queue.Count >= limit)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    /// <summary>Cuántos usos lleva la clave dentro de la ventana, sin registrar uno nuevo.</summary>
    public int Count(string key)
    {
        lock (_gate)
        {
            return Prune(key, _clock()).Count;
        }
    }

    /// <summary>Registra un uso aunque ya esté al límite (para contar fallos sin cortar el conteo).</summary>
    public void Register(string key)
    {
        lock (_gate)
        {
            var now = _clock();
            var queue = Prune(key, now);
            if (queue.Count < limit)
            {
                queue.Enqueue(now);
            }
        }
    }

    public bool IsBlocked(string key) => Count(key) >= limit;

    private Queue<DateTimeOffset> Prune(string key, DateTimeOffset now)
    {
        if (_hits.Count > SweepAbove)
        {
            foreach (var stale in _hits.Where(h => h.Value.Count == 0 || now - h.Value.Peek() > window)
                         .Select(h => h.Key).ToList())
            {
                _hits.Remove(stale);
            }
        }

        if (!_hits.TryGetValue(key, out var queue))
        {
            _hits[key] = queue = new Queue<DateTimeOffset>();
        }

        while (queue.Count > 0 && now - queue.Peek() > window)
        {
            queue.Dequeue();
        }

        return queue;
    }
}
