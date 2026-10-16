// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Kantor.Core.Data;
using Kantor.Core.Library;
using Kantor.Core.Remote;

namespace Kantor.Tests;

/// <summary>
/// La ventana de Kantor simulada: mantiene un <see cref="HostState"/> y lo cambia como lo haría la real (cargar,
/// preguntar quién canta, cantar, resultados). No hay audio ni WPF: lo que se prueba es el contrato y las reglas.
/// </summary>
internal sealed class FakeHost : IKantorHost
{
    public HostState State { get; set; } = new() { MicrophoneSlots = [1, 2] };

    public List<string> Calls { get; } = [];

    public HostStartRequest? LastStart { get; private set; }

    /// <summary>Hilo en el que se pidió empezar la última canción (debe ser el de la interfaz).</summary>
    public int LastStartThread { get; private set; }

    public int DataChangedCount { get; private set; }

    public Dictionary<string, (string Artist, string Title)> Songs { get; } = [];

    public KantorStore? Store { get; set; }

    private long _resultSeq;

    public HostState Capture() => State;

    private IReadOnlyList<HostSlot> SlotsOf(IEnumerable<SelectionEntry> selection) =>
        [.. selection.OrderBy(s => s.Slot).Select(s => new HostSlot(s.Slot, s.PlayerId,
            Store?.GetPlayer(s.PlayerId)?.Name ?? "P" + s.PlayerId, State.MicrophoneSlots.Contains(s.Slot), 0, 0))];

    public void StartSong(HostStartRequest request)
    {
        Calls.Add("StartSong");
        LastStart = request;
        LastStartThread = Environment.CurrentManagedThreadId;
        var (artist, title) = Songs.TryGetValue(request.Path, out var s) ? s : ("Artista", "Título");
        var slots = request.Selection is not null ? SlotsOf(request.Selection) : State.Slots;
        if (request.Policy == SessionPolicy.Solo && request.Selection is null)
        {
            slots = [];
        }

        var ready = request.AutoConfirmSingers && slots.Any(x => x.HasMicrophone);
        State = State with
        {
            LoadedPath = request.Path,
            Artist = artist,
            Title = title,
            Solo = request.Policy == SessionPolicy.Solo,
            Slots = slots,
            Screen = ready ? Screens.Stage : Screens.WhoSings,
            Status = ready ? PlaybackStatuses.Playing : PlaybackStatuses.Preparing,
            RunId = Guid.NewGuid().ToString("N"),
            DurationSeconds = 200,
            PositionSeconds = 0,
        };
    }

    public void ConfirmSingers()
    {
        Calls.Add("ConfirmSingers");
        State = State with { Screen = Screens.Stage, Status = PlaybackStatuses.Playing };
    }

    public void Pause()
    {
        Calls.Add("Pause");
        State = State with { Status = PlaybackStatuses.Paused };
    }

    public void Resume()
    {
        Calls.Add("Resume");
        State = State with { Status = PlaybackStatuses.Playing };
    }

    public void Restart()
    {
        Calls.Add("Restart");
        State = State with
        {
            Screen = Screens.Stage,
            Status = PlaybackStatuses.Playing,
            RunId = Guid.NewGuid().ToString("N"),
            AbandonSeq = State.AbandonSeq + 1,
            PositionSeconds = 0,
        };
    }

    public void Stop()
    {
        Calls.Add("Stop");
        State = State with { Screen = Screens.Stage, Status = PlaybackStatuses.Idle, AbandonSeq = State.AbandonSeq + 1 };
    }

    public void ShowMenu()
    {
        Calls.Add("ShowMenu");
        State = State with { Screen = Screens.Menu, Status = PlaybackStatuses.Idle, AbandonSeq = State.AbandonSeq + 1 };
    }

    public void SkipResultsAnimation()
    {
        Calls.Add("SkipResultsAnimation");
        State = State with { ResultsAnimating = false };
    }

    public void CloseResults()
    {
        Calls.Add("CloseResults");
        ShowMenu();
    }

