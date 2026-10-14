using Kantor.Core.Audio;

namespace Kantor.Tests;

public class AudioInputTests
{
    private static float[][] Buffers(int channels, int frames) =>
        Enumerable.Range(0, channels).Select(_ => new float[frames]).ToArray();

    [Fact]
    public void Deinterleaves_float32_stereo_into_independent_channels()
    {
        var interleaved = new float[] { 0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f };
        var bytes = new byte[interleaved.Length * 4];
        Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
        var dest = Buffers(2, 8);

        var frames = PcmDecoder.Deinterleave(bytes, SampleFormat.Float32, 2, dest);

        Assert.Equal(3, frames);
        Assert.Equal([0.1f, 0.2f, 0.3f], dest[0].Take(3));
        Assert.Equal([-0.1f, -0.2f, -0.3f], dest[1].Take(3));
    }

    [Fact]
    public void Deinterleaves_int16()
    {
        byte[] bytes = [0x00, 0x40, 0x00, 0xC0]; // 16384 y -16384
        var dest = Buffers(2, 4);

        var frames = PcmDecoder.Deinterleave(bytes, SampleFormat.Int16, 2, dest);

        Assert.Equal(1, frames);
        Assert.Equal(0.5f, dest[0][0], 1e-6);
        Assert.Equal(-0.5f, dest[1][0], 1e-6);
    }

    [Fact]
    public void Deinterleaves_int24_with_sign()
    {
        // 0x400000 = +0.5, 0xC00000 = -0.5 (little endian)
        byte[] bytes = [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0];
        var dest = Buffers(2, 2);

        PcmDecoder.Deinterleave(bytes, SampleFormat.Int24, 2, dest);

        Assert.Equal(0.5f, dest[0][0], 1e-6);
        Assert.Equal(-0.5f, dest[1][0], 1e-6);
    }

    [Fact]
    public void Deinterleaves_int32()
    {
        var bytes = BitConverter.GetBytes(int.MinValue / 2);
        var dest = Buffers(1, 1);

        PcmDecoder.Deinterleave(bytes, SampleFormat.Int32, 1, dest);

        Assert.Equal(-0.5f, dest[0][0], 1e-6);
    }

    [Fact]
    public void Eight_channel_interface_keeps_each_channel_separate()
    {
        const int channels = 8;
        var interleaved = new float[channels * 2];
        for (var f = 0; f < 2; f++)
        {
            for (var c = 0; c < channels; c++)
            {
                interleaved[f * channels + c] = (c + 1) / 10f;
            }
        }

        var bytes = new byte[interleaved.Length * 4];
        Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
        var dest = Buffers(channels, 4);

        var frames = PcmDecoder.Deinterleave(bytes, SampleFormat.Float32, channels, dest);

        Assert.Equal(2, frames);
        for (var c = 0; c < channels; c++)
        {
            Assert.Equal((c + 1) / 10f, dest[c][0], 1e-6);
            Assert.Equal((c + 1) / 10f, dest[c][1], 1e-6);
        }
    }

    [Fact]
    public void Ignores_incomplete_trailing_frame()
    {
        var bytes = new byte[2 * 4 + 3];
        var dest = Buffers(2, 4);

        Assert.Equal(1, PcmDecoder.Deinterleave(bytes, SampleFormat.Float32, 2, dest));
    }

    [Fact]
    public void Full_scale_sine_peaks_at_zero_db_and_rms_is_minus_three()
    {
        var samples = Enumerable.Range(0, 4800).Select(i => (float)Math.Sin(2 * Math.PI * 100 * i / 48000.0)).ToArray();

        var level = LevelMeter.Measure(samples);

        Assert.Equal(0, level.PeakDb, 0.05);
        Assert.Equal(-3.01, level.RmsDb, 0.05);
    }

    [Fact]
    public void Silence_reads_at_the_floor()
    {
        Assert.Equal(LevelReading.FloorDb, LevelMeter.Measure(new float[256]).RmsDb);
        Assert.Equal(LevelReading.Silence, LevelMeter.Measure([]));
    }

    [Fact]
    public void Channels_of_the_same_device_have_distinct_ids()
    {
        var a = new InputChannelId("dev1", 0);
        var b = new InputChannelId("dev1", 1);

        Assert.NotEqual(a, b);
        Assert.Equal(a, new InputChannelId("dev1", 0));
    }
}
