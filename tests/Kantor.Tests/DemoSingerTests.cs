// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Demo;
using Kantor.Core.Pitch;
using Kantor.Core.Scoring;
using Kantor.Core.Songs;
using Kantor.Core.Timing;

namespace Kantor.Tests;

public class DemoSingerTests
{
    private static readonly SongTiming Timing = new(bpm: 240, gapMilliseconds: 0);
    private static readonly TimeSpan Reaction = TimeSpan.FromMilliseconds(175);

    private static Voice MakeVoice()
    {
        int[] tones = [0, 4, 7, 12, 7, 4, 2, 5];
        var lines = new List<Line>();
        for (var l = 0; l < 4; l++)
        {
            var notes = new List<Note>();
            for (var i = 0; i < 4; i++)
            {
                notes.Add(new Note(NoteKind.Normal, l * 40 + i * 8, 6, tones[(l * 4 + i) % tones.Length], "la"));
            }

            lines.Add(new Line(notes, l * 40 + 36));
        }

        return new Voice("v", lines);
    }

    /// <summary>Canta toda la canción con un cantante simulado, como lo hace la sesión de captura, y devuelve el puntaje.</summary>
    private static int Score(Voice voice, double accuracy, int seed)
    {
        var singer = new DemoSinger(accuracy, Reaction, seed);
        var scorer = new VoiceScorer(voice, Timing, new ScoringRules { Difficulty = Difficulty.Medium, AcousticLatency = Reaction });
        var timeline = new PitchTimeline();
        var end = Timing.TimeOf(voice.Lines[^1].EndBeat) + TimeSpan.FromSeconds(1);
        var window = TimeSpan.FromMilliseconds(40);
        for (var t = TimeSpan.Zero; t < end; t += TimeSpan.FromMilliseconds(20))
        {
            var (frequency, level) = singer.Sing(voice, Timing, t);
            timeline.Add(new PitchSample(t, window, frequency, frequency > 0 ? 0.95 : 0, level));
            scorer.Advance(t, timeline);
        }

        scorer.Advance(end + TimeSpan.FromSeconds(1), timeline);
        return scorer.Snapshot.Total;
    }

    [Fact]
    public void Is_silent_where_the_song_has_no_notes()
    {
        var voice = MakeVoice();
        var (frequency, level) = new DemoSinger(1, Reaction, 1).Sing(voice, Timing, TimeSpan.FromMinutes(5));
        Assert.Equal(0, frequency);
        Assert.True(level < -50);
    }

    [Fact]
    public void Is_deterministic_for_the_same_seed()
    {
        var voice = MakeVoice();
        var t = Timing.TimeOf(10) + Reaction + TimeSpan.FromMilliseconds(300);
        Assert.Equal(new DemoSinger(0.5, Reaction, 3).Sing(voice, Timing, t), new DemoSinger(0.5, Reaction, 3).Sing(voice, Timing, t));
    }

    [Fact]
    public void A_perfect_singer_gets_nearly_the_maximum()
    {
        Assert.InRange(Score(MakeVoice(), 1.0, 1), 9000, 10000);
    }

    [Fact]
    public void A_singer_who_always_misses_gets_almost_nothing()
    {
        Assert.InRange(Score(MakeVoice(), 0.0, 1), 0, 1500);
    }

    [Fact]
    public void Better_accuracy_gives_a_better_score()
    {
        var voice = MakeVoice();
        Assert.True(Score(voice, 0.95, 5) > Score(voice, 0.5, 5));
    }
}
