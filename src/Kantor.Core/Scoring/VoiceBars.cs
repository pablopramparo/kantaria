// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Songs;

namespace Kantor.Core.Scoring;

/// <summary>Una barra de la voz del jugador: <see cref="Length"/> beats seguidos en el mismo tono.</summary>
public readonly record struct VoiceBar(int StartBeat, int Length, int Tone, bool Hit, bool Rap)
{
    public int EndBeat => StartBeat + Length;
}

public static class VoiceBars
{
    /// <summary>
    /// Agrupa los resultados por beat en barras: beats contiguos con el mismo tono mostrado, el mismo
    /// resultado y la misma clase (rap o con tono) forman una sola. Solo entran beats desde
    /// <paramref name="fromBeat"/>, para dibujar únicamente la ventana visible.
    /// </summary>
    public static List<VoiceBar> Build(IReadOnlyList<BeatResult> results, int fromBeat = int.MinValue)
    {
        var bars = new List<VoiceBar>();
        foreach (var r in results)
        {
            if (r.Beat < fromBeat)
            {
                continue;
            }

            var rap = r.Kind is NoteKind.Rap or NoteKind.RapGolden;
            if (bars.Count > 0)
            {
                var last = bars[^1];
                if (r.Beat == last.EndBeat && r.DisplayTone == last.Tone && r.Hit == last.Hit && rap == last.Rap)
                {
                    bars[^1] = last with { Length = last.Length + 1 };
                    continue;
                }

                // Dos notas solapadas pueden dar el mismo beat dos veces: se muestra una sola vez.
                if (r.Beat < last.EndBeat && r.DisplayTone == last.Tone && r.Hit == last.Hit && rap == last.Rap)
                {
                    continue;
                }
            }

            bars.Add(new VoiceBar(r.Beat, 1, r.DisplayTone, r.Hit, rap));
        }

        return bars;
    }
}
