// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Pitch;
using Kantor.Core.Scoring;
using Kantor.Core.Songs;
using Kantor.Core.Timing;

namespace Kantor.Tests;

public class BeatEvaluationTests
{
    private static PitchSample Sung(double ultraStarTone, double level = -20) =>
        new(TimeSpan.Zero, TimeSpan.FromMilliseconds(43), SingerSim.Hertz(ultraStarTone), 0.9, level);

    private static ScoringRules Easy => new() { Difficulty = Difficulty.Easy };

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(-3, -3)]
    [InlineData(6, 6)]       // a 6 semitonos se conserva el signo tal como viene
    [InlineData(-6, -6)]
    [InlineData(7, -5)]
    [InlineData(-7, 5)]
    [InlineData(12, 0)]
    [InlineData(18, 6)]
    [InlineData(-18, -6)]
    [InlineData(-9, 3)]
    [InlineData(48, 0)]
    [InlineData(-47, 1)]
    public void Offset_is_folded_within_six_semitones(int sungMinusNote, int expected)
    {
        Assert.Equal(expected, NoteScoring.OffsetWithinOctave(60 + sungMinusNote, 60));
        Assert.Equal(expected, NoteScoring.OffsetWithinOctave(sungMinusNote, 0));
    }

    [Fact]
    public void A_hit_is_shown_on_the_note_even_when_sung_off_pitch_within_tolerance()
    {
        // Nota en -5 (G3 con C4 = 0); se canta 2 semitonos arriba: dentro de la tolerancia de Fácil.
        var onNote = NoteScoring.Evaluate(NoteKind.Normal, -5, Sung(-5 + 2), Easy);

        Assert.True(onNote!.Value.Hit);
        Assert.Equal(-5, onNote.Value.DisplayTone);
    }

    [Fact]
    public void A_hit_is_shown_on_the_note_when_the_file_uses_midi_numbers_as_tones()
    {
        // Archivo con origen MIDI directo: la nota es 55 y el tono cantado (C4 = 0) es -5. Misma clase.
        var onNote = NoteScoring.Evaluate(NoteKind.Normal, 55, Sung(-5), new ScoringRules { Difficulty = Difficulty.Hard });

        Assert.True(onNote!.Value.Hit);
        Assert.Equal(55, onNote.Value.DisplayTone);
    }

    [Fact]
    public void A_miss_is_shown_at_the_real_tone_within_six_semitones_of_the_note()
    {
        // Nota en 0; se canta +3 (fuera de tolerancia Fácil = 2): se ve 3 semitonos arriba.
        var above = NoteScoring.Evaluate(NoteKind.Normal, 0, Sung(3), Easy)!.Value;
        // Se canta 9 abajo: equivale a 3 arriba.
        var folded = NoteScoring.Evaluate(NoteKind.Normal, 0, Sung(-9), Easy)!.Value;

        Assert.False(above.Hit);
        Assert.Equal(3, above.DisplayTone);
        Assert.False(folded.Hit);
        Assert.Equal(3, folded.DisplayTone);
    }

    [Fact]
    public void A_miss_far_in_octaves_is_still_shown_near_the_note()
    {
        var shown = NoteScoring.Evaluate(NoteKind.Normal, 60, Sung(-48 + 4), Easy)!.Value;

        Assert.False(shown.Hit);
        Assert.InRange(shown.DisplayTone, 60 - 6, 60 + 6);
    }

    [Fact]
    public void Rap_is_a_hit_shown_on_the_note_with_any_sound()
    {
        var quiet = new PitchSample(TimeSpan.Zero, TimeSpan.FromMilliseconds(43), 0, 0, -30);
        var e = NoteScoring.Evaluate(NoteKind.Rap, 7, quiet, Easy)!.Value;

        Assert.True(e.Hit);
        Assert.Equal(7, e.DisplayTone);
    }

    [Fact]
    public void Nothing_is_shown_without_sound_without_voice_or_on_freestyle()
    {
        var silent = new PitchSample(TimeSpan.Zero, TimeSpan.FromMilliseconds(43), 0, 0, -80);
        var unvoiced = new PitchSample(TimeSpan.Zero, TimeSpan.FromMilliseconds(43), 0, 0, -20);

        Assert.Null(NoteScoring.Evaluate(NoteKind.Normal, 0, silent, Easy));
        Assert.Null(NoteScoring.Evaluate(NoteKind.Normal, 0, unvoiced, Easy));
        Assert.Null(NoteScoring.Evaluate(NoteKind.Rap, 0, silent, Easy));
        Assert.Null(NoteScoring.Evaluate(NoteKind.Freestyle, 0, Sung(0), Easy));
    }
}

