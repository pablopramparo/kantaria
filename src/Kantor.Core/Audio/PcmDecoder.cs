namespace Kantor.Core.Audio;

public enum SampleFormat
{
    Float32,
    Int16,
    Int24,
    Int32,
}

public static class PcmDecoder
{
    public static int BytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.Int16 => 2,
        SampleFormat.Int24 => 3,
        _ => 4,
    };

    /// <summary>
    /// Separa audio entrelazado en un arreglo por canal, normalizado a [-1, 1].
    /// Devuelve la cantidad de frames escritos (limitada por el tamaño de los destinos).
    /// </summary>
    public static int Deinterleave(ReadOnlySpan<byte> data, SampleFormat format, int channels, float[][] destination)
    {
        if (channels < 1 || destination.Length < channels)
        {
            throw new ArgumentException("Hay menos destinos que canales.", nameof(destination));
        }

        var bytesPerSample = BytesPerSample(format);
        var frames = data.Length / (bytesPerSample * channels);
        for (var c = 0; c < channels; c++)
        {
            frames = Math.Min(frames, destination[c].Length);
        }

        var offset = 0;
        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < channels; c++)
            {
                destination[c][f] = ReadSample(data.Slice(offset, bytesPerSample), format);
                offset += bytesPerSample;
            }
        }

        return frames;
    }

    private static float ReadSample(ReadOnlySpan<byte> s, SampleFormat format) => format switch
    {
        SampleFormat.Float32 => BitConverter.ToSingle(s),
        SampleFormat.Int16 => BitConverter.ToInt16(s) / 32768f,
        SampleFormat.Int24 => (((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) >> 8) / 8388608f,
        _ => BitConverter.ToInt32(s) / 2147483648f,
    };
}
