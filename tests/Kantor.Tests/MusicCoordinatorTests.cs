// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Music;
using Kantor.Core.Settings;
using Xunit;

namespace Kantor.Tests;

internal sealed class FakeMusicChannel : IMusicChannel
{
    public bool Available { get; set; } = true;

    public bool Playing { get; private set; }

    public float Gain { get; private set; }

    public int Starts { get; private set; }

    public int DoubleStarts { get; private set; }

    public int FailNextStarts { get; set; }

    public bool Disposed { get; private set; }

    public List<string> Calls { get; } = [];

    public bool IsAvailable => Available;

    public bool IsPlaying => Playing;

    public bool Start(bool fromBeginning)
    {
        Calls.Add(fromBeginning ? "start:begin" : "start:resume");
        if (Playing)
        {
            DoubleStarts++; // el coordinador nunca debe abrir dos salidas para la misma pista
        }

        if (FailNextStarts > 0)
        {
            FailNextStarts--;
            return false;
        }

        Playing = true;
        Starts++;
        return true;
    }

    public void SetGain(float gain) => Gain = gain;

    public void Stop()
    {
        if (Playing)
        {
            Calls.Add("stop");
        }

        Playing = false;
        Gain = 0;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeMusicOutput : IMusicOutput
{
    public FakeMusicChannel Menu { get; } = new();

    public FakeMusicChannel Results { get; } = new();

    public bool Disposed { get; private set; }

    public IMusicChannel Channel(MusicTrack track) => track == MusicTrack.Menu ? Menu : Results;

    public void Dispose() => Disposed = true;
}

public class MusicCoordinatorTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);

    private static (MusicCoordinator Music, FakeMusicOutput Output) Make(float volume = 0.2f)
    {
        var output = new FakeMusicOutput();
        return (new MusicCoordinator(output, volume), output);
    }

    private static void Run(MusicCoordinator music, double seconds)
    {
        for (var t = TimeSpan.Zero; t < TimeSpan.FromSeconds(seconds); t += Tick)
        {
            music.Advance(Tick);
        }
    }

    // ---- Estados -----------------------------------------------------------------------------------------------

    [Fact]
    public void Before_the_interface_is_ready_nothing_plays()
    {
        var (music, o) = Make();
        Run(music, 1);
        Assert.Equal(MusicScene.None, music.Scene);
        Assert.False(o.Menu.Playing);
        Assert.False(o.Results.Playing);
    }

    [Fact]
    public void Navigation_starts_the_menu_music_with_a_fade_in_to_the_user_volume()
    {
        var (music, o) = Make(0.2f);
        music.SetScene(MusicScene.Navigation);
        music.Advance(Tick);
        Assert.True(o.Menu.Playing);
        Assert.InRange(o.Menu.Gain, 0.001f, 0.1f); // arrancó: todavía subiendo
        Run(music, 0.6);
        Assert.Equal(0.2f, o.Menu.Gain, 3);
        Assert.False(o.Results.Playing);
    }

    [Fact]
    public void Moving_between_navigation_screens_never_restarts_the_loop()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        for (var i = 0; i < 50; i++)
        {
            music.SetScene(MusicScene.Navigation); // menú → biblioteca → opciones → jugadores…: la escena no cambia
            Run(music, 0.05);
        }

