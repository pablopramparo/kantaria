// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Kantor.Core.Remote;

/// <summary>Corre trabajos en el hilo de la interfaz (en la aplicación, el dispatcher de WPF).</summary>
public interface IUiInvoker
{
    Task<T> InvokeAsync<T>(Func<T> work);
}

/// <summary>Un hilo propio que ejecuta los trabajos de a uno: el "hilo de la interfaz" de las pruebas.</summary>
public sealed class SingleThreadInvoker : IUiInvoker, IDisposable
{
    private readonly BlockingCollection<Action> _work = [];
    private readonly Thread _thread;

    public SingleThreadInvoker()
    {
        _thread = new Thread(() =>
        {
            foreach (var job in _work.GetConsumingEnumerable())
            {
                job();
            }
        }) { IsBackground = true, Name = "Kantor.TestUi" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception e)
            {
                completion.SetException(e);
            }
        });
        return completion.Task;
    }

    public void Dispose() => _work.CompleteAdding();
}

/// <summary>
/// El pipe local <c>Kantor.ControlPlane.v1</c>: un único cliente (el Agent de Avyn) a la vez, solo para el usuario
/// actual y sin puertos de red. Marco de 4 bytes + JSON. La lectura, <c>kantor.ping</c> y la escritura no esperan
/// a la interfaz; los comandos corren en ella de a uno, en orden de llegada.
/// </summary>
public sealed class KantorIpcServer : IAsyncDisposable
{
    private readonly KantorController _controller;
    private readonly IUiInvoker _ui;
    private readonly string _pipeName;
    private readonly Func<TimeSpan> _clock;
    private readonly TimeSpan _watchdogInterval;
    private readonly TimeSpan _livenessTimeout;
    private readonly TimeSpan _livenessGrace;
    private readonly TimeSpan _stall;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private volatile Connection? _current;

    public KantorIpcServer(KantorController controller, IUiInvoker ui, string? pipeName = null,
        Func<TimeSpan>? clock = null, TimeSpan? watchdogInterval = null, TimeSpan? livenessTimeout = null,
        TimeSpan? livenessGrace = null, TimeSpan? stallThreshold = null)
    {
        _controller = controller;
        _ui = ui;
        _pipeName = pipeName ?? Protocol.PipeName;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _clock = clock ?? (() => stopwatch.Elapsed);
        _watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(1);
        _livenessTimeout = livenessTimeout ?? Protocol.LivenessTimeout;
        _livenessGrace = livenessGrace ?? Protocol.LivenessGrace;
        _stall = stallThreshold ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Hay un cliente conectado y ya recibió el estado inicial.</summary>
    public bool Connected => _current is not null;

    /// <summary>Cambió la conexión (<c>true</c> = el Agent se conectó).</summary>
    public event Action<bool>? ConnectionChanged;

    public event Action<string>? Log;

    public void Start() => _loop ??= Task.Run(() => AcceptLoopAsync(_stop.Token));

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _current?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // cierre normal
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
            }
            catch (IOException e)
            {
                // otra instancia de Kantaria ya tiene el pipe (o no se pudo crear): se reintenta sin ruido
                Log?.Invoke("No se pudo crear el pipe: " + e.Message);
                await DelayAsync(TimeSpan.FromSeconds(3), stop).ConfigureAwait(false);
                continue;
            }

            try
            {
                using (pipe)
                {
                    await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                    await ServeAsync(pipe, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // cierre o conexión caída
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Log?.Invoke("Conexión terminada: " + e.Message);
            }
        }
    }

    private static async Task DelayAsync(TimeSpan time, CancellationToken stop)
    {
        try
        {
            await Task.Delay(time, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // cierre
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using var connection = new Connection(pipe, _controller, _ui, _clock, new PipeLiveness(
            _livenessTimeout, _livenessGrace, _stall).Start(_clock()), _watchdogInterval, stop, Log);
        _current = connection;
        try
        {
            await connection.RunAsync(() => ConnectionChanged?.Invoke(true)).ConfigureAwait(false);
        }
        finally
        {
            _current = null;
            ConnectionChanged?.Invoke(false);
        }
    }

    private sealed class Connection : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly KantorController _controller;
        private readonly IUiInvoker _ui;
        private readonly Func<TimeSpan> _clock;
        private readonly PipeLiveness _liveness;
        private readonly TimeSpan _watchdog;
        private readonly CancellationTokenSource _cts;
        private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private readonly Channel<IncomingMessage> _commands = Channel.CreateUnbounded<IncomingMessage>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly Action<string>? _log;
        private int _closed;

        public Connection(NamedPipeServerStream pipe, KantorController controller, IUiInvoker ui, Func<TimeSpan> clock,
            PipeLiveness liveness, TimeSpan watchdog, CancellationToken stop, Action<string>? log)
        {
            _pipe = pipe;
            _controller = controller;
            _ui = ui;
            _clock = clock;
            _liveness = liveness;
            _watchdog = watchdog;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            _log = log;
        }

        public void Cancel() => _cts.Cancel();

        public void Dispose() => _cts.Dispose();

        public async Task RunAsync(Action connected)
        {
            var token = _cts.Token;
            Action<JsonObject> forward = message => Enqueue(Serialize(message));
            try
            {
                // hello + estado completo + suscripción, juntos en el hilo de la interfaz: ningún evento se cuela en medio
                await _ui.InvokeAsync(() =>
                {
                    forward(_controller.BuildHello());
                    forward(_controller.BuildSnapshotMessage());
                    _controller.Outbound += forward;
                    return 0;
                }).ConfigureAwait(false);
                connected();

                var writer = Task.Run(() => WriteLoopAsync(token));
                var executor = Task.Run(() => CommandLoopAsync(token));
                var watchdog = Task.Run(() => WatchdogAsync(token));
                try
                {
                    await ReadLoopAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _closed, 1);
                    _cts.Cancel();
                    _outgoing.Writer.TryComplete();
                    _commands.Writer.TryComplete();
                    await Task.WhenAll(Quiet(writer), Quiet(executor), Quiet(watchdog)).ConfigureAwait(false);
                }
            }
            finally
            {
                _controller.Outbound -= forward;
            }
        }

        private static async Task Quiet(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // la conexión terminó
            }
        }

