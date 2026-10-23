// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using System.Text.Json.Nodes;
using Kantor.Core.Data;
using Kantor.Core.Humor;
using Kantor.Core.Library;
using Kantor.Core.Results;

namespace Kantor.Core.Remote;

/// <summary>Dependencias del controlador (todas inyectables: las pruebas usan reloj, ventana y portadas falsos).</summary>
public sealed class KantorControllerOptions
{
    public required IKantorHost Host { get; init; }

    public required LibraryCatalog Catalog { get; init; }

    public required QueueService Queue { get; init; }

    /// <summary>Base de datos; <c>null</c> si no abrió (la integración sigue, sin historial ni partidas).</summary>
    public KantorStore? Store { get; init; }

    public ICoverProvider? Covers { get; init; }

    /// <summary>Reloj monotónico (para la cuenta del avance automático y la calma de <c>display</c>).</summary>
    public Func<TimeSpan>? Monotonic { get; init; }

    public string KantorVersion { get; init; } = "1.0.0";

    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");
}

/// <summary>
/// El estado autoritativo de Kantaria visto desde el control remoto y las operaciones que acepta. No conoce WPF ni
/// la red: la ventana entra por <see cref="IKantorHost"/> y los mensajes salen por <see cref="Outbound"/>. Todo lo
/// que toca la ventana o muta estado corre en el hilo de la interfaz; las lecturas pesadas (biblioteca, historial)
/// corren fuera de él sobre datos inmutables o protegidos.
/// </summary>
public sealed partial class KantorController
{
    private static readonly TimeSpan DisplayCalm = TimeSpan.FromMilliseconds(250);

    private readonly KantorControllerOptions _o;
    private readonly IKantorHost _host;
    private readonly KantorStore? _store;
    private readonly LibraryCatalog _catalog;
    private readonly QueueService _queue;
    private readonly StatePublisher _publisher = new();
    private readonly CommandDeduplicator _dedupe = new();
    private readonly AutoAdvanceMachine _auto = new();
    private readonly Func<TimeSpan> _clock;
    private readonly Dictionary<string, HumorBank> _banks = [];
    private readonly object _bankGate = new();

    // seguimiento de lo que ve el controlador entre una lectura y la siguiente (hilo de la interfaz)
    private long _lastResultSeq;
    private long _lastAbandonSeq;
    private int _selectionRev = 1;
    private string _selectionSignature = string.Empty;
    private string? _lastPlaybackStable;
    private double _baselinePosition;
    private TimeSpan _baselineAt;
    private double _baselineRate;
    private int _jumps;
    private string? _publishedDisplayStable;
    private string? _displayCandidate;
    private TimeSpan _displayCandidateSince;
    private HostDisplay? _publishedDisplay;
    private AutoAdvanceOutput _autoOutput;
    private string? _autoItemId;
    private string? _autoBlocked;
    private long _autoBlockedSeq;
    private string? _lastScoreTick;
    private TimeSpan? _lastScoreTickAt;
    private HostResult? _announcedResult;

    public KantorController(KantorControllerOptions options)
    {
        _o = options;
        _host = options.Host;
        _store = options.Store;
        _catalog = options.Catalog;
        _queue = options.Queue;
        var started = System.Diagnostics.Stopwatch.StartNew();
        _clock = options.Monotonic ?? (() => started.Elapsed);
        _lastResultSeq = _host.Capture().LastResult?.Seq ?? 0;
    }

    /// <summary>Identifica esta ejecución de Kantaria: cambia en cada arranque.</summary>
    public string InstanceId => _o.InstanceId;

    public long Rev => _publisher.Rev;

    /// <summary>Segundos que faltan para que empiece sola la próxima canción (<c>null</c> sin cuenta en marcha).</summary>
    public double? AutoAdvanceSecondsRemaining =>
        _autoOutput.Kind == AutoAdvanceKind.Counting ? _autoOutput.Remaining.TotalSeconds : null;

