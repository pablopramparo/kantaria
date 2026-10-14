// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Pitch;
using Kantor.Core.Songs;

namespace Kantor.Core.Scoring;

/// <summary>Reglas de acierto de un beat, sin estado ni conocimiento del origen del tono.</summary>
public static class NoteScoring
{
    /// <summary>Valor de un beat de la nota: normal y rap 1, dorada y rap dorada 2, freestyle 0.</summary>
    public static int Factor(NoteKind kind) => kind switch
    {
        NoteKind.Normal or NoteKind.Rap => 1,
        NoteKind.Golden or NoteKind.RapGolden => 2,
        _ => 0,
    };

    /// <summary>Las notas doradas y rap doradas suman a una bolsa aparte, que se redondea al revés.</summary>
    public static bool IsGolden(NoteKind kind) => kind is NoteKind.Golden or NoteKind.RapGolden;

    public static bool IsRap(NoteKind kind) => kind is NoteKind.Rap or NoteKind.RapGolden;

    /// <summary>Distancia circular entre dos tonos como clases de altura (0 a 6 semitonos); no distingue octavas.</summary>
    public static int PitchClassDistance(int toneA, int toneB)
    {
        var d = ((toneA - toneB) % 12 + 12) % 12;
        return Math.Min(d, 12 - d);
    }

    /// <summary>
    /// Desvío del tono cantado respecto de la nota, llevado por octavas completas a [-6, +6]. El signo dice
    /// si se canta por encima o por debajo; +6 y -6 se conservan como vienen (a 6 semitonos no hay "más cerca").
    /// </summary>
    public static int OffsetWithinOctave(int sungTone, int noteTone)
    {
        var offset = (sungTone - noteTone) % 12;
        if (offset > 6)
        {
            offset -= 12;
        }
        else if (offset < -6)
        {
            offset += 12;
        }

        return offset;
    }

    /// <summary>
    /// Evalúa un beat contra una muestra: <c>null</c> si no hay nada que mostrar ni puntuar (nota sin puntaje,
    /// por debajo de la compuerta, o nota con tono sin voz detectada). Si hay sonido, devuelve si acertó y el
    /// tono a mostrar: en un acierto, el de la nota; en un fallo, el tono real a ±6 semitonos de la nota.
    /// </summary>
    public static BeatEvaluation? Evaluate(NoteKind kind, int noteTone, PitchSample sample, ScoringRules rules)
    {
        if (Factor(kind) == 0 || sample.LevelDb < rules.LevelGateDb)
        {
            return null;
        }

        // El rap acierta con sonido y se muestra sobre la nota, sin comparar tono.
        if (IsRap(kind))
        {
            return new BeatEvaluation(true, noteTone);
        }

        if (!sample.IsVoiced)
        {
            return null;
        }

        var sung = MusicalPitch.FromFrequency(sample.Frequency).UltraStarTone;
        var offset = OffsetWithinOctave(sung, noteTone);
        return Math.Abs(offset) <= rules.Difficulty.ToleranceSemitones()
            ? new BeatEvaluation(true, noteTone)
            : new BeatEvaluation(false, noteTone + offset);
    }

    /// <summary>
    /// ¿Esta muestra acierta el beat de una nota? Con sonido por encima de la compuerta: el rap acierta
    /// siempre; las notas con tono necesitan voz detectada y una clase de altura dentro de la tolerancia.
    /// </summary>
    public static bool IsHit(NoteKind kind, int noteTone, PitchSample sample, ScoringRules rules) =>
        Evaluate(kind, noteTone, sample, rules) is { Hit: true };
}

/// <summary>Resultado de comparar una muestra con un beat: si acertó y a qué tono se muestra.</summary>
public readonly record struct BeatEvaluation(bool Hit, int DisplayTone);
