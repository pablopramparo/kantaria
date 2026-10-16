// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Text.Json.Nodes;
using Kantor.Core.Characters;
using Kantor.Core.Data;
using Kantor.Core.Humor;
using Kantor.Core.Library;
using Kantor.Core.Remote;
using Xunit;

namespace Kantor.Tests;

public sealed class KantorControllerTests : IDisposable
{
    private readonly RemoteHarness _h = new();

    public void Dispose() => _h.Dispose();

    private JsonObject State()
    {
        var reply = _h.Read("kantor.state.get");
        Assert.True(reply.Ok);
        return reply.P;
    }

    private JsonObject Section(string name) => State()[name]!.AsObject();

    private string Add(string song, string? payloadExtra = null) =>
        _h.Send("kantor.queue.add", $$"""{"libraryEntryId":"{{_h.IdOf(song)}}"{{payloadExtra}}}""")
            .P["item"]!["itemId"]!.GetValue<string>();

    private IReadOnlyList<(int Slot, long? Id, string Name, int Score, string Comment)> Singers(int scoreA = 9000,
        int scoreB = 5000) =>
    [
        (1, _h.LongPlayerId("Ana"), "Ana", scoreA, "good_001"),
        (2, _h.LongPlayerId("Beto"), "Beto", scoreB, "trying_001"),
    ];

    private void Sing(string song = "abba-waterloo")
    {
        var start = _h.Send("kantor.playback.start", $$"""{"libraryEntryId":"{{_h.IdOf(song)}}"}""");
        Assert.True(start.Ok, $"start: {start.Code} {start.Details}");
        var confirm = _h.Send("kantor.playback.confirmSingers");
        Assert.True(confirm.Ok, $"confirm: {confirm.Code} {confirm.Details}");
    }

    // ---- Contrato ------------------------------------------------------------------------------------------

    [Fact]
    public void Command_table_has_exactly_the_documented_mutations_and_reads()
    {
        var table = _h.Controller.CommandTable;
        string[] mutations =
        [
            "kantor.queue.add", "kantor.queue.remove", "kantor.queue.move", "kantor.queue.setNext",
            "kantor.queue.setSlots", "kantor.queue.clear", "kantor.players.create", "kantor.players.rename",
            "kantor.players.archive", "kantor.players.setCharacter", "kantor.selection.set", "kantor.session.start",
            "kantor.session.close", "kantor.session.addPlayer", "kantor.session.removePlayer", "kantor.playback.start",
            "kantor.playback.confirmSingers", "kantor.playback.pause", "kantor.playback.resume",
            "kantor.playback.restart", "kantor.playback.stop", "kantor.playback.next", "kantor.navigate.menu",
            "kantor.results.skipAnimation", "kantor.results.close", "kantor.settings.setAutoAdvance",
            "kantor.autoAdvance.cancel", "kantor.screen.setFullscreen",
        ];
        string[] reads =
        [
            "kantor.state.get", "kantor.library.search", "kantor.library.get", "kantor.library.facets",
            "kantor.library.cover", "kantor.queue.list", "kantor.players.list", "kantor.characters.list",
            "kantor.session.get", "kantor.session.list", "kantor.session.history", "kantor.results.last",
            "kantor.history.performance", "kantor.history.player",
        ];

        Assert.Equal(mutations.Order(), table.Where(c => c.Value).Select(c => c.Key).Order());
        Assert.Equal(reads.Order(), table.Where(c => !c.Value).Select(c => c.Key).Order());
    }

    [Fact]
    public void Mutations_require_a_command_id_and_an_origin_but_reads_do_not()
    {
        var id = _h.IdOf("abba-waterloo");
        var payload = $$"""{"libraryEntryId":"{{id}}"}""";

        var noCommand = _h.Send("kantor.queue.add", payload, commandId: null);
        var noOrigin = _h.Send("kantor.queue.add", payload, withOrigin: false);
        var shortId = _h.Send("kantor.queue.add", payload, commandId: "corto");

        Assert.All(new[] { noCommand, noOrigin, shortId }, r =>
        {
            Assert.False(r.Ok);
            Assert.Equal(ErrorCodes.InvalidPayload, r.Code);
        });
        Assert.Empty(_h.Queue.Pending()); // nada se aplicó
        Assert.True(_h.Read("kantor.library.search").Ok);
    }

    [Fact]
    public void Unknown_commands_are_reported_and_rejected_errors_say_nothing_was_applied()
    {
        var unknown = _h.Send("kantor.no.existe");
        var invalid = _h.Send("kantor.queue.remove", """{"itemId":""}""");

        Assert.Equal(ErrorCodes.UnknownCommand, unknown.Code);
        Assert.Equal(ErrorCodes.InvalidPayload, invalid.Code);
        Assert.False(invalid.Details!["applied"]!.GetValue<bool>());
    }

    [Fact]
    public void A_retry_with_the_same_command_id_is_not_executed_twice_and_returns_the_same_answer()
    {
        var payload = $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}"}""";
        var first = _h.Send("kantor.queue.add", payload, commandId: "retry-0001");
        var again = _h.Send("kantor.queue.add", payload, commandId: "retry-0001");

