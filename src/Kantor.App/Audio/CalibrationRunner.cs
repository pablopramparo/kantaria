// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Reflection;
using Kantor.Core.Audio;
using Kantor.Core.Calibration;
using Kantor.Core.Timing;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kantor.App.Audio;

/// <summary>
/// Mide la latencia salida → entrada de este equipo: reproduce barridos por la salida predeterminada de Windows
/// (WASAPI compartido, igual que <see cref="SongPlayer"/>) y los busca en el canal de entrada elegido por
/// correlación. Ambos lados se ubican en la base de tiempo común (QPC), igual que el resto de la aplicación.
/// Bloquea unos segundos: llamar desde un hilo de trabajo.
/// </summary>
public static class CalibrationRunner
{
    private const double Amplitude = 0.5;

    private static readonly FieldInfo AudioClientField =
        typeof(WasapiOut).GetField("audioClient", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException("NAudio.Wave.WasapiOut.audioClient no existe en esta versión de NAudio.");

    public static CalibrationResult Run(AudioInputDevice input, int channel)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        using var captureDevice = enumerator.GetDevice(input.Id);

        var renderRate = render.AudioClient.MixFormat.SampleRate;
        var template = CalibrationSignal.Chirp(input.SampleRate);
        var starts = CalibrationSignal.RepetitionStarts(renderRate);

        // Captura: un solo canal, con el QPC del primer cuadro de cada paquete.
        var samples = new List<float>((int)(CalibrationSignal.CaptureSeconds * input.SampleRate) + 4096);
        var packets = new List<(int Start, long Qpc)>();
        var buffers = Enumerable.Range(0, input.Channels).Select(_ => new float[8192]).ToArray();
        var gate = new object();
        var capture = new WasapiInputCapture(captureDevice);
        capture.Packet += (data, frames, qpc, discontinuity) =>
        {
            var decoded = PcmDecoder.Deinterleave(data, input.Format, input.Channels, buffers);
            lock (gate)
            {
                packets.Add((samples.Count, qpc));
                samples.AddRange(buffers[channel].AsSpan(0, decoded));
            }
        };

        // Reproducción y lecturas del reloj del dispositivo: (posición del flujo en segundos, QPC de esa lectura).
        var provider = new TrainProvider(CalibrationSignal.Train(renderRate, Amplitude), renderRate);
        using var output = new WasapiOut(render, AudioClientShareMode.Shared, true, 100);
        output.Init(provider);
        var clock = ((AudioClient)AudioClientField.GetValue(output)!).AudioClockClient;
        var polls = new List<(double Stream, long Host)>();
        var stop = false;
        var poller = new Thread(() =>
        {
            while (!stop)
            {
                if (clock.GetPosition(out var position, out var qpc))
                {
                    lock (polls)
                    {
                        polls.Add((position / (double)clock.Frequency, (long)qpc));
                    }
                }

                Thread.Sleep(5);
            }
        }) { IsBackground = true };

        try
        {
            capture.Start();
            poller.Start();
            output.Play();
            Thread.Sleep(TimeSpan.FromSeconds(CalibrationSignal.CaptureSeconds));
        }
        finally
        {
            output.Stop();
            stop = true;
            poller.Join(1000);
            capture.Dispose();
        }

        float[] captured;
        List<(int Start, long Qpc)> map;
        lock (gate)
        {
            captured = [.. samples];
            map = [.. packets];
        }

        List<(double Stream, long Host)> clockReadings;
        lock (polls)
        {
            clockReadings = [.. polls];
        }

        if (clockReadings.Count == 0 || map.Count == 0)
        {
            return new CalibrationResult(false, 0, 0, 0, CalibrationSignal.Repetitions, 0,
                "No se pudo leer el reloj de audio o la entrada. Revisá el dispositivo y repetí la prueba.");
        }

        var peaks = MatchedFilter.FindPeaks(captured, template, CalibrationSignal.Repetitions, input.SampleRate / 2);

        // Llegada de cada barrido en segundos de la base común.
        var arrivals = peaks.Select(p =>
        {
            var packet = map.Last(m => m.Start <= p.SampleIndex);
            var host = packet.Qpc + (p.SampleIndex - packet.Start) / input.SampleRate * HostClock.TicksPerSecond;
            return (HostClock.ToSeconds((long)host), p.Quality);
        }).ToList();

        // Instante en que el reloj de reproducción dice que suena cada barrido.
        var renders = starts.Select(frame =>
        {
            var stream = frame / (double)renderRate;
            var near = clockReadings.MinBy(r => Math.Abs(r.Stream - stream));
            return HostClock.ToSeconds(near.Host) + (stream - near.Stream);
        }).ToList();

        var paired = LatencyEvaluation.Pair(arrivals, renders);
        return LatencyEvaluation.Evaluate(paired, CalibrationSignal.Repetitions);
    }

    /// <summary>Entrega la señal mono por ambos canales y silencio después; se detiene desde afuera.</summary>
    private sealed class TrainProvider : IWaveProvider
    {
        private readonly float[] _mono;
        private int _position;

        public TrainProvider(float[] mono, int sampleRate)
        {
            _mono = mono;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var frames = count / 8;
            for (var i = 0; i < frames; i++)
            {
                var value = _position < _mono.Length ? _mono[_position] : 0f;
                _position++;
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + i * 8), value);
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + i * 8 + 4), value);
            }

            return frames * 8;
        }
    }
}
