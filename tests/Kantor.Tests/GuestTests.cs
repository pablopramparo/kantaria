// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kantor.Core.Guest;
using Kantor.Core.Library;
using Kantor.Core.Remote;

namespace Kantor.Tests;

internal sealed class FakeAvatars : IGuestAvatars
{
    public byte[]? GetPortraitPng(string characterId) => characterId == "avatar_001" ? [0x89, 0x50, 0x4E, 0x47] : null;
}

internal sealed class ThrowingCovers : ICoverProvider
{
    public CoverImage? GetThumbnail(string coverPath, int size) =>
        throw new InvalidOperationException(@"secreto en C:\Songs\interno");
}

/// <summary>Kantor "real" sin ventana + el servidor de invitados de verdad (Kestrel) en el loopback.</summary>
internal sealed class GuestHarness : IDisposable
{
    public RemoteHarness Remote { get; }

    public GuestAccess Access { get; } = new();

    public GuestService Service { get; }

    public GuestWebServer Server { get; }

    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(20) };

    public List<string> Logs { get; } = [];

    public string Token => Access.Current!;

    public GuestHarness(Action<GuestServerOptionsBuilder>? tweak = null, bool withSession = true, bool start = true,
        ICoverProvider? covers = null, string language = "es")
    {
        Remote = new RemoteHarness(withSession: withSession);
        Service = new GuestService(Remote.Catalog, Remote.Controller, Remote.Ui, covers ?? Remote.Covers,
            new FakeAvatars(), () => language);
        var builder = new GuestServerOptionsBuilder();
        tweak?.Invoke(builder);
        Server = new GuestWebServer(Service, Access, builder.Build());
        Server.Log += Logs.Add;
        if (start)
        {
            Server.StartAsync().GetAwaiter().GetResult();
            Http.BaseAddress = new Uri(Server.BaseUrl);
        }
    }

    public async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, string? json = null, string? token = "valid", Action<HttpRequestMessage>? edit = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token == "valid")
        {
            token = Access.Current;
        }

        if (token is not null)
        {
            request.Headers.Add(GuestWebServer.TokenHeader, token);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        edit?.Invoke(request);
        var response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    public Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> GetAsync(string path,
        string? token = "valid") => SendAsync(HttpMethod.Get, path, null, token);

    public Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> PostQueueAsync(string songId,
        long[] players, string? requestId = null, string? token = "valid") =>
        SendAsync(HttpMethod.Post, "/api/queue",
            $$"""{"songId":"{{songId}}","playerIds":[{{string.Join(",", players)}}],"requestId":"{{requestId ?? Guid.NewGuid().ToString("N")}}"}""",
            token);

    public string SongId(string name) => Remote.IdOf(name);

    public long PlayerId(string name) => Remote.LongPlayerId(name);

    public static JsonObject Json(string body) => (JsonObject)JsonNode.Parse(body)!;

    public void Dispose()
    {
        Server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Http.Dispose();
        Remote.Dispose();
    }
}

internal sealed class GuestServerOptionsBuilder
{
    public int RequestsPerWindow { get; set; } = 1000;

    public int QueueWritesPerWindow { get; set; } = 1000;

    public int AuthFailuresPerMinute { get; set; } = 1000;

    public int MaxInFlight { get; set; } = 32;

    public int PlayerCreatesPerClient { get; set; } = 1000;

    public int PlayerCreatesTotal { get; set; } = 1000;

    public int PreferredPort { get; set; }

    public Func<DateTimeOffset>? Clock { get; set; }

    public GuestServerOptions Build() => new()
    {
        Address = IPAddress.Loopback,
        PreferredPort = PreferredPort,
        RequestsPerWindow = RequestsPerWindow,
        QueueWritesPerWindow = QueueWritesPerWindow,
        AuthFailuresPerMinute = AuthFailuresPerMinute,
        MaxInFlight = MaxInFlight,
        PlayerCreatesPerClient = PlayerCreatesPerClient,
        PlayerCreatesTotal = PlayerCreatesTotal,
        Clock = Clock,
    };
}

public class GuestServerLifecycleTests
{
    [Fact]
    public void Disabled_by_default_nothing_listens_and_no_token_exists()
    {
        using var h = new GuestHarness(start: false);
        Assert.False(h.Server.IsRunning);
        Assert.False(h.Access.IsActive);
        Assert.Null(h.Access.Current);
        Assert.Null(h.Server.JoinUrl);
    }

    [Fact]
    public async Task Start_serves_and_stop_closes_the_port_and_revokes_the_token()
    {
        using var h = new GuestHarness();
        Assert.True(h.Server.IsRunning);
        var token = h.Token;
        Assert.Equal(HttpStatusCode.OK, (await h.GetAsync("/api/config")).Status);

        await h.Server.StopAsync();

        Assert.False(h.Server.IsRunning);
        Assert.False(h.Access.IsActive);
        Assert.False(h.Access.Validate(token));
        Assert.Null(h.Server.JoinUrl);
        using var fresh = new HttpClient { BaseAddress = h.Http.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => fresh.GetAsync("/"));
    }

