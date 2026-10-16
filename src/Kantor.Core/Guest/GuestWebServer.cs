// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kantor.Core.Guest;

public sealed class GuestServerOptions
{
    public required IPAddress Address { get; init; }

    /// <summary>Puerto preferido; si está ocupado se prueban los siguientes y, al final, uno libre. 0 = uno libre.</summary>
    public int PreferredPort { get; init; } = 8765;

    /// <summary>Rechazar clientes que no sean de una red privada ni el propio equipo (por omisión sí).</summary>
    public bool RequireLanClients { get; init; } = true;

    public int RequestsPerWindow { get; init; } = 120;

    public TimeSpan RequestWindow { get; init; } = TimeSpan.FromSeconds(10);

    public int QueueWritesPerWindow { get; init; } = 6;

    public TimeSpan QueueWriteWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Tokens inválidos tolerados por cliente y minuto antes de bloquearlo un rato.</summary>
    public int AuthFailuresPerMinute { get; init; } = 10;

    /// <summary>Solicitudes que se atienden a la vez; las demás reciben 503.</summary>
    public int MaxInFlight { get; init; } = 32;

    public Func<DateTimeOffset>? Clock { get; init; }
}

/// <summary>
/// Servidor HTTP del acceso de invitados (Kestrel). Escucha en UNA dirección de la LAN, atiende un conjunto cerrado
/// de rutas y exige el token en todo lo que no sea la página estática. No sirve archivos del disco: la página sale de
/// recursos embebidos por nombre fijo, y las imágenes se piden por identificador. Sin CORS, sin CDN.
/// </summary>
public sealed partial class GuestWebServer : IAsyncDisposable
{
    public const string TokenHeader = "X-Kantor-Token";

    private const long MaxBodyBytes = 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex RequestIdPattern();

    private sealed record Asset(byte[] Bytes, string ContentType, string CacheControl);

    private static readonly Lazy<IReadOnlyDictionary<string, Asset>> Assets = new(LoadAssets);

    private readonly GuestService _service;
    private readonly GuestAccess _access;
    private readonly GuestServerOptions _options;
    private readonly GuestRateLimiter _requests;
    private readonly GuestRateLimiter _writes;
    private readonly GuestRateLimiter _failures;
    private readonly SemaphoreSlim _inFlight;
    private WebApplication? _app;
    private HashSet<string> _allowedHosts = [];

    public GuestWebServer(GuestService service, GuestAccess access, GuestServerOptions options)
    {
        _service = service;
        _access = access;
        _options = options;
        _requests = new GuestRateLimiter(options.RequestsPerWindow, options.RequestWindow, options.Clock);
        _writes = new GuestRateLimiter(options.QueueWritesPerWindow, options.QueueWriteWindow, options.Clock);
        _failures = new GuestRateLimiter(options.AuthFailuresPerMinute, TimeSpan.FromMinutes(1), options.Clock);
        _inFlight = new SemaphoreSlim(options.MaxInFlight, options.MaxInFlight);
    }

    /// <summary>Mensajes de estado sin secretos (nunca el token ni rutas de la solicitud).</summary>
    public event Action<string>? Log;

    public bool IsRunning => _app is not null;

    public int Port { get; private set; }

    /// <summary>Dirección legible sin el token: <c>http://192.168.1.20:8765</c>.</summary>
    public string BaseUrl => $"http://{_options.Address}:{Port}";

    /// <summary>
    /// Enlace del QR. El token va en el fragmento (<c>#k=…</c>): el navegador no lo envía al servidor ni en
    /// <c>Referer</c>, y la página lo guarda en memoria de la pestaña y lo quita de la barra.
    /// </summary>
    public string? JoinUrl => _access.Current is { } token ? $"{BaseUrl}/#k={token}" : null;