    /// <summary>Un mensaje saliente (<c>kantor.state.changed</c>, <c>kantor.result</c>, <c>kantor.score.tick</c>).</summary>
    public event Action<JsonObject>? Outbound;

    public static IReadOnlyDictionary<string, int> Capabilities { get; } = new Dictionary<string, int>
    {
        ["state"] = 1, ["library"] = 1, ["queue"] = 1, ["players"] = 1, ["session"] = 1, ["playback"] = 1,
        ["results"] = 1, ["screen"] = 1,
    };

    // ---- Mensajes de arranque -----------------------------------------------------------------------------

    public JsonObject BuildHello() => new()
    {
        ["type"] = "kantor.hello",
        ["payload"] = new JsonObject
        {
            ["kantorVersion"] = _o.KantorVersion,
            ["protocolVersion"] = Protocol.ProtocolVersion,
            ["contractRevision"] = Protocol.ContractRevision,
            ["instanceId"] = InstanceId,
            ["capabilities"] = Json.ToNode(Capabilities),
            ["limits"] = new JsonObject
            {
                ["maxFrameBytes"] = Protocol.MaxFrameBytes,
                ["maxPageSize"] = Protocol.MaxPageSize,
                ["maxQueue"] = QueueService.MaxPending,
                ["maxSlots"] = KantorStore.MaxPlayersPerPerformance,
                ["maxQueryChars"] = Protocol.MaxQueryChars,
                ["maxPlayerNameChars"] = Protocol.MaxPlayerNameChars,
            },
        },
    };

    /// <summary>El <c>kantor.state</c> completo (también es la respuesta de <c>kantor.state.get</c>). Hilo de la interfaz.</summary>
    public JsonObject BuildSnapshotMessage()
    {
        Flush();
        var (rev, sections) = _publisher.Snapshot();
        sections["instanceId"] = InstanceId;
        return new JsonObject { ["type"] = "kantor.state", ["rev"] = rev, ["payload"] = sections };
    }

    // ---- Lectura del estado y publicación -----------------------------------------------------------------

    /// <summary>
    /// Lee la ventana, concilia la cola y el avance automático con lo que pasó, y publica lo que cambió (una sola
    /// revisión por llamada). Se llama desde el temporizador de la interfaz y después de cada mutación remota.
    /// </summary>
    public StatePublication? Flush()
    {
        var host = _host.Capture();
        var announce = Reconcile(host);
        host = _host.Capture();

        var now = _clock();
        var sections = BuildSections(host, now);
        var publication = _publisher.Update(sections);
        if (publication is not null)
        {
            Outbound?.Invoke(new JsonObject
            {
                ["type"] = "kantor.state.changed",
                ["rev"] = publication.Rev,
                ["payload"] = publication.Changed,
            });
        }

        if (announce is not null)
        {
            Outbound?.Invoke(new JsonObject
            {
                ["type"] = "kantor.result",
                ["rev"] = _publisher.Rev,
                ["payload"] = announce,
            });
        }

        EmitScoreTick(host, now);
        return publication;
    }

    private void EmitScoreTick(HostState host, TimeSpan now)
    {
        if (host.Status != PlaybackStatuses.Playing || host.Slots.Count == 0)
        {
            return;
        }

        var tick = new JsonObject
        {
            ["slots"] = new JsonArray([.. host.Slots.Select(s => (JsonNode?)new JsonObject
            {
                ["slot"] = s.Slot,
                ["score"] = s.Score,
                ["accuracy"] = Math.Round(s.Accuracy, 3),
            })]),
        };
        var text = tick.ToJsonString();
        if (text == _lastScoreTick || (_lastScoreTickAt is { } last && now - last < TimeSpan.FromSeconds(1)))
        {
            return;
        }

        _lastScoreTick = text;
        _lastScoreTickAt = now;
        Outbound?.Invoke(new JsonObject { ["type"] = "kantor.score.tick", ["payload"] = tick });
    }

