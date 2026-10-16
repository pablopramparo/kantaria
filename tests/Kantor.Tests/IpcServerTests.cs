// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Kantor.Core.Remote;
using Xunit;

namespace Kantor.Tests;

/// <summary>Un cliente de prueba del pipe: el mismo marco que usa el Agent de Avyn.</summary>
internal sealed class PipeTestClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private int _counter;

    private PipeTestClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public static async Task<PipeTestClient> ConnectAsync(string name, int timeoutMs = 3000)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutMs);
        return new PipeTestClient(pipe);
    }

    public async Task SendRawAsync(byte[] bytes)
    {
        await _pipe.WriteAsync(bytes);
        await _pipe.FlushAsync();
    }

    public Task SendAsync(JsonObject message) => SendRawAsync(FrameCodec.Encode(Encoding.UTF8.GetBytes(message.ToJsonString())));

    public async Task<JsonObject> SendCommandAsync(string type, string payload = "{}", string? commandId = null,
        bool origin = true)
    {
        var id = $"c{++_counter}";
        var message = new JsonObject { ["id"] = id, ["type"] = type, ["payload"] = JsonNode.Parse(payload) };
        if (commandId is not null)
        {
            message["commandId"] = commandId;
        }

        if (origin)
        {
            message["origin"] = new JsonObject { ["clientId"] = "phone-1", ["name"] = "Pablo" };
        }

        await SendAsync(message);
        return await ReceiveResponseAsync(id);
    }

    /// <summary>Lee marcos hasta encontrar la respuesta con ese id; los eventos intermedios quedan en <see cref="Events"/>.</summary>
    public async Task<JsonObject> ReceiveResponseAsync(string id, int timeoutMs = 5000)
    {
        while (true)
        {
            var message = await ReceiveAsync(timeoutMs) ?? throw new IOException("La conexión se cerró.");
            if (message["id"]?.GetValue<string>() == id)
            {
                return message;
            }

            Events.Add(message);
        }
    }

    public List<JsonObject> Events { get; } = [];

    public async Task<JsonObject?> ReceiveAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var frame = await FrameCodec.ReadAsync(_pipe, cts.Token);
        return frame is null ? null : JsonNode.Parse(frame)!.AsObject();
    }

    /// <summary>¿El servidor cerró la conexión? (lee hasta el fin o un error).</summary>
    public async Task<bool> WaitClosedAsync(int timeoutMs = 4000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (true)
            {
                if (await FrameCodec.ReadAsync(_pipe, cts.Token) is null)
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or FrameException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose() => _pipe.Dispose();
}

public sealed class IpcServerTests : IAsyncLifetime
{
    private readonly RemoteHarness _h = new();
    private readonly string _pipe = "Kantor.Test." + Guid.NewGuid().ToString("N");
    private KantorIpcServer _server = null!;

    public Task InitializeAsync()
    {
        // vigilancia acelerada: 15 s + 5 s de la realidad pasan a 0,6 s + 0,3 s
        _server = new KantorIpcServer(_h.Controller, _h.Ui, _pipe, watchdogInterval: TimeSpan.FromMilliseconds(50),
            livenessTimeout: TimeSpan.FromMilliseconds(600), livenessGrace: TimeSpan.FromMilliseconds(300),
            stallThreshold: TimeSpan.FromSeconds(5));
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _h.Dispose();
    }

    private Task<PipeTestClient> Connect(int timeoutMs = 3000) => PipeTestClient.ConnectAsync(_pipe, timeoutMs);

    private static async Task<(JsonObject Hello, JsonObject State)> Handshake(PipeTestClient client)
    {
        var hello = await client.ReceiveAsync() ?? throw new IOException("sin hello");
        var state = await client.ReceiveAsync() ?? throw new IOException("sin estado");
        return (hello, state);
    }

    private string EntryId(string name) => _h.IdOf(name);

    private async Task<PipeTestClient> ConnectedClient()
    {
        var client = await Connect();
        await Handshake(client);
        return client;
    }

    [Fact]
    public async Task On_connect_the_server_sends_hello_then_the_full_state()
    {
        using var client = await Connect();

        var (hello, state) = await Handshake(client);

        Assert.Equal("kantor.hello", hello["type"]!.GetValue<string>());
        Assert.Equal("test-instance", hello["payload"]!["instanceId"]!.GetValue<string>());
        Assert.Equal(1, hello["payload"]!["protocolVersion"]!.GetValue<int>());
        Assert.Equal("kantor.state", state["type"]!.GetValue<string>());
        Assert.True(state["rev"]!.GetValue<long>() >= 1);
        foreach (var section in new[] { "screen", "playback", "queue", "slots", "library", "display", "attention" })
        {
            Assert.True(state["payload"]!.AsObject().ContainsKey(section), section);
        }

        Assert.True(_server.Connected);
    }

    [Fact]
    public async Task Ping_is_answered_while_the_interface_thread_is_blocked()
    {
        using var client = await ConnectedClient();
        var blocked = _h.Ui.InvokeAsync(() =>
        {
            Thread.Sleep(1500); // la interfaz ocupada (por ejemplo, cargando una canción)
            return 0;
        });

        var started = DateTime.UtcNow;
        var pong = await client.SendCommandAsync("kantor.ping");

        Assert.True(pong["ok"]!.GetValue<bool>());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(1000));
        await blocked;
    }

    [Fact]
    public async Task Read_only_library_queries_do_not_wait_for_the_interface_thread()
    {
        using var client = await ConnectedClient();
        var blocked = _h.Ui.InvokeAsync(() =>
        {
            Thread.Sleep(1500);
            return 0;
        });

        var started = DateTime.UtcNow;
        var search = await client.SendCommandAsync("kantor.library.search", """{"query":"abba"}""");

        Assert.True(search["ok"]!.GetValue<bool>());
        Assert.Single(search["payload"]!["items"]!.AsArray());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(1000));
        await blocked;
    }

    [Fact]
    public async Task A_mutation_publishes_the_new_state_before_its_response_and_runs_on_the_interface_thread()
    {
        using var client = await ConnectedClient();

        var response = await client.SendCommandAsync("kantor.queue.add",
            $$"""{"libraryEntryId":"{{EntryId("abba-waterloo")}}"}""", commandId: "ipc-add-0001");

        Assert.True(response["ok"]!.GetValue<bool>());
        var changed = Assert.Single(client.Events, e => e["type"]!.GetValue<string>() == "kantor.state.changed");
        Assert.Equal(response["rev"]!.GetValue<long>(), changed["rev"]!.GetValue<long>());
        Assert.Equal(1, changed["payload"]!["queue"]!["pendingCount"]!.GetValue<int>());

        await client.SendCommandAsync("kantor.playback.start",
            $$"""{"libraryEntryId":"{{EntryId("abba-waterloo")}}"}""", commandId: "ipc-start-001");
        Assert.Equal(((SingleThreadInvoker)_h.Ui).ThreadId, _h.Host.LastStartThread);
    }

    [Fact]
    public async Task Errors_are_structured_and_the_connection_survives_them()
    {
        using var client = await ConnectedClient();

        var missing = await client.SendCommandAsync("kantor.queue.add", "{}", commandId: null, origin: false);
        var unknown = await client.SendCommandAsync("kantor.nada", "{}", commandId: "ipc-unk-00001");
        var bad = await client.SendCommandAsync("kantor.queue.remove", """{"itemId":"inexistente"}""",
            commandId: "ipc-bad-00001");

        Assert.False(missing["ok"]!.GetValue<bool>());
        Assert.Equal("INVALID_PAYLOAD", missing["error"]!["code"]!.GetValue<string>());
        Assert.Equal("UNKNOWN_COMMAND", unknown["error"]!["code"]!.GetValue<string>());
        Assert.Equal("NOT_FOUND", bad["error"]!["code"]!.GetValue<string>());
        Assert.Equal("unknown", bad["error"]!["details"]!["reason"]!.GetValue<string>());
        Assert.False(bad["error"]!["details"]!["applied"]!.GetValue<bool>());
        Assert.True((await client.SendCommandAsync("kantor.ping"))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_message_without_a_type_gets_a_protocol_error_but_keeps_the_connection()
    {
        using var client = await ConnectedClient();

        await client.SendAsync(new JsonObject { ["id"] = "sin-tipo" });
        var error = await client.ReceiveResponseAsync("sin-tipo");

        Assert.Equal("PROTOCOL_ERROR", error["error"]!["code"]!.GetValue<string>());
        Assert.True((await client.SendCommandAsync("kantor.ping"))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_oversized_frame_closes_the_connection_and_a_new_client_can_connect()
    {
        var client = await ConnectedClient();
        var prefix = new byte[4];
        BitConverter.TryWriteBytes(prefix, (uint)Protocol.MaxFrameBytes + 1);

        await client.SendRawAsync(prefix);

        Assert.True(await client.WaitClosedAsync());
        client.Dispose();
        using var next = await Connect(5000);
        var (hello, _) = await Handshake(next);
        Assert.Equal("kantor.hello", hello["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_second_client_cannot_take_over_an_active_one_and_the_first_keeps_working()
    {
        using var first = await ConnectedClient();
        var keepAlive = Task.Run(async () =>
        {
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(100);
                await first.SendAsync(new JsonObject { ["id"] = $"k{i}", ["type"] = "kantor.ping" });
            }
        });

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            using var intruder = await Connect(timeoutMs: 700);
        });

        await keepAlive;
        var response = await first.SendCommandAsync("kantor.library.facets");
        Assert.True(response["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_client_that_only_sends_pings_stays_connected_far_past_the_timeout()
    {
        using var client = await ConnectedClient();

        for (var i = 0; i < 25; i++) // 2,5 s: más de cuatro veces el plazo de este servidor
        {
            await Task.Delay(100);
            var pong = await client.SendCommandAsync("kantor.ping");
            Assert.True(pong["ok"]!.GetValue<bool>());
        }

        Assert.True(_server.Connected);
    }

    [Fact]
    public async Task A_silent_dead_client_is_dropped_and_the_pipe_is_freed_for_the_next_one()
    {
        var dead = await ConnectedClient(); // conecta y no vuelve a mandar nada

        Assert.True(await dead.WaitClosedAsync(timeoutMs: 4000));
        dead.Dispose();

        using var next = await Connect(5000);
        await Handshake(next);
        Assert.True((await next.SendCommandAsync("kantor.ping"))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task After_a_reconnection_the_state_is_sent_again_and_a_retried_command_is_not_repeated()
    {
        string firstItem;
        long firstRev;
        using (var first = await ConnectedClient())
        {
            var added = await first.SendCommandAsync("kantor.queue.add",
                $$"""{"libraryEntryId":"{{EntryId("abba-waterloo")}}"}""", commandId: "ipc-retry-0001");
            firstItem = added["payload"]!["item"]!["itemId"]!.GetValue<string>();
            firstRev = added["rev"]!.GetValue<long>();
        } // el Agent se reinició antes de recibir/confirmar la respuesta

        Assert.True(SpinWait.SpinUntil(() => !_server.Connected, 3000));
        using var second = await Connect(5000);
        var (hello, state) = await Handshake(second);

        Assert.Equal("test-instance", hello["payload"]!["instanceId"]!.GetValue<string>()); // misma instancia
        Assert.Equal(1, state["payload"]!["queue"]!["pendingCount"]!.GetValue<int>());
        Assert.Equal(firstRev, state["rev"]!.GetValue<long>());

        var retry = await second.SendCommandAsync("kantor.queue.add",
            $$"""{"libraryEntryId":"{{EntryId("abba-waterloo")}}"}""", commandId: "ipc-retry-0001");

        Assert.Equal(firstItem, retry["payload"]!["item"]!["itemId"]!.GetValue<string>());
        Assert.Single(_h.Queue.Pending());
    }

    [Fact]
    public async Task A_client_that_vanishes_mid_command_does_not_break_the_server()
    {
        var client = await ConnectedClient();
        await client.SendAsync(new JsonObject
        {
            ["id"] = "huerfano",
            ["type"] = "kantor.queue.add",
            ["commandId"] = "ipc-orphan-001",
            ["origin"] = new JsonObject { ["clientId"] = "p", ["name"] = "n" },
            ["payload"] = new JsonObject { ["libraryEntryId"] = EntryId("abba-waterloo") },
        });
        client.Dispose(); // se fue sin esperar la respuesta

        Assert.True(SpinWait.SpinUntil(() => _h.Queue.Pending().Count == 1, 3000)); // el comando llegó a aplicarse
        using var next = await Connect(5000);
        await Handshake(next);
        var again = await next.SendCommandAsync("kantor.queue.add",
            $$"""{"libraryEntryId":"{{EntryId("abba-waterloo")}}"}""", commandId: "ipc-orphan-001");

        Assert.True(again["ok"]!.GetValue<bool>());
        Assert.Single(_h.Queue.Pending());
    }

    [Fact]
    public async Task Two_clients_one_after_the_other_see_the_same_authoritative_queue()
    {
        using (var phoneA = await ConnectedClient())
        {
            await phoneA.SendCommandAsync("kantor.queue.add",
                $$"""{"libraryEntryId":"{{EntryId("abba-waterloo")}}"}""", commandId: "ipc-two-000001");
        }

        Assert.True(SpinWait.SpinUntil(() => !_server.Connected, 3000));
        using var phoneB = await Connect();
        var (_, state) = await Handshake(phoneB);
        var move = await phoneB.SendCommandAsync("kantor.queue.add",
            $$"""{"libraryEntryId":"{{EntryId("queen-radio-1")}}","position":"next"}""", commandId: "ipc-two-000002");

        Assert.Equal(1, state["payload"]!["queue"]!["pendingCount"]!.GetValue<int>());
        var titles = _h.Queue.Pending().Select(i => i.Title).ToList();
        Assert.Equal(["Radio Ga Ga", "Waterloo"], titles);
        Assert.True(move["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Connection_changes_are_reported_to_the_application()
    {
        var states = new List<bool>();
        _server.ConnectionChanged += states.Add;

        using (var client = await ConnectedClient())
        {
            Assert.True(SpinWait.SpinUntil(() => states.Contains(true), 3000));
        }

        Assert.True(SpinWait.SpinUntil(() => states.Contains(false), 3000));
    }
}
