// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Reflection;
using Kantor.Core.Audio;
using Kantor.Core.Calibration;
using Kantor.Core.Pitch;
using Kantor.Core.Timing;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Kantor.App.Audio;

/// <summary>
/// Motor de audio por un controlador ASIO. Reproducción y captura salen del mismo callback y del mismo reloj:
/// el cuadro de entrada <c>j</c> y el de salida <c>j</c> son contemporáneos en el controlador, y los retardos
/// de hardware (informados por el propio controlador) se descuentan al ubicar la voz y la reproducción en la
/// base de tiempo común:
/// <list type="bullet">
/// <item><description>posición oída = cuadros entregados al controlador − latencia de salida;</description></item>
/// <item><description>instante de captura de una muestra = instante de su cuadro − latencia de entrada.</description></item>
/// </list>
/// Así una muestra capturada se ubica en la posición de la canción que se estaba oyendo en ese momento.
/// Debe crearse y usarse desde un hilo STA (el de la interfaz), como exige ASIO.
/// </summary>
public sealed class AsioEngine : IAudioEngine
{
    public const int SampleRate = 48000;

    private readonly string _driverName;
    private readonly AsioOut _asio;
    private readonly int _inputCount;
    private readonly int _inputLatency;
    private readonly int _outputLatency;
    private readonly string[] _inputNames;
    private readonly List<string> _outputPairs = [];
    private readonly int _outputPair;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    private readonly object _lock = new();
    private readonly FrameClock _clock = new(SampleRate, 128);
    private readonly float[] _interleaved;
    private readonly float[][] _channels;
    private long _bufferStart;          // índice del primer cuadro del buffer en curso (entrada y salida)
    private bool _disposed;

    // Reproducción de la canción (decodificada en memoria como estéreo float de 48 kHz).
    private float[]? _song;
    private int _songFrames;
    private int _cursor;                // cuadros de la canción ya entregados al controlador
    private bool _want;                 // el usuario pidió reproducir
    private bool _paused;
    private bool _playing;              // hay un tramo de reproducción activo en el controlador
    private bool _segmentValid;
    private long _segGlobal;            // cuadro global donde empezó el tramo actual
    private int _segSong;               // cuadro de la canción donde empezó el tramo actual

    // Calibración: tren de barridos que reemplaza a la canción y captura de un canal.
    private float[]? _train;
    private long _calibStart = -1;
    private List<float>? _calibCapture;
    private int _calibChannel;

    private volatile AsioCaptureSession[] _sessions = [];

    public AsioEngine(string driverName, int outputPair)
    {
        _driverName = driverName;
        _asio = new AsioOut(driverName);
        try
        {
            _inputCount = _asio.DriverInputChannelCount;
            var outputs = _asio.DriverOutputChannelCount;
            if (outputs < 2)
            {
                throw new NotSupportedException("El controlador no tiene una salida estéreo.");
            }

            if (_inputCount < 1)
            {
                throw new NotSupportedException("El controlador no tiene entradas.");
            }

            if (!_asio.IsSampleRateSupported(SampleRate))
            {
                throw new NotSupportedException($"El controlador no admite {SampleRate / 1000} kHz.");
            }

            _inputNames = Enumerable.Range(0, _inputCount).Select(_asio.AsioInputChannelName).ToArray();
            for (var p = 0; p + 1 < outputs; p += 2)
            {
                _outputPairs.Add($"{_asio.AsioOutputChannelName(p)} + {_asio.AsioOutputChannelName(p + 1)}");
            }

            _outputPair = Math.Clamp(outputPair, 0, _outputPairs.Count - 1);
            _asio.ChannelOffset = _outputPair * 2;

            (_inputLatency, _outputLatency) = ReadLatencies(_asio);

            _asio.InitRecordAndPlayback(new Mixer(this), _inputCount, SampleRate);
            var frames = Math.Max(_asio.FramesPerBuffer, 1);
            _interleaved = new float[frames * _inputCount];
            _channels = Enumerable.Range(0, _inputCount).Select(_ => new float[frames]).ToArray();

            _asio.AudioAvailable += OnAudioAvailable;
            _asio.DriverResetRequest += OnDriverReset;
            _asio.Play();
        }
        catch
        {
            _asio.Dispose();
            throw;
        }
    }

