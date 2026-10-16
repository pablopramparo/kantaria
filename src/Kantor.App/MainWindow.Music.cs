// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Kantor.App.Audio;
using Kantor.Core.Music;

namespace Kantor.App;

/// <summary>
/// Música de la interfaz (inicio y navegación, resultados). Un solo <see cref="MusicCoordinator"/> decide qué suena a
/// partir de UNA función de estado (<see cref="DesiredMusicScene"/>) que se evalúa en un temporizador: no hay eventos
/// dispersos por pantalla. Es independiente del motor de la canción, de la captura de micrófonos y del puntaje.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _musicTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(33),
    };

    private readonly Stopwatch _musicClock = new();
    private MusicCoordinator? _music;
    private TimeSpan _musicLast;

    /// <summary>Se llama cuando la ventana principal ya está lista.</summary>
    private void StartMusic()
    {
        if (_music is not null)
        {
            return;
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Music");
        _music = new MusicCoordinator(new UiMusicOutput(directory), MusicVolume.ToGain(_prefs.MusicVolumePercent));
        _musicClock.Start();
        _musicTimer.Tick += (_, _) =>
        {
            if (_music is null)
            {
                return;
            }

            var now = _musicClock.Elapsed;
            _music.SetScene(DesiredMusicScene());
            _music.Advance(now - _musicLast);
            _musicLast = now;
        };
        _musicTimer.Start();
    }

    /// <summary>
    /// La única fuente de verdad: resultados en pantalla → melodía de resultados; canción sonando o en pausa → nada; lista
    /// de canciones → nada (así se oyen las vistas previas); cualquier otra cosa (menú, jugadores, opciones, cola, carga,
    /// "¿quién canta?") → música de navegación.
    /// </summary>
    private MusicScene DesiredMusicScene()
    {
        if (Results.Visibility == Visibility.Visible)
        {
            return MusicScene.Results;
        }

        if (_player.IsPlaying || _player.IsPaused)
        {
            return MusicScene.Song;
        }

        return SongList.Visibility == Visibility.Visible ? MusicScene.Library : MusicScene.Navigation;
    }

    private void StopMusic()
    {
        _musicTimer.Stop();
        _music?.Dispose();
        _music = null;
    }

    private void SetMusicVolume(int percent)
    {
        _prefs = _prefs with { MusicVolumePercent = MusicVolume.Clamp(percent) };
        SaveSettings();
        _music?.SetVolume(MusicVolume.ToGain(_prefs.MusicVolumePercent));
    }
}