    public void SetFullscreen(bool enabled)
    {
        Calls.Add("SetFullscreen:" + enabled);
        State = State with { Display = State.Display with { Fullscreen = enabled } };
    }

    public void SetAutoAdvance(bool enabled)
    {
        Calls.Add("SetAutoAdvance:" + enabled);
        State = State with { AutoAdvance = enabled };
    }

    public void ApplySelection(IReadOnlyList<SelectionEntry> selection)
    {
        Calls.Add("ApplySelection");
        State = State with { Slots = SlotsOf(selection) };
    }

    public void DataChanged() => DataChangedCount++;

    // ---- Lo que haría la persona frente a la PC ---------------------------------------------------------------

    /// <summary>La canción terminó: resultados en pantalla, con la animación en curso.</summary>
    public void Finish(IReadOnlyList<(int Slot, long? PlayerId, string Name, int Score, string CommentId)> players,
        bool animating = true, bool save = true)
    {
        var performanceId = State.RunId!;
        var saved = false;
        if (save && Store is not null && !State.Solo)
        {
            Store.SavePerformance(new NewPerformance(performanceId, SongIdentity.For(State.Artist!, State.Title!),
                State.Title!, State.Artist!, 0, 1,
                [.. players.Where(p => p.PlayerId is not null)
                    .Select(p => new ResultInput(p.PlayerId!.Value, p.Slot, p.Score, p.CommentId))]));
            saved = true;
        }

        State = State with
        {
            Screen = Screens.Results,
            Status = PlaybackStatuses.Finished,
            ResultsAnimating = animating,
            LastResult = new HostResult(++_resultSeq, performanceId, saved, DateTimeOffset.UtcNow, State.LoadedPath!,
                State.Artist!, State.Title!, 0,
                [.. players.Select(p => new HostResultPlayer(p.Slot, p.PlayerId, p.Name, p.Score, p.CommentId))]),
        };
    }

    public void LocalInteraction() => State = State with { ResultsInteractionSeq = State.ResultsInteractionSeq + 1 };
}

internal sealed class FakeCovers : ICoverProvider
{
    public int Calls { get; private set; }

    public CoverImage? GetThumbnail(string coverPath, int size)
    {
        Calls++;
        return coverPath.EndsWith("missing.jpg", StringComparison.Ordinal)
            ? null
            : new CoverImage([0xFF, 0xD8, 0xFF, 0xD9], size, size);
    }
}

/// <summary>Una respuesta ya desarmada para que las aserciones sean legibles.</summary>
internal sealed record Reply(bool Ok, long Rev, JsonObject? Payload, string? Code, JsonObject? Details)
{
    public JsonObject P => Payload ?? throw new InvalidOperationException($"Sin payload ({Code}).");
}

/// <summary>Kantor "real" sin ventana: store SQLite, catálogo, cola, controlador y un hilo de interfaz propio.</summary>
internal sealed class RemoteHarness : IDisposable
{
    public const string Origin = """{"clientId":"phone-1","name":"Pablo"}""";

    private readonly SingleThreadInvoker _ui = new();
    private int _counter;

    public FakeHost Host { get; } = new();

    public KantorStore Store { get; }

    public LibraryCatalog Catalog { get; } = new();

    public QueueService Queue { get; }

    public KantorController Controller { get; }

    public FakeCovers Covers { get; } = new();

    public TimeSpan Now { get; set; } = TimeSpan.Zero;

    public ConcurrentQueue<JsonObject> Out { get; } = new();

    public Dictionary<string, string> Ids { get; } = [];

    public IUiInvoker Ui => _ui;

