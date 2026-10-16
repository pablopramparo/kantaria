// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kantor.App.Audio;
using Kantor.App.Characters;
using Kantor.App.Options;
using Kantor.App.Players;
using Kantor.App.Results;
using Kantor.App.Stage;
using Kantor.Core.Audio;
using Kantor.Core.Characters;
using Kantor.Core.Data;
using Kantor.Core.Feedback;
using Kantor.Core.Menu;
using Kantor.Core.Humor;
using Kantor.Core.Guest;
using Kantor.Core.Library;
using Kantor.Core.Remote;
using Kantor.Core.Results;
using Kantor.Core.Scoring;
using Kantor.Core.Settings;
using Kantor.Core.Songs;
using Kantor.Core.Timing;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Kantor.App;

public partial class MainWindow : Window
{
    private IAudioEngine _engine = null!;
    private ISongPlayer _player = null!;
    private ActiveInput _input = null!;
    private string? _engineNote;
    private string? _audioPath;
    private readonly DispatcherTimer _scoreTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Song? _song;
    private readonly PhraseFeedbackTracker _feedback = new();

    /// <summary>Un jugador en juego: su puesto de micrófono, su puntaje y la voz que canta.</summary>
    private sealed record PlayerState(int Slot, VoiceScorer? Scorer, TimeSpan Delay, int VoiceIndex);

    /// <summary>Resultado de un jugador ya sorteado: se conserva el ID del comentario al cambiar de idioma.</summary>
    private sealed record PlayerOutcome(int Slot, int Score, HumorPick Pick);

    private List<PlayerState> _players = [];
    private List<PlayerOutcome>? _outcomes;
    private AsioAvailability? _asioAvailability;
    private bool _optionsFromMenu;

    // Jugadores, sesión e historial (SQLite). Si la base no abre, la aplicación sigue sin historial.
    private KantorStore? _store;
    private SessionRecord? _session;
    private List<SelectionEntry> _selection = [];
    private Dictionary<long, PlayerRecord> _playerCache = [];
    private readonly CharacterAssets _characters = new();
    private readonly UserCharacterStore _userCharacters = new(UserCharacterStore.DefaultRoot());
    private bool _playersFromMenu;
    private bool _pendingPlay;

    // Control remoto (Avyn): la biblioteca con identificadores, la cola compartida y el controlador del estado.
    private readonly LibraryCatalog _catalog = new();
    private QueueService _queue = null!;
    private KantorController? _controller;
    private KantorIpcServer? _server;
    private string? _remoteFailure;
    private readonly DispatcherTimer _remoteTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private string? _loadedPath;
    private bool _autoConfirm;
    private long _abandonSeq;
    private long _resultsInteraction;
    private long _resultSeq;
    private HostResult? _lastResult;
    private string? _resultRunId;
    private List<PlayerOutcome>? _runOutcomes;
    private bool _lastSaveOk;
    private bool _saveFailed;
    private string? _loadFailedPath;
    private List<IndexItem> _libraryIndex = [];

    // Identidad de la ejecución en curso: se renueva al empezar de nuevo y se consume al guardar.
    private string _runId = Guid.NewGuid().ToString("N");
    private bool _runSaved;
    private TimeSpan _lastPosition;
    private bool _fullscreen;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResize;
    private bool _restoreTopmost;
    private Rect _restoreBounds;

    private readonly string _settingsPath = UserSettingsStore.DefaultPath();
    private UserSettings _prefs;
    private bool _restoringSettings;
    private List<MicrophoneSetting>? _micsConfig;
    private bool _restoringInput;
    private Dictionary<string, int>? _profiles;
    private int _windowsDelayMs;
    private int _asioDelayMs;
    private readonly Dictionary<string, HumorBank> _banks = [];
    private CommentSelector? _selector;

