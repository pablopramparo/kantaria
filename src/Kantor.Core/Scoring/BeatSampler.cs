// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Pitch;

namespace Kantor.Core.Scoring;

/// <summary>
/// Política temporal para elegir qué análisis de tono corresponde a un instante. Es determinista:
/// depende solo del instante y de las muestras, no de cuándo ni con qué frecuencia se pregunte.
/// </summary>
/// <remarks>
/// Las ventanas de análisis se solapan (cada instante cae en dos). Reglas:
/// 1. Candidatas: muestras cuya ventana cubre el instante, con los extremos incluidos.
/// 2. Gana la de centro más cercano al instante (la que lo ve más de frente).
/// 3. Si dos centros equidistan, gana la anterior en el tiempo.
/// 4. Si ninguna ventana lo cubre (hueco en la captura, silencio sin cuadros, historial descartado),
///    no hay muestra: nunca se usa "la última conocida".
/// </remarks>
public static class BeatSampler
{
    /// <summary>Alcance de la búsqueda alrededor del instante al consultar un historial; sobra para ventanas de ~45 ms.</summary>
    public static readonly TimeSpan SearchReach = TimeSpan.FromMilliseconds(250);

    public static PitchSample? Select(TimeSpan instant, IEnumerable<PitchSample> candidates)
    {
        PitchSample? best = null;
        var bestDistance = TimeSpan.MaxValue;

        foreach (var sample in candidates)
        {
            if (sample.Start > instant || sample.End < instant)
            {
                continue;
            }

            var distance = (sample.Time - instant).Duration();
            if (distance < bestDistance || (distance == bestDistance && sample.Time < best!.Value.Time))
            {
                best = sample;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Puente de huecos cortos: si en <paramref name="instant"/> no hay tono pero hay sonido, y hay tono a
    /// ambos lados a no más de <paramref name="maxSeparation"/> entre sí, devuelve el vecino con voz más
    /// cercano al instante (empate: el anterior) para evaluar con su tono. Nunca extiende el final de una
    /// nota: sin tono posterior, no hay puente. <paramref name="candidates"/> va en orden cronológico.
    /// </summary>
    /// <param name="mightBridgeLater">
    /// Verdadero si hay tono anterior dentro del alcance pero todavía no se vio el posterior: si más tarde
    /// llega uno cercano, el hueco se puentea. Quien llama decide cuánto esperar.
    /// </param>
    public static PitchSample? Bridge(TimeSpan instant, IReadOnlyList<PitchSample> candidates, double gateDb,
        TimeSpan maxSeparation, out bool mightBridgeLater)
    {
        mightBridgeLater = false;
        PitchSample? previous = null;
        PitchSample? following = null;

        foreach (var sample in candidates)
        {
            if (!sample.IsVoiced || sample.LevelDb < gateDb)
            {
                continue;
            }

            if (sample.Time <= instant)
            {
                previous = sample;
            }
            else
            {
                following = sample;
                break;
            }
        }

        if (previous is not { } before || instant - before.Time > maxSeparation)
        {
            return null;
        }

        if (following is not { } after)
        {
            mightBridgeLater = true;
            return null;
        }

        if (after.Time - before.Time > maxSeparation)
        {
            return null;
        }

        return instant - before.Time <= after.Time - instant ? before : after;
    }

    public static PitchSample? Select(TimeSpan instant, PitchTimeline timeline, List<PitchSample> scratch)
    {
        scratch.Clear();
        timeline.CopyRange(instant - SearchReach, instant + SearchReach, scratch);
        return Select(instant, scratch);
    }
}
