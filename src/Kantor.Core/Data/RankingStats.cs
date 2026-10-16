// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Data;

/// <summary>
/// Un jugador en un ranking (de una partida o global).
/// </summary>
/// <param name="Position">1 = primero; empatados en (competición, efectividad) comparten puesto (1, 1, 3).</param>
/// <param name="CompetitionPoints">Suma de los puntos de competición de cada canción (ver <see cref="RankingStats"/>).</param>
/// <param name="Wins">Canciones donde tuvo el puntaje más alto entre dos o más jugadores (un empate cuenta para todos).</param>
/// <param name="Total">Suma de puntos musicales.</param>
/// <param name="Average">Promedio de puntos musicales por canción (0–10.000).</param>
/// <param name="Effectiveness">Promedio de puntos musicales como porcentaje del máximo (0–100).</param>
public sealed record PlayerStanding(
    long PlayerId, string Name, int Position, int Songs, int Wins, int CompetitionPoints, int Total, int Average,
    double Effectiveness, int Best);

/// <summary>
/// Rankings a partir de las interpretaciones guardadas. Reglas (versión <see cref="Version"/>):
/// <list type="bullet">
/// <item><b>Puntos musicales</b>: el puntaje de la canción (0–10.000). Es lo que se guarda y no se toca.</item>
/// <item><b>Puntos de competición</b>: por canción cantada por 2 o más jugadores, <c>N − puesto</c> (con 3: 2, 1, 0).
/// Los empates comparten puesto y puntos. Quien queda último no suma (si todos empatan, todos suman <c>N − 1</c>).
/// Cantar solo no da puntos de competición.</item>
/// <item><b>Efectividad</b>: promedio de puntos musicales sobre 10.000, en %, contando todas sus canciones (solo o
/// en competencia). No penaliza a quien cantó menos.</item>
/// <item>Orden: puntos de competición, luego efectividad, luego puntos totales, luego nombre.</item>
/// </list>
/// </summary>
public static class RankingStats
{
    /// <summary>Versión de las reglas de arriba; si cambian, se incrementa.</summary>
    public const int Version = 1;

    /// <summary>Puntos de competición de cada resultado de una interpretación (mismo orden que sus resultados).</summary>
    public static int[] CompetitionPoints(IReadOnlyList<PerformanceResultView> results)
    {
        var n = results.Count;
        var points = new int[n];
        if (n < 2)
        {
            return points;
        }

        var last = results.Max(r => r.Position);
        var allTied = results.All(r => r.Position == 1);
        for (var i = 0; i < n; i++)
        {
            points[i] = !allTied && results[i].Position == last ? 0 : n - results[i].Position;
        }

        return points;
    }

    /// <param name="minSongs">Solo entran al ranking quienes cantaron al menos tantas canciones.</param>
    public static IReadOnlyList<PlayerStanding> Compute(IReadOnlyList<PerformanceView> performances, int minSongs = 1)
    {
        var table = new Dictionary<long, (string Name, int Songs, int Wins, int Competition, int Total, int Best)>();
        foreach (var performance in performances)
        {
            var results = performance.Results;
            var points = CompetitionPoints(results);
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                table.TryGetValue(result.PlayerId, out var row);
                var won = results.Count > 1 && result.Position == 1;
                table[result.PlayerId] = (result.PlayerName, row.Songs + 1, row.Wins + (won ? 1 : 0),
                    row.Competition + points[i], row.Total + result.Score, Math.Max(row.Best, result.Score));
            }
        }

        var ordered = table
            .Where(kv => kv.Value.Songs >= minSongs)
            .Select(kv => (Id: kv.Key, kv.Value))
            .OrderByDescending(x => x.Value.Competition)
            .ThenByDescending(x => x.Value.Total / x.Value.Songs)
            .ThenByDescending(x => x.Value.Total)
            .ThenBy(x => x.Value.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var positions = ScoreRanking.Positions(
            ordered.Select(x => x.Value.Competition * 20_000 + x.Value.Total / x.Value.Songs).ToList());
        return ordered
            .Select((x, i) =>
            {
                var average = x.Value.Total / x.Value.Songs;
                return new PlayerStanding(x.Id, x.Value.Name, positions[i], x.Value.Songs, x.Value.Wins,
                    x.Value.Competition, x.Value.Total, average,
                    x.Value.Total / (double)x.Value.Songs / KantorStore.MaxScore * 100, x.Value.Best);
            })
            .ToList();
    }
}