    public RemoteHarness(IEnumerable<SongEntry>? songs = null, bool withSession = true)
    {
        var clock = new TickingClock();
        Store = new KantorStore(":memory:", clock.Next);
        Host.Store = Store;
        if (withSession)
        {
            var ana = Store.CreatePlayer("Ana");
            var beto = Store.CreatePlayer("Beto");
            Store.StartSession(selection: [new SelectionEntry(1, ana.Id), new SelectionEntry(2, beto.Id)]);
            Host.State = Host.State with
            {
                Slots =
                [
                    new HostSlot(1, ana.Id, "Ana", true, 0, 0), new HostSlot(2, beto.Id, "Beto", true, 0, 0),
                ],
            };
        }

        Queue = new QueueService(Store, () => Host.State.Solo ? null : Store.GetOpenSession()?.Id, clock.Next);
        SetLibrary(songs ?? DefaultSongs());
        Controller = new KantorController(new KantorControllerOptions
        {
            Host = Host,
            Catalog = Catalog,
            Queue = Queue,
            Store = Store,
            Covers = Covers,
            Monotonic = () => Now,
            InstanceId = "test-instance",
        });
        Controller.Outbound += Out.Enqueue;
    }

    public static SongEntry Song(string name, string artist, string title, string? edition = null, string? genre = null,
        string? cover = null) =>
        new($@"C:\Songs\{name}.txt", title, artist, cover, false, genre, "2001", "Spanish", edition);

    public static IEnumerable<SongEntry> DefaultSongs() =>
    [
        Song("queen-radio-1", "Queen", "Radio Ga Ga", "Original", "Rock", @"C:\Songs\radio.jpg"),
        Song("queen-radio-2", "Queen", "Radio Ga Ga", "Karaoke", "Rock"),
        Song("abba-waterloo", "Abba", "Waterloo", null, "Pop"),
        Song("lost-cover", "Zeta", "Sin portada", null, null, @"C:\Songs\missing.jpg"),
    ];

    /// <summary>Reemplaza la biblioteca (asigna ids estables en el store, como la aplicación).</summary>
    public void SetLibrary(IEnumerable<SongEntry> songs)
    {
        var list = songs.ToList();
        var ids = Store.SyncLibraryEntries([.. list.Select(s => new LibraryFile(s.Path, 1, 1))], _ => "h", _ => false);
        foreach (var song in list)
        {
            Ids[song.Path] = ids[LibraryEntryKeys.KeyOf(song.Path)];
            Host.Songs[song.Path] = (song.Artist, song.Title);
        }

        Catalog.Replace(LibraryCatalog.Build(list, ids, _ => "stamp"));
    }

    public string IdOf(string name) => Ids[$@"C:\Songs\{name}.txt"];

    public void RunOnUi(Action action) => _ui.InvokeAsync(() =>
    {
        action();
        return 0;
    }).Wait();

    public T OnUi<T>(Func<T> work) => _ui.InvokeAsync(work).Result;

    public void Flush() => RunOnUi(() => Controller.Flush());

    public Reply Send(string type, string? payload = null, string? commandId = "auto", bool withOrigin = true,
        string? idOverride = null)
    {
        commandId = commandId == "auto" ? $"cmd-{++_counter:D6}" : commandId;
        var parts = new List<string> { $"\"id\":\"{idOverride ?? "r" + _counter}\"", $"\"type\":\"{type}\"" };
        if (commandId is not null)
        {
            parts.Add($"\"commandId\":\"{commandId}\"");
        }

        if (withOrigin)
        {
            parts.Add($"\"origin\":{Origin}");
        }

        parts.Add($"\"payload\":{payload ?? "{}"}");
        var json = "{" + string.Join(",", parts) + "}";
        var message = IncomingMessage.Parse(System.Text.Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow, out _);
        var response = Controller.HandleAsync(message, work => _ui.InvokeAsync(work)).Result;
        return new Reply(response.Ok, response.Rev, response.PayloadNode as JsonObject, response.ErrorCode,
            response.DetailsJson is null ? null : JsonNode.Parse(response.DetailsJson) as JsonObject);
    }

    /// <summary>Un comando de lectura (sin commandId ni origin).</summary>
    public Reply Read(string type, string? payload = null) => Send(type, payload, commandId: null, withOrigin: false);

    public long LongPlayerId(string name) => Store.ListPlayers().First(p => p.Name == name).Id;

    public List<JsonObject> DrainOut()
    {
        var all = new List<JsonObject>();
        while (Out.TryDequeue(out var message))
        {
            all.Add(message);
        }

        return all;
    }

    public void Dispose()
    {
        _ui.Dispose();
        Store.Dispose();
    }
}
