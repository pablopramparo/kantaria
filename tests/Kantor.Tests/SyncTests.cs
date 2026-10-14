// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Pitch;
using Kantor.Core.Timing;

namespace Kantor.Tests;

public class PlaybackClockModelTests
{
    private static long Host(double seconds) => HostClock.FromSeconds(seconds);

    [Fact]
    public void Position_follows_the_device_clock_while_playing()
    {
        var clock = new PlaybackClockModel();
        clock.Start();

        Assert.Equal(2.5, clock.Position(2.5).TotalSeconds, 6);
    }

    [Fact]
    public void Position_is_zero_when_not_started()
    {
        Assert.Equal(TimeSpan.Zero, new PlaybackClockModel().Position(10));
    }

    [Fact]
    public void Paused_position_is_the_raw_device_clock_which_stops_by_itself()
    {
        var clock = new PlaybackClockModel();
        clock.Start();
        clock.Pause();

        Assert.True(clock.IsPaused);
        Assert.Equal(3.0, clock.Position(3.0).TotalSeconds, 6);
    }

    [Fact]
    public void Resume_continues_from_the_device_clock_without_discounting_anything()
    {
        var clock = new PlaybackClockModel();
        clock.Start();
        clock.Pause();
        clock.Resume();

        Assert.False(clock.IsPaused);
        Assert.Equal(3.25, clock.Position(3.25).TotalSeconds, 6);
    }

    [Fact]
    public void PositionAt_extrapolates_a_reading_back_in_host_time()
    {
        var clock = new PlaybackClockModel();
        clock.Start();

        // Lectura: la canción estaba en 10,000 s cuando el QPC marcaba 100,000 s.
        // Un sonido capturado 40 ms antes ocurrió en la posición 9,960 s.
        var position = clock.PositionAt(10.0, Host(100.0), Host(99.96));

        Assert.NotNull(position);
        Assert.Equal(9.96, position!.Value.TotalSeconds, 6);
    }

    [Fact]
    public void PositionAt_is_null_when_paused_or_stopped()
    {
        var clock = new PlaybackClockModel();
        Assert.Null(clock.PositionAt(1, Host(1), Host(1)));

        clock.Start();
        clock.Pause();
        Assert.Null(clock.PositionAt(2, Host(2), Host(2)));

        clock.Resume();
        clock.Stop();
        Assert.Null(clock.PositionAt(2, Host(2), Host(2)));
    }
}

/// <summary>
/// Dispositivo de salida simulado con el comportamiento medido en WASAPI compartido (NAudio 2.2.1,
/// búfer de 100 ms): su reloj cuenta solo audio efectivamente reproducido. Tras pausar, el búfer que ya
/// estaba cargado sigue sonando hasta vaciarse y recién entonces el reloj se detiene.
/// </summary>
internal sealed class SimulatedOutput
{
    private readonly double _bufferSeconds;
    private bool _refilling = true;
    private double _buffered;

    public SimulatedOutput(double bufferSeconds)
    {
        _bufferSeconds = bufferSeconds;
        _buffered = bufferSeconds;
    }

    /// <summary>Lo que informa IAudioClock: segundos de audio reproducidos desde el inicio del flujo.</summary>
    public double ClockSeconds { get; private set; }

    public void SoftPause() => _refilling = false;

    public void SoftResume() => _refilling = true;

    public void Advance(double seconds)
    {
        var played = _refilling ? seconds : Math.Min(_buffered, seconds);
        ClockSeconds += played;
        if (!_refilling)
        {
            _buffered -= played;
        }
        else
        {
            _buffered = _bufferSeconds;
        }
    }
}

public class PauseResumeCycleTests
{
    // Lo que se escucha equivale al tiempo de reproducción acumulado: el reloj del dispositivo solo
    // avanza mientras suena audio de la canción, así que la posición mostrada debe seguirlo siempre.
    [Fact]
    public void Position_keeps_matching_the_audio_over_many_pause_resume_cycles()
    {
        var device = new SimulatedOutput(bufferSeconds: 0.1);
        var clock = new PlaybackClockModel();
        clock.Start();
        device.Advance(1.0);

        for (var cycle = 1; cycle <= 20; cycle++)
        {
            clock.Pause();
            device.SoftPause();
            device.Advance(0.5); // pausado: el búfer se vacía y el reloj se detiene

            clock.Resume();
            device.SoftResume();
            device.Advance(0.75);

            Assert.Equal(device.ClockSeconds, clock.Position(device.ClockSeconds).TotalSeconds, 6);
        }
    }