    /// <summary>
    /// Lleva la cola y el avance automático al día con lo que hizo la ventana (también por la interfaz local).
    /// Devuelve el <c>PerformanceDetail</c> a anunciar como <c>kantor.result</c> si terminó una canción.
    /// </summary>
    private JsonObject? Reconcile(HostState host)
    {
        // cambió quién ocupa los puestos (desde cualquier origen): nueva revisión de la selección
        var signature = string.Join(",", host.Slots.Select(s => $"{s.Slot}:{s.PlayerId}"));
        if (signature != _selectionSignature)
        {
            if (_selectionSignature.Length > 0 || signature.Length > 0)
            {
                _selectionRev++;
            }

            _selectionSignature = signature;
        }

        JsonObject? announce = null;
        var running = _queue.Running();

        if (host.LastResult is { } result && result.Seq != _lastResultSeq)
        {
            _lastResultSeq = result.Seq;
            if (running is not null && SamePath(ResolvePath(running.LibraryEntryId), result.SongPath))
            {
                _queue.Complete(running.ItemId, result.Saved ? result.PerformanceId : null);
                running = null;
            }

            _announcedResult = result;
            announce = BuildResultDetail(result, host.Language, includeSaved: true);
        }

        if (running is not null)
        {
            var path = ResolvePath(running.LibraryEntryId);
            var elsewhere = host.Screen != Screens.Loading && !SamePath(path, host.LoadedPath);
            if (host.AbandonSeq != _lastAbandonSeq || elsewhere)
            {
                _queue.Cancel(running.ItemId);
            }
        }

        _lastAbandonSeq = host.AbandonSeq;

        StepAutoAdvance(host);
        return announce;
    }

    private void StepAutoAdvance(HostState host)
    {
        var onResults = host.Screen == Screens.Results && host.Status == PlaybackStatuses.Finished
                        && host.LastResult is not null;
        var next = NextEligibility(host, out var nextItem);
        var input = new AutoAdvanceInput(onResults, host.LastResult?.Seq ?? 0, host.ResultsAnimating,
            host.ResultsInteractionSeq, host.AutoAdvance, next);
        _autoOutput = _auto.Step(_clock(), input);

        // el aviso de "no se puede avanzar" dura mientras sigan estos resultados en pantalla
        var seq = host.LastResult?.Seq ?? 0;
        if (!onResults || seq != _autoBlockedSeq)
        {
            _autoBlocked = null;
        }

        switch (_autoOutput.Kind)
        {
            case AutoAdvanceKind.Counting:
                _autoItemId = nextItem?.ItemId;
                _autoBlocked = null;
                break;
            case AutoAdvanceKind.Blocked:
                _autoBlocked = _autoOutput.Reason;
                _autoBlockedSeq = seq;
                _autoItemId = null;
                break;
            case AutoAdvanceKind.Fire:
                _autoItemId = null;
                try
                {
                    StartNext(autoConfirm: true, origin: null);
                }
                catch (RemoteException e)
                {
                    _autoBlocked = e.Details?["reason"]?.GetValue<string>() ?? e.Code;
                    _autoBlockedSeq = seq;
                }

                break;
            default:
                _autoItemId = null;
                break;
        }
    }

    /// <summary>
    /// ¿Se puede avanzar a la próxima? <c>null</c> = sí. Hace falta una partida abierta, un pendiente disponible y
    /// cantantes válidos: los puestos del elemento o, si no tiene, la selección vigente de la partida.
    /// </summary>
    private string? NextEligibility(HostState host, out QueueEntry? next)
    {
        next = _queue.Pending().FirstOrDefault();
        if (next is null)
        {
            return "queue-empty";
        }

        if (_catalog.Current.Find(next.LibraryEntryId) is null)
        {
            return "entry-unavailable";
        }

        if (_store is null || host.Solo || _store.GetOpenSession() is not { } session)
        {
            return "no-session";
        }

        var wanted = next.Slots.Count > 0
            ? next.Slots.Select(s => (s.Slot, s.PlayerId)).ToList()
            : host.Slots.Select(s => (s.Slot, s.PlayerId)).ToList();
        if (wanted.Count == 0)
        {
            return "no-singers";
        }

        var members = _store.SessionPlayers(session.Id).Where(p => !p.Archived).Select(p => p.Id).ToHashSet();
        foreach (var (slot, playerId) in wanted)
        {
            if (!members.Contains(playerId))
            {
                return "invalid-singer";
            }

            if (!host.MicrophoneSlots.Contains(slot))
            {
                return "no-microphone";
            }
        }

        return null;
    }

