// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using NAudio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Kantor.App.Audio;

/// <summary>
/// Vista previa de una canción mientras se navega la biblioteca: suena un tramo a volumen bajo, con entrada
/// suave, por la salida predeterminada de Windows. No toca el motor de la canción ni la captura. Abrir el archivo
/// se hace en segundo plano; pedir otra vista previa (o <see cref="Stop"/>) descarta la anterior.
/// </summary>
public sealed class SongPreview : IDisposable
{
    /// <summary>Volumen de la vista previa (la reproducción de la canción va a 1,0).</summary>
    public const float Volume = 0.35f;

    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;

    /// <param name="startSeconds">Desde dónde; sin dato, un cuarto de la canción.</param>
    /// <param name="started">Se llama (desde un hilo de fondo) cuando el audio arranca, con el segundo real de inicio.</param>
    public void Play(string audioPath, double? startSeconds, Action<double>? started = null)
    {
        Stop();
        var cancel = new CancellationTokenSource();
        _cancel = cancel;

        Task.Run(() =>
        {
            AudioFileReader? reader = null;
            WaveOutEvent? output = null;
            try
            {
                reader = new AudioFileReader(audioPath);
                if (cancel.IsCancellationRequested)
                {
                    reader.Dispose();
                    return;
                }

                var total = reader.TotalTime.TotalSeconds;
                var start = Math.Clamp(startSeconds ?? total * 0.25, 0, Math.Max(0, total - 12));
                reader.CurrentTime = TimeSpan.FromSeconds(start);

                var fade = new FadeInOutSampleProvider(reader, initiallySilent: true);
                fade.BeginFadeIn(600);
                output = new WaveOutEvent { DesiredLatency = 150 };
                output.Init(new VolumeSampleProvider(fade) { Volume = Volume });

                lock (_gate)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        output.Dispose();
                        reader.Dispose();
                        return;
                    }

                    _reader = reader;
                    _output = output;
                    output.Play();
                    started?.Invoke(start);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                          or System.Runtime.InteropServices.COMException or MmException
                                          or ArgumentException or NotSupportedException or FormatException)
            {
                // Una vista previa que no se puede reproducir no es un error: simplemente no suena.
                output?.Dispose();
                reader?.Dispose();
            }
        });
    }

    public void Stop()
    {
        _cancel?.Cancel();
        _cancel = null;
        lock (_gate)
        {
            _output?.Dispose();
            _reader?.Dispose();
            _output = null;
            _reader = null;
        }
    }

    public void Dispose() => Stop();
}