    public AudioEngineKind Kind => AudioEngineKind.Asio;

    public string Name => $"ASIO · {_driverName}";

    public IReadOnlyList<string> OutputPairs => _outputPairs;

    public int OutputPair => _outputPair;

    /// <summary>Latencias que informa el controlador, en muestras (entrada, salida).</summary>
    public (int Input, int Output) ReportedLatencies => (_inputLatency, _outputLatency);

    public event EventHandler<string>? Failed;

    public ISongPlayer CreatePlayer() => new AsioSongPlayer(this);

    public IReadOnlyList<AudioInputDevice> ListInputs() =>
    [
        new AudioInputDevice(_driverName, _driverName, _inputCount, SampleRate, SampleFormat.Float32, IsDefault: true)
        {
            ChannelNames = _inputNames,
        },
    ];

    public ICaptureSession CreateCapture(AudioInputDevice device) => new AsioCaptureSession(this, device);

    /// <summary>Nombres de los controladores ASIO instalados.</summary>
    public static string[] InstalledDrivers()
    {
        try
        {
            return AsioOut.GetDriverNames();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException
                                      or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static (int Input, int Output) ReadLatencies(AsioOut asio)
    {
        // NAudio solo expone la latencia de salida; la de entrada está en las capacidades del controlador.
        var driver = typeof(AsioOut).GetField("driver", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(asio);
        var caps = driver?.GetType().GetProperty("Capabilities")?.GetValue(driver);
        var input = caps?.GetType().GetField("InputLatency")?.GetValue(caps) as int? ?? 0;
        var output = caps?.GetType().GetField("OutputLatency")?.GetValue(caps) as int? ?? asio.PlaybackLatency;
        return (Math.Max(input, 0), Math.Max(output, 0));
    }

    // ---- Callback del controlador -------------------------------------------------------------------------

    private void OnAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        var frames = e.SamplesPerBuffer;
        var host = HostClock.Now();
        long start;
        long captureHost;
        lock (_lock)
        {
            start = _bufferStart;
            _clock.Add(start, host);
            _bufferStart = start + frames;
            captureHost = (_clock.IsReady ? _clock.HostAt(start) : host)
                          - (long)(_inputLatency * (double)HostClock.TicksPerSecond / SampleRate);
        }

        if (_interleaved.Length < frames * _inputCount)
        {
            return; // un buffer mayor que el declarado: no se procesa
        }

        e.GetAsInterleavedSamples(_interleaved);
        for (var i = 0; i < frames; i++)
        {
            var baseIndex = i * _inputCount;
            for (var c = 0; c < _inputCount; c++)
            {
                _channels[c][i] = _interleaved[baseIndex + c];
            }
        }

        lock (_lock)
        {
            if (_calibCapture is not null && _train is not null)
            {
                if (_calibStart < 0)
                {
                    _calibStart = start;
                }

                var channel = _channels[Math.Clamp(_calibChannel, 0, _inputCount - 1)];
                for (var i = 0; i < frames; i++)
                {
                    _calibCapture.Add(channel[i]);
                }
            }
        }

        foreach (var session in _sessions)
        {
            session.Deliver(_channels, frames, captureHost);
        }
    }

    private void OnDriverReset(object? sender, EventArgs e)
    {
        Raise(Failed, "El controlador ASIO pidió reiniciarse (por ejemplo, por un cambio de frecuencia de muestreo " +
                      "o de configuración en el panel del fabricante).");
    }

    private void Raise(EventHandler<string>? handler, string message)
    {
        if (handler is null)
        {
            return;
        }

        if (_context is not null)
        {
            _context.Post(_ => handler(this, message), null);
        }
        else
        {
            handler(this, message);
        }
    }

    // ---- Salida: lo que el controlador pide en cada buffer ------------------------------------------------

    private sealed class Mixer(AsioEngine engine) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        public int Read(byte[] buffer, int offset, int count) => engine.FillOutput(buffer, offset, count);
    }

    private float _lastOutputPeak;

    /// <summary>Pico (0 a 1) de lo último entregado al controlador por la salida; diagnóstico.</summary>
    public float LastOutputPeak => Volatile.Read(ref _lastOutputPeak);

    private int FillOutput(byte[] buffer, int offset, int count)
    {
        var frames = count / 8;
        var output = buffer.AsSpan(offset, frames * 8);
        output.Clear();
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(output);
        bool endedNow = false;

        lock (_lock)
        {
            // El buffer que se llena ahora es el que acaba de abrirse en OnAudioAvailable.
            var bufferStart = _bufferStart - frames;

            if (_train is not null && _calibStart >= 0)
            {
                // Calibración: barridos en lugar de la canción, alineados con el primer cuadro capturado.
                for (var i = 0; i < frames; i++)
                {
                    var index = bufferStart + i - _calibStart;
                    if (index >= 0 && index < _train.Length)
                    {
                        floats[i * 2] = floats[i * 2 + 1] = _train[index];
                    }
                }

                return frames * 8;
            }

            if (_want && _song is not null && _cursor < _songFrames)
            {
                if (!_playing)
                {
                    _playing = true;
                    _segmentValid = true;
                    _segGlobal = bufferStart;
                    _segSong = _cursor;
                }

                var n = Math.Min(frames, _songFrames - _cursor);
                _song.AsSpan(_cursor * 2, n * 2).CopyTo(floats);
                _cursor += n;
                if (_cursor >= _songFrames)
                {
                    _want = false;
                    _playing = false;
                    _segmentValid = false;
                    _cursor = 0;
                    endedNow = true;
                }
            }
            else if (_playing)
            {
                _playing = false;
            }
        }

        var peak = 0f;
        foreach (var v in floats)
        {
            peak = Math.Max(peak, Math.Abs(v));
        }

        Volatile.Write(ref _lastOutputPeak, peak);

        if (endedNow)
        {
            // Al terminar por sí sola, como en el motor de Windows: vuelve al principio y avisa.
            Raise(_endedHandlers, string.Empty);
        }

        return frames * 8;
    }

    private EventHandler<string>? _endedHandlers;

    private void OnEnded(EventHandler<string> handler) => _endedHandlers += handler;

    private void OffEnded(EventHandler<string> handler) => _endedHandlers -= handler;

    // ---- Estado de reproducción (lo usa AsioSongPlayer) ----------------------------------------------------

    private void LoadSong(float[] interleavedStereo)
    {
        lock (_lock)
        {
            _song = interleavedStereo;
            _songFrames = interleavedStereo.Length / 2;
            ResetPlayback();
        }
    }

    private void ResetPlayback()
    {
        _cursor = 0;
        _want = false;
        _paused = false;
        _playing = false;
        _segmentValid = false;
    }

    private void PlaySong()
    {
        lock (_lock)
        {
            if (_song is null)
            {
                return;
            }

            if (_cursor >= _songFrames)
            {
                _cursor = 0;
            }

            _want = true;
            _paused = false;
        }
    }

    private void PauseSong()
    {
        lock (_lock)
        {
            if (_want)
            {
                _want = false;
                _paused = true;
            }
        }
    }

    private void StopSong()
    {
        lock (_lock)
        {
            ResetPlayback();
        }
    }

    private bool IsSongLoaded
    {
        get
        {
            lock (_lock)
            {
                return _song is not null;
            }
        }
    }

    private bool IsSongPlaying
    {
        get
        {
            lock (_lock)
            {
                return _want;
            }
        }
    }

    private bool IsSongPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    private TimeSpan SongDuration
    {
        get
        {
            lock (_lock)
            {
                return TimeSpan.FromSeconds(_songFrames / (double)SampleRate);
            }
        }
    }

    /// <summary>Posición de la canción que se está oyendo en el instante dado, o <c>null</c> si no está sonando.</summary>
    private TimeSpan? SongPositionAtCore(long hostTicks, bool requirePlaying)
    {
        lock (_lock)
        {
            if (!_clock.IsReady || !_segmentValid)
            {
                return null;
            }

            if (requirePlaying && !_playing)
            {
                return null;
            }

            var heardGlobal = _clock.FramesAt(hostTicks) - _outputLatency;
            var song = _segSong + (heardGlobal - _segGlobal);
            if (song < _segSong)
            {
                return requirePlaying ? null : TimeSpan.FromSeconds(_segSong / (double)SampleRate);
            }

            // Lo que se oye nunca supera lo ya entregado al controlador.
            return TimeSpan.FromSeconds(Math.Min(song, _cursor) / SampleRate);
        }
    }

    private TimeSpan SongPositionNow()
    {
        lock (_lock)
        {
            if (!_segmentValid)
            {
                // Sin tramo: en pausa antes de arrancar o detenida.
                return _paused ? TimeSpan.FromSeconds(_cursor / (double)SampleRate) : TimeSpan.Zero;
            }
        }

        return SongPositionAtCore(HostClock.Now(), requirePlaying: false) ?? TimeSpan.Zero;
    }

    // ---- Captura (la usa AsioCaptureSession) ---------------------------------------------------------------

    private void Register(AsioCaptureSession session)
    {
        lock (_lock)
        {
            _sessions = [.. _sessions, session];
        }
    }

    private void Unregister(AsioCaptureSession session)
    {
        lock (_lock)
        {
            _sessions = _sessions.Where(s => !ReferenceEquals(s, session)).ToArray();
        }
    }

    // ---- Calibración -------------------------------------------------------------------------------------

    public CalibrationResult Calibrate(AudioInputDevice input, int channel)
    {
        lock (_lock)
        {
            if (_want || _train is not null)
            {
                throw new InvalidOperationException("Detené la canción antes de calibrar.");
            }

            _train = CalibrationSignal.Train(SampleRate, 0.5);
            _calibCapture = new List<float>((int)(CalibrationSignal.CaptureSeconds * SampleRate) + 4096);
            _calibChannel = channel;
            _calibStart = -1;
        }

        float[] captured;
        try
        {
            Thread.Sleep(TimeSpan.FromSeconds(CalibrationSignal.CaptureSeconds));
        }
        finally
        {
            lock (_lock)
            {
                captured = [.. _calibCapture ?? []];
                _train = null;
                _calibCapture = null;
                _calibStart = -1;
            }
        }

        var template = CalibrationSignal.Chirp(SampleRate);
        var peaks = MatchedFilter.FindPeaks(captured, template, CalibrationSignal.Repetitions, SampleRate / 2);
        var starts = CalibrationSignal.RepetitionStarts(SampleRate).Select(s => s / (double)SampleRate).ToList();
        var paired = LatencyEvaluation.Pair(peaks.Select(p => (p.SampleIndex / SampleRate, p.Quality)).ToList(), starts);
        var result = LatencyEvaluation.Evaluate(paired, CalibrationSignal.Repetitions);
        if (!result.Success)
        {
            return result;
        }

        // Lo medido es ida y vuelta; el controlador ya compensa sus latencias en la ubicación de la voz, así
        // que el retardo que hace falta es lo que sobra.
        var compensatedMs = (_inputLatency + _outputLatency) * 1000.0 / SampleRate;
        var residual = Math.Max(0, result.MedianMs - compensatedMs);
        var step = Core.Settings.UserSettings.VoiceDelayStepMs;
        var suggested = Math.Clamp((int)Math.Round(residual / step) * step, 0, Core.Settings.UserSettings.MaxVoiceDelayMs);
        return result with { SuggestedDelayMs = suggested, CompensatedMs = compensatedMs };
    }

    // ---- Ciclo de vida ------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _asio.AudioAvailable -= OnAudioAvailable;
        _asio.DriverResetRequest -= OnDriverReset;
        try
        {
            _asio.Stop();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // El controlador ya no responde.
        }

        _asio.Dispose();
    }

