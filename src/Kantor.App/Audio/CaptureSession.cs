using Kantor.Core.Audio;
using Kantor.Core.Pitch;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kantor.App.Audio;

/// <summary>Última lectura de un canal: nivel del último bloque y último análisis de tono, si el canal se analiza.</summary>
public readonly record struct ChannelReading(LevelReading Level, PitchFrame? Pitch);

/// <summary>
/// Captura un dispositivo por WASAPI en modo compartido y mide el nivel de cada canal por separado.
/// El tono solo se analiza en los canales elegidos con <see cref="SetPitchChannels"/>.
/// El audio capturado no se reproduce ni se guarda: solo se miden niveles y tono.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private readonly object _gate = new();
    private readonly AudioInputDevice _device;
    private readonly ChannelReading[] _readings;
    private readonly Dictionary<int, PitchTracker> _trackers = [];

    private WasapiCapture? _capture;
    private MMDevice? _mmDevice;
    private float[][] _channelBuffers;

    public CaptureSession(AudioInputDevice device)
    {
        _device = device;
        _readings = new ChannelReading[device.Channels];
        Array.Fill(_readings, new ChannelReading(LevelReading.Silence, null));
        _channelBuffers = NewBuffers(4096);
    }

    public AudioInputDevice Device => _device;

    /// <summary>Se produce al detenerse la captura; la excepción es no nula si fue por un fallo (por ejemplo, desconexión).</summary>
    public event EventHandler<Exception?>? Stopped;

    public bool IsRunning => _capture is not null;

    public void Start()
    {
        if (_capture is not null)
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        _mmDevice = enumerator.GetDevice(_device.Id);
        var capture = new WasapiCapture(_mmDevice);
        capture.DataAvailable += OnData;
        capture.RecordingStopped += OnStopped;
        _capture = capture;
        capture.StartRecording();
    }

    public void Stop()
    {
        _capture?.StopRecording();
    }

    /// <summary>Define qué canales se analizan para tono; el resto solo mide nivel.</summary>
    public void SetPitchChannels(IEnumerable<int> channels)
    {
        var wanted = channels.Where(c => c >= 0 && c < _device.Channels).ToHashSet();
        lock (_gate)
        {
            foreach (var c in _trackers.Keys.Where(c => !wanted.Contains(c)).ToList())
            {
                _trackers.Remove(c);
                _readings[c] = _readings[c] with { Pitch = null };
            }

            foreach (var c in wanted.Where(c => !_trackers.ContainsKey(c)))
            {
                _trackers[c] = new PitchTracker(_device.SampleRate);
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

    public void Dispose()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            try
            {
                capture.StopRecording();
            }
            catch (InvalidOperationException)
            {
                // Ya estaba detenida.
            }

            capture.Dispose();
        }

        _mmDevice?.Dispose();
        _mmDevice = null;
    }

    private float[][] NewBuffers(int frames) =>
        Enumerable.Range(0, _device.Channels).Select(_ => new float[frames]).ToArray();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var bytesPerFrame = PcmDecoder.BytesPerSample(_device.Format) * _device.Channels;
        var frames = e.BytesRecorded / bytesPerFrame;
        if (frames == 0)
        {
            return;
        }

        if (_channelBuffers[0].Length < frames)
        {
            _channelBuffers = NewBuffers(frames);
        }

        var buffers = _channelBuffers;
        var decoded = PcmDecoder.Deinterleave(e.Buffer.AsSpan(0, e.BytesRecorded), _device.Format, _device.Channels, buffers);

        lock (_gate)
        {
            for (var c = 0; c < _device.Channels; c++)
            {
                var block = buffers[c].AsSpan(0, decoded);
                PitchFrame? pitch = _readings[c].Pitch;
                if (_trackers.TryGetValue(c, out var tracker) && tracker.Push(block, out var frame))
                {
                    pitch = frame;
                }

                _readings[c] = new ChannelReading(LevelMeter.Measure(block), pitch);
            }
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        Stopped?.Invoke(this, e.Exception);
    }
}
