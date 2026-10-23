// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using Kantor.Core.Audio;
using Kantor.Core.Data;
using Kantor.Core.Demo;
using Kantor.Core.Pitch;
using Kantor.Core.Settings;
using Kantor.Core.Songs;
using Kantor.Core.Timing;

namespace Kantor.App.Demo;

/// <summary>
/// Modo demo (<c>KANTARIA_DEMO=1</c>): para sacar capturas o probar sin micrófonos. Usa una carpeta de datos propia
/// (no toca la del usuario), ofrece una entrada simulada con cuatro canales de voces que cantan la canción en curso,
/// abre dos micrófonos en ella, agrega las canciones de demostración a la biblioteca y crea dos jugadores si no hay.
/// El audio de la canción es el real; solo la voz es simulada.
/// </summary>
public static class DemoMode
{
    public const string Variable = "KANTARIA_DEMO";

    public const string DeviceId = "kantaria-demo-input";

    /// <summary>Retardo con el que "cantan" los cantantes simulados: el que la puntuación compensa por omisión.</summary>
    public static readonly TimeSpan Reaction = TimeSpan.FromMilliseconds(UserSettings.DefaultVoiceDelayMs);

    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable(Variable) is "1" or "true" or "TRUE";

    public static readonly AudioInputDevice Device = new(
        DeviceId, "Kantaria Demo (voces simuladas)", MicrophoneMap.MaxSlots, 48000, SampleFormat.Float32, false);

    /// <summary>Se llama al arrancar, antes de leer ajustes o base de datos: la carpeta de datos queda aparte.</summary>
    public static void Prepare()
    {
        if (Enabled && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirVariable)))
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirVariable,
                Path.Combine(Path.GetTempPath(), "Kantaria-demo"));
        }
    }

    /// <summary>Ajustes de arranque del demo: dos micrófonos simulados y las canciones de demostración, si no hay otros.</summary>
    public static UserSettings Seed(UserSettings prefs)
    {
        if (prefs.Microphones is null)
        {
            prefs = prefs with { Microphones = [new MicrophoneSetting(0, DeviceId, 0), new MicrophoneSetting(1, DeviceId, 1)] };
        }

        if (prefs.LibraryPaths is not { Count: > 0 } && FindDemoSongs() is { } songs)
        {
            prefs = prefs with { LibraryPaths = [songs] };
        }

        return prefs;
    }

    public static void SeedPlayers(KantorStore store)
    {
        if (store.ListPlayers().Count == 0)
        {
            store.CreatePlayer("Ana", "avatar_001");
            store.CreatePlayer("Leo", "avatar_008");
        }
    }

    /// <summary>
    /// Carpeta con las canciones de demostración: <c>KANTARIA_DEMO_SONGS</c> si está definida, o <c>samples\kantor-demo-songs</c>
    /// del repositorio subiendo desde el ejecutable.
    /// </summary>
    private static string? FindDemoSongs()
    {
        var chosen = Environment.GetEnvironmentVariable("KANTARIA_DEMO_SONGS");
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            return Directory.Exists(chosen) ? chosen : null;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "kantor-demo-songs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// El motor de siempre (reproducción real) con una entrada más: la simulada. Como el demo se usa sin micrófonos,
/// la lista de entradas es solo la simulada.
/// </summary>
public sealed class DemoEngine : IAudioEngine
{
    private readonly IAudioEngine _inner;
    private readonly Func<Song?> _song;
    private ISongTimeMap? _time;

    public DemoEngine(IAudioEngine inner, Func<Song?> song)
    {
        _inner = inner;
        _song = song;
    }

    public AudioEngineKind Kind => _inner.Kind;

    public string Name => $"{_inner.Name} · demo";

    public IReadOnlyList<string> OutputPairs => _inner.OutputPairs;

    public int OutputPair => _inner.OutputPair;

    public event EventHandler<string>? Failed
    {
        add => _inner.Failed += value;
        remove => _inner.Failed -= value;
    }

    public ISongPlayer CreatePlayer()
    {
        var player = _inner.CreatePlayer();
        _time = player;
        return player;
    }

    public IReadOnlyList<AudioInputDevice> ListInputs() => [DemoMode.Device];

    public ICaptureSession CreateCapture(AudioInputDevice device) =>
        device.Id == DemoMode.DeviceId && _time is { } time
            ? new DemoCaptureSession(device, time, _song)
            : _inner.CreateCapture(device);

    public Core.Calibration.CalibrationResult Calibrate(AudioInputDevice input, int channel) =>
        throw new NotSupportedException("La calibración no está disponible en el modo demo.");

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Una entrada que no captura nada: cada ~20 ms entrega, por cada canal con tono, lo que cantaría el cantante simulado
/// de ese canal en el instante actual de la canción. Los marcos van con la misma marca de tiempo común que los reales,
/// así que el resto de la aplicación no distingue la diferencia.
/// </summary>
public sealed class DemoCaptureSession : ICaptureSession
{
    private static readonly TimeSpan Hop = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(40);

    private readonly ISongTimeMap _time;
    private readonly Func<Song?> _song;
    private readonly DemoSinger[] _singers;
    private readonly ChannelReading[] _last;
    private readonly object _gate = new();
    private int[] _channels = [];
    private CancellationTokenSource? _cts;

    public DemoCaptureSession(AudioInputDevice device, ISongTimeMap time, Func<Song?> song)
    {
        Device = device;
        _time = time;
        _song = song;
        _singers = Enumerable.Range(0, device.Channels)
            .Select(c => new DemoSinger(DemoSinger.DefaultAccuracy(c), DemoMode.Reaction, seed: 1000 + c))
            .ToArray();
        _last = Enumerable.Repeat(new ChannelReading(LevelReading.Silence, null), device.Channels).ToArray();
    }

    public AudioInputDevice Device { get; }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public int Discontinuities => 0;

    public event EventHandler<Exception?>? Stopped
    {
        add { }
        remove { }
    }

    public event Action<int, PitchFrame>? PitchFrameReady;

    public void SetPitchChannels(IEnumerable<int> channels)
    {
        lock (_gate)
        {
            _channels = channels.Where(c => c >= 0 && c < _singers.Length).ToArray();
        }
    }

    public ChannelReading Read(int channel)
    {
        lock (_gate)
        {
            return channel >= 0 && channel < _last.Length ? _last[channel] : new ChannelReading(LevelReading.Silence, null);
        }
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var thread = new Thread(() => Run(token)) { IsBackground = true, Name = "Kantor.DemoCapture" };
        thread.Start();
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Thread.Sleep(Hop);
            int[] channels;
            lock (_gate)
            {
                channels = _channels;
            }

            var end = HostClock.Now();
            var start = end - HostClock.FromSeconds(Window.TotalSeconds);
            var center = start + (end - start) / 2;
            var song = _song();
            var position = _time.SongPositionAt(center);

            foreach (var channel in channels)
            {
                var (frequency, level) = (0.0, DemoSinger.SilenceLevelDb);
                if (song is { Voices.Count: > 0 } s && position is { } at)
                {
                    var voice = s.Voices[VoiceAssignment.VoiceFor(channel, s.Voices.Count)];
                    (frequency, level) = _singers[channel].Sing(voice, new SongTiming(s.Bpm, s.GapMilliseconds), at);
                }

                var frame = new PitchFrame(frequency, frequency > 0 ? 0.95 : 0, level, start, end, frequency > 0 ? 0.05 : 1);
                lock (_gate)
                {
                    _last[channel] = new ChannelReading(new LevelReading(level, level + 6), frame);
                }

                PitchFrameReady?.Invoke(channel, frame);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
