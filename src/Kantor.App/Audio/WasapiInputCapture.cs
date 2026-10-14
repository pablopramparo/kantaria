// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kantor.App.Audio;

/// <summary>Un paquete de audio entrelazado y el QPC (100 ns) de su primera muestra, según el dispositivo.</summary>
public delegate void CapturePacketHandler(ReadOnlySpan<byte> data, int frames, long hostTicks, bool discontinuity);

/// <summary>
/// Captura de un endpoint WASAPI en modo compartido con las marcas de tiempo que informa el dispositivo
/// para cada paquete. <c>WasapiCapture</c> de NAudio las descarta, y sin ellas no se puede ubicar una
/// muestra en el tiempo real. No reproduce ni guarda el audio.
/// </summary>
public sealed class WasapiInputCapture : IDisposable
{
    private const long BufferDuration100ns = 1_000_000; // 100 ms

    private readonly MMDevice _device;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly AutoResetEvent _dataReady = new(false);

    private AudioClient? _client;
    private AudioCaptureClient? _capture;
    private Thread? _thread;
    private byte[] _scratch = new byte[4096];

    public WasapiInputCapture(MMDevice device)
    {
        _device = device;
    }

    public event CapturePacketHandler? Packet;

    /// <summary>Se produce al terminar la captura; la excepción no es nula si fue por un fallo (por ejemplo, desconexión).</summary>
    public event EventHandler<Exception?>? Stopped;

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        var client = _device.AudioClient;
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, BufferDuration100ns, 0,
            client.MixFormat, Guid.Empty);
        client.SetEventHandle(_dataReady.SafeWaitHandle.DangerousGetHandle());
        _capture = client.AudioCaptureClient;
        _client = client;
        client.Start();

        _thread = new Thread(Run) { IsBackground = true, Name = "Kantor.Capture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _dataReady.Set();
        _thread?.Join(2000);
        _client?.Dispose();
        _client = null;
        _dataReady.Dispose();
        _stop.Dispose();
    }

    private void Run()
    {
        Exception? error = null;
        try
        {
            var capture = _capture!;
            var blockAlign = _client!.MixFormat.BlockAlign;
            while (!_stop.IsSet)
            {
                _dataReady.WaitOne(500);
                while (!_stop.IsSet && capture.GetNextPacketSize() > 0)
                {
                    var data = capture.GetBuffer(out var frames, out var flags, out _, out var qpc);
                    var bytes = frames * blockAlign;
                    if (_scratch.Length < bytes)
                    {
                        _scratch = new byte[bytes];
                    }

                    if (flags.HasFlag(AudioClientBufferFlags.Silent))
                    {
                        Array.Clear(_scratch, 0, bytes);
                    }
                    else
                    {
                        Marshal.Copy(data, _scratch, 0, bytes);
                    }

                    capture.ReleaseBuffer(frames);
                    Packet?.Invoke(_scratch.AsSpan(0, bytes), frames, qpc, flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity));
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ObjectDisposedException)
        {
            error = e;
        }
        finally
        {
            try
            {
                _client?.Stop();
            }
            catch (Exception e) when (e is COMException or ObjectDisposedException)
            {
                // El dispositivo ya no está.
            }

            if (!_stop.IsSet)
            {
                Stopped?.Invoke(this, error);
            }
        }
    }
}
