// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Audio;
using Kantor.Core.Pitch;
using Kantor.Core.Timing;

namespace Kantor.Tests;

public class FrameClockTests
{
    private const int Rate = 48000;
    private static long Ticks(double seconds) => HostClock.FromSeconds(seconds);

    [Fact]
    public void Is_not_ready_until_it_has_a_few_blocks()
    {
        var clock = new FrameClock(Rate);

        Assert.False(clock.IsReady);
        clock.Add(0, 0);
        clock.Add(192, Ticks(0.004));
        Assert.False(clock.IsReady);
        clock.Add(384, Ticks(0.008));
        Assert.True(clock.IsReady);
    }

    [Fact]
    public void Maps_frames_and_instants_both_ways_for_an_exact_clock()
    {
        var clock = new FrameClock(Rate);
        var start = Ticks(1000);
        for (var k = 0; k < 50; k++)
        {
            clock.Add(k * 192L, start + (long)(k * 192 * 10_000_000.0 / Rate));
        }

        Assert.Equal(48000.0, clock.FramesAt(start + Ticks(1.0)), 3);
        Assert.Equal((double)(start + Ticks(0.5)), (double)clock.HostAt(24000), 2);
    }

    [Fact]
    public void Late_callbacks_do_not_shift_the_estimate()
    {
        var clock = new FrameClock(Rate);
        var start = Ticks(500);
        var rng = new Random(3);
        for (var k = 0; k < 400; k++)
        {
            var exact = start + (long)(k * 192 * 10_000_000.0 / Rate);
            // La planificación solo atrasa: entre 0 y 3 ms, y alguna vez 15 ms.
            var late = Ticks(rng.NextDouble() * 0.003) + (rng.Next(20) == 0 ? Ticks(0.012) : 0);
            clock.Add(k * 192L, exact + late);
        }

        // El error de ubicar un cuadro queda por debajo de la dispersión de los retrasos.
        var frame = 399 * 192L;
        var truth = start + (long)(frame * 10_000_000.0 / Rate);
        Assert.InRange(Math.Abs(clock.HostAt(frame) - truth) / 10_000.0, 0, 1.0);
    }

    [Fact]
    public void Follows_a_changed_offset_within_the_window()
    {
        var clock = new FrameClock(Rate, capacity: 32);
        var start = Ticks(10);
        for (var k = 0; k < 200; k++)
        {
            var jump = k >= 100 ? Ticks(0.020) : 0; // el flujo se retrasó 20 ms
            clock.Add(k * 192L, start + (long)(k * 192 * 10_000_000.0 / Rate) + jump);
        }

        var frame = 199 * 192L;
        var truth = start + (long)(frame * 10_000_000.0 / Rate) + Ticks(0.020);
        Assert.Equal(truth, clock.HostAt(frame), tolerance: 100);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var clock = new FrameClock(Rate);
        for (var k = 0; k < 10; k++)
        {
            clock.Add(k * 192L, Ticks(k * 0.004));
        }

        clock.Reset();

        Assert.False(clock.IsReady);
        Assert.Equal(0, clock.Count);
    }
}

public class ChannelAnalyzerTests
{
    private static float[] Sine(double hz, int frames, double amplitude = 0.3, int start = 0) =>
        Enumerable.Range(start, frames).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / 48000.0))).ToArray();

    [Fact]
    public void Levels_are_measured_per_channel_and_pitch_only_where_requested()
    {
        var analyzer = new ChannelAnalyzer(2, 48000);
        analyzer.SetPitchChannels([1]);
        var emitted = new List<(int, PitchFrame)>();
        var quiet = new float[480];

        long host = 0;
        for (var i = 0; i < 12; i++)
        {
            analyzer.Process([quiet, Sine(220, 480, 0.3, i * 480)], 480, host, false, emitted);
            host += 100_000;
        }

        Assert.True(analyzer.Read(1).Level.RmsDb > analyzer.Read(0).Level.RmsDb);
        Assert.NotEmpty(emitted);
        Assert.All(emitted, e => Assert.Equal(1, e.Item1));
        Assert.Null(analyzer.Read(0).Pitch);
        Assert.NotNull(analyzer.Read(1).Pitch);
        Assert.InRange(analyzer.Read(1).Pitch!.Value.Frequency, 210, 230);
    }

    [Fact]
    public void Removing_a_pitch_channel_clears_its_reading()
    {
        var analyzer = new ChannelAnalyzer(1, 48000);
        analyzer.SetPitchChannels([0]);
        var emitted = new List<(int, PitchFrame)>();
        for (var i = 0; i < 12; i++)
        {
            analyzer.Process([Sine(220, 480, 0.3, i * 480)], 480, i * 100_000L, false, emitted);
        }

        Assert.NotNull(analyzer.Read(0).Pitch);

        analyzer.SetPitchChannels([]);

        Assert.Null(analyzer.Read(0).Pitch);
    }

    [Fact]
    public void A_discontinuity_restarts_the_accumulation_of_a_channel()
    {
        var analyzer = new ChannelAnalyzer(1, 48000);
        analyzer.SetPitchChannels([0]);
        var emitted = new List<(int, PitchFrame)>();
        // Un bloque con discontinuidad vacía lo acumulado: con 2048 muestras de ventana no puede emitir enseguida.
        analyzer.Process([Sine(220, 1500)], 1500, 0, false, emitted);
        analyzer.Process([Sine(220, 1500)], 1500, 100_000, true, emitted);

        Assert.Empty(emitted);
    }

    [Fact]
    public void Ignores_channels_outside_the_range()
    {
        var analyzer = new ChannelAnalyzer(2, 48000);

        analyzer.SetPitchChannels([-1, 5]);
        var emitted = new List<(int, PitchFrame)>();
        analyzer.Process([Sine(220, 480), Sine(220, 480)], 480, 0, false, emitted);

        Assert.Empty(emitted);
    }
}
