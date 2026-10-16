// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using Kantor.Core.Music;
using NAudio;
using NAudio.Wave;

namespace Kantor.App.Audio;

/// <summary>
/// Salida de la música de la interfaz: una pista en loop por salida (<c>WaveOutEvent</c> sobre el dispositivo
/// predeterminado, como la vista previa de canciones). Es independiente del motor de la canción (WASAPI o ASIO), de la
/// captura de micrófonos y del puntaje: no comparte reloj, hilo ni dispositivo con ellos. La lógica de qué suena y cuándo
/// está en <see cref="MusicCoordinator"/>.
/// </summary>
internal sealed class UiMusicOutput : IMusicOutput
{
    /// <summary>"8Bit Title Screen": MP3 con 967 680 cuadros válidos (el resto es relleno del codificador; ver Assets/Music/README.md).</summary>
    private const int TitleLoopFrames = 967_680;

    private readonly Dictionary<MusicTrack, FileLoopChannel> _channels;

    public UiMusicOutput(string directory)
    {
        _channels = new Dictionary<MusicTrack, FileLoopChannel>
        {
            [MusicTrack.Menu] = new(Path.Combine(directory, "8bit-title-screen.mp3"), TitleLoopFrames),
            [MusicTrack.Results] = new(Path.Combine(directory, "heroic-loop.wav"), loopFrames: null),
        };
    }

    public IMusicChannel Channel(MusicTrack track) => _channels[track];

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }
    }
}

/// <summary>
/// Un archivo de audio decodificado a memoria (16 bits) que suena en loop sin cortes. Abrir y decodificar se hace en
/// segundo plano la primera vez; la salida se abre y se cierra a pedido (al cerrarla se conserva la posición del loop).
/// </summary>
internal sealed class FileLoopChannel : IMusicChannel
{
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(2);

    private readonly string _path;
    private readonly int? _loopFrames;
    private readonly object _gate = new();
    private readonly bool _exists;
    private LoopProvider? _provider;
    private WaveOutEvent? _output;
    private bool _wanted;
    private bool _decoding;
    private bool _unavailable;
    private bool _disposed;
    private long _noRetryUntil;

    public FileLoopChannel(string path, int? loopFrames)
    {
        _path = path;
        _loopFrames = loopFrames;
        _exists = File.Exists(path);
    }

    public bool IsAvailable => _exists && !_unavailable;

    /// <summary>Hay una salida abierta o a punto de abrirse (todavía decodificando).</summary>
    public bool IsPlaying => _wanted;

    public bool Start(bool fromBeginning)
    {
        lock (_gate)
        {
            if (_disposed || _unavailable || !_exists)
            {
                return false;
            }

            if (_wanted)
            {
                return true;
            }

            if (Environment.TickCount64 < _noRetryUntil)
            {
                return false;
            }

            if (fromBeginning)
            {
                if (_provider is null)
                {
                    _rewindOnLoad = true; // todavía decodificando: se rebobina al terminar
                }
                else
                {
                    _provider.Rewind();
                }
            }

            _wanted = true;
            if (_provider is null)
            {
                BeginDecode();
                return true;
            }

            return OpenOutput();
        }
    }

    private bool _rewindOnLoad;