    private Dictionary<string, SectionValue> BuildSections(HostState host, TimeSpan now)
    {
        var sections = new Dictionary<string, SectionValue>(StringComparer.Ordinal);

        sections["screen"] = Plain(JsonValue.Create(host.Screen));
        sections["playback"] = BuildPlayback(host, now);
        sections["session"] = Plain(BuildSession());
        sections["slots"] = BuildSlots(host);
        sections["queue"] = Plain(BuildQueueSection(host));
        sections["lastResult"] = Plain(host.LastResult is null
            ? null
            : new JsonObject
            {
                ["performanceId"] = host.LastResult.Saved ? host.LastResult.PerformanceId : null,
                ["finishedAt"] = host.LastResult.FinishedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["saved"] = host.LastResult.Saved,
            });
        sections["settings"] = Plain(new JsonObject
        {
            ["autoAdvance"] = host.AutoAdvance,
            ["language"] = host.Language,
            ["remoteControl"] = true,
        });
        sections["autoAdvanceCountdown"] = BuildCountdown();
        sections["library"] = Plain(new JsonObject
        {
            ["libraryRev"] = _catalog.Current.Rev,
            ["scan"] = _catalog.Scanning ? "scanning" : "idle",
            ["entryCount"] = _catalog.Current.Items.Count,
            ["folderCount"] = _catalog.FolderCount,
        });
        sections["display"] = BuildDisplay(host, now);
        sections["attention"] = Plain(BuildAttention(host));
        return sections;
    }

    private static SectionValue Plain(JsonNode? node) => new(node, node?.ToJsonString() ?? "null");

    private SectionValue BuildPlayback(HostState host, TimeSpan now)
    {
        var item = host.LoadedPath is null ? null : _catalog.Current.FindByPath(host.LoadedPath);
        var running = _queue.Running();
        var rate = host.Status == PlaybackStatuses.Playing ? 1 : 0;
        var hasSong = host.LoadedPath is not null;
        var identity = hasSong && host.Artist is not null && host.Title is not null
            ? SongIdentity.For(host.Artist, host.Title)
            : null;

        JsonObject Node(bool withPosition) => new()
        {
            ["status"] = host.Status,
            ["positionSeconds"] = withPosition ? Math.Round(host.PositionSeconds, 3) : 0,
            ["rate"] = rate,
            ["durationSeconds"] = hasSong ? Math.Round(host.DurationSeconds, 3) : null,
            ["libraryEntryId"] = item?.EntryId,
            ["songIdentity"] = item?.SongIdentity ?? identity,
            ["artist"] = host.Artist,
            ["title"] = host.Title,
            ["edition"] = item?.Entry.Edition,
            ["coverId"] = item?.CoverId,
            ["queueItemId"] = hasSong ? running?.ItemId : null,
            ["performanceId"] = hasSong ? host.RunId : null,
            ["difficulty"] = host.Difficulty,
            ["solo"] = host.Solo,
        };

        var stable = Node(withPosition: false).ToJsonString();
        var expected = _baselinePosition + (now - _baselineAt).TotalSeconds * _baselineRate;
        var jumped = rate == 1 && Math.Abs(host.PositionSeconds - expected) > 1.0;
        if (jumped)
        {
            _jumps++;
        }

        if (stable != _lastPlaybackStable || jumped)
        {
            _lastPlaybackStable = stable;
            _baselinePosition = host.PositionSeconds;
            _baselineAt = now;
            _baselineRate = rate;
        }

        // la posición se mide al serializar; el Agent extrapola desde que lo recibe
        return new SectionValue(Node(withPosition: true), stable + "#" + _jumps);
    }

