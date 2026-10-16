// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Timing;

/// <summary>
/// Relación entre el índice de cuadro de un flujo de audio (que avanza en bloques exactos) y la base de
/// tiempo común (<see cref="HostClock"/>). Cada bloque llega con un instante de lectura que tiene retrasos
/// de planificación: nunca adelantado, a veces tarde. Se asume la frecuencia de muestreo nominal y se
/// estima el desplazamiento con un percentil bajo de una ventana de bloques recientes, que descarta los
/// bloques tardíos y promedia el resto.
/// </summary>
public sealed class FrameClock
{
    private readonly double _ticksPerFrame;
    private readonly double[] _offsets;
    private readonly double[] _scratch;
    private readonly double _percentile;
    private int _count;
    private int _next;
    private double _offset;

    public FrameClock(int sampleRate, int capacity = 128, double percentile = 0.2)
    {
        if (sampleRate <= 0 || capacity < 3 || percentile is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _ticksPerFrame = HostClock.TicksPerSecond / (double)sampleRate;
        _offsets = new double[capacity];
        _scratch = new double[capacity];
        _percentile = percentile;
    }

    /// <summary>Hay suficientes lecturas para confiar en la estimación.</summary>
    public bool IsReady => _count >= 3;

    public int Count => _count;

    /// <summary>Empieza de cero (por ejemplo, tras reiniciar el flujo).</summary>
    public void Reset()
    {
        _count = 0;
        _next = 0;
        _offset = 0;
    }

    /// <summary>Registra que el cuadro <paramref name="frame"/> (primero del bloque) se leyó en <paramref name="hostTicks"/>.</summary>
    public void Add(long frame, long hostTicks)
    {
        _offsets[_next] = hostTicks - frame * _ticksPerFrame;
        _next = (_next + 1) % _offsets.Length;
        if (_count < _offsets.Length)
        {
            _count++;
        }

        Array.Copy(_offsets, _scratch, _count);
        Array.Sort(_scratch, 0, _count);
        var index = (int)Math.Round((_count - 1) * _percentile);
        _offset = _scratch[index];
    }

    /// <summary>Índice de cuadro (con fracción) que corresponde al instante dado.</summary>
    public double FramesAt(long hostTicks) => (hostTicks - _offset) / _ticksPerFrame;

    /// <summary>Instante que corresponde al cuadro dado.</summary>
    public long HostAt(double frame) => (long)Math.Round(_offset + frame * _ticksPerFrame);
}
