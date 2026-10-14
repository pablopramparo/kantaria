using System.IO;
using System.Windows;
using System.Windows.Input;
using Kantor.App.Audio;
using Kantor.Core.Songs;
using Microsoft.Win32;

namespace Kantor.App;

public partial class MainWindow : Window
{
    private readonly SongPlayer _player = new();
    private bool _fullscreen;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;

    public MainWindow()
    {
        InitializeComponent();
        _player.Ended += (_, _) => UpdateControls();

        Loaded += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                LoadSong(args[1]);
            }
        };
        Closed += (_, _) => _player.Dispose();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenSongDialog();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        UpdateControls();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                OpenSongDialog();
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
            Stage.Show(song, _player);
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
            Stage.Show(null, null);
            TitleText.Text = string.Empty;
            StatusText.Text = $"No se pudo abrir: {e.Message}";
        }

        UpdateControls();
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
