// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Pitch;
using Kantor.Core.Timing;

namespace Kantor.Core.Audio;

/// <summary>Con qué se maneja el audio: el mezclador de Windows (compartido) o un controlador ASIO.</summary>
public enum AudioEngineKind
{
    Windows = 0,
    Asio = 1,
}

/// <summary>Una entrada de captura y los canales que expone con el motor elegido.</summary>
public sealed record AudioInputDevice(
    string Id,
    string Name,
    int Channels,
    int SampleRate,
    SampleFormat Format,
    bool IsDefault)
{
    public string Label => Channels == 1
        ? $"{Name} — 1 canal, {SampleRate / 1000.0:0.#} kHz"
        : $"{Name} — {Channels} canales, {SampleRate / 1000.0:0.#} kHz";

    /// <summary>Nombre de cada canal según el controlador (por ejemplo "Input 1", "Loop 1"), si lo informa.</summary>
    public IReadOnlyList<string>? ChannelNames { get; init; }

    public string ChannelLabel(int channel) =>
        ChannelNames is { } names && channel >= 0 && channel < names.Count && !string.IsNullOrWhiteSpace(names[channel])
            ? $"Canal {channel + 1} · {names[channel]}"
            : $"Canal {channel + 1}";
}

/// <summary>Última lectura de un canal: nivel del último bloque y último análisis de tono, si el canal se analiza.</summary>
public readonly record struct ChannelReading(LevelReading Level, PitchFrame? Pitch);

/// <summary>
/// Reproduce la canción y expone su posición. Es lo único que el resto de la aplicación necesita de la
/// salida de audio: la posición como reloj y la conversión de un instante de la base de tiempo común
/// (<see cref="HostClock"/>) a posición de la canción.
/// </summary>
public interface ISongPlayer : ISongClock, ISongTimeMap, IDisposable
{
    /// <summary>Se produce al terminar la canción por sí sola (no al detenerla).</summary>
    event EventHandler? Ended;

    bool IsLoaded { get; }

    bool IsPaused { get; }

    TimeSpan Duration { get; }

    void Load(string audioPath);

    void Play();

    void Pause();

    void Stop();
}

/// <summary>
/// Una entrada abierta: mide el nivel de cada canal y analiza el tono de los canales elegidos, ubicando cada
/// análisis en la base de tiempo común.
/// </summary>
public interface ICaptureSession : IDisposable
{
    AudioInputDevice Device { get; }

    bool IsRunning { get; }

    /// <summary>Paquetes marcados con un hueco en el flujo desde que arrancó (diagnóstico).</summary>
    int Discontinuities { get; }

    /// <summary>Se produce al detenerse la captura por un fallo (por ejemplo, desconexión).</summary>
    event EventHandler<Exception?>? Stopped;

    /// <summary>Un análisis de tono terminó. Se invoca en el hilo de captura; el manejador debe ser breve.</summary>
    event Action<int, PitchFrame>? PitchFrameReady;

    void Start();

    /// <summary>Define qué canales se analizan para tono; el resto solo mide nivel.</summary>
    void SetPitchChannels(IEnumerable<int> channels);

    ChannelReading Read(int channel);
}

/// <summary>
/// Un motor de audio: de dónde salen la reproducción y la captura. El resto de la aplicación no sabe si es
/// el mezclador de Windows o un controlador ASIO.
/// </summary>
public interface IAudioEngine : IDisposable
{
    AudioEngineKind Kind { get; }

    /// <summary>Nombre para mostrar, por ejemplo "Windows (compartido)".</summary>
    string Name { get; }

    ISongPlayer CreatePlayer();

    /// <summary>Entradas que ofrece este motor.</summary>
    IReadOnlyList<AudioInputDevice> ListInputs();

    ICaptureSession CreateCapture(AudioInputDevice device);

    /// <summary>
    /// Pares de salida elegibles ("Output 1 + Output 2", …). Vacío si el motor usa la salida predeterminada de
    /// Windows y no deja elegir.
    /// </summary>
    IReadOnlyList<string> OutputPairs { get; }

    /// <summary>Par de salida en uso (índice en <see cref="OutputPairs"/>); sin sentido si no hay pares.</summary>
    int OutputPair { get; }

    /// <summary>El motor dejó de funcionar (por ejemplo, el controlador pidió reiniciarse). El argumento explica por qué.</summary>
    event EventHandler<string>? Failed;

    /// <summary>
    /// Mide el retardo de voz que necesita este motor con esta entrada y canal, con la reproducción de este
    /// mismo motor. Bloquea unos segundos: llamar desde un hilo de trabajo.
    /// </summary>
    Calibration.CalibrationResult Calibrate(AudioInputDevice input, int channel);
}

/// <summary>Decisión de qué motor usar dada la preferencia del usuario y lo que hay disponible.</summary>
public static class AudioEngineChoice
{
    public readonly record struct Resolution(AudioEngineKind Kind, string? FallbackReason);

    /// <summary>
    /// El motor Windows siempre está disponible y es el respaldo: si se pidió ASIO y no hay (sin controlador,
    /// o todavía no implementado), se usa Windows y se explica por qué.
    /// </summary>
    public static Resolution Resolve(AudioEngineKind requested, bool asioAvailable, string? asioUnavailableReason = null)
    {
        if (requested == AudioEngineKind.Asio && !asioAvailable)
        {
            return new Resolution(AudioEngineKind.Windows,
                asioUnavailableReason ?? "ASIO no está disponible en este equipo.");
        }

        return new Resolution(requested, null);
    }
}