public class VoiceResultsTests
{
    private const string Mixed = """
        #TITLE:Mix
        #ARTIST:T
        #MP3:a.mp3
        #BPM:60
        #GAP:0
        : 0 4 0 uno
        * 4 2 7 dos
        R 6 2 0 tres
        F 8 2 0 cuatro
        - 10
        : 12 4 -5 cinco
        : 16 3 12 seis
        E
        """;

    [Fact]
    public void Perfect_singing_shows_every_scored_beat_as_a_hit_on_its_note()
    {
        var song = UltraStarParser.Parse(Mixed);
        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));

        var results = scorer.Results;
        Assert.Equal(15, results.Count);
        Assert.All(results, r => Assert.True(r.Hit));
        Assert.Equal(0, results[0].DisplayTone);
        Assert.Equal(7, results[4].DisplayTone);
        Assert.Equal(results.Select(r => r.Beat).Order(), results.Select(r => r.Beat));
        Assert.DoesNotContain(results, r => r.Kind == NoteKind.Freestyle);
    }

    [Fact]
    public void Off_pitch_singing_shows_misses_at_the_real_tone_while_rap_still_hits()
    {
        var song = UltraStarParser.Parse(Mixed);
        var scorer = SingerSim.Run(song, new ScoringRules { Difficulty = Difficulty.Hard },
            SingerSim.Perfect(song, semitoneOffset: 3));

        var misses = scorer.Results.Where(r => r.Kind is NoteKind.Normal or NoteKind.Golden).ToList();
        Assert.Equal(13, misses.Count);
        Assert.All(misses, r => Assert.False(r.Hit));
        Assert.Equal(3, misses[0].DisplayTone);             // nota 0 cantada +3
        Assert.Equal(7 + 3, misses.First(r => r.Kind == NoteKind.Golden).DisplayTone);
        Assert.All(scorer.Results.Where(r => r.Kind == NoteKind.Rap), r => Assert.True(r.Hit));
    }

    [Fact]
    public void Silence_shows_nothing_and_reset_clears_the_results()
    {
        var song = UltraStarParser.Parse(Mixed);
        var silent = SingerSim.Run(song, new ScoringRules(), _ => (0, -80));
        Assert.Empty(silent.Results);

        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));
        Assert.NotEmpty(scorer.Results);
        scorer.Reset();
        Assert.Empty(scorer.Results);
    }

    [Fact]
    public void Display_does_not_change_the_score()
    {
        var song = UltraStarParser.Parse(Mixed);
        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));

        Assert.Equal(10000, scorer.Snapshot.Total);
        Assert.Equal(scorer.Snapshot.BeatsHit, scorer.Results.Count(r => r.Hit));
    }
}

public class VoiceBarsTests
{
    private static BeatResult R(int beat, int tone, bool hit = true, NoteKind kind = NoteKind.Normal) =>
        new(beat, kind, tone, hit);

    [Fact]
    public void Contiguous_beats_with_the_same_tone_become_one_bar()
    {
        var bars = VoiceBars.Build([R(0, 5), R(1, 5), R(2, 5)]);

        Assert.Equal([new VoiceBar(0, 3, 5, true, false)], bars);
    }

    [Fact]
    public void A_gap_a_tone_change_or_a_result_change_start_a_new_bar()
    {
        var bars = VoiceBars.Build([R(0, 5), R(2, 5), R(3, 6), R(4, 6, hit: false), R(5, 6, hit: false)]);

        Assert.Equal(
            [new VoiceBar(0, 1, 5, true, false), new VoiceBar(2, 1, 5, true, false), new VoiceBar(3, 1, 6, true, false),
             new VoiceBar(4, 2, 6, false, false)],
            bars);
    }

    [Fact]
    public void Rap_and_tone_notes_are_not_merged_even_at_the_same_tone()
    {
        var bars = VoiceBars.Build([R(0, 0), R(1, 0, kind: NoteKind.Rap)]);

        Assert.Equal(2, bars.Count);
        Assert.True(bars[1].Rap);
    }

    [Fact]
    public void A_beat_repeated_by_overlapping_notes_is_shown_once()
    {
        var bars = VoiceBars.Build([R(0, 5), R(1, 5), R(1, 5), R(2, 5)]);

        Assert.Equal([new VoiceBar(0, 3, 5, true, false)], bars);
    }

    [Fact]
    public void Only_beats_from_the_visible_window_are_included()
    {
        var bars = VoiceBars.Build([R(0, 5), R(1, 5), R(10, 7), R(11, 7)], fromBeat: 10);

        Assert.Equal([new VoiceBar(10, 2, 7, true, false)], bars);
    }
}

public class VoicePreviewTests
{
    private const string OneNote = """
        #TITLE:P
        #ARTIST:T
        #MP3:a.mp3
        #BPM:60
        #GAP:0
        : 0 16 0 la
        E
        """;

