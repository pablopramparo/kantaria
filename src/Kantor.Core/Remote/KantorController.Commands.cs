// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Kantor.Core.Characters;
using Kantor.Core.Data;
using Kantor.Core.Library;

namespace Kantor.Core.Remote;

public sealed partial class KantorController
{
    private enum Kind
    {
        /// <summary>Cambia algo: exige <c>commandId</c> y <c>origin</c>, se deduplica y corre en la interfaz.</summary>
        Mutation,

        /// <summary>Lee estado de la ventana: corre en la interfaz.</summary>
        UiRead,

        /// <summary>Lee datos inmutables o protegidos: corre fuera de la interfaz.</summary>
        HeavyRead,
    }

    private sealed record Command(string Capability, Kind Kind, Func<Call, JsonNode?> Handler);

    private sealed record Call(IncomingMessage Message, PayloadReader Payload, Origin? Origin);

    private Dictionary<string, Command>? _commands;

    private Dictionary<string, Command> Commands => _commands ??= BuildCommands();

    /// <summary>Los comandos que existen y qué exigen: sirve a las pruebas del contrato.</summary>
    public IReadOnlyDictionary<string, bool> CommandTable =>
        Commands.ToDictionary(c => c.Key, c => c.Value.Kind == Kind.Mutation);

    private Dictionary<string, Command> BuildCommands() => new(StringComparer.Ordinal)
    {
        ["kantor.state.get"] = new("state", Kind.UiRead, _ => BuildSnapshotMessage()["payload"]!.DeepClone()),

        ["kantor.library.search"] = new("library", Kind.HeavyRead, LibrarySearchCommand),
        ["kantor.library.get"] = new("library", Kind.HeavyRead, LibraryGet),
        ["kantor.library.facets"] = new("library", Kind.HeavyRead, LibraryFacetsCommand),
        ["kantor.library.cover"] = new("library", Kind.HeavyRead, LibraryCover),

        ["kantor.queue.list"] = new("queue", Kind.HeavyRead, QueueList),
        ["kantor.queue.add"] = new("queue", Kind.Mutation, QueueAdd),
        ["kantor.queue.remove"] = new("queue", Kind.Mutation, c => QueueOp(c, () => _queue.Remove(Req(c, "itemId")))),
        ["kantor.queue.move"] = new("queue", Kind.Mutation, c => QueueOp(c, () =>
            _queue.Move(Req(c, "itemId"), NullableId(c, "beforeItemId"), c.Payload.Int("expectedQueueRev")))),
        ["kantor.queue.setNext"] = new("queue", Kind.Mutation, c => QueueOp(c, () =>
            _queue.SetNext(Req(c, "itemId"), c.Payload.Int("expectedQueueRev")))),
        ["kantor.queue.setSlots"] = new("queue", Kind.Mutation, QueueSetSlots),
        ["kantor.queue.clear"] = new("queue", Kind.Mutation, QueueClear),

        ["kantor.players.list"] = new("players", Kind.HeavyRead, PlayersList),
        ["kantor.players.create"] = new("players", Kind.Mutation, PlayersCreate),
        ["kantor.players.rename"] = new("players", Kind.Mutation, PlayersRename),
        ["kantor.players.archive"] = new("players", Kind.Mutation, PlayersArchive),
        ["kantor.players.setCharacter"] = new("players", Kind.Mutation, PlayersSetCharacter),
        ["kantor.characters.list"] = new("players", Kind.HeavyRead, _ => CharactersList()),
        ["kantor.selection.set"] = new("players", Kind.Mutation, SelectionSet),

        ["kantor.session.get"] = new("session", Kind.HeavyRead, SessionGet),
        ["kantor.session.list"] = new("session", Kind.HeavyRead, SessionList),
        ["kantor.session.start"] = new("session", Kind.Mutation, SessionStart),
        ["kantor.session.close"] = new("session", Kind.Mutation, SessionClose),
        ["kantor.session.addPlayer"] = new("session", Kind.Mutation, c => SessionMember(c, add: true)),
        ["kantor.session.removePlayer"] = new("session", Kind.Mutation, c => SessionMember(c, add: false)),
        ["kantor.session.history"] = new("session", Kind.HeavyRead, SessionHistory),

        ["kantor.playback.start"] = new("playback", Kind.Mutation, PlaybackStart),
        ["kantor.playback.confirmSingers"] = new("playback", Kind.Mutation, PlaybackConfirm),
        ["kantor.playback.pause"] = new("playback", Kind.Mutation, c => PlaybackSimple(c, Pause)),
        ["kantor.playback.resume"] = new("playback", Kind.Mutation, c => PlaybackSimple(c, Resume)),
        ["kantor.playback.restart"] = new("playback", Kind.Mutation, c => PlaybackSimple(c, Restart)),
        ["kantor.playback.stop"] = new("playback", Kind.Mutation, c => PlaybackSimple(c, Stop)),
        ["kantor.playback.next"] = new("playback", Kind.Mutation, c => PlaybackSimple(c, Next)),
        ["kantor.navigate.menu"] = new("playback", Kind.Mutation, c => PlaybackSimple(c, ToMenu)),
        ["kantor.results.skipAnimation"] = new("results", Kind.Mutation, c => PlaybackSimple(c, SkipAnimation, cancelAuto: false)),
        ["kantor.results.close"] = new("results", Kind.Mutation, c => PlaybackSimple(c, CloseResults)),
        ["kantor.settings.setAutoAdvance"] = new("playback", Kind.Mutation, SetAutoAdvance),
        ["kantor.autoAdvance.cancel"] = new("playback", Kind.Mutation, c =>
        {
            _auto.Cancel();
            return new JsonObject();
        }),

        ["kantor.results.last"] = new("results", Kind.UiRead, ResultsLast),
        ["kantor.history.performance"] = new("results", Kind.HeavyRead, HistoryPerformance),
        ["kantor.history.player"] = new("results", Kind.HeavyRead, HistoryPlayer),

        ["kantor.screen.setFullscreen"] = new("screen", Kind.Mutation, c =>
        {
            _host.SetFullscreen(c.Payload.Bool("enabled", required: true)!.Value);
            return new JsonObject();
        }),
    };