    private JsonNode? BuildSession()
    {
        if (_store is null || _store.GetOpenSession() is not { } session)
        {
            return null;
        }

        return new JsonObject
        {
            ["sessionId"] = session.Id,
            ["name"] = session.Name,
            ["startedAt"] = session.StartedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["playerIds"] = new JsonArray([.. _store.SessionPlayers(session.Id).Select(p => (JsonNode?)p.Id)]),
        };
    }

    private SectionValue BuildSlots(HostState host)
    {
        var available = host.MicrophoneSlots.OrderBy(s => s).ToList();
        var slotNumbers = available.Union(host.Slots.Select(s => s.Slot)).OrderBy(s => s).ToList();
        JsonObject Node(bool withScore) => new()
        {
            ["selectionRev"] = _selectionRev,
            ["maxSlots"] = available.Count,
            ["availableSlots"] = new JsonArray([.. available.Select(s => (JsonNode?)s)]),
            ["items"] = new JsonArray([.. slotNumbers.Select(number =>
            {
                var entry = host.Slots.FirstOrDefault(s => s.Slot == number);
                return (JsonNode?)new JsonObject
                {
                    ["slot"] = number,
                    ["playerId"] = entry is null ? null : entry.PlayerId,
                    ["name"] = entry?.Name,
                    ["score"] = withScore && entry is not null ? entry.Score : 0,
                    ["accuracy"] = withScore && entry is not null ? Math.Round(entry.Accuracy, 3) : 0,
                };
            })]),
        };
        return new SectionValue(Node(withScore: true), Node(withScore: false).ToJsonString());
    }

    private JsonNode BuildQueueSection(HostState host)
    {
        var items = _queue.List();
        return new JsonObject
        {
            ["queueRev"] = _queue.Rev,
            ["persistent"] = _queue.Persistent,
            ["pendingCount"] = items.Count(i => i.State == QueueState.Pending),
            ["items"] = new JsonArray([.. items.Select(i => (JsonNode?)QueueItemNode(i))]),
        };
    }

    private JsonObject QueueItemNode(QueueEntry e) => new()
    {
        ["itemId"] = e.ItemId,
        ["libraryEntryId"] = e.LibraryEntryId,
        ["songIdentity"] = e.SongIdentity,
        ["artist"] = e.Artist,
        ["title"] = e.Title,
        ["edition"] = e.Edition,
        ["state"] = e.State.ToString().ToLowerInvariant(),
        ["order"] = e.Order,
        ["available"] = _catalog.Current.Find(e.LibraryEntryId) is not null,
        ["slots"] = new JsonArray([.. e.Slots.Select(s => (JsonNode?)new JsonObject
        {
            ["slot"] = s.Slot, ["playerId"] = s.PlayerId,
        })]),
        ["addedBy"] = e.AddedById is null
            ? null
            : new JsonObject { ["clientId"] = e.AddedById, ["name"] = e.AddedByName },
        ["addedAt"] = e.AddedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        ["finishedAt"] = e.FinishedAt?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        ["performanceId"] = e.PerformanceId,
    };

    private SectionValue BuildCountdown()
    {
        if (_autoOutput.Kind != AutoAdvanceKind.Counting)
        {
            return Plain(null);
        }

        // los segundos restantes no entran en la clave: la cuenta no publica una revisión por cada segundo
        var node = new JsonObject
        {
            ["secondsRemaining"] = Math.Round(_autoOutput.Remaining.TotalSeconds, 2),
            ["itemId"] = _autoItemId,
        };
        return new SectionValue(node, "counting:" + _autoItemId);
    }

