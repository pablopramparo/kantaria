// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Pitch;

namespace Kantor.Tests;

public class PitchFoldingTests
{
    [Fact]
    public void Tone_already_inside_the_lane_is_left_alone()
    {
        Assert.Equal(-3.0, MusicalPitch.FoldIntoRange(-3.0, -11, 10));
    }

    // Regresión: en archivos que usan el número MIDI directo (tonos 52..71) la voz, expresada con
    // C4 = 0, queda muchas octavas por debajo y el plegado de cuatro pasos la descartaba.
    [Theory]
    [InlineData(98.0)]
    [InlineData(130.8)]
    [InlineData(220.0)]
    [InlineData(392.0)]
    public void Voice_is_folded_into_a_lane_whose_tones_use_midi_numbers(double hertz)
    {
        const int low = 52 - 2;
        const int high = 71 + 2;
        var tone = MusicalPitch.FromFrequency(hertz).Midi - 60;

        var folded = MusicalPitch.FoldIntoRange(tone, low, high);

        Assert.NotNull(folded);
        Assert.InRange(folded!.Value, low, high);
        Assert.Equal(0, (folded.Value - tone) % 12, 6); // solo cambia de octava
    }

    [Fact]
    public void Voice_far_above_the_lane_is_folded_down()
    {
        var folded = MusicalPitch.FoldIntoRange(40, -11, 10);

        Assert.Equal(4.0, folded);
    }

    [Fact]
    public void Chooses_the_octave_nearest_the_center_of_a_wide_lane()
    {
        // Carril de 20 semitonos (0..20): el do (pc 0) cabe en 0, 12; el centro es 10, gana 12.
        Assert.Equal(12.0, MusicalPitch.FoldIntoRange(-24, 0, 20));
    }

    [Fact]
    public void Returns_null_when_no_octave_fits_a_narrow_lane()
    {
        // Carril de 4 semitonos (0..4): el tono 8 no tiene octava dentro.
        Assert.Null(MusicalPitch.FoldIntoRange(8, 0, 4));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_finite_tone_is_rejected(double tone)
    {
        Assert.Null(MusicalPitch.FoldIntoRange(tone, -11, 10));
    }
}
