// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Pitch;

/// <param name="MinCmnd">
/// Valor mínimo de la función de YIN en el rango de períodos: cuánto se parece la señal a periódica
/// (0 = periódica perfecta). Con voz por debajo del umbral (0,15 por omisión); si no hay voz, dice qué
/// tan cerca estuvo. Es solo diagnóstico: no interviene en la decisión.
/// </param>
public readonly record struct PitchEstimate(double Frequency, double Confidence, double MinCmnd = 1)
{
    public bool IsVoiced => Frequency > 0;

    public static PitchEstimate Unvoiced => new(0, 0);
}

/// <summary>
/// Detector de frecuencia fundamental YIN (de Cheveigné y Kawahara, 2002): diferencia cuadrática,
/// normalización acumulada, umbral absoluto e interpolación parabólica.
/// Una instancia reutiliza sus buffers y no es segura entre hilos.
/// </summary>
public sealed class PitchDetector
{
    private readonly int _sampleRate;
    private readonly int _tauMin;
    private readonly int _tauMax;
    private readonly int _integrationLength;
    private readonly double _threshold;
    private readonly float[] _cmnd;

    /// <param name="sampleRate">Frecuencia de muestreo del audio.</param>
    /// <param name="minFrequency">Frecuencia más grave a detectar (una voz de bajo llega a ~65 Hz).</param>
    /// <param name="maxFrequency">Frecuencia más aguda a detectar.</param>
    /// <param name="threshold">Umbral de aperiodicidad de YIN; menor es más estricto.</param>
    /// <param name="windowSize">Muestras por análisis; 0 elige el menor tamaño potencia de 2 adecuado.</param>
    public PitchDetector(int sampleRate, double minFrequency = 60, double maxFrequency = 1000,
        double threshold = 0.15, int windowSize = 0)
    {
        if (sampleRate <= 0 || minFrequency <= 0 || maxFrequency <= minFrequency)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Parámetros de detección inválidos.");
        }

        _sampleRate = sampleRate;
        _threshold = threshold;
        _tauMin = Math.Max(2, (int)Math.Floor(sampleRate / maxFrequency));
        _tauMax = (int)Math.Ceiling(sampleRate / minFrequency);

        if (windowSize == 0)
        {
            windowSize = 1;
            while (windowSize < _tauMax * 2.5)
            {
                windowSize <<= 1;
            }
        }

        if (windowSize <= _tauMax + 16)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), "La ventana es demasiado corta para la frecuencia mínima.");
        }

        WindowSize = windowSize;
        _integrationLength = windowSize - _tauMax;
        _cmnd = new float[_tauMax + 2];
    }

    public int WindowSize { get; }

    public PitchEstimate Detect(ReadOnlySpan<float> window)
    {
        if (window.Length != WindowSize)
        {
            throw new ArgumentException($"Se esperaban {WindowSize} muestras.", nameof(window));
        }

        // Diferencia cuadrática d(tau) y su normalización por media acumulada (cmnd).
        _cmnd[0] = 1;
        double running = 0;
        for (var tau = 1; tau <= _tauMax; tau++)
        {
            double d = 0;
            for (var j = 0; j < _integrationLength; j++)
            {
                var delta = window[j] - window[j + tau];
                d += delta * delta;
            }

            running += d;
            _cmnd[tau] = running <= 0 ? 1f : (float)(d * tau / running);
        }

        var minCmnd = 1.0;
        for (var tau = _tauMin; tau < _tauMax; tau++)
        {
            minCmnd = Math.Min(minCmnd, _cmnd[tau]);
        }

        for (var tau = _tauMin; tau < _tauMax; tau++)
        {
            if (_cmnd[tau] >= _threshold)
            {
                continue;
            }

            // Primer valle bajo el umbral: bajar hasta su mínimo local.
            while (tau + 1 < _tauMax && _cmnd[tau + 1] < _cmnd[tau])
            {
                tau++;
            }

            var refined = Refine(tau);
            return new PitchEstimate(_sampleRate / refined, Math.Clamp(1 - _cmnd[tau], 0, 1), minCmnd);
        }

        return new PitchEstimate(0, 0, minCmnd);
    }

    private double Refine(int tau)
    {
        if (tau <= 1 || tau >= _tauMax)
        {
            return tau;
        }

        double a = _cmnd[tau - 1];
        double b = _cmnd[tau];
        double c = _cmnd[tau + 1];
        var denominator = 2 * (a - 2 * b + c);
        return Math.Abs(denominator) < 1e-12 ? tau : tau + (a - c) / denominator;
    }
}