        Assert.True(again.Ok);
        Assert.Equal(first.P["item"]!["itemId"]!.GetValue<string>(), again.P["item"]!["itemId"]!.GetValue<string>());
        Assert.Equal(first.Rev, again.Rev);
        Assert.Single(_h.Queue.Pending());
    }

    [Fact]
    public void The_same_command_id_with_another_payload_is_rejected()
    {
        var id = _h.IdOf("abba-waterloo");
        _h.Send("kantor.queue.add", $$"""{"libraryEntryId":"{{id}}"}""", commandId: "reuse-0001");

        var other = _h.Send("kantor.queue.add", $$"""{"libraryEntryId":"{{_h.IdOf("queen-radio-1")}}"}""",
            commandId: "reuse-0001");

        Assert.Equal(ErrorCodes.CommandIdReused, other.Code);
        Assert.Single(_h.Queue.Pending());
    }

    [Fact]
    public void Published_state_comes_before_the_response_with_the_same_revision()
    {
        _h.Flush();
        _h.DrainOut();

        var reply = _h.Send("kantor.queue.add", $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}"}""");

        var events = _h.DrainOut();
        var changed = Assert.Single(events, m => m["type"]!.GetValue<string>() == "kantor.state.changed");
        Assert.Equal(reply.Rev, changed["rev"]!.GetValue<long>());
        Assert.NotNull(changed["payload"]!["queue"]);
        Assert.Equal(1, changed["payload"]!["queue"]!["pendingCount"]!.GetValue<int>());
    }

    [Fact]
    public void A_mutation_that_changes_nothing_publishes_nothing_and_keeps_the_revision()
    {
        _h.Flush();
        var rev = _h.Controller.Rev;
        _h.DrainOut();

        var reply = _h.Send("kantor.playback.resume", commandId: "noop-0001");

        Assert.False(reply.Ok); // no hay nada pausado
        Assert.Equal(rev, _h.Controller.Rev);
        Assert.Empty(_h.DrainOut());
    }

    [Fact]
    public void Snapshot_has_every_section_and_hello_announces_capabilities_and_limits()
    {
        var state = State();

        foreach (var name in new[]
                 {
                     "instanceId", "screen", "playback", "session", "slots", "queue", "lastResult", "settings",
                     "autoAdvanceCountdown", "library", "display", "attention",
                 })
        {
            Assert.True(state.ContainsKey(name), name);
        }

        Assert.Equal("test-instance", state["instanceId"]!.GetValue<string>());
        var hello = _h.Controller.BuildHello()["payload"]!;
        Assert.Equal(1, hello["protocolVersion"]!.GetValue<int>());
        Assert.Equal(Protocol.MaxFrameBytes, hello["limits"]!["maxFrameBytes"]!.GetValue<int>());
        Assert.Equal(1, hello["capabilities"]!["playback"]!.GetValue<int>());
    }

    // ---- Biblioteca ----------------------------------------------------------------------------------------

    [Fact]
    public void Search_returns_every_edition_as_its_own_entry_with_the_shared_song_identity()
    {
        var reply = _h.Read("kantor.library.search", """{"query":"radio"}""");

        var items = reply.P["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal(2, reply.P["total"]!.GetValue<int>());
        Assert.NotEqual(items[0]!["libraryEntryId"]!.GetValue<string>(), items[1]!["libraryEntryId"]!.GetValue<string>());
        Assert.Equal(items[0]!["songIdentity"]!.GetValue<string>(), items[1]!["songIdentity"]!.GetValue<string>());
        Assert.Equal(["Karaoke", "Original"], items.Select(i => i!["edition"]!.GetValue<string>()).Order());

        var identity = items[0]!["songIdentity"]!.GetValue<string>();
        var byIdentity = _h.Read("kantor.library.search", $$"""{"songIdentity":"{{identity}}"}""");
        Assert.Equal(2, byIdentity.P["items"]!.AsArray().Count);
    }

    [Fact]
    public void Pages_are_cut_by_size_and_walking_them_returns_everything_once()
    {
        var huge = new string('x', 6000);
        var many = Enumerable.Range(0, 120).Select(i =>
            RemoteHarness.Song($"s{i:D3}", "Artista", $"{i:D3} {huge}")).ToList();
        _h.SetLibrary(many);

        var seen = new List<string>();
        var offset = 0;
        var pages = 0;
        while (true)
        {
            var reply = _h.Read("kantor.library.search", $$"""{"offset":{{offset}},"limit":50}""");
            var items = reply.P["items"]!.AsArray();
            Assert.InRange(items.Count, 1, 50);
            Assert.True(reply.P.ToJsonString().Length <= Protocol.SearchPageBudgetBytes + 8_000);
            seen.AddRange(items.Select(i => i!["libraryEntryId"]!.GetValue<string>()));
            pages++;
            if (reply.P["nextOffset"] is null)
            {
                break;
            }

            Assert.Equal(offset + items.Count, reply.P["nextOffset"]!.GetValue<int>());
            offset = reply.P["nextOffset"]!.GetValue<int>();
        }

        Assert.Equal(120, seen.Count);
        Assert.Equal(120, seen.Distinct().Count());
        Assert.True(pages >= 3); // 120 entradas de ~6 KB no entran en 50
    }

    [Fact]
    public void Search_arguments_are_validated()
    {
        Assert.Equal(ErrorCodes.InvalidPayload, _h.Read("kantor.library.search", """{"limit":51}""").Code);
        Assert.Equal(ErrorCodes.InvalidPayload, _h.Read("kantor.library.search", """{"offset":-1}""").Code);
        Assert.Equal(ErrorCodes.InvalidPayload, _h.Read("kantor.library.search", """{"sort":"fecha"}""").Code);
        var longQuery = new string('q', Protocol.MaxQueryChars + 1);
        Assert.Equal(ErrorCodes.InvalidPayload, _h.Read("kantor.library.search", $$"""{"query":"{{longQuery}}"}""").Code);
    }

    [Fact]
    public void Get_tells_a_missing_entry_from_one_that_disappeared()
    {
        var gone = _h.IdOf("abba-waterloo");
        _h.SetLibrary(_h.Catalog.Current.Items.Where(i => i.EntryId != gone).Select(i => i.Entry));

        var missing = _h.Read("kantor.library.get", $$"""{"libraryEntryId":"{{gone}}"}""");
        var never = _h.Read("kantor.library.get", """{"libraryEntryId":"nunca-existio"}""");

        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Equal("gone", missing.Details!["reason"]!.GetValue<string>());
        Assert.Equal("unknown", never.Details!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Cover_is_returned_per_entry_with_its_cover_id_and_validates_size()
    {
        var id = _h.IdOf("queen-radio-1");

        var ok = _h.Read("kantor.library.cover", $$"""{"libraryEntryId":"{{id}}","size":96}""");
        Assert.True(ok.Ok);
        Assert.Equal("image/jpeg", ok.P["mime"]!.GetValue<string>());
        Assert.Equal(96, ok.P["width"]!.GetValue<int>());
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, Convert.FromBase64String(ok.P["dataBase64"]!.GetValue<string>()));
        var song = _h.Read("kantor.library.get", $$"""{"libraryEntryId":"{{id}}"}""");
        Assert.Equal(song.P["coverId"]!.GetValue<string>(), ok.P["coverId"]!.GetValue<string>());

        Assert.Equal(ErrorCodes.InvalidPayload,
            _h.Read("kantor.library.cover", $$"""{"libraryEntryId":"{{id}}","size":100}""").Code);
        var noCover = _h.Read("kantor.library.cover", $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}","size":96}""");
        Assert.Equal(ErrorCodes.NotFound, noCover.Code);
        var unreadable = _h.Read("kantor.library.cover", $$"""{"libraryEntryId":"{{_h.IdOf("lost-cover")}}","size":96}""");
        Assert.Equal(ErrorCodes.NotFound, unreadable.Code);
    }

    [Fact]
    public void Facets_count_entries_and_the_library_section_reports_the_scan_state()
    {
        var facets = _h.Read("kantor.library.facets");

        var genres = facets.P["genres"]!.AsArray().ToDictionary(g => g!["value"]!.GetValue<string>(),
            g => g!["count"]!.GetValue<int>());
        Assert.Equal(2, genres["Rock"]);
        Assert.Equal(1, genres["Pop"]);

        _h.Catalog.Scanning = true;
        _h.Catalog.FolderCount = 2;
        _h.Flush();
        var library = Section("library");
        Assert.Equal("scanning", library["scan"]!.GetValue<string>());
        Assert.Equal(4, library["entryCount"]!.GetValue<int>());
        Assert.Equal(2, library["folderCount"]!.GetValue<int>());
    }

    // ---- Cola ----------------------------------------------------------------------------------------------

    [Fact]
    public void Queue_add_list_and_item_shape()
    {
        var a = Add("abba-waterloo");
        var b = Add("queen-radio-2");

        var list = _h.Read("kantor.queue.list");

        var items = list.P["items"]!.AsArray();
        Assert.Equal([a, b], items.Select(i => i!["itemId"]!.GetValue<string>()));
        Assert.Equal("pending", items[0]!["state"]!.GetValue<string>());
        Assert.Equal("Karaoke", items[1]!["edition"]!.GetValue<string>());
        Assert.True(items[0]!["available"]!.GetValue<bool>());
        Assert.Equal("phone-1", items[0]!["addedBy"]!["clientId"]!.GetValue<string>());
        Assert.True(list.P["persistent"]!.GetValue<bool>());
    }

    [Fact]
    public void Queue_add_distinguishes_unknown_entries_from_ones_no_longer_available()
    {
        var gone = _h.IdOf("abba-waterloo");
        _h.SetLibrary(_h.Catalog.Current.Items.Where(i => i.EntryId != gone).Select(i => i.Entry));

        var unavailable = _h.Send("kantor.queue.add", $$"""{"libraryEntryId":"{{gone}}"}""");
        var unknown = _h.Send("kantor.queue.add", """{"libraryEntryId":"zzz"}""");

        Assert.Equal(ErrorCodes.EntryUnavailable, unavailable.Code);
        Assert.Equal(ErrorCodes.NotFound, unknown.Code);
        Assert.Equal("unknown", unknown.Details!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void A_queued_entry_that_disappears_stays_listed_as_unavailable_and_is_not_replaced_by_another_edition()
    {
        var item = Add("queen-radio-1");
        var original = _h.IdOf("queen-radio-1");
        _h.SetLibrary(_h.Catalog.Current.Items.Where(i => i.EntryId != original).Select(i => i.Entry));

        var listed = _h.Read("kantor.queue.list").P["items"]![0]!;

        Assert.Equal(item, listed["itemId"]!.GetValue<string>());
        Assert.False(listed["available"]!.GetValue<bool>());
        Assert.Equal(original, listed["libraryEntryId"]!.GetValue<string>()); // sigue siendo la misma entrada
        var start = _h.Send("kantor.playback.start", $$"""{"itemId":"{{item}}"}""");
        Assert.Equal(ErrorCodes.EntryUnavailable, start.Code);
    }

    [Fact]
    public void Queue_move_with_a_stale_revision_conflicts_with_the_current_queue_and_remove_reports_gone()
    {
        var a = Add("abba-waterloo");
        var b = Add("queen-radio-1");
        var staleRev = _h.Read("kantor.queue.list").P["queueRev"]!.GetValue<int>();
        Assert.True(_h.Send("kantor.queue.move", $$"""{"itemId":"{{b}}","beforeItemId":"{{a}}"}""").Ok);

        var conflict = _h.Send("kantor.queue.move",
            $$"""{"itemId":"{{a}}","beforeItemId":null,"expectedQueueRev":{{staleRev}}}""");

        Assert.Equal(ErrorCodes.Conflict, conflict.Code);
        Assert.Equal([b, a], conflict.Details!["items"]!.AsArray().Select(i => i!["itemId"]!.GetValue<string>()));

        Assert.True(_h.Send("kantor.queue.remove", $$"""{"itemId":"{{a}}"}""").Ok);
        var again = _h.Send("kantor.queue.remove", $$"""{"itemId":"{{a}}"}""");
        Assert.Equal("gone", again.Details!["reason"]!.GetValue<string>());
        var invalid = _h.Send("kantor.queue.remove", """{"itemId":"inventado"}""");
        Assert.Equal("unknown", invalid.Details!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Queue_clear_needs_confirmation_and_the_current_revision()
    {
        Add("abba-waterloo");
        Add("queen-radio-1");
        var rev = _h.Read("kantor.queue.list").P["queueRev"]!.GetValue<int>();

        Assert.Equal(ErrorCodes.InvalidPayload,
            _h.Send("kantor.queue.clear", $$"""{"confirm":false,"expectedQueueRev":{{rev}}}""").Code);
        Assert.Equal(ErrorCodes.Conflict,
            _h.Send("kantor.queue.clear", $$"""{"confirm":true,"expectedQueueRev":{{rev - 1}}}""").Code);
        var ok = _h.Send("kantor.queue.clear", $$"""{"confirm":true,"expectedQueueRev":{{rev}}}""");

        Assert.Equal(2, ok.P["removed"]!.GetValue<int>());
        Assert.Empty(_h.Queue.Pending());
    }

    [Fact]
    public void Slots_assigned_to_an_item_are_validated_against_players_and_microphones()
    {
        var item = Add("abba-waterloo");
        var ana = _h.LongPlayerId("Ana");

        var ok = _h.Send("kantor.queue.setSlots", $$"""{"itemId":"{{item}}","slots":[{"slot":1,"playerId":{{ana}}}]}""");
        var noMic = _h.Send("kantor.queue.setSlots", $$"""{"itemId":"{{item}}","slots":[{"slot":3,"playerId":{{ana}}}]}""");
        var stranger = _h.Send("kantor.queue.setSlots", $$"""{"itemId":"{{item}}","slots":[{"slot":1,"playerId":999}]}""");
        var repeated = _h.Send("kantor.queue.setSlots",
            $$"""{"itemId":"{{item}}","slots":[{"slot":1,"playerId":{{ana}}},{"slot":2,"playerId":{{ana}}}]}""");

        Assert.True(ok.Ok);
        Assert.Equal(ErrorCodes.SlotUnavailable, noMic.Code);
        Assert.Equal(ErrorCodes.NotFound, stranger.Code);
        Assert.Equal(ErrorCodes.InvalidPayload, repeated.Code);
        Assert.Equal([new QueueSlot(1, ana)], _h.Queue.Find(item)!.Slots);
    }

    [Fact]
    public void The_historic_queue_of_a_closed_session_is_read_only_and_cancelled_items_are_kept()
    {
        var first = _h.Store.GetOpenSession()!.Id;
        var item = Add("abba-waterloo");
        _h.Send("kantor.playback.start", $$"""{"itemId":"{{item}}"}""");
        _h.Send("kantor.playback.stop");
        _h.Store.StartSession(); // otra partida

        var historic = _h.Read("kantor.queue.list", $$"""{"sessionId":{{first}},"includeFinished":true}""");
        var current = _h.Read("kantor.queue.list");

        Assert.Equal("cancelled", historic.P["items"]![0]!["state"]!.GetValue<string>());
        Assert.True(historic.P["readOnly"]!.GetValue<bool>());
        Assert.Empty(current.P["items"]!.AsArray());
        Assert.Equal(ErrorCodes.NotFound, _h.Read("kantor.queue.list", """{"sessionId":9999}""").Code);
    }

    // ---- Jugadores y selección ----------------------------------------------------------------------------

    [Fact]
    public void Players_are_created_renamed_archived_and_never_anonymous()
    {
        var created = _h.Send("kantor.players.create", """{"name":"  Carla "}""");
        var id = created.P["playerId"]!.GetValue<long>();
        Assert.Equal("Carla", created.P["name"]!.GetValue<string>());
        Assert.Equal("avatar_001", created.P["characterId"]!.GetValue<string>());

        Assert.Equal(ErrorCodes.PlayerNameTaken, _h.Send("kantor.players.create", """{"name":"carla"}""").Code);
        Assert.Equal(ErrorCodes.InvalidPayload, _h.Send("kantor.players.create", """{"name":""}""").Code);
        Assert.Equal(ErrorCodes.InvalidPayload,
            _h.Send("kantor.players.create", $$"""{"name":"{{new string('n', 25)}}"}""").Code);
        Assert.Equal(ErrorCodes.NotFound,
            _h.Send("kantor.players.create", """{"name":"Dana","characterId":"nope"}""").Code);

        Assert.True(_h.Send("kantor.players.rename", $$"""{"playerId":{{id}},"name":"Carlita"}""").Ok);
        Assert.Equal(ErrorCodes.PlayerNameTaken,
            _h.Send("kantor.players.rename", $$"""{"playerId":{{id}},"name":"Ana"}""").Code);
        Assert.True(_h.Send("kantor.players.archive", $$"""{"playerId":{{id}}}""").Ok);
        Assert.True(_h.Send("kantor.players.archive", $$"""{"playerId":{{id}}}""").Ok); // idempotente
        Assert.Equal(ErrorCodes.PlayerArchived,
            _h.Send("kantor.players.rename", $$"""{"playerId":{{id}},"name":"Otra"}""").Code);

        var all = _h.Read("kantor.players.list", """{"includeArchived":true}""").P["items"]!.AsArray();
        Assert.Contains(all, p => p!["playerId"]!.GetValue<long>() == id && p["archived"]!.GetValue<bool>());
        Assert.DoesNotContain(_h.Read("kantor.players.list").P["items"]!.AsArray(),
            p => p!["playerId"]!.GetValue<long>() == id);
        Assert.True(_h.Host.DataChangedCount > 0);
    }

    [Fact]
    public void Characters_come_from_the_existing_catalog()
    {
        var items = _h.Read("kantor.characters.list").P["items"]!.AsArray();

        Assert.Equal(CharacterCatalog.FactoryCount, items.Count);
        Assert.Equal("avatar_001", items[0]!["characterId"]!.GetValue<string>());
        var ana = _h.LongPlayerId("Ana");
        Assert.Equal(ErrorCodes.NotFound,
            _h.Send("kantor.players.setCharacter", $$"""{"playerId":{{ana}},"characterId":"inexistente"}""").Code);
        Assert.True(_h.Send("kantor.players.setCharacter", $$"""{"playerId":{{ana}},"characterId":"avatar_001"}""").Ok);
    }

    [Fact]
    public void Selection_validates_microphones_session_membership_and_revision()
    {
        var ana = _h.LongPlayerId("Ana");
        var carla = _h.Store.CreatePlayer("Carla").Id; // existe, pero no está en la partida
        var archived = _h.Store.CreatePlayer("Vieja");
        _h.Store.AddPlayerToSession(_h.Store.GetOpenSession()!.Id, archived.Id);
        _h.Store.ArchivePlayer(archived.Id);

        Assert.True(_h.Send("kantor.selection.set", $$"""{"slots":[{"slot":2,"playerId":{{ana}}}]}""").Ok);
        Assert.Contains("ApplySelection", _h.Host.Calls);

        Assert.Equal(ErrorCodes.SlotUnavailable,
            _h.Send("kantor.selection.set", $$"""{"slots":[{"slot":4,"playerId":{{ana}}}]}""").Code);
        Assert.Equal(ErrorCodes.PlayerNotInSession,
            _h.Send("kantor.selection.set", $$"""{"slots":[{"slot":1,"playerId":{{carla}}}]}""").Code);
        Assert.Equal(ErrorCodes.PlayerArchived,
            _h.Send("kantor.selection.set", $$"""{"slots":[{"slot":1,"playerId":{{archived.Id}}}]}""").Code);

        var rev = Section("slots")["selectionRev"]!.GetValue<int>();
        _h.Host.State = _h.Host.State with { Slots = [] }; // alguien cambió la selección desde la PC
        _h.Flush();
        var stale = _h.Send("kantor.selection.set",
            $$"""{"slots":[{"slot":1,"playerId":{{ana}}}],"expectedSelectionRev":{{rev}}}""");
        Assert.Equal(ErrorCodes.Conflict, stale.Code);
    }

    [Fact]
    public void Slots_never_announce_more_microphones_than_exist()
    {
        _h.Host.State = _h.Host.State with { MicrophoneSlots = [1] };
        _h.Flush();

        var slots = Section("slots");

        Assert.Equal(1, slots["maxSlots"]!.GetValue<int>());
        Assert.Equal([1], slots["availableSlots"]!.AsArray().Select(s => s!.GetValue<int>()));
        var ana = _h.LongPlayerId("Beto");
        Assert.Equal(ErrorCodes.SlotUnavailable,
            _h.Send("kantor.selection.set", $$"""{"slots":[{"slot":2,"playerId":{{ana}}}]}""").Code);

        _h.Host.State = _h.Host.State with { MicrophoneSlots = [] };
        _h.Flush();
        Assert.Equal(ErrorCodes.NoMicrophones,
            _h.Send("kantor.selection.set", $$"""{"slots":[{"slot":1,"playerId":{{ana}}}]}""").Code);
        Assert.Contains(State()["attention"]!.AsArray(), a => a!["code"]!.GetValue<string>() == "NO_MICROPHONES");
    }

    // ---- Partida -------------------------------------------------------------------------------------------

    [Fact]
    public void Only_one_session_is_open_and_starting_another_requires_an_explicit_choice()
    {
        var open = _h.Store.GetOpenSession()!;

        var refused = _h.Send("kantor.session.start", """{"onOpen":"fail"}""");
        Assert.Equal(ErrorCodes.SessionOpen, refused.Code);
        Assert.Equal(open.Id, _h.Store.GetOpenSession()!.Id);

        var started = _h.Send("kantor.session.start", """{"name":"Cumple","onOpen":"closeAndStart"}""");
        var id = started.P["sessionId"]!.GetValue<long>();
        Assert.NotEqual(open.Id, id);
        Assert.Equal("Cumple", started.P["name"]!.GetValue<string>());
        Assert.Equal(id, _h.Store.GetOpenSession()!.Id);
        Assert.Single(_h.Store.ListSessions(), s => s.IsOpen);
        Assert.Empty(started.P["playerIds"]!.AsArray()); // no hereda jugadores
    }

    [Fact]
    public void A_session_cannot_be_changed_while_a_song_is_being_sung()
    {
        Sing();

        Assert.Equal(ErrorCodes.Busy,
            _h.Send("kantor.session.start", """{"onOpen":"closeAndStart"}""").Code);
        var id = _h.Store.GetOpenSession()!.Id;
        Assert.Equal(ErrorCodes.Busy, _h.Send("kantor.session.close", $$"""{"sessionId":{{id}}}""").Code);
        Assert.Equal(ErrorCodes.Busy, _h.Send("kantor.players.archive",
            $$"""{"playerId":{{_h.LongPlayerId("Ana")}}}""").Code); // está cantando
        Assert.Equal(id, _h.Store.GetOpenSession()!.Id);
    }

    [Fact]
    public void Closing_is_explicit_and_idempotent_and_members_follow_the_open_session()
    {
        var id = _h.Store.GetOpenSession()!.Id;
        var carla = _h.Store.CreatePlayer("Carla").Id;

        Assert.True(_h.Send("kantor.session.addPlayer", $$"""{"sessionId":{{id}},"playerId":{{carla}}}""").Ok);
        Assert.Contains(carla, _h.Store.SessionPlayers(id).Select(p => p.Id));
        Assert.True(_h.Send("kantor.session.removePlayer", $$"""{"sessionId":{{id}},"playerId":{{carla}}}""").Ok);

        Assert.True(_h.Send("kantor.session.close", $$"""{"sessionId":{{id}}}""").Ok);
        Assert.True(_h.Send("kantor.session.close", $$"""{"sessionId":{{id}}}""").Ok);
        Assert.Null(_h.Store.GetOpenSession());
        Assert.Equal(ErrorCodes.InvalidState,
            _h.Send("kantor.session.addPlayer", $$"""{"sessionId":{{id}},"playerId":{{carla}}}""").Code);
        Assert.Equal(ErrorCodes.NotFound, _h.Send("kantor.session.close", """{"sessionId":777}""").Code);
        Assert.Null(_h.Read("kantor.session.get").P["session"]);
        Assert.Equal(id, _h.Read("kantor.session.list").P["items"]![0]!["sessionId"]!.GetValue<long>());
    }

    // ---- Reproducción --------------------------------------------------------------------------------------

    [Fact]
    public void Start_defaults_to_the_open_session_and_requires_an_explicit_policy_without_one()
    {
        var waterloo = $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}"}""";
        var ok = _h.Send("kantor.playback.start", waterloo);
        Assert.True(ok.Ok);
        Assert.Equal(SessionPolicy.Current, _h.Host.LastStart!.Policy);
        Assert.Equal(_h.IdOf("abba-waterloo"), _h.Host.LastStart.LibraryEntryId);
        Assert.Equal(@"C:\Songs\abba-waterloo.txt", _h.Host.LastStart.Path);

        _h.RunOnUi(() => _h.Host.State = _h.Host.State with { Screen = Screens.Menu, Status = PlaybackStatuses.Idle });
        _h.Store.CloseSession(_h.Store.GetOpenSession()!.Id);

        Assert.Equal(ErrorCodes.SessionRequired, _h.Send("kantor.playback.start", waterloo).Code);
        Assert.Equal(ErrorCodes.SessionRequired,
            _h.Send("kantor.playback.start", $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}","sessionPolicy":"current"}""").Code);

        var solo = _h.Send("kantor.playback.start",
            $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}","sessionPolicy":"solo"}""");
        Assert.True(solo.Ok);
        Assert.Equal(SessionPolicy.Solo, _h.Host.LastStart.Policy);
    }

    [Fact]
    public void Starting_with_a_new_session_creates_it_but_never_replaces_an_open_one()
    {
        var id = $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}","sessionPolicy":"new"}""";

        Assert.Equal(ErrorCodes.SessionOpen, _h.Send("kantor.playback.start", id).Code);
        Assert.DoesNotContain("StartSong", _h.Host.Calls);

        _h.Store.CloseSession(_h.Store.GetOpenSession()!.Id);
        var ana = _h.LongPlayerId("Ana");
        var ok = _h.Send("kantor.playback.start",
            $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}","sessionPolicy":"new","slots":[{"slot":1,"playerId":{{ana}}}]}""");

        Assert.True(ok.Ok);
        var session = _h.Store.GetOpenSession()!;
        Assert.Equal([ana], _h.Store.SessionPlayers(session.Id).Select(p => p.Id));
    }

    [Fact]
    public void Start_is_refused_while_busy_or_while_a_local_decision_is_pending()
    {
        var payload = $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}"}""";
        _h.Host.State = _h.Host.State with { Status = PlaybackStatuses.Playing, Screen = Screens.Stage };
        Assert.Equal(ErrorCodes.Busy, _h.Send("kantor.playback.start", payload).Code);

        _h.Host.State = _h.Host.State with { Status = PlaybackStatuses.Idle, Screen = Screens.Loading };
        Assert.Equal(ErrorCodes.Busy, _h.Send("kantor.playback.start", payload).Code);

        foreach (var screen in new[] { Screens.Options, Screens.Players, Screens.SessionGate, Screens.WhoPlays, Screens.WhoSings })
        {
            _h.Host.State = _h.Host.State with { Status = PlaybackStatuses.Idle, Screen = screen };
            var refused = _h.Send("kantor.playback.start", payload);
            Assert.Equal(ErrorCodes.NeedsLocalAction, refused.Code);
            Assert.Equal(screen, refused.Details!["screen"]!.GetValue<string>());
        }

        Assert.DoesNotContain("StartSong", _h.Host.Calls);
    }

    [Fact]
    public void Start_takes_exactly_one_of_entry_or_item()
    {
        Assert.Equal(ErrorCodes.InvalidPayload, _h.Send("kantor.playback.start", "{}").Code);
        var both = $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}","itemId":"x"}""";
        Assert.Equal(ErrorCodes.InvalidPayload, _h.Send("kantor.playback.start", both).Code);
        Assert.Equal(ErrorCodes.NotFound, _h.Send("kantor.playback.start", """{"itemId":"fantasma"}""").Code);
    }

    [Fact]
    public void Confirming_singers_needs_the_who_sings_screen_and_assigned_microphones()
    {
        Assert.Equal(ErrorCodes.InvalidState, _h.Send("kantor.playback.confirmSingers").Code);

        _h.Send("kantor.playback.start", $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}"}""");
        Assert.Equal(Screens.WhoSings, State()["screen"]!.GetValue<string>());
        Assert.Equal("preparing", Section("playback")["status"]!.GetValue<string>());

        _h.Host.State = _h.Host.State with { MicrophoneSlots = [] };
        Assert.Equal(ErrorCodes.NoMicrophones, _h.Send("kantor.playback.confirmSingers").Code);

        _h.Host.State = _h.Host.State with { MicrophoneSlots = [1, 2], Slots = [] };
        Assert.Equal(ErrorCodes.InvalidState, _h.Send("kantor.playback.confirmSingers").Code);

        _h.Host.State = _h.Host.State with
        {
            Slots = [new HostSlot(1, _h.LongPlayerId("Ana"), "Ana", true, 0, 0)],
        };
        Assert.True(_h.Send("kantor.playback.confirmSingers").Ok);
        Assert.Equal("playing", Section("playback")["status"]!.GetValue<string>());
        // la PC ya había confirmado: gana el primero
        Assert.Equal(ErrorCodes.InvalidState, _h.Send("kantor.playback.confirmSingers").Code);
    }

    [Fact]
    public void Pause_resume_stop_and_restart_follow_the_documented_states()
    {
        Assert.Equal(ErrorCodes.InvalidState, _h.Send("kantor.playback.pause").Code);
        Assert.Equal(ErrorCodes.InvalidState, _h.Send("kantor.playback.restart").Code);
        Assert.Equal(ErrorCodes.InvalidState, _h.Send("kantor.playback.stop").Code);
        Sing();

        Assert.True(_h.Send("kantor.playback.pause").Ok);
        Assert.True(_h.Send("kantor.playback.pause").Ok); // ya pausada: estado deseado, sin cambio
        Assert.Equal("paused", Section("playback")["status"]!.GetValue<string>());
        Assert.True(_h.Send("kantor.playback.resume").Ok);
        Assert.True(_h.Send("kantor.playback.resume").Ok);
        Assert.Equal(1, _h.Host.Calls.Count(c => c == "Pause"));
        Assert.Equal(1, _h.Host.Calls.Count(c => c == "Resume"));

        var before = Section("playback")["performanceId"]!.GetValue<string>();
        Assert.True(_h.Send("kantor.playback.restart").Ok);
        Assert.NotEqual(before, Section("playback")["performanceId"]!.GetValue<string>());
        Assert.Equal("playing", Section("playback")["status"]!.GetValue<string>());
    }

    [Fact]
    public void Restart_from_the_results_runs_the_same_song_again_without_a_queue_item()
    {
        Sing();
        _h.RunOnUi(() => _h.Host.Finish(Singers()));
        _h.Flush();

        Assert.True(_h.Send("kantor.playback.restart").Ok);

        var playback = Section("playback");
        Assert.Equal("playing", playback["status"]!.GetValue<string>());
        Assert.Null(playback["queueItemId"]);
        Assert.Single(_h.Store.GetAllHistory()); // el resultado anterior sigue y no se duplicó
    }

    [Fact]
    public void Stop_and_menu_cancel_the_running_queue_item_and_nothing_is_saved()
    {
        var first = Add("abba-waterloo");
        var second = Add("queen-radio-1");

        _h.Send("kantor.playback.start", $$"""{"itemId":"{{first}}"}""");
        Assert.Equal("running", _h.Queue.Find(first)!.State.ToString().ToLowerInvariant());
        Assert.True(_h.Send("kantor.playback.stop").Ok);
        Assert.Equal(QueueState.Cancelled, _h.Queue.Find(first)!.State);

        _h.RunOnUi(() => _h.Host.State = _h.Host.State with { Screen = Screens.Menu, Status = PlaybackStatuses.Idle });
        _h.Send("kantor.playback.start", $$"""{"itemId":"{{second}}"}""");
        Assert.True(_h.Send("kantor.navigate.menu").Ok);
        Assert.Equal(QueueState.Cancelled, _h.Queue.Find(second)!.State);
        Assert.Empty(_h.Store.GetAllHistory());
    }

    [Fact]
    public void A_local_abandon_also_cancels_the_running_item()
    {
        var item = Add("abba-waterloo");
        _h.Send("kantor.playback.start", $$"""{"itemId":"{{item}}"}""");
        Assert.Equal(QueueState.Running, _h.Queue.Find(item)!.State);

        _h.RunOnUi(() => _h.Host.ShowMenu()); // la persona volvió al menú en la PC
        _h.Flush();

        Assert.Equal(QueueState.Cancelled, _h.Queue.Find(item)!.State);
    }

    [Fact]
    public void Another_song_loaded_locally_cancels_the_running_item()
    {
        var item = Add("abba-waterloo");
        _h.Send("kantor.playback.start", $$"""{"itemId":"{{item}}"}""");

        _h.RunOnUi(() => _h.Host.State = _h.Host.State with
        {
            LoadedPath = @"C:\Songs\otra.txt", Screen = Screens.Stage, Status = PlaybackStatuses.Idle,
        });
        _h.Flush();

        Assert.Equal(QueueState.Cancelled, _h.Queue.Find(item)!.State);
    }

    [Fact]
    public void A_finished_song_completes_the_item_with_its_performance_and_announces_the_result_once()
    {
        var item = Add("abba-waterloo");
        _h.Send("kantor.playback.start", $$"""{"itemId":"{{item}}"}""");
        _h.Send("kantor.playback.confirmSingers");
        _h.Flush();
        _h.DrainOut();

        _h.RunOnUi(() => _h.Host.Finish(Singers()));
        _h.Flush();
        _h.Flush();

        var entry = _h.Queue.Find(item)!;
        Assert.Equal(QueueState.Completed, entry.State);
        Assert.Equal(_h.Host.State.RunId, entry.PerformanceId);
        var results = _h.DrainOut().Where(m => m["type"]!.GetValue<string>() == "kantor.result").ToList();
        var result = Assert.Single(results);
        Assert.Equal(2, result["payload"]!["results"]!.AsArray().Count);
        Assert.True(result["payload"]!["saved"]!.GetValue<bool>());
        Assert.Equal("finished", Section("playback")["status"]!.GetValue<string>());
        Assert.NotNull(Section("lastResult")["performanceId"]);
    }

    [Fact]
    public void Playback_next_takes_the_first_pending_and_is_refused_with_an_empty_queue_or_while_playing()
    {
        var empty = _h.Send("kantor.playback.next");
        Assert.Equal(ErrorCodes.InvalidState, empty.Code);
        Assert.Equal("queue-empty", empty.Details!["reason"]!.GetValue<string>());

        var a = Add("abba-waterloo");
        Add("queen-radio-1");
        Assert.True(_h.Send("kantor.playback.next").Ok);
        Assert.Equal(QueueState.Running, _h.Queue.Find(a)!.State);
        Assert.Equal(ErrorCodes.NeedsLocalAction, _h.Send("kantor.playback.next").Code); // falta confirmar cantantes
        Assert.True(_h.Send("kantor.playback.confirmSingers").Ok);
        Assert.Equal(ErrorCodes.Busy, _h.Send("kantor.playback.next").Code);
    }

    [Fact]
    public void Remote_commands_cannot_touch_audio_configuration()
    {
        foreach (var type in new[]
                 {
                     "kantor.audio.setEngine", "kantor.audio.setDevice", "kantor.settings.setDifficulty",
                     "kantor.settings.setDelay", "kantor.calibration.start", "kantor.microphones.set",
                 })
        {
            Assert.Equal(ErrorCodes.UnknownCommand, _h.Send(type).Code);
        }
    }

    // ---- Resultados e historial ----------------------------------------------------------------------------

    [Fact]
    public void Results_keep_the_comments_that_were_shown_and_localise_only_the_text()
    {
        Sing();
        _h.RunOnUi(() => _h.Host.Finish(Singers(9000, 5000)));
        _h.Flush();
        var es = HumorBank.Load("es");
        var en = HumorBank.Load("en");

        var spanish = _h.Read("kantor.results.last", """{"lang":"es"}""").P;
        var english = _h.Read("kantor.results.last", """{"lang":"en"}""").P;

        var first = spanish["results"]![0]!;
        Assert.Equal("good_001", first["commentId"]!.GetValue<string>());
        Assert.Equal(es.Text("good_001"), first["comment"]!.GetValue<string>());
        Assert.Equal(en.Text("good_001"), english["results"]![0]!["comment"]!.GetValue<string>());
        Assert.Equal(1, first["position"]!.GetValue<int>());
        Assert.Equal(1, first["competitionPoints"]!.GetValue<int>());
        Assert.Equal("Ana", first["name"]!.GetValue<string>());
        Assert.Equal(2, spanish["results"]![1]!["position"]!.GetValue<int>());
        Assert.Equal("superstar", first["seal"]!["rankId"]!.GetValue<string>()); // 9000 = ¡SUPERSTAR!
        Assert.False(string.IsNullOrEmpty(first["seal"]!["text"]!.GetValue<string>()));

        // consultar el historial devuelve lo mismo y no vuelve a sortear
        var performanceId = spanish["performanceId"]!.GetValue<string>();
        var again = _h.Read("kantor.history.performance", $$"""{"performanceId":"{{performanceId}}","lang":"es"}""").P;
        Assert.Equal("good_001", again["results"]![0]!["commentId"]!.GetValue<string>());
        Assert.Equal("trying_001", again["results"]![1]!["commentId"]!.GetValue<string>());
    }

    [Fact]
    public void Ties_share_the_position_and_the_last_place_does_not_score()
    {
        Sing();
        var ana = _h.LongPlayerId("Ana");
        var beto = _h.LongPlayerId("Beto");
        _h.RunOnUi(() => _h.Host.Finish(
            [(1, ana, "Ana", 7000, "good_003"), (2, beto, "Beto", 7000, "good_004")]));
        _h.Flush();

        var results = _h.Read("kantor.results.last").P["results"]!.AsArray();

        Assert.Equal([1, 1], results.Select(r => r!["position"]!.GetValue<int>()));
        Assert.Equal([1, 1], results.Select(r => r!["competitionPoints"]!.GetValue<int>())); // todos empatados: N − 1
    }

    [Fact]
    public void A_solo_result_is_reported_as_not_saved_and_leaves_no_history()
    {
        _h.Send("kantor.playback.start",
            $$"""{"libraryEntryId":"{{_h.IdOf("abba-waterloo")}}","sessionPolicy":"solo","slots":[{"slot":1,"playerId":{{_h.LongPlayerId("Ana")}}}]}""");
        _h.Send("kantor.playback.confirmSingers");
        _h.Flush();
        _h.DrainOut();
        _h.RunOnUi(() => _h.Host.Finish([(1, _h.LongPlayerId("Ana"), "Ana", 8000, "good_002")]));
        _h.Flush();

        var result = _h.Read("kantor.results.last").P;

        Assert.False(result["saved"]!.GetValue<bool>());
        Assert.Null(result["performanceId"]);
        Assert.Equal("good_002", result["results"]![0]!["commentId"]!.GetValue<string>());
        Assert.Empty(_h.Store.GetAllHistory());
        var announced = _h.DrainOut().Single(m => m["type"]!.GetValue<string>() == "kantor.result");
        Assert.False(announced["payload"]!["saved"]!.GetValue<bool>());
        Assert.Contains(State()["attention"]!.AsArray(), a => a!["code"]!.GetValue<string>() == "SOLO_NOT_SAVED");
    }

    [Fact]
    public void Session_and_player_history_and_unknown_ids()
    {
        Sing();
        _h.RunOnUi(() => _h.Host.Finish(Singers()));
        _h.Flush();
        var session = _h.Store.GetOpenSession()!.Id;
        var ana = _h.LongPlayerId("Ana");

        var bySession = _h.Read("kantor.session.history", $$"""{"sessionId":{{session}},"limit":10}""").P;
        var byPlayer = _h.Read("kantor.history.player", $$"""{"playerId":{{ana}}}""").P;

        Assert.Single(bySession["items"]!.AsArray());
        Assert.Null(bySession["nextOffset"]);
        Assert.Single(byPlayer["items"]!.AsArray());
        Assert.Equal(ErrorCodes.NotFound, _h.Read("kantor.history.performance", """{"performanceId":"nada"}""").Code);
        Assert.Equal(ErrorCodes.NotFound, _h.Read("kantor.history.player", """{"playerId":4242}""").Code);
        Assert.Equal(ErrorCodes.InvalidPayload,
            _h.Read("kantor.history.player", $$"""{"playerId":{{ana}},"lang":"fr"}""").Code);
    }

    [Fact]
    public void Results_on_the_screen_can_be_skipped_and_closed_only_from_the_results()
    {
        Assert.Equal(ErrorCodes.InvalidState, _h.Send("kantor.results.skipAnimation").Code);
        Assert.Equal(ErrorCodes.InvalidState, _h.Send("kantor.results.close").Code);

        Sing();
        _h.RunOnUi(() => _h.Host.Finish(Singers()));
        Assert.True(_h.Send("kantor.results.skipAnimation").Ok);
        Assert.False(_h.Host.State.ResultsAnimating);
        Assert.True(_h.Send("kantor.results.close").Ok);
        Assert.Equal(Screens.Menu, _h.Host.State.Screen);
    }

    // ---- Pantalla ------------------------------------------------------------------------------------------

    [Fact]
    public void Fullscreen_is_a_desired_state_and_display_exposes_what_the_agent_needs()
    {
        _h.Host.State = _h.Host.State with
        {
            Display = new HostDisplay(false, 4242, "0x00040A1C",
                new HostMonitor(@"\\.\DISPLAY2", false, 1920, 0, 2560, 1440, 1.25)),
        };
        _h.Flush();

        Assert.True(_h.Send("kantor.screen.setFullscreen", """{"enabled":true}""").Ok);
        Assert.True(_h.Send("kantor.screen.setFullscreen", """{"enabled":true}""").Ok);
        _h.Now = TimeSpan.FromSeconds(1); // la ventana ya quedó quieta
        _h.Flush();

        var display = Section("display");
        Assert.True(display["fullscreen"]!.GetValue<bool>());
        Assert.Equal(4242, display["processId"]!.GetValue<int>());
        Assert.Equal("0x00040A1C", display["windowHandle"]!.GetValue<string>());
        var monitor = display["monitor"]!;
        Assert.Equal(@"\\.\DISPLAY2", monitor["deviceName"]!.GetValue<string>());
        Assert.Equal(1920, monitor["bounds"]!["x"]!.GetValue<int>());
        Assert.Equal(2560, monitor["bounds"]!["width"]!.GetValue<int>());
        Assert.Equal(1.25, monitor["dpiScale"]!.GetValue<double>());
    }

    [Fact]
    public void Display_changes_are_published_only_after_the_window_stops_moving()
    {
        HostDisplay At(int x) => new(true, 1, "0x1", new HostMonitor("D1", true, x, 0, 1920, 1080, 1.0));
        _h.Host.State = _h.Host.State with { Display = At(0) };
        _h.Now = TimeSpan.Zero;
        _h.Flush();
        _h.DrainOut();

        bool DisplayPublished() => _h.DrainOut().Any(m =>
            m["type"]!.GetValue<string>() == "kantor.state.changed" && m["payload"]!.AsObject().ContainsKey("display"));

        _h.Now = TimeSpan.FromSeconds(1.0);
        _h.Host.State = _h.Host.State with { Display = At(100) };
        _h.Flush();
        Assert.False(DisplayPublished());

        _h.Now = TimeSpan.FromSeconds(1.15);
        _h.Host.State = _h.Host.State with { Display = At(200) }; // sigue moviéndose
        _h.Flush();
        _h.Now = TimeSpan.FromSeconds(1.30);
        _h.Flush();
        Assert.False(DisplayPublished());

        _h.Now = TimeSpan.FromSeconds(1.45); // 300 ms quieta
        _h.Flush();
        Assert.True(DisplayPublished());
        Assert.Equal(200, Section("display")["monitor"]!["bounds"]!["x"]!.GetValue<int>());
    }

    // ---- Posición ------------------------------------------------------------------------------------------

    [Fact]
    public void Position_is_not_published_every_tick_but_a_seek_is_published_at_once()
    {
        Sing();
        _h.Now = TimeSpan.Zero;
        _h.Host.State = _h.Host.State with { PositionSeconds = 10 };
        _h.Flush();
        _h.DrainOut();

        bool PlaybackPublished() => _h.DrainOut().Any(m =>
            m["type"]!.GetValue<string>() == "kantor.state.changed" && m["payload"]!.AsObject().ContainsKey("playback"));

        _h.Now = TimeSpan.FromSeconds(1);
        _h.Host.State = _h.Host.State with { PositionSeconds = 11 };
        _h.Flush();
        Assert.False(PlaybackPublished());

        _h.Now = TimeSpan.FromSeconds(2);
        _h.Host.State = _h.Host.State with { PositionSeconds = 60 }; // un salto
        _h.Flush();
        Assert.True(PlaybackPublished());
        Assert.Equal(60, Section("playback")["positionSeconds"]!.GetValue<double>());
        Assert.Equal(1, Section("playback")["rate"]!.GetValue<int>());
    }

    // ---- Avance automático ---------------------------------------------------------------------------------

    private string ArmedWithNextItem(bool withSlots = true)
    {
        Assert.True(_h.Send("kantor.settings.setAutoAdvance", """{"enabled":true}""").Ok);
        var next = Add("queen-radio-1");
        if (withSlots)
        {
            var ana = _h.LongPlayerId("Ana");
            var beto = _h.LongPlayerId("Beto");
            Assert.True(_h.Send("kantor.queue.setSlots",
                $$"""{"itemId":"{{next}}","slots":[{"slot":1,"playerId":{{ana}}},{"slot":2,"playerId":{{beto}}}]}""").Ok);
        }

        Sing();
        _h.Now = TimeSpan.Zero;
        _h.RunOnUi(() => _h.Host.Finish(Singers()));
        _h.Flush(); // animando: todavía no hay cuenta
        Assert.Null(Countdown());
        _h.Host.State = _h.Host.State with { ResultsAnimating = false };
        _h.Flush(); // terminó la animación: arranca la cuenta
        return next;
    }

    private JsonNode? Countdown() => State()["autoAdvanceCountdown"];

    [Fact]
    public void Auto_advance_counts_ten_seconds_publishes_the_count_and_starts_the_next_with_confirmed_singers()
    {
        var next = ArmedWithNextItem();

        var count = Countdown()!;
        Assert.Equal(10, count["secondsRemaining"]!.GetValue<double>());
        Assert.Equal(next, count["itemId"]!.GetValue<string>());
        Assert.True(State()["settings"]!["autoAdvance"]!.GetValue<bool>());

        _h.Now = TimeSpan.FromSeconds(4);
        _h.Flush();
        Assert.Equal(6, Countdown()!["secondsRemaining"]!.GetValue<double>(), 1);

        _h.Now = TimeSpan.FromSeconds(10);
        _h.Flush();

        Assert.Equal(next, _h.Host.LastStart!.QueueItemId);
        Assert.True(_h.Host.LastStart.AutoConfirmSingers);
        Assert.Equal(SessionPolicy.Current, _h.Host.LastStart.Policy);
        Assert.Equal(QueueState.Running, _h.Queue.Find(next)!.State);
        Assert.Null(Countdown());
    }

    [Fact]
    public void The_countdown_does_not_publish_a_new_revision_every_tick()
    {
        ArmedWithNextItem();
        _h.DrainOut();
        var rev = _h.Controller.Rev;

        for (var second = 1; second <= 8; second++)
        {
            _h.Now = TimeSpan.FromSeconds(second);
            _h.Flush();
        }

        Assert.Equal(rev, _h.Controller.Rev);
        Assert.DoesNotContain(_h.DrainOut(), m => m["type"]!.GetValue<string>() == "kantor.state.changed");
    }

    [Fact]
    public void Explicit_cancel_stops_the_count_and_nothing_starts()
    {
        ArmedWithNextItem();

        Assert.True(_h.Send("kantor.autoAdvance.cancel").Ok);
        _h.Now = TimeSpan.FromSeconds(30);
        _h.Flush();

        Assert.Null(Countdown());
        Assert.Equal(1, _h.Host.Calls.Count(c => c == "StartSong")); // solo la de antes
    }

    [Fact]
    public void A_local_key_or_click_cancels_but_remote_queue_and_library_activity_does_not()
    {
        ArmedWithNextItem();

        Add("abba-waterloo"); // otro teléfono agrega a la cola
        _h.Read("kantor.library.search");
        _h.Read("kantor.players.list");
        _h.Send("kantor.players.create", """{"name":"Dana"}""");
        _h.Now = TimeSpan.FromSeconds(3);
        _h.Flush();
        Assert.NotNull(Countdown());

        _h.RunOnUi(() => _h.Host.LocalInteraction());
        _h.Flush();

        Assert.Null(Countdown());
        _h.Now = TimeSpan.FromSeconds(30);
        _h.Flush();
        Assert.Equal(1, _h.Host.Calls.Count(c => c == "StartSong"));
    }

    [Fact]
    public void Results_close_menu_and_any_playback_command_cancel_the_count()
    {
        foreach (var cancel in new[] { "kantor.results.close", "kantor.navigate.menu", "kantor.playback.restart" })
        {
            using var h = new RemoteHarness();
            h.Send("kantor.settings.setAutoAdvance", """{"enabled":true}""");
            var next = h.Send("kantor.queue.add", $$"""{"libraryEntryId":"{{h.IdOf("queen-radio-1")}}"}""")
                .P["item"]!["itemId"]!.GetValue<string>();
            var ana = h.LongPlayerId("Ana");
            h.Send("kantor.queue.setSlots", $$"""{"itemId":"{{next}}","slots":[{"slot":1,"playerId":{{ana}}}]}""");
            h.Send("kantor.playback.start", $$"""{"libraryEntryId":"{{h.IdOf("abba-waterloo")}}"}""");
            h.Send("kantor.playback.confirmSingers");
            h.RunOnUi(() => h.Host.Finish([(1, ana, "Ana", 8000, "good_001")], animating: false));
            h.Flush();
            Assert.NotNull(h.Read("kantor.state.get").P["autoAdvanceCountdown"]);

            var reply = h.Send(cancel);
            Assert.True(reply.Ok, cancel);
            h.Now = TimeSpan.FromSeconds(60);
            h.Flush();

            Assert.Equal(1, h.Host.Calls.Count(c => c == "StartSong")); // la del principio, no la automática
        }
    }

    [Fact]
    public void A_rejected_command_does_not_cancel_the_count_and_turning_the_setting_off_does()
    {
        ArmedWithNextItem();

        Assert.False(_h.Send("kantor.playback.resume").Ok); // INVALID_STATE: no cambió nada
        _h.Flush();
        Assert.NotNull(Countdown());

        Assert.True(_h.Send("kantor.settings.setAutoAdvance", """{"enabled":false}""").Ok);
        Assert.Null(Countdown());
        Assert.False(State()["settings"]!["autoAdvance"]!.GetValue<bool>());
    }

    [Fact]
    public void Auto_advance_never_starts_without_a_valid_next_song_and_singers_and_says_why()
    {
        Assert.True(_h.Send("kantor.settings.setAutoAdvance", """{"enabled":true}""").Ok);
        Add("queen-radio-1"); // pendiente SIN jugadores asignados
        Sing();
        _h.Host.State = _h.Host.State with { Slots = [] }; // y la selección vigente está vacía
        _h.RunOnUi(() => _h.Host.Finish(Singers(), animating: false));
        _h.Flush();

        Assert.Null(Countdown());
        var attention = State()["attention"]!.AsArray();
        var blocked = Assert.Single(attention, a => a!["code"]!.GetValue<string>() == "AUTO_ADVANCE_BLOCKED");
        Assert.Equal("no-singers", blocked!["params"]!["reason"]!.GetValue<string>());
        _h.Now = TimeSpan.FromSeconds(60);
        _h.Flush();
        Assert.Equal(1, _h.Host.Calls.Count(c => c == "StartSong"));
    }

    [Fact]
    public void Auto_advance_uses_the_current_selection_when_the_item_has_no_slots_of_its_own()
    {
        ArmedWithNextItem(withSlots: false);

        Assert.NotNull(Countdown()); // la selección vigente (Ana y Beto) es válida y está confirmada

        _h.Now = TimeSpan.FromSeconds(10);
        _h.Flush();
        Assert.True(_h.Host.LastStart!.AutoConfirmSingers);
        Assert.Null(_h.Host.LastStart.Selection); // sin puestos propios: la selección vigente sigue
    }

    [Fact]
    public void An_archived_singer_cancels_a_running_count()
    {
        ArmedWithNextItem();
        _h.Store.ArchivePlayer(_h.LongPlayerId("Beto")); // alguien lo archivó desde otro lado

        _h.Flush();

        Assert.Null(Countdown());
        Assert.Contains(State()["attention"]!.AsArray(), a => a!["code"]!.GetValue<string>() == "AUTO_ADVANCE_BLOCKED");
        _h.Now = TimeSpan.FromSeconds(60);
        _h.Flush();
        Assert.Equal(1, _h.Host.Calls.Count(c => c == "StartSong"));
    }

    // ---- Atención ------------------------------------------------------------------------------------------

    [Fact]
    public void Attention_uses_translatable_codes_without_technical_messages()
    {
        _h.Host.State = _h.Host.State with
        {
            StoreAvailable = false,
            HasLibraryFolders = false,
            Attention = [new HostAttention("AUDIO_ENGINE_FALLBACK",
                new Dictionary<string, string> { ["requested"] = "asio", ["active"] = "windows" })],
        };
        _h.Flush();

        var attention = State()["attention"]!.AsArray();
        var codes = attention.Select(a => a!["code"]!.GetValue<string>()).ToList();

        Assert.Contains("DATABASE_UNAVAILABLE", codes);
        Assert.Contains("NO_LIBRARY_FOLDERS", codes);
        Assert.Contains("AUDIO_ENGINE_FALLBACK", codes);
        Assert.All(attention, a => Assert.False(a!.AsObject().ContainsKey("message")));
    }
}
