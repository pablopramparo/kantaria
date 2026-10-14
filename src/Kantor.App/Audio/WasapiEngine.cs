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

    public void Dispose()
    {
    }
}

public static class AudioEngineFactory
{
    /// <summary>
    /// Crea el motor pedido, o el de Windows si ese no está disponible. <paramref name="note"/> explica el
    /// respaldo cuando lo hubo.
    /// </summary>
    public static IAudioEngine Create(AudioEngineKind requested, out string? note)
    {
        // ASIO todavía no está implementado: se pide por si en la configuración ya figura.
        var resolution = AudioEngineChoice.Resolve(requested, asioAvailable: false,
            asioUnavailableReason: "El motor ASIO todavía no está implementado; se usa el de Windows.");
        note = resolution.FallbackReason;
        return new WasapiEngine();
    }
}