    // ---- Despacho -------------------------------------------------------------------------------------------

    /// <summary>
    /// Ejecuta un comando y devuelve su respuesta. <paramref name="runOnUi"/> corre un trabajo en el hilo de la
    /// interfaz y devuelve su resultado. Para las mutaciones, el estado publicado sale ANTES que la respuesta.
    /// </summary>
    public async Task<CachedResponse> HandleAsync(IncomingMessage message,
        Func<Func<CachedResponse>, Task<CachedResponse>> runOnUi)
    {
        if (!Commands.TryGetValue(message.Type, out var command))
        {
            return Failure(new RemoteException(ErrorCodes.UnknownCommand, $"Unknown command '{message.Type}'."));
        }

        if (!Capabilities.ContainsKey(command.Capability))
        {
            return Failure(new RemoteException(ErrorCodes.CapabilityUnavailable,
                $"Capability '{command.Capability}' is not available."));
        }

        var call = new Call(message, message.Payload, message.Origin);
        try
        {
            if (command.Kind == Kind.HeavyRead)
            {
                return Success(command.Handler(call));
            }

            if (command.Kind == Kind.UiRead)
            {
                return await runOnUi(() => Expired(message) ? TimedOut() : RunHandler(command, call, flush: false))
                    .ConfigureAwait(false);
            }

            var commandId = PayloadReader.CheckCommandId(message.CommandId);
            if (message.Origin is null)
            {
                throw RemoteException.Invalid("origin", "'origin' with clientId (<= 64) and name (<= 48) is required.");
            }

            var fingerprint = CommandDeduplicator.Fingerprint(message.Type, message.Payload.Raw);
            switch (_dedupe.Begin(commandId, fingerprint))
            {
                case DedupeDecision.Reused:
                    return Failure(new RemoteException(ErrorCodes.CommandIdReused,
                        "The commandId was already used with a different command or payload.",
                        new JsonObject { ["applied"] = false }));
                case DedupeDecision.Replay replay:
                    return await replay.Response.ConfigureAwait(false);
            }

            // un comando que esperó más de 3 s su turno no se ejecuta: y su id queda libre para reintentarlo
            var response = await runOnUi(() => Expired(message) ? TimedOut() : RunHandler(command, call, flush: true))
                .ConfigureAwait(false);
            _dedupe.Complete(commandId, response, remember: response.ErrorCode != ErrorCodes.Timeout);
            return response;
        }
        catch (RemoteException e)
        {
            return Failure(e);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Failure(new RemoteException(ErrorCodes.InternalError, "Internal error: " + e.Message,
                new JsonObject { ["applied"] = "unknown" }));
        }
    }

    private static bool Expired(IncomingMessage message) =>
        DateTimeOffset.UtcNow - message.ReceivedAt > Protocol.QueueWait;

    /// <summary>Las lecturas pesadas corren fuera del hilo de la interfaz y fuera de la cola de comandos.</summary>
    public bool IsHeavyRead(string type) => Commands.TryGetValue(type, out var c) && c.Kind == Kind.HeavyRead;

    /// <summary>Un comando que venció esperando su turno: no se ejecutó y el <c>commandId</c> queda libre.</summary>
    public CachedResponse TimedOut() => Failure(new RemoteException(ErrorCodes.Timeout,
        "The command waited too long before starting and was not executed."));

    private CachedResponse RunHandler(Command command, Call call, bool flush)
    {
        CachedResponse response;
        try
        {
            response = Success(command.Handler(call));
        }
        catch (RemoteException e)
        {
            response = Failure(e);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            response = Failure(new RemoteException(ErrorCodes.InternalError, "Internal error: " + e.Message,
                new JsonObject { ["applied"] = "unknown" }));
        }

        if (flush)
        {
            Flush(); // lo publicado sale antes que la respuesta
            return response with { Rev = _publisher.Rev };
        }

        return response;
    }

    private CachedResponse Success(JsonNode? payload) =>
        new(true, _publisher.Rev, payload?.ToJsonString(), null, null, null);

    private CachedResponse Failure(RemoteException e)
    {
        var details = e.Details ?? [];
        if (!details.ContainsKey("applied"))
        {
            details["applied"] = e.Code == ErrorCodes.InternalError ? "unknown" : false;
        }

        return new CachedResponse(false, _publisher.Rev, null, e.Code, e.Message, details.ToJsonString());
    }

    // ---- Utilidades de los comandos ---------------------------------------------------------------------------

    private static string Req(Call c, string field, int max = Protocol.MaxIdChars) =>
        c.Payload.String(field, required: true, min: 1, max: max)!;

    private static string? NullableId(Call c, string field) =>
        c.Payload.Has(field) ? c.Payload.String(field, min: 1, max: Protocol.MaxIdChars) : null;

    private KantorStore RequireStore() =>
        _store ?? throw RemoteException.State("The history database is not available.", reason: "database-unavailable");

