// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Kantor.App.Players;
using Kantor.App.Remote;
using Kantor.Core.Audio;
using Kantor.Core.Data;
using Kantor.Core.Menu;
using Kantor.Core.Remote;
using Kantor.Core.Scoring;
using Kantor.Core.Settings;
using Microsoft.Win32;

namespace Kantor.App;

/// <summary>
/// Control remoto (Avyn): el controlador siempre existe (también hace el avance automático de la cola); el pipe
/// solo se abre si el usuario lo habilitó en Opciones. La ventana entra al controlador por <see cref="IKantorHost"/>.
/// </summary>
public partial class MainWindow
{
    private sealed class WindowInvoker(Dispatcher dispatcher) : IUiInvoker
    {
        public Task<T> InvokeAsync<T>(Func<T> work) => dispatcher.InvokeAsync(work).Task;
    }

    private void StartRemote()
    {
        _controller = new KantorController(new KantorControllerOptions
        {
            Host = new HostAdapter(this),
            Catalog = _catalog,
            Queue = _queue,
            Store = _store,
            Covers = new WpfCoverProvider(),
        });
        _remoteTimer.Tick += (_, _) =>
        {
            try
            {
                _controller.Flush();
                UpdateCountdownLabel();
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                Diag.Mark("control remoto: " + e.Message);
            }
        };
        _remoteTimer.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        if (_prefs.RemoteControl)
        {
            OpenRemoteChannel();
        }
    }

    private void StopRemote()
    {
        _remoteTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        CloseRemoteChannel();
    }

    private void OpenRemoteChannel()
    {
        if (_server is not null || _controller is null)
        {
            return;
        }

        _remoteFailure = null;
        _server = new KantorIpcServer(_controller, new WindowInvoker(Dispatcher));
        _server.ConnectionChanged += connected => Dispatcher.BeginInvoke(() =>
        {
            if (connected)
            {
                _remoteFailure = null;
            }

            RefreshOptions();
        });
        _server.Log += message =>
        {
            Diag.Mark("control remoto: " + message);
            if (message.StartsWith("No se pudo crear el pipe", StringComparison.Ordinal))
            {
                _remoteFailure = message; // otra instancia de Kantaria ya lo tiene, o el sistema lo negó
                Dispatcher.BeginInvoke(RefreshOptions);
            }
        };
        _server.Start();
    }

    private void CloseRemoteChannel()
    {
        var server = _server;
        _server = null;
        if (server is not null)
        {
            // cerrar el pipe no debe colgar la interfaz: se espera en segundo plano
            _ = Task.Run(async () => await server.DisposeAsync().ConfigureAwait(false));
        }
    }

    private void SetRemoteControlSetting(bool on)
    {
        _prefs = _prefs with { RemoteControl = on };
        SaveSettings();
        if (on)
        {
            OpenRemoteChannel();
        }
        else
        {
            CloseRemoteChannel();
        }

        RefreshOptions();
    }

    private void SetAutoAdvanceSetting(bool on)
    {
        _prefs = _prefs with { AutoAdvance = on };
        SaveSettings();
        RefreshOptions();
    }

    private string? RemoteStatusText()
    {
        var language = _prefs.CommentLanguage;
        if (!_prefs.RemoteControl)
        {
            return null;
        }

        if (_remoteFailure is not null)
        {
            return OptionsTexts.Get(language, "game.remote.failed");
        }

        return OptionsTexts.Get(language, _server?.Connected == true ? "game.remote.connected" : "game.remote.waiting");
    }

    /// <summary>La cuenta regresiva del avance automático, en la pantalla de resultados.</summary>
    private void UpdateCountdownLabel()
    {
        var seconds = Results.Visibility == Visibility.Visible ? _controller?.AutoAdvanceSecondsRemaining : null;
        Results.SetCountdown(seconds is { } s
            ? QueueTexts.AutoAdvance(_prefs.CommentLanguage, Math.Max(1, (int)Math.Ceiling(s)))
            : null);
    }

