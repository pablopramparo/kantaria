// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Numerics;

namespace Kantor.Core.Calibration;

/// <summary>
/// Señal de prueba para medir la latencia de audio: barridos de frecuencia (chirps) repetidos. Un barrido de
/// banda ancha se detecta por correlación con mucha más confianza que un clic, incluso con ruido y eco.
/// </summary>
public static class CalibrationSignal
{
    public const int Repetitions = 5;
    public const double ChirpSeconds = 0.5;
    public const double SpacingSeconds = 1.0;
    public const double LeadSeconds = 0.5;
    public const double StartHz = 200;
    public const double EndHz = 6000;

    /// <summary>Un barrido lineal con bordes suavizados (ventana de Tukey) para que no chasquee al sonar.</summary>
    public static float[] Chirp(int sampleRate, double amplitude = 1.0)
    {
        var length = (int)Math.Round(ChirpSeconds * sampleRate);
        var samples = new float[length];
        var rate = (EndHz - StartHz) / ChirpSeconds;
        var taper = (int)(0.05 * sampleRate);
        for (var i = 0; i < length; i++)
        {
            var t = i / (double)sampleRate;
            var phase = 2 * Math.PI * (StartHz * t + 0.5 * rate * t * t);
            var envelope = 1.0;
            if (i < taper)
            {
                envelope = 0.5 * (1 - Math.Cos(Math.PI * i / taper));
            }
            else if (i >= length - taper)
            {
                envelope = 0.5 * (1 - Math.Cos(Math.PI * (length - 1 - i) / taper));
            }

            samples[i] = (float)(amplitude * envelope * Math.Sin(phase));
        }

        return samples;
    }

    /// <summary>Cuadro, dentro del flujo reproducido, donde empieza cada repetición.</summary>
    public static IReadOnlyList<int> RepetitionStarts(int sampleRate) =>
        Enumerable.Range(0, Repetitions)
            .Select(k => (int)Math.Round((LeadSeconds + k * SpacingSeconds) * sampleRate))
            .ToArray();

    /// <summary>El flujo completo a reproducir (mono): silencio, los barridos y un cierre de silencio.</summary>
    public static float[] Train(int sampleRate, double amplitude)
    {
        var chirp = Chirp(sampleRate, amplitude);
        var total = (int)Math.Round((LeadSeconds + Repetitions * SpacingSeconds + 0.5) * sampleRate);
        var train = new float[total];
        foreach (var start in RepetitionStarts(sampleRate))
        {
            chirp.CopyTo(train, start);
        }

        return train;
    }

    /// <summary>Cuánto hay que capturar para ver todas las repeticiones, con margen para la latencia.</summary>
    public static double CaptureSeconds => LeadSeconds + Repetitions * SpacingSeconds + 1.5;
}

/// <summary>Un barrido encontrado en lo capturado: posición en muestras (con fracción) y qué tan claro se oye.</summary>
public readonly record struct ArrivalPeak(double SampleIndex, double Quality);

public static class MatchedFilter
{
    /// <summary>
    /// Busca hasta <paramref name="count"/> apariciones de <paramref name="template"/> en <paramref name="captured"/>
    /// por correlación cruzada (vía FFT). Devuelve los picos en orden cronológico. La calidad es el cociente entre
    /// el pico y el nivel típico de la correlación: grande si la señal se oye con claridad, cercano a 1 si es ruido.
    /// </summary>
    public static IReadOnlyList<ArrivalPeak> FindPeaks(ReadOnlySpan<float> captured, ReadOnlySpan<float> template,
        int count, int minSeparation)
    {
        if (captured.Length < template.Length || count <= 0)
        {
            return [];
        }

        var size = 1;
        while (size < captured.Length + template.Length)
        {
            size <<= 1;
        }

        var x = new Complex[size];
        var h = new Complex[size];
        for (var i = 0; i < captured.Length; i++)
        {
            x[i] = captured[i];
        }

        for (var i = 0; i < template.Length; i++)
        {
            h[i] = template[i];
        }

        Fft(x, inverse: false);
        Fft(h, inverse: false);
        for (var i = 0; i < size; i++)
        {
            x[i] *= Complex.Conjugate(h[i]);
        }

        Fft(x, inverse: true);

        // Solo tienen sentido los desfases donde el barrido entra completo en lo capturado.
        var valid = captured.Length - template.Length + 1;
        var magnitude = new double[valid];
        for (var i = 0; i < valid; i++)
        {
            magnitude[i] = Math.Abs(x[i].Real);
        }

        var typical = Median(magnitude);
        if (typical <= 0)
        {
            return [];
        }

        var working = (double[])magnitude.Clone();
        var peaks = new List<ArrivalPeak>();
        for (var n = 0; n < count; n++)
        {
            var best = 0;
            for (var i = 1; i < valid; i++)
            {
                if (working[i] > working[best])
                {
                    best = i;
                }
            }

            if (working[best] <= 0)
            {
                break;
            }

            peaks.Add(new ArrivalPeak(Refine(magnitude, best), magnitude[best] / typical));
            var from = Math.Max(0, best - minSeparation);
            var to = Math.Min(valid - 1, best + minSeparation);
            for (var i = from; i <= to; i++)
            {
                working[i] = 0;
            }
        }

        return peaks.OrderBy(p => p.SampleIndex).ToList();
    }

