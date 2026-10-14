// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Pitch;

/// <summary>Una altura musical expresada en semitonos MIDI (A4 = 69 = 440 Hz), con conversiones a nota y a tono UltraStar.</summary>
public readonly record struct MusicalPitch(double Midi)
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static MusicalPitch FromFrequency(double hertz)
    {
        if (hertz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hertz), "La frecuencia debe ser positiva.");
        }

        return new MusicalPitch(69 + 12 * Math.Log2(hertz / 440.0));
    }

    public int NearestSemitone => (int)Math.Round(Midi);

    /// <summary>Desvío respecto de la nota más cercana, en centésimas de semitono (-50 a +50).</summary>
    public double Cents => (Midi - NearestSemitone) * 100;

    /// <summary>Nota con octava científica: C4 es el do central.</summary>
    public string Name
    {
        get
        {
            var n = NearestSemitone;
            var index = ((n % 12) + 12) % 12;
            return $"{Names[index]}{(int)Math.Floor(n / 12.0) - 1}";
        }
    }

    /// <summary>Tono UltraStar: semitonos relativos a C4 (C4 = 0).</summary>
    public int UltraStarTone => NearestSemitone - 60;
}
