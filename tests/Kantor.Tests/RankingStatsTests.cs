// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Data;
using Xunit;

namespace Kantor.Tests;

public class RankingStatsTests
{
    private static PerformanceView Song(string id, params (long Player, string Name, int Score)[] results)
    {
        var positions = ScoreRanking.Positions(results.Select(r => r.Score).ToList());
        var record = new PerformanceRecord(id, 1, "song-" + id, "T" + id, "A", DateTimeOffset.UnixEpoch, 0, 1);
        return new PerformanceView(record, results
            .Select((r, i) => new PerformanceResultView(r.Player, r.Name, i + 1, r.Score, positions[i])).ToList());
    }

    private static int[] Points(params int[] scores)
    {
        var positions = ScoreRanking.Positions(scores);
        return RankingStats.CompetitionPoints(scores
            .Select((s, i) => new PerformanceResultView(i + 1, "P" + i, i + 1, s, positions[i])).ToList());
    }

    [Fact]
    public void NoSongsMeansNoStandings() => Assert.Empty(RankingStats.Compute([]));

    [Fact]
    public void CompetitionPointsGoByPlaceAndTheLastOneGetsNone()
    {
        Assert.Equal([1, 0], Points(9000, 4000));
        Assert.Equal([2, 1, 0], Points(9000, 6000, 3000));
        Assert.Equal([3, 2, 1, 0], Points(100, 300, 200, 50).OrderByDescending(x => x).ToArray());
    }

    [Fact]
    public void SingingAloneGivesNoCompetitionPoints() => Assert.Equal([0], Points(9000));

    [Fact]
    public void TiesShareThePointsAndTheTiedLastGetNone()
    {
        Assert.Equal([2, 2, 0], Points(9000, 9000, 5000)); // empate arriba: ambos 2º... 3 − 1
        Assert.Equal([2, 0, 0], Points(9000, 5000, 5000)); // empate en el último puesto: ninguno suma
        Assert.Equal([2, 2, 2], Points(5000, 5000, 5000)); // todos empatados: nadie perdió
    }

    [Fact]
    public void TotalsCompetitionEffectivenessAndWinsAccumulatePerPlayer()
    {
        var standings = RankingStats.Compute(
        [
            Song("1", (1, "Ana", 9000), (2, "Beto", 7000)),
            Song("2", (1, "Ana", 6000), (2, "Beto", 8000)),
            Song("3", (2, "Beto", 5000)),
        ]);

        // empatan en competición (1 cada uno); manda la efectividad: Ana 75 % contra Beto 66,7 %
        var ana = standings[0];
        Assert.Equal("Ana", ana.Name);
        Assert.Equal(1, ana.CompetitionPoints);
        Assert.Equal(7500, ana.Average);
        Assert.Equal(75.0, ana.Effectiveness, 3);
        Assert.Equal(15000, ana.Total);
        Assert.Equal(9000, ana.Best);

        var beto = standings[1];
        Assert.Equal(1, beto.CompetitionPoints);
        Assert.Equal(3, beto.Songs);
        Assert.Equal(66.666, beto.Effectiveness, 2);
        Assert.Equal(1, beto.Wins); // cantando solo no se gana
        Assert.Equal([1, 2], standings.Select(s => s.Position));
    }

    [Fact]
    public void IdenticalResultsShareThePosition()
    {
        var standings = RankingStats.Compute([Song("1", (1, "Ana", 5000), (2, "Beto", 5000))]);
        Assert.All(standings, s => Assert.Equal(1, s.Position));
        Assert.All(standings, s => Assert.Equal(1, s.Wins));
    }
}