    private static bool Busy(HostState host) =>
        host.Status is PlaybackStatuses.Playing or PlaybackStatuses.Paused || host.Screen == Screens.Loading
        || host.Screen == Screens.WhoSings;

    private static RemoteException BusyError(HostState host) =>
        new(ErrorCodes.Busy, "A song is being loaded or sung.", new JsonObject { ["screen"] = host.Screen });

    private static JsonNode? Paged(IEnumerable<JsonNode?> items, int total, int offset, int? next, JsonObject? extra = null)
    {
        var node = new JsonObject
        {
            ["total"] = total,
            ["offset"] = offset,
            ["nextOffset"] = next,
            ["items"] = new JsonArray([.. items]),
        };
        if (extra is not null)
        {
            foreach (var (k, v) in extra)
            {
                node[k] = v?.DeepClone();
            }
        }

        return node;
    }

    private static JsonNode? Slice(IReadOnlyList<JsonNode?> all, int offset, int limit, JsonObject? extra = null)
    {
        var page = all.Skip(offset).Take(limit).ToList();
        var end = offset + page.Count;
        return Paged(page, all.Count, offset, end < all.Count ? end : null, extra);
    }

    // ---- Biblioteca -------------------------------------------------------------------------------------------

    private static JsonObject SongNode(LibraryItem i) => new()
    {
        ["libraryEntryId"] = i.EntryId,
        ["songIdentity"] = i.SongIdentity,
        ["artist"] = i.Entry.Artist,
        ["title"] = i.Entry.Title,
        ["edition"] = i.Entry.Edition,
        ["genre"] = i.Entry.Genre,
        ["year"] = i.Entry.Year,
        ["language"] = i.Entry.Language,
        ["hasVideo"] = i.Entry.HasVideo,
        ["coverId"] = i.CoverId,
    };

    private JsonNode? LibrarySearchCommand(Call c)
    {
        var p = c.Payload;
        var sort = p.Choice("sort", ["artist", "title"]) ?? "artist";
        var query = new LibraryQuery(
            p.String("query", max: Protocol.MaxQueryChars),
            p.String("songIdentity", max: Protocol.MaxIdChars),
            p.String("genre", max: Protocol.MaxQueryChars),
            p.String("year", max: 16),
            p.String("language", max: Protocol.MaxQueryChars),
            sort == "title" ? LibrarySort.Title : LibrarySort.Artist);
        var offset = p.Int("offset", 0) ?? 0;
        var limit = p.Int("limit", 1, Protocol.MaxPageSize) ?? 20;

        var snapshot = _catalog.Current;
        var found = LibrarySearch.Run(snapshot, query);
        var items = new List<JsonNode?>();
        var bytes = 0;
        var index = offset;
        while (index < found.Count && items.Count < limit)
        {
            var node = SongNode(found[index]);
            var size = Encoding.UTF8.GetByteCount(node.ToJsonString());
            if (items.Count > 0 && bytes + size > Protocol.SearchPageBudgetBytes)
            {
                break;
            }

            items.Add(node);
            bytes += size;
            index++;
        }

        return Paged(items, found.Count, offset, index < found.Count ? index : null,
            new JsonObject { ["libraryRev"] = snapshot.Rev });
    }

    private JsonNode? LibraryGet(Call c)
    {
        var id = Req(c, "libraryEntryId");
        if (_catalog.Current.Find(id) is { } item)
        {
            return SongNode(item);
        }

        throw RemoteException.NotFound("libraryEntryId", gone: _store?.GetLibraryEntry(id) is not null);
    }

    private JsonNode? LibraryFacetsCommand(Call c)
    {
        var snapshot = _catalog.Current;
        var facets = LibrarySearch.Facets(snapshot);
        JsonArray Values(IReadOnlyList<FacetValue> list) =>
            new([.. list.Select(v => (JsonNode?)new JsonObject { ["value"] = v.Value, ["count"] = v.Count })]);
        return new JsonObject
        {
            ["libraryRev"] = snapshot.Rev,
            ["genres"] = Values(facets.Genres),
            ["years"] = Values(facets.Years),
            ["languages"] = Values(facets.Languages),
        };
    }

    private JsonNode? LibraryCover(Call c)
    {
        var id = Req(c, "libraryEntryId");
        var size = c.Payload.Int("size", required: true)!.Value;
        if (size is not (96 or 256))
        {
            throw RemoteException.Invalid("size", "'size' must be 96 or 256.");
        }

        var item = _catalog.Current.Find(id)
                   ?? throw RemoteException.NotFound("libraryEntryId", gone: _store?.GetLibraryEntry(id) is not null);
        if (item.Entry.CoverPath is null || _o.Covers is null)
        {
            throw RemoteException.NotFound("cover", gone: false);
        }

        var image = _o.Covers.GetThumbnail(item.Entry.CoverPath, size)
                    ?? throw RemoteException.NotFound("cover", gone: true);
        return new JsonObject
        {
            ["libraryEntryId"] = id,
            ["coverId"] = item.CoverId,
            ["size"] = size,
            ["mime"] = "image/jpeg",
            ["width"] = image.Width,
            ["height"] = image.Height,
            ["dataBase64"] = Convert.ToBase64String(image.Jpeg),
        };
    }

    // ---- Cola -------------------------------------------------------------------------------------------------

