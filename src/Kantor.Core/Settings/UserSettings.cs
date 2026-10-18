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

    /// <summary>
    /// Motor de audio preferido (configuración global). Windows es el predeterminado y el respaldo: si el
    /// pedido no está disponible, la aplicación usa Windows y lo avisa.
    /// </summary>
    public Audio.AudioEngineKind AudioEngine { get; init; } = Audio.AudioEngineKind.Windows;

    /// <summary>Controlador ASIO elegido (nombre), o <c>null</c> para el primero disponible.</summary>
    public string? AsioDriver { get; init; }

    /// <summary>Par de salida elegido con ASIO (0 = salidas 1+2, 1 = 3+4, …).</summary>
    public int AsioOutputPair { get; init; }

    /// <summary>Retardo de voz con el motor de Windows, en milisegundos, de 0 a 300 en pasos de 25.</summary>
    public int VoiceDelayMs { get; init; } = DefaultVoiceDelayMs;

    /// <summary>
    /// Retardo de voz con ASIO. No se reutiliza el de Windows: la latencia es otra y se calibra aparte. Hasta
    /// calibrar, 0.
    /// </summary>
    public int AsioVoiceDelayMs { get; init; }

    /// <summary>
    /// Entrada única de versiones anteriores. Solo se lee para migrar: si <see cref="Microphones"/> no existe,
    /// equivale a un micrófono en el puesto 0 (ver <see cref="EffectiveMicrophones"/>).
    /// </summary>
    public string? InputDeviceId { get; init; }

    public int InputChannel { get; init; }

    /// <summary>
    /// Micrófonos abiertos: puesto, dispositivo y canal. <c>null</c> = nunca se guardaron (se usa la entrada
    /// única anterior); una lista vacía significa que el usuario no quiere ninguno.
    /// </summary>
    public List<MicrophoneSetting>? Microphones { get; init; }

    /// <summary>Los micrófonos a abrir al arrancar, migrando la entrada única de versiones anteriores.</summary>
    public IReadOnlyList<MicrophoneSetting> EffectiveMicrophones() =>
        Microphones is not null
            ? Microphones
            : InputDeviceId is { } id ? [new MicrophoneSetting(0, id, InputChannel)] : [];

    /// <summary>
    /// Retardo de voz por combinación salida + entrada + canal (ver <see cref="ProfileKey"/>): lo calibrado o
    /// ajustado con un equipo no se arrastra a otro. <c>null</c> si no hay ninguno.
    /// </summary>
    public Dictionary<string, int>? DelayProfiles { get; init; }

    /// <summary>Carpetas donde se buscan canciones UltraStar (recursivamente). <c>null</c> o vacío = ninguna.</summary>
    public List<string>? LibraryPaths { get; init; }

    /// <summary>La lista de canciones se muestra como grilla (<c>true</c>) o como lista con detalle.</summary>
    public bool LibraryGrid { get; init; }

    /// <summary>Al navegar la lista de canciones suena una vista previa, a volumen bajo.</summary>
    public bool SongPreview { get; init; } = true;

    /// <summary>Durante el canto, al cerrar cada frase se muestra una valoración breve (PERFECTO, UPS…).</summary>
    public bool PhraseFeedback { get; init; } = true;

    /// <summary>Los mensajes por frase en español usan la variante argentina (<c>true</c>) o la neutra (<c>false</c>).</summary>
    public bool ArgentinePhrases { get; init; } = true;

    /// <summary>La aplicación arranca en pantalla completa.</summary>
    public bool StartFullscreen { get; init; }

    /// <summary>
    /// Control remoto (Avyn) habilitado: Kantor abre el pipe local <c>Kantor.ControlPlane.v1</c>. Apagado por
    /// omisión: no se abre nada sin que el usuario lo quiera.
    /// </summary>
    public bool RemoteControl { get; init; }

    /// <summary>
    /// Volumen de la música de la interfaz (inicio, navegación y resultados), de 0 a 100 %. 0 la apaga. No afecta a las
    /// canciones, a los micrófonos ni al volumen del dispositivo.
    /// </summary>
    public int MusicVolumePercent { get; init; } = Music.MusicVolume.DefaultPercent;

    /// <summary>
    /// Dirección IPv4 de la red local donde se ofrece el acceso de invitados (<c>null</c> = elegir sola). El acceso
    /// en sí nunca se guarda: arranca apagado cada vez.
    /// </summary>
    public string? GuestInterface { get; init; }

    /// <summary>
    /// Al terminar los resultados de una canción, empieza sola la próxima de la cola (10 s después de la
    /// animación, cancelable), solo con jugadores válidos. Apagado por omisión.
    /// </summary>
    public bool AutoAdvance { get; init; }

    /// <summary>Idioma de la interfaz (menú, resultados y comentarios): "es" o "en".</summary>
    public string CommentLanguage { get; init; } = Humor.HumorBank.DefaultLanguage;

    /// <summary>Probabilidad (0–1) del comentario sin sentido en resultados de 0 a 9999.</summary>
    public double NonsenseProbability { get; init; } = Humor.CommentSelector.DefaultNonsenseProbability;

    /// <summary>Clave del perfil: dispositivo de salida predeterminado, dispositivo de entrada y canal.</summary>
    public static string ProfileKey(string outputDeviceId, string inputDeviceId, int channel) =>
        $"{outputDeviceId}|{inputDeviceId}|{channel}";

    /// <summary>
    /// Clave del perfil con ASIO: controlador, par de salida y canal de entrada. No coincide con ninguna clave
    /// de Windows, así que las calibraciones de un motor nunca se aplican al otro.
    /// </summary>
    public static string AsioProfileKey(string driver, int outputPair, int channel) =>
        $"asio|{driver}|out{outputPair}|in{channel}";

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

    /// <summary>Descarta puestos fuera de rango, repetidos o sin dispositivo, y canales repetidos.</summary>
    private static List<MicrophoneSetting>? NormalizedMicrophones(List<MicrophoneSetting>? list)
    {
        if (list is null)
        {
            return null;
        }

        var result = new List<MicrophoneSetting>();
        foreach (var m in list.Where(m => m is not null && !string.IsNullOrWhiteSpace(m.DeviceId)
                                          && m.Slot >= 0 && m.Slot < Audio.MicrophoneMap.MaxSlots))
        {
            var fixedChannel = m with { Channel = Math.Max(0, m.Channel) };
            if (result.All(r => r.Slot != fixedChannel.Slot
                                && !(r.DeviceId == fixedChannel.DeviceId && r.Channel == fixedChannel.Channel)))
            {
                result.Add(fixedChannel);
            }
        }

        return [.. result.OrderBy(r => r.Slot)];
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
            AudioEngine = Enum.IsDefined(AudioEngine) ? AudioEngine : Audio.AudioEngineKind.Windows,
            VoiceDisplay = VoiceDisplay == 1 ? 1 : 0,
            MusicVolumePercent = Music.MusicVolume.Clamp(MusicVolumePercent),
            VoiceDelayMs = SnapDelay(VoiceDelayMs),
            AsioVoiceDelayMs = SnapDelay(AsioVoiceDelayMs),
            AsioDriver = string.IsNullOrWhiteSpace(AsioDriver) ? null : AsioDriver,
            AsioOutputPair = Math.Max(0, AsioOutputPair),
            InputDeviceId = string.IsNullOrWhiteSpace(InputDeviceId) ? null : InputDeviceId,
            InputChannel = Math.Max(0, InputChannel),
            Microphones = NormalizedMicrophones(Microphones),
            DelayProfiles = profiles is { Count: > 0 } ? profiles : null,
            LibraryPaths = LibraryPaths?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() is { Count: > 0 } paths ? paths : null,
            CommentLanguage = Humor.HumorBank.NormalizeLanguage(CommentLanguage),
            NonsenseProbability = double.IsFinite(NonsenseProbability)
                ? Math.Clamp(NonsenseProbability, 0.0, 1.0)
                : Humor.CommentSelector.DefaultNonsenseProbability,
        };
    }
}

/// <summary>Un micrófono guardado: el puesto (jugador) que alimenta y de dónde sale su señal.</summary>
public sealed record MicrophoneSetting(int Slot, string DeviceId, int Channel);

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

    /// <summary>Ubicación por omisión: la carpeta de datos de la aplicación (ver <see cref="AppPaths"/>).</summary>
    public static string DefaultPath() => Path.Combine(AppPaths.DataDirectory(), "settings.json");
}
