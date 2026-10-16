// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Data;

namespace Kantor.Core.Remote;

/// <summary>Pantallas de Kantor tal como las publica el contrato.</summary>
public static class Screens
{
    public const string Menu = "menu";
    public const string Library = "library";
    public const string Queue = "queue";
    public const string Options = "options";
    public const string Players = "players";
    public const string SessionGate = "session-gate";
    public const string WhoPlays = "who-plays";
    public const string WhoSings = "who-sings";
    public const string Loading = "loading";
    public const string Stage = "stage";
    public const string Results = "results";

    /// <summary>Pantallas donde hay una decisión que se toma en la PC: un comando remoto no las pisa.</summary>
    public static bool IsLocalDecision(string screen) =>
        screen is Options or Players or SessionGate or WhoPlays or WhoSings;
}

public static class PlaybackStatuses
{
    public const string Idle = "idle";
    public const string Preparing = "preparing";
    public const string Playing = "playing";
    public const string Paused = "paused";
    public const string Finished = "finished";
}

public enum SessionPolicy
{
    /// <summary>Con la partida abierta.</summary>
    Current,

    /// <summary>Con una partida nueva (solo si no hay otra abierta).</summary>
    New,

    /// <summary>Cantar suelto: el resultado no se guarda.</summary>
    Solo,
}

/// <summary>Quién ocupa un puesto en la selección actual y cuánto lleva.</summary>
public sealed record HostSlot(int Slot, long PlayerId, string Name, bool HasMicrophone, int Score, double Accuracy);

public sealed record HostResultPlayer(int Slot, long? PlayerId, string Name, int Score, string? CommentId);

/// <summary>El último resultado mostrado (guardado o no). <see cref="Seq"/> crece con cada resultado nuevo.</summary>
public sealed record HostResult(
    long Seq,
    string? PerformanceId,
    bool Saved,
    DateTimeOffset FinishedAt,
    string SongPath,
    string Artist,
    string Title,
    int Difficulty,
    IReadOnlyList<HostResultPlayer> Players);

public sealed record HostMonitor(string DeviceName, bool Primary, int X, int Y, int Width, int Height, double DpiScale);

public sealed record HostDisplay(bool Fullscreen, int ProcessId, string WindowHandle, HostMonitor? Monitor);

public sealed record HostAttention(string Code, IReadOnlyDictionary<string, string>? Params = null);

/// <summary>Lo que el contrato publica de la ventana: una lectura de un instante, siempre tomada en el hilo de la interfaz.</summary>
public sealed record HostState
{
    public string Screen { get; init; } = Screens.Menu;

    public string Status { get; init; } = PlaybackStatuses.Idle;

    public double PositionSeconds { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary>Archivo de la canción cargada (<c>null</c> si no hay).</summary>
    public string? LoadedPath { get; init; }

    public string? Artist { get; init; }

    public string? Title { get; init; }

    /// <summary>Identificador de la ejecución en curso (el de la interpretación si se guarda).</summary>
    public string? RunId { get; init; }

    /// <summary>"easy", "medium" o "hard".</summary>
    public string Difficulty { get; init; } = "easy";

    /// <summary>Se canta suelto (sin partida): el resultado no se guarda.</summary>
    public bool Solo { get; init; }

    /// <summary>Puestos (1–4) que tienen un micrófono creado.</summary>
    public IReadOnlyList<int> MicrophoneSlots { get; init; } = [];

    /// <summary>La selección actual (quién ocupa cada puesto) con el puntaje en vivo.</summary>
    public IReadOnlyList<HostSlot> Slots { get; init; } = [];

    public HostResult? LastResult { get; init; }

    public bool ResultsAnimating { get; init; }

    /// <summary>Crece con cada interacción local explícita en resultados (tecla o clic) posterior a la animación.</summary>
    public long ResultsInteractionSeq { get; init; }

    /// <summary>Crece cada vez que una interpretación se abandona (detener, menú, opciones, error de carga…).</summary>
    public long AbandonSeq { get; init; }

    public HostDisplay Display { get; init; } = new(false, 0, "0x0", null);

    public string Language { get; init; } = "es";

    public bool AutoAdvance { get; init; }

    public bool StoreAvailable { get; init; } = true;

    public bool HasLibraryFolders { get; init; } = true;

    public IReadOnlyList<HostAttention> Attention { get; init; } = [];
}

/// <summary>Pedido para empezar una canción desde el control remoto (la interfaz decide cómo mostrarlo).</summary>
public sealed record HostStartRequest(
    string Path,
    string LibraryEntryId,
    string? QueueItemId,
    SessionPolicy Policy,
    IReadOnlyList<SelectionEntry>? Selection,
    bool AutoConfirmSingers);

/// <summary>
/// La ventana de Kantor vista por el control remoto. Todos los métodos se llaman en el hilo de la interfaz. Las
/// acciones lanzan <see cref="RemoteException"/> cuando el estado no las permite (la misma regla que la interfaz).
/// </summary>
public interface IKantorHost
{
    HostState Capture();

    /// <summary>Carga la canción y sigue el flujo local (¿quién canta?). No bloquea: la carga es asíncrona.</summary>
    void StartSong(HostStartRequest request);

    /// <summary>"LISTO" en ¿Quién canta?: empieza la interpretación.</summary>
    void ConfirmSingers();

    void Pause();

    void Resume();

    /// <summary>"Otra vez": detiene sin guardar y vuelve a empezar con los mismos jugadores.</summary>
    void Restart();

    /// <summary>Detiene lo que suena (o cancela la carga/decisión): no se guarda nada.</summary>
    void Stop();

    void ShowMenu();

    void SkipResultsAnimation();

    void CloseResults();

    void SetFullscreen(bool enabled);

    void SetAutoAdvance(bool enabled);

    /// <summary>Cambia quién ocupa cada puesto (con partida, queda guardado en ella).</summary>
    void ApplySelection(IReadOnlyList<SelectionEntry> selection);

    /// <summary>La partida o los jugadores cambiaron por el control remoto: refrescar cachés y pantalla.</summary>
    void DataChanged();

    /// <summary>
    /// Se agregó un jugador (desde el celular de un invitado): refrescar los nombres y la pantalla de jugadores sin
    /// tocar la canción en curso, el puntaje ni la selección (a diferencia de <see cref="DataChanged"/>).
    /// </summary>
    void PlayersAdded();
}

/// <summary>Miniatura de una portada para enviarla al Agent (la decodificación la hace la aplicación).</summary>
public interface ICoverProvider
{
    /// <summary>JPEG con el borde mayor de <paramref name="size"/> como máximo y como mucho <see cref="Protocol.CoverMaxBytes"/>.</summary>
    CoverImage? GetThumbnail(string coverPath, int size);
}

public sealed record CoverImage(byte[] Jpeg, int Width, int Height);