    [Fact]
    public async Task Restart_issues_a_new_token_and_the_old_one_stops_working()
    {
        using var h = new GuestHarness();
        var old = h.Token;
        var oldLink = h.Server.JoinUrl;
        await h.Server.StopAsync();
        await h.Server.StartAsync();
        h.Http.BaseAddress = new Uri(h.Server.BaseUrl);

        Assert.NotEqual(old, h.Token);
        Assert.NotEqual(oldLink, h.Server.JoinUrl); // el QR se regenera con el token nuevo
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.GetAsync("/api/config", old)).Status);
        Assert.Equal(HttpStatusCode.OK, (await h.GetAsync("/api/config")).Status);
    }

    [Fact]
    public async Task Start_and_stop_are_idempotent()
    {
        using var h = new GuestHarness();
        var token = h.Token;
        await h.Server.StartAsync(); // ya en marcha: no cambia nada
        Assert.Equal(token, h.Token);
        await h.Server.StopAsync();
        await h.Server.StopAsync();
        Assert.False(h.Server.IsRunning);
    }

    [Fact]
    public async Task A_busy_preferred_port_falls_back_to_another()
    {
        using var first = new GuestHarness();
        using var second = new GuestHarness(b => b.PreferredPort = first.Server.Port);
        Assert.NotEqual(first.Server.Port, second.Server.Port);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/config")).Status);
    }

    [Fact]
    public void The_join_link_carries_the_token_in_the_fragment_only()
    {
        using var h = new GuestHarness();
        var url = h.Server.JoinUrl!;
        Assert.StartsWith(h.Server.BaseUrl + "/#k=", url, StringComparison.Ordinal);
        Assert.EndsWith(h.Token, url, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Token, h.Server.BaseUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_never_reaches_the_log()
    {
        using var h = new GuestHarness();
        var token = h.Token;
        await h.GetAsync("/api/config");
        await h.GetAsync("/api/config", "wrong-token-value");
        await h.Server.StopAsync();
        Assert.NotEmpty(h.Logs);
        Assert.All(h.Logs, line => Assert.DoesNotContain(token, line, StringComparison.Ordinal));
    }
}

public class GuestAccessTests
{
    [Fact]
    public void Token_is_random_url_safe_and_128_bits()
    {
        var a = new GuestAccess();
        var first = a.Issue();
        var second = a.Issue();
        Assert.NotEqual(first, second);
        Assert.Equal(22, first.Length); // 16 bytes en base64 sin relleno
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
        Assert.False(a.Validate(first)); // el anterior ya no vale
        Assert.True(a.Validate(second));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void Missing_or_wrong_tokens_are_rejected(string? candidate)
    {
        var a = new GuestAccess();
        Assert.False(a.Validate(candidate)); // sin token emitido
        a.Issue();
        Assert.False(a.Validate(candidate));
    }

    [Fact]
    public void Revoke_invalidates_everything()
    {
        var a = new GuestAccess();
        var token = a.Issue();
        a.Revoke();
        Assert.False(a.IsActive);
        Assert.False(a.Validate(token));
    }
}

public class GuestRateLimiterTests
{
    [Fact]
    public void Allows_up_to_the_limit_then_refuses_until_the_window_passes()
    {
        var now = DateTimeOffset.UnixEpoch;
        var limiter = new GuestRateLimiter(3, TimeSpan.FromSeconds(10), () => now);
        Assert.True(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("a"));
        Assert.False(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("b")); // otra clave, otro cupo
        now += TimeSpan.FromSeconds(11);
        Assert.True(limiter.TryAcquire("a"));
    }

    [Fact]
    public void Failures_block_the_key_without_growing_past_the_limit()
    {
        var now = DateTimeOffset.UnixEpoch;
        var limiter = new GuestRateLimiter(2, TimeSpan.FromMinutes(1), () => now);
        Assert.False(limiter.IsBlocked("a"));
        limiter.Register("a");
        limiter.Register("a");
        limiter.Register("a");
        Assert.True(limiter.IsBlocked("a"));
        now += TimeSpan.FromMinutes(2);
        Assert.False(limiter.IsBlocked("a"));
    }
}

public class GuestNetworkTests
{
    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.3.4", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    public void Private_lan_ranges(string address, bool expected) =>
        Assert.Equal(expected, GuestNetwork.IsPrivateLan(IPAddress.Parse(address)));

    [Theory]
    [InlineData("192.168.0.9", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:10.1.2.3", true)]
    [InlineData("203.0.113.9", false)]
    [InlineData("2001:db8::1", false)]
    public void Clients_must_be_local_or_lan(string address, bool expected) =>
        Assert.Equal(expected, GuestNetwork.IsLocalOrLanClient(IPAddress.Parse(address)));

    [Fact]
    public void Null_client_is_rejected() => Assert.False(GuestNetwork.IsLocalOrLanClient(null));

    [Theory]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter", true)]
    [InlineData("Tailscale", "Tailscale Tunnel", true)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", false)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", false)]
    public void Virtual_adapters_are_recognised(string name, string description, bool virtualAdapter) =>
        Assert.Equal(virtualAdapter, GuestNetwork.LooksVirtual(name, description));

    [Fact]
    public void Choose_prefers_the_requested_address_then_the_one_with_a_gateway()
    {
        var a = new GuestInterface("A", IPAddress.Parse("192.168.1.2"), false);
        var b = new GuestInterface("B", IPAddress.Parse("10.0.0.2"), true);
        Assert.Equal(a, GuestNetwork.Choose([b, a], "192.168.1.2"));
        Assert.Equal(b, GuestNetwork.Choose([a, b], "1.2.3.4"));
        Assert.Equal(b, GuestNetwork.Choose([a, b], null));
        Assert.Equal(a, GuestNetwork.Choose([a], null));
        Assert.Null(GuestNetwork.Choose([], null));
    }

    [Fact]
    public void Candidates_only_lists_private_addresses()
    {
        foreach (var candidate in GuestNetwork.Candidates())
        {
            Assert.True(GuestNetwork.IsPrivateLan(candidate.Address));
        }
    }
}

public class GuestQrTests
{
    [Fact]
    public void Builds_a_square_matrix_with_the_finder_patterns()
    {
        var m = GuestQr.Modules("http://192.168.1.20:8765/#k=AbCdEfGhIjKlMnOpQrStUv");
        var size = m.GetLength(0);
        Assert.Equal(size, m.GetLength(1));
        Assert.True((size - 21) % 4 == 0 && size >= 21);
        // esquina superior izquierda: cuadrado de 7 × 7 con anillo oscuro, anillo claro y centro de 3 × 3 oscuro
        for (var i = 0; i < 7; i++)
        {
            Assert.True(m[0, i]);
            Assert.True(m[i, 0]);
            Assert.True(m[6, i]);
            Assert.True(m[i, 6]);
        }

        Assert.False(m[1, 1]);
        Assert.True(m[3, 3]);
        Assert.True(m[0, size - 1]); // buscador de la esquina superior derecha
        Assert.True(m[size - 1, 0]); // y el de la inferior izquierda
    }
}

public class GuestAuthTests
{
    [Fact]
    public async Task Static_page_is_public_but_every_api_route_needs_the_token()
    {
        using var h = new GuestHarness();
        Assert.Equal(HttpStatusCode.OK, (await h.GetAsync("/", token: null)).Status);
        foreach (var path in new[] { "/api/config", "/api/songs", "/api/players", "/api/cover/abc", "/api/avatar/avatar_001" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.GetAsync(path, token: null)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.GetAsync(path, token: "wrong")).Status);
        }

        var post = await h.PostQueueAsync(h.SongId("abba-waterloo"), [h.PlayerId("Ana")], token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, post.Status);
        Assert.Equal(HttpStatusCode.OK, (await h.GetAsync("/api/config")).Status);
        Assert.Empty(h.Remote.Queue.Pending());
    }

    [Fact]
    public async Task The_token_is_not_accepted_in_the_url()
    {
        using var h = new GuestHarness();
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.GetAsync($"/api/config?k={h.Token}", token: null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.GetAsync($"/api/config?token={h.Token}", token: null)).Status);
    }

    [Fact]
    public async Task Repeated_bad_tokens_lock_the_client_out_even_with_the_right_one()
    {
        using var h = new GuestHarness(b => b.AuthFailuresPerMinute = 3);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.GetAsync("/api/config", "bad" + i)).Status);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await h.GetAsync("/api/config")).Status);
    }

    [Fact]
    public async Task Rate_limit_returns_429_and_recovers_when_the_window_passes()
    {
        var now = DateTimeOffset.UnixEpoch;
        using var h = new GuestHarness(b =>
        {
            b.RequestsPerWindow = 5;
            b.Clock = () => now;
        });
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await h.GetAsync("/api/config")).Status);
        }

        var limited = await h.GetAsync("/api/config");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("RATE_LIMITED", GuestHarness.Json(limited.Body)["error"]!.GetValue<string>());
        now += TimeSpan.FromSeconds(30);
        Assert.Equal(HttpStatusCode.OK, (await h.GetAsync("/api/config")).Status);
    }

    [Fact]
    public async Task Queue_writes_have_their_own_tighter_limit()
    {
        using var h = new GuestHarness(b => b.QueueWritesPerWindow = 2);
        var ana = h.PlayerId("Ana");
        Assert.Equal(HttpStatusCode.OK, (await h.PostQueueAsync(h.SongId("abba-waterloo"), [ana])).Status);
        Assert.Equal(HttpStatusCode.OK, (await h.PostQueueAsync(h.SongId("queen-radio-1"), [ana])).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await h.PostQueueAsync(h.SongId("lost-cover"), [ana])).Status);
        Assert.Equal(2, h.Remote.Queue.Pending().Count);
    }

    [Fact]
    public async Task Requests_beyond_the_in_flight_cap_are_refused_with_503()
    {
        using var h = new GuestHarness(b => b.MaxInFlight = 1);
        // un pedido lento ocupa el único lugar: el roster espera al hilo de la interfaz
        using var gate = new ManualResetEventSlim();
        var blocker = h.Remote.Ui.InvokeAsync(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return 0;
        });
        var slow = h.GetAsync("/api/players");
        await Task.Delay(300);
        var refused = await h.GetAsync("/api/config");
        gate.Set();
        await blocker;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.Status);
        Assert.Equal(HttpStatusCode.OK, (await slow).Status);
    }
}

