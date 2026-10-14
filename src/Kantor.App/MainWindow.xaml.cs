// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kantor.App.Audio;
using Kantor.App.Stage;
using Kantor.Core.Audio;
using Kantor.Core.Scoring;
using Kantor.Core.Settings;
using Kantor.Core.Songs;
using Kantor.Core.Timing;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Kantor.App;

public partial class MainWindow : Window
{
    private readonly IAudioEngine _engine;
    private string? _engineNote;
    private readonly ISongPlayer _player;
    private readonly ActiveInput _input;
    private readonly DispatcherTimer _scoreTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Song? _song;
    private VoiceScorer? _scorer;
    private InputWindow? _inputWindow;
    private bool _fullscreen;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;

    private readonly string _settingsPath = UserSettingsStore.DefaultPath();
    private readonly UserSettings _startupSettings;
    private bool _restoringSettings;
    private string? _lastInputId;
    private int _lastInputChannel;
    private Dictionary<string, int>? _profiles;

    public MainWindow()
    {
        InitializeComponent();
        _startupSettings = UserSettingsStore.Load(_settingsPath);
        _lastInputId = _startupSettings.InputDeviceId;
        _lastInputChannel = _startupSettings.InputChannel;
        _profiles = _startupSettings.DelayProfiles is { } stored ? new Dictionary<string, int>(stored) : null;
        ApplySettings(_startupSettings);

        // El motor de audio es una configuración global; Windows es el respaldo si el pedido no está disponible.
        _engine = AudioEngineFactory.Create(_startupSettings.AudioEngine, out var engineNote);
        _player = _engine.CreatePlayer();
        _engineNote = engineNote;
        if (engineNote is not null)
        {
            StatusText.Text = engineNote;
        }

        _input = new ActiveInput(_player, _engine.CreateCapture);
        _input.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            // Se recuerda la última entrada abierta; cerrarla a propósito la olvida, pero un arranque sin el
            // dispositivo (desenchufado) no toca lo guardado.
            _lastInputId = _input.Session?.Device.Id;
            _lastInputChannel = _input.Channel;
            UpdateInputStatus();
            ApplyProfileDelay();
            SaveSettings();
        });
        _player.Ended += (_, _) => UpdateControls();
        _scoreTimer.Tick += (_, _) => AdvanceScore();

        Loaded += (_, _) =>
        {
            RestoreInput();
            _scoreTimer.Start();
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                LoadSong(args[1]);
            }
        };
        Closed += (_, _) =>
        {
            _scoreTimer.Stop();
            _input.Dispose();
            _player.Dispose();
            _engine.Dispose();
        };
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenSongDialog();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void OnInputsClick(object sender, RoutedEventArgs e) => ShowInputs();

    private void ShowInputs()
    {
        if (_inputWindow is { IsLoaded: true })
        {
            _inputWindow.Activate();
            return;
        }

        _inputWindow = new InputWindow(_input, _engine) { Owner = this };
        _inputWindow.Show();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        // Detener devuelve la canción al principio: el puntaje empieza de nuevo. Pausar, en cambio, lo conserva.
        _player.Stop();
        _scorer?.Reset();
        UpdateScore();
        UpdateControls();
    }

    private void OnDifficultyChanged(object sender, SelectionChangedEventArgs e)
    {
        // Cambiar la dificultad puntúa de nuevo desde cero; el selector solo está activo con la canción detenida.
        if (IsInitialized)
        {
            CreateScorer();
            SaveSettings();
        }
    }

    private void OnDelayChanged(object sender, SelectionChangedEventArgs e)
    {
        // Como la dificultad: cambia cómo se puntúa, así que se vuelve a empezar; solo con la canción detenida.
        if (IsInitialized)
        {
            CreateScorer();
            RememberDelayForCurrentDevice();
            SaveSettings();
        }
    }

    /// <summary>Clave del perfil de la combinación actual (salida predeterminada + entrada + canal), si hay entrada.</summary>
    private string? CurrentProfileKey()
    {
        if (_lastInputId is null)
        {
            return null;
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

        return UserSettings.ProfileKey(output, _lastInputId, _lastInputChannel);
    }

    private void RememberDelayForCurrentDevice()
    {
        if (_restoringSettings || CurrentProfileKey() is not { } key)
        {
            return;
        }

        _profiles ??= [];
        _profiles[key] = Math.Max(DelayBox.SelectedIndex, 0) * UserSettings.VoiceDelayStepMs;
    }

    /// <summary>Si esta combinación de salida y entrada ya tiene un retardo guardado, lo pone en el selector.</summary>
    private void ApplyProfileDelay()
    {
        if (CurrentProfileKey() is not { } key || _profiles is null || !_profiles.TryGetValue(key, out var ms))
        {
            return;
        }

        var index = Math.Clamp(ms / UserSettings.VoiceDelayStepMs, 0, DelayBox.Items.Count - 1);
        if (DelayBox.SelectedIndex != index)
        {
            DelayBox.SelectedIndex = index;
        }
    }

    private void OnCalibrateClick(object sender, RoutedEventArgs e)
    {
        if (_input.Session is not { } session)
        {
            StatusText.Text = "Elegí primero una entrada (Entradas… o Ctrl+I) para calibrar con ella.";
            return;
        }

        if (_player.IsPlaying || _player.IsPaused)
        {
            StatusText.Text = "Detené la canción antes de calibrar.";
            return;
        }

        if (_engine.Kind != AudioEngineKind.Windows)
        {
            StatusText.Text = "La calibración por ahora solo mide el motor de Windows.";
            return;
        }

        var window = new CalibrationWindow(session.Device, _input.Channel, ms =>
        {
            DelayBox.SelectedIndex = Math.Clamp(ms / UserSettings.VoiceDelayStepMs, 0, DelayBox.Items.Count - 1);
            StatusText.Text = $"Retardo de voz ajustado a {ms} ms por calibración.";
        }) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>Pone los selectores según lo guardado, sin disparar guardados ni reinicios de más.</summary>
    private void ApplySettings(UserSettings settings)
    {
        _restoringSettings = true;
        try
        {
            DifficultyBox.SelectedIndex = (int)settings.Difficulty;
            DelayBox.SelectedIndex = settings.VoiceDelayMs / UserSettings.VoiceDelayStepMs;
            DisplayBox.SelectedIndex = settings.VoiceDisplay;
        }
        finally
        {
            _restoringSettings = false;
        }
    }

    private void SaveSettings()
    {
        if (_restoringSettings || !IsInitialized)
        {
            return;
        }

        UserSettingsStore.Save(_settingsPath, new UserSettings
        {
            Difficulty = SelectedDifficulty,
            VoiceDisplay = DisplayBox.SelectedIndex == 1 ? 1 : 0,
            VoiceDelayMs = Math.Max(DelayBox.SelectedIndex, 0) * UserSettings.VoiceDelayStepMs,
            InputDeviceId = _lastInputId,
            InputChannel = _lastInputChannel,
            DelayProfiles = _profiles,
            AudioEngine = _startupSettings.AudioEngine,
        });
    }

    /// <summary>Vuelve a abrir la última entrada si sigue disponible; si no, queda sin entrada y sin avisar.</summary>
    private void RestoreInput()
    {
        if (_startupSettings.InputDeviceId is not { } id)
        {
            return;
        }

        try
        {
            if (_engine.ListInputs().FirstOrDefault(d => d.Id == id) is { } device)
            {
                _input.Open(device, Math.Min(_startupSettings.InputChannel, device.Channels - 1));
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException
                                      or UnauthorizedAccessException)
        {
            StatusText.Text = $"No se pudo reabrir la entrada anterior: {e.Message}";
        }
    }

    /// <summary>Retardo de voz elegido: 25 ms por posición de la lista (0 a 300 ms).</summary>
    private TimeSpan SelectedVoiceDelay =>
        TimeSpan.FromMilliseconds(25 * Math.Max(DelayBox.SelectedIndex, 0));

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

    private void CreateScorer()
    {
        _scorer = _song is { Voices.Count: > 0 } song
            ? new VoiceScorer(song.Voices[0], new SongTiming(song.Bpm, song.GapMilliseconds),
                new ScoringRules { Difficulty = SelectedDifficulty, AcousticLatency = SelectedVoiceDelay })
            : null;
        Stage.Scorer = _scorer;
        Stage.VoiceDelay = SelectedVoiceDelay;
        UpdateScore();
    }

    /// <summary>
    /// Solo avanza con la canción sonando: en pausa la posición no se mueve y el puntaje se conserva; al terminar
    /// o detenerse no se toca (el final queda visible hasta volver a empezar).
    /// </summary>
    private void AdvanceScore()
    {
        if (_scorer is null || !_player.IsPlaying)
        {
            return;
        }

        var position = _player.Position;
        if (position <= TimeSpan.Zero)
        {
            return; // lectura de reloj no disponible: no se interpreta como un retroceso
        }

        _scorer.Advance(position, _input.Timeline);
        UpdateScore();
    }

    private void UpdateScore()
    {
        var s = _scorer?.Snapshot;
        ScoreText.Text = (s?.Total ?? 0).ToString("N0");
        ScoreText.ToolTip = s is { } snap
            ? $"Notas {snap.NotesRounded:N0} · Doradas {snap.GoldenRounded:N0} · Líneas {snap.LinesRounded:N0}\n" +
              $"Beats acertados {snap.BeatsHit:N0} de {snap.BeatsEvaluated:N0} evaluados ({snap.BeatsTotal:N0} en total)"
            : null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                OpenSongDialog();
                break;
            case Key.I when Keyboard.Modifiers == ModifierKeys.Control:
                ShowInputs();
                break;
            case Key.Space:
                TogglePlayPause();
                break;
            case Key.F11:
                SetFullscreen(!_fullscreen);
                break;
            case Key.Escape when _fullscreen:
                SetFullscreen(false);
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

    private void LoadSong(string txtPath)
    {
        _player.Stop();

        try
        {
            var song = UltraStarParser.ParseFile(txtPath);
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

            _player.Load(audioPath);
            _song = song;
            CreateScorer();
            Stage.Show(song, _player, _input.Timeline);
            TitleText.Text = $"{song.Artist} — {song.Title}";
            Title = $"Kantor — {song.Artist} — {song.Title}";

            var voices = song.Voices.Count == 1 ? "1 voz" : $"{song.Voices.Count} voces";
            StatusText.Text = song.Warnings.Count == 0
                ? $"{voices} · {song.Bpm:0.##} BPM · {_player.Duration:mm\\:ss}"
                : $"{voices} · {song.Bpm:0.##} BPM · {_player.Duration:mm\\:ss} · {song.Warnings.Count} advertencias: {song.Warnings[0]}";
        }
        catch (Exception e) when (e is SongFormatException or IOException or UnauthorizedAccessException
                                      or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _song = null;
            CreateScorer();
            Stage.Show(null, null);
            TitleText.Text = string.Empty;
            StatusText.Text = $"No se pudo abrir: {e.Message}";
        }

        UpdateControls();
    }

    private void UpdateInputStatus()
    {
        InputText.Text = _input.Session is { } session
            ? $"Entrada: {session.Device.Name} · canal {_input.Channel + 1} · {_engine.Name}"
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
            _player.Play();
        }

        UpdateControls();
    }

    private void UpdateControls()
    {
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
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _restoreStyle;
            WindowState = _restoreState;
        }

        _fullscreen = on;
        Toolbar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Visibility = Toolbar.Visibility;
    }
}
