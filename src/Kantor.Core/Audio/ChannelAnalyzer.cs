// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Pitch;

namespace Kantor.Core.Audio;

/// <summary>
/// Mide el nivel de cada canal de una entrada y analiza el tono de los canales elegidos, ubicando cada
/// análisis en la base de tiempo común. Es independiente del motor de audio: quien captura le pasa bloques
/// de muestras por canal y el instante de la primera muestra.
/// </summary>
public sealed class ChannelAnalyzer
{
    private readonly object _gate = new();
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly ChannelReading[] _readings;
    private readonly Dictionary<int, PitchTracker> _trackers = [];

    public ChannelAnalyzer(int channels, int sampleRate)
    {
        _channels = channels;
        _sampleRate = sampleRate;
        _readings = new ChannelReading[channels];
        Array.Fill(_readings, new ChannelReading(LevelReading.Silence, null));
    }

    public int Channels => _channels;

    /// <summary>Define qué canales se analizan para tono; el resto solo mide nivel.</summary>
    public void SetPitchChannels(IEnumerable<int> channels)
    {
        var wanted = channels.Where(c => c >= 0 && c < _channels).ToHashSet();
        lock (_gate)
        {
            foreach (var c in _trackers.Keys.Where(c => !wanted.Contains(c)).ToList())
            {
                _trackers.Remove(c);
                _readings[c] = _readings[c] with { Pitch = null };
            }

            foreach (var c in wanted.Where(c => !_trackers.ContainsKey(c)))
            {
                _trackers[c] = new PitchTracker(_sampleRate);
            }
        }
    }

    public ChannelReading Read(int channel)
    {
        lock (_gate)
        {
            return _readings[channel];
        }
    }

    /// <summary>
    /// Procesa un bloque. <paramref name="blocks"/> trae un arreglo por canal con al menos
    /// <paramref name="frames"/> muestras; <paramref name="hostTicks"/> es el instante de la primera. Agrega a
    /// <paramref name="emitted"/> los análisis de tono que se completaron (canal y resultado).
    /// </summary>
    public void Process(float[][] blocks, int frames, long hostTicks, bool discontinuity,
        List<(int Channel, PitchFrame Frame)> emitted)
    {
        lock (_gate)
        {
            for (var c = 0; c < _channels && c < blocks.Length; c++)
            {
                var block = blocks[c].AsSpan(0, frames);
                var pitch = _readings[c].Pitch;
                if (_trackers.TryGetValue(c, out var tracker))
                {
                    if (discontinuity)
                    {
                        // Hubo un hueco en el flujo: lo acumulado ya no es contiguo con este bloque.
                        tracker.Reset();
                    }

                    if (tracker.Push(block, hostTicks, out var frame))
                    {
                        pitch = frame;
                        emitted.Add((c, frame));
                    }
                }

                _readings[c] = new ChannelReading(LevelMeter.Measure(block), pitch);
            }
        }
    }
}
