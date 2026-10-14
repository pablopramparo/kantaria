namespace Kantor.Core.Audio;

/// <summary>Nivel de un bloque de audio en dBFS (0 dB = fondo de escala).</summary>
public readonly record struct LevelReading(double RmsDb, double PeakDb)
{
    public const double FloorDb = -120;

    public static LevelReading Silence => new(FloorDb, FloorDb);
}

public static class LevelMeter
{
    public static LevelReading Measure(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return LevelReading.Silence;
        }

        double sum = 0;
        double peak = 0;
        foreach (var s in samples)
        {
            sum += (double)s * s;
            peak = Math.Max(peak, Math.Abs(s));
        }

        return new LevelReading(ToDb(Math.Sqrt(sum / samples.Length)), ToDb(peak));
    }

    public static double ToDb(double linear) =>
        linear <= 1e-6 ? LevelReading.FloorDb : Math.Max(LevelReading.FloorDb, 20 * Math.Log10(linear));
}