    public void SetGain(float gain)
    {
        _provider?.SetTarget(gain);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _wanted = false;
            CloseOutput();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _wanted = false;
            CloseOutput();
            _provider = null;
        }
    }

    private void BeginDecode()
    {
        if (_decoding)
        {
            return;
        }

        _decoding = true;
        Task.Run(() =>
        {
            LoopProvider? loaded = null;
            try
            {
                loaded = Decode();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                          or System.Runtime.InteropServices.COMException or MmException
                                          or ArgumentException or NotSupportedException or FormatException)
            {
                // un archivo ilegible no tumba nada: esa pista queda sin sonar
                Diag.Mark($"música: no se pudo decodificar {Path.GetFileName(_path)}: {e.Message}");
            }

            lock (_gate)
            {
                _decoding = false;
                if (loaded is null)
                {
                    _unavailable = true;
                    _wanted = false;
                    return;
                }

                _provider = loaded;
                if (_rewindOnLoad)
                {
                    _provider.Rewind();
                    _rewindOnLoad = false;
                }

                if (_wanted && !_disposed)
                {
                    OpenOutput();
                }
            }
        });
    }

    private LoopProvider Decode()
    {
        using var reader = new AudioFileReader(_path);
        var channels = reader.WaveFormat.Channels;
        var limit = _loopFrames is { } frames ? (long)frames * channels : long.MaxValue;
        var pcm = new List<short[]>();
        var total = 0L;
        var buffer = new float[channels * 8192];
        int read;
        while (total < limit && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            read = (int)Math.Min(read, limit - total);
            var block = new short[read];
            for (var i = 0; i < read; i++)
            {
                block[i] = (short)Math.Clamp((int)Math.Round(buffer[i] * 32767f), short.MinValue, short.MaxValue);
            }

            pcm.Add(block);
            total += read;
        }

        if (total < channels)
        {
            throw new InvalidOperationException("El archivo de música está vacío.");
        }

        var all = new short[total];
        var at = 0;
        foreach (var block in pcm)
        {
            Array.Copy(block, 0, all, at, block.Length);
            at += block.Length;
        }

        return new LoopProvider(all, WaveFormat.CreateIeeeFloatWaveFormat(reader.WaveFormat.SampleRate, channels));
    }

    private bool OpenOutput()
    {
        try
        {
            var provider = _provider!;
            provider.Silence();
            var output = new WaveOutEvent { DesiredLatency = 150 };
            Diag.Mark($"música: abriendo salida de {Path.GetFileName(_path)}");
            output.Init(provider);
            output.PlaybackStopped += OnPlaybackStopped;
            _output = output;
            output.Play();
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException
                                      or MmException or ArgumentException)
        {
            Diag.Mark($"música: no se pudo abrir la salida: {e.Message}");
            _wanted = false;
            CloseOutput();
            _noRetryUntil = Environment.TickCount64 + (long)Backoff.TotalMilliseconds;
            return false;
        }
    }

    private void CloseOutput()
    {
        var output = _output;
        _output = null;
        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                output.Stop();
            }
            catch (Exception e) when (e is InvalidOperationException or MmException)
            {
                // la salida ya estaba rota (dispositivo desconectado): se libera igual
            }

            output.Dispose();
        }
    }

    /// <summary>La salida se cortó sola (por ejemplo, se desconectó el dispositivo): se libera y se reintenta más tarde.</summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(sender, _output))
            {
                return;
            }

            _wanted = false;
            CloseOutput();
            _noRetryUntil = Environment.TickCount64 + (long)Backoff.TotalMilliseconds;
        }
    }
}

/// <summary>Reproduce un bloque de PCM en loop, con volumen que cambia suavemente dentro de cada lectura.</summary>
internal sealed class LoopProvider(short[] pcm, WaveFormat format) : ISampleProvider
{
    private readonly int _channels = format.Channels;
    private long _position; // en muestras
    private float _current;
    private volatile float _target;

    public WaveFormat WaveFormat { get; } = format;

    public void SetTarget(float gain) => _target = Math.Clamp(gain, 0f, 1f);

    /// <summary>Vuelve a empezar desde el principio de la pista (solo con la salida cerrada).</summary>
    public void Rewind() => _position = 0;

    /// <summary>Parte del silencio: el fundido de entrada lo marca el coordinador con <see cref="SetTarget"/>.</summary>
    public void Silence() => _current = 0;

    public int Read(float[] buffer, int offset, int count)
    {
        var target = _target;
        var step = (target - _current) / Math.Max(1, count / _channels);
        var gain = _current;
        for (var j = 0; j < count; j++)
        {
            if (j % _channels == 0)
            {
                gain += step;
            }

            buffer[offset + j] = pcm[_position] / 32768f * gain;
            if (++_position >= pcm.Length)
            {
                _position = 0; // vuelve al principio sin silencio entre vueltas
            }
        }

        _current = target;
        return count;
    }
}
