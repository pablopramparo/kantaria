// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Calibration;

namespace Kantor.Tests;

public class CalibrationTests
{
    private const int Rate = 48000;

    /// <summary>Lo capturado: la señal reproducida con un retardo, atenuada, con ruido y opcionalmente un eco.</summary>
    private static float[] Capture(float[] played, double latencySeconds, double gain = 1.0, double noise = 0,
        double echoDelay = 0, double echoGain = 0, int seed = 11, double tailSeconds = 1.0)
    {
        var delay = (int)Math.Round(latencySeconds * Rate);
        var length = played.Length + delay + (int)(tailSeconds * Rate);
        var x = new float[length];
        var rng = new Random(seed);
        for (var i = 0; i < length; i++)
        {
            x[i] = (float)(noise * (rng.NextDouble() * 2 - 1));
        }

        for (var i = 0; i < played.Length; i++)
        {
            x[i + delay] += (float)(gain * played[i]);
            if (echoGain > 0)
            {
                var e = i + delay + (int)(echoDelay * Rate);
                if (e < length)
                {
                    x[e] += (float)(echoGain * played[i]);
                }
            }
        }

        return x;
    }

    [Fact]
    public void A_single_chirp_is_found_at_its_position()
    {
        var chirp = CalibrationSignal.Chirp(Rate);
        var captured = new float[Rate];
        chirp.CopyTo(captured, 12345);

        var peaks = MatchedFilter.FindPeaks(captured, chirp, 1, Rate / 4);

        Assert.Single(peaks);
        Assert.Equal(12345, peaks[0].SampleIndex, 0.3);
        Assert.True(peaks[0].Quality > 50);
    }

    [Fact]
    public void The_chirp_is_found_through_heavy_noise()
    {
        var chirp = CalibrationSignal.Chirp(Rate, 0.3);
        var captured = Capture(chirp, 0.2, noise: 0.9); // ruido ~9 dB por encima de la señal

        var peaks = MatchedFilter.FindPeaks(captured, chirp, 1, Rate / 4);

        Assert.Equal(0.2 * Rate, peaks[0].SampleIndex, 1.0);
        Assert.True(peaks[0].Quality >= LatencyEvaluation.MinQuality);
    }

    [Fact]
    public void Only_noise_gives_a_low_quality_peak()
    {
        var chirp = CalibrationSignal.Chirp(Rate);
        var captured = Capture(new float[Rate], 0.1, noise: 0.05, tailSeconds: 2);

        var peaks = MatchedFilter.FindPeaks(captured, chirp, 1, Rate / 4);

        Assert.True(peaks[0].Quality < LatencyEvaluation.MinQuality);
    }

    [Fact]
    public void An_echo_does_not_move_the_direct_arrival()
    {
        var chirp = CalibrationSignal.Chirp(Rate);
        var captured = Capture(chirp, 0.15, echoDelay: 0.03, echoGain: 0.5);

        var peaks = MatchedFilter.FindPeaks(captured, chirp, 2, Rate / 100);

        // El primer pico en el tiempo es el directo, el más fuerte.
        Assert.Equal(0.15 * Rate, peaks[0].SampleIndex, 1.0);
        Assert.True(peaks[0].Quality > peaks[1].Quality);
    }

    [Theory]
    [InlineData(0.020)]
    [InlineData(0.1734)]
    [InlineData(0.350)]
    public void A_full_train_measures_a_known_latency_precisely(double latency)
    {
        var train = CalibrationSignal.Train(Rate, 0.5);
        var captured = Capture(train, latency, gain: 0.2, noise: 0.05);
        var chirp = CalibrationSignal.Chirp(Rate);

        var peaks = MatchedFilter.FindPeaks(captured, chirp, CalibrationSignal.Repetitions, Rate / 2);
        var render = CalibrationSignal.RepetitionStarts(Rate).Select(s => s / (double)Rate).ToList();
        var paired = LatencyEvaluation.Pair(peaks.Select(p => (p.SampleIndex / Rate, p.Quality)).ToList(), render);
        var result = LatencyEvaluation.Evaluate(paired, CalibrationSignal.Repetitions);

        Assert.True(result.Success, result.Problem);
        Assert.Equal(CalibrationSignal.Repetitions, result.Accepted);
        Assert.Equal(latency * 1000, result.MedianMs, 0.5);
        Assert.InRange(result.SpreadMs, 0, 1);
    }

    [Theory]
    [InlineData(173.4, 175)]
    [InlineData(176.0, 175)]
    [InlineData(162.0, 150)]
    [InlineData(163.0, 175)]
    [InlineData(19.5, 25)]
    [InlineData(5.0, 0)]
    [InlineData(450.0, 300)]
    public void The_suggested_delay_is_the_nearest_selector_step(double measuredMs, int expected)
    {
        var m = Enumerable.Repeat((measuredMs, 50.0), 5).ToList();

        var result = LatencyEvaluation.Evaluate(m, 5);

        Assert.True(result.Success);
        Assert.Equal(expected, result.SuggestedDelayMs);
    }

    [Fact]
    public void Too_few_clear_repetitions_are_rejected_with_advice()
    {
        var m = new List<(double, double)> { (170, 40), (171, 3), (172, 2), (169, 40), (300, 1) };

        var result = LatencyEvaluation.Evaluate(m, 5);

        Assert.False(result.Success);
        Assert.Equal(2, result.Accepted);
        Assert.Contains("volumen", result.Problem);
    }

    [Fact]
    public void Inconsistent_repetitions_are_rejected()
    {
        var m = new List<(double, double)> { (100, 40), (180, 40), (260, 40), (130, 40), (220, 40) };

        var result = LatencyEvaluation.Evaluate(m, 5);

        Assert.False(result.Success);
        Assert.Contains("dispersión", result.Problem);
    }

    [Fact]
    public void An_implausible_latency_is_rejected()
    {
        var m = Enumerable.Repeat((900.0, 40.0), 5).ToList();

        Assert.False(LatencyEvaluation.Evaluate(m, 5).Success);
    }

    [Fact]
    public void An_arrival_is_paired_with_the_repetition_just_before_it()
    {
        var render = new List<double> { 0.5, 1.5, 2.5 };
        var arrivals = new List<(double, double)> { (1.7, 20), (0.3, 20), (2.5 + 1.0, 20) };

        var paired = LatencyEvaluation.Pair(arrivals, render);

        // 1,7 s → repetición de 1,5 s (200 ms); 0,3 s no tiene ninguna antes; 3,5 s queda a 1 s de la última.
        Assert.Single(paired);
        Assert.Equal(200, paired[0].LatencyMs, 6);
    }

    [Fact]
    public void The_playback_train_places_each_chirp_at_the_announced_frame()
    {
        var train = CalibrationSignal.Train(Rate, 1.0);
        var starts = CalibrationSignal.RepetitionStarts(Rate);

        Assert.Equal(CalibrationSignal.Repetitions, starts.Count);
        Assert.All(starts, s => Assert.True(Math.Abs(train[s + 2000]) > 0 || Math.Abs(train[s + 2001]) > 0));
        Assert.Equal(0f, train[starts[0] - 1]);
        Assert.True(train.Max() <= 1.0f);
    }
}
