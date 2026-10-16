// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Feedback;

/// <summary>Valoración de una frase cantada, de peor a mejor.</summary>
public enum PhraseRating
{
    Disaster = 0,
    Bad = 1,
    Poor = 2,
    Acceptable = 3,
    Good = 4,
    VeryGood = 5,
    Excellent = 6,
    Perfect = 7,
}

/// <summary>
/// Rangos de valoración. El porcentaje es la parte de los puntos posibles de la frase que se obtuvo
/// (<see cref="Scoring.LineResult.Fraction"/>); cada rango incluye su límite inferior.
/// </summary>
public static class PhraseRatings
{
    /// <summary>Límite inferior de cada nivel, en porcentaje, de mayor a menor.</summary>
    public static readonly IReadOnlyList<(PhraseRating Rating, double MinPercent)> Bands =
    [
        (PhraseRating.Perfect, 98),
        (PhraseRating.Excellent, 90),
        (PhraseRating.VeryGood, 80),
        (PhraseRating.Good, 65),
        (PhraseRating.Acceptable, 50),
        (PhraseRating.Poor, 30),
        (PhraseRating.Bad, 10),
        (PhraseRating.Disaster, 0),
    ];

    /// <summary>Fracción mínima (0–1) para contar una frase como "buena" en rachas y recuperaciones.</summary>
    public const double GoodRunFraction = 0.80;

    /// <summary>Fracción por debajo de la cual una frase cuenta como floja en las recuperaciones.</summary>
    public const double WeakFraction = 0.50;

    public static PhraseRating Classify(double fraction)
    {
        // Los pesos son enteros: 0,98 puede llegar como 0,9799999… de una división; se compara con tolerancia.
        var percent = Math.Clamp(fraction, 0, 1) * 100 + 1e-9;
        foreach (var (rating, min) in Bands)
        {
            if (percent >= min)
            {
                return rating;
            }
        }

        return PhraseRating.Disaster;
    }
}