    private SectionValue BuildDisplay(HostState host, TimeSpan now)
    {
        var current = host.Display;
        var stable = JsonDisplay(current).ToJsonString();
        if (_publishedDisplay is null)
        {
            _publishedDisplay = current;
            _publishedDisplayStable = stable;
            _displayCandidate = stable;
            _displayCandidateSince = now;
        }
        else if (stable != _displayCandidate)
        {
            _displayCandidate = stable;
            _displayCandidateSince = now;
        }
        else if (stable != _publishedDisplayStable && now - _displayCandidateSince >= DisplayCalm)
        {
            // dejó de moverse hace 250 ms: se publica
            _publishedDisplay = current;
            _publishedDisplayStable = stable;
        }

        var shown = JsonDisplay(_publishedDisplay);
        return new SectionValue(shown, shown.ToJsonString());
    }

    private static JsonObject JsonDisplay(HostDisplay d) => new()
    {
        ["fullscreen"] = d.Fullscreen,
        ["processId"] = d.ProcessId,
        ["windowHandle"] = d.WindowHandle,
        ["monitor"] = d.Monitor is null
            ? null
            : new JsonObject
            {
                ["deviceName"] = d.Monitor.DeviceName,
                ["primary"] = d.Monitor.Primary,
                ["bounds"] = new JsonObject
                {
                    ["x"] = d.Monitor.X, ["y"] = d.Monitor.Y, ["width"] = d.Monitor.Width, ["height"] = d.Monitor.Height,
                },
                ["dpiScale"] = Math.Round(d.Monitor.DpiScale, 3),
            },
    };

    private JsonNode BuildAttention(HostState host)
    {
        var items = new List<HostAttention>(host.Attention);
        if (!host.StoreAvailable)
        {
            items.Add(new HostAttention("DATABASE_UNAVAILABLE"));
        }

        if (!host.HasLibraryFolders)
        {
            items.Add(new HostAttention("NO_LIBRARY_FOLDERS"));
        }

        if (host.MicrophoneSlots.Count == 0)
        {
            items.Add(new HostAttention("NO_MICROPHONES"));
        }

        if (host.Solo && host.Status == PlaybackStatuses.Finished)
        {
            items.Add(new HostAttention("SOLO_NOT_SAVED"));
        }

        if (_autoBlocked is not null)
        {
            items.Add(new HostAttention("AUTO_ADVANCE_BLOCKED",
                new Dictionary<string, string> { ["reason"] = _autoBlocked }));
        }

        return new JsonArray([.. items.Select(a => (JsonNode?)new JsonObject
        {
            ["code"] = a.Code,
            ["params"] = a.Params is null ? null : Json.ToNode(a.Params),
        })]);
    }

    // ---- Utilidades ---------------------------------------------------------------------------------------

    /// <summary>Ruta de una entrada: la de la biblioteca vigente o, si ya no está, la última conocida.</summary>
    private string? ResolvePath(string entryId) =>
        _catalog.Current.Find(entryId)?.Entry.Path ?? _store?.GetLibraryEntry(entryId)?.Path;

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(LibraryEntryKeys.KeyOf(a), LibraryEntryKeys.KeyOf(b), StringComparison.Ordinal);

    private HumorBank Bank(string language)
    {
        language = HumorBank.NormalizeLanguage(language);
        lock (_bankGate)
        {
            if (!_banks.TryGetValue(language, out var bank))
            {
                _banks[language] = bank = HumorBank.Load(language);
            }

            return bank;
        }
    }

    private string? CommentText(string? commentId, string language)
    {
        if (commentId is null)
        {
            return null;
        }

        return Bank(language).TryGet(commentId, out var text) ? text : null;
    }

    private static string RankId(ResultRank rank) => rank switch
    {
        ResultRank.SystemSurvived => "system-survived",
        ResultRank.NiceTry => "nice-try",
        ResultRank.Acceptable => "acceptable",
        ResultRank.NiceWork => "nice-work",
        ResultRank.Impressive => "impressive",
        ResultRank.Superstar => "superstar",
        ResultRank.Legendary => "legendary",
        _ => "perfect",
    };

