// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kantor.Core.Results;

/// <summary>Posición de un jugador respecto del puntaje más alto.</summary>
public readonly record struct Standing(bool IsWinner, bool IsTie);

/// <summary>
/// Quién gana y quién empata en una pantalla de 1 a 4 jugadores. No toca el sello ni el comentario de nadie:
/// solo decide el énfasis visual. Con un único jugador no hay ganador. Si dos o más comparten el puntaje más
/// alto, todos ellos quedan "empatados" (empate múltiple) y los demás siguen sin marca.
/// </summary>
public static class ResultStandings
{
    public static Standing[] Compute(IReadOnlyList<int> scores)
    {
        var result = new Standing[scores.Count];
        if (scores.Count < 2)
        {
            return result;
        }

        var best = scores.Max();
        var leaders = scores.Count(s => s == best);
        for (var i = 0; i < scores.Count; i++)
        {
            if (scores[i] != best)
            {
                continue;
            }

            result[i] = leaders == 1 ? new Standing(true, false) : new Standing(false, true);
        }

        return result;
    }
}
