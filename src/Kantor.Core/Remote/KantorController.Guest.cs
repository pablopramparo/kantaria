// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Text.Json.Nodes;
using Kantor.Core.Characters;
using Kantor.Core.Data;

namespace Kantor.Core.Remote;

/// <summary>Un jugador que un invitado puede elegir como cantante.</summary>
public sealed record GuestPlayer(long Id, string Name, string? CharacterId);

/// <summary>Quiénes se pueden elegir ahora y cuántos pueden cantar a la vez (micrófonos creados).</summary>
public sealed record GuestRoster(IReadOnlyList<GuestPlayer> Players, int MaxSingers);

/// <summary>Una canción agregada a la cola: su lugar entre los pendientes (desde 1).</summary>
public sealed record GuestQueued(string ItemId, int Position, int Pending);

public sealed partial class KantorController
{
    /// <summary>Quién figura como autor de lo agregado desde el celular (lo ve también Avyn en la cola).</summary>
    public const string GuestOriginId = "guest-lan";

    /// <summary>
    /// Jugadores elegibles por un invitado y cantidad máxima de cantantes. Mismas reglas que
    /// <see cref="ValidatePlayers"/>: con una partida abierta, solo sus jugadores; sin partida (o cantando suelto),
    /// cualquier jugador activo. Hilo de la interfaz.
    /// </summary>
    public GuestRoster GuestRosterSnapshot()
    {
        var host = _host.Capture();
        var store = _store;
        if (store is null)
        {
            return new GuestRoster([], 0);
        }

        var open = store.GetOpenSession();
        var players = open is null || host.Solo ? store.ListPlayers(false) : store.SessionPlayers(open.Id);
        return new GuestRoster(
            [.. players.Where(p => !p.Archived).Select(p => new GuestPlayer(p.Id, p.Name, p.CharacterId))],
            Math.Min(host.MicrophoneSlots.Count, KantorStore.MaxPlayersPerPerformance));
    }

    /// <summary>
    /// Crea un jugador normal de Kantor (el mismo registro que crea la PC) con un avatar de fábrica. Con una partida
    /// abierta (y sin cantar suelto) entra a ella, como los que se crean desde "¿Quién canta?" en la PC: si no, no
    /// se podría elegir. No toca la selección de micrófonos ni la canción en curso. Hilo de la interfaz.
    /// </summary>
    /// <exception cref="RemoteException">
    /// <c>INVALID_PAYLOAD</c> con <c>details.reason</c> (<c>empty</c>, <c>too-long</c>, <c>invalid-chars</c>,
    /// <c>unknown-avatar</c>) o <c>PLAYER_NAME_TAKEN</c>; no cambió nada.
    /// </exception>
    public GuestPlayer GuestCreatePlayer(string name, string? avatarId)
    {
        var store = RequireStore();
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw Reason("name", "empty");
        }

        if (name.Length > KantorStore.MaxNameLength)
        {
            throw Reason("name", "too-long");
        }

        if (name.Any(char.IsControl))
        {
            throw Reason("name", "invalid-chars");
        }

        // solo los avatares de fábrica: los cargados por el usuario no se ofrecen ni se aceptan desde el celular
        if (avatarId is not null && !CharacterCatalog.Factory.Any(c => c.Id == avatarId))
        {
            throw Reason("avatar", "unknown-avatar");
        }

        var host = _host.Capture();
        PlayerRecord player;
        try
        {
            player = store.CreatePlayer(name, avatarId);
        }
        catch (StoreRuleException)
        {
            throw new RemoteException(ErrorCodes.PlayerNameTaken, "An active player already has that name.");
        }

        if (store.GetOpenSession() is { } open && !host.Solo)
        {
            store.AddPlayerToSession(open.Id, player.Id);
        }

        _host.PlayersAdded();
        return new GuestPlayer(player.Id, player.Name, player.CharacterId);
    }

    private static RemoteException Reason(string field, string reason) =>
        new(ErrorCodes.InvalidPayload, $"Invalid {field}.", new JsonObject { ["field"] = field, ["reason"] = reason });

    /// <summary>
    /// Agrega una canción a la cola con los cantantes elegidos, por la misma ruta que <c>kantor.queue.add</c>: el
    /// primer cantante ocupa el primer micrófono creado, el segundo el siguiente, y así. Hilo de la interfaz.
    /// </summary>
    /// <exception cref="RemoteException">Con los mismos códigos que el control remoto; no cambió nada.</exception>
    public GuestQueued GuestEnqueue(string libraryEntryId, IReadOnlyList<long> playerIds)
    {
        var host = _host.Capture();
        var item = _catalog.Current.Find(libraryEntryId);
        if (item is null)
        {
            throw _store?.GetLibraryEntry(libraryEntryId) is not null
                ? new RemoteException(ErrorCodes.EntryUnavailable, "That library entry is no longer available.")
                : RemoteException.NotFound("libraryEntryId", gone: false);
        }

        if (playerIds.Count == 0 || playerIds.Distinct().Count() != playerIds.Count)
        {
            throw RemoteException.Invalid("playerIds", "Choose at least one player, without repeating.");
        }

        if (host.MicrophoneSlots.Count == 0)
        {
            throw new RemoteException(ErrorCodes.NoMicrophones, "No microphone is set up in Options.");
        }

        if (playerIds.Count > host.MicrophoneSlots.Count)
        {
            throw new RemoteException(ErrorCodes.SlotUnavailable, "There are more singers than microphones.",
                new JsonObject { ["slot"] = playerIds.Count });
        }

        var slots = playerIds.Select((player, i) => new SelectionEntry(host.MicrophoneSlots[i], player)).ToList();
        ValidatePlayers(slots, host, SessionPolicy.Current, requireMicrophones: true);

        var added = Guard(() => _queue.Add(item.EntryId, item.SongIdentity, item.Entry.Artist, item.Entry.Title,
            item.Entry.Edition, atNext: false, slots: [.. slots.Select(s => new QueueSlot(s.Slot, s.PlayerId))],
            addedById: GuestOriginId, addedByName: "Guest"));
        Flush(); // lo que ve Avyn sale ya publicado, como con cualquier otra mutación
        var pending = _queue.Pending();
        return new GuestQueued(added.ItemId, pending.ToList().FindIndex(e => e.ItemId == added.ItemId) + 1, pending.Count);
    }
}