    /// <summary>La ventana tal como la ve el controlador. Todo corre en el hilo de la interfaz.</summary>
    private sealed class HostAdapter(MainWindow w) : IKantorHost
    {
        public HostState Capture()
        {
            var screen = ScreenOf();
            var status = StatusOf(screen);
            var finished = status == PlaybackStatuses.Finished;
            var position = w._player.IsLoaded ? w._player.Position.TotalSeconds : 0;
            var duration = w._player.IsLoaded ? w._player.Duration.TotalSeconds : 0;
            if (finished)
            {
                position = duration;
            }

            var micSlots = w._input.Map.ActiveSlots.Select(s => s + 1).OrderBy(s => s).ToList();
            var slots = new List<HostSlot>();
            foreach (var entry in w._selection.OrderBy(s => s.Slot))
            {
                var index = entry.Slot - 1;
                var who = w.PlayerAt(index);
                var scorer = w._players.FirstOrDefault(p => p.Slot == index)?.Scorer;
                var snap = scorer?.Snapshot;
                var accuracy = snap is { BeatsEvaluated: > 0 } s ? s.BeatsHit / (double)s.BeatsEvaluated : 0;
                slots.Add(new HostSlot(entry.Slot, entry.PlayerId, who?.Name ?? string.Empty,
                    w._input.Map[index] is not null, snap?.Total ?? 0, accuracy));
            }

            return new HostState
            {
                Screen = screen,
                Status = status,
                PositionSeconds = position,
                DurationSeconds = duration,
                LoadedPath = w._song is null ? null : w._loadedPath,
                Artist = w._song?.Artist,
                Title = w._song?.Title,
                RunId = w._runId,
                Difficulty = w.SelectedDifficulty switch
                {
                    Difficulty.Medium => "medium",
                    Difficulty.Hard => "hard",
                    _ => "easy",
                },
                Solo = w._freePlay,
                MicrophoneSlots = micSlots,
                Slots = slots,
                LastResult = w._lastResult,
                ResultsAnimating = w.Results.Visibility == Visibility.Visible && w.Results.IsAnimating,
                ResultsInteractionSeq = w._resultsInteraction,
                AbandonSeq = w._abandonSeq,
                Display = DisplayOf(),
                Language = w._prefs.CommentLanguage,
                AutoAdvance = w._prefs.AutoAdvance,
                StoreAvailable = w._store is not null,
                HasLibraryFolders = w._prefs.LibraryPaths is { Count: > 0 },
                Attention = AttentionOf(),
            };
        }

        private string ScreenOf()
        {
            if (w.SessionPrompt.Visibility == Visibility.Visible)
            {
                return Screens.SessionGate;
            }

            if (w.PlayersScreen.Visibility == Visibility.Visible)
            {
                return w._inMembers ? Screens.WhoPlays : w._pendingPlay ? Screens.WhoSings : Screens.Players;
            }

            if (w.Options.Visibility == Visibility.Visible)
            {
                return Screens.Options;
            }

            if (w.LoadingLayer.Visibility == Visibility.Visible)
            {
                return Screens.Loading;
            }

            if (w.QueueScreen.Visibility == Visibility.Visible)
            {
                return Screens.Queue;
            }

            if (w.SongList.Visibility == Visibility.Visible)
            {
                return Screens.Library;
            }

            if (w.Results.Visibility == Visibility.Visible)
            {
                return Screens.Results;
            }

            if (w.Menu.Visibility == Visibility.Visible || w._song is null)
            {
                return Screens.Menu;
            }

            return Screens.Stage;
        }

        private string StatusOf(string screen)
        {
            if (screen == Screens.Results)
            {
                return PlaybackStatuses.Finished;
            }

            if (w._player.IsPlaying)
            {
                return PlaybackStatuses.Playing;
            }

            if (w._player.IsPaused)
            {
                return PlaybackStatuses.Paused;
            }

            return screen is Screens.Loading or Screens.WhoSings or Screens.WhoPlays or Screens.SessionGate
                ? PlaybackStatuses.Preparing
                : PlaybackStatuses.Idle;
        }

        private HostDisplay DisplayOf()
        {
            HostMonitor? monitor = null;
            if (NativeMonitor.Describe(w) is { } m)
            {
                monitor = new HostMonitor(m.DeviceName, m.Primary, m.X, m.Y, m.Width, m.Height, m.DpiScale);
            }

            var handle = new WindowInteropHelper(w).Handle;
            return new HostDisplay(w._fullscreen, Environment.ProcessId, "0x" + handle.ToInt64().ToString("X8"), monitor);
        }