    private JsonNode? QueueList(Call c)
    {
        var p = c.Payload;
        var sessionId = p.Long("sessionId");
        var includeFinished = p.Bool("includeFinished") ?? false;
        var offset = p.Int("offset", 0) ?? 0;
        var limit = p.Int("limit", 1, Protocol.MaxQueueListSize) ?? Protocol.MaxQueueListSize;
        if (sessionId is { } id && (_store is null || _store.ListSessions().All(s => s.Id != id)))
        {
            throw RemoteException.NotFound("sessionId", gone: false);
        }

        var items = _queue.List(sessionId, includeFinished);
        var nodes = items.Select(i => (JsonNode?)QueueItemNode(i)).ToList();
        return Slice(nodes, offset, limit, new JsonObject
        {
            ["queueRev"] = _queue.Rev,
            ["persistent"] = sessionId is not null || _queue.Persistent,
            ["sessionId"] = sessionId ?? _store?.GetOpenSession()?.Id,
            ["readOnly"] = sessionId is { } sid && _store?.GetOpenSession()?.Id != sid,
        });
    }

    private JsonNode? QueueAdd(Call c)
    {
        var id = Req(c, "libraryEntryId");
        var position = c.Payload.Choice("position", ["end", "next"]) ?? "end";
        var host = _host.Capture();
        var slots = ReadSlots(c, "slots", host);
        var item = _catalog.Current.Find(id);
        if (item is null)
        {
            throw _store?.GetLibraryEntry(id) is not null
                ? new RemoteException(ErrorCodes.EntryUnavailable, "That library entry is no longer available.",
                    new JsonObject { ["libraryEntryId"] = id })
                : RemoteException.NotFound("libraryEntryId", gone: false);
        }

        if (slots is not null)
        {
            ValidatePlayers(slots, host, SessionPolicy.Current, requireMicrophones: true);
        }

        var added = Guard(() => _queue.Add(item.EntryId, item.SongIdentity, item.Entry.Artist, item.Entry.Title,
            item.Entry.Edition, atNext: position == "next",
            slots: slots?.Select(s => new QueueSlot(s.Slot, s.PlayerId)).ToList(),
            addedById: c.Origin?.ClientId, addedByName: c.Origin?.Name));
        return new JsonObject { ["queueRev"] = _queue.Rev, ["item"] = QueueItemNode(added) };
    }

    private JsonNode? QueueOp(Call c, Action action)
    {
        Guard(() =>
        {
            action();
            return 0;
        });
        return new JsonObject { ["queueRev"] = _queue.Rev };
    }

    private JsonNode? QueueSetSlots(Call c)
    {
        var id = Req(c, "itemId");
        var host = _host.Capture();
        var slots = ReadSlots(c, "slots", host) ?? [];
        if (slots.Count > 0)
        {
            ValidatePlayers(slots, host, SessionPolicy.Current, requireMicrophones: true);
        }

        Guard(() =>
        {
            _queue.SetSlots(id, [.. slots.Select(s => new QueueSlot(s.Slot, s.PlayerId))]);
            return 0;
        });
        return new JsonObject { ["queueRev"] = _queue.Rev };
    }

    private JsonNode? QueueClear(Call c)
    {
        if (c.Payload.Bool("confirm", required: true) != true)
        {
            throw RemoteException.Invalid("confirm", "'confirm' must be true.");
        }

        var expected = c.Payload.Int("expectedQueueRev", required: true)!.Value;
        var removed = Guard(() => _queue.Clear(expected));
        return new JsonObject { ["queueRev"] = _queue.Rev, ["removed"] = removed };
    }

