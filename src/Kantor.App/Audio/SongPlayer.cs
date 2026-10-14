using Kantor.Core.Timing;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kantor.App.Audio;

/// <summary>
/// Reproduce el audio de la canción por WASAPI y expone su posición como <see cref="ISongClock"/>.
/// La posición sale del reloj del dispositivo, no de un cronómetro aparte, para que más adelante
/// la captura de micrófonos se sincronice contra lo que realmente se está reproduciendo.
/// </summary>
public sealed class SongPlayer : ISongClock, IDisposable
{
    private const int OutputLatencyMilliseconds = 100;

    private AudioFileReader? _reader;
    private WasapiOut? _output;

    private bool _paused;
    private double _pausedAt;
    private double _skippedWhilePaused;

    public event EventHandler? Ended;

    public bool IsLoaded => _reader is not null;

    public bool IsPlaying => _output is { PlaybackState: PlaybackState.Playing };

    public bool IsPaused => _paused;

    public TimeSpan Position
    {
        get
        {
            if (_output is null)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(_paused ? _pausedAt : RawSeconds() - _skippedWhilePaused);
        }
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public void Load(string audioPath)
    {
        Unload();
        _reader = new AudioFileReader(audioPath);
    }

    public void Play()
    {
        if (_reader is null)
        {
            return;
        }

        if (_output is null)
        {
            _reader.Position = 0;
            _skippedWhilePaused = 0;
            _paused = false;
            _output = new WasapiOut(AudioClientShareMode.Shared, OutputLatencyMilliseconds);
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Init(_reader);
        }
        else if (_paused)
        {
            // El reloj del dispositivo puede seguir avanzando durante la pausa; no contamos ese tiempo.
            _skippedWhilePaused += Math.Max(0, RawSeconds() - _skippedWhilePaused - _pausedAt);
            _paused = false;
        }

        _output.Play();
    }

    public void Pause()
    {
        if (_output is null || _paused || _output.PlaybackState != PlaybackState.Playing)
        {
            return;
        }

        _pausedAt = RawSeconds() - _skippedWhilePaused;
        _paused = true;
        _output.Pause();
    }

    public void Stop()
    {
        ReleaseOutput();
        if (_reader is not null)
        {
            _reader.Position = 0;
        }
    }

    public void Dispose()
    {
        Unload();
    }

    private double RawSeconds()
    {
        var output = _output!;
        return output.GetPosition() / (double)output.OutputWaveFormat.AverageBytesPerSecond;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Solo avisamos del final natural; Stop() libera la salida antes de que llegue este evento.
        if (ReferenceEquals(sender, _output))
        {
            Stop();
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReleaseOutput()
    {
        var output = _output;
        _output = null;
        _paused = false;
        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Dispose();
        }
    }

    private void Unload()
    {
        ReleaseOutput();
        _reader?.Dispose();
        _reader = null;
    }
}