    /// <summary>Emite un token nuevo y empieza a atender. Si ya estaba en marcha, no hace nada.</summary>
    public async Task StartAsync()
    {
        if (_app is not null)
        {
            return;
        }

        _access.Issue();
        try
        {
            var ports = _options.PreferredPort == 0
                ? new[] { 0 }
                : Enumerable.Range(_options.PreferredPort, 10).Append(0).ToArray();
            Exception? last = null;
            foreach (var port in ports)
            {
                var app = await Task.Run(() => Build(port)).ConfigureAwait(false);
                try
                {
                    await app.StartAsync().ConfigureAwait(false);
                    _app = app;
                    Port = new Uri(app.Urls.First()).Port;
                    _allowedHosts = AllowedHostsFor(Port);
                    Log?.Invoke($"Acceso de invitados activo en {BaseUrl}");
                    return;
                }
                catch (IOException e)
                {
                    last = e; // puerto ocupado: se prueba el siguiente
                    await app.DisposeAsync().ConfigureAwait(false);
                }
            }

            throw last ?? new IOException("No se pudo abrir ningún puerto.");
        }
        catch
        {
            _access.Revoke();
            throw;
        }
    }

    /// <summary>Revoca el token y detiene el servidor (las conexiones abiertas se cortan).</summary>
    public async Task StopAsync()
    {
        _access.Revoke(); // primero: desde este instante ninguna solicitud vale
        var app = _app;
        _app = null;
        if (app is null)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // pasados 2 s se corta igual al liberar
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
            Log?.Invoke("Acceso de invitados detenido");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private HashSet<string> AllowedHostsFor(int port)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"{_options.Address}:{port}" };
        if (IPAddress.IsLoopback(_options.Address))
        {
            hosts.Add($"localhost:{port}");
        }

