// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kantor.Core.Characters;
using Kantor.Core.Guest;
using Kantor.Core.Remote;

namespace Kantor.Tests;

public class GuestPlayerCreationTests
{
    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> Create(
        GuestHarness h, string name, string? avatar = "avatar_003", string? requestId = null, string? token = "valid")
    {
        var avatarJson = avatar is null ? "null" : JsonValue.Create(avatar)!.ToJsonString();
        var json = $$"""{"name":{{JsonValue.Create(name)!.ToJsonString()}},"avatar":{{avatarJson}},"requestId":"{{requestId ?? Guid.NewGuid().ToString("N")}}"}""";
        return await h.SendAsync(HttpMethod.Post, "/api/players", json, token);
    }

    private static string Error(string body) => GuestHarness.Json(body)["error"]!.GetValue<string>();

    private static JsonObject PlayerOf(string body) => (JsonObject)GuestHarness.Json(body)["player"]!;

    // ---- Creación y persistencia -------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_player_is_a_normal_persistent_kantor_player_in_the_open_session()
    {
        using var h = new GuestHarness();
        h.Remote.Host.State = h.Remote.Host.State with { Slots = [] };
        var selectionBefore = h.Remote.Host.State.Slots;

        var r = await Create(h, "  Carla  ", "avatar_003");

        Assert.Equal(HttpStatusCode.OK, r.Status);
        var json = GuestHarness.Json(r.Body);
        Assert.True(json["created"]!.GetValue<bool>());
        Assert.False(json["duplicate"]!.GetValue<bool>());
        var id = PlayerOf(r.Body)["id"]!.GetValue<long>();
        Assert.Equal("Carla", PlayerOf(r.Body)["name"]!.GetValue<string>());
        Assert.Equal("avatar_003", PlayerOf(r.Body)["avatar"]!.GetValue<string>());

        var stored = h.Remote.Store.GetPlayer(id)!; // el mismo registro que crea la PC
        Assert.Equal("Carla", stored.Name);
        Assert.Equal("avatar_003", stored.CharacterId);
        Assert.False(stored.Archived);
        Assert.Contains(h.Remote.Store.SessionPlayers(h.Remote.Store.GetOpenSession()!.Id), p => p.Id == id);
        Assert.Equal(selectionBefore, h.Remote.Host.State.Slots); // no toca los micrófonos asignados
    }