        private static byte[] Serialize(JsonObject message) => Encoding.UTF8.GetBytes(message.ToJsonString());

        private void Enqueue(byte[] json)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return;
            }

            if (!_outgoing.Writer.TryWrite(json))
            {
                // el Agent no lee y la cola se llenó: la conexión se da por muerta
                _log?.Invoke("Cola de salida llena: se cierra la conexión.");
                _cts.Cancel();
            }
        }

        private async Task WriteLoopAsync(CancellationToken token)
        {
            await foreach (var json in _outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                byte[] frame;
                try
                {
                    frame = FrameCodec.Encode(json);
                }
                catch (FrameException e)
                {
                    _log?.Invoke("Mensaje descartado: " + e.Message);
                    continue;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Protocol.WriteTimeout);
                try
                {
                    await _pipe.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
                    await _pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    _log?.Invoke("Una escritura no avanzó en 5 s: se cierra la conexión.");
                    _cts.Cancel();
                    return;
                }
            }
        }

        private async Task WatchdogAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_watchdog, token).ConfigureAwait(false);
                if (_liveness.Check(_clock()) == LivenessVerdict.Dead)
                {
                    _log?.Invoke("Sin tráfico del cliente: se cierra la conexión.");
                    _cts.Cancel();
                    return;
                }
            }
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                byte[]? frame;
                try
                {
                    frame = await FrameCodec.ReadAsync(_pipe, token).ConfigureAwait(false);
                }
                catch (FrameException e)
                {
                    _log?.Invoke("Marco inválido: " + e.Message);
                    return; // sin resincronización posible: se cierra
                }

                if (frame is null)
                {
                    return;
                }

                _liveness.Touch(_clock()); // actividad registrada ANTES de despachar

                IncomingMessage message;
                string? knownId;
                try
                {
                    message = IncomingMessage.Parse(frame, DateTimeOffset.UtcNow, out knownId);
                }
                catch (RemoteException e)
                {
                    // sin id legible no hay a quién responder: solo se descarta
                    if (e.Code == ErrorCodes.ProtocolError && TryReadId(frame) is { } id)
                    {
                        Enqueue(Serialize(ErrorMessage(id, _controllerRev(), e)));
                    }

                    continue;
                }

                if (message.Type == "kantor.ping")
                {
                    Enqueue(Serialize(OkMessage(message.Id, _controllerRev(), new JsonObject())));
                    continue;
                }

                if (_controller.IsHeavyRead(message.Type))
                {
                    _ = Task.Run(async () =>
                    {
                        var response = await _controller.HandleAsync(message, work => _ui.InvokeAsync(work))
                            .ConfigureAwait(false);
                        Enqueue(Serialize(ToMessage(message.Id, response)));
                    });
                    continue;
                }

                _commands.Writer.TryWrite(message);
            }
        }

        private long _controllerRev() => _controller.Rev;

        private static string? TryReadId(byte[] frame)
        {
            try
            {
                return JsonNode.Parse(frame) is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out var id)
                       && id.Length is > 0 and <= Protocol.MaxIdChars
                    ? id
                    : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private async Task CommandLoopAsync(CancellationToken token)
        {
            await foreach (var message in _commands.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                CachedResponse response;
                if (DateTimeOffset.UtcNow - message.ReceivedAt > Protocol.QueueWait)
                {
                    response = _controller.TimedOut(); // no se ejecutó
                }
                else
                {
                    response = await _controller.HandleAsync(message, work => _ui.InvokeAsync(work))
                        .ConfigureAwait(false);
                }

                Enqueue(Serialize(ToMessage(message.Id, response)));
            }
        }

        private static JsonObject ToMessage(string id, CachedResponse r)
        {
            if (r.Ok)
            {
                return OkMessage(id, r.Rev, r.PayloadNode);
            }

            var error = new JsonObject { ["code"] = r.ErrorCode, ["message"] = r.ErrorMessage };
            if (r.DetailsJson is not null)
            {
                error["details"] = JsonNode.Parse(r.DetailsJson);
            }

            return new JsonObject { ["id"] = id, ["ok"] = false, ["rev"] = r.Rev, ["error"] = error };
        }

        private static JsonObject OkMessage(string id, long rev, JsonNode? payload) =>
            new() { ["id"] = id, ["ok"] = true, ["rev"] = rev, ["payload"] = payload ?? new JsonObject() };

        private static JsonObject ErrorMessage(string id, long rev, RemoteException e) => new()
        {
            ["id"] = id,
            ["ok"] = false,
            ["rev"] = rev,
            ["error"] = new JsonObject
            {
                ["code"] = e.Code,
                ["message"] = e.Message,
                ["details"] = new JsonObject { ["applied"] = false },
            },
        };
    }
}