    [Fact]
    public void Song_position_does_not_accumulate_error_with_each_cycle()
    {
        var device = new SimulatedOutput(bufferSeconds: 0.1);
        var clock = new PlaybackClockModel();
        clock.Start();
        device.Advance(1.0);

        double? firstError = null;
        for (var cycle = 1; cycle <= 10; cycle++)
        {
            clock.Pause();
            device.SoftPause();
            device.Advance(0.3);
            clock.Resume();
            device.SoftResume();
            device.Advance(0.2);

            var error = clock.Position(device.ClockSeconds).TotalSeconds - device.ClockSeconds;
            firstError ??= error;
            Assert.Equal(firstError.Value, error, 6);
        }
    }

    [Fact]
    public void PositionAt_matches_the_audio_after_many_cycles()
    {
        var device = new SimulatedOutput(bufferSeconds: 0.1);
        var clock = new PlaybackClockModel();
        clock.Start();
        device.Advance(1.0);

        for (var cycle = 1; cycle <= 10; cycle++)
        {
            clock.Pause();
            device.SoftPause();
            device.Advance(0.4);
            clock.Resume();
            device.SoftResume();
            device.Advance(0.6);
        }

        // Una lectura tomada ahora (QPC 500 s), proyectada 50 ms hacia atrás.
        var position = clock.PositionAt(device.ClockSeconds, Host(500.0), Host(499.95));

        Assert.Equal(device.ClockSeconds - 0.05, position!.Value.TotalSeconds, 6);
    }

    private static long Host(double seconds) => HostClock.FromSeconds(seconds);
}

public class PitchFrameTimingTests
{
    [Fact]
    public void Window_ends_at_the_last_sample_of_the_packet_not_when_processing_happens()
    {
        var tracker = new PitchTracker(48000);
        var window = tracker.WindowSize; // 2048 muestras
        var packet = new float[480];
        var packetStart = HostClock.FromSeconds(50.0);
        PitchFrame frame = default;
        var emitted = false;

        // Llenar la ventana con paquetes consecutivos de 10 ms.
        var t = packetStart;
        for (var i = 0; i < 6 && !emitted; i++)
        {
            emitted = tracker.Push(packet, t, out frame);
            if (!emitted)
            {
                t += HostClock.FromSeconds(480 / 48000.0);
            }
        }

        Assert.True(emitted);
        var expectedEnd = t + HostClock.FromSeconds(480 / 48000.0);
        Assert.Equal(expectedEnd, frame.WindowEndHost, tolerance: 1);

        var windowTicks = HostClock.FromSeconds(window / 48000.0);
        Assert.Equal(frame.WindowEndHost - windowTicks, frame.WindowStartHost, tolerance: 1);
        Assert.Equal(frame.WindowStartHost + windowTicks / 2, frame.CenterHost, tolerance: 1);
    }

    [Fact]
    public void Reset_requires_refilling_the_window_before_emitting_again()
    {
        var tracker = new PitchTracker(48000);
        Assert.True(tracker.Push(new float[tracker.WindowSize], 0, out _));

        tracker.Reset();

        Assert.False(tracker.Push(new float[tracker.WindowSize / 2], 0, out _));
        Assert.True(tracker.Push(new float[tracker.WindowSize / 2], 0, out _));
    }
}

public class PitchTimelineTests
{
    private static PitchSample Sample(double seconds, double frequency = 220) =>
        new(TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(43), frequency, 0.9, -20);

    [Fact]
    public void Returns_samples_in_range_in_order()
    {
        var timeline = new PitchTimeline();
        for (var i = 0; i < 10; i++)
        {
            timeline.Add(Sample(i));
        }

        var result = new List<PitchSample>();
        timeline.CopyRange(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), result);

        Assert.Equal([3.0, 4.0, 5.0, 6.0], result.Select(s => s.Time.TotalSeconds));
    }

    [Fact]
    public void Overwrites_oldest_when_full()
    {
        var timeline = new PitchTimeline(capacity: 4);
        for (var i = 0; i < 7; i++)
        {
            timeline.Add(Sample(i));
        }

        var result = new List<PitchSample>();
        timeline.CopyRange(TimeSpan.MinValue, TimeSpan.MaxValue, result);

        Assert.Equal([3.0, 4.0, 5.0, 6.0], result.Select(s => s.Time.TotalSeconds));
    }

    [Fact]
    public void Time_going_backwards_discards_history()
    {
        var timeline = new PitchTimeline();
        timeline.Add(Sample(10));
        timeline.Add(Sample(11));
        timeline.Add(Sample(0.5)); // la canción se reinició

        var result = new List<PitchSample>();
        timeline.CopyRange(TimeSpan.MinValue, TimeSpan.MaxValue, result);

        Assert.Equal([0.5], result.Select(s => s.Time.TotalSeconds));
    }

    [Fact]
    public void Sample_exposes_the_analyzed_interval()
    {
        var sample = Sample(10);

        Assert.Equal(10.0 - 0.0215, sample.Start.TotalSeconds, 6);
        Assert.Equal(10.0 + 0.0215, sample.End.TotalSeconds, 6);
    }
}
