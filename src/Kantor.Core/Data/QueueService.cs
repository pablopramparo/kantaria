// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Data;

/// <summary>
/// La cola de canciones: una sola implementación que usan la interfaz local y el control remoto. Con partida
/// abierta vive en SQLite (sobrevive al cierre de Kantor); sin partida (cantar suelto) vive en memoria. Todas las
/// operaciones corren de a una (las serializa un candado) y cada cambio incrementa <see cref="Rev"/>.
/// </summary>
public sealed class QueueService
{
    public const int MaxPending = 100;

    private readonly KantorStore? _store;
    private readonly Func<long?> _sessionId;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private List<QueueEntry> _loose = [];
    private long? _scope;
    private bool _scopeKnown;
    private int _rev = 1;

    /// <param name="sessionId">Partida a la que pertenece la cola ahora; <c>null</c> = cola suelta (en memoria).</param>
    public QueueService(KantorStore? store, Func<long?> sessionId, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _sessionId = sessionId;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Revisión de la cola: crece con cada cambio; sirve para detectar ediciones concurrentes.</summary>
    public int Rev
    {
        get
        {
            lock (_gate)
            {
                SyncScope();
                return _rev;
            }
        }
    }

    /// <summary>La cola de la partida actual se guarda en disco (sobrevive al cierre de Kantor).</summary>
    public bool Persistent => _store is not null && _sessionId() is not null;

    /// <summary>Hubo un cambio (también el de otra pantalla u origen). Se dispara fuera del candado.</summary>
    public event Action? Changed;

    // ---- Lectura ----------------------------------------------------------------------------------------------

    /// <summary>Elementos visibles (sin los quitados). Con <paramref name="sessionId"/>, la cola de esa partida.</summary>
    public IReadOnlyList<QueueEntry> List(long? sessionId = null, bool includeFinished = false)
    {
        lock (_gate)
        {
            SyncScope();
            var all = sessionId is { } id && id != _scope ? Read(id) : Current();
            var visible = all.Where(e => e.State != QueueState.Removed
                                         && (includeFinished || e.State is QueueState.Pending or QueueState.Running))
                .ToList();
            return [.. visible.Select((e, i) => e with { Order = i })];
        }
    }

    /// <summary>Solo los pendientes de la cola actual, en orden.</summary>
    public IReadOnlyList<QueueEntry> Pending()
    {
        lock (_gate)
        {
            SyncScope();
            return [.. Current().Where(e => e.State == QueueState.Pending).Select((e, i) => e with { Order = i })];
        }
    }

    public QueueEntry? Running()
    {
        lock (_gate)
        {
            SyncScope();
            return Current().FirstOrDefault(e => e.State == QueueState.Running);
        }
    }

    /// <summary>El elemento de la cola actual; con <paramref name="includeRemoved"/> también uno ya quitado.</summary>
    public QueueEntry? Find(string itemId, bool includeRemoved = false)
    {
        lock (_gate)
        {
            SyncScope();
            return Current().FirstOrDefault(e => e.ItemId == itemId && (includeRemoved || e.State != QueueState.Removed));
        }
    }

    // ---- Operaciones ------------------------------------------------------------------------------------------

    public QueueEntry Add(string libraryEntryId, string songIdentity, string artist, string title, string? edition,
        bool atNext = false, IReadOnlyList<QueueSlot>? slots = null, string? addedById = null, string? addedByName = null)
    {
        QueueEntry added;
        lock (_gate)
        {
            SyncScope();
            var list = Current();
            if (list.Count(e => e.State == QueueState.Pending) >= MaxPending)
            {
                throw new QueueException(QueueError.Full, "La cola está llena.");
            }

            added = new QueueEntry(Guid.NewGuid().ToString("N"), libraryEntryId, songIdentity, artist, title, edition,
                QueueState.Pending, 0, slots ?? [], addedById, addedByName, _now(), null, null);
            var at = list.Count;
            if (atNext)
            {
                var firstPending = list.FindIndex(e => e.State == QueueState.Pending);
                at = firstPending < 0 ? list.Count : firstPending;
            }

            list.Insert(at, added);
            Commit(list);
        }

        Changed?.Invoke();
        return added;
    }

    public void Remove(string itemId)
    {
        lock (_gate)
        {
            SyncScope();
            var list = Current();
            var index = RequirePending(list, itemId);
            list[index] = list[index] with { State = QueueState.Removed };
            Commit(list);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Mueve un pendiente delante de <paramref name="beforeItemId"/> (<c>null</c> = al final de los pendientes).
    /// Los terminados y el que está en ejecución no se mueven.
    /// </summary>
    public void Move(string itemId, string? beforeItemId, int? expectedRev = null)
    {
        lock (_gate)
        {
            SyncScope();
            CheckRev(expectedRev);
            var list = Current();
            RequirePending(list, itemId);
            if (beforeItemId is not null)
            {
                RequirePending(list, beforeItemId, "beforeItemId");
                if (beforeItemId == itemId)
                {
                    return;
                }
            }

            var slots = list.Select((e, i) => (e, i)).Where(x => x.e.State == QueueState.Pending).Select(x => x.i).ToList();
            var order = slots.Select(i => list[i]).ToList();
            var moving = order.First(e => e.ItemId == itemId);
            order.Remove(moving);
            var target = beforeItemId is null ? order.Count : order.FindIndex(e => e.ItemId == beforeItemId);
            order.Insert(target, moving);
            var changed = false;
            for (var k = 0; k < slots.Count; k++)
            {
                if (!ReferenceEquals(list[slots[k]], order[k]))
                {
                    changed = true;
                }

                list[slots[k]] = order[k];
            }

            if (!changed)
            {
                return;
            }

            Commit(list);
        }

        Changed?.Invoke();
    }

    /// <summary>Lo pone primero entre los pendientes (la próxima).</summary>
    public void SetNext(string itemId, int? expectedRev = null)
    {
        string? first;
        lock (_gate)
        {
            SyncScope();
            CheckRev(expectedRev);
            first = Current().FirstOrDefault(e => e.State == QueueState.Pending)?.ItemId;
            if (first is null)
            {
                RequirePending(Current(), itemId); // reporta por qué no es pendiente
            }
        }

        Move(itemId, first);
    }

    public void SetSlots(string itemId, IReadOnlyList<QueueSlot> slots)
    {
        lock (_gate)
        {
            SyncScope();
            var list = Current();
            var index = RequirePending(list, itemId);
            list[index] = list[index] with { Slots = [.. slots.OrderBy(s => s.Slot)] };
            Commit(list);
        }

        Changed?.Invoke();
    }

    /// <summary>Quita todos los pendientes. Devuelve cuántos.</summary>
    public int Clear(int expectedRev)
    {
        int removed;
        lock (_gate)
        {
            SyncScope();
            CheckRev(expectedRev);
            var list = Current();
            removed = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].State == QueueState.Pending)
                {
                    list[i] = list[i] with { State = QueueState.Removed };
                    removed++;
                }
            }

            if (removed == 0)
            {
                return 0;
            }

            Commit(list);
        }

        Changed?.Invoke();
        return removed;
    }