        Assert.Equal(1, o.Menu.Starts);
        Assert.Equal(["start:resume"], o.Menu.Calls);
    }

    [Fact]
    public void Starting_a_song_fades_the_music_out_in_about_half_a_second_and_then_stops_it()
    {
        var (music, o) = Make(0.5f);
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.SetScene(MusicScene.Song);
        music.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True(o.Menu.Playing);
        Assert.InRange(o.Menu.Gain, 0.2f, 0.3f); // a mitad del fundido
        Run(music, 0.4);
        Assert.False(o.Menu.Playing);
        Assert.Contains("stop", o.Menu.Calls);
        Assert.Equal(0f, o.Menu.Gain);
    }

    [Fact]
    public void While_a_song_plays_or_is_paused_neither_track_is_audible()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.SetScene(MusicScene.Song);
        for (var i = 0; i < 100; i++)
        {
            music.SetScene(MusicScene.Song); // la pausa y la reanudación no cambian la escena
            Run(music, 0.1);
        }

        Assert.False(o.Menu.Playing);
        Assert.False(o.Results.Playing);
        Assert.Equal(1, o.Menu.Starts); // ni música de menú durante la pausa
    }

    [Fact]
    public void A_cancelled_song_brings_the_menu_music_back_where_it_was()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.SetScene(MusicScene.Song);
        Run(music, 1);
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        Assert.True(o.Menu.Playing);
        Assert.Equal(["start:resume", "stop", "start:resume"], o.Menu.Calls); // sigue donde quedó: nunca "begin"
    }

    [Fact]
    public void Back_to_navigation_after_a_song_waits_a_moment_to_avoid_blips()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.SetScene(MusicScene.Song);
        Run(music, 1);
        music.SetScene(MusicScene.Navigation); // la canción terminó, los resultados todavía no se dibujaron
        music.Advance(TimeSpan.FromMilliseconds(60));
        Assert.Equal(MusicScene.Song, music.Scene);
        Assert.False(o.Menu.Playing);
        music.SetScene(MusicScene.Results); // aparecen los resultados: nunca sonó el menú
        Run(music, 1);
        Assert.Equal(1, o.Menu.Starts);
        Assert.True(o.Results.Playing);
    }

    [Fact]
    public void Results_start_the_celebration_from_the_beginning_and_the_menu_fades_out()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.SetScene(MusicScene.Results);
        Run(music, 1);
        Assert.Equal(["start:begin"], o.Results.Calls);
        Assert.False(o.Menu.Playing);
        Assert.Equal(0.2f, o.Results.Gain, 3);
    }

    [Fact]
    public void Leaving_results_fades_them_out_and_brings_the_menu_back()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Results);
        Run(music, 1);
        music.SetScene(MusicScene.Navigation);
        Run(music, 0.2);
        Assert.True(o.Results.Playing); // todavía en el fundido
        Assert.True(o.Menu.Playing);    // y la de menú ya entra
        Run(music, 1);
        Assert.False(o.Results.Playing);
        Assert.Equal(0.2f, o.Menu.Gain, 3);
    }

    [Fact]
    public void Results_of_another_song_start_again_from_the_beginning()
    {
        var (music, o) = Make();
        for (var i = 0; i < 3; i++)
        {
            music.SetScene(MusicScene.Results);
            Run(music, 1);
            music.SetScene(MusicScene.Navigation);
            Run(music, 1);
        }

        Assert.Equal(["start:begin", "stop", "start:begin", "stop", "start:begin", "stop"], o.Results.Calls);
    }

    [Fact]
    public void Re_entering_results_during_the_fade_out_continues_without_a_jump_or_a_second_track()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Results);
        Run(music, 1);
        music.SetScene(MusicScene.Navigation);
        Run(music, 0.2); // a medio fundido
        var gainMid = o.Results.Gain;
        music.SetScene(MusicScene.Results);
        music.Advance(Tick);
        Assert.True(o.Results.Gain >= gainMid); // sube desde donde estaba: no se reinicia
        Run(music, 1);
        Assert.Equal(1, o.Results.Starts);
        Assert.Equal(0, o.Results.DoubleStarts);
    }

    // ---- Cambios rápidos y cancelaciones ---------------------------------------------------------------------------

    [Fact]
    public void Rapid_scene_changes_never_open_two_outputs_for_one_track_and_end_consistent()
    {
        var (music, o) = Make();
        var scenes = new[] { MusicScene.Navigation, MusicScene.Song, MusicScene.Navigation, MusicScene.Results,
            MusicScene.Navigation, MusicScene.Results, MusicScene.Song, MusicScene.Results, MusicScene.Navigation };
        foreach (var scene in scenes)
        {
            music.SetScene(scene);
            music.Advance(TimeSpan.FromMilliseconds(40));
            Assert.Equal(0, o.Menu.DoubleStarts);
            Assert.Equal(0, o.Results.DoubleStarts);
        }

        Run(music, 2);
        Assert.Equal(MusicScene.Navigation, music.Scene);
        Assert.True(o.Menu.Playing);
        Assert.False(o.Results.Playing); // al asentarse suena una sola pista
    }

    [Fact]
    public void A_song_that_starts_during_a_fade_in_cancels_it_and_fades_out_from_the_current_level()
    {
        var (music, o) = Make(1f);
        music.SetScene(MusicScene.Navigation);
        music.Advance(TimeSpan.FromMilliseconds(200)); // a mitad del fundido de entrada
        var level = music.LevelOf(MusicTrack.Menu);
        Assert.InRange(level, 0.3f, 0.7f);
        music.SetScene(MusicScene.Song);
        music.Advance(Tick);
        Assert.True(music.LevelOf(MusicTrack.Menu) < level);
        Run(music, 1);
        Assert.False(o.Menu.Playing);
    }

    [Fact]
    public void A_song_started_during_the_results_fade_leaves_everything_silent()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Results);
        Run(music, 1);
        music.SetScene(MusicScene.Navigation);
        Run(music, 0.2);
        music.SetScene(MusicScene.Song);
        Run(music, 1);
        Assert.False(o.Menu.Playing);
        Assert.False(o.Results.Playing);
    }

    // ---- Volumen -----------------------------------------------------------------------------------------------

    [Fact]
    public void Volume_zero_means_no_music_and_no_output_is_ever_opened()
    {
        var (music, o) = Make(0f);
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.SetScene(MusicScene.Results);
        Run(music, 1);
        Assert.Equal(0, o.Menu.Starts);
        Assert.Equal(0, o.Results.Starts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(55)]
    [InlineData(100)]
    public void Any_volume_from_1_to_100_percent_is_the_final_gain(int percent)
    {
        var (music, o) = Make(MusicVolume.ToGain(percent));
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        Assert.Equal(percent / 100f, o.Menu.Gain, 3);
    }

    [Fact]
    public void Changing_the_volume_applies_in_real_time_also_in_the_middle_of_a_fade()
    {
        var (music, o) = Make(0.2f);
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.SetVolume(0.8f);
        Assert.Equal(0.8f, o.Menu.Gain, 3); // al instante, sin esperar el próximo cuadro
        music.SetScene(MusicScene.Song);
        music.Advance(TimeSpan.FromMilliseconds(250));
        music.SetVolume(0.4f);
        Assert.InRange(o.Menu.Gain, 0.15f, 0.25f); // 50 % del fundido × 0,4
        Run(music, 1);
        Assert.False(o.Menu.Playing);
    }

    [Fact]
    public void Turning_the_volume_down_to_zero_releases_the_device_and_up_again_resumes()
    {
        var (music, o) = Make(0.2f);
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.SetVolume(0f);
        Assert.False(o.Menu.Playing);
        music.SetVolume(0.3f);
        Run(music, 0.1);
        Assert.True(o.Menu.Playing);
        Assert.Equal(["start:resume", "stop", "start:resume"], o.Menu.Calls);
    }

    [Fact]
    public void Volume_is_kept_between_runs_and_clamped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kantor-music-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(20, new UserSettings().MusicVolumePercent); // moderado por omisión
            Assert.True(UserSettingsStore.Save(path, new UserSettings { MusicVolumePercent = 65 }));
            Assert.Equal(65, UserSettingsStore.Load(path).MusicVolumePercent);
            Assert.Equal(0, UserSettingsStore.Load(WriteRaw(path, """{"MusicVolumePercent": -5}""")).MusicVolumePercent);
            Assert.Equal(100, UserSettingsStore.Load(WriteRaw(path, """{"MusicVolumePercent": 900}""")).MusicVolumePercent);
            Assert.Equal(20, UserSettingsStore.Load(WriteRaw(path, "{}")).MusicVolumePercent); // archivo de otra versión
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteRaw(string path, string json)
    {
        File.WriteAllText(path, json);
        return path;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, 2)]
    [InlineData(24, 2)]
    [InlineData(26, 3)]
    [InlineData(100, 10)]
    [InlineData(250, 10)]
    [InlineData(-3, 0)]
    public void The_options_steps_map_percentages_to_the_nearest_step(int percent, int index)
    {
        Assert.Equal(index, MusicVolume.IndexOf(percent));
        Assert.Equal(11, MusicVolume.Steps.Count);
        Assert.Equal(0, MusicVolume.Steps[0]);
        Assert.Equal(100, MusicVolume.Steps[^1]);
    }

    // ---- Fallos y cierre ---------------------------------------------------------------------------------------

    [Fact]
    public void A_missing_menu_file_leaves_that_track_silent_and_the_rest_working()
    {
        var (music, o) = Make();
        o.Menu.Available = false;
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        Assert.Equal(0, o.Menu.Starts);
        music.SetScene(MusicScene.Results);
        Run(music, 1);
        Assert.True(o.Results.Playing);
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        Assert.False(o.Results.Playing);
    }

    [Fact]
    public void A_missing_results_file_does_not_break_the_transition_and_the_menu_still_fades_out()
    {
        var (music, o) = Make();
        o.Results.Available = false;
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.SetScene(MusicScene.Results);
        Run(music, 1);
        Assert.False(o.Menu.Playing);
        Assert.Equal(0, o.Results.Starts);
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        Assert.True(o.Menu.Playing);
    }

    [Fact]
    public void An_output_that_fails_to_open_is_retried_every_two_seconds_and_then_plays()
    {
        var (music, o) = Make();
        o.Menu.FailNextStarts = 2; // dispositivo ausente o cambiando
        music.SetScene(MusicScene.Navigation);
        music.Advance(Tick);
        Assert.False(o.Menu.Playing);
        Run(music, 1); // dentro de los 2 s: no insiste
        Assert.Single(o.Menu.Calls);
        Run(music, 2.2);
        Assert.Equal(2, o.Menu.Calls.Count); // segundo intento, también falla
        Run(music, 2.2);
        Assert.True(o.Menu.Playing);
        Assert.Equal(3, o.Menu.Calls.Count);
    }

    [Fact]
    public void An_output_that_drops_while_playing_is_reopened_without_restarting_the_loop()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        o.Menu.Stop(); // el dispositivo se desconectó: el canal se cerró solo
        Run(music, 0.2);
        Assert.True(o.Menu.Playing);
        Assert.Equal("start:resume", o.Menu.Calls[^1]);
    }

    [Fact]
    public void The_song_list_is_silent_so_previews_can_be_heard_and_the_menu_music_resumes_after()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.SetScene(MusicScene.Library);
        Run(music, 1);
        Assert.False(o.Menu.Playing);
        Assert.False(o.Results.Playing);
        for (var i = 0; i < 20; i++)
        {
            music.SetScene(MusicScene.Library); // recorrer la lista no cambia nada
            Run(music, 0.1);
        }

        Assert.Equal(1, o.Menu.Starts);
        music.SetScene(MusicScene.Navigation); // vuelve al menú: sin espera y donde había quedado
        music.Advance(Tick);
        Assert.True(o.Menu.Playing);
        Assert.Equal(["start:resume", "stop", "start:resume"], o.Menu.Calls);
    }

    [Fact]
    public void Opening_the_song_list_and_then_a_song_never_brings_the_menu_music_back_in_between()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.SetScene(MusicScene.Library);
        Run(music, 1);
        music.SetScene(MusicScene.Song);
        Run(music, 1);
        Assert.Equal(1, o.Menu.Starts);
        Assert.False(o.Menu.Playing);
    }

    [Fact]
    public void Closing_stops_and_releases_everything_at_once_and_later_calls_do_nothing()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Navigation);
        Run(music, 1);
        music.Dispose();
        Assert.False(o.Menu.Playing);
        Assert.True(o.Menu.Disposed);
        Assert.True(o.Results.Disposed);
        Assert.True(o.Disposed);
        var calls = o.Menu.Calls.Count;
        music.SetScene(MusicScene.Results);
        music.SetVolume(1f);
        music.Advance(TimeSpan.FromSeconds(5));
        music.Dispose(); // idempotente
        Assert.Equal(calls, o.Menu.Calls.Count);
        Assert.False(o.Results.Playing);
    }

    [Fact]
    public void Closing_in_the_middle_of_a_fade_is_clean()
    {
        var (music, o) = Make();
        music.SetScene(MusicScene.Results);
        Run(music, 1);
        music.SetScene(MusicScene.Navigation);
        music.Advance(TimeSpan.FromMilliseconds(150));
        music.Dispose();
        Assert.False(o.Menu.Playing);
        Assert.False(o.Results.Playing);
    }
}
