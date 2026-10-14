// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kantor.App.Audio;

/// <summary>Un endpoint de captura de Windows y los canales que expone en modo compartido.</summary>
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
}

public static class AudioInputCatalog
{
    /// <summary>
    /// Lista los endpoints de captura activos. Los canales son los que Windows expone en modo compartido
    /// (el formato configurado en el panel de sonido); una interfaz multicanal puede aparecer como
    /// varios endpoints estéreo o como uno solo con todos sus canales, según su controlador.
    /// </summary>
    public static IReadOnlyList<AudioInputDevice> List()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            defaultId = def.ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // No hay dispositivo de captura predeterminado.
        }

        var result = new List<AudioInputDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            try
            {
                var format = ToSampleFormat(device.AudioClient.MixFormat);
                if (format is null)
                {
                    continue;
                }

                var mix = device.AudioClient.MixFormat;
                result.Add(new AudioInputDevice(device.ID, device.FriendlyName, mix.Channels, mix.SampleRate,
                    format.Value, device.ID == defaultId));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // El dispositivo desapareció o no entrega formato; no se ofrece.
            }
            finally
            {
                device.Dispose();
            }
        }

        return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static SampleFormat? ToSampleFormat(WaveFormat format)
    {
        // WASAPI entrega WAVE_FORMAT_EXTENSIBLE; ToStandardWaveFormat resuelve el subtipo real (PCM o float).
        var standard = format is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : format;

        return (standard.Encoding, standard.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => SampleFormat.Float32,
            (WaveFormatEncoding.Pcm, 16) => SampleFormat.Int16,
            (WaveFormatEncoding.Pcm, 24) => SampleFormat.Int24,
            (WaveFormatEncoding.Pcm, 32) => SampleFormat.Int32,
            _ => null,
        };
    }
}
