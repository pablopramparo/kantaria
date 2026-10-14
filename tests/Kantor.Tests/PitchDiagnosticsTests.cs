// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Pitch;

namespace Kantor.Tests;

public class PitchDiagnosticsTests
{
    private static float[] Tone(double hz, int length, double noise = 0, int seed = 1)
    {
        var rng = new Random(seed);
        var x = new float[length];
        for (var i = 0; i < length; i++)
        {
            x[i] = (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / 48000.0) + noise * (rng.NextDouble() * 2 - 1));
        }

        return x;
    }

    [Fact]
    public void A_clean_tone_has_a_minimum_well_below_the_threshold()
    {
        var detector = new PitchDetector(48000);

        var estimate = detector.Detect(Tone(220, detector.WindowSize));

        Assert.True(estimate.IsVoiced);
        Assert.InRange(estimate.MinCmnd, 0, 0.05);
    }

    [Fact]
    public void Noise_is_unvoiced_and_reports_how_far_it_was_from_the_threshold()
    {
        var detector = new PitchDetector(48000);
        var rng = new Random(5);
        var noise = new float[detector.WindowSize];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)(rng.NextDouble() * 2 - 1);
        }

        var estimate = detector.Detect(noise);

        Assert.False(estimate.IsVoiced);
        Assert.InRange(estimate.MinCmnd, 0.15, 1.0);
    }

    [Fact]
    public void The_minimum_is_carried_into_the_frame_but_does_not_change_the_decision()
    {
        var tracker = new PitchTracker(48000);
        var samples = Tone(220, tracker.WindowSize);

        Assert.True(tracker.Push(samples, 0, out var frame));
        Assert.True(frame.IsVoiced);
        Assert.InRange(frame.MinCmnd, 0, 0.05);
        Assert.InRange(frame.Frequency, 215, 225);
    }
}
