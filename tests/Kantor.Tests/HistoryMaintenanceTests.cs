// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Data;
using Kantor.Core.Scoring;
using Xunit;

namespace Kantor.Tests;

public sealed class HistoryMaintenanceTests : IDisposable
{
    private readonly TickingClock _clock = new();
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    private static NewPerformance Perf(string id, int difficulty, params ResultInput[] results) =>
        new(id, SongIdentity.For("Artista", "Canción " + id), "Canción " + id, "Artista, Banda", difficulty,
            ScoringRules.ScoringVersion, results);

    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kantor-test-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    [Fact]
    public void ClosedSessionIsDeletedWithItsPerformancesAndPlayersRemain()
    {
        using var store = new KantorStore(":memory:", _clock.Next);
        var ana = store.CreatePlayer("Ana");
        var beto = store.CreatePlayer("Beto");
        var session = store.StartSession(selection: [new SelectionEntry(1, ana.Id), new SelectionEntry(2, beto.Id)]);
        store.SavePerformance(Perf("a", 0, new ResultInput(ana.Id, 1, 9000), new ResultInput(beto.Id, 2, 7000)));
        store.CloseSession(session.Id);

        Assert.True(store.DeleteSession(session.Id));

        Assert.Empty(store.ListSessions());
        Assert.Empty(store.GetAllHistory());
        Assert.NotNull(store.GetPlayer(ana.Id));
        Assert.Empty(store.GetPlayerHistory(ana.Id));
    }

    [Fact]
    public void OpenSessionCannotBeDeleted()
    {
        using var store = new KantorStore(":memory:", _clock.Next);
        var ana = store.CreatePlayer("Ana");
        var session = store.StartSession(selection: [new SelectionEntry(1, ana.Id)]);

        Assert.False(store.DeleteSession(session.Id));
        Assert.Single(store.ListSessions());
        Assert.False(store.DeleteSession(9999));
    }

    [Fact]
    public void DeletingOneSessionLeavesTheOthers()
    {
        using var store = new KantorStore(":memory:", _clock.Next);
        var ana = store.CreatePlayer("Ana");
        var first = store.StartSession(selection: [new SelectionEntry(1, ana.Id)]);
        store.SavePerformance(Perf("a", 0, new ResultInput(ana.Id, 1, 5000)));
        var second = store.StartSession(selection: [new SelectionEntry(1, ana.Id)]); // cierra la primera
        store.SavePerformance(Perf("b", 0, new ResultInput(ana.Id, 1, 6000)));

        Assert.True(store.DeleteSession(first.Id));

        var remaining = Assert.Single(store.GetAllHistory());
        Assert.Equal(second.Id, remaining.Performance.SessionId);
    }

    [Fact]
    public void BackupIsAReadableCopyAndRefusesToOverwrite()
    {
        var path = TempFile();
        using (var store = new KantorStore(":memory:", _clock.Next))
        {
            var ana = store.CreatePlayer("Ana");
            store.StartSession(selection: [new SelectionEntry(1, ana.Id)]);
            store.SavePerformance(Perf("a", 0, new ResultInput(ana.Id, 1, 8000)));
            store.BackupTo(path);
            Assert.Throws<IOException>(() => store.BackupTo(path));
        }

        using var copy = new KantorStore(path, _clock.Next);
        Assert.Equal("Ana", Assert.Single(copy.ListPlayers()).Name);
        Assert.Equal(8000, Assert.Single(Assert.Single(copy.GetAllHistory()).Results).Score);
    }

    [Fact]
    public void CsvHasOneRowPerResultWithQuotingAndCompetitionPoints()
    {
        using var store = new KantorStore(":memory:", _clock.Next);
        var ana = store.CreatePlayer("Ana");
        var beto = store.CreatePlayer("Beto");
        store.StartSession("Fiesta", [new SelectionEntry(1, ana.Id), new SelectionEntry(2, beto.Id)]);
        store.SavePerformance(Perf("a", 2, new ResultInput(ana.Id, 1, 9000), new ResultInput(beto.Id, 2, 7000)));

        var csv = HistoryExport.ToCsv(store.GetAllHistory(), store.ListSessions());
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(HistoryExport.Header, lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"Artista, Banda\"", lines[1]); // la coma obliga a entrecomillar
        Assert.EndsWith(",dificil,Ana,1,9000,1,1", lines[1].Substring(lines[1].IndexOf("Canción a", StringComparison.Ordinal) + "Canción a".Length));
        Assert.EndsWith(",Beto,2,7000,2,0", lines[2]);
    }

    [Fact]
    public void MinimumSongsFilterDropsPlayersWithFewerSongsAndRenumbersPositions()
    {
        using var store = new KantorStore(":memory:", _clock.Next);
        var ana = store.CreatePlayer("Ana");
        var beto = store.CreatePlayer("Beto");
        store.StartSession(selection: [new SelectionEntry(1, ana.Id), new SelectionEntry(2, beto.Id)]);
        store.SavePerformance(Perf("a", 0, new ResultInput(ana.Id, 1, 4000), new ResultInput(beto.Id, 2, 9900)));
        store.SavePerformance(Perf("b", 0, new ResultInput(ana.Id, 1, 4000)));

        var all = store.GetAllHistory();

        Assert.Equal(2, RankingStats.Compute(all).Count);
        var filtered = Assert.Single(RankingStats.Compute(all, minSongs: 2));
        Assert.Equal("Ana", filtered.Name);
        Assert.Equal(1, filtered.Position);
    }
}