public class GuestRoutingTests
{
    [Theory]
    [InlineData("DELETE", "/api/queue")]
    [InlineData("PUT", "/api/queue")]
    [InlineData("GET", "/api/queue")]
    [InlineData("POST", "/api/songs")]
    [InlineData("DELETE", "/api/players")]
    [InlineData("PUT", "/api/players")]
    [InlineData("POST", "/api/avatars")]
    [InlineData("POST", "/api/config")]
    public async Task Known_routes_reject_other_methods(string method, string path)
    {
        using var h = new GuestHarness();
        var r = await h.SendAsync(new HttpMethod(method), path, method == "GET" ? null : "{}");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, r.Status);
    }

    [Theory]
    [InlineData("/api/playback/stop")]
    [InlineData("/api/playback/start")]
    [InlineData("/api/session")]
    [InlineData("/api/settings")]
    [InlineData("/api/players/create")]
    [InlineData("/api/queue/clear")]
    [InlineData("/api/queue/remove")]
    [InlineData("/api/history")]
    [InlineData("/api/admin")]
    [InlineData("/api/avyn")]
    [InlineData("/kantor.ControlPlane.v1")]
    [InlineData("/admin")]
    public async Task There_are_no_admin_playback_or_history_endpoints(string path)
    {
        using var h = new GuestHarness();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
        {
            var r = await h.SendAsync(method, path, method == HttpMethod.Get ? null : "{}");
            Assert.Equal(HttpStatusCode.NotFound, r.Status);
        }
    }

    [Fact]
    public async Task The_guest_cannot_change_the_queue_or_players_beyond_adding()
    {
        using var h = new GuestHarness();
        var before = h.Remote.Queue.Pending().Count;
        await h.SendAsync(HttpMethod.Post, "/api/players/create", """{"name":"Intruso"}""");
        await h.SendAsync(HttpMethod.Post, "/api/queue/clear", """{"confirm":true}""");
        await h.SendAsync(HttpMethod.Post, "/api/playback/stop", "{}");
        Assert.Equal(before, h.Remote.Queue.Pending().Count);
        Assert.DoesNotContain(h.Remote.Store.ListPlayers(), p => p.Name == "Intruso");
        Assert.DoesNotContain("Stop", h.Remote.Host.Calls);
    }

    [Theory]
    [InlineData("/assets/Kantor.Core.dll")]
    [InlineData("/assets/../Kantor.Core.dll")]
    [InlineData("/assets/..%2fKantor.Core.dll")]
    [InlineData("/assets/..%5cKantor.Core.dll")]
    [InlineData("/assets/guest.js/../../secret.txt")]
    [InlineData("/assets/%2e%2e/%2e%2e/windows/win.ini")]
    [InlineData("/assets/C:/Windows/win.ini")]
    [InlineData("/assets/")]
    [InlineData("/index.htm")]
    [InlineData("/..%2f..%2fwindows/win.ini")]
    [InlineData("/Kantor.App.exe")]
    public async Task Static_files_come_only_from_a_closed_list(string path)
    {
        using var h = new GuestHarness();
        var r = await h.GetAsync(path, token: null);
        Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{path} → {r.Status}");
        Assert.DoesNotContain("MZ", r.Body[..Math.Min(2, r.Body.Length)], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/cover/..%5C..%5Cwindows%5Cwin.ini")]
    [InlineData("/api/cover/C:%5CSongs%5Cradio.jpg")]
    [InlineData("/api/cover/C%3A%5CSongs%5Cradio.jpg")]
    [InlineData(@"/api/cover/..%2f..%2fx")]
    [InlineData("/api/cover/")]
    [InlineData("/api/avatar/..%2f..%2fx")]
    [InlineData("/api/avatar/avatar_999")]
    [InlineData("/api/avatar/%2e%2e")]
    public async Task Images_are_requested_by_identifier_never_by_path(string path)
    {
        using var h = new GuestHarness();
        var r = await h.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, r.Status);
        Assert.Equal(0, h.Remote.Covers.Calls); // el proveedor de portadas ni se consultó
    }

    [Fact]
    public async Task Cover_and_avatar_are_served_by_known_identifiers()
    {
        using var h = new GuestHarness();
        var cover = await h.GetAsync($"/api/cover/{h.SongId("queen-radio-1")}");
        Assert.Equal(HttpStatusCode.OK, cover.Status);
        Assert.Equal("image/jpeg", cover.Response.Content.Headers.ContentType!.MediaType);
        var noCover = await h.GetAsync($"/api/cover/{h.SongId("abba-waterloo")}");
        Assert.Equal(HttpStatusCode.NotFound, noCover.Status);
        var avatar = await h.GetAsync("/api/avatar/avatar_001");
        Assert.Equal(HttpStatusCode.OK, avatar.Status);
        Assert.Equal("image/png", avatar.Response.Content.Headers.ContentType!.MediaType);
    }
}

