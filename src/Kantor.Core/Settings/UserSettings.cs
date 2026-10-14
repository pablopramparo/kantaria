// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Text.Json;
using Kantor.Core.Scoring;

namespace Kantor.Core.Settings;

/// <summary>Ajustes del usuario que se conservan entre sesiones.</summary>
public sealed record UserSettings
{
    public const int DefaultVoiceDelayMs = 175;
    public const int MaxVoiceDelayMs = 300;
    public const int VoiceDelayStepMs = 25;

    public Difficulty Difficulty { get; init; } = Difficulty.Easy;

    /// <summary>0 = por beat (UltraStar), 1 = cruda.</summary>
    public int VoiceDisplay { get; init; }

    /// <summary>Retardo de voz en milisegundos, de 0 a 300 en pasos de 25.</summary>
    public int VoiceDelayMs { get; init; } = DefaultVoiceDelayMs;

    /// <summary>Identificador del dispositivo de entrada que estaba abierto, o <c>null</c> si ninguno.</summary>
    public string? InputDeviceId { get; init; }

    public int InputChannel { get; init; }

    /// <summary>
    /// Retardo de voz por combinación salida + entrada + canal (ver <see cref="ProfileKey"/>): lo calibrado o
    /// ajustado con un equipo no se arrastra a otro. <c>null</c> si no hay ninguno.
    /// </summary>
    public Dictionary<string, int>? DelayProfiles { get; init; }

    /// <summary>Clave del perfil: dispositivo de salida predeterminado, dispositivo de entrada y canal.</summary>
    public static string ProfileKey(string outputDeviceId, string inputDeviceId, int channel) =>
        $"{outputDeviceId}|{inputDeviceId}|{channel}";

    /// <summary>Retardo guardado para esa combinación, o <c>null</c> si no hay.</summary>
    public int? DelayFor(string profileKey) =>
        DelayProfiles is { } profiles && profiles.TryGetValue(profileKey, out var ms) ? ms : null;

    /// <summary>Copia con el retardo de esa combinación fijado.</summary>
    public UserSettings WithDelayFor(string profileKey, int delayMs)
    {
        var profiles = DelayProfiles is null ? [] : new Dictionary<string, int>(DelayProfiles);
        profiles[profileKey] = delayMs;
        return this with { DelayProfiles = profiles };
    }

    private static int SnapDelay(int ms)
    {
        var clamped = Math.Clamp(ms, 0, MaxVoiceDelayMs);
        return (int)Math.Round(clamped / (double)VoiceDelayStepMs) * VoiceDelayStepMs;
    }

    /// <summary>Lleva cada valor a uno válido: un archivo editado a mano o de otra versión no debe romper la app.</summary>
    public UserSettings Normalized()
    {
        var profiles = DelayProfiles?
            .Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .ToDictionary(p => p.Key, p => SnapDelay(p.Value));

        return this with
        {
            Difficulty = Enum.IsDefined(Difficulty) ? Difficulty : Difficulty.Easy,
            VoiceDisplay = VoiceDisplay == 1 ? 1 : 0,
            VoiceDelayMs = SnapDelay(VoiceDelayMs),
            InputDeviceId = string.IsNullOrWhiteSpace(InputDeviceId) ? null : InputDeviceId,
            InputChannel = Math.Max(0, InputChannel),
            DelayProfiles = profiles is { Count: > 0 } ? profiles : null,
        };
    }
}

/// <summary>Lee y escribe <see cref="UserSettings"/> en un archivo JSON. Nunca lanza por un archivo ausente o dañado.</summary>
public static class UserSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static UserSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), Options);
                if (loaded is not null)
                {
                    return loaded.Normalized();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Archivo ilegible o con formato inválido: se arranca con los valores por omisión.
        }

        return new UserSettings();
    }

    /// <summary>Guarda de forma atómica (archivo temporal y reemplazo). Devuelve false si no pudo.</summary>
    public static bool Save(string path, UserSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalized(), Options));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Ubicación por omisión: la carpeta de datos de la aplicación del usuario.</summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kantor", "settings.json");
}
