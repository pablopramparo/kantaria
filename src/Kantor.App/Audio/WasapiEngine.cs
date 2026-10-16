// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Audio;

namespace Kantor.App.Audio;

/// <summary>
/// El motor de Windows: reproducción y captura por WASAPI en modo compartido, con el dispositivo de salida
/// predeterminado y los endpoints de entrada que Windows expone. Funciona con cualquier placa y es el
/// respaldo de los demás motores.
/// </summary>
public sealed class WasapiEngine : IAudioEngine
{
    public AudioEngineKind Kind => AudioEngineKind.Windows;

    public string Name => "Windows (compartido)";

    public ISongPlayer CreatePlayer() => new SongPlayer();

    public IReadOnlyList<AudioInputDevice> ListInputs() => AudioInputCatalog.List();

    public ICaptureSession CreateCapture(AudioInputDevice device) => new CaptureSession(device);

    /// <summary>La salida es la predeterminada de Windows: no hay pares que elegir.</summary>
    public IReadOnlyList<string> OutputPairs => [];

    public int OutputPair => 0;

    public event EventHandler<string>? Failed
    {
        add { }
        remove { }
    }

    public Core.Calibration.CalibrationResult Calibrate(AudioInputDevice input, int channel) =>
        CalibrationRunner.Run(input, channel);

    public void Dispose()
    {
    }
}

/// <summary>Qué motor hay disponible y por qué, para ofrecerlo en la configuración.</summary>
public sealed record AsioAvailability(IReadOnlyList<string> Drivers, string? UnavailableReason)
{
    public bool IsAvailable => Drivers.Count > 0;

    public static AsioAvailability Detect()
    {
        var drivers = AsioEngine.InstalledDrivers();
        return drivers.Length > 0
            ? new AsioAvailability(drivers, null)
            : new AsioAvailability([],
                "No se encontró ningún controlador ASIO instalado. Con la interfaz de audio suele venir con su " +
                "programa de instalación (por ejemplo, Focusrite Control).");
    }
}

public static class AudioEngineFactory
{
    /// <summary>
    /// Crea el motor pedido, o el de Windows si ese no está disponible o falla al iniciar. <paramref name="note"/>
    /// explica el respaldo cuando lo hubo. Debe llamarse desde el hilo de interfaz (STA): ASIO lo exige.
    /// </summary>
    public static IAudioEngine Create(AudioEngineKind requested, string? asioDriver, int asioOutputPair, out string? note)
    {
        var availability = requested == AudioEngineKind.Asio ? AsioAvailability.Detect() : null;
        var resolution = AudioEngineChoice.Resolve(requested, availability?.IsAvailable ?? false, availability?.UnavailableReason);
        note = resolution.FallbackReason;
        if (resolution.Kind != AudioEngineKind.Asio || availability is null)
        {
            return new WasapiEngine();
        }

        var driver = availability.Drivers.FirstOrDefault(d => string.Equals(d, asioDriver, StringComparison.OrdinalIgnoreCase))
                     ?? availability.Drivers[0];
        try
        {
            return new AsioEngine(driver, asioOutputPair);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException
                                      or NotSupportedException or ArgumentException or UnauthorizedAccessException)
        {
            note = $"No se pudo iniciar ASIO ({driver}): {e.Message} Se usa Windows (compartido).";
            return new WasapiEngine();
        }
    }
}