    [Fact]
    public async Task The_new_player_is_available_at_once_to_every_guest_the_pc_and_avyn()
    {
        using var h = new GuestHarness();
        var r = await Create(h, "Dani");
        var id = PlayerOf(r.Body)["id"]!.GetValue<long>();

        var names = GuestHarness.Json((await h.GetAsync("/api/players")).Body)["players"]!.AsArray()
            .Select(p => p!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Dani", names);
        var avyn = h.Remote.Read("kantor.players.list").P["items"]!.AsArray();
        Assert.Contains(avyn, p => p!["playerId"]!.GetValue<long>() == id);
        Assert.Equal(1, h.Remote.Host.PlayersAddedCount); // la PC se entera, sin reiniciar la canción en curso
        Assert.Equal(0, h.Remote.Host.DataChangedCount);
    }

    [Fact]
    public async Task The_roster_tells_the_page_the_name_limit()
    {
        using var h = new GuestHarness();
        Assert.Equal(24, GuestHarness.Json((await h.GetAsync("/api/players")).Body)["maxName"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_new_player_can_be_picked_to_sing_and_queued_without_queueing_on_creation()
    {
        using var h = new GuestHarness();
        var created = await Create(h, "Eva");
        Assert.Empty(h.Remote.Queue.Pending()); // crear no agrega la canción
        var id = PlayerOf(created.Body)["id"]!.GetValue<long>();

        var queued = await h.PostQueueAsync(h.SongId("abba-waterloo"), [id]);

        Assert.Equal(HttpStatusCode.OK, queued.Status);
        Assert.Equal([new Kantor.Core.Data.QueueSlot(1, id)], h.Remote.Queue.Pending().Single().Slots);
    }

    [Fact]
    public async Task Without_an_open_session_the_player_is_created_and_nobody_is_added_to_a_session()
    {
        using var h = new GuestHarness(withSession: false);
        var r = await Create(h, "Fran");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Null(h.Remote.Store.GetOpenSession());
        var names = GuestHarness.Json((await h.GetAsync("/api/players")).Body)["players"]!.AsArray()
            .Select(p => p!["name"]!.GetValue<string>());
        Assert.Contains("Fran", names);
    }

    [Fact]
    public async Task Singing_solo_keeps_the_open_session_untouched()
    {
        using var h = new GuestHarness();
        h.Remote.Host.State = h.Remote.Host.State with { Solo = true };
        var r = await Create(h, "Gus");
        var id = PlayerOf(r.Body)["id"]!.GetValue<long>();
        Assert.DoesNotContain(h.Remote.Store.SessionPlayers(h.Remote.Store.GetOpenSession()!.Id), p => p.Id == id);
    }

    // ---- Validaciones ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "NAME_EMPTY")]
    [InlineData("    ", "NAME_EMPTY")]
    [InlineData("abcdefghijklmnopqrstuvwxy", "NAME_TOO_LONG")]   // 25
    [InlineData("tab\there", "NAME_INVALID")]
    [InlineData("line\nbreak", "NAME_INVALID")]
    public async Task Invalid_names_are_rejected_by_the_server_without_creating(string name, string code)
    {
        using var h = new GuestHarness();
        var before = h.Remote.Store.ListPlayers().Count;
        var r = await Create(h, name);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal(code, Error(r.Body));
        Assert.Equal(before, h.Remote.Store.ListPlayers().Count);
    }

    [Fact]
    public async Task The_longest_allowed_name_and_accents_are_accepted()
    {
        using var h = new GuestHarness();
        var long24 = new string('x', KantorStoreLimit);
        Assert.Equal(HttpStatusCode.OK, (await Create(h, long24)).Status);
        var r = await Create(h, "Ñandú Ángel");
        Assert.Equal("Ñandú Ángel", PlayerOf(r.Body)["name"]!.GetValue<string>());
    }

    private const int KantorStoreLimit = Kantor.Core.Data.KantorStore.MaxNameLength;

    [Fact]
    public async Task Duplicate_names_follow_the_normal_rules_case_insensitive_among_active_players()
    {
        using var h = new GuestHarness();
        var same = await Create(h, "Ana"); // ya existe en la partida
        Assert.Equal(HttpStatusCode.Conflict, same.Status);
        Assert.Equal("NAME_TAKEN", Error(same.Body));
        Assert.Equal("NAME_TAKEN", Error((await Create(h, "ANA")).Body));
        Assert.Equal("NAME_TAKEN", Error((await Create(h, "  ana ")).Body));

        h.Remote.Store.ArchivePlayer(h.PlayerId("Ana")); // un archivado libera el nombre, como en la PC
        Assert.Equal(HttpStatusCode.OK, (await Create(h, "Ana")).Status);
    }

    [Fact]
    public async Task Two_independent_requests_with_the_same_name_are_not_merged_by_name()
    {
        using var h = new GuestHarness();
        var first = await Create(h, "Hugo", requestId: "create-0000000001");
        var second = await Create(h, "Hugo", requestId: "create-0000000002");
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal("NAME_TAKEN", Error(second.Body));
        Assert.Single(h.Remote.Store.ListPlayers(), p => p.Name == "Hugo");
    }

    [Fact]
    public async Task Any_factory_avatar_is_valid_and_is_stored_exactly_as_the_desktop_does()
    {
        using var h = new GuestHarness();
        var r = await Create(h, "Ivo", CharacterCatalog.Factory[7].Id);
        var id = PlayerOf(r.Body)["id"]!.GetValue<long>();
        Assert.Equal(CharacterCatalog.Factory[7].Id, h.Remote.Store.GetPlayer(id)!.CharacterId);
    }

    [Theory]
    [InlineData("avatar_999")]
    [InlineData("provisional")]
    [InlineData("..%2f..%2fx")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Unknown_or_path_like_avatars_are_rejected(string avatar)
    {
        using var h = new GuestHarness();
        var r = await Create(h, "Jose", avatar);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal("INVALID_AVATAR", Error(r.Body));
        Assert.DoesNotContain(h.Remote.Store.ListPlayers(), p => p.Name == "Jose");
    }

    [Fact]
    public async Task A_character_loaded_by_the_user_is_not_offered_nor_accepted()
    {
        var user = new CharacterDefinition("usuario-abc123", "usuario-abc123", @"C:\x\portrait.png", null, null, true);
        CharacterCatalog.Default.SetUserCharacters([user]);
        try
        {
            using var h = new GuestHarness();
            Assert.DoesNotContain("usuario-abc123", GuestHarness.Json((await h.GetAsync("/api/avatars")).Body)["items"]!
                .AsArray().Select(i => i!.GetValue<string>()));
            var r = await Create(h, "Kari", "usuario-abc123");
            Assert.Equal("INVALID_AVATAR", Error(r.Body));
        }
        finally
        {
            CharacterCatalog.Default.SetUserCharacters([]);
        }
    }

    [Fact]
    public async Task Without_an_avatar_the_player_gets_the_existing_generic_one()
    {
        using var h = new GuestHarness();
        var r = await Create(h, "Lola", avatar: null);
        var id = PlayerOf(r.Body)["id"]!.GetValue<long>();
        Assert.Equal(CharacterCatalog.Fallback.Id, h.Remote.Store.GetPlayer(id)!.CharacterId);
        Assert.Equal(CharacterCatalog.Fallback.Id, PlayerOf(r.Body)["avatar"]!.GetValue<string>());
        var omitted = await h.SendAsync(HttpMethod.Post, "/api/players",
            """{"name":"Mati","requestId":"create-0000000009"}""");
        Assert.Equal(HttpStatusCode.OK, omitted.Status);
    }

    [Fact]
    public async Task The_gallery_is_the_real_factory_catalog_in_its_order()
    {
        using var h = new GuestHarness();
        var items = GuestHarness.Json((await h.GetAsync("/api/avatars")).Body)["items"]!.AsArray()
            .Select(i => i!.GetValue<string>()).ToList();
        Assert.Equal(CharacterCatalog.Factory.Select(c => c.Id), items);
        Assert.Equal(items, GuestService.FactoryAvatars());
    }

    [Theory]
    [InlineData("""{"requestId":"create-0000000003"}""")]
    [InlineData("""{"name":5,"requestId":"create-0000000003"}""")]
    [InlineData("""{"name":"Nico"}""")]
    [InlineData("""{"name":"Nico","requestId":"short"}""")]
    [InlineData("""{"name":"Nico","avatar":3,"requestId":"create-0000000003"}""")]
    [InlineData("[]")]
    [InlineData("{no json")]
    public async Task Malformed_creation_requests_are_400(string json)
    {
        using var h = new GuestHarness();
        var r = await h.SendAsync(HttpMethod.Post, "/api/players", json);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.DoesNotContain(h.Remote.Store.ListPlayers(), p => p.Name == "Nico");
    }

    [Fact]
    public async Task Creation_needs_json_and_a_small_body_and_a_same_origin_request()
    {
        using var h = new GuestHarness();
        var big = await h.SendAsync(HttpMethod.Post, "/api/players", new string('x', 5000));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.Status);
        var text = await h.SendAsync(HttpMethod.Post, "/api/players", "{}", edit: r =>
            r.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.Status);
        var foreign = await h.SendAsync(HttpMethod.Post, "/api/players",
            """{"name":"Otto","requestId":"create-0000000004"}""", edit: r => r.Headers.Add("Origin", "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.Status);
        Assert.DoesNotContain(h.Remote.Store.ListPlayers(), p => p.Name == "Otto");
    }

    // ---- Acceso -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_invalid_token_creates_nothing()
    {
        using var h = new GuestHarness();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Create(h, "Pepe", token: null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Create(h, "Pepe", token: "wrong")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.GetAsync("/api/avatars", token: null)).Status);
        Assert.DoesNotContain(h.Remote.Store.ListPlayers(), p => p.Name == "Pepe");
    }

    [Fact]
    public async Task With_the_server_off_nothing_can_be_created()
    {
        using var h = new GuestHarness();
        var token = h.Token;
        await h.Server.StopAsync();
        using var fresh = new HttpClient { BaseAddress = h.Http.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/players")
        {
            Content = new StringContent("""{"name":"Quique","requestId":"create-0000000005"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(GuestWebServer.TokenHeader, token);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => fresh.SendAsync(request));
        Assert.DoesNotContain(h.Remote.Store.ListPlayers(), p => p.Name == "Quique");
    }

    [Fact]
    public async Task Player_creation_is_rate_limited_per_client_and_in_total()
    {
        using var perClient = new GuestHarness(b => b.PlayerCreatesPerClient = 2);
        Assert.Equal(HttpStatusCode.OK, (await Create(perClient, "Rita")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Create(perClient, "Saul")).Status);
        var limited = await Create(perClient, "Tina");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("RATE_LIMITED", Error(limited.Body));
        Assert.DoesNotContain(perClient.Remote.Store.ListPlayers(), p => p.Name == "Tina");

        using var total = new GuestHarness(b => b.PlayerCreatesTotal = 2);
        await Create(total, "Uma");
        await Create(total, "Vera");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Create(total, "Walt")).Status);
    }

    [Fact]
    public async Task The_creation_limit_recovers_when_the_window_passes()
    {
        var now = DateTimeOffset.UnixEpoch;
        using var h = new GuestHarness(b =>
        {
            b.PlayerCreatesPerClient = 1;
            b.Clock = () => now;
        });
        Assert.Equal(HttpStatusCode.OK, (await Create(h, "Xena")).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Create(h, "Yago")).Status);
        now += TimeSpan.FromMinutes(11);
        Assert.Equal(HttpStatusCode.OK, (await Create(h, "Yago")).Status);
    }

    // ---- Idempotencia y concurrencia ---------------------------------------------------------------------------

    [Fact]
    public async Task A_retry_with_the_same_request_id_returns_the_same_player_without_creating_another()
    {
        using var h = new GuestHarness();
        var first = await Create(h, "Zoe", requestId: "create-same-0001");
        var retry = await Create(h, "Zoe", requestId: "create-same-0001");
        Assert.Equal(HttpStatusCode.OK, retry.Status);
        Assert.True(GuestHarness.Json(retry.Body)["duplicate"]!.GetValue<bool>());
        Assert.Equal(PlayerOf(first.Body)["id"]!.GetValue<long>(), PlayerOf(retry.Body)["id"]!.GetValue<long>());
        Assert.Single(h.Remote.Store.ListPlayers(), p => p.Name == "Zoe");
        Assert.Equal(1, h.Remote.Host.PlayersAddedCount);
    }

    [Fact]
    public async Task Reusing_a_request_id_for_another_player_is_refused()
    {
        using var h = new GuestHarness();
        await Create(h, "Abel", requestId: "create-same-0002");
        var r = await Create(h, "Bruno", requestId: "create-same-0002");
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal("REQUEST_REUSED", Error(r.Body));
        Assert.DoesNotContain(h.Remote.Store.ListPlayers(), p => p.Name == "Bruno");
    }

    [Fact]
    public async Task A_queue_request_id_and_a_player_request_id_do_not_collide()
    {
        using var h = new GuestHarness();
        var player = await Create(h, "Cora", requestId: "shared-id-00001");
        var id = PlayerOf(player.Body)["id"]!.GetValue<long>();
        var queued = await h.PostQueueAsync(h.SongId("abba-waterloo"), [id], "shared-id-00001");
        Assert.Equal(HttpStatusCode.OK, queued.Status);
        Assert.False(GuestHarness.Json(queued.Body)["duplicate"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_rejected_creation_is_not_remembered_so_it_can_be_retried_once_fixed()
    {
        using var h = new GuestHarness();
        Assert.Equal("NAME_TAKEN", Error((await Create(h, "Ana", requestId: "create-same-0003")).Body));
        h.Remote.Store.ArchivePlayer(h.PlayerId("Ana"));
        Assert.Equal(HttpStatusCode.OK, (await Create(h, "Ana", requestId: "create-same-0003")).Status);
    }

    [Fact]
    public async Task Many_simultaneous_taps_on_the_same_request_create_exactly_one_player()
    {
        using var h = new GuestHarness();
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => Create(h, "Dario", requestId: "create-same-0004"))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(1, results.Count(r => !GuestHarness.Json(r.Body)["duplicate"]!.GetValue<bool>()));
        Assert.Single(h.Remote.Store.ListPlayers(), p => p.Name == "Dario");
    }

    [Fact]
    public async Task Simultaneous_independent_creations_from_many_phones_all_succeed()
    {
        using var h = new GuestHarness();
        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => Task.Run(() => Create(h, $"Invitado {i:00}", CharacterCatalog.Factory[i].Id))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(12, results.Select(r => PlayerOf(r.Body)["id"]!.GetValue<long>()).Distinct().Count());
        Assert.Equal(12, h.Remote.Store.ListPlayers().Count(p => p.Name.StartsWith("Invitado")));
    }

    [Fact]
    public async Task Simultaneous_creations_of_the_same_name_let_only_one_win()
    {
        using var h = new GuestHarness();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Create(h, "Elisa"))));
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => r.Status == HttpStatusCode.Conflict && Error(r.Body) == "NAME_TAKEN"));
        Assert.Single(h.Remote.Store.ListPlayers(), p => p.Name == "Elisa");
    }

    // ---- Alcance: solo crear -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("PUT", "/api/players")]
    [InlineData("DELETE", "/api/players")]
    [InlineData("PATCH", "/api/players")]
    public async Task Players_cannot_be_updated_or_deleted_through_the_guest_api(string method, string path)
    {
        using var h = new GuestHarness();
        var r = await h.SendAsync(new HttpMethod(method), path, """{"playerId":1,"name":"Otro"}""");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, r.Status);
    }

    [Theory]
    [InlineData("/api/players/1")]
    [InlineData("/api/players/rename")]
    [InlineData("/api/players/archive")]
    [InlineData("/api/players/setCharacter")]
    [InlineData("/api/players/delete")]
    [InlineData("/api/characters")]
    public async Task There_are_no_other_player_endpoints(string path)
    {
        using var h = new GuestHarness();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await h.SendAsync(method, path, method == HttpMethod.Get ? null : """{"playerId":1}""")).Status);
        }
    }

    [Fact]
    public async Task Posting_an_existing_id_or_name_never_modifies_an_existing_player()
    {
        using var h = new GuestHarness();
        var ana = h.Remote.Store.GetPlayer(h.PlayerId("Ana"))!;
        var r = await h.SendAsync(HttpMethod.Post, "/api/players",
            $$"""{"playerId":{{ana.Id}},"id":{{ana.Id}},"name":"Renombrada","avatar":"avatar_020","requestId":"create-0000000006"}""");
        Assert.Equal(HttpStatusCode.OK, r.Status); // es una creación nueva: los campos de edición se ignoran
        Assert.NotEqual(ana.Id, PlayerOf(r.Body)["id"]!.GetValue<long>());
        var after = h.Remote.Store.GetPlayer(ana.Id)!;
        Assert.Equal("Ana", after.Name);
        Assert.Equal(ana.CharacterId, after.CharacterId);
        Assert.False(after.Archived);
    }

    [Fact]
    public async Task Without_a_database_creation_is_unavailable_and_says_nothing_internal()
    {
        using var h = new GuestHarness();
        var noStore = new KantorController(new KantorControllerOptions
        {
            Host = h.Remote.Host, Catalog = h.Remote.Catalog, Queue = h.Remote.Queue, Store = null,
        });
        var service = new GuestService(h.Remote.Catalog, noStore, h.Remote.Ui, null, null, () => "es");
        var e = await Assert.ThrowsAsync<GuestException>(() => service.CreatePlayerAsync("Fede", null, "create-0000000007"));
        Assert.Equal("UNAVAILABLE", e.Code);
        Assert.Equal(503, e.Status);
    }
}