    /// <summary>Traduce los errores de la cola a los del contrato; <c>CONFLICT</c> trae la cola vigente.</summary>
    private T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (QueueException e)
        {
            throw e.Error switch
            {
                QueueError.Unknown => RemoteException.NotFound(e.Field, gone: false),
                QueueError.Gone => RemoteException.NotFound(e.Field, gone: true),
                QueueError.Full => new RemoteException(ErrorCodes.QueueFull, e.Message),
                QueueError.Running => new RemoteException(ErrorCodes.ItemRunning, e.Message,
                    new JsonObject { ["itemId"] = e.Field }),
                _ => new RemoteException(ErrorCodes.Conflict, e.Message, new JsonObject
                {
                    ["queueRev"] = _queue.Rev,
                    ["items"] = new JsonArray([.. _queue.List().Select(i => (JsonNode?)QueueItemNode(i))]),
                }),
            };
        }
    }

    // ---- Jugadores y selección --------------------------------------------------------------------------------

    private static JsonObject PlayerNode(PlayerRecord p) => new()
    {
        ["playerId"] = p.Id,
        ["name"] = p.Name,
        ["characterId"] = p.CharacterId,
        ["archived"] = p.Archived,
        ["lastActivityAt"] = p.LastActivityAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
    };

    private JsonNode? PlayersList(Call c)
    {
        var include = c.Payload.Bool("includeArchived") ?? false;
        var players = RequireStore().ListPlayers(include);
        return new JsonObject { ["items"] = new JsonArray([.. players.Select(p => (JsonNode?)PlayerNode(p))]) };
    }

    private static string PlayerName(Call c, string field)
    {
        var name = c.Payload.String(field, required: true)!.Trim();
        if (name.Length is 0 or > Protocol.MaxPlayerNameChars)
        {
            throw RemoteException.Invalid(field, $"'{field}' must have 1 to {Protocol.MaxPlayerNameChars} characters.");
        }

        return name;
    }

    private static CharacterDefinition Character(string? id) =>
        id is null
            ? CharacterCatalog.Fallback
            : CharacterCatalog.Default.TryGet(id, out var found)
                ? found
                : throw RemoteException.NotFound("characterId", gone: false);

    private JsonNode? PlayersCreate(Call c)
    {
        var store = RequireStore();
        var name = PlayerName(c, "name");
        var character = Character(c.Payload.String("characterId", max: 64));
        try
        {
            var player = store.CreatePlayer(name, character.Id);
            _host.DataChanged();
            return PlayerNode(player);
        }
        catch (StoreRuleException)
        {
            throw new RemoteException(ErrorCodes.PlayerNameTaken, "An active player already has that name.");
        }
    }

    private PlayerRecord ActivePlayer(long id)
    {
        var player = RequireStore().GetPlayer(id) ?? throw RemoteException.NotFound("playerId", gone: false);
        return player.Archived
            ? throw new RemoteException(ErrorCodes.PlayerArchived, "The player is archived.",
                new JsonObject { ["playerId"] = id })
            : player;
    }

    private JsonNode? PlayersRename(Call c)
    {
        var player = ActivePlayer(c.Payload.Long("playerId", required: true)!.Value);
        var name = PlayerName(c, "name");
        try
        {
            RequireStore().RenamePlayer(player.Id, name);
        }
        catch (StoreRuleException)
        {
            throw new RemoteException(ErrorCodes.PlayerNameTaken, "An active player already has that name.");
        }

        _host.DataChanged();
        return PlayerNode(RequireStore().GetPlayer(player.Id)!);
    }

    private void RejectIfSinging(long playerId)
    {
        var host = _host.Capture();
        if (host.Slots.Any(s => s.PlayerId == playerId) && (Busy(host) || host.Screen == Screens.WhoSings))
        {
            throw BusyError(host);
        }
    }

    private JsonNode? PlayersArchive(Call c)
    {
        var id = c.Payload.Long("playerId", required: true)!.Value;
        var player = RequireStore().GetPlayer(id) ?? throw RemoteException.NotFound("playerId", gone: false);
        if (!player.Archived)
        {
            RejectIfSinging(id);
            RequireStore().ArchivePlayer(id);
            _host.DataChanged();
        }

        return PlayerNode(RequireStore().GetPlayer(id)!);
    }

    private JsonNode? PlayersSetCharacter(Call c)
    {
        var player = ActivePlayer(c.Payload.Long("playerId", required: true)!.Value);
        var character = Character(Req(c, "characterId", 64));
        RequireStore().SetCharacter(player.Id, character.Id);
        _host.DataChanged();
        return PlayerNode(RequireStore().GetPlayer(player.Id)!);
    }

    private static JsonNode? CharactersList() => new JsonObject
    {
        ["items"] = new JsonArray([.. CharacterCatalog.Default.All.Select(ch => (JsonNode?)new JsonObject
        {
            ["characterId"] = ch.Id,
            ["name"] = ch.Name,
            // los de fábrica traen las dos imágenes por convención; los del usuario, las que se cargaron
            ["hasPortrait"] = !ch.IsUser || ch.PortraitPath is not null,
            ["hasFullBody"] = !ch.IsUser || ch.FullBodyPath is not null,
        })]),
    };

    /// <summary>Lee <c>[{slot, playerId}]</c>; valida el rango del puesto y que no se repitan puestos ni jugadores.</summary>
    private static List<SelectionEntry>? ReadSlots(Call c, string field, HostState host)
    {
        var array = c.Payload.Array(field, KantorStore.MaxPlayersPerPerformance);
        if (array is null)
        {
            return null;
        }

        var result = new List<SelectionEntry>();
        foreach (var node in array)
        {
            if (node is not JsonObject obj)
            {
                throw RemoteException.Invalid(field, $"'{field}' items must be objects.");
            }

            var item = new PayloadReader(obj);
            var slot = item.Int("slot", 1, KantorStore.MaxPlayersPerPerformance, required: true)!.Value;
            var player = item.Long("playerId", required: true)!.Value;
            if (result.Any(r => r.Slot == slot || r.PlayerId == player))
            {
                throw RemoteException.Invalid(field, "A slot or a player is repeated.");
            }

            result.Add(new SelectionEntry(slot, player));
        }

        return result;
    }

    /// <summary>
    /// Que cada puesto exista (y tenga micrófono), que cada jugador exista, esté activo y, con partida, pertenezca
    /// a ella.
    /// </summary>
    private void ValidatePlayers(IReadOnlyList<SelectionEntry> slots, HostState host, SessionPolicy policy,
        bool requireMicrophones)
    {
        var store = RequireStore();
        var open = policy == SessionPolicy.Current ? store.GetOpenSession() : null;
        var members = open is null ? null : store.SessionPlayers(open.Id).Select(p => p.Id).ToHashSet();
        foreach (var entry in slots)
        {
            if (requireMicrophones && !host.MicrophoneSlots.Contains(entry.Slot))
            {
                throw host.MicrophoneSlots.Count == 0
                    ? new RemoteException(ErrorCodes.NoMicrophones, "No microphone is set up in Options.")
                    : new RemoteException(ErrorCodes.SlotUnavailable, $"Slot {entry.Slot} has no microphone.",
                        new JsonObject { ["slot"] = entry.Slot });
            }

            var player = ActivePlayer(entry.PlayerId);
            if (members is not null && !members.Contains(player.Id) && !host.Solo)
            {
                throw new RemoteException(ErrorCodes.PlayerNotInSession, "The player is not in the open session.",
                    new JsonObject { ["playerId"] = player.Id });
            }
        }
    }

    private JsonNode? SelectionSet(Call c)
    {
        var host = _host.Capture();
        if (host.Status is PlaybackStatuses.Playing or PlaybackStatuses.Paused || host.Screen == Screens.Loading)
        {
            throw BusyError(host);
        }

        var slots = ReadSlots(c, "slots", host) ?? throw RemoteException.Invalid("slots", "'slots' is required.");
        var expected = c.Payload.Int("expectedSelectionRev");
        if (expected is { } rev && rev != _selectionRev)
        {
            throw new RemoteException(ErrorCodes.Conflict, "The selection changed.", new JsonObject
            {
                ["selectionRev"] = _selectionRev,
                ["slots"] = new JsonArray([.. host.Slots.Select(s => (JsonNode?)new JsonObject
                {
                    ["slot"] = s.Slot, ["playerId"] = s.PlayerId,
                })]),
            });
        }

        ValidatePlayers(slots, host, SessionPolicy.Current, requireMicrophones: true);
        _host.ApplySelection(slots);
        Flush();
        return new JsonObject { ["selectionRev"] = _selectionRev };
    }

    // ---- Partida ----------------------------------------------------------------------------------------------

    private JsonNode SessionNode(SessionRecord s, int? performances = null)
    {
        var store = RequireStore();
        return new JsonObject
        {
            ["sessionId"] = s.Id,
            ["name"] = s.Name,
            ["startedAt"] = s.StartedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["closedAt"] = s.ClosedAt?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["isOpen"] = s.IsOpen,
            ["playerIds"] = new JsonArray([.. store.SessionPlayers(s.Id).Select(p => (JsonNode?)p.Id)]),
            ["performanceCount"] = performances ?? store.GetSessionHistory(s.Id).Count,
        };
    }

    private JsonNode? SessionGet(Call c)
    {
        var store = RequireStore();
        var id = c.Payload.Long("sessionId");
        var session = id is { } wanted
            ? store.ListSessions().FirstOrDefault(s => s.Id == wanted)
              ?? throw RemoteException.NotFound("sessionId", gone: false)
            : store.GetOpenSession();
        return new JsonObject
        {
            ["session"] = session is null ? null : SessionNode(session),
            ["selection"] = session is null
                ? new JsonArray()
                : new JsonArray([.. store.GetSelection(session.Id).Select(s => (JsonNode?)new JsonObject
                {
                    ["slot"] = s.Slot, ["playerId"] = s.PlayerId,
                })]),
        };
    }

    private JsonNode? SessionList(Call c)
    {
        var store = RequireStore();
        var offset = c.Payload.Int("offset", 0) ?? 0;
        var limit = c.Payload.Int("limit", 1, Protocol.MaxPageSize) ?? Protocol.MaxPageSize;
        var all = store.ListSessions().Select(s => (JsonNode?)SessionNode(s)).ToList();
        return Slice(all, offset, limit);
    }

    private void RejectIfBusyWithSession()
    {
        var host = _host.Capture();
        if (host.Status is PlaybackStatuses.Playing or PlaybackStatuses.Paused || host.Screen == Screens.Loading
            || host.Screen == Screens.WhoSings)
        {
            throw BusyError(host);
        }
    }

    private JsonNode? SessionStart(Call c)
    {
        var store = RequireStore();
        var name = c.Payload.String("name", max: Protocol.MaxSessionNameChars);
        var onOpen = c.Payload.Choice("onOpen", ["fail", "closeAndStart"], required: true)!;
        var open = store.GetOpenSession();
        if (open is not null)
        {
            if (!string.Equals(onOpen, "closeAndStart", StringComparison.OrdinalIgnoreCase))
            {
                throw new RemoteException(ErrorCodes.SessionOpen, "There is already an open session.",
                    new JsonObject { ["sessionId"] = open.Id });
            }

            RejectIfBusyWithSession();
        }

        var session = store.StartSession(name); // cierra la anterior en la misma transacción
        _host.DataChanged();
        return SessionNode(session, 0);
    }

    private JsonNode? SessionClose(Call c)
    {
        var store = RequireStore();
        var id = c.Payload.Long("sessionId", required: true)!.Value;
        var session = store.ListSessions().FirstOrDefault(s => s.Id == id)
                      ?? throw RemoteException.NotFound("sessionId", gone: false);
        if (session.IsOpen)
        {
            RejectIfBusyWithSession();
            store.CloseSession(id);
            _host.DataChanged();
        }

        return new JsonObject();
    }

    private JsonNode? SessionMember(Call c, bool add)
    {
        var store = RequireStore();
        var sessionId = c.Payload.Long("sessionId", required: true)!.Value;
        var playerId = c.Payload.Long("playerId", required: true)!.Value;
        var session = store.ListSessions().FirstOrDefault(s => s.Id == sessionId)
                      ?? throw RemoteException.NotFound("sessionId", gone: false);
        if (!session.IsOpen)
        {
            throw RemoteException.State("The session is closed.", reason: "session-closed");
        }

        if (add)
        {
            ActivePlayer(playerId);
            store.AddPlayerToSession(sessionId, playerId);
        }
        else
        {
            _ = store.GetPlayer(playerId) ?? throw RemoteException.NotFound("playerId", gone: false);
            RejectIfSinging(playerId);
            store.RemovePlayerFromSession(sessionId, playerId);
        }

        _host.DataChanged();
        return SessionNode(session);
    }

    private JsonNode? SessionHistory(Call c)
    {
        var store = RequireStore();
        var id = c.Payload.Long("sessionId", required: true)!.Value;
        var offset = c.Payload.Int("offset", 0) ?? 0;
        var limit = c.Payload.Int("limit", 1, Protocol.MaxPageSize) ?? Protocol.MaxPageSize;
        var language = Language(c);
        if (store.ListSessions().All(s => s.Id != id))
        {
            throw RemoteException.NotFound("sessionId", gone: false);
        }

        var all = store.GetSessionHistory(id).Select(v => (JsonNode?)BuildPerformanceDetail(v, language)).ToList();
        return Slice(all, offset, limit);
    }

    // ---- Resultados e historial -----------------------------------------------------------------------------------

    private string Language(Call c)
    {
        var requested = c.Payload.Choice("lang", ["es", "es-AR", "en"]);
        return requested ?? _host.Capture().Language;
    }

    private JsonNode? ResultsLast(Call c)
    {
        var language = Language(c);
        var host = _host.Capture();
        if (host.LastResult is { } result)
        {
            return BuildResultDetail(result, language, includeSaved: true);
        }

        // sin resultado en esta ejecución: el último guardado
        var last = _store?.GetAllHistory().LastOrDefault()
                   ?? throw RemoteException.NotFound("result", gone: false);
        var detail = BuildPerformanceDetail(last, language);
        detail["saved"] = true;
        return detail;
    }

    private JsonNode? HistoryPerformance(Call c)
    {
        var id = Req(c, "performanceId");
        var language = Language(c);
        var view = RequireStore().GetPerformance(id) ?? throw RemoteException.NotFound("performanceId", gone: false);
        return BuildPerformanceDetail(view, language);
    }

    private JsonNode? HistoryPlayer(Call c)
    {
        var store = RequireStore();
        var playerId = c.Payload.Long("playerId", required: true)!.Value;
        var offset = c.Payload.Int("offset", 0) ?? 0;
        var limit = c.Payload.Int("limit", 1, Protocol.MaxPageSize) ?? Protocol.MaxPageSize;
        var language = Language(c);
        if (store.GetPlayer(playerId) is null)
        {
            throw RemoteException.NotFound("playerId", gone: false);
        }

        var rows = store.GetPlayerHistory(playerId).Skip(offset).Take(limit + 1).ToList();
        var page = rows.Take(limit).Select(r => store.GetPerformance(r.Performance.Id))
            .Where(v => v is not null)
            .Select(v => (JsonNode?)BuildPerformanceDetail(v!, language)).ToList();
        var end = offset + page.Count;
        return new JsonObject
        {
            ["offset"] = offset,
            ["nextOffset"] = rows.Count > limit ? end : null,
            ["items"] = new JsonArray([.. page]),
        };
    }

    // ---- Reproducción -------------------------------------------------------------------------------------------

    private void EnsureCanStart(HostState host)
    {
        if (host.Status is PlaybackStatuses.Playing or PlaybackStatuses.Paused || host.Screen == Screens.Loading)
        {
            throw BusyError(host);
        }

        if (Screens.IsLocalDecision(host.Screen))
        {
            throw new RemoteException(ErrorCodes.NeedsLocalAction, "A decision is pending on the PC.",
                new JsonObject { ["screen"] = host.Screen });
        }
    }

    private JsonNode? PlaybackStart(Call c)
    {
        var entryId = c.Payload.String("libraryEntryId", min: 1, max: Protocol.MaxIdChars);
        var itemId = c.Payload.String("itemId", min: 1, max: Protocol.MaxIdChars);
        if ((entryId is null) == (itemId is null))
        {
            throw RemoteException.Invalid("libraryEntryId", "Exactly one of 'libraryEntryId' or 'itemId' is required.");
        }

        var host = _host.Capture();
        var policyText = c.Payload.Choice("sessionPolicy", ["current", "new", "solo"]);
        var policy = policyText is null
            ? (SessionPolicy?)null
            : Enum.Parse<SessionPolicy>(policyText, ignoreCase: true);
        var slots = ReadSlots(c, "slots", host);

        QueueEntry? item = null;
        if (itemId is not null)
        {
            item = _queue.Find(itemId, includeRemoved: true) ?? throw RemoteException.NotFound("itemId", gone: false);
            switch (item.State)
            {
                case QueueState.Running:
                    throw new RemoteException(ErrorCodes.ItemRunning, "That item is already being sung.",
                        new JsonObject { ["itemId"] = itemId });
                case not QueueState.Pending:
                    throw RemoteException.NotFound("itemId", gone: true);
            }

            entryId = item.LibraryEntryId;
            slots ??= item.Slots.Count > 0 ? [.. item.Slots.Select(s => new SelectionEntry(s.Slot, s.PlayerId))] : null;
        }

        StartEntry(entryId!, item?.ItemId, slots, policy, autoConfirm: false, c.Payload.String("sessionName", max: Protocol.MaxSessionNameChars));
        return new JsonObject { ["accepted"] = true };
    }

    /// <summary>La misma secuencia para <c>playback.start</c>, <c>playback.next</c> y el avance automático.</summary>
    private void StartEntry(string entryId, string? itemId, IReadOnlyList<SelectionEntry>? slots,
        SessionPolicy? requested, bool autoConfirm, string? sessionName)
    {
        var host = _host.Capture();
        EnsureCanStart(host);

        var item = _catalog.Current.Find(entryId);
        if (item is null)
        {
            throw _store?.GetLibraryEntry(entryId) is not null
                ? new RemoteException(ErrorCodes.EntryUnavailable, "That library entry is no longer available.",
                    new JsonObject { ["libraryEntryId"] = entryId })
                : RemoteException.NotFound("libraryEntryId", gone: false);
        }

        var open = _store?.GetOpenSession();
        var policy = requested ?? (open is not null
            ? SessionPolicy.Current
            : throw new RemoteException(ErrorCodes.SessionRequired,
                "There is no open session: a sessionPolicy of 'new' or 'solo' is required."));
        switch (policy)
        {
            case SessionPolicy.Current when open is null:
                throw new RemoteException(ErrorCodes.SessionRequired, "There is no open session.");
            case SessionPolicy.New when open is not null:
                throw new RemoteException(ErrorCodes.SessionOpen, "There is already an open session.",
                    new JsonObject { ["sessionId"] = open.Id });
            case SessionPolicy.New when _store is null:
                throw RemoteException.State("The history database is not available.", reason: "database-unavailable");
        }

        if (slots is { Count: > 0 })
        {
            ValidatePlayers(slots, host, policy, requireMicrophones: true);
        }

        if (policy == SessionPolicy.New)
        {
            _store!.StartSession(sessionName, slots);
            _host.DataChanged();
        }

        _host.StartSong(new HostStartRequest(item.Entry.Path, item.EntryId, itemId, policy, slots, autoConfirm));
        if (itemId is not null)
        {
            _queue.MarkRunning(itemId);
        }

        _lastAbandonSeq = _host.Capture().AbandonSeq;
        _auto.Cancel();
    }

    private void StartNext(bool autoConfirm, Origin? origin)
    {
        var next = _queue.Pending().FirstOrDefault()
                   ?? throw RemoteException.State("The queue is empty.", reason: "queue-empty");
        var slots = next.Slots.Count > 0
            ? next.Slots.Select(s => new SelectionEntry(s.Slot, s.PlayerId)).ToList()
            : null;
        StartEntry(next.LibraryEntryId, next.ItemId, slots, SessionPolicy.Current, autoConfirm, null);
    }

    private JsonNode? PlaybackConfirm(Call c)
    {
        var host = _host.Capture();
        if (host.Screen != Screens.WhoSings)
        {
            throw RemoteException.State("There is no 'who sings' decision pending.", host.Screen);
        }

        if (host.MicrophoneSlots.Count == 0)
        {
            throw new RemoteException(ErrorCodes.NoMicrophones, "No microphone is set up in Options.");
        }

        var singers = host.Slots.Where(s => host.MicrophoneSlots.Contains(s.Slot)).ToList();
        if (singers.Count == 0)
        {
            throw RemoteException.State("No player is assigned to a microphone.", host.Screen, "no-singers");
        }

        foreach (var singer in singers)
        {
            ActivePlayer(singer.PlayerId);
        }

        _host.ConfirmSingers();
        _auto.Cancel();
        return new JsonObject();
    }

    private JsonNode? PlaybackSimple(Call c, Action<HostState> action, bool cancelAuto = true)
    {
        action(_host.Capture());
        if (cancelAuto)
        {
            _auto.Cancel();
        }

        return new JsonObject();
    }

    private void Pause(HostState host)
    {
        switch (host.Status)
        {
            case PlaybackStatuses.Playing:
                _host.Pause();
                break;
            case PlaybackStatuses.Paused:
                break;
            default:
                throw RemoteException.State("Nothing is playing.", host.Screen);
        }
    }

    private void Resume(HostState host)
    {
        switch (host.Status)
        {
            case PlaybackStatuses.Paused:
                _host.Resume();
                break;
            case PlaybackStatuses.Playing:
                break;
            default:
                throw RemoteException.State("Nothing is paused.", host.Screen);
        }
    }

    private void Restart(HostState host)
    {
        if (host.Status is not (PlaybackStatuses.Playing or PlaybackStatuses.Paused or PlaybackStatuses.Finished))
        {
            throw RemoteException.State("There is nothing to restart.", host.Screen);
        }

        _host.Restart();
        _lastAbandonSeq = _host.Capture().AbandonSeq; // reiniciar no abandona el elemento en curso
    }

    private void Stop(HostState host)
    {
        if (host.Status is not (PlaybackStatuses.Playing or PlaybackStatuses.Paused or PlaybackStatuses.Preparing))
        {
            throw RemoteException.State("There is nothing to stop.", host.Screen);
        }

        _host.Stop();
        if (_queue.Running() is { } running)
        {
            _queue.Cancel(running.ItemId);
        }
    }

    private void Next(HostState host)
    {
        if (host.Status is not (PlaybackStatuses.Idle or PlaybackStatuses.Finished))
        {
            throw host.Status == PlaybackStatuses.Preparing && Screens.IsLocalDecision(host.Screen)
                ? new RemoteException(ErrorCodes.NeedsLocalAction, "A decision is pending on the PC.",
                    new JsonObject { ["screen"] = host.Screen })
                : BusyError(host);
        }

        StartNext(autoConfirm: false, origin: null);
    }

    private void ToMenu(HostState host)
    {
        _host.ShowMenu();
        if (_queue.Running() is { } running)
        {
            _queue.Cancel(running.ItemId);
        }
    }

    private void SkipAnimation(HostState host)
    {
        if (host.Screen != Screens.Results)
        {
            throw RemoteException.State("The results are not on screen.", host.Screen);
        }

        _host.SkipResultsAnimation();
    }

    private void CloseResults(HostState host)
    {
        if (host.Screen != Screens.Results)
        {
            throw RemoteException.State("The results are not on screen.", host.Screen);
        }

        _host.CloseResults();
    }

    private JsonNode? SetAutoAdvance(Call c)
    {
        var enabled = c.Payload.Bool("enabled", required: true)!.Value;
        _host.SetAutoAdvance(enabled);
        if (!enabled)
        {
            _auto.Cancel();
        }

        return new JsonObject { ["enabled"] = enabled };
    }
}