    // ---- Ciclo de vida de una interpretación -------------------------------------------------------------------

    /// <summary>
    /// El elemento empieza a cantarse: pasa a "en ejecución" (cualquier otro en ejecución se cancela). Un elemento que
    /// no está pendiente no se toca.
    /// </summary>
    public bool MarkRunning(string itemId)
    {
        lock (_gate)
        {
            SyncScope();
            var list = Current();
            var index = list.FindIndex(e => e.ItemId == itemId && e.State == QueueState.Pending);
            if (index < 0)
            {
                return false;
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].State == QueueState.Running)
                {
                    list[i] = list[i] with { State = QueueState.Cancelled, FinishedAt = _now() };
                }
            }

            list[index] = list[index] with { State = QueueState.Running };
            Commit(list);
        }

        Changed?.Invoke();
        return true;
    }

    public void Complete(string itemId, string? performanceId) =>
        Finish(itemId, QueueState.Completed, performanceId);

    public void Cancel(string itemId) => Finish(itemId, QueueState.Cancelled, null);

    private void Finish(string itemId, QueueState state, string? performanceId)
    {
        lock (_gate)
        {
            SyncScope();
            var list = Current();
            var index = list.FindIndex(e => e.ItemId == itemId && e.State == QueueState.Running);
            if (index < 0)
            {
                return;
            }

            list[index] = list[index] with { State = state, FinishedAt = _now(), PerformanceId = performanceId };
            Commit(list);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Al abrir Kantor: un elemento "en ejecución" de una sesión anterior quedó colgado (la aplicación se cerró cantando)
    /// y pasa a cancelado.
    /// </summary>
    public void RecoverInterrupted()
    {
        bool any;
        lock (_gate)
        {
            SyncScope();
            var list = Current();
            any = false;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].State == QueueState.Running)
                {
                    list[i] = list[i] with { State = QueueState.Cancelled, FinishedAt = _now() };
                    any = true;
                }
            }

            if (any)
            {
                Commit(list);
            }
        }

        if (any)
        {
            Changed?.Invoke();
        }
    }

    // ---- Internos ---------------------------------------------------------------------------------------------

    private void CheckRev(int? expected)
    {
        if (expected is { } rev && rev != _rev)
        {
            throw new QueueException(QueueError.Conflict, "La cola cambió: la revisión esperada ya no es la vigente.");
        }
    }

    private static int RequirePending(List<QueueEntry> list, string itemId, string field = "itemId")
    {
        var index = list.FindIndex(e => e.ItemId == itemId);
        if (index < 0)
        {
            throw new QueueException(QueueError.Unknown, "Ese elemento no existe en la cola.", field);
        }

        return list[index].State switch
        {
            QueueState.Pending => index,
            QueueState.Running => throw new QueueException(QueueError.Running, "Ese elemento se está cantando.", field),
            _ => throw new QueueException(QueueError.Gone, "Ese elemento ya no está en la cola.", field),
        };
    }

    /// <summary>Si cambió la partida a la que pertenece la cola, es otra cola: la revisión avanza.</summary>
    private void SyncScope()
    {
        var scope = _store is null ? null : _sessionId();
        if (!_scopeKnown)
        {
            _scopeKnown = true;
            _scope = scope;
            return;
        }

        if (scope != _scope)
        {
            _scope = scope;
            _rev++;
        }
    }

    private List<QueueEntry> Current() => _scope is { } id && _store is not null ? Read(id) : _loose;

    private List<QueueEntry> Read(long sessionId) =>
        _store is null ? [] : [.. _store.GetQueueEntries(sessionId)];

    private void Commit(List<QueueEntry> list)
    {
        if (_scope is { } id && _store is not null)
        {
            _store.SetQueueEntries(id, list);
        }
        else
        {
            _loose = list;
        }

        _rev++;
    }
}
