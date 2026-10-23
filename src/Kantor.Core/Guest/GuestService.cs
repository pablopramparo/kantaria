// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Characters;
using Kantor.Core.Library;
using Kantor.Core.Remote;

namespace Kantor.Core.Guest;

/// <summary>Una solicitud de invitado que no se puede atender. <see cref="Code"/> es estable; el texto lo pone la página.</summary>
public sealed class GuestException(string code, int status) : Exception(code)
{
    public string Code { get; } = code;

    public int Status { get; } = status;
}

/// <summary>Códigos de error que ve la página de invitados (nunca detalles internos).</summary>
public static class GuestErrors
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string SongNotFound = "SONG_NOT_FOUND";
    public const string PlayerNotFound = "PLAYER_NOT_FOUND";
    public const string PlayerNotAvailable = "PLAYER_NOT_AVAILABLE";
    public const string NoMicrophones = "NO_MICROPHONES";
    public const string TooManySingers = "TOO_MANY_SINGERS";
    public const string QueueFull = "QUEUE_FULL";
    public const string RequestReused = "REQUEST_REUSED";
    public const string Unavailable = "UNAVAILABLE";
    public const string NameEmpty = "NAME_EMPTY";
    public const string NameTooLong = "NAME_TOO_LONG";
    public const string NameInvalid = "NAME_INVALID";
    public const string NameTaken = "NAME_TAKEN";
    public const string InvalidAvatar = "INVALID_AVATAR";
}

public sealed record GuestSong(string Id, string Artist, string Title, string? Edition, bool HasCover, string? CoverId,
    bool HasVideo);

public sealed record GuestSongPage(int Total, int Offset, int? NextOffset, IReadOnlyList<GuestSong> Items);

/// <summary>Jugador recién creado. <see cref="Duplicate"/> si era el reintento de una creación ya hecha.</summary>
public sealed record GuestCreatedPlayer(long Id, string Name, string Avatar, bool Duplicate);

/// <summary>Resultado de agregar: <see cref="Duplicate"/> si era un reintento o un doble toque de algo ya agregado.</summary>
public sealed record GuestEnqueueResult(int Position, int Pending, bool Duplicate);

/// <summary>Las imágenes de los jugadores (retrato) en un formato liviano para el celular.</summary>
public interface IGuestAvatars
{
    /// <summary>PNG del retrato del personaje, o <c>null</c> si no hay.</summary>
    byte[]? GetPortraitPng(string characterId);
}

/// <summary>
/// Lo que un invitado puede hacer: buscar en la biblioteca, ver los jugadores elegibles y agregar una canción a la
/// cola. Todo pasa por la biblioteca, el almacén y la cola que ya usa Kantaria (no hay copias): la búsqueda lee la
/// foto inmutable del catálogo y el agregado corre en el hilo de la interfaz por
/// <see cref="KantorController.GuestEnqueue"/>, igual que cualquier otra mutación.
/// </summary>
public sealed class GuestService
{
    public const int MaxPageSize = 40;
    public const int MaxQueryChars = 100;
    public static readonly TimeSpan RequestMemory = TimeSpan.FromMinutes(10);

    private const int CoverSize = 96;
    private const int MaxRemembered = 512;

    private readonly LibraryCatalog _catalog;
    private readonly KantorController _controller;
    private readonly IUiInvoker _ui;
    private readonly ICoverProvider? _covers;
    private readonly IGuestAvatars? _avatars;
    private readonly Func<string> _language;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Remembered> _byRequest = new(StringComparer.Ordinal);

    private sealed record Remembered(string Fingerprint, Task Task, DateTimeOffset At);

