// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Reflection;
using Kantor.Core.Timing;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kantor.App.Audio;

/// <summary>
/// Reproduce el audio de la canción por WASAPI y expone su posición como <see cref="ISongClock"/> y
/// como <see cref="ISongTimeMap"/>. La posición sale del reloj del dispositivo (IAudioClock), que
/// informa cada lectura junto con el QPC en que se tomó; así un instante de la captura puede ubicarse
/// en la canción sin cronómetros intermedios. No incluye la latencia del hardware de salida.
/// </summary>
public sealed class SongPlayer : ISongClock, ISongTimeMap, IDisposable
{
    private const int OutputLatencyMilliseconds = 100;

    // WasapiOut no expone su AudioClient; la versión de NAudio está fijada y el campo se verifica al usarlo.
    private static readonly FieldInfo AudioClientField =
        typeof(WasapiOut).GetField("audioClient", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException("NAudio.Wave.WasapiOut.audioClient no existe en esta versión de NAudio.");

    private readonly object _sync = new();
    private readonly PlaybackClockModel _model = new();

    private AudioFileReader? _reader;
    private WasapiOut? _output;
    private AudioClockClient? _deviceClock;

    public event EventHandler? Ended;

    public bool IsLoaded => _reader is not null;

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
            {
                return _output is { PlaybackState: PlaybackState.Playing };
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _model.IsPaused;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_sync)
            {
                return TryReadDeviceClock(out var raw, out _) ? _model.Position(raw) : TimeSpan.Zero;
            }
        }
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public TimeSpan? SongPositionAt(long hostTicks)
    {
        lock (_sync)
        {
            return TryReadDeviceClock(out var raw, out var observedHost)
                ? _model.PositionAt(raw, observedHost, hostTicks)
                : null;
        }
    }

    public void Load(string audioPath)
    {
        lock (_sync)
        {
            Unload();
            _reader = new AudioFileReader(audioPath);
        }
    }

    public void Play()
    {
        lock (_sync)
        {
            if (_reader is null)
            {
                return;
            }

            if (_output is null)
            {
                _reader.Position = 0;
                _output = new WasapiOut(AudioClientShareMode.Shared, OutputLatencyMilliseconds);
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(_reader);
                _deviceClock = ((AudioClient)AudioClientField.GetValue(_output)!).AudioClockClient;
                _model.Start();
            }
            else if (_model.IsPaused)
            {
                _model.Resume();
            }

            _output.Play();
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_output is not { PlaybackState: PlaybackState.Playing } || _model.IsPaused)
            {
                return;
            }

            _model.Pause();
            _output.Pause();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            ReleaseOutput();
            if (_reader is not null)
            {
                _reader.Position = 0;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            Unload();
        }
    }

    /// <summary>Lee el reloj del dispositivo: segundos desde el inicio del flujo y el QPC (100 ns) de esa lectura.</summary>
    private bool TryReadDeviceClock(out double rawSeconds, out long hostTicks)
    {
        rawSeconds = 0;
        hostTicks = 0;
        if (_deviceClock is null || _output is null)
        {
            return false;
        }

        if (!_deviceClock.GetPosition(out var position, out var qpc))
        {
            return false;
        }

        rawSeconds = position / (double)_deviceClock.Frequency;
        hostTicks = (long)qpc;
        return true;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        bool natural;
        lock (_sync)
        {
            natural = ReferenceEquals(sender, _output);
            if (natural)
            {
                ReleaseOutput();
                _reader!.Position = 0;
            }
        }

        // Solo avisamos del final natural; Stop() libera la salida antes de que llegue este evento.
        if (natural)
        {
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReleaseOutput()
    {
        var output = _output;
        _output = null;
        _deviceClock = null;
        _model.Stop();
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