    private JsonObject ResultNode(long playerId, string name, int slot, int score, int position, int competition,
        string? commentId, string language)
    {
        var rank = ResultRanks.For(score);
        return new JsonObject
        {
            ["playerId"] = playerId == 0 ? null : playerId,
            ["name"] = name,
            ["slot"] = slot,
            ["score"] = score,
            ["position"] = position,
            ["competitionPoints"] = competition,
            ["seal"] = new JsonObject { ["rankId"] = RankId(rank), ["text"] = ResultRanks.Label(rank, language) },
            ["commentId"] = commentId,
            ["comment"] = CommentText(commentId, language),
        };
    }

    private JsonObject BuildPerformanceDetail(PerformanceView view, string language)
    {
        var points = RankingStats.CompetitionPoints(view.Results);
        var p = view.Performance;
        return new JsonObject
        {
            ["performanceId"] = p.Id,
            ["sessionId"] = p.SessionId,
            ["songIdentity"] = p.SongId,
            ["libraryEntryId"] = p.LibraryEntryId,
            ["artist"] = p.Artist,
            ["title"] = p.Title,
            ["edition"] = p.Edition,
            ["finishedAt"] = p.FinishedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["difficulty"] = DifficultyName(p.Difficulty),
            ["scoringVersion"] = p.ScoringVersion,
            ["rankingVersion"] = RankingStats.Version,
            ["results"] = new JsonArray([.. view.Results.Select((r, i) => (JsonNode?)ResultNode(r.PlayerId,
                r.PlayerName, r.Slot, r.Score, r.Position, points[i], r.CommentId, language))]),
        };
    }

    /// <summary>El detalle de un resultado: el guardado (con lo persistido) o, si no se guardó, el de la pantalla.</summary>
    private JsonObject BuildResultDetail(HostResult result, string language, bool includeSaved)
    {
        if (result.Saved && result.PerformanceId is not null && _store?.GetPerformance(result.PerformanceId) is { } view)
        {
            var saved = BuildPerformanceDetail(view, language);
            if (includeSaved)
            {
                saved["saved"] = true;
            }

            return saved;
        }

        var ordered = result.Players.OrderBy(p => p.Slot).ToList();
        var positions = ScoreRanking.Positions(ordered.Select(p => p.Score).ToList());
        var views = ordered.Select((p, i) => new PerformanceResultView(p.PlayerId ?? 0, p.Name, p.Slot, p.Score,
            positions[i], p.CommentId)).ToList();
        var points = RankingStats.CompetitionPoints(views);
        var item = _catalog.Current.FindByPath(result.SongPath);
        var detail = new JsonObject
        {
            ["performanceId"] = result.Saved ? result.PerformanceId : null,
            ["sessionId"] = null,
            ["songIdentity"] = item?.SongIdentity ?? SongIdentity.For(result.Artist, result.Title),
            ["libraryEntryId"] = item?.EntryId,
            ["artist"] = result.Artist,
            ["title"] = result.Title,
            ["edition"] = item?.Entry.Edition,
            ["finishedAt"] = result.FinishedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["difficulty"] = DifficultyName(result.Difficulty),
            ["scoringVersion"] = Scoring.ScoringRules.ScoringVersion,
            ["rankingVersion"] = RankingStats.Version,
            ["results"] = new JsonArray([.. ordered.Select((p, i) => (JsonNode?)ResultNode(p.PlayerId ?? 0, p.Name,
                p.Slot, p.Score, positions[i], points[i], p.CommentId, language))]),
        };
        if (includeSaved)
        {
            detail["saved"] = result.Saved;
        }

        return detail;
    }

    private static string DifficultyName(int difficulty) => difficulty switch
    {
        1 => "medium",
        2 => "hard",
        _ => "easy",
    };
}