    public MainWindow()
    {
        Diag.Start(Dispatcher);
        using (Diag.Time("inicio: InitializeComponent", 30))
        {
            InitializeComponent();
        }

        using (Diag.Time("inicio: ajustes", 30))
        {
            _prefs = UserSettingsStore.Load(_settingsPath);
        }

        using (Diag.Time("inicio: base de datos", 30))
        {
            OpenStore();
            _queue = new QueueService(_store, () => QueueSession()?.Id);
            _queue.RecoverInterrupted(); // un elemento "en ejecución" de la vez anterior quedó colgado
        }

        _micsConfig = _prefs.Microphones is { } stored0 ? [.. stored0] : null;
        _profiles = _prefs.DelayProfiles is { } stored ? new Dictionary<string, int>(stored) : null;
        _windowsDelayMs = _prefs.VoiceDelayMs;
        _asioDelayMs = _prefs.AsioVoiceDelayMs;

        _restoringSettings = true;
        try
        {
            DifficultyBox.SelectedIndex = (int)_prefs.Difficulty;
            DisplayBox.SelectedIndex = _prefs.VoiceDisplay;
            LanguageBox.SelectedIndex = _prefs.CommentLanguage == "en" ? 1 : 0;
        }
        finally
        {
            _restoringSettings = false;
        }

        using (Diag.Time("inicio: motor de audio", 30))
        {
            BuildAudio(_prefs.AudioEngine);
        }

        using (Diag.Time("inicio: opciones", 30))
        {
            WireOptions();
        }

        using (Diag.Time("inicio: control remoto", 30))
        {
            StartRemote();
            InitGuest();
        }

        _scoreTimer.Tick += (_, _) =>
        {
            AdvanceScore();
            SyncVideo();
            UpdateStageControls();
        };

        Loaded += (_, _) =>
        {
            Diag.Mark("ventana cargada");
            if (_prefs.StartFullscreen)
            {
                using (Diag.Time("inicio: pantalla completa", 30))
                {
                    SetFullscreen(true);
                }
            }

            using (Diag.Time("inicio: entradas de audio", 30))
            {
                RestoreInput();
            }

            _scoreTimer.Start();
            StartMusic(); // la interfaz ya está lista: empieza la música de inicio
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                LoadSong(args[1]);
            }
            else
            {
                using (Diag.Time("inicio: menú", 30))
                {
                    ShowMenu();
                }

                using (Diag.Time("inicio: biblioteca", 30))
                {
                    PrimeLibrary();
                }
            }

            using (Diag.Time("inicio: sesión abierta", 30))
            {
                OfferOpenSession();
            }

            Diag.Mark("inicio terminado");

            // Recién con la ventana lista: preparar libVLC compite por CPU con el arranque y lo alarga.
            Dispatcher.BeginInvoke(Video.WarmUp, DispatcherPriority.ApplicationIdle);
        };
        Activated += OnWindowActiveChanged;
        Deactivated += OnWindowActiveChanged;
        Closed += (_, _) =>
        {
            _scoreTimer.Stop();
            StopRemote();
            StopGuest();
            StopMusic();
            Video.Dispose();
            DisposeAudio();
            _preview.Dispose();
            _store?.Dispose(); // la sesión abierta NO se cierra: se retoma al volver a abrir Kantor
        };
    }

    // ---- Motor de audio ----------------------------------------------------------------------------------

    /// <summary>
    /// Crea el motor pedido (o Windows si ese no está disponible), el reproductor y la entrada activa, y los
    /// vuelve a atar al escenario y a la canción cargada. La configuración del motor es global.
    /// </summary>
    private void BuildAudio(AudioEngineKind requested)
    {
        DisposeAudio();

        _engine = AudioEngineFactory.Create(requested, _prefs.AsioDriver, _prefs.AsioOutputPair, out _engineNote);
        _player = _engine.CreatePlayer();
        _input = new ActiveInput(_player, _engine.CreateCapture);
        _input.Changed += OnInputChanged;
        _player.Ended += OnPlayerEnded;
        _engine.Failed += OnEngineFailed;

        if (_engineNote is not null)
        {
            StatusText.Text = _engineNote;
        }

        ApplyEngineDelay();

        if (_audioPath is not null && _song is not null)
        {
            try
            {
                _player.Load(_audioPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                          or System.Runtime.InteropServices.COMException)
            {
                StatusText.Text = $"No se pudo volver a cargar el audio con el motor nuevo: {e.Message}";
                _song = null;
                _audioPath = null;
            }
        }

        Stage.Show(_song, _song is null ? null : _player);
        CreateScorers();
        UpdateInputStatus();
        UpdateControls();
    }

    private void DisposeAudio()
    {
        if (_input is not null)
        {
            _input.Changed -= OnInputChanged;
            _input.Dispose();
        }

        if (_player is not null)
        {
            _player.Ended -= OnPlayerEnded;
            _player.Dispose();
        }

        if (_engine is not null)
        {
            _engine.Failed -= OnEngineFailed;
            _engine.Dispose();
        }
    }

    private void OnPlayerEnded(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        UpdateControls();
        ShowResult();
    });

    // ---- Comentario del resultado -------------------------------------------------------------------------

    /// <summary>
    /// Elige una sola vez el comentario del resultado y lo muestra. Repintar o cambiar de idioma solo vuelve a
    /// leer el texto de ese mismo ID; nunca vuelve a sortear.
    /// </summary>
    private void ShowResult()
    {
        var scored = _players.Where(p => p.Scorer is not null).ToList();
        if (scored.Count == 0)
        {
            return;
        }

        // Una misma ejecución se sortea una sola vez: un aviso de fin repetido muestra y guarda lo mismo.
        if (_resultRunId == _runId && _runOutcomes is not null)
        {
            _outcomes = _runOutcomes;
        }
        else
        {
            try
            {
                _selector ??= new CommentSelector(GetBank(HumorBank.DefaultLanguage), new SystemRandomSource(),
                    _prefs.NonsenseProbability);
                var outcomes = new List<PlayerOutcome>();
                foreach (var player in scored)
                {
                    var score = Math.Min(player.Scorer!.Snapshot.Total, HumorRanges.PerfectScore);
                    outcomes.Add(new PlayerOutcome(player.Slot, score, _selector.Pick(score)));
                }

                _outcomes = outcomes;
                _runOutcomes = outcomes;
            }
            catch (InvalidOperationException e)
            {
                StatusText.Text = $"No se pudieron cargar los comentarios: {e.Message}";
                return;
            }
        }

        // Se guarda acá, donde la canción terminó de verdad (no al mostrar la pantalla ni al repintarla).
        _lastSaveOk = false;
        SavePerformance(_outcomes);
        if (_resultRunId != _runId)
        {
            _resultRunId = _runId;
            RecordResult(_outcomes);
        }

        Results.Visibility = Visibility.Visible;
        RefreshResult(animate: true);
    }

    /// <summary>Deja el resultado a disposición del control remoto (con el comentario que se sorteó y mostró).</summary>
    private void RecordResult(IReadOnlyList<PlayerOutcome> outcomes)
    {
        if (_loadedPath is null || _song is null)
        {
            return;
        }

        var players = outcomes
            .Select(o => new HostResultPlayer(o.Slot + 1, PlayerAt(o.Slot)?.Id, NameAt(o.Slot), o.Score, o.Pick.Id))
            .ToList();
        _lastResult = new HostResult(++_resultSeq, _runId, _lastSaveOk, DateTimeOffset.UtcNow, _loadedPath,
            _song.Artist, _song.Title, (int)SelectedDifficulty, players);
    }

    private void HideResult()
    {
        _outcomes = null;
        Results.StopAnimations();
        Results.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Vuelve a componer la pantalla con el ID ya elegido y el idioma activo. Sin animación cuando es un cambio
    /// de idioma: el puntaje, el sello y el comentario (mismo ID) quedan en su sitio.
    /// </summary>
    private void RefreshResult(bool animate)
    {
        if (_outcomes is not { Count: > 0 } outcomes)
        {
            return;
        }

        var language = _prefs.CommentLanguage;
        var bank = GetBank(language);
        var standings = ResultStandings.Compute(outcomes.Select(o => o.Score).ToList());
        var players = outcomes.Select((o, index) =>
        {
            var result = new PlayerResult(o.Slot + 1, o.Score, ResultRanks.For(o.Score),
                bank.Text(o.Pick.Id), PlayerColors.For(o.Slot + 1), Name: PlayerAt(o.Slot)?.Name,
                IsWinner: standings[index].IsWinner, IsTie: standings[index].IsTie);
            return PlayerAt(o.Slot) is { } who ? _characters.Resolve(who.CharacterId).ApplyTo(result) : result;
        }).ToList();
        UpdateNextInResults();
        Results.Show(players, language, animate);
    }

    private void OnResultsReplay(object? sender, EventArgs e)
    {
        // Misma secuencia que Detener + Reproducir: el puntaje vuelve a cero.
        HideResult();
        _player.Stop();
        ResetScorers();
        UpdateScore();
        _player.Play();
        UpdateControls();
    }

    private void OnResultsClose(object? sender, EventArgs e) => ShowMenu();

    // ---- Menú principal -----------------------------------------------------------------------------------

    /// <summary>Vuelve al menú: detiene la canción, descarta el resultado y deja el puntaje en cero.</summary>
    private void ShowMenu()
    {
        _abandonSeq++;
        _loadVersion++; // si había una canción cargándose, se descarta
        LoadingLayer.Visibility = Visibility.Collapsed;
        QueueScreen.Visibility = Visibility.Collapsed;
        SessionPrompt.Visibility = Visibility.Collapsed;
        PlayersScreen.Visibility = Visibility.Collapsed;
        _pendingPlay = false;
        HideResult();
        _player.Stop();
        ResetScorers();
        UpdateScore();
        UpdateControls();
        Options.Visibility = Visibility.Collapsed;
        SongList.Visibility = Visibility.Collapsed;
        Menu.Show(_prefs.CommentLanguage);
        Menu.Visibility = Visibility.Visible;
    }

    private void OnMenuClick(object sender, RoutedEventArgs e) => ShowMenu();

    /// <summary>"Cantar": abre la lista de canciones (si todavía no hay carpetas, pide una).</summary>
    /// <summary>
    /// Cada vez que se entra a CANTAR desde el menú se vuelve a ofrecer la partida (continuar la abierta, empezar
    /// una nueva o cantar suelto): con una abierta, CONTINUAR es la primera opción y alcanza con Enter.
    /// </summary>
    private void OnMenuSing(object? sender, EventArgs e)
    {
        _sessionDecided = false;
        EnsureSessionDecision(OpenLibrary);
    }

    // ---- Biblioteca de canciones --------------------------------------------------------------------------

    private List<SongEntry> _library = [];
    private CancellationTokenSource? _scanCts;

    private void OpenLibrary()
    {
        // Siempre se muestra la lista; sin carpetas configuradas aparece vacía, con el botón para elegir una.
        SongList.GridMode = _prefs.LibraryGrid;
        SongList.Show(_prefs.CommentLanguage);
        SongList.SetQueueCount(_queue.Pending().Count);
        SongList.SetSongs(_library);
        SongList.Visibility = Visibility.Visible;
        if (_prefs.LibraryPaths is { Count: > 0 } && _library.Count == 0 && _scanCts is null)
        {
            StartScan();
        }
    }

    /// <summary>Pide una carpeta y la suma a la biblioteca. Devuelve <c>false</c> si se canceló.</summary>
    private bool AddLibraryFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Carpeta con canciones UltraStar" };
        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        var paths = (_prefs.LibraryPaths ?? []).Append(dialog.FolderName).ToList();
        _prefs = _prefs with { LibraryPaths = paths };
        SaveSettings();
        return true;
    }

    /// <summary>Recorre las carpetas en segundo plano; la lista se actualiza al terminar.</summary>
    private void StartScan()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var roots = (_prefs.LibraryPaths ?? []).ToList();
        var indexFile = IndexPath;
        // El escaneo avisa por cada archivo: se actualiza la pantalla cada 25 para no inundar la interfaz.
        var progress = new Progress<int>(n =>
        {
            if (!cts.IsCancellationRequested && n % 25 == 0)
            {
                SongList.SetScanProgress(n);
            }
        });

        // Con índice previo la lista ya está a la vista: el refresco no la tapa con "Buscando…".
        if (_library.Count == 0)
        {
            SongList.SetScanProgress(0);
        }

        _catalog.Scanning = true;
        _catalog.FolderCount = roots.Count;
        Task.Run(() =>
        {
            var previous = SongIndexStore.Load(indexFile);
            var index = SongLibraryScanner.ScanIndexed(roots, previous, progress, cts.Token);
            SongIndexStore.Save(indexFile, index);
            return index;
        }, cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (t.IsCompletedSuccessfully)
            {
                ApplyLibrary(t.Result);
                SongList.SetSongs(_library);
            }

            _catalog.Scanning = false;
            SongList.SetScanProgress(null);
            _scanCts = null;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Pone la biblioteca vigente (la lista local) y, en segundo plano, le asigna a cada archivo su identificador
    /// estable para el control remoto. La lista y el control remoto ven siempre las mismas entradas.
    /// </summary>
    private void ApplyLibrary(List<IndexItem> index)
    {
        _libraryIndex = index;
        _library = SongSearch.SortByArtistAndTitle(index.Select(i => i.Entry));
        _catalog.FolderCount = _prefs.LibraryPaths?.Count ?? 0;
        var store = _store;
        Task.Run(() =>
        {
            var files = index.Select(i => new LibraryFile(i.Path, i.ModifiedTicks, i.Length)).ToList();
            IReadOnlyDictionary<string, string> ids;
            try
            {
                ids = store?.SyncLibraryEntries(files) ?? files.ToDictionary(
                    f => LibraryEntryKeys.KeyOf(f.Path), f => LibraryIds.FromPath(f.Path));
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                ids = files.ToDictionary(f => LibraryEntryKeys.KeyOf(f.Path), f => LibraryIds.FromPath(f.Path));
            }

            var items = LibraryCatalog.Build(index.Select(i => i.Entry), ids, CoverStamp);
            _catalog.Replace(items);
        });
    }

    /// <summary>Marca de la portada (fecha y tamaño), para que el cliente sepa cuándo cambió la imagen.</summary>
    private static string? CoverStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string IndexPath => Path.Combine(Path.GetDirectoryName(_settingsPath)!, "library-index.json");

    /// <summary>Al arrancar: la lista sale del índice guardado y se refresca en segundo plano.</summary>
    private void PrimeLibrary()
    {
        if (_prefs.LibraryPaths is not { Count: > 0 })
        {
            return;
        }

        var indexFile = IndexPath;
        Task.Run(() => SongIndexStore.Load(indexFile))
            .ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && _library.Count == 0)
                {
                    ApplyLibrary(t.Result);
                    if (SongList.Visibility == Visibility.Visible)
                    {
                        SongList.SetSongs(_library);
                    }
                }

                StartScan();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- Vista previa al navegar la lista ----------------------------------------------------------------------
    // Al detenerse un instante sobre una canción de la lista suena un tramo a volumen bajo (SongPreview.Volume).

    private readonly SongPreview _preview = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private SongEntry? _previewPending;

    private void StopPreview()
    {
        _previewTimer.Stop();
        _previewPending = null;
        using (Diag.Time("vista previa: detener"))
        {
            _preview.Stop();
        }
    }

    private void OnSongHighlighted(object? sender, SongEntry? entry)
    {
        StopPreview();
        if (!_prefs.SongPreview || entry is null || !SongList.IsVisible)
        {
            return;
        }

        _previewPending = entry;
        _previewTimer.Start(); // espera a que el cursor se detenga: navegar rápido no dispara una vista previa por fila
    }

    private void OnPreviewTimer(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        var entry = _previewPending;
        if (entry is null)
        {
            return;
        }

        Task.Run(() => SongHeaderReader.ReadPreview(entry.Path)).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result is { } info && ReferenceEquals(_previewPending, entry)
                && SongList.IsVisible)
            {
                _preview.Play(info.AudioPath, info.StartSeconds);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>La canción cargada se eligió en la lista: VOLVER en "¿Quién canta?" regresa a ella.</summary>
    private bool _songFromLibrary;

    private void OnSongChosen(object? sender, SongEntry entry)
    {
        LoadSong(entry.Path);
        _songFromLibrary = true;
    }

    // ---- Cola de canciones ----------------------------------------------------------------------------------

    /// <summary>La partida a la que pertenece la cola; <c>null</c> cantando suelto (cola en memoria).</summary>
    private SessionRecord? QueueSession() => _store is not null && UseSession ? _store.GetOpenSession() : null;

    /// <summary>Después de cualquier cambio de la cola (local o remoto): contadores y botón "siguiente".</summary>
    private void OnQueueChanged()
    {
        SongList.SetQueueCount(_queue.Pending().Count);
        UpdateNextInResults();
    }

    private void OnQueueAdd(object? sender, SongEntry entry)
    {
        var language = _prefs.CommentLanguage;
        var entryId = EntryIdOf(entry.Path);
        try
        {
            _queue.Add(entryId, SongIdentity.For(entry.Artist, entry.Title), entry.Artist, entry.Title, entry.Edition);
        }
        catch (QueueException)
        {
            StatusText.Text = QueueTexts.Full(language);
            StatusBar.Visibility = Visibility.Visible;
            return;
        }

        OnQueueChanged();
        StatusText.Text = QueueTexts.Added(language, entry.Title, _queue.Pending().Count);
        StatusBar.Visibility = Visibility.Visible;
    }

    /// <summary>Identificador de la entrada de una ruta: el de la biblioteca o, si no está, uno nuevo para esa ruta.</summary>
    private string EntryIdOf(string path)
    {
        if (_catalog.Current.FindByPath(path) is { } item)
        {
            return item.EntryId;
        }

        return _store?.EnsureLibraryEntry(path) ?? LibraryIds.FromPath(path);
    }

    private void OnQueueOpen(object? sender, EventArgs e)
    {
        SongList.Visibility = Visibility.Collapsed;
        QueueScreen.Show(_queue.Pending, MoveQueueItem, RemoveQueueItem, _prefs.CommentLanguage, QueueSession() is not null);
        QueueScreen.Visibility = Visibility.Visible;
    }

    /// <summary>Sube (−1) o baja (+1) un pendiente un lugar: en términos de la cola compartida, "delante de…".</summary>
    private void MoveQueueItem(string itemId, int delta)
    {
        var pending = _queue.Pending();
        var index = pending.ToList().FindIndex(i => i.ItemId == itemId);
        if (index < 0)
        {
            return;
        }

        var target = index + delta;
        if (target < 0 || target >= pending.Count)
        {
            return;
        }

        try
        {
            // subir: delante del elemento que ocupaba el lugar; bajar: delante del que sigue al vecino (o al final)
            var before = delta < 0 ? pending[target].ItemId : target + 1 < pending.Count ? pending[target + 1].ItemId : null;
            _queue.Move(itemId, before);
        }
        catch (QueueException)
        {
            // la cola cambió desde otro origen: la pantalla se vuelve a armar con lo vigente
        }

        OnQueueChanged();
    }

    private void RemoveQueueItem(string itemId)
    {
        try
        {
            _queue.Remove(itemId);
        }
        catch (QueueException)
        {
            // ya no estaba
        }

        OnQueueChanged();
    }

    private void OnQueueBack(object? sender, EventArgs e)
    {
        QueueScreen.Visibility = Visibility.Collapsed;
        OpenLibrary();
    }

    /// <summary>"Cantar ya": el elemento pasa a "en ejecución" y la canción se carga como cualquier otra elegida.</summary>
    private void OnQueueSing(object? sender, string itemId)
    {
        if (!StartQueueItem(itemId))
        {
            return;
        }

        QueueScreen.Visibility = Visibility.Collapsed;
    }

    /// <summary>Empieza a cantar un pendiente de la cola. Devuelve <c>false</c> si no se puede (se avisa).</summary>
    private bool StartQueueItem(string itemId)
    {
        var item = _queue.Find(itemId);
        if (item is null || item.State != QueueState.Pending)
        {
            return false;
        }

        var path = _catalog.Current.Find(item.LibraryEntryId)?.Entry.Path;
        if (path is null)
        {
            StatusText.Text = QueueTexts.Unavailable(_prefs.CommentLanguage, item.Title);
            StatusBar.Visibility = Visibility.Visible;
            return false;
        }

        ApplyQueueSlots(item);
        _queue.MarkRunning(itemId);
        OnQueueChanged();
        HideResult();
        LoadSong(path);
        return true;
    }

    /// <summary>
    /// Si el elemento trae cantantes (los eligió un invitado o Avyn), quedan como la selección de "¿Quién canta?".
    /// Solo si siguen siendo válidos (activos, de la partida y con micrófono): si no, no se toca nada.
    /// </summary>
    private void ApplyQueueSlots(QueueEntry item)
    {
        if (item.Slots.Count == 0 || _store is null)
        {
            return;
        }

        var open = UseSession ? _store.GetOpenSession() : null;
        var members = open is null ? null : _store.SessionPlayers(open.Id).Select(p => p.Id).ToHashSet();
        var valid = item.Slots.All(s => _store.GetPlayer(s.PlayerId) is { Archived: false }
                                        && (members is null || members.Contains(s.PlayerId))
                                        && _input.Map.ActiveSlots.Contains(s.Slot - 1));
        if (valid)
        {
            SetSelection([.. item.Slots.Select(s => new SelectionEntry(s.Slot, s.PlayerId))]);
        }
    }

    /// <summary>Botón "SIGUIENTE" de los resultados: toma la primera de la cola y la carga.</summary>
    private void OnResultsNext(object? sender, EventArgs e)
    {
        if (_queue.Pending().FirstOrDefault() is { } first)
        {
            StartQueueItem(first.ItemId);
        }
    }

    private void UpdateNextInResults()
    {
        var next = _queue.Pending().FirstOrDefault();
        Results.SetNext(next?.Title, _prefs.CommentLanguage);
    }

    /// <summary>Se abrió la partida desde la lista de canciones: al cerrarla se vuelve a la lista.</summary>
    private bool _playersFromSongList;

    private void OnSongListSession(object? sender, EventArgs e)
    {
        _playersFromSongList = true;
        ShowPlayers(PlayersView.Section.Session);
    }

    private void OnSongListBack(object? sender, EventArgs e)
    {
        SongList.Visibility = Visibility.Collapsed;
        Menu.Show(_prefs.CommentLanguage); // vuelve al menú con el primer botón enfocado
    }

    private void OnSongListViewMode(object? sender, EventArgs e)
    {
        _prefs = _prefs with { LibraryGrid = SongList.GridMode };
        SaveSettings();
    }

    private void OnSongListFolders(object? sender, EventArgs e)
    {
        if (AddLibraryFolder())
        {
            StartScan();
        }
    }

    private void OnMenuOptions(object? sender, EventArgs e) => ShowOptions();

    private void OnMenuHelp(object? sender, EventArgs e) => ShowOptions(OptionsView.Section.Help);

    private void OnMenuExit(object? sender, EventArgs e) => Close();

    private HumorBank GetBank(string language)
    {
        language = HumorBank.NormalizeLanguage(language);
        if (!_banks.TryGetValue(language, out var bank))
        {
            _banks[language] = bank = HumorBank.Load(language);
        }

        return bank;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _restoringSettings || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string language })
        {
            return;
        }

        _prefs = _prefs with { CommentLanguage = language };
        ApplyStageTexts();
        try
        {
            RefreshResult(animate: false);
        }
        catch (InvalidOperationException)
        {
            // el selector de idioma no debe romper la ventana si falta un recurso
        }

        SaveSettings();
    }

    private void OnInputChanged(object? sender, EventArgs e)
    {
        // Se recuerdan los micrófonos que el usuario dejó abiertos; reabrirlos al arrancar no cuenta como un
        // cambio, así un dispositivo desenchufado no borra lo guardado.
        var byUser = !_restoringInput;
        if (byUser)
        {
            _micsConfig = _input.Map.ActiveSlots
                .Select(slot => new MicrophoneSetting(slot, _input.Map[slot]!.Value.DeviceId, _input.Map[slot]!.Value.Channel))
                .ToList();
        }

        Dispatcher.BeginInvoke(() =>
        {
            UpdateInputStatus();
            ApplyProfileDelay();
            if (SamePlayers())
            {
                // el mismo conjunto de puestos: solo cambió de dónde sale la voz o su retardo
                RefreshStageRoster();
            }
            else
            {
                CreateScorers();
            }

            if (byUser)
            {
                SaveSettings();
            }
        });
    }

    /// <summary>Un motor dejó de funcionar: se pasa a Windows y se avisa; la preferencia del usuario no se toca.</summary>
    private void OnEngineFailed(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _abandonSeq++;
            ResetScorers();
            BuildAudio(AudioEngineKind.Windows);
            _engineNote = $"{message} Se pasó a Windows (compartido); elegí ASIO de nuevo en Opciones.";
            StatusText.Text = _engineNote;
            UpdateInputStatus();
            RestoreInput();
            RefreshOptions();
        });
    }

    private void OnEngineRequested(AudioEngineKind kind)
    {
        _prefs = _prefs with { AudioEngine = kind };
        RebuildAudio();
    }

    private void OnAsioDriverRequested(string driver)
    {
        _prefs = _prefs with { AsioDriver = driver };
        RebuildAudio();
    }

    private void OnOutputPairRequested(int pair)
    {
        _prefs = _prefs with { AsioOutputPair = pair };
        RebuildAudio();
    }

    /// <summary>Cambió el motor o su salida: se reabre todo con la configuración nueva (la reproducción se detiene).</summary>
    private void RebuildAudio()
    {
        BuildAudio(_prefs.AudioEngine);
        SaveSettings();
        RestoreInput();
        RefreshOptions();
    }

    // ---- Retardo de voz: global por motor y perfil por combinación ----------------------------------------

    private bool IsAsio => _engine.Kind == AudioEngineKind.Asio;

    /// <summary>
    /// Clave del perfil de la combinación actual, si hay entrada: motor + dispositivo de salida + entrada + canal.
    /// Con ASIO se usa el controlador, el par de salida y el canal; nunca coincide con una clave de Windows.
    /// </summary>
    private string? CurrentProfileKey() => ProfileKeyOf(PrimarySlot);

    /// <summary>
    /// El puesto cuyo retardo se edita con el selector de la barra: el primer micrófono abierto. Los demás usan su
    /// perfil guardado o el retardo global del motor.
    /// </summary>
    private int PrimarySlot => PlayingSlots() is { Count: > 0 } playing ? playing[0] : _input.Map.ActiveSlots.FirstOrDefault();

    private string? ProfileKeyOf(int slot)
    {
        if (_input.Map[slot] is not { } source)
        {
            return null;
        }

        if (IsAsio)
        {
            return UserSettings.AsioProfileKey(source.DeviceId, _engine.OutputPair, source.Channel);
        }

        var output = string.Empty;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            output = device.ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Sin salida predeterminada: el perfil queda atado solo a la entrada.
        }

        return UserSettings.ProfileKey(output, source.DeviceId, source.Channel);
    }

    /// <summary>
    /// Retardo de voz de un puesto: el del selector para el principal; para los demás, su perfil guardado o el
    /// global del motor.
    /// </summary>
    private TimeSpan DelayOf(int slot)
    {
        if (slot == PrimarySlot)
        {
            return SelectedVoiceDelay;
        }

        if (ProfileKeyOf(slot) is { } key && _profiles is not null && _profiles.TryGetValue(key, out var profile))
        {
            return TimeSpan.FromMilliseconds(profile);
        }

        return TimeSpan.FromMilliseconds(IsAsio ? _asioDelayMs : _windowsDelayMs);
    }

    /// <summary>Pone en el selector el retardo del motor activo: el calibrado de la combinación o el global del motor.</summary>
    private void ApplyEngineDelay()
    {
        var ms = IsAsio ? _asioDelayMs : _windowsDelayMs;
        if (CurrentProfileKey() is { } key && _profiles is not null && _profiles.TryGetValue(key, out var profile))
        {
            ms = profile;
        }

        SetDelaySelector(ms);
    }

    private void SetDelaySelector(int ms)
    {
        var index = Math.Clamp(ms / UserSettings.VoiceDelayStepMs, 0, DelayBox.Items.Count - 1);
        if (DelayBox.SelectedIndex == index)
        {
            return;
        }

        var previous = _restoringSettings;
        _restoringSettings = true;
        try
        {
            DelayBox.SelectedIndex = index;
        }
        finally
        {
            _restoringSettings = previous;
        }

        // El cambio fue de la aplicación, no del usuario: solo hay que rehacer el puntaje con el valor nuevo.
        CreateScorers();
    }

    private void OnDelayChanged(object sender, SelectionChangedEventArgs e)
    {
        // Como la dificultad: cambia cómo se puntúa, así que se vuelve a empezar; solo con la canción detenida.
        if (!IsInitialized || _restoringSettings)
        {
            return;
        }

        var ms = Math.Max(DelayBox.SelectedIndex, 0) * UserSettings.VoiceDelayStepMs;
        if (IsAsio)
        {
            _asioDelayMs = ms;
        }
        else
        {
            _windowsDelayMs = ms;
        }

        if (CurrentProfileKey() is { } key)
        {
            _profiles ??= [];
            _profiles[key] = ms;
        }

        CreateScorers();
        SaveSettings();
    }

    /// <summary>Si esta combinación ya tiene un retardo calibrado o ajustado, lo pone en el selector.</summary>
    private void ApplyProfileDelay()
    {
        if (CurrentProfileKey() is { } key && _profiles is not null && _profiles.TryGetValue(key, out var ms))
        {
            SetDelaySelector(ms);
        }
        else if (IsAsio && _input.Map.Count > 0)
        {
            // Sin calibración para esta combinación con ASIO: no se reutiliza lo de Windows.
            SetDelaySelector(_asioDelayMs);
        }
    }

    // ---- Calibración ---------------------------------------------------------------------------------------

    /// <summary>La barra técnica calibra con el micrófono principal (el que gobierna el selector de retardo).</summary>
    private void OnCalibrateClick(object sender, RoutedEventArgs e) => CalibrateSlot(PrimarySlot);

    private void CalibrateSlot(int slot)
    {
        if (_input.DeviceOf(slot) is not { } calibrationDevice || _input.Map[slot] is not { } calibrationSource)
        {
            StatusText.Text = "Elegí primero una entrada (Opciones o Ctrl+I) para calibrar con ella.";
            return;
        }

        if (_player.IsPlaying || _player.IsPaused)
        {
            StatusText.Text = "Detené la canción antes de calibrar.";
            return;
        }

        var window = new CalibrationWindow(calibrationDevice, calibrationSource.Channel, _engine.Name, _engine.Calibrate, ms =>
        {
            ApplySlotDelay(slot, ms);
            StatusText.Text = $"Retardo de voz del jugador {slot + 1} ajustado a {ms} ms por calibración ({_engine.Name}).";
        }) { Owner = this };
        window.ShowDialog();
        RefreshOptions();
    }

    /// <summary>
    /// Fija el retardo del micrófono de un puesto. El principal pasa por el selector (que también actualiza el
    /// retardo global del motor); los demás solo guardan su perfil por dispositivo y canal.
    /// </summary>
    private void ApplySlotDelay(int slot, int ms)
    {
        if (slot == PrimarySlot)
        {
            SetDelaySelector(ms);
            if (IsAsio)
            {
                _asioDelayMs = ms;
            }
            else
            {
                _windowsDelayMs = ms;
            }
        }

        if (ProfileKeyOf(slot) is { } key)
        {
            _profiles ??= [];
            _profiles[key] = ms;
        }

        if (slot != PrimarySlot)
        {
            CreateScorers();
        }

        SaveSettings();
    }

    // ---- Opciones -------------------------------------------------------------------------------------------

    // ---- Jugadores, sesión e historial -----------------------------------------------------------------------

    private void OpenStore()
    {
        try
        {
            CharacterCatalog.Default.SetUserCharacters(_userCharacters.Load());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // sin personajes propios: se usan los de fábrica
        }

        try
        {
            _store = new KantorStore(KantorStore.DefaultPath());
            RefreshPlayerCache();
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            _store = null;
            StatusText.Text = $"No se pudo abrir el historial: {e.Message}. Se canta sin guardar resultados.";
            StatusBar.Visibility = Visibility.Visible;
        }
    }

    private void RefreshPlayerCache()
    {
        if (_store is null)
        {
            _playerCache = [];
            return;
        }

        try
        {
            _playerCache = _store.ListPlayers(includeArchived: true).ToDictionary(p => p.Id);
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            StatusText.Text = $"No se pudo leer el historial: {e.Message}";
        }
    }

    /// <summary>Al arrancar con una sesión abierta: continuarla o iniciar una nueva. No cierra nada por sí solo.</summary>
    private void OfferOpenSession()
    {
        if (_store is null)
        {
            return;
        }

        SessionRecord? open;
        try
        {
            open = _store.GetOpenSession();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return;
        }

        _session = open;
        UpdateSessionLabel();
    }

    // La sesión se decide al entrar a CANTAR (una vez por ejecución de la aplicación, o al reanudar desde
    // Jugadores): continuar la abierta, iniciar una nueva o cantar sin sesión.
    private bool _sessionDecided;
    private bool _freePlay;
    private bool _singersConfirmed;
    private Action? _afterGate;

    /// <summary>¿Se canta con la sesión abierta? Falso sin sesión elegida, sin sesión abierta o sin base.</summary>
    private bool UseSession
    {
        get
        {
            try
            {
                return _store is not null && !_freePlay && _store.GetOpenSession() is not null;
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Muestra la puerta de "Cantar" si todavía no se decidió cómo cantar; <paramref name="then"/> corre con la
    /// decisión tomada (de inmediato si ya estaba).
    /// </summary>
    private void EnsureSessionDecision(Action then)
    {
        if (_store is null || _sessionDecided)
        {
            then();
            return;
        }

        _afterGate = then;
        SessionRecord? open = null;
        try
        {
            open = _store.GetOpenSession();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // sin lectura: se ofrece iniciar o cantar sin sesión
        }

        var players = open is null ? [] : _store.SessionPlayers(open.Id).Select(p => p.Name).ToList();
        var songs = open is null ? 0 : _store.GetSessionHistory(open.Id).Count;
        SessionPrompt.Show(_prefs.CommentLanguage, open is not null, open?.Name,
            open?.StartedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty,
            songs, players);
        SessionPrompt.Visibility = Visibility.Visible;
    }

    private void FinishSessionDecision()
    {
        SessionPrompt.Visibility = Visibility.Collapsed;
        _sessionDecided = true;
        _singersConfirmed = false;
        RefreshPlayerCache();
        CreateScorers();
        UpdateSessionLabel();
        var then = _afterGate;
        _afterGate = null;
        then?.Invoke();
    }

    private void OnSessionContinue(object? sender, EventArgs e)
    {
        _freePlay = false;
        if (_store is not null && _store.GetOpenSession() is { } session)
        {
            try
            {
                _session = session;
                _selection = [.. _store.GetSelection(session.Id)];
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                StatusText.Text = $"No se pudo recuperar la selección: {ex.Message}";
            }
        }

        FinishSessionDecision();
    }

    private void OnSessionNew(object? sender, EventArgs e)
    {
        _freePlay = false;
        if (_store is not null)
        {
            try
            {
                _session = _store.StartSession(); // cierra la anterior en la misma transacción
                _selection = [];
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                StatusText.Text = $"No se pudo iniciar la partida: {ex.Message}";
            }
        }

        // Una partida nueva arranca sin jugadores: antes de seguir se pregunta quién juega.
        var next = _afterGate;
        if (_session is not null)
        {
            _afterGate = () => ShowMembers(next);
        }

        FinishSessionDecision();
    }

    /// <summary>Cantar sin sesión: la abierta (si hay) no se toca, el resultado no se guarda.</summary>
    private void OnSessionFree(object? sender, EventArgs e)
    {
        _freePlay = true;
        _selection = [];
        FinishSessionDecision();
    }

    private void OnSessionCancel(object? sender, EventArgs e)
    {
        SessionPrompt.Visibility = Visibility.Collapsed;
        _afterGate = null;
        _pendingPlay = false;
        Menu.Show(_prefs.CommentLanguage);
    }

    /// <summary>Línea de la sesión activa en el menú.</summary>
    private void UpdateSessionLabel()
    {
        if (_store is null)
        {
            Menu.SetSession(string.Empty);
            return;
        }

        try
        {
            var lang = _prefs.CommentLanguage;
            if (_sessionDecided && _freePlay)
            {
                Menu.SetSession(PlayersTexts.Get(lang, "s.label.free"));
            }
            else if (_store.GetOpenSession() is { } open)
            {
                var names = string.Join(", ", _store.SessionPlayers(open.Id).Select(p => p.Name));
                var count = _store.GetSessionHistory(open.Id).Count;
                Menu.SetSession(PlayersTexts.Format(lang, "s.label", PlayersTexts.Songs(lang, count), names)
                    .TrimEnd(' ', '·'));
            }
            else
            {
                Menu.SetSession(PlayersTexts.Get(lang, "s.label.none"));
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            Menu.SetSession(string.Empty);
        }
    }

    /// <summary>
    /// Cambia quién ocupa cada puesto P1–P4. Con sesión, lo deja guardado en ella; sin sesión vive solo en memoria.
    /// </summary>
    private void SetSelection(IReadOnlyList<SelectionEntry> selection)
    {
        _selection = [.. selection];
        try
        {
            if (UseSession && _store?.GetOpenSession() is { } open)
            {
                _store.SetSelection(open.Id, selection);
            }
        }
        catch (Exception e) when (e is StoreRuleException or Microsoft.Data.Sqlite.SqliteException)
        {
            StatusText.Text = e.Message;
        }

        CreateScorers();
    }

    private PlayersModel BuildPlayersModel() => new()
    {
        Store = _store!,
        Language = _prefs.CommentLanguage,
        Catalog = CharacterCatalog.Default,
        UserCharacters = _userCharacters,
        GetSelection = () => _selection,
        SetSelection = SetSelection,
        EnabledSlots = _input.Map.ActiveSlots,
        MicLabel = MicLabelOf,
        VoiceNames = _song is { Voices.Count: > 1 } duet ? duet.Voices.Select(v => v.Name).ToList() : [],
        GetVoice = VoiceOf,
        SetVoice = (slot, voice) =>
        {
            _voiceChoice[slot] = voice;
            CreateScorers();
        },
        PortraitOf = characterId => _characters.Resolve(characterId).Portrait,
        UseSession = UseSession,
        SetUseSession = useSession =>
        {
            _freePlay = !useSession;
            _sessionDecided = true;
            _selection = []; // cambia de dónde salen los jugadores: se vuelve a elegir
            _singersConfirmed = false;
            CreateScorers();
            UpdateSessionLabel();
        },
    };

    private string MicLabelOf(int slot) =>
        _input.DeviceOf(slot) is { } device && _input.Map[slot] is { } source
            ? $"{device.Name} · {device.ChannelLabel(source.Channel)}"
            : string.Empty;

    private void ShowPlayers(PlayersView.Section section, string? notice = null, bool pick = false,
        bool members = false)
    {
        if (_store is null)
        {
            StatusText.Text = "El historial no está disponible: no se pudo abrir la base de datos.";
            StatusBar.Visibility = Visibility.Visible;
            return;
        }

        if (PlayersScreen.Visibility != Visibility.Visible)
        {
            if (!pick && !members)
            {
                _abandonSeq++; // entrar a Jugadores por su cuenta abandona la canción; elegir quién canta, no
            }

            _playersFromMenu = Menu.Visibility == Visibility.Visible;
            HideResult();
            _player.Stop();
            ResetScorers();
            UpdateScore();
            UpdateControls();
            Options.Visibility = Visibility.Collapsed;
            SongList.Visibility = Visibility.Collapsed;
        }

        RefreshPlayerCache();
        PlayersScreen.Show(BuildPlayersModel(), section, notice, pick, members);
        PlayersScreen.Visibility = Visibility.Visible;
    }

    private bool _inMembers;
    private Action? _membersThen;

    /// <summary>
    /// Después de iniciar una partida nueva (que arranca sin jugadores) se pregunta quién juega; <paramref name="then"/>
    /// sigue con lo que el usuario estaba haciendo (abrir la lista de canciones o empezar a cantar).
    /// </summary>
    private void ShowMembers(Action? then)
    {
        _inMembers = true;
        _membersThen = then;
        ShowPlayers(PlayersView.Section.Session, members: true);
    }

    /// <param name="ready">Se confirmó "¿quién canta?": arranca la interpretación que estaba pendiente.</param>
    private void ClosePlayers(bool ready = false)
    {
        PlayersScreen.Visibility = Visibility.Collapsed;
        RefreshPlayerCache();
        UpdateSessionLabel();
        if (_playersFromSongList)
        {
            _playersFromSongList = false;
            _inMembers = false;
            _membersThen = null;
            OpenLibrary();
            return;
        }

        if (_inMembers)
        {
            _inMembers = false;
            var then = _membersThen;
            _membersThen = null;
            if (ready)
            {
                then?.Invoke();
            }
            else if (_playersFromMenu)
            {
                Menu.Show(_prefs.CommentLanguage);
            }

            return;
        }

        if (_pendingPlay)
        {
            _pendingPlay = false;
            if (ready && PlayingSlots().Count > 0)
            {
                _singersConfirmed = true;
                TogglePlayPause(); // ya hay quién cante: arranca la interpretación que se había pedido
            }
            else if (!ready && _songFromLibrary)
            {
                _songFromLibrary = false;
                _player.Stop();
                OpenLibrary(); // la canción se había elegido en la lista: VOLVER regresa a ella
            }

            return;
        }

        if (_playersFromMenu)
        {
            Menu.Show(_prefs.CommentLanguage);
        }
    }

    private void OnMenuPlayers(object? sender, EventArgs e) => ShowPlayers(PlayersView.Section.Players);

    private void OnPlayersToolbarClick(object sender, RoutedEventArgs e) => ShowPlayers(PlayersView.Section.Session);

    /// <summary>
    /// Antes de empezar de cero hace falta saber quién canta: no hay participantes anónimos. Sin elegir, se
    /// abre la selección y la canción arranca al terminar.
    /// </summary>
    private bool EnsureParticipants()
    {
        if (_store is null)
        {
            return true;
        }

        // Cantar sin haber decidido cómo (canción abierta por Ctrl+O o por línea de comandos): primero la puerta.
        if (!_sessionDecided)
        {
            _pendingPlay = false;
            EnsureSessionDecision(TogglePlayPause);
            return false;
        }

        // En cada canción se elige quién canta en los micrófonos habilitados (la elección anterior viene cargada).
        if (_singersConfirmed && PlayingSlots().Count > 0)
        {
            return true;
        }

        _pendingPlay = true;
        var lang = _prefs.CommentLanguage;
        ShowPlayers(PlayersView.Section.Session, _input.Map.Count == 0 ? PlayersTexts.Get(lang, "s.nomics") : null,
            pick: true);
        return false;
    }

    /// <summary>
    /// Guarda la interpretación terminada en el historial, una sola vez por ejecución. Una canción interrumpida
    /// no llega acá con la posición final, así que no deja nada. Un fallo técnico se reintenta una vez.
    /// </summary>
    private void SavePerformance(IReadOnlyList<PlayerOutcome>? outcomes)
    {
        if (_store is null || _song is null || outcomes is null || _runSaved)
        {
            return;
        }

        // Terminó de verdad solo si la última posición vista llegó al final (a ≤ 1 s de la duración).
        if (_player.Duration <= TimeSpan.Zero || _lastPosition < _player.Duration - TimeSpan.FromSeconds(1))
        {
            return;
        }

        // Sin sesión no hay historial: la interpretación cuelga de una sesión. Se avisa y no se guarda.
        if (!UseSession)
        {
            _runSaved = true;
            StatusText.Text = PlayersTexts.Get(_prefs.CommentLanguage, "save.free");
            return;
        }

        var results = new List<ResultInput>();
        foreach (var outcome in outcomes)
        {
            if (PlayerAt(outcome.Slot) is { } who)
            {
                results.Add(new ResultInput(who.Id, outcome.Slot + 1, outcome.Score, outcome.Pick.Id));
            }
        }

        if (results.Count == 0)
        {
            return;
        }

        string? entryId = null;
        string? edition = null;
        if (_loadedPath is not null)
        {
            var entry = _catalog.Current.FindByPath(_loadedPath);
            entryId = entry?.EntryId ?? EntryIdOf(_loadedPath);
            edition = entry?.Entry.Edition;
        }

        var run = new NewPerformance(_runId, SongIdentity.For(_song.Artist, _song.Title), _song.Title, _song.Artist,
            (int)SelectedDifficulty, ScoringRules.ScoringVersion, results, entryId, edition);
        var language = _prefs.CommentLanguage;
        _runSaved = true; // pase lo que pase, este fin no se vuelve a intentar por eventos repetidos
        Exception? failure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _store.SavePerformance(run, _selection);
                _session = _store.GetOpenSession();
                RefreshPlayerCache();
                UpdateSessionLabel();
                StatusText.Text = PlayersTexts.Get(language, "save.ok");
                _lastSaveOk = true;
                _saveFailed = false;
                return;
            }
            catch (StoreRuleException e)
            {
                failure = e; // una regla incumplida no mejora reintentando
                break;
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException
                                          or UnauthorizedAccessException)
            {
                failure = e;
            }
        }

        _saveFailed = true;
        StatusText.Text = PlayersTexts.Format(language, "save.failed", failure?.Message ?? "?");
        StatusBar.Visibility = Visibility.Visible; // que el fallo no pase desapercibido
    }

    private void WireOptions()
    {
        _previewTimer.Tick += OnPreviewTimer;
        SongList.HighlightChanged += OnSongHighlighted;
        SongList.IsVisibleChanged += (_, _) =>
        {
            if (!SongList.IsVisible)
            {
                StopPreview();
            }
        };
        Options.SongPreviewChanged += on =>
        {
            _prefs = _prefs with { SongPreview = on };
            if (!on)
            {
                StopPreview();
            }

            SaveSettings();
        };
        _queue.Changed += () => Dispatcher.BeginInvoke(OnQueueChanged);
        PlayersScreen.BackRequested += (_, _) => ClosePlayers();
        PlayersScreen.ReadyRequested += (_, _) => ClosePlayers(ready: true);
        PlayersScreen.OptionsRequested += (_, _) =>
        {
            _pendingPlay = false;
            ClosePlayers();
            ShowOptions(OptionsView.Section.Audio); // crear los micrófonos; al volver, Reproducir pregunta de nuevo
        };
        PlayersScreen.DataChanged += () =>
        {
            RefreshPlayerCache();
            UpdateSessionLabel();
            RefreshStageRoster();
            UpdateScore();
        };

        Options.EngineRequested += OnEngineRequested;
        Options.AsioDriverRequested += OnAsioDriverRequested;
        Options.OutputPairRequested += OnOutputPairRequested;
        Options.DelayChanged += (slot, ms) =>
        {
            ApplySlotDelay(slot, ms);
            RefreshOptions();
        };
        Options.CalibrateRequested += CalibrateSlot;
        Options.AddFolderRequested += () =>
        {
            if (AddLibraryFolder())
            {
                StartScan();
                RefreshOptions();
            }
        };
        Options.RemoveFolderRequested += path =>
        {
            var left = (_prefs.LibraryPaths ?? []).Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
            _prefs = _prefs with { LibraryPaths = left.Count > 0 ? left : null };
            ApplyLibrary([]);
            SaveSettings();
            if (left.Count > 0)
            {
                StartScan();
            }

            RefreshOptions();
        };
        Options.GridChanged += grid =>
        {
            _prefs = _prefs with { LibraryGrid = grid };
            SaveSettings();
        };
        Options.DifficultyChanged += index => DifficultyBox.SelectedIndex = index;
        Options.VoiceDisplayChanged += index => DisplayBox.SelectedIndex = index;
        Options.LanguageChanged += language =>
        {
            LanguageBox.SelectedIndex = language == "en" ? 1 : 0;
            RefreshOptions();
            RefreshGuest();
        };
        Options.NonsenseChanged += probability =>
        {
            _prefs = _prefs with { NonsenseProbability = probability };
            _selector = null; // el sorteo toma la probabilidad al crearse
            SaveSettings();
        };
        Options.PhraseFeedbackChanged += on =>
        {
            _prefs = _prefs with { PhraseFeedback = on };
            Stage.FeedbackEnabled = on;
            if (!on)
            {
                Stage.ClearFeedback();
            }

            SaveSettings();
        };
        Options.StartFullscreenChanged += on =>
        {
            _prefs = _prefs with { StartFullscreen = on };
            SaveSettings();
        };
        Options.AutoAdvanceChanged += SetAutoAdvanceSetting;
        Options.RemoteControlChanged += SetRemoteControlSetting;
        Options.MusicVolumeChanged += SetMusicVolume;
        Options.TechnicalBarChanged += on =>
            StatusBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Options.BackRequested += (_, _) => CloseOptions();
    }

    private OptionsModel BuildOptionsModel() => new()
    {
        Language = _prefs.CommentLanguage,
        Engine = _engine,
        Requested = _prefs.AudioEngine,
        Asio = _asioAvailability ??= AsioAvailability.Detect(),
        PreferredDriver = _prefs.AsioDriver,
        EngineNote = _engineNote,
        Input = _input,
        DelayMs = slot => (int)DelayOf(slot).TotalMilliseconds,
        Folders = _prefs.LibraryPaths ?? [],
        Grid = _prefs.LibraryGrid,
        Difficulty = DifficultyBox.SelectedIndex,
        VoiceDisplay = DisplayBox.SelectedIndex == 1 ? 1 : 0,
        Nonsense = _prefs.NonsenseProbability,
        SongPreview = _prefs.SongPreview,
        PhraseFeedback = _prefs.PhraseFeedback,
        StartFullscreen = _prefs.StartFullscreen,
        TechnicalBar = StatusBar.Visibility == Visibility.Visible,
        AutoAdvance = _prefs.AutoAdvance,
        RemoteControl = _prefs.RemoteControl,
        MusicVolume = _prefs.MusicVolumePercent,
        GuestNetworks = [.. GuestNetwork.Candidates().Select(c => (c.Address.ToString(), $"{c.Address} ({c.Name})"))],
        GuestInterface = _prefs.GuestInterface,
        RemoteStatus = RemoteStatusText(),
    };

    /// <summary>
    /// Abre las opciones. Detiene la canción (como el botón Menú): cambiar micrófonos, dificultad o retardo
    /// reinicia el puntaje, así que no tiene sentido hacerlo con una canción sonando.
    /// </summary>
    private void ShowOptions(OptionsView.Section? section = null)
    {
        if (Options.Visibility != Visibility.Visible)
        {
            _abandonSeq++;
            _optionsFromMenu = Menu.Visibility == Visibility.Visible;
            HideResult();
            _player.Stop();
            ResetScorers();
            UpdateScore();
            UpdateControls();
            SongList.Visibility = Visibility.Collapsed;
            _asioAvailability = AsioAvailability.Detect();
        }

        Options.Show(BuildOptionsModel(), section);
        Options.Visibility = Visibility.Visible;
    }

    private void RefreshOptions()
    {
        if (Options.Visibility == Visibility.Visible)
        {
            Options.Show(BuildOptionsModel());
        }
    }

    private void CloseOptions()
    {
        Options.Visibility = Visibility.Collapsed;
        if (_optionsFromMenu)
        {
            Menu.Show(_prefs.CommentLanguage); // vuelve al menú con el primer botón enfocado y el idioma vigente
        }
    }

    // ---- Ajustes ------------------------------------------------------------------------------------------

    private void SaveSettings()
    {
        if (_restoringSettings || !IsInitialized)
        {
            return;
        }

        _prefs = _prefs with
        {
            Difficulty = SelectedDifficulty,
            VoiceDisplay = DisplayBox.SelectedIndex == 1 ? 1 : 0,
            VoiceDelayMs = _windowsDelayMs,
            AsioVoiceDelayMs = _asioDelayMs,
            Microphones = _micsConfig,
            InputDeviceId = _micsConfig is null ? _prefs.InputDeviceId : null,
            InputChannel = _micsConfig is null ? _prefs.InputChannel : 0,
            DelayProfiles = _profiles,
        };
        UserSettingsStore.Save(_settingsPath, _prefs);
    }

    /// <summary>
    /// Vuelve a abrir los micrófonos guardados cuyo dispositivo sigue disponible; los demás se omiten sin avisar
    /// y lo guardado no se toca.
    /// </summary>
    private void RestoreInput()
    {
        var wanted = _prefs.EffectiveMicrophones();
        if (wanted.Count == 0 || _input.Map.Count > 0)
        {
            return;
        }

        _restoringInput = true;
        try
        {
            var devices = _engine.ListInputs();
            foreach (var mic in wanted)
            {
                if (devices.FirstOrDefault(d => d.Id == mic.DeviceId) is not { } device)
                {
                    continue;
                }

                try
                {
                    _input.Assign(mic.Slot, device, Math.Min(mic.Channel, device.Channels - 1));
                }
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException
                                              or UnauthorizedAccessException)
                {
                    StatusText.Text = $"No se pudo reabrir el micrófono {mic.Slot + 1}: {e.Message}";
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            StatusText.Text = $"No se pudo leer la lista de dispositivos: {e.Message}";
        }
        finally
        {
            _restoringInput = false;
        }
    }

    /// <summary>Retardo de voz elegido: 25 ms por posición de la lista (0 a 300 ms).</summary>
    private TimeSpan SelectedVoiceDelay =>
        TimeSpan.FromMilliseconds(UserSettings.VoiceDelayStepMs * Math.Max(DelayBox.SelectedIndex, 0));

    private void OnDifficultyChanged(object sender, SelectionChangedEventArgs e)
    {
        // Cambiar la dificultad puntúa de nuevo desde cero; el selector solo está activo con la canción detenida.
        if (IsInitialized && !_restoringSettings)
        {
            CreateScorers();
            SaveSettings();
        }
    }

    private void OnDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized)
        {
            Stage.VoiceMode = DisplayBox.SelectedIndex == 1 ? VoiceDisplay.Raw : VoiceDisplay.Beats;
            SaveSettings();
        }
    }

    private Difficulty SelectedDifficulty => DifficultyBox.SelectedIndex switch
    {
        1 => Difficulty.Medium,
        2 => Difficulty.Hard,
        _ => Difficulty.Easy,
    };

    // ---- Puntaje -----------------------------------------------------------------------------------------

    /// <summary>
    /// Puestos que cantan: los que tienen un jugador asignado Y un micrófono. Un puesto sin jugador no figura
    /// durante el canto. Sin base de datos (no hay perfiles que elegir) cantan los micrófonos abiertos.
    /// </summary>
    private IReadOnlyList<int> PlayingSlots()
    {
        if (_store is null)
        {
            return _input.Map.ActiveSlots;
        }

        return _selection.Select(s => s.Slot - 1).Where(slot => _input.Map[slot] is not null).OrderBy(s => s).ToList();
    }

    /// <summary>El jugador que ocupa el puesto (0–3) en la selección actual, o <c>null</c>.</summary>
    private PlayerRecord? PlayerAt(int slot)
    {
        var entry = _selection.FirstOrDefault(s => s.Slot == slot + 1);
        return entry.PlayerId != 0 && _playerCache.TryGetValue(entry.PlayerId, out var player) ? player : null;
    }

    private string NameAt(int slot) => PlayerAt(slot)?.Name ?? ResultTexts.Player(_prefs.CommentLanguage, slot + 1);

    /// <summary>Un puntaje por jugador, cada uno con su voz y su retardo. Vuelve todo a cero.</summary>
    private void CreateScorers()
    {
        if (Stage is null)
        {
            return;
        }

        var players = new List<PlayerState>();
        foreach (var slot in PlayingSlots())
        {
            var delay = DelayOf(slot);
            var voiceIndex = VoiceOf(slot);
            var scorer = _song is { Voices.Count: > 0 } song
                ? new VoiceScorer(song.Voices[voiceIndex], new SongTiming(song.Bpm, song.GapMilliseconds),
                    new ScoringRules { Difficulty = SelectedDifficulty, AcousticLatency = delay })
                : null;
            players.Add(new PlayerState(slot, scorer, delay, voiceIndex));
        }

        _players = players;
        Stage.NoteTolerance = SelectedDifficulty.ToleranceSemitones();
        NewRun();
        RefreshStageRoster();
        HideResult();
        UpdateScore();
    }

    /// <summary>Empieza una ejecución nueva: identidad propia para el historial y sin posición vista.</summary>
    private void NewRun()
    {
        _runId = Guid.NewGuid().ToString("N");
        _runSaved = false;
        _lastPosition = TimeSpan.Zero;
    }

    /// <summary>Voz elegida por canción para cada puesto; sin elegir, el reparto automático.</summary>
    private readonly Dictionary<int, int> _voiceChoice = [];

    private int VoiceOf(int slot)
    {
        if (_song is not { } song)
        {
            return 0;
        }

        return _voiceChoice.TryGetValue(slot, out var chosen) && chosen >= 0 && chosen < song.Voices.Count
            ? chosen
            : VoiceAssignment.VoiceFor(slot, song.Voices.Count);
    }

    /// <summary>¿Los jugadores en juego ya son los que corresponden a los micrófonos y retardos actuales?</summary>
    private bool SamePlayers()
    {
        var slots = PlayingSlots();
        return slots.Count == _players.Count
               && slots.Select((slot, i) => _players[i].Slot == slot && _players[i].Delay == DelayOf(slot)
                   && _players[i].VoiceIndex == VoiceOf(slot)).All(same => same);
    }

    /// <summary>Pasa al escenario los jugadores con sus nombres, colores y micrófonos actuales.</summary>
    private void RefreshStageRoster()
    {
        Stage.AccuracyLabel = ResultTexts.Accuracy(_prefs.CommentLanguage);
        _feedback.SetPlayers(_players.Select(p => (p.Slot + 1, p.Scorer)));
        Stage.FeedbackEnabled = _prefs.PhraseFeedback;
        Stage.Players = _players.Select(p => new StagePlayer
        {
            Number = p.Slot + 1,
            Name = NameAt(p.Slot),
            Color = PlayerColors.For(p.Slot + 1),
            Portrait = PlayerAt(p.Slot) is { } who ? _characters.Resolve(who.CharacterId).Portrait : null,
            Scorer = p.Scorer,
            Timeline = _input.TimelineOf(p.Slot),
            VoiceIndex = p.VoiceIndex,
            Delay = p.Delay,
        }).ToList();
    }

    private void ResetScorers()
    {
        foreach (var player in _players)
        {
            player.Scorer?.Reset();
        }

        NewRun();
    }

    /// <summary>
    /// Solo avanza con la canción sonando: en pausa la posición no se mueve y el puntaje se conserva; al terminar
    /// o detenerse no se toca (el final queda visible hasta volver a empezar).
    /// </summary>
    private void AdvanceScore()
    {
        if (_players.Count == 0 || !_player.IsPlaying)
        {
            return;
        }

        var position = _player.Position;
        if (position <= TimeSpan.Zero)
        {
            return; // lectura de reloj no disponible: no se interpreta como un retroceso
        }

        _lastPosition = position; // al terminar la posición vuelve a 0: esta es la que dice si llegó al final
        foreach (var player in _players)
        {
            player.Scorer?.Advance(position, _input.TimelineOf(player.Slot));
        }

        if (_prefs.PhraseFeedback)
        {
            _feedback.SetLanguage(_prefs.CommentLanguage); // idioma de la interfaz, no el de la canción
            Stage.ShowFeedback(_feedback.Poll(position));
        }

        UpdateScore();
    }

    private void UpdateScore()
    {
        var snapshots = _players.Select(p => (p.Slot, Snapshot: p.Scorer?.Snapshot)).ToList();
        if (snapshots.Count <= 1)
        {
            var s = snapshots.Count == 1 ? snapshots[0].Snapshot : null;
            ScoreText.Text = (s?.Total ?? 0).ToString("N0");
            ScoreText.ToolTip = s is { } snap ? DescribeScore(snap) : null;
            return;
        }

        ScoreText.Text = string.Join("  ·  ",
            snapshots.Select(x => $"{NameAt(x.Slot)} {(x.Snapshot?.Total ?? 0):N0}"));
        ScoreText.ToolTip = string.Join("\n\n", snapshots.Where(x => x.Snapshot is not null)
            .Select(x => $"{NameAt(x.Slot)}\n{DescribeScore(x.Snapshot!.Value)}"));
    }

    private static string DescribeScore(ScoreSnapshot snap) =>
        $"Notas {snap.NotesRounded:N0} · Doradas {snap.GoldenRounded:N0} · Líneas {snap.LinesRounded:N0}\n" +
        $"Beats acertados {snap.BeatsHit:N0} de {snap.BeatsEvaluated:N0} evaluados ({snap.BeatsTotal:N0} en total)";

    // ---- Interfaz ----------------------------------------------------------------------------------------

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenSongDialog();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();

    // ---- Controles del escenario para el mouse ---------------------------------------------------------------
    // Sin barra de herramientas, el mouse necesita una salida del escenario: dos botones (pausa/seguir y menú)
    // que aparecen al mover el mouse y se quedan mientras la canción no suena.

    private DateTime _lastMouseMove = DateTime.MinValue;

    private bool StageIsShowing =>
        _song is not null
        && Menu.Visibility != Visibility.Visible && SongList.Visibility != Visibility.Visible
        && Results.Visibility != Visibility.Visible && Options.Visibility != Visibility.Visible
        && PlayersScreen.Visibility != Visibility.Visible && SessionPrompt.Visibility != Visibility.Visible;

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        _lastMouseMove = DateTime.UtcNow;
        UpdateStageControls();
    }

    private void UpdateStageControls()
    {
        if (StageControls is null)
        {
            return;
        }

        var recent = DateTime.UtcNow - _lastMouseMove < TimeSpan.FromSeconds(3);
        var lyricsDone = LyricsFinished();
        var visible = StageIsShowing && (recent || !_player.IsPlaying || lyricsDone);
        StageControls.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            var language = _prefs.CommentLanguage;
            StagePauseButton.Content = _player.IsPlaying ? MenuTexts.Pause(language)
                : _player.IsPaused ? MenuTexts.Resume(language) : MenuTexts.Sing(language);
            StageMenuButton.Content = MenuTexts.BackToMenu(language);
            StageFinishButton.Content = MenuTexts.Finish(language);
            StageFinishButton.Visibility = lyricsDone ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>La canción está sonando y ya no queda letra por cantar (la música sigue).</summary>
    private bool LyricsFinished()
    {
        if (_song is null || !_player.IsPlaying)
        {
            return false;
        }

        var lastBeat = _song.Voices.Where(v => v.HasNotes).Select(v => v.Lines[^1].EndBeat).DefaultIfEmpty(0).Max();
        return _player.Position > new SongTiming(_song.Bpm, _song.GapMilliseconds).TimeOf(lastBeat);
    }

    private void OnStageFinishClick(object sender, RoutedEventArgs e)
    {
        // Terminar antes de que acabe la música cuenta como canción cantada: se guarda y va a resultados.
        var duration = _player.Duration;
        _player.Stop();
        _lastPosition = duration;
        UpdateControls();
        ShowResult();
        UpdateStageControls();
    }

    private void OnStagePauseClick(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
        UpdateStageControls();
    }

    private void OnStageMenuClick(object sender, RoutedEventArgs e) => ShowMenu();

    private void OnInputsClick(object sender, RoutedEventArgs e) => ShowOptions(OptionsView.Section.Audio);

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        // Detener devuelve la canción al principio: el puntaje empieza de nuevo. Pausar, en cambio, lo conserva.
        _abandonSeq++;
        _player.Stop();
        ResetScorers();
        HideResult();
        UpdateScore();
        UpdateControls();
    }

    /// <summary>
    /// Cualquier clic en la ventana: con los resultados en pantalla y la animación terminada cuenta como una
    /// interacción local explícita (cancela la cuenta del avance automático). El movimiento del mouse no.
    /// </summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e) => NoteResultsInteraction();

    private void NoteResultsInteraction()
    {
        if (Results.Visibility == Visibility.Visible && !Results.IsAnimating)
        {
            _resultsInteraction++;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        NoteResultsInteraction();
        if ((Results.Visibility == Visibility.Visible || Menu.Visibility == Visibility.Visible
                || SongList.Visibility == Visibility.Visible || Options.Visibility == Visibility.Visible
                || PlayersScreen.Visibility == Visibility.Visible || SessionPrompt.Visibility == Visibility.Visible
                || GuestScreen.Visibility == Visibility.Visible)
            && e.Key is Key.Space or Key.Enter or Key.Escape or Key.Up or Key.Down)
        {
            return; // el menú, las listas y la pantalla de resultados interpretan estas teclas
        }

        switch (e.Key)
        {
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                OpenSongDialog();
                break;
            case Key.I when Keyboard.Modifiers == ModifierKeys.Control:
                ShowOptions(OptionsView.Section.Audio);
                break;
            case Key.Space:
                TogglePlayPause();
                break;
            case Key.F11:
                SetFullscreen(!_fullscreen);
                break;
            case Key.F9:
                StatusBar.Visibility = StatusBar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                break;
            case Key.Escape when _fullscreen:
                SetFullscreen(false);
                break;
            case Key.Escape when _player.IsPlaying:
                TogglePlayPause(); // primera vez: pausa
                break;
            case Key.Escape:
                ShowMenu(); // segunda: vuelve al menú (detiene la canción)
                break;
            case Key.J when Keyboard.Modifiers == ModifierKeys.Control:
                ShowPlayers(PlayersView.Section.Session);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OpenSongDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir canción UltraStar",
            Filter = "Canción UltraStar (*.txt)|*.txt|Todos los archivos (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            LoadSong(dialog.FileName);
        }
    }

    private int _loadVersion;

    /// <summary>
    /// Abre una canción. Leer el archivo y decodificar el audio (con ASIO, la canción entera) se hace en segundo
    /// plano con un cartel "CARGANDO…": la interfaz no se congela. Si mientras tanto se elige otra canción o se
    /// vuelve al menú, el resultado de esta carga se descarta (<see cref="_loadVersion"/>).
    /// </summary>
    private void LoadSong(string txtPath)
    {
        Diag.Mark($"cargar canción: {Path.GetFileName(txtPath)}");
        StopPreview();
        using (Diag.Time("reproductor: detener"))
        {
            _player.Stop();
        }

        _songFromLibrary = false;
        var version = ++_loadVersion;
        LoadingText.Text = MenuTexts.Loading(_prefs.CommentLanguage);
        LoadingLayer.Visibility = Visibility.Visible;
        var player = _player;

        Task.Run(() =>
        {
            Diag.Mark("carga: leyendo TXT");
            var song = UltraStarParser.ParseFile(txtPath);
            Diag.Mark("carga: TXT leído");
            var audioPath = SongMedia.ResolveInside(song.Directory!, song.AudioFile);
            if (audioPath is null)
            {
                throw new FileNotFoundException(song.AudioFile is null
                    ? "El archivo no indica un audio (#MP3 o #AUDIO)."
                    : $"El audio '{song.AudioFile}' no está dentro de la carpeta de la canción.");
            }

            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"No se encuentra el audio: {audioPath}");
            }

            Diag.Mark("carga: abriendo audio");
            player.Load(audioPath);
            Diag.Mark("carga: audio abierto");
            return (Song: song, AudioPath: audioPath);
        }).ContinueWith(task =>
        {
            if (version != _loadVersion)
            {
                return; // se pidió otra cosa mientras cargaba
            }

            LoadingLayer.Visibility = Visibility.Collapsed;
            if (task.IsCompletedSuccessfully)
            {
                _loadedPath = txtPath;
                _loadFailedPath = null;
                CompleteLoad(task.Result.Song, task.Result.AudioPath);
            }
            else
            {
                _loadFailedPath = txtPath;
                FailLoad(task.Exception?.GetBaseException() ?? new InvalidOperationException("La carga falló."));
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>La canción y su audio ya están listos: se arma el escenario y se pasa a él.</summary>
    private void CompleteLoad(Song song, string audioPath)
    {
        try
        {
            _song = song;
            _audioPath = audioPath;
            _singersConfirmed = false; // en cada canción se vuelve a elegir quién canta
            if (_autoConfirm)
            {
                // arranque pedido por el control remoto o por el avance automático con cantantes ya validados
                _singersConfirmed = true;
                _autoConfirm = false;
            }

            _voiceChoice.Clear(); // ...y qué voz canta cada micrófono (por omisión, el reparto automático)
            Diag.Mark("completar: video");
            LoadVideo(song);
            Diag.Mark("completar: escenario");
            Stage.Show(song, _player);
            Diag.Mark("completar: puntuadores");
            CreateScorers();
            Diag.Mark("completar: listo");
            TitleText.Text = $"{song.Artist} — {song.Title}";
            Title = $"Kantor — {song.Artist} — {song.Title}";

            var voices = song.Voices.Count == 1 ? "1 voz" : $"{song.Voices.Count} voces";
            StatusText.Text = song.Warnings.Count == 0
                ? $"{voices} · {song.Bpm:0.##} BPM · {_player.Duration:mm\\:ss}"
                : $"{voices} · {song.Bpm:0.##} BPM · {_player.Duration:mm\\:ss} · {song.Warnings.Count} advertencias: {song.Warnings[0]}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.Runtime.InteropServices.COMException)
        {
            FailLoad(e);
            return;
        }

        Menu.Visibility = Visibility.Collapsed; // canción cargada: se pasa al escenario
        SongList.Visibility = Visibility.Collapsed;
        UpdateControls();

        // Al entrar a una canción se pregunta quién canta (y, si hace falta, cómo se canta: partida o suelto).
        // Confirmar empieza la canción; Esc la deja cargada para darle Reproducir. Sin historial no hay nada que preguntar.
        if (_store is not null)
        {
            Dispatcher.BeginInvoke(TogglePlayPause, DispatcherPriority.Background);
        }
    }

    private void FailLoad(Exception e)
    {
        _abandonSeq++;
        _autoConfirm = false;
        _loadedPath = null;
        _song = null;
        _audioPath = null;
        Video.Load(null);
        Stage.TranslucentBackground = false;
        Stage.Show(null, null);
        CreateScorers();
        TitleText.Text = string.Empty;
        StatusText.Text = $"No se pudo abrir: {e.Message}";
        StatusBar.Visibility = Visibility.Visible; // un error no debe pasar desapercibido
        UpdateControls();
    }

    // ---- Video de fondo -----------------------------------------------------------------------------------

    /// <summary>Textos del HUD del escenario en el idioma activo.</summary>
    private void ApplyStageTexts()
    {
        RefreshStageRoster();
    }

    private void LoadVideo(Song song)
    {
        var path = song.VideoFile is null ? null : SongMedia.ResolveInside(song.Directory!, song.VideoFile);
        using (Diag.Time("video: cargar", 50))
        {
            Video.Load(path);
        }

        Stage.TranslucentBackground = Video.HasVideo;
        Stage.Duration = _player.Duration;
        ApplyStageTexts();
        if (song.VideoFile is not null && Video.Failure is not null)
        {
            _engineNote = Video.Failure;
        }
    }

    /// <summary>El video sigue al reloj del audio; la canción nunca espera al video.</summary>
    private void SyncVideo()
    {
        if (Video.HasVideo && _song is not null)
        {
            Video.Sync(_player.Position, _player.IsPlaying, _song.VideoGapSeconds);
        }
    }

    private void UpdateInputStatus()
    {
        var output = IsAsio && _engine.OutputPairs.Count > 0 ? $" · salida {_engine.OutputPairs[_engine.OutputPair]}" : string.Empty;
        var mics = _input.Map.ActiveSlots
            .Where(slot => _input.DeviceOf(slot) is not null && _input.Map[slot] is not null)
            .Select(slot => $"M{slot + 1} {_input.DeviceOf(slot)!.Name} · {_input.DeviceOf(slot)!.ChannelLabel(_input.Map[slot]!.Value.Channel)}")
            .ToList();
        InputText.Text = mics.Count > 0
            ? $"{(mics.Count == 1 ? "Entrada" : "Entradas")}: {string.Join(" | ", mics)} · {_engine.Name}{output}"
            : string.Empty;
        InputText.ToolTip = _engineNote;
    }

    private void TogglePlayPause()
    {
        if (!_player.IsLoaded)
        {
            return;
        }

        if (_player.IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            // Empezar de cero requiere jugadores elegidos; reanudar una pausa no.
            Diag.Mark("reproducir: participantes");
            if (!_player.IsPaused && !EnsureParticipants())
            {
                return;
            }

            HideResult();
            Diag.Mark("reproducir: iniciando audio");
            using (Diag.Time("reproductor: Play", 50))
            {
                _player.Play();
            }
        }

        UpdateControls();
    }

    private void UpdateControls()
    {
        if (PlayButton is null)
        {
            return;
        }

        PlayButton.IsEnabled = _player.IsLoaded;
        StopButton.IsEnabled = _player.IsLoaded;
        PlayButton.Content = _player.IsPlaying ? "Pausa (Espacio)" : "Reproducir (Espacio)";
        DifficultyBox.IsEnabled = DelayBox.IsEnabled = !_player.IsPlaying && !_player.IsPaused;
    }

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen)
        {
            return;
        }

        if (on)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreResize = ResizeMode;
            _restoreTopmost = Topmost;
            _restoreBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            // Pantalla completa real: sin borde, al tamaño del monitor entero y por encima de la barra de
            // tareas (maximizar solo cubre el área de trabajo).
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            _fullscreen = true; // antes de ajustar: los avisos de ubicación ya la tratan como pantalla completa
            Topmost = true;
            if (!FitFullscreen() && NativeMonitor.Describe(this) is null)
            {
                WindowState = WindowState.Maximized; // no se pudo consultar el monitor: lo mejor posible
            }
        }
        else
        {
            _fullscreen = false; // antes de mover la ventana: si no, el reajuste a monitor la vuelve a poner arriba de todo
            Topmost = _restoreTopmost;
            WindowState = WindowState.Normal;
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            WindowState = _restoreState;
        }

        _fullscreen = on;
        StatusText.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        // En pantalla completa no hay barra de título ni zona de arrastre/redimensión.
        TitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Chrome.CaptionHeight = on ? 0 : 38;
        Chrome.ResizeBorderThickness = on ? new Thickness(0) : new Thickness(6);
        FullscreenButton.Content = on ? "" : "";
    }

    // ---- Varios monitores ---------------------------------------------------------------------------------
    // En pantalla completa la ventana mide exactamente el monitor donde está (en píxeles físicos). Si algo la
    // mueve (el Agent de Avyn con windows.move, el usuario, un cambio de pantallas o de DPI) se vuelve a ajustar al
    // monitor donde quedó su centro; maximizada, se vuelve a maximizar en el monitor de destino.

    private bool _refitting;
    private IntPtr _lastMonitor;

    private bool FitFullscreen()
    {
        if (_refitting)
        {
            return false;
        }

        _refitting = true;
        try
        {
            return NativeMonitor.FitToMonitor(this);
        }
        finally
        {
            _refitting = false;
        }
    }

    /// <summary>
    /// En pantalla completa la ventana está "siempre arriba" (así tapa la barra de tareas) solo mientras tiene el
    /// foco: al pasar a otra aplicación deja de estarlo y esa aplicación se ve.
    /// </summary>
    private void OnWindowActiveChanged(object? sender, EventArgs e)
    {
        if (_fullscreen)
        {
            Topmost = IsActive;
        }
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e) => FollowMonitor();

    private void OnWindowDpiChanged(object sender, DpiChangedEventArgs e) => FollowMonitor();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(FollowMonitor);

    private void FollowMonitor()
    {
        if (!IsLoaded || _refitting)
        {
            return;
        }

        if (_fullscreen)
        {
            if (FitFullscreen())
            {
                ApplyMaximizedInset();
            }

            return;
        }

        if (NativeMonitor.Describe(this) is not { } monitor)
        {
            return;
        }

        var moved = _lastMonitor != IntPtr.Zero && monitor.Handle != _lastMonitor;
        _lastMonitor = monitor.Handle;
        if (moved && WindowState == WindowState.Maximized)
        {
            // maximizada y cambió de monitor: se la vuelve a maximizar para que cubra el área de trabajo del nuevo
            Dispatcher.BeginInvoke(() =>
            {
                if (WindowState == WindowState.Maximized && !_fullscreen)
                {
                    WindowState = WindowState.Normal;
                    WindowState = WindowState.Maximized;
                }
            }, DispatcherPriority.Background);
        }
    }

    // ---- Barra de título propia ---------------------------------------------------------------------------

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restaurar" : "Maximizar";
        Dispatcher.BeginInvoke(ApplyMaximizedInset, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Con una barra de título propia, una ventana maximizada se pasa unos píxeles de cada borde de la pantalla.
    /// Se compensa con un margen igual a lo que sobra respecto del área de trabajo del monitor.
    /// </summary>
    private void ApplyMaximizedInset()
    {
        if (WindowState != WindowState.Maximized || _fullscreen || NativeMonitor.WorkAreaOf(this) is not { } work)
        {
            Root.Margin = new Thickness(0);
            return;
        }

        var horizontal = Math.Max(0, (ActualWidth - work.Width) / 2);
        var vertical = Math.Max(0, (ActualHeight - work.Height) / 2);
        Root.Margin = new Thickness(horizontal, vertical, horizontal, vertical);
    }
}
