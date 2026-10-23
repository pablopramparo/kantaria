// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.App.Audio;
using Kantor.Core.Audio;

namespace Kantor.App.Options;

/// <summary>Lo que la pantalla de opciones muestra: una foto de los ajustes y del audio en uso.</summary>
public sealed class OptionsModel
{
    public required string Language { get; init; }

    public required IAudioEngine Engine { get; init; }

    /// <summary>Motor que el usuario tiene como preferencia (puede diferir del activo si hubo respaldo).</summary>
    public required AudioEngineKind Requested { get; init; }

    public required AsioAvailability Asio { get; init; }

    public string? PreferredDriver { get; init; }

    /// <summary>Motivo del respaldo cuando el motor pedido no pudo usarse.</summary>
    public string? EngineNote { get; init; }

    public required ActiveInput Input { get; init; }

    /// <summary>Retardo de voz, en milisegundos, del micrófono de cada puesto.</summary>
    public required Func<int, int> DelayMs { get; init; }

    public IReadOnlyList<string> Folders { get; init; } = [];

    public bool Grid { get; init; }

    /// <summary>0 = Fácil, 1 = Medio, 2 = Difícil.</summary>
    public int Difficulty { get; init; }

    /// <summary>0 = por beat, 1 = cruda.</summary>
    public int VoiceDisplay { get; init; }

    /// <summary>Probabilidad (0–1) del comentario disparatado.</summary>
    public double Nonsense { get; init; }

    /// <summary>Vista previa sonora al navegar la lista de canciones.</summary>
    public bool SongPreview { get; init; } = true;

    /// <summary>Valoración breve al cerrar cada frase durante el canto.</summary>
    public bool PhraseFeedback { get; init; } = true;


    public bool StartFullscreen { get; init; }

    public bool TechnicalBar { get; init; }

    /// <summary>Avanza sola la próxima canción de la cola al terminar los resultados.</summary>
    public bool AutoAdvance { get; init; }

    /// <summary>El canal local con el Agent de Avyn está habilitado.</summary>
    public bool RemoteControl { get; init; }

    /// <summary>Redes locales donde se puede ofrecer el acceso de invitados (dirección y nombre).</summary>
    public IReadOnlyList<(string Address, string Label)> GuestNetworks { get; init; } = [];

    /// <summary>Dirección elegida para los invitados (<c>null</c> = automática).</summary>
    public string? GuestInterface { get; init; }

    /// <summary>Volumen de la música de interfaz, de 0 a 100 %.</summary>
    public int MusicVolume { get; init; } = Kantor.Core.Music.MusicVolume.DefaultPercent;

    /// <summary>Texto de estado del canal remoto (<c>null</c> si está apagado).</summary>
    public string? RemoteStatus { get; init; }
}