        private List<HostAttention> AttentionOf()
        {
            var attention = new List<HostAttention>();
            if (w._engineNote is not null && w._prefs.AudioEngine == AudioEngineKind.Asio && !w.IsAsio)
            {
                attention.Add(new HostAttention("AUDIO_ENGINE_FALLBACK",
                    new Dictionary<string, string> { ["requested"] = "asio", ["active"] = "windows" }));
            }

            if (w._saveFailed)
            {
                attention.Add(new HostAttention("SAVE_FAILED"));
            }

            if (w._song?.VideoFile is not null && w.Video.Failure is not null)
            {
                attention.Add(new HostAttention("VIDEO_UNAVAILABLE"));
            }

            if (w._loadFailedPath is { } failed)
            {
                var entry = w._catalog.Current.FindByPath(failed);
                attention.Add(new HostAttention("SONG_LOAD_FAILED", entry is null
                    ? null
                    : new Dictionary<string, string> { ["libraryEntryId"] = entry.EntryId }));
            }

            return attention;
        }

        // ---- Acciones -------------------------------------------------------------------------------------

        public void StartSong(HostStartRequest request)
        {
            switch (request.Policy)
            {
                case SessionPolicy.Solo:
                    w._freePlay = true;
                    w._selection = request.Selection is null ? [] : [.. request.Selection];
                    break;
                case SessionPolicy.New:
                    w._freePlay = false;
                    w._session = w._store?.GetOpenSession();
                    w._selection = request.Selection is null ? [] : [.. request.Selection];
                    break;
                default:
                    w._freePlay = false;
                    w._session = w._store?.GetOpenSession();
                    if (request.Selection is not null)
                    {
                        w.SetSelection(request.Selection);
                    }
                    else if (w._session is not null && w._store is not null && w._selection.Count == 0)
                    {
                        w._selection = [.. w._store.GetSelection(w._session.Id)];
                    }

                    break;
            }

            w._sessionDecided = true;
            w._afterGate = null;
            w._autoConfirm = request.AutoConfirmSingers;
            w.StopPreview();
            w.HideResult();
            w.QueueScreen.Visibility = Visibility.Collapsed;
            w.SongList.Visibility = Visibility.Collapsed;
            w.Menu.Visibility = Visibility.Collapsed;
            w.UpdateSessionLabel();
            w.RefreshPlayerCache();
            w.LoadSong(request.Path);
        }

        public void ConfirmSingers() => w.ClosePlayers(ready: true);

        public void Pause()
        {
            if (w._player.IsPlaying)
            {
                w.TogglePlayPause();
            }
        }

        public void Resume()
        {
            if (w._player.IsPaused)
            {
                w.TogglePlayPause();
            }
        }

        public void Restart()
        {
            // la misma secuencia que "Otra vez": detener, puntaje en cero y volver a empezar con los mismos jugadores
            w.HideResult();
            w._player.Stop();
            w.ResetScorers();
            w.UpdateScore();
            w._player.Play();
            w.UpdateControls();
        }

        public void Stop()
        {
            if (w._player.IsPlaying || w._player.IsPaused)
            {
                w.OnStopClick(w, new RoutedEventArgs());
            }
            else
            {
                ShowMenu();
            }
        }

        public void ShowMenu()
        {
            w._inMembers = false;
            w._membersThen = null;
            w._playersFromSongList = false;
            w._afterGate = null;
            w._autoConfirm = false;
            w.ShowMenu();
        }

        public void SkipResultsAnimation() => w.Results.SkipAnimation();

        public void CloseResults() => w.OnResultsClose(w, EventArgs.Empty);

        public void SetFullscreen(bool enabled) => w.SetFullscreen(enabled);

        public void SetAutoAdvance(bool enabled) => w.SetAutoAdvanceSetting(enabled);

        public void ApplySelection(IReadOnlyList<SelectionEntry> selection)
        {
            w.SetSelection(selection);
            RefreshPlayersScreen();
        }

        public void DataChanged()
        {
            w.RefreshPlayerCache();
            w._session = w._store?.GetOpenSession();
            if (w._session is not null && !w._freePlay && w._store is not null)
            {
                var stored = w._store.GetSelection(w._session.Id);
                w._selection = [.. stored];
            }
            else if (w._session is null && !w._freePlay)
            {
                w._selection = [];
            }

            w.UpdateSessionLabel();
            w.CreateScorers();
            RefreshPlayersScreen();
            w.OnQueueChanged();
        }

        public void PlayersAdded()
        {
            w.RefreshPlayerCache();
            RefreshPlayersScreen();
        }

        private void RefreshPlayersScreen()
        {
            if (w.PlayersScreen.Visibility == Visibility.Visible && w._store is not null)
            {
                w.PlayersScreen.Show(w.BuildPlayersModel(), PlayersView.Section.Session, null, w._pendingPlay,
                    w._inMembers);
            }
        }
    }
}
