// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Audio;
using Kantor.Core.Pitch;
using NAudio.CoreAudioApi;

namespace Kantor.App.Audio;

/// <summary>
/// Captura un dispositivo por WASAPI (modo compartido) y mide el nivel de cada canal por separado. El tono
/// solo se analiza en los canales elegidos con <see cref="SetPitchChannels"/>, y cada análisis lleva el
/// intervalo de las muestras analizadas en la base de tiempo común. El audio no se reproduce ni se guarda:
/// solo se miden niveles y tono.
/// </summary>
public sealed class CaptureSession : ICaptureSession
{
    private readonly AudioInputDevice _device;
    private readonly ChannelAnalyzer _analyzer;
    private readonly List<(int Channel, PitchFrame Frame)> _emitted = []; // solo el hilo de captura

    private WasapiInputCapture? _capture;
    private MMDevice? _mmDevice;
    private float[][] _channelBuffers;
    private int _discontinuities;

    public CaptureSession(AudioInputDevice device)
    {
        _device = device;
        _analyzer = new ChannelAnalyzer(device.Channels, device.SampleRate);
        _channelBuffers = NewBuffers(4096);
    }

    public AudioInputDevice Device => _device;

    /// <summary>Paquetes que llegaron marcados con un hueco en el flujo desde que arrancó (diagnóstico).</summary>
    public int Discontinuities => Volatile.Read(ref _discontinuities);

    /// <summary>Se produce al detenerse la captura por un fallo (por ejemplo, desconexión).</summary>
    public event EventHandler<Exception?>? Stopped;

    /// <summary>
    /// Un análisis de tono terminó. Se invoca en el hilo de captura, fuera de cualquier bloqueo
    /// interno; el manejador debe ser breve.
    /// </summary>
    public event Action<int, PitchFrame>? PitchFrameReady;

    public bool IsRunning => _capture is not null;

    public void Start()
    {
        if (_capture is not null)
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        _mmDevice = enumerator.GetDevice(_device.Id);
        var capture = new WasapiInputCapture(_mmDevice);
        capture.Packet += OnPacket;
        capture.Stopped += OnStopped;
        _capture = capture;
        capture.Start();
    }

    /// <summary>Define qué canales se analizan para tono; el resto solo mide nivel.</summary>
    public void SetPitchChannels(IEnumerable<int> channels) => _analyzer.SetPitchChannels(channels);

    public ChannelReading Read(int channel) => _analyzer.Read(channel);

    public void Dispose()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.Packet -= OnPacket;
            capture.Stopped -= OnStopped;
            capture.Dispose();
        }

        _mmDevice?.Dispose();
        _mmDevice = null;
    }

    private float[][] NewBuffers(int frames) =>
        Enumerable.Range(0, _device.Channels).Select(_ => new float[frames]).ToArray();

    private void OnPacket(ReadOnlySpan<byte> data, int frames, long hostTicks, bool discontinuity)
    {
        if (frames == 0)
        {
            return;
        }

        if (_channelBuffers[0].Length < frames)
        {
            _channelBuffers = NewBuffers(frames);
        }

        if (discontinuity)
        {
            Interlocked.Increment(ref _discontinuities);
        }

        var decoded = PcmDecoder.Deinterleave(data, _device.Format, _device.Channels, _channelBuffers);
        _emitted.Clear();
        _analyzer.Process(_channelBuffers, decoded, hostTicks, discontinuity, _emitted);

        foreach (var (channel, frame) in _emitted)
        {
            PitchFrameReady?.Invoke(channel, frame);
        }
    }

    private void OnStopped(object? sender, Exception? error)
    {
        Stopped?.Invoke(this, error);
    }
}
