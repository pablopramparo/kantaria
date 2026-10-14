using Kantor.Core.Pitch;

namespace Kantor.Tests;

public class PitchTests
{
    private static float[] Tone(double frequency, int sampleRate, int length, double amplitude = 0.5, int harmonics = 1, double phase = 0)
    {
        var data = new float[length];
        for (var i = 0; i < length; i++)
        {
            double v = 0;
            for (var h = 1; h <= harmonics; h++)
            {
                v += Math.Sin(2 * Math.PI * frequency * h * i / sampleRate + phase) / h;
            }

            data[i] = (float)(amplitude * v / (harmonics > 1 ? 1.5 : 1));
        }

        return data;
    }

    [Theory]
    [InlineData(44100, 65.41)]
    [InlineData(44100, 110)]
    [InlineData(48000, 196)]
    [InlineData(48000, 261.63)]
    [InlineData(48000, 440)]
    [InlineData(44100, 880)]
    public void Detects_pure_tones_within_half_a_percent(int sampleRate, double frequency)
    {
        var detector = new PitchDetector(sampleRate);

        var estimate = detector.Detect(Tone(frequency, sampleRate, detector.WindowSize));

        Assert.True(estimate.IsVoiced);
        Assert.Equal(frequency, estimate.Frequency, frequency * 0.005);
    }

    [Theory]
    [InlineData(82.41)]
    [InlineData(196)]
    [InlineData(330)]
    public void Detects_fundamental_of_harmonic_voice_like_signal(double frequency)
    {
        var detector = new PitchDetector(48000);

        var estimate = detector.Detect(Tone(frequency, 48000, detector.WindowSize, harmonics: 6));

        Assert.True(estimate.IsVoiced);
        Assert.Equal(frequency, estimate.Frequency, frequency * 0.01);
    }

    [Fact]
    public void Silence_is_unvoiced()
    {
        var detector = new PitchDetector(48000);

        var estimate = detector.Detect(new float[detector.WindowSize]);

        Assert.False(estimate.IsVoiced);
    }

    [Fact]
    public void White_noise_is_unvoiced()
    {
        var detector = new PitchDetector(48000);
        var random = new Random(1234);
        var noise = new float[detector.WindowSize];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)(random.NextDouble() * 2 - 1) * 0.5f;
        }

        Assert.False(detector.Detect(noise).IsVoiced);
    }

    [Fact]
    public void Window_size_fits_lowest_frequency_for_common_sample_rates()
    {
        Assert.Equal(2048, new PitchDetector(44100).WindowSize);
        Assert.Equal(2048, new PitchDetector(48000).WindowSize);
        Assert.Equal(4096, new PitchDetector(96000).WindowSize);
    }

    [Fact]
    public void Wrong_window_length_is_rejected()
    {
        var detector = new PitchDetector(48000);

        Assert.Throws<ArgumentException>(() => detector.Detect(new float[10]));
    }

    [Fact]
    public void Tracker_emits_frames_while_fed_in_small_device_blocks()
    {
        var tracker = new PitchTracker(48000);
        var signal = Tone(220, 48000, 48000);
        var frames = new List<PitchFrame>();

        for (var offset = 0; offset + 480 <= signal.Length; offset += 480)
        {
            if (tracker.Push(signal.AsSpan(offset, 480), out var frame))
            {
                frames.Add(frame);
            }
        }

        Assert.True(frames.Count > 30, $"Se esperaban muchos cuadros, hubo {frames.Count}.");
        Assert.All(frames, f =>
        {
            Assert.True(f.IsVoiced);
            Assert.Equal(220, f.Frequency, 2.0);
        });
    }

    [Fact]
    public void Tracker_does_not_emit_until_the_window_is_full()
    {
        var tracker = new PitchTracker(48000);

        var emitted = tracker.Push(new float[tracker.WindowSize / 2], out _);

        Assert.False(emitted);
    }

    [Fact]
    public void Tracker_gates_signals_below_threshold()
    {
        var tracker = new PitchTracker(48000, gateDb: -40);
        // 0.001 de amplitud ≈ -63 dBFS RMS: por debajo del umbral aunque sea un tono perfecto.
        var quiet = Tone(220, 48000, 8192, amplitude: 0.001);

        Assert.True(tracker.Push(quiet, out var frame));

        Assert.False(frame.IsVoiced);
        Assert.True(frame.LevelDb < -40);
    }

    [Theory]
    [InlineData(440.0, "A4", 0, 9)]
    [InlineData(261.6256, "C4", 0, 0)]
    [InlineData(65.4064, "C2", 0, -24)]
    [InlineData(932.33, "A#5", 0, 22)]
    public void Maps_frequency_to_note_and_ultrastar_tone(double hertz, string name, int cents, int tone)
    {
        var pitch = MusicalPitch.FromFrequency(hertz);

        Assert.Equal(name, pitch.Name);
        Assert.Equal(cents, pitch.Cents, 1.0);
        Assert.Equal(tone, pitch.UltraStarTone);
    }

    [Fact]
    public void Reports_cents_deviation()
    {
        // 50 centésimas sobre A4: 440 * 2^(0.25/12) ≈ 452.89 Hz está a +25 centésimas.
        var pitch = MusicalPitch.FromFrequency(440 * Math.Pow(2, 25 / 1200.0));

        Assert.Equal("A4", pitch.Name);
        Assert.Equal(25, pitch.Cents, 0.01);
    }
}