    private static (VoiceScorer Scorer, PitchTimeline Timeline, Song Song) Setup(ScoringRules? rules = null)
    {
        var song = UltraStarParser.Parse(OneNote);
        var scorer = new VoiceScorer(song.Voices[0], new SongTiming(song.Bpm, song.GapMilliseconds),
            rules ?? new ScoringRules { Difficulty = Difficulty.Hard });
        return (scorer, new PitchTimeline(), song);
    }

    private static void Feed(PitchTimeline timeline, double from, double to, double hz)
    {
        for (var c = from; c <= to; c += SingerSim.Hop)
        {
            timeline.Add(new PitchSample(TimeSpan.FromSeconds(c), TimeSpan.FromSeconds(SingerSim.Window), hz, hz > 0 ? 0.9 : 0, -20));
        }
    }

    [Fact]
    public void A_beat_shows_up_as_soon_as_its_voice_arrived_without_waiting_for_the_settling_time()
    {
        var (scorer, timeline, _) = Setup();
        Feed(timeline, 0, 0.62, SingerSim.Hertz(0));

        // Posición donde el beat 0 (instante 0,125 s) todavía no se evaluaría: instante + asentamiento = 0,225 s.
        var position = TimeSpan.FromSeconds(0.2);
        scorer.Advance(position, timeline);
        var preview = scorer.Preview(timeline, position);

        Assert.Equal(0, scorer.Snapshot.BeatsEvaluated);
        Assert.NotEmpty(preview);
        Assert.Equal(0, preview[0].Beat);
        Assert.True(preview[0].Hit);
    }

    [Fact]
    public void Preview_does_not_change_the_score_or_the_results()
    {
        var (scorer, timeline, _) = Setup();
        Feed(timeline, 0, 1.0, SingerSim.Hertz(0));
        var position = TimeSpan.FromSeconds(0.8);
        scorer.Advance(position, timeline);
        var before = scorer.Snapshot;
        var resultsBefore = scorer.Results.Count;

        for (var i = 0; i < 5; i++)
        {
            scorer.Preview(timeline, position);
        }

        Assert.Equal(before, scorer.Snapshot);
        Assert.Equal(resultsBefore, scorer.Results.Count);
    }

    [Fact]
    public void Preview_only_covers_beats_not_yet_evaluated_and_agrees_with_the_final_result()
    {
        var (scorer, timeline, _) = Setup();
        Feed(timeline, 0, 3.0, SingerSim.Hertz(0));

        var early = TimeSpan.FromSeconds(0.9);
        scorer.Advance(early, timeline);
        var preview = scorer.Preview(timeline, early);
        var evaluatedBefore = scorer.Results.Select(r => r.Beat).ToHashSet();
        Assert.DoesNotContain(preview, r => evaluatedBefore.Contains(r.Beat));

        // Más adelante esos beats se evalúan y coinciden con lo que se había adelantado.
        scorer.Advance(TimeSpan.FromSeconds(3.0), timeline);
        foreach (var provisional in preview)
        {
            var final = scorer.Results.First(r => r.Beat == provisional.Beat);
            Assert.Equal(final.Hit, provisional.Hit);
            Assert.Equal(final.DisplayTone, provisional.DisplayTone);
        }
    }

    [Fact]
    public void Preview_is_empty_when_no_voice_has_arrived()
    {
        var (scorer, timeline, _) = Setup();

        Assert.Empty(scorer.Preview(timeline, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Preview_waits_until_the_analysis_around_the_instant_is_complete()
    {
        var (scorer, timeline, _) = Setup();
        // Solo llegó la ventana centrada en 0,10 s; el beat 0 se mide en 0,125 s: todavía puede llegar una más cercana.
        Feed(timeline, 0.10, 0.10, SingerSim.Hertz(0));

        Assert.Empty(scorer.Preview(timeline, TimeSpan.FromSeconds(0.2)));
    }

    [Fact]
    public void Preview_uses_the_voice_delay_like_the_score_does()
    {
        var rules = new ScoringRules { Difficulty = Difficulty.Hard, AcousticLatency = TimeSpan.FromMilliseconds(200) };
        var (scorer, timeline, _) = Setup(rules);
        // La voz llega 200 ms tarde: la que corresponde al beat 0 (0,125 s) es la de 0,325 s.
        Feed(timeline, 0.30, 0.40, SingerSim.Hertz(0));

        var preview = scorer.Preview(timeline, TimeSpan.FromSeconds(0.45));

        Assert.NotEmpty(preview);
        Assert.Equal(0, preview[0].Beat);
        Assert.True(preview[0].Hit);
    }
}
