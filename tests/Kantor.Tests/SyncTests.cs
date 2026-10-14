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
    public void Paused_position_is_frozen_even_if_the_device_clock_keeps_running()
    {
        var clock = new PlaybackClockModel();
        clock.Start();
        clock.Pause(3.0);

        Assert.Equal(3.0, clock.Position(3.0).TotalSeconds, 6);
        Assert.Equal(3.0, clock.Position(4.5).TotalSeconds, 6);
    }

    [Fact]
    public void Resume_discounts_the_time_spent_paused_when_device_clock_kept_running()
    {
        var clock = new PlaybackClockModel();
        clock.Start();
        clock.Pause(3.0);
        clock.Resume(5.0); // el reloj del dispositivo avanzó 2 s en la pausa

        Assert.Equal(3.0, clock.Position(5.0).TotalSeconds, 6);
        Assert.Equal(3.5, clock.Position(5.5).TotalSeconds, 6);
    }

    [Fact]
    public void Resume_when_device_clock_froze_during_pause_adds_nothing()
    {
        var clock = new PlaybackClockModel();
        clock.Start();
        clock.Pause(3.0);
        clock.Resume(3.0);

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
        clock.Pause(2);
        Assert.Null(clock.PositionAt(2, Host(2), Host(2)));

        clock.Resume(2);
        clock.Stop();
        Assert.Null(clock.PositionAt(2, Host(2), Host(2)));
    }

    [Fact]
    public void PositionAt_accounts_for_paused_time_discounted_earlier()
    {
        var clock = new PlaybackClockModel();
        clock.Start();
        clock.Pause(3.0);
        clock.Resume(5.0);

        var position = clock.PositionAt(6.0, Host(200.0), Host(200.0));

        Assert.Equal(4.0, position!.Value.TotalSeconds, 6);
    }
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