    // ---- Reproductor y captura -----------------------------------------------------------------------------

    private sealed class AsioSongPlayer : ISongPlayer
    {
        private readonly AsioEngine _engine;
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;
        private readonly EventHandler<string> _onEnded;

        public AsioSongPlayer(AsioEngine engine)
        {
            _engine = engine;
            _onEnded = (_, _) => Ended?.Invoke(this, EventArgs.Empty);
            engine.OnEnded(_onEnded);
        }

        public event EventHandler? Ended;

        public bool IsLoaded => _engine.IsSongLoaded;

        public bool IsPlaying => _engine.IsSongPlaying;

        public bool IsPaused => _engine.IsSongPaused;

        public TimeSpan Position => _engine.SongPositionNow();

        public TimeSpan Duration => _engine.SongDuration;

        public TimeSpan? SongPositionAt(long hostTicks) => _engine.SongPositionAtCore(hostTicks, requirePlaying: true);

        public void Load(string audioPath) => _engine.LoadSong(DecodeStereo48k(audioPath));

        public void Play() => _engine.PlaySong();

        public void Pause() => _engine.PauseSong();

        public void Stop() => _engine.StopSong();

        public void Dispose()
        {
            _engine.OffEnded(_onEnded);
            _engine.StopSong();
        }

        /// <summary>Decodifica todo el archivo a estéreo float de 48 kHz: el callback de audio nunca toca el disco.</summary>
        private static float[] DecodeStereo48k(string path)
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider source = reader;
            if (source.WaveFormat.SampleRate != SampleRate)
            {
                source = new WdlResamplingSampleProvider(source, SampleRate);
            }

