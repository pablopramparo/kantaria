// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Data;
using Xunit;

namespace Kantor.Tests;

public sealed class QueueServiceTests : IDisposable
{
    private readonly List<string> _files = [];
    private readonly TickingClock _clock = new();

    public void Dispose()
    {
        foreach (var file in _files.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    private string TempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kantor-queue-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    private static QueueEntry AddSong(QueueService queue, string title, bool next = false) =>
        queue.Add("entry-" + title, "identity-" + title, "Artista", title, null, atNext: next);

    private static string Titles(IEnumerable<QueueEntry> items) => string.Join(",", items.Select(i => i.Title));

    private (KantorStore Store, QueueService Queue, long Session) WithSession(string? path = null)
    {
        var store = new KantorStore(path ?? ":memory:", _clock.Next);
        var ana = store.CreatePlayer("Ana");
        var session = store.StartSession(selection: [new SelectionEntry(1, ana.Id)]);
        return (store, new QueueService(store, () => store.GetOpenSession()?.Id, _clock.Next), session.Id);
    }

    [Fact]
    public void Add_keeps_order_gives_each_item_its_own_id_and_allows_repeats()
    {
        var queue = new QueueService(null, () => null);
        var a = AddSong(queue, "a");
        var b = AddSong(queue, "b");
        var a2 = AddSong(queue, "a");

        Assert.Equal("a,b,a", Titles(queue.Pending()));
        Assert.Equal(3, new[] { a.ItemId, b.ItemId, a2.ItemId }.Distinct().Count());
        Assert.Equal([0, 1, 2], queue.Pending().Select(i => i.Order));
    }

    [Fact]
    public void Add_next_goes_before_the_first_pending_but_after_the_running_one()
    {
        var queue = new QueueService(null, () => null);
        var a = AddSong(queue, "a");
        AddSong(queue, "b");
        queue.MarkRunning(a.ItemId);
        AddSong(queue, "c", next: true);

        Assert.Equal("c,b", Titles(queue.Pending()));
        Assert.Equal("a,c,b", Titles(queue.List()));
    }

    [Fact]
    public void Pending_is_capped_and_finished_items_do_not_count()
    {
        var queue = new QueueService(null, () => null);
        QueueEntry? first = null;
        for (var i = 0; i < QueueService.MaxPending; i++)
        {
            var added = AddSong(queue, i.ToString());
            first ??= added;
        }

        var full = Assert.Throws<QueueException>(() => AddSong(queue, "extra"));
        Assert.Equal(QueueError.Full, full.Error);

        queue.MarkRunning(first!.ItemId);
        queue.Complete(first.ItemId, "perf-1");
        AddSong(queue, "cabe ahora"); // el terminado no ocupa lugar
        Assert.Equal(QueueService.MaxPending, queue.Pending().Count);
    }

    [Fact]
    public void Move_places_an_item_before_another_or_at_the_end()
    {
        var queue = new QueueService(null, () => null);
        var a = AddSong(queue, "a");
        var b = AddSong(queue, "b");
        var c = AddSong(queue, "c");

        queue.Move(c.ItemId, a.ItemId);
        Assert.Equal("c,a,b", Titles(queue.Pending()));
        queue.Move(c.ItemId, null);
        Assert.Equal("a,b,c", Titles(queue.Pending()));
        queue.Move(a.ItemId, a.ItemId); // sobre sí mismo: nada
        Assert.Equal("a,b,c", Titles(queue.Pending()));
        queue.SetNext(c.ItemId);
        Assert.Equal("c,a,b", Titles(queue.Pending()));
        _ = b;
    }

    [Fact]
    public void Move_with_a_stale_revision_conflicts_and_changes_nothing()
    {
        var queue = new QueueService(null, () => null);
        var a = AddSong(queue, "a");
        var b = AddSong(queue, "b");
        var stale = queue.Rev;
        queue.Move(b.ItemId, a.ItemId); // otro cliente reordenó y la revisión avanzó

        var error = Assert.Throws<QueueException>(() => queue.Move(a.ItemId, null, stale));
        Assert.Equal(QueueError.Conflict, error.Error);
        Assert.Equal("b,a", Titles(queue.Pending()));

        queue.Move(a.ItemId, null, queue.Rev); // con la revisión vigente se aplica
        Assert.Equal("b,a", Titles(queue.Pending()));
    }

    [Fact]
    public void Operations_without_an_expected_revision_apply_in_arrival_order_deterministically()
    {
        var queue = new QueueService(null, () => null);
        var a = AddSong(queue, "a");
        var b = AddSong(queue, "b");
        var c = AddSong(queue, "c");

        // dos teléfonos "simultáneos": gana el último en llegar, y el resultado es siempre el mismo
        queue.Move(c.ItemId, a.ItemId);
        queue.Move(b.ItemId, c.ItemId);

        Assert.Equal("b,c,a", Titles(queue.Pending()));
    }

    [Fact]
    public void Unknown_and_gone_items_are_told_apart_and_running_items_are_protected()
    {
        var queue = new QueueService(null, () => null);
        var a = AddSong(queue, "a");
        var b = AddSong(queue, "b");
        queue.Remove(b.ItemId);
        queue.MarkRunning(a.ItemId);

        Assert.Equal(QueueError.Unknown, Assert.Throws<QueueException>(() => queue.Remove("nunca-existio")).Error);
        Assert.Equal(QueueError.Gone, Assert.Throws<QueueException>(() => queue.Remove(b.ItemId)).Error);
        Assert.Equal(QueueError.Running, Assert.Throws<QueueException>(() => queue.Remove(a.ItemId)).Error);
        Assert.Equal(QueueError.Running, Assert.Throws<QueueException>(() => queue.Move(a.ItemId, null)).Error);

        var badBefore = Assert.Throws<QueueException>(() => queue.Move(AddSong(queue, "c").ItemId, "fantasma"));
        Assert.Equal("beforeItemId", badBefore.Field);
    }

    [Fact]
    public void Clear_removes_only_pending_and_requires_the_current_revision()
    {
        var queue = new QueueService(null, () => null);
        var a = AddSong(queue, "a");
        AddSong(queue, "b");
        AddSong(queue, "c");
        queue.MarkRunning(a.ItemId);

        Assert.Equal(QueueError.Conflict, Assert.Throws<QueueException>(() => queue.Clear(queue.Rev - 1)).Error);
        Assert.Equal(2, queue.Clear(queue.Rev));
        Assert.Empty(queue.Pending());
        Assert.Equal("a", Titles(queue.List())); // la que se canta sigue
    }

    [Fact]
    public void A_running_item_completes_or_cancels_and_a_second_start_cancels_the_first()
    {
        var queue = new QueueService(null, () => null);
        var a = AddSong(queue, "a");
        var b = AddSong(queue, "b");
        var c = AddSong(queue, "c");

        queue.MarkRunning(a.ItemId);
        queue.MarkRunning(b.ItemId); // no puede haber dos en ejecución
        queue.Complete(b.ItemId, "perf-b");
        queue.MarkRunning(c.ItemId);
        queue.Cancel(c.ItemId);

        var all = queue.List(includeFinished: true).ToDictionary(i => i.Title);
        Assert.Equal(QueueState.Cancelled, all["a"].State);
        Assert.Equal(QueueState.Completed, all["b"].State);
        Assert.Equal("perf-b", all["b"].PerformanceId);
        Assert.NotNull(all["b"].FinishedAt);
        Assert.Equal(QueueState.Cancelled, all["c"].State);
        Assert.Empty(queue.List()); // sin terminados
    }

    [Fact]
    public void Slots_are_stored_per_item_and_a_running_item_does_not_accept_changes()
    {
        var queue = new QueueService(null, () => null);
        var a = AddSong(queue, "a");

        queue.SetSlots(a.ItemId, [new QueueSlot(2, 7), new QueueSlot(1, 3)]);
        Assert.Equal([new QueueSlot(1, 3), new QueueSlot(2, 7)], queue.Find(a.ItemId)!.Slots);

        queue.MarkRunning(a.ItemId);
        Assert.Throws<QueueException>(() => queue.SetSlots(a.ItemId, []));
    }

    [Fact]
    public void Revision_changes_with_every_change_and_with_the_owning_session()
    {
        var (store, queue, _) = WithSession();
        using (store)
        {
            var before = queue.Rev;
            var a = AddSong(queue, "a");
            Assert.True(queue.Rev > before);

            queue.Remove(a.ItemId);
            var afterRemove = queue.Rev;
            store.StartSession(); // otra partida: es otra cola
            Assert.True(queue.Rev > afterRemove);
            Assert.Empty(queue.Pending());
        }
    }

    [Fact]
    public void Session_queue_survives_reopening_and_a_dangling_running_item_is_cancelled()
    {
        var path = TempDb();
        string runningId;
        long session;
        var (firstStore, firstQueue, id) = WithSession(path);
        using (firstStore)
        {
            var queue = firstQueue;
            session = id;
            AddSong(queue, "a");
            var b = AddSong(queue, "b");
            AddSong(queue, "c");
            queue.MarkRunning(b.ItemId); // Kantor se cierra "cantando"
            runningId = b.ItemId;
        }

        using var reopened = new KantorStore(path, _clock.Next);
        var again = new QueueService(reopened, () => reopened.GetOpenSession()?.Id, _clock.Next);
        Assert.Equal(session, reopened.GetOpenSession()!.Id);
        Assert.Equal("b", again.Running()!.Title);

        again.RecoverInterrupted();

        Assert.Null(again.Running());
        Assert.Equal("a,c", Titles(again.Pending()));
        Assert.Equal(QueueState.Cancelled, again.Find(runningId)!.State);
    }

    [Fact]
    public void A_closed_session_queue_is_readable_by_id_and_a_new_session_starts_empty()
    {
        var (store, queue, first) = WithSession();
        using (store)
        {
            var a = AddSong(queue, "a");
            AddSong(queue, "b");
            queue.MarkRunning(a.ItemId);
            queue.Complete(a.ItemId, "p1");

            store.StartSession(); // cierra la primera

            Assert.Empty(queue.Pending());
            var historic = queue.List(first, includeFinished: true);
            Assert.Equal(["a", "b"], historic.Select(i => i.Title));
            Assert.Equal(QueueState.Completed, historic[0].State);
        }
    }

    [Fact]
    public void Loose_queue_lives_in_memory_and_is_not_persistent()
    {
        var store = new KantorStore(":memory:", _clock.Next);
        using (store)
        {
            var queue = new QueueService(store, () => null);
            AddSong(queue, "a");

            Assert.False(queue.Persistent);
            Assert.Equal("a", Titles(queue.Pending()));
        }
    }

    [Fact]
    public void Changed_is_raised_by_every_mutation()
    {
        var queue = new QueueService(null, () => null);
        var count = 0;
        queue.Changed += () => count++;

        var a = AddSong(queue, "a");
        var b = AddSong(queue, "b");
        queue.SetNext(b.ItemId);
        queue.Remove(a.ItemId);

        Assert.Equal(4, count);
    }
}