    public GuestService(LibraryCatalog catalog, KantorController controller, IUiInvoker ui, ICoverProvider? covers,
        IGuestAvatars? avatars, Func<string> language, Func<DateTimeOffset>? clock = null)
    {
        _catalog = catalog;
        _controller = controller;
        _ui = ui;
        _covers = covers;
        _avatars = avatars;
        _language = language;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Idioma de Kantaria ("es", "es-AR" o "en"): el valor inicial de la página.</summary>
    public string Language => Localization.AppLanguage.Normalize(_language());

    // ---- Biblioteca -------------------------------------------------------------------------------------------

    /// <summary>
    /// Búsqueda con las mismas reglas que la lista de la PC (todas las palabras, sin importar mayúsculas ni acentos;
    /// primero las que empiezan con la consulta). Paginada: nunca devuelve toda la biblioteca.
    /// </summary>
    public GuestSongPage Search(string? query, int offset, int limit)
    {
        if (query is { Length: > MaxQueryChars } || offset < 0 || limit is < 1 or > MaxPageSize)
        {
            throw new GuestException(GuestErrors.InvalidRequest, 400);
        }

        var found = LibrarySearch.Run(_catalog.Current, new LibraryQuery(query));
        var page = found.Skip(offset).Take(limit)
            .Select(i => new GuestSong(i.EntryId, i.Entry.Artist, i.Entry.Title, i.Entry.Edition, i.CoverId is not null,
                i.CoverId, i.Entry.HasVideo))
            .ToList();
        var end = offset + page.Count;
        return new GuestSongPage(found.Count, offset, end < found.Count ? end : null, page);
    }

    /// <summary>Miniatura de la portada de una entrada (por identificador, nunca por ruta), o <c>null</c>.</summary>
    public byte[]? Cover(string entryId)
    {
        if (_covers is null || _catalog.Current.Find(entryId)?.Entry.CoverPath is not { } path)
        {
            return null;
        }

        return _covers.GetThumbnail(path, CoverSize)?.Jpeg;
    }

    // ---- Jugadores --------------------------------------------------------------------------------------------

    public Task<GuestRoster> RosterAsync() => _ui.InvokeAsync(_controller.GuestRosterSnapshot);

    /// <summary>Retrato del personaje por su identificador (solo los del catálogo de Kantaria).</summary>
    public byte[]? Avatar(string characterId) =>
        _avatars is not null && CharacterCatalog.Default.TryGet(characterId, out var found)
            ? _avatars.GetPortraitPng(found.Id)
            : null;

    /// <summary>Identificador de personaje que la página puede pedir: el del jugador o, si ya no existe, el de reserva.</summary>
    public static string AvatarIdOf(string? characterId) => CharacterCatalog.Default.GetOrDefault(characterId).Id;

    /// <summary>
    /// La galería de personajes de fábrica de Kantaria (la misma de la PC, en su orden). Los cargados por el usuario no
    /// se ofrecen. Si no hubiera ninguno, la lista queda vacía y el jugador se crea con el personaje genérico.
    /// </summary>
    public static IReadOnlyList<string> FactoryAvatars() =>
        [.. CharacterCatalog.Default.All.Where(c => !c.IsUser).Select(c => c.Id)];

    /// <summary>
    /// Crea un jugador normal de Kantaria con ese nombre y avatar (<c>null</c> = el genérico). Idempotente por
    /// <paramref name="requestId"/> (reintento o doble toque devuelve el mismo jugador); dos pedidos independientes
    /// siguen las reglas normales: el segundo con un nombre ya usado se rechaza (<c>NAME_TAKEN</c>).
    /// </summary>
    public Task<GuestCreatedPlayer> CreatePlayerAsync(string name, string? avatar, string requestId) =>
        Idempotent("p:" + requestId, name.Trim() + "|" + avatar, async () =>
        {
            try
            {
                var player = await _ui.InvokeAsync(() => _controller.GuestCreatePlayer(name, avatar)).ConfigureAwait(false);
                return new GuestCreatedPlayer(player.Id, player.Name, AvatarIdOf(player.CharacterId), false);
            }
            catch (RemoteException e)
            {
                throw Translate(e);
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
            {
                throw new GuestException(GuestErrors.Unavailable, 503);
            }
        }, created => created with { Duplicate = true });

    // ---- Cola -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Agrega la canción con esos cantantes. Idempotente por <paramref name="requestId"/>: un reintento de red o un doble toque
    /// (la página reusa el identificador) devuelve lo ya hecho sin agregar otra vez. Un pedido nuevo (otro
    /// identificador) siempre agrega, aunque sea la misma canción con los mismos cantantes: repetir una canción es
    /// legítimo. Las solicitudes que llegan juntas se serializan en el hilo de la interfaz.
    /// </summary>
    public Task<GuestEnqueueResult> EnqueueAsync(string songId, IReadOnlyList<long> playerIds, string requestId) =>
        Idempotent("q:" + requestId, songId + "|" + string.Join(",", playerIds), () => RunAsync(songId, playerIds),
            result => result with { Duplicate = true });

    /// <summary>
    /// Ejecuta <paramref name="run"/> una sola vez por <paramref name="key"/>: los reintentos esperan el mismo
    /// resultado. La misma clave con otro contenido es un error; un pedido rechazado no se recuerda.
    /// </summary>
    private async Task<T> Idempotent<T>(string key, string fingerprint, Func<Task<T>> run, Func<T, T> asDuplicate)
    {
        Remembered entry;
        bool created;
        lock (_gate)
        {
            var now = _clock();
            Prune(now);
            if (_byRequest.TryGetValue(key, out var same))
            {
                if (same.Fingerprint != fingerprint)
                {
                    throw new GuestException(GuestErrors.RequestReused, 409);
                }

                (entry, created) = (same, false);
            }
            else
            {
                entry = new Remembered(fingerprint, run(), now);
                _byRequest[key] = entry;
                created = true;
            }
        }

        try
        {
            var result = await ((Task<T>)entry.Task).ConfigureAwait(false);
            return created ? result : asDuplicate(result);
        }
        catch (GuestException)
        {
            Forget(key, entry); // un pedido rechazado no se recuerda: se puede reintentar
            throw;
        }
    }

    private async Task<GuestEnqueueResult> RunAsync(string songId, IReadOnlyList<long> playerIds)
    {
        try
        {
            var queued = await _ui.InvokeAsync(() => _controller.GuestEnqueue(songId, playerIds)).ConfigureAwait(false);
            return new GuestEnqueueResult(queued.Position, queued.Pending, false);
        }
        catch (RemoteException e)
        {
            throw Translate(e);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            throw new GuestException(GuestErrors.Unavailable, 503);
        }
    }

    private static GuestException Translate(RemoteException e) => e.Code switch
    {
        ErrorCodes.NotFound when e.Details?["field"]?.GetValue<string>() == "playerId" =>
            new GuestException(GuestErrors.PlayerNotFound, 404),
        ErrorCodes.NotFound or ErrorCodes.EntryUnavailable => new GuestException(GuestErrors.SongNotFound, 404),
        ErrorCodes.PlayerArchived or ErrorCodes.PlayerNotInSession =>
            new GuestException(GuestErrors.PlayerNotAvailable, 409),
        ErrorCodes.NoMicrophones => new GuestException(GuestErrors.NoMicrophones, 409),
        ErrorCodes.SlotUnavailable => new GuestException(GuestErrors.TooManySingers, 409),
        ErrorCodes.QueueFull => new GuestException(GuestErrors.QueueFull, 409),
        ErrorCodes.InvalidPayload => InvalidPayload(e),
        ErrorCodes.PlayerNameTaken => new GuestException(GuestErrors.NameTaken, 409),
        _ => new GuestException(GuestErrors.Unavailable, 503),
    };

    private static GuestException InvalidPayload(RemoteException e) =>
        e.Details?["reason"]?.GetValue<string>() switch
        {
            "empty" => new GuestException(GuestErrors.NameEmpty, 400),
            "too-long" => new GuestException(GuestErrors.NameTooLong, 400),
            "invalid-chars" => new GuestException(GuestErrors.NameInvalid, 400),
            "unknown-avatar" => new GuestException(GuestErrors.InvalidAvatar, 400),
            _ => new GuestException(GuestErrors.InvalidRequest, 400),
        };

    private void Forget(string key, Remembered entry)
    {
        lock (_gate)
        {
            if (_byRequest.TryGetValue(key, out var r) && ReferenceEquals(r, entry))
            {
                _byRequest.Remove(key);
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (_byRequest.Count == 0)
        {
            return;
        }

        foreach (var key in _byRequest.Where(p => now - p.Value.At > RequestMemory).Select(p => p.Key).ToList())
        {
            _byRequest.Remove(key);
        }

        while (_byRequest.Count > MaxRemembered)
        {
            var oldest = _byRequest.MinBy(p => p.Value.At).Key;
            _byRequest.Remove(oldest);
        }
    }
}