            var channels = source.WaveFormat.Channels;
            var result = new List<float>((int)(reader.TotalTime.TotalSeconds * SampleRate * 2) + 4096);
            var buffer = new float[SampleRate * channels / 4];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i + channels <= read; i += channels)
                {
                    var left = buffer[i];
                    var right = channels == 1 ? buffer[i] : buffer[i + 1];
                    result.Add(left);
                    result.Add(right);
                }
            }

            return [.. result];
        }
    }

    private sealed class AsioCaptureSession : ICaptureSession
    {
        private readonly AsioEngine _engine;
        private readonly ChannelAnalyzer _analyzer;
        private readonly List<(int Channel, PitchFrame Frame)> _emitted = []; // solo el hilo del controlador
        private volatile bool _running;

        public AsioCaptureSession(AsioEngine engine, AudioInputDevice device)
        {
            _engine = engine;
            Device = device;
            _analyzer = new ChannelAnalyzer(engine._inputCount, SampleRate);
        }

        public AudioInputDevice Device { get; }

        public bool IsRunning => _running;

        /// <summary>ASIO no marca huecos en el flujo.</summary>
        public int Discontinuities => 0;

        public event EventHandler<Exception?>? Stopped;

        public event Action<int, PitchFrame>? PitchFrameReady;

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _engine.Register(this);
            _engine.Failed += OnEngineFailed;
        }

        public void SetPitchChannels(IEnumerable<int> channels) => _analyzer.SetPitchChannels(channels);

        public ChannelReading Read(int channel) => _analyzer.Read(channel);

        public void Dispose()
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _engine.Failed -= OnEngineFailed;
            _engine.Unregister(this);
        }

        internal void Deliver(float[][] channels, int frames, long hostTicks)
        {
            if (!_running)
            {
                return;
            }

            _emitted.Clear();
            _analyzer.Process(channels, frames, hostTicks, discontinuity: false, _emitted);
            foreach (var (channel, frame) in _emitted)
            {
                PitchFrameReady?.Invoke(channel, frame);
            }
        }

        private void OnEngineFailed(object? sender, string message) =>
            Stopped?.Invoke(this, new InvalidOperationException(message));
    }
}