        return hosts;
    }

    private WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "Kantor.Guest",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.RemoveAll<IHostLifetime>();
        builder.Services.AddSingleton<IHostLifetime, NoLifetime>();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxBodyBytes;
            kestrel.Limits.MaxRequestLineSize = 2048;
            kestrel.Limits.MaxRequestHeadersTotalSize = 8192;
            kestrel.Limits.MaxRequestHeaderCount = 40;
            kestrel.Limits.MaxConcurrentConnections = 64;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            kestrel.Listen(_options.Address, port, listen => listen.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();
        app.Run(HandleAsync);
        return app;
    }

    private sealed class NoLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ---- Solicitudes ------------------------------------------------------------------------------------------

    private async Task HandleAsync(HttpContext ctx)
    {
        SecurityHeaders(ctx.Response);
        var peer = ctx.Connection.RemoteIpAddress;
        var key = (peer is { IsIPv4MappedToIPv6: true } ? peer.MapToIPv4() : peer)?.ToString() ?? "?";

        if (_options.RequireLanClients && !GuestNetwork.IsLocalOrLanClient(peer))
        {
            Log?.Invoke("Cliente fuera de la red local rechazado");
            await Fail(ctx, 403, "FORBIDDEN");
            return;
        }

        if (!_inFlight.Wait(0))
        {
            await Fail(ctx, 503, "BUSY");
            return;
        }

        try
        {
            if (_failures.IsBlocked(key) || !_requests.TryAcquire(key))
            {
                ctx.Response.Headers.RetryAfter = "10";
                await Fail(ctx, 429, "RATE_LIMITED");
                return;
            }

            if (!HostAndOriginAllowed(ctx.Request))
            {
                await Fail(ctx, 403, "FORBIDDEN");
                return;
            }

            await RouteAsync(ctx, key);
        }
        catch (GuestException e)
        {
            await Fail(ctx, e.Status, e.Code);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
        {
            await Fail(ctx, e.StatusCode, e.StatusCode == 413 ? "PAYLOAD_TOO_LARGE" : GuestErrors.InvalidRequest);
        }
        catch (OperationCanceledException)
        {
            // el cliente se fue
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log?.Invoke("Error al atender una solicitud: " + e.GetType().Name); // sin mensaje: podría traer rutas
            if (!ctx.Response.HasStarted)
            {
                await Fail(ctx, 500, GuestErrors.Unavailable);
            }
        }
        finally
        {
            _inFlight.Release();
        }
    }

    /// <summary>
    /// <c>Host</c> debe ser la dirección en la que se escucha (evita el rebinding de DNS) y, si viene <c>Origin</c>,
    /// debe ser este mismo sitio: no hay CORS, así que una página ajena no puede pedirle nada al servidor.
    /// </summary>
    private bool HostAndOriginAllowed(HttpRequest request)
    {
        if (!_allowedHosts.Contains(request.Host.Value ?? string.Empty))
        {
            return false;
        }

        if (request.Headers.TryGetValue("Origin", out var origin)
            && !string.Equals(origin.ToString(), $"http://{request.Host.Value}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !request.Headers.TryGetValue("Sec-Fetch-Site", out var site) || site.ToString() is "same-origin" or "none";
    }

    private async Task RouteAsync(HttpContext ctx, string client)
    {
        var request = ctx.Request;
        var path = request.Path.Value ?? "/";
        var isGet = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);

        if (path == "/" || path == "/index.html" || path.StartsWith("/assets/", StringComparison.Ordinal)
            || path == "/favicon.ico")
        {
            if (!isGet)
            {
                await Fail(ctx, 405, "METHOD_NOT_ALLOWED");
                return;
            }

            await ServeAssetAsync(ctx, path);
            return;
        }

        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await Fail(ctx, 404, "NOT_FOUND");
            return;
        }

        if (!_access.Validate(request.Headers[TokenHeader].ToString()))
        {
            _failures.Register(client);
            await Fail(ctx, 401, "UNAUTHORIZED");
            return;
        }

        switch (path)
        {
            case "/api/config" when isGet:
                await Ok(ctx, new { language = _service.Language, maxQuery = GuestService.MaxQueryChars,
                    pageSize = GuestService.MaxPageSize });
                break;
            case "/api/songs" when isGet:
                await SongsAsync(ctx);
                break;
            case "/api/players" when isGet:
                await PlayersAsync(ctx);
                break;
            case "/api/queue" when HttpMethods.IsPost(request.Method):
                await EnqueueAsync(ctx, client);
                break;
            default:
                if (isGet && path.StartsWith("/api/cover/", StringComparison.Ordinal))
                {
                    await ImageAsync(ctx, path["/api/cover/".Length..], cover: true);
                }
                else if (isGet && path.StartsWith("/api/avatar/", StringComparison.Ordinal))
                {
                    await ImageAsync(ctx, path["/api/avatar/".Length..], cover: false);
                }
                else if (path is "/api/config" or "/api/songs" or "/api/players" or "/api/queue")
                {
                    await Fail(ctx, 405, "METHOD_NOT_ALLOWED");
                }
                else
                {
                    await Fail(ctx, 404, "NOT_FOUND");
                }

                break;
        }
    }

    private async Task SongsAsync(HttpContext ctx)
    {
        var query = ctx.Request.Query;
        var q = query["q"].ToString();
        if (!int.TryParse(query["offset"].FirstOrDefault() ?? "0", out var offset)
            || !int.TryParse(query["limit"].FirstOrDefault() ?? "30", out var limit))
        {
            throw new GuestException(GuestErrors.InvalidRequest, 400);
        }

        var page = _service.Search(q, offset, limit);
        await Ok(ctx, new
        {
            total = page.Total, offset = page.Offset, nextOffset = page.NextOffset,
            items = page.Items.Select(s => new
            {
                id = s.Id, artist = s.Artist, title = s.Title, edition = s.Edition,
                cover = s.CoverId, video = s.HasVideo,
            }),
        });
    }

    private async Task PlayersAsync(HttpContext ctx)
    {
        var roster = await _service.RosterAsync().WaitAsync(ctx.RequestAborted).ConfigureAwait(false);
        await Ok(ctx, new
        {
            maxSingers = roster.MaxSingers,
            players = roster.Players.Select(p => new { id = p.Id, name = p.Name, avatar = GuestService.AvatarIdOf(p.CharacterId) }),
        });
    }

    private async Task EnqueueAsync(HttpContext ctx, string client)
    {
        var request = ctx.Request;
        if (request.ContentType is not { } type || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await Fail(ctx, 415, GuestErrors.InvalidRequest);
            return;
        }

        if (!_writes.TryAcquire(client))
        {
            ctx.Response.Headers.RetryAfter = "10";
            await Fail(ctx, 429, "RATE_LIMITED");
            return;
        }

        ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxBodyBytes;
        var (songId, playerIds, requestId) = await ReadEnqueueBody(request).ConfigureAwait(false);
        var result = await _service.EnqueueAsync(songId, playerIds, requestId).WaitAsync(ctx.RequestAborted)
            .ConfigureAwait(false);
        await Ok(ctx, new { queued = true, duplicate = result.Duplicate, position = result.Position, pending = result.Pending });
    }

    private static async Task<(string SongId, List<long> PlayerIds, string RequestId)> ReadEnqueueBody(HttpRequest request)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 4 },
                request.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new GuestException(GuestErrors.InvalidRequest, 400);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("songId", out var song) || song.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("requestId", out var rid) || rid.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("playerIds", out var players) || players.ValueKind != JsonValueKind.Array
                || players.GetArrayLength() is 0 or > 4)
            {
                throw new GuestException(GuestErrors.InvalidRequest, 400);
            }

            var songId = song.GetString()!;
            var requestId = rid.GetString()!;
            if (!IdPattern().IsMatch(songId) || !RequestIdPattern().IsMatch(requestId))
            {
                throw new GuestException(GuestErrors.InvalidRequest, 400);
            }

            var ids = new List<long>();
            foreach (var element in players.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var id) || id <= 0)
                {
                    throw new GuestException(GuestErrors.InvalidRequest, 400);
                }

                ids.Add(id);
            }

            return (songId, ids, requestId);
        }
    }

    private async Task ImageAsync(HttpContext ctx, string id, bool cover)
    {
        if (!IdPattern().IsMatch(id))
        {
            await Fail(ctx, 404, "NOT_FOUND");
            return;
        }

        var bytes = cover ? _service.Cover(id) : _service.Avatar(id);
        if (bytes is null)
        {
            await Fail(ctx, 404, "NOT_FOUND");
            return;
        }

        ctx.Response.ContentType = cover ? "image/jpeg" : "image/png";
        ctx.Response.ContentLength = bytes.Length;
        ctx.Response.Headers.CacheControl = "private, max-age=3600";
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task ServeAssetAsync(HttpContext ctx, string path)
    {
        var name = path switch
        {
            "/" or "/index.html" => "index.html",
            "/favicon.ico" => "logo.png",
            _ => path["/assets/".Length..],
        };
        if (!Assets.Value.TryGetValue(name, out var asset))
        {
            await Fail(ctx, 404, "NOT_FOUND");
            return;
        }

        ctx.Response.ContentType = asset.ContentType;
        ctx.Response.ContentLength = asset.Bytes.Length;
        ctx.Response.Headers.CacheControl = asset.CacheControl;
        if (!HttpMethods.IsHead(ctx.Request.Method))
        {
            await ctx.Response.Body.WriteAsync(asset.Bytes, ctx.RequestAborted).ConfigureAwait(false);
        }
    }

    // ---- Respuestas -------------------------------------------------------------------------------------------

    private static void SecurityHeaders(HttpResponse response)
    {
        var h = response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Frame-Options"] = "DENY";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob:; "
                                       + "font-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; "
                                       + "frame-ancestors 'none'";
    }

    private static Task Ok(HttpContext ctx, object body)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body, Json), ctx.RequestAborted);
    }

    private static Task Fail(HttpContext ctx, int status, string code)
    {
        if (ctx.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync($"{{\"error\":\"{code}\"}}", ctx.RequestAborted);
    }

    // ---- Recursos embebidos -----------------------------------------------------------------------------------

    /// <summary>La lista cerrada de lo que se sirve: nada que el cliente pueda nombrar fuera de ella.</summary>
    private static IReadOnlyDictionary<string, Asset> LoadAssets()
    {
        var files = new (string File, string Type, string Cache)[]
        {
            ("index.html", "text/html; charset=utf-8", "no-cache"),
            ("guest.css", "text/css; charset=utf-8", "no-cache"),
            ("guest.js", "text/javascript; charset=utf-8", "no-cache"),
            ("Jersey15-Regular.ttf", "font/ttf", "public, max-age=86400"),
            ("logo.png", "image/png", "public, max-age=86400"),
        };
        var assembly = typeof(GuestWebServer).Assembly;
        var result = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var (file, type, cache) in files)
        {
            using var stream = assembly.GetManifestResourceStream("Kantor.Guest." + file);
            if (stream is null)
            {
                continue;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            result[file] = new Asset(memory.ToArray(), type, cache);
        }

        return result;
    }
}