    /// <summary>Interpolación parabólica del máximo sobre sus dos vecinos: precisión de una fracción de muestra.</summary>
    private static double Refine(double[] y, int i)
    {
        if (i <= 0 || i >= y.Length - 1)
        {
            return i;
        }

        var a = y[i - 1];
        var b = y[i];
        var c = y[i + 1];
        var denominator = a - 2 * b + c;
        return Math.Abs(denominator) < 1e-12 ? i : i + 0.5 * (a - c) / denominator;
    }

    private static double Median(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    /// <summary>FFT iterativa radix-2 en el lugar. El largo debe ser potencia de 2.</summary>
    private static void Fft(Complex[] data, bool inverse)
    {
        var n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < n; start += length)
            {
                var w = Complex.One;
                for (var k = 0; k < length / 2; k++)
                {
                    var even = data[start + k];
                    var odd = data[start + k + length / 2] * w;
                    data[start + k] = even + odd;
                    data[start + k + length / 2] = even - odd;
                    w *= step;
                }
            }
        }

        if (inverse)
        {
            for (var i = 0; i < n; i++)
            {
                data[i] /= n;
            }
        }
    }
}

/// <summary>Resultado de una calibración: latencia medida y el retardo de voz sugerido, o por qué no se pudo.</summary>
public sealed record CalibrationResult(
    bool Success,
    double MedianMs,
    double SpreadMs,
    int Accepted,
    int Expected,
    int SuggestedDelayMs,
    string? Problem);

public static class LatencyEvaluation
{
    /// <summary>Calidad mínima de un pico para tomarlo como una detección real del barrido.</summary>
    public const double MinQuality = 8;

    /// <summary>Desvío absoluto mediano máximo (ms) entre repeticiones para fiarse de la medición.</summary>
    public const double MaxSpreadMs = 8;

    public const int MinAccepted = 3;

    /// <summary>
    /// Empareja cada llegada con la repetición reproducida inmediatamente antes (hasta 1 s antes) y devuelve las
    /// latencias en milisegundos. Todo está en la misma base de tiempo, en segundos.
    /// </summary>
    public static IReadOnlyList<(double LatencyMs, double Quality)> Pair(
        IReadOnlyList<(double Seconds, double Quality)> arrivals, IReadOnlyList<double> renderSeconds)
    {
        var result = new List<(double, double)>();
        foreach (var (seconds, quality) in arrivals)
        {
            var candidates = renderSeconds.Where(r => seconds - r >= 0 && seconds - r < SpacingMax).ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            result.Add(((seconds - candidates.Max()) * 1000.0, quality));
        }

        return result;
    }

    private const double SpacingMax = 0.9;

    public static CalibrationResult Evaluate(IReadOnlyList<(double LatencyMs, double Quality)> measurements, int expected)
    {
        var good = measurements.Where(m => m.Quality >= MinQuality).Select(m => m.LatencyMs).OrderBy(x => x).ToList();
        if (good.Count < MinAccepted)
        {
            return new CalibrationResult(false, 0, 0, good.Count, expected, 0,
                "No se oyó la señal con claridad. Subí el volumen de los parlantes, acercá el micrófono a ellos " +
                "(con auriculares no funciona por el aire) o usá un cable de la salida a la entrada.");
        }

        var median = good[good.Count / 2];
        var deviations = good.Select(g => Math.Abs(g - median)).OrderBy(x => x).ToList();
        var spread = deviations[deviations.Count / 2];
        if (median < 0 || median > 700)
        {
            return new CalibrationResult(false, median, spread, good.Count, expected, 0,
                $"La latencia medida ({median:0} ms) no es plausible. Repetí la prueba en un lugar más silencioso.");
        }

        if (spread > MaxSpreadMs)
        {
            return new CalibrationResult(false, median, spread, good.Count, expected, 0,
                $"Las repeticiones no coinciden (dispersión de {spread:0.#} ms). Hay ruido o eco: repetí la prueba.");
        }

        var step = Settings.UserSettings.VoiceDelayStepMs;
        var suggested = (int)Math.Round(median / step) * step;
        suggested = Math.Clamp(suggested, 0, Settings.UserSettings.MaxVoiceDelayMs);
        return new CalibrationResult(true, median, spread, good.Count, expected, suggested, null);
    }
}