public class GuestHardeningTests
{
    [Fact]
    public async Task Static_assets_are_served_with_the_right_types_and_security_headers()
    {
        using var h = new GuestHarness();
        foreach (var (path, type) in new[]
                 {
                     ("/", "text/html"), ("/assets/guest.css", "text/css"), ("/assets/guest.js", "text/javascript"),
                     ("/assets/Jersey15-Regular.ttf", "font/ttf"), ("/assets/logo.png", "image/png"),
                 })
        {
            var r = await h.GetAsync(path, token: null);
            Assert.Equal(HttpStatusCode.OK, r.Status);
            Assert.Equal(type, r.Response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("nosniff", r.Response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", r.Response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("default-src 'none'", r.Response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.False(r.Response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(r.Response.Headers.Contains("Server"));
        }
    }

    [Fact]
    public async Task The_page_loads_nothing_from_outside()
    {
        using var h = new GuestHarness();
        foreach (var path in new[] { "/", "/assets/guest.css", "/assets/guest.js" })
        {
            var body = (await h.GetAsync(path, token: null)).Body;
            Assert.DoesNotContain("https://", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("//cdn", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("googleapis", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("innerHTML", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cross_origin_and_foreign_host_requests_are_refused()
    {
        using var h = new GuestHarness();
        var origin = await h.SendAsync(HttpMethod.Get, "/api/config", edit: r => r.Headers.Add("Origin", "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, origin.Status);
        var host = await h.SendAsync(HttpMethod.Get, "/api/config", edit: r => r.Headers.Host = "evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, host.Status);
        var site = await h.SendAsync(HttpMethod.Get, "/api/config", edit: r => r.Headers.Add("Sec-Fetch-Site", "cross-site"));
        Assert.Equal(HttpStatusCode.Forbidden, site.Status);
        var post = await h.SendAsync(HttpMethod.Post, "/api/queue",
            $$"""{"songId":"{{h.SongId("abba-waterloo")}}","playerIds":[{{h.PlayerId("Ana")}}],"requestId":"abcdefgh12345678"}""",
            edit: r => r.Headers.Add("Origin", "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, post.Status);
        Assert.Empty(h.Remote.Queue.Pending());

        var same = await h.SendAsync(HttpMethod.Get, "/api/config", edit: r => r.Headers.Add("Origin", h.Server.BaseUrl));
        Assert.Equal(HttpStatusCode.OK, same.Status);
    }

    [Fact]
    public async Task There_is_no_cors_preflight_support()
    {
        using var h = new GuestHarness();
        var r = await h.SendAsync(new HttpMethod("OPTIONS"), "/api/queue", edit: x =>
        {
            x.Headers.Add("Origin", "http://evil.example");
            x.Headers.Add("Access-Control-Request-Method", "POST");
        });
        Assert.NotEqual(HttpStatusCode.OK, r.Status);
        Assert.False(r.Response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Oversized_wrong_type_and_malformed_bodies_are_rejected_without_adding()
    {
        using var h = new GuestHarness();
        var big = await h.SendAsync(HttpMethod.Post, "/api/queue", new string('x', 5000));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.Status);

        var text = await h.SendAsync(HttpMethod.Post, "/api/queue", "{}", edit: r =>
            r.Content!.Headers.ContentType = new MediaTypeHeaderValue("text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.Status);

        Assert.Equal(HttpStatusCode.BadRequest, (await h.SendAsync(HttpMethod.Post, "/api/queue", "{no json")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.SendAsync(HttpMethod.Post, "/api/queue", "[]")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.SendAsync(HttpMethod.Post, "/api/queue", "{}")).Status);
        Assert.Empty(h.Remote.Queue.Pending());
    }

    [Theory]
    [InlineData("""{"songId":"../x","playerIds":[1],"requestId":"abcdefgh12345678"}""")]
    [InlineData("""{"songId":"abc","playerIds":[],"requestId":"abcdefgh12345678"}""")]
    [InlineData("""{"songId":"abc","playerIds":[1,2,3,4,5],"requestId":"abcdefgh12345678"}""")]
    [InlineData("""{"songId":"abc","playerIds":["1"],"requestId":"abcdefgh12345678"}""")]
    [InlineData("""{"songId":"abc","playerIds":[-1],"requestId":"abcdefgh12345678"}""")]
    [InlineData("""{"songId":"abc","playerIds":[1.5],"requestId":"abcdefgh12345678"}""")]
    [InlineData("""{"songId":"abc","playerIds":[1],"requestId":"short"}""")]
    [InlineData("""{"songId":"abc","playerIds":[1],"requestId":"has space in it!"}""")]
    [InlineData("""{"songId":1,"playerIds":[1],"requestId":"abcdefgh12345678"}""")]
    public async Task Invalid_enqueue_payloads_are_400(string json)
    {
        using var h = new GuestHarness();
        var r = await h.SendAsync(HttpMethod.Post, "/api/queue", json);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal("INVALID_REQUEST", GuestHarness.Json(r.Body)["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Internal_errors_do_not_leak_details()
    {
        using var h = new GuestHarness(covers: new ThrowingCovers());
        var r = await h.GetAsync($"/api/cover/{h.SongId("queen-radio-1")}");
        Assert.Equal(HttpStatusCode.InternalServerError, r.Status);
        Assert.Equal("UNAVAILABLE", GuestHarness.Json(r.Body)["error"]!.GetValue<string>());
        Assert.DoesNotContain("secreto", r.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", r.Body, StringComparison.Ordinal);
        Assert.All(h.Logs, line => Assert.DoesNotContain("secreto", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_library_never_exposes_file_paths()
    {
        using var h = new GuestHarness();
        var all = (await h.GetAsync("/api/songs?limit=40")).Body;
        Assert.DoesNotContain("C:\\", all, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", all, StringComparison.Ordinal);
        Assert.DoesNotContain(".txt", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".jpg", all, StringComparison.OrdinalIgnoreCase);
    }
}

public class GuestLibraryTests
{
    [Fact]
    public async Task Config_reports_kantors_language()
    {
        using var es = new GuestHarness(language: "es");
        using var en = new GuestHarness(language: "en");
        Assert.Equal("es", GuestHarness.Json((await es.GetAsync("/api/config")).Body)["language"]!.GetValue<string>());
        Assert.Equal("en", GuestHarness.Json((await en.GetAsync("/api/config")).Body)["language"]!.GetValue<string>());
    }

    [Fact]
    public async Task Search_matches_title_and_artist_ignoring_case_and_accents()
    {
        using var h = new GuestHarness();
        h.Remote.SetLibrary([.. RemoteHarness.DefaultSongs(),
            RemoteHarness.Song("cancion", "Los Ñandúes", "Canción de Cuna")]);

        async Task<JsonObject> Search(string q) =>
            GuestHarness.Json((await h.GetAsync("/api/songs?q=" + Uri.EscapeDataString(q))).Body);

        Assert.Equal(2, (await Search("RADIO")).Value<int>("total")); // dos ediciones de la misma canción
        Assert.Equal(1, (await Search("waterloo")).Value<int>("total"));
        Assert.Equal(1, (await Search("abba")).Value<int>("total"));
        Assert.Equal(1, (await Search("cancion de cuna")).Value<int>("total"));
        Assert.Equal(1, (await Search("CANCIÓN")).Value<int>("total"));
        Assert.Equal(1, (await Search("nandues")).Value<int>("total"));
        Assert.True((await Search("queen radio")).Value<int>("total") > 0); // varias palabras, en cualquier campo
    }

    [Fact]
    public async Task No_results_is_an_empty_list_not_an_error()
    {
        using var h = new GuestHarness();
        var r = await h.GetAsync("/api/songs?q=zzzzzzzz");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var json = GuestHarness.Json(r.Body);
        Assert.Equal(0, json.Value<int>("total"));
        Assert.Empty(json["items"]!.AsArray());
        Assert.Null(json["nextOffset"]);
    }

    [Fact]
    public async Task Results_are_paged_so_the_whole_library_is_never_sent_at_once()
    {
        using var h = new GuestHarness();
        h.Remote.SetLibrary(Enumerable.Range(0, 95).Select(i => RemoteHarness.Song($"s{i}", $"Artista {i:000}", $"Tema {i:000}")));

        var first = GuestHarness.Json((await h.GetAsync("/api/songs?limit=40")).Body);
        Assert.Equal(95, first.Value<int>("total"));
        Assert.Equal(40, first["items"]!.AsArray().Count);
        Assert.Equal(40, first.Value<int>("nextOffset"));

        var last = GuestHarness.Json((await h.GetAsync("/api/songs?limit=40&offset=80")).Body);
        Assert.Equal(15, last["items"]!.AsArray().Count);
        Assert.Null(last["nextOffset"]);

        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync("/api/songs?limit=41")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync("/api/songs?limit=0")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync("/api/songs?offset=-1")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync("/api/songs?offset=abc")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync("/api/songs?q=" + new string('a', 101))).Status);
    }

    [Fact]
    public async Task Search_uses_the_same_catalog_and_rules_as_the_desktop_remote_search()
    {
        using var h = new GuestHarness();
        var guest = GuestHarness.Json((await h.GetAsync("/api/songs?q=radio")).Body)["items"]!.AsArray()
            .Select(i => i!["id"]!.GetValue<string>()).ToList();
        var remote = h.Remote.Read("kantor.library.search", """{"query":"radio"}""").P["items"]!.AsArray()
            .Select(i => i!["libraryEntryId"]!.GetValue<string>()).ToList();
        Assert.Equal(remote, guest);
    }
}

public class GuestPlayersTests
{
    [Fact]
    public async Task Roster_lists_only_the_open_sessions_active_players()
    {
        using var h = new GuestHarness();
        var carla = h.Remote.Store.CreatePlayer("Carla"); // existe, pero no está en la partida
        var dani = h.Remote.Store.CreatePlayer("Dani");
        h.Remote.Store.ArchivePlayer(dani.Id);

        var json = GuestHarness.Json((await h.GetAsync("/api/players")).Body);
        var names = json["players"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["Ana", "Beto"], names);
        Assert.Equal(2, json.Value<int>("maxSingers"));
        Assert.DoesNotContain("Carla", names);
        Assert.True(carla.Id > 0);
    }

    [Fact]
    public async Task Without_an_open_session_every_active_player_is_available()
    {
        using var h = new GuestHarness(withSession: false);
        h.Remote.Store.CreatePlayer("Ana");
        h.Remote.Store.CreatePlayer("Beto");
        var gone = h.Remote.Store.CreatePlayer("Gone");
        h.Remote.Store.ArchivePlayer(gone.Id);

        var names = GuestHarness.Json((await h.GetAsync("/api/players")).Body)["players"]!.AsArray()
            .Select(p => p!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["Ana", "Beto"], names.Order());
    }

    [Fact]
    public async Task Every_player_carries_an_avatar_id_the_server_can_serve()
    {
        using var h = new GuestHarness();
        var player = GuestHarness.Json((await h.GetAsync("/api/players")).Body)["players"]!.AsArray()[0]!;
        Assert.Equal("avatar_001", player["avatar"]!.GetValue<string>());
    }

    [Fact]
    public async Task Max_singers_follows_the_microphones_created_on_the_pc()
    {
        using var h = new GuestHarness();
        h.Remote.Host.State = h.Remote.Host.State with { MicrophoneSlots = [2] };
        Assert.Equal(1, GuestHarness.Json((await h.GetAsync("/api/players")).Body).Value<int>("maxSingers"));
        h.Remote.Host.State = h.Remote.Host.State with { MicrophoneSlots = [] };
        Assert.Equal(0, GuestHarness.Json((await h.GetAsync("/api/players")).Body).Value<int>("maxSingers"));
    }

    [Fact]
    public async Task Without_a_database_there_is_nobody_to_choose()
    {
        using var h = new GuestHarness();
        var noStore = new KantorController(new KantorControllerOptions
        {
            Host = h.Remote.Host, Catalog = h.Remote.Catalog, Queue = h.Remote.Queue, Store = null,
        });
        var roster = await h.Remote.Ui.InvokeAsync(noStore.GuestRosterSnapshot);
        Assert.Empty(roster.Players);
        Assert.Equal(0, roster.MaxSingers);
    }
}

public class GuestEnqueueTests
{
    private static string Error(string body) => GuestHarness.Json(body)["error"]!.GetValue<string>();

    [Fact]
    public async Task Adding_uses_the_real_queue_with_singers_assigned_to_the_microphones_in_order()
    {
        using var h = new GuestHarness();
        var ana = h.PlayerId("Ana");
        var beto = h.PlayerId("Beto");
        h.Remote.DrainOut();

        var r = await h.PostQueueAsync(h.SongId("abba-waterloo"), [beto, ana]);

        Assert.Equal(HttpStatusCode.OK, r.Status);
        var json = GuestHarness.Json(r.Body);
        Assert.True(json["queued"]!.GetValue<bool>());
        Assert.False(json["duplicate"]!.GetValue<bool>());
        Assert.Equal(1, json.Value<int>("position"));
        var item = Assert.Single(h.Remote.Queue.Pending());
        Assert.Equal(h.SongId("abba-waterloo"), item.LibraryEntryId);
        Assert.Equal("Waterloo", item.Title);
        Assert.Equal("Abba", item.Artist);
        Assert.Equal([new Kantor.Core.Data.QueueSlot(1, beto), new Kantor.Core.Data.QueueSlot(2, ana)], item.Slots);
        Assert.Equal(KantorController.GuestOriginId, item.AddedById);
    }

    [Fact]
    public async Task The_added_item_is_the_same_one_avyn_sees_and_is_published_to_it()
    {
        using var h = new GuestHarness();
        h.Remote.Flush();
        h.Remote.DrainOut();
        await h.PostQueueAsync(h.SongId("queen-radio-1"), [h.PlayerId("Ana")]);

        var listed = h.Remote.Read("kantor.queue.list").P["items"]!.AsArray();
        var entry = Assert.Single(listed)!;
        Assert.Equal(h.SongId("queen-radio-1"), entry["libraryEntryId"]!.GetValue<string>());
        Assert.Contains(h.Remote.DrainOut(), m => m["type"]!.GetValue<string>() == "kantor.state.changed");
    }

    [Fact]
    public async Task Positions_follow_the_queue_order_and_it_persists_in_the_session()
    {
        using var h = new GuestHarness();
        var ana = h.PlayerId("Ana");
        var one = GuestHarness.Json((await h.PostQueueAsync(h.SongId("abba-waterloo"), [ana])).Body);
        var two = GuestHarness.Json((await h.PostQueueAsync(h.SongId("queen-radio-1"), [ana])).Body);
        Assert.Equal(1, one.Value<int>("position"));
        Assert.Equal(2, two.Value<int>("position"));
        Assert.Equal(2, two.Value<int>("pending"));
        Assert.True(h.Remote.Queue.Persistent);
        Assert.Equal(2, h.Remote.Queue.List(h.Remote.Store.GetOpenSession()!.Id).Count);
    }

    [Fact]
    public async Task Unknown_song_and_unknown_player_are_404()
    {
        using var h = new GuestHarness();
        var ana = h.PlayerId("Ana");
        var song = await h.PostQueueAsync("ffffffffffffffffffffffffffffffff", [ana]);
        Assert.Equal(HttpStatusCode.NotFound, song.Status);
        Assert.Equal("SONG_NOT_FOUND", Error(song.Body));

        var player = await h.PostQueueAsync(h.SongId("abba-waterloo"), [999999]);
        Assert.Equal(HttpStatusCode.NotFound, player.Status);
        Assert.Equal("PLAYER_NOT_FOUND", Error(player.Body));
        Assert.Empty(h.Remote.Queue.Pending());
    }

    [Fact]
    public async Task A_song_that_left_the_library_is_not_found()
    {
        using var h = new GuestHarness();
        var id = h.SongId("abba-waterloo");
        h.Remote.SetLibrary(RemoteHarness.DefaultSongs().Where(s => !s.Path.Contains("abba")));
        var r = await h.PostQueueAsync(id, [h.PlayerId("Ana")]);
        Assert.Equal(HttpStatusCode.NotFound, r.Status);
        Assert.Equal("SONG_NOT_FOUND", Error(r.Body));
    }

    [Fact]
    public async Task Invalid_singer_assignments_are_rejected_with_the_desktop_rules()
    {
        using var h = new GuestHarness();
        var ana = h.PlayerId("Ana");
        var beto = h.PlayerId("Beto");
        var carla = h.Remote.Store.CreatePlayer("Carla").Id; // no está en la partida
        var dani = h.Remote.Store.CreatePlayer("Dani").Id;
        h.Remote.Store.AddPlayerToSession(h.Remote.Store.GetOpenSession()!.Id, dani);
        h.Remote.Store.ArchivePlayer(dani);
        var song = h.SongId("abba-waterloo");

        var dup = await h.PostQueueAsync(song, [ana, ana]);
        Assert.Equal(HttpStatusCode.BadRequest, dup.Status);
        Assert.Equal("INVALID_REQUEST", Error(dup.Body));

        var tooMany = await h.PostQueueAsync(song, [ana, beto, carla]); // 2 micrófonos
        Assert.Equal(HttpStatusCode.Conflict, tooMany.Status);
        Assert.Equal("TOO_MANY_SINGERS", Error(tooMany.Body));

        var outside = await h.PostQueueAsync(song, [carla]);
        Assert.Equal(HttpStatusCode.Conflict, outside.Status);
        Assert.Equal("PLAYER_NOT_AVAILABLE", Error(outside.Body));

        var archived = await h.PostQueueAsync(song, [dani]);
        Assert.Equal(HttpStatusCode.Conflict, archived.Status);
        Assert.Equal("PLAYER_NOT_AVAILABLE", Error(archived.Body));

        Assert.Empty(h.Remote.Queue.Pending());
    }

    [Fact]
    public async Task The_number_of_singers_cannot_exceed_the_microphones()
    {
        using var h = new GuestHarness();
        h.Remote.Host.State = h.Remote.Host.State with { MicrophoneSlots = [1] };
        var r = await h.PostQueueAsync(h.SongId("abba-waterloo"), [h.PlayerId("Ana"), h.PlayerId("Beto")]);
        Assert.Equal("TOO_MANY_SINGERS", Error(r.Body));
        var ok = await h.PostQueueAsync(h.SongId("abba-waterloo"), [h.PlayerId("Ana")]);
        Assert.Equal(HttpStatusCode.OK, ok.Status);
    }

    [Fact]
    public async Task Only_the_microphones_actually_open_count_as_available()
    {
        // en el escritorio, "¿Quién canta?" ofrece solo los micrófonos abiertos (HostState.MicrophoneSlots): con
        // cuatro creados y dos disponibles el máximo es dos, y lo de más se rechaza igual que en la PC
        using var h = new GuestHarness();
        h.Remote.Host.State = h.Remote.Host.State with { MicrophoneSlots = [1, 3] };
        Assert.Equal(2, GuestHarness.Json((await h.GetAsync("/api/players")).Body).Value<int>("maxSingers"));
        var third = h.Remote.Store.CreatePlayer("Eva").Id;
        h.Remote.Store.AddPlayerToSession(h.Remote.Store.GetOpenSession()!.Id, third);
        var r = await h.PostQueueAsync(h.SongId("abba-waterloo"), [h.PlayerId("Ana"), h.PlayerId("Beto"), third]);
        Assert.Equal("TOO_MANY_SINGERS", Error(r.Body));
        var ok = await h.PostQueueAsync(h.SongId("abba-waterloo"), [h.PlayerId("Ana"), h.PlayerId("Beto")]);
        Assert.Equal([1, 3], h.Remote.Queue.Pending().Single().Slots.Select(s => s.Slot));
        Assert.Equal(HttpStatusCode.OK, ok.Status);
    }

    [Fact]
    public async Task Concurrent_new_requests_for_the_same_song_all_add()
    {
        using var h = new GuestHarness();
        var song = h.SongId("abba-waterloo");
        var ana = h.PlayerId("Ana");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => h.PostQueueAsync(song, [ana]))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(8, h.Remote.Queue.Pending().Count);
    }

    [Fact]
    public async Task A_queued_item_keeps_its_singers_when_started_from_avyn_or_auto_advance()
    {
        using var h = new GuestHarness();
        var ana = h.PlayerId("Ana");
        var beto = h.PlayerId("Beto");
        await h.PostQueueAsync(h.SongId("abba-waterloo"), [beto, ana]);
        var item = h.Remote.Queue.Pending().Single();

        var started = h.Remote.Send("kantor.playback.start", $$"""{"itemId":"{{item.ItemId}}"}""");
        Assert.True(started.Ok, started.Code);
        Assert.Equal([(1, beto), (2, ana)], h.Remote.Host.LastStart!.Selection!.Select(s => (s.Slot, s.PlayerId)));
    }

    [Fact]
    public async Task Skipped_microphone_slots_are_used_as_they_are()
    {
        using var h = new GuestHarness();
        h.Remote.Host.State = h.Remote.Host.State with { MicrophoneSlots = [2, 4] };
        var r = await h.PostQueueAsync(h.SongId("abba-waterloo"), [h.PlayerId("Ana"), h.PlayerId("Beto")]);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal([2, 4], h.Remote.Queue.Pending().Single().Slots.Select(s => s.Slot));
    }

    [Fact]
    public async Task Without_microphones_nothing_is_added()
    {
        using var h = new GuestHarness();
        h.Remote.Host.State = h.Remote.Host.State with { MicrophoneSlots = [] };
        var r = await h.PostQueueAsync(h.SongId("abba-waterloo"), [h.PlayerId("Ana")]);
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal("NO_MICROPHONES", Error(r.Body));
        Assert.Empty(h.Remote.Queue.Pending());
    }

    [Fact]
    public async Task Without_a_session_any_active_player_can_sing()
    {
        using var h = new GuestHarness(withSession: false);
        var ana = h.Remote.Store.CreatePlayer("Ana").Id;
        var r = await h.PostQueueAsync(h.SongId("abba-waterloo"), [ana]);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.False(h.Remote.Queue.Persistent); // cola suelta: solo en memoria
        Assert.Single(h.Remote.Queue.Pending());
    }

    [Fact]
    public async Task A_full_queue_is_reported_without_adding()
    {
        using var h = new GuestHarness();
        var ana = h.PlayerId("Ana");
        var entry = h.Remote.Catalog.Current.Items[0];
        for (var i = 0; i < Kantor.Core.Data.QueueService.MaxPending; i++)
        {
            h.Remote.Queue.Add(entry.EntryId, entry.SongIdentity, entry.Entry.Artist, entry.Entry.Title, null);
        }

        var r = await h.PostQueueAsync(h.SongId("abba-waterloo"), [ana]);
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal("QUEUE_FULL", Error(r.Body));
    }

    // ---- Duplicados y concurrencia ------------------------------------------------------------------------------

    [Fact]
    public async Task A_network_retry_with_the_same_request_id_adds_once()
    {
        using var h = new GuestHarness();
        var song = h.SongId("abba-waterloo");
        var ana = h.PlayerId("Ana");
        var first = await h.PostQueueAsync(song, [ana], "req-0000000001");
        var retry = await h.PostQueueAsync(song, [ana], "req-0000000001");
        Assert.False(GuestHarness.Json(first.Body)["duplicate"]!.GetValue<bool>());
        Assert.True(GuestHarness.Json(retry.Body)["duplicate"]!.GetValue<bool>());
        Assert.Single(h.Remote.Queue.Pending());
    }

    [Fact]
    public async Task The_same_song_with_the_same_singers_can_be_queued_again_by_a_new_request()
    {
        using var h = new GuestHarness();
        var song = h.SongId("abba-waterloo");
        var ana = h.PlayerId("Ana");
        var first = await h.PostQueueAsync(song, [ana], "req-aaaaaaaa0001");
        var second = await h.PostQueueAsync(song, [ana], "req-aaaaaaaa0002");
        Assert.False(GuestHarness.Json(second.Body)["duplicate"]!.GetValue<bool>());
        Assert.Equal(2, GuestHarness.Json(second.Body).Value<int>("position"));
        Assert.Equal(2, h.Remote.Queue.Pending().Count);
        // y el reintento de cualquiera de los dos sigue sin sumar
        await h.PostQueueAsync(song, [ana], "req-aaaaaaaa0001");
        await h.PostQueueAsync(song, [ana], "req-aaaaaaaa0002");
        Assert.Equal(2, h.Remote.Queue.Pending().Count);
        Assert.False(GuestHarness.Json(first.Body)["duplicate"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Different_content_is_not_a_duplicate()
    {
        using var h = new GuestHarness();
        var ana = h.PlayerId("Ana");
        await h.PostQueueAsync(h.SongId("abba-waterloo"), [ana]);
        await h.PostQueueAsync(h.SongId("abba-waterloo"), [h.PlayerId("Beto")]);
        await h.PostQueueAsync(h.SongId("queen-radio-1"), [ana]);
        Assert.Equal(3, h.Remote.Queue.Pending().Count);
    }

    [Fact]
    public async Task Reusing_a_request_id_for_another_choice_is_refused()
    {
        using var h = new GuestHarness();
        await h.PostQueueAsync(h.SongId("abba-waterloo"), [h.PlayerId("Ana")], "req-0000000002");
        var r = await h.PostQueueAsync(h.SongId("queen-radio-1"), [h.PlayerId("Ana")], "req-0000000002");
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal("REQUEST_REUSED", Error(r.Body));
        Assert.Single(h.Remote.Queue.Pending());
    }

    [Fact]
    public async Task A_rejected_request_can_be_retried_with_the_same_id_once_fixed()
    {
        using var h = new GuestHarness();
        h.Remote.Host.State = h.Remote.Host.State with { MicrophoneSlots = [] };
        var song = h.SongId("abba-waterloo");
        var ana = h.PlayerId("Ana");
        Assert.Equal(HttpStatusCode.Conflict, (await h.PostQueueAsync(song, [ana], "req-0000000003")).Status);
        h.Remote.Host.State = h.Remote.Host.State with { MicrophoneSlots = [1, 2] };
        Assert.Equal(HttpStatusCode.OK, (await h.PostQueueAsync(song, [ana], "req-0000000003")).Status);
        Assert.Single(h.Remote.Queue.Pending());
    }

    [Fact]
    public async Task Many_simultaneous_taps_on_the_same_request_add_exactly_one()
    {
        using var h = new GuestHarness();
        var song = h.SongId("abba-waterloo");
        var ana = h.PlayerId("Ana");
        var results = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => h.PostQueueAsync(song, [ana], "req-same-000001"))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(1, results.Count(r => !GuestHarness.Json(r.Body)["duplicate"]!.GetValue<bool>()));
        Assert.Single(h.Remote.Queue.Pending());
    }

    [Fact]
    public async Task Simultaneous_distinct_requests_are_all_added_with_unique_positions()
    {
        using var h = new GuestHarness();
        var ana = h.PlayerId("Ana");
        var beto = h.PlayerId("Beto");
        var songs = new[] { "abba-waterloo", "queen-radio-1", "queen-radio-2", "lost-cover" }.Select(h.SongId).ToArray();
        var combos = songs.SelectMany(s => new[] { new[] { ana }, new[] { beto }, new[] { ana, beto } }.Select(p => (s, p)))
            .Take(10).ToList();
        var results = await Task.WhenAll(combos.Select(c => Task.Run(() => h.PostQueueAsync(c.s, c.p))));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(10, h.Remote.Queue.Pending().Count);
        var positions = results.Select(r => GuestHarness.Json(r.Body).Value<int>("position")).Order().ToList();
        Assert.Equal(Enumerable.Range(1, 10), positions);
    }

    [Fact]
    public async Task Guest_adds_and_avyn_adds_interleave_without_losing_items()
    {
        using var h = new GuestHarness();
        var ana = h.PlayerId("Ana");
        var beto = h.PlayerId("Beto");
        var songs = new[] { "abba-waterloo", "queen-radio-1", "queen-radio-2" }.Select(h.SongId).ToArray();
        var guest = songs.SelectMany(s => new[] { ana, beto }.Select(p => (s, p)))
            .Select(c => Task.Run(() => h.PostQueueAsync(c.s, [c.p])));
        var avyn = Enumerable.Range(0, 6).Select(i => Task.Run(() =>
            h.Remote.Send("kantor.queue.add", $$"""{"libraryEntryId":"{{h.SongId("lost-cover")}}"}""",
                commandId: $"avyn-{i:D4}-xxxx")));
        await Task.WhenAll(guest.Cast<Task>().Concat(avyn));
        var pending = h.Remote.Queue.Pending();
        Assert.Equal(12, pending.Count);
        Assert.Equal(6, pending.Count(p => p.AddedById == KantorController.GuestOriginId));
        Assert.Equal(pending.Count, pending.Select(p => p.ItemId).Distinct().Count());
    }
}

file static class JsonExtensions
{
    public static int Value<T>(this JsonObject json, string name) => json[name]!.GetValue<int>();
}

public class GuestTextsTests
{
    [Fact]
    public void Every_guest_text_exists_in_both_languages()
    {
        foreach (var language in new[] { "es", "en" })
        {
            var all = new[]
            {
                Kantor.Core.Menu.GuestTexts.Title(language), Kantor.Core.Menu.GuestTexts.Back(language),
                Kantor.Core.Menu.GuestTexts.Scan(language), Kantor.Core.Menu.GuestTexts.SameWifi(language),
                Kantor.Core.Menu.GuestTexts.What(language), Kantor.Core.Menu.GuestTexts.Active(language),
                Kantor.Core.Menu.GuestTexts.Inactive(language), Kantor.Core.Menu.GuestTexts.TurnOn(language),
                Kantor.Core.Menu.GuestTexts.TurnOff(language), Kantor.Core.Menu.GuestTexts.Address(language, "x"),
                Kantor.Core.Menu.GuestTexts.OffNote(language), Kantor.Core.Menu.GuestTexts.OnNote(language),
                Kantor.Core.Menu.GuestTexts.NoNetwork(language), Kantor.Core.Menu.GuestTexts.Failed(language, "x"),
                Kantor.Core.Menu.GuestTexts.Hint(language), Kantor.Core.Menu.GuestTexts.CardTitle(language),
                Kantor.Core.Menu.GuestTexts.CardScan(language), Kantor.Core.Menu.GuestTexts.CardOn(language),
                Kantor.Core.Menu.GuestTexts.OptionLabel(language), Kantor.Core.Menu.GuestTexts.OptionAuto(language),
                Kantor.Core.Menu.GuestTexts.OptionHelp(language),
            };
            Assert.All(all, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        }

        Assert.Equal("JOIN THE PARTY", Kantor.Core.Menu.GuestTexts.Title("en"));
        Assert.Equal("SUMATE AL KARAOKE", Kantor.Core.Menu.GuestTexts.Title("es"));
    }
}
