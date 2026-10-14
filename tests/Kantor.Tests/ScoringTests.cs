// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Pitch;
using Kantor.Core.Scoring;
using Kantor.Core.Songs;
using Kantor.Core.Timing;

namespace Kantor.Tests;

/// <summary>Un cantante simulado: genera el historial de tono como lo haría la captura, con retardo de llegada.</summary>
internal static class SingerSim
{
    public const double Hop = 0.02133;          // 1024 muestras a 48 kHz
    public const double Window = 0.04267;       // 2048 muestras a 48 kHz
    public const double ArrivalLag = 0.045;     // una muestra llega ~45 ms después de su centro (medido)

    public static double Hertz(double ultraStarTone) => 440.0 * Math.Pow(2, (ultraStarTone + 60 - 69) / 12.0);

    /// <summary>Qué canta en cada instante: frecuencia (0 = sin tono) y nivel en dB; null = no llega ninguna muestra.</summary>
    public delegate (double Frequency, double LevelDb)? Voice(double seconds);

    public static Voice Perfect(Song song, double semitoneOffset = 0, double delay = 0)
    {
        var timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        var notes = song.Voices[0].Lines.SelectMany(l => l.Notes).ToList();
        return seconds =>
        {
            var beat = timing.BeatAt(TimeSpan.FromSeconds(seconds - delay));
            var note = notes.FirstOrDefault(n => beat >= n.StartBeat && beat < n.EndBeat);
            return note.Length == 0 || note.Kind == NoteKind.Freestyle
                ? (0, -60)
                : (note.Kind is NoteKind.Rap or NoteKind.RapGolden
                    ? (0.0, -20)                       // rap: ruido hablado, sin tono estable
                    : (Hertz(note.Tone + semitoneOffset), -20));
        };
    }

    /// <summary>Alimenta el historial y puntúa avanzando el reloj en pasos de <paramref name="step"/> segundos.</summary>
    public static VoiceScorer Run(Song song, ScoringRules rules, Voice singer, double step = 0.0167, double? until = null,
        PitchTimeline? timeline = null)
    {
        var scorer = new VoiceScorer(song.Voices[0], new SongTiming(song.Bpm, song.GapMilliseconds), rules);
        Drive(scorer, singer, until ?? SongSeconds(song) + 1.0, step, timeline ?? new PitchTimeline());
        return scorer;
    }

    public static void Drive(VoiceScorer scorer, Voice singer, double until, double step, PitchTimeline timeline,
        double from = 0, double nextCenter = 0)
    {
        var center = nextCenter;
        for (var t = from; t <= until; t += step)
        {
            while (center <= t - ArrivalLag)
            {
                if (singer(center) is { } s)
                {
                    timeline.Add(new PitchSample(TimeSpan.FromSeconds(center), TimeSpan.FromSeconds(Window),
                        s.Frequency, s.Frequency > 0 ? 0.9 : 0, s.LevelDb));
                }

                center += Hop;
            }

            scorer.Advance(TimeSpan.FromSeconds(t), timeline);
        }
    }

    public static double SongSeconds(Song song)
    {
        var timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        var end = song.Voices[0].Lines.Max(l => l.EndBeat);
        return timing.TimeOf(end).TotalSeconds;
    }
}

public class ScoringTests
{
    // 60 BPM de archivo: un beat dura 0,25 s. Valor: línea 1 = 4 + 2*2 + 2 = 10; línea 2 = 4 + 3 = 7 (freestyle no cuenta).
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

    private static Song Parse(string text = Mixed) => UltraStarParser.Parse(text);

    private static ScoringRules Rules(Difficulty d = Difficulty.Easy, bool bonus = true) =>
        new() { Difficulty = d, LineBonus = bonus };

    [Fact]
    public void Singing_perfectly_scores_exactly_10000_with_line_bonus()
    {
        var song = Parse();
        var scorer = SingerSim.Run(song, Rules(), SingerSim.Perfect(song));

        var s = scorer.Snapshot;
        Assert.Equal(10000, s.Total);
        Assert.Equal(15, s.BeatsHit);
        Assert.Equal(15, s.BeatsTotal);
        Assert.Equal(2, s.LinesCompleted);
        Assert.Equal(1000, s.LinesRounded);
        Assert.Equal(9000, s.NotesRounded + s.GoldenRounded);
    }

    [Fact]
    public void Singing_perfectly_scores_exactly_10000_without_line_bonus()
    {
        var song = Parse();
        var s = SingerSim.Run(song, Rules(bonus: false), SingerSim.Perfect(song)).Snapshot;

        Assert.Equal(10000, s.Total);
        Assert.Equal(0, s.LinesRounded);
    }

    [Fact]
    public void Silence_scores_zero()
    {
        var song = Parse();
        var s = SingerSim.Run(song, Rules(), _ => (0, -80)).Snapshot;

        Assert.Equal(0, s.Total);
        Assert.Equal(15, s.BeatsEvaluated);
    }

    [Theory]
    [InlineData(Difficulty.Easy, 1, true)]
    [InlineData(Difficulty.Easy, 2, true)]
    [InlineData(Difficulty.Easy, 3, false)]
    [InlineData(Difficulty.Medium, 1, true)]
    [InlineData(Difficulty.Medium, 2, false)]
    [InlineData(Difficulty.Hard, 0, true)]
    [InlineData(Difficulty.Hard, 1, false)]
    [InlineData(Difficulty.Easy, -2, true)]
    [InlineData(Difficulty.Medium, -2, false)]
    public void Difficulty_sets_the_tolerance_in_semitones(Difficulty difficulty, int error, bool scores)
    {
        var song = Parse();
        var s = SingerSim.Run(song, Rules(difficulty), SingerSim.Perfect(song, semitoneOffset: error)).Snapshot;

        // El rap siempre acierta (2 beats); el resto solo si el error entra en la tolerancia.
        Assert.Equal(scores ? 15 : 2, s.BeatsHit);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(-12)]
    [InlineData(36)]
    [InlineData(-24)]
    public void Notes_are_equivalent_across_octaves(int octaves)
    {
        var song = Parse();
        var s = SingerSim.Run(song, Rules(Difficulty.Hard), SingerSim.Perfect(song, semitoneOffset: octaves)).Snapshot;

        Assert.Equal(10000, s.Total);
    }

    [Fact]
    public void Pitch_class_distance_is_circular()
    {
        Assert.Equal(0, NoteScoring.PitchClassDistance(0, 12));
        Assert.Equal(1, NoteScoring.PitchClassDistance(11, 0));
        Assert.Equal(6, NoteScoring.PitchClassDistance(0, 6));
        Assert.Equal(6, NoteScoring.PitchClassDistance(-6, 0));
        Assert.Equal(2, NoteScoring.PitchClassDistance(-1, 1));
        Assert.Equal(1, NoteScoring.PitchClassDistance(-13, 0));
    }

    [Fact]
    public void Rap_only_needs_sound_above_the_gate_and_ignores_pitch()
    {
        const string rap = """
            #TITLE:R
            #ARTIST:T
            #MP3:a.mp3
            #BPM:60
            #GAP:0
            R 0 4 0 rap
            E
            """;
        var song = Parse(rap);

        var loud = SingerSim.Run(song, Rules(), _ => (0, -30)).Snapshot;
        var quiet = SingerSim.Run(song, Rules(), _ => (0, -60)).Snapshot;
        var lowGate = SingerSim.Run(song, Rules() with { LevelGateDb = -70 }, _ => (0, -60)).Snapshot;

        Assert.Equal(4, loud.BeatsHit);
        Assert.Equal(0, quiet.BeatsHit);
        Assert.Equal(4, lowGate.BeatsHit);
    }

    [Fact]
    public void Tone_notes_need_voice_and_level_above_the_gate()
    {
        const string one = """
            #TITLE:N
            #ARTIST:T
            #MP3:a.mp3
            #BPM:60
            #GAP:0
            : 0 4 0 uno
            E
            """;
        var song = Parse(one);

        var voiced = SingerSim.Run(song, Rules(), _ => (SingerSim.Hertz(0), -30)).Snapshot;
        var noVoice = SingerSim.Run(song, Rules(), _ => (0, -30)).Snapshot;
        var tooQuiet = SingerSim.Run(song, Rules() with { LevelGateDb = -20 }, _ => (SingerSim.Hertz(0), -30)).Snapshot;

        Assert.Equal(4, voiced.BeatsHit);
        Assert.Equal(0, noVoice.BeatsHit);
        Assert.Equal(0, tooQuiet.BeatsHit);
    }

    [Fact]
    public void Golden_beats_are_worth_twice_and_go_in_their_own_bucket()
    {
        const string text = """
            #TITLE:G
            #ARTIST:T
            #MP3:a.mp3
            #BPM:60
            #GAP:0
            : 0 2 0 a
            * 2 2 0 b
            E
            """;
        var song = Parse(text);
        // Valor = 2 + 2*2 = 6. Solo la normal: 1/3 de lo de notas; solo la dorada: 2/3.
        var timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        SingerSim.Voice onlyNormal = t => timing.BeatAt(TimeSpan.FromSeconds(t)) < 2 ? (SingerSim.Hertz(0), -20) : (0, -60);
        SingerSim.Voice onlyGolden = t => timing.BeatAt(TimeSpan.FromSeconds(t)) >= 2 ? (SingerSim.Hertz(0), -20) : (0, -60);

        var n = SingerSim.Run(song, Rules(bonus: false), onlyNormal).Snapshot;
        var g = SingerSim.Run(song, Rules(bonus: false), onlyGolden).Snapshot;

        Assert.Equal(10000m / 3, n.Notes, 6);
        Assert.Equal(0m, n.Golden);
        Assert.Equal(20000m / 3, g.Golden, 6);
        Assert.Equal(0m, g.Notes);
        Assert.Equal(3330, n.Total);
        Assert.Equal(6660, g.Total); // sola, la dorada se trunca: el redondeo opuesto compensa solo si hay normales
    }

    [Fact]
    public void Freestyle_is_ignored_and_empty_lines_do_not_split_the_bonus()
    {
        const string text = """
            #TITLE:F
            #ARTIST:T
            #MP3:a.mp3
            #BPM:60
            #GAP:0
            : 0 4 0 a
            - 4
            F 4 4 0 libre
            - 8
            : 8 4 0 b
            E
            """;
        var song = Parse(text);
        var s = SingerSim.Run(song, Rules(), SingerSim.Perfect(song)).Snapshot;

        // Dos frases puntuables (la del medio es solo freestyle): bono de 500 cada una.
        Assert.Equal(10000, s.Total);
        Assert.Equal(8, s.BeatsTotal);
        Assert.Equal(2, s.LinesCompleted);
    }

    [Fact]
    public void Line_bonus_scales_with_how_well_each_line_was_sung_and_has_two_points_of_slack()
    {
        var song = Parse();
        var timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        var perfect = SingerSim.Perfect(song);
        // Se canta bien solo la segunda frase (desde el beat 12).
        SingerSim.Voice onlySecond = t => timing.BeatAt(TimeSpan.FromSeconds(t)) >= 12 ? perfect(t) : (0, -80);

        var s = SingerSim.Run(song, Rules(), onlySecond).Snapshot;

        Assert.Equal(500m, s.Lines);
        Assert.Equal(500, s.LinesRounded);
        // Frase 2 vale 9000 * 7/17 = 3705,88 puntos; con 2 de holgura, cantar 3703,88 ya es perfecta.
        var nearlyAll = (9000m * 7 / 17 - 2) / (9000m * 7 / 17);
        Assert.True(nearlyAll > 0.99m);
    }

    [Fact]
    public void Rounding_never_exceeds_10000_for_any_set_of_hits()
    {
        // Muchas canciones sintéticas y subconjuntos de aciertos: el total redondeado nunca pasa del máximo.
        var rng = new Random(12345);
        for (var iteration = 0; iteration < 150; iteration++)
        {
            var lines = new List<string>();
            var beat = 0;
            for (var l = 0; l < rng.Next(1, 8); l++)
            {
                for (var n = 0; n < rng.Next(1, 6); n++)
                {
                    var length = rng.Next(1, 9);
                    var kind = new[] { ":", "*", "R", "G", "F" }[rng.Next(5)];
                    lines.Add($"{kind} {beat} {length} {rng.Next(-10, 25)} x");
                    beat += length + rng.Next(0, 3);
                }

                lines.Add($"- {beat}");
            }

            var text = $"#TITLE:r\n#ARTIST:r\n#MP3:a.mp3\n#BPM:{rng.Next(80, 400)}\n#GAP:0\n" + string.Join('\n', lines) + "\nE";
            var song = Parse(text);
            var perfect = SingerSim.Perfect(song);
            var hitProbability = rng.NextDouble();
            var cache = new Dictionary<int, bool>();
            SingerSim.Voice singer = t =>
            {
                var slot = (int)(t / 0.05);
                if (!cache.TryGetValue(slot, out var ok))
                {
                    ok = rng.NextDouble() < hitProbability;
                    cache[slot] = ok;
                }

                return ok ? perfect(t) : (0, -80);
            };

            foreach (var bonus in new[] { true, false })
            {
                var total = SingerSim.Run(song, Rules(Difficulty.Medium, bonus), singer, step: 0.05).Snapshot.Total;
                Assert.InRange(total, 0, 10000);
            }

            Assert.Equal(10000, SingerSim.Run(song, Rules(Difficulty.Hard), perfect, step: 0.05).Snapshot.Total);
        }
    }

    [Fact]
    public void Result_does_not_depend_on_how_often_the_clock_is_advanced()
    {
        var song = Parse();
        var perfect = SingerSim.Perfect(song);
        // Un cantante con huecos y desafinaciones, determinista.
        SingerSim.Voice singer = t => (int)(t * 10) % 7 == 0 ? (0, -80) : perfect(t + ((int)(t * 4) % 3 == 0 ? 0.0 : 0.0));

        var reference = SingerSim.Run(song, Rules(), singer, step: 0.001).Snapshot;
        foreach (var step in new[] { 0.0167, 0.033, 0.1, 0.25, 1.0 })
        {
            Assert.Equal(reference, SingerSim.Run(song, Rules(), singer, step: step).Snapshot);
        }
    }

    [Fact]
    public void A_beat_is_not_evaluated_until_its_settling_time_has_passed()
    {
        var song = Parse();
        var timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        var scorer = new VoiceScorer(song.Voices[0], timing, Rules());
        var timeline = new PitchTimeline();
        var settling = scorer.Rules.Settling;

        // El beat 0 cubre 0–0,25 s; su instante es 0,125 s.
        scorer.Advance(TimeSpan.FromSeconds(0.125) + settling - TimeSpan.FromMilliseconds(1), timeline);
        Assert.Equal(0, scorer.Snapshot.BeatsEvaluated);

        scorer.Advance(TimeSpan.FromSeconds(0.125) + settling, timeline);
        Assert.Equal(1, scorer.Snapshot.BeatsEvaluated);
    }

    [Fact]
    public void A_sample_arriving_after_its_beat_was_evaluated_is_ignored()
    {
        var song = Parse();
        var timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        var scorer = new VoiceScorer(song.Voices[0], timing, Rules());
        var timeline = new PitchTimeline();

        scorer.Advance(TimeSpan.FromSeconds(0.5), timeline); // beats 0 y 1 evaluados sin muestras
        timeline.Add(new PitchSample(TimeSpan.FromSeconds(0.125), TimeSpan.FromSeconds(0.043), SingerSim.Hertz(0), 0.9, -20));
        scorer.Advance(TimeSpan.FromSeconds(0.6), timeline);

        Assert.Equal(0, scorer.Snapshot.BeatsHit);
    }

    [Fact]
    public void Missing_samples_are_a_miss_not_the_last_known_value()
    {
        var song = Parse();
        var perfect = SingerSim.Perfect(song);
        // La captura no entrega nada entre 1,0 s y 2,0 s (beats 4 a 7).
        SingerSim.Voice withGap = t => t is >= 1.0 and < 2.0 ? null : perfect(t);

        var s = SingerSim.Run(song, Rules(), withGap).Snapshot;

        // Los beats 4 a 7 (instantes 1,125 a 1,875 s) quedan sin ventana que los cubra: 4 de los 15 se pierden.
        Assert.Equal(15 - 4, s.BeatsHit);
    }

    [Fact]
    public void Latency_is_a_parameter_that_shifts_which_instant_of_the_voice_is_compared()
    {
        var song = Parse();
        // El jugador canta 0,5 s después de lo que suena (dos beats).
        var late = SingerSim.Perfect(song, delay: 0.5);

        var withoutLatency = SingerSim.Run(song, Rules(Difficulty.Hard), late).Snapshot;
        var withLatency = SingerSim.Run(song, Rules(Difficulty.Hard) with { AcousticLatency = TimeSpan.FromSeconds(0.5) }, late).Snapshot;

        Assert.True(withoutLatency.Total < 10000);
        Assert.Equal(10000, withLatency.Total);
    }

    [Fact]
    public void Pausing_keeps_the_score_and_resuming_continues_it()
    {
        var song = Parse();
        var timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        var scorer = new VoiceScorer(song.Voices[0], timing, Rules());
        var timeline = new PitchTimeline();
        var singer = SingerSim.Perfect(song);

        SingerSim.Drive(scorer, singer, until: 2.0, step: 0.0167, timeline);
        var atPause = scorer.Snapshot;
        Assert.True(atPause.BeatsHit > 0);

        // Pausa: la posición queda quieta y se sigue consultando; nada cambia.
        for (var i = 0; i < 100; i++)
        {
            scorer.Advance(TimeSpan.FromSeconds(2.0), timeline);
        }

        Assert.Equal(atPause, scorer.Snapshot);

        // Se reanuda desde la misma posición y se completa.
        SingerSim.Drive(scorer, singer, until: SingerSim.SongSeconds(song) + 1, step: 0.0167, timeline, from: 2.0,
            nextCenter: Math.Ceiling(2.0 / SingerSim.Hop) * SingerSim.Hop - SingerSim.ArrivalLag * 0);
        Assert.True(scorer.Snapshot.BeatsHit >= atPause.BeatsHit);
    }

    [Fact]
    public void Reset_returns_to_zero_and_the_song_can_be_scored_again()
    {
        var song = Parse();
        var timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        var scorer = new VoiceScorer(song.Voices[0], timing, Rules());
        var singer = SingerSim.Perfect(song);

        SingerSim.Drive(scorer, singer, SingerSim.SongSeconds(song) + 1, 0.0167, new PitchTimeline());
        Assert.Equal(10000, scorer.Snapshot.Total);

        scorer.Reset();
        Assert.Equal(0, scorer.Snapshot.Total);
        Assert.Equal(0, scorer.Snapshot.BeatsEvaluated);

        SingerSim.Drive(scorer, singer, SingerSim.SongSeconds(song) + 1, 0.0167, new PitchTimeline());
        Assert.Equal(10000, scorer.Snapshot.Total);
    }

    [Fact]
    public void A_large_rewind_restarts_the_score_and_a_small_one_is_ignored()
    {
        var song = Parse();
        var timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        var scorer = new VoiceScorer(song.Voices[0], timing, Rules());
        var timeline = new PitchTimeline();
        var singer = SingerSim.Perfect(song);

        SingerSim.Drive(scorer, singer, until: 3.0, step: 0.0167, timeline);
        var before = scorer.Snapshot;
        Assert.True(before.BeatsEvaluated > 0);

        scorer.Advance(TimeSpan.FromSeconds(3.0 - 0.1), timeline); // retroceso chico: se ignora
        Assert.Equal(before, scorer.Snapshot);

        scorer.Advance(TimeSpan.FromSeconds(0.2), timeline);        // vuelve a empezar
        Assert.Equal(0, scorer.Snapshot.BeatsEvaluated);
        Assert.Equal(0, scorer.Snapshot.Total);
    }

    [Fact]
    public void A_forward_jump_evaluates_the_skipped_beats_with_whatever_history_exists()
    {
        var song = Parse();
        var timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        var scorer = new VoiceScorer(song.Voices[0], timing, Rules());

        scorer.Advance(TimeSpan.FromSeconds(100), new PitchTimeline());

        Assert.Equal(15, scorer.Snapshot.BeatsEvaluated);
        Assert.Equal(0, scorer.Snapshot.Total);
        Assert.Equal(2, scorer.Snapshot.LinesCompleted);
    }

    [Fact]
    public void A_voice_without_scored_notes_scores_zero_without_dividing_by_zero()
    {
        const string text = """
            #TITLE:F
            #ARTIST:T
            #MP3:a.mp3
            #BPM:60
            #GAP:0
            F 0 4 0 libre
            E
            """;
        var song = Parse(text);
        var s = SingerSim.Run(song, Rules(), SingerSim.Perfect(song)).Snapshot;

        Assert.Equal(0, s.Total);
        Assert.Equal(0, s.BeatsTotal);
    }
}

public class BeatSamplerTests
{
    private static PitchSample At(double center, double window = 0.04, double hz = 220) =>
        new(TimeSpan.FromSeconds(center), TimeSpan.FromSeconds(window), hz, 0.9, -20);

    private static TimeSpan T(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Picks_the_window_whose_center_is_nearest()
    {
        // Ventanas solapadas: centros 1,000 y 1,020, ambas cubren 1,012.
        var chosen = BeatSampler.Select(T(1.012), [At(1.000), At(1.020)]);

        Assert.Equal(T(1.020), chosen!.Value.Time);
    }

    [Fact]
    public void Ties_go_to_the_earlier_sample_regardless_of_order()
    {
        var a = BeatSampler.Select(T(1.010), [At(1.000), At(1.020)]);
        var b = BeatSampler.Select(T(1.010), [At(1.020), At(1.000)]);

        Assert.Equal(T(1.000), a!.Value.Time);
        Assert.Equal(T(1.000), b!.Value.Time);
    }

    [Fact]
    public void Window_edges_are_inclusive()
    {
        // La ventana de centro 1,000 y largo 0,040 cubre [0,980; 1,020].
        Assert.NotNull(BeatSampler.Select(T(1.020), [At(1.000)]));
        Assert.NotNull(BeatSampler.Select(T(0.980), [At(1.000)]));
        Assert.Null(BeatSampler.Select(T(1.021), [At(1.000)]));
        Assert.Null(BeatSampler.Select(T(0.979), [At(1.000)]));
    }

    [Fact]
    public void A_hole_between_windows_has_no_sample_even_if_neighbors_exist()
    {
        // Centros 1,000 y 1,100 con ventanas de 40 ms: nada cubre 1,050.
        Assert.Null(BeatSampler.Select(T(1.050), [At(1.000), At(1.100)]));
    }

    [Fact]
    public void Looks_up_a_timeline_and_ignores_samples_far_from_the_instant()
    {
        var timeline = new PitchTimeline();
        timeline.Add(At(0.5));
        timeline.Add(At(5.0));
        var scratch = new List<PitchSample>();

        Assert.Equal(T(5.0), BeatSampler.Select(T(5.01), timeline, scratch)!.Value.Time);
        Assert.Null(BeatSampler.Select(T(2.0), timeline, scratch));
    }
}

public class GapBridgeTests
{
    // Una nota de 16 beats (4 s a 0,25 s por beat) en el tono 0: el beat k se evalúa en (k + 0,5) * 0,25 s.
    private const string OneNote = """
        #TITLE:G
        #ARTIST:T
        #MP3:a.mp3
        #BPM:60
        #GAP:0
        : 0 16 0 la
        E
        """;

    private static Song Song() => UltraStarParser.Parse(OneNote);

    private static SingerSim.Voice WithGap(double from, double to, double level = -20, double? endAt = null) => t =>
        endAt is { } end && t >= end ? (0, level)
        : t >= from && t < to ? (0, level)
        : (SingerSim.Hertz(0), -20);

    private static ScoringRules Rules(TimeSpan? bridge = null) =>
        new() { Difficulty = Difficulty.Hard, GapBridge = bridge ?? TimeSpan.FromMilliseconds(150) };

    [Fact]
    public void A_short_gap_with_sound_inside_a_phrase_is_bridged()
    {
        // Hueco de 90 ms centrado en el instante del beat 4 (1,125 s).
        var singer = WithGap(1.08, 1.17);

        var bridged = SingerSim.Run(Song(), Rules(), singer).Snapshot;
        var plain = SingerSim.Run(Song(), Rules(TimeSpan.Zero), singer).Snapshot;

        Assert.Equal(16, bridged.BeatsHit);
        Assert.True(plain.BeatsHit < 16);
    }

    [Fact]
    public void A_gap_longer_than_the_limit_is_not_bridged()
    {
        var s = SingerSim.Run(Song(), Rules(), WithGap(0.93, 1.33)).Snapshot;

        Assert.True(s.BeatsHit < 16);
    }

    [Fact]
    public void A_gap_without_sound_is_silence_not_a_bridge()
    {
        var s = SingerSim.Run(Song(), Rules(), WithGap(1.08, 1.17, level: -60)).Snapshot;

        Assert.True(s.BeatsHit < 16);
    }

    [Fact]
    public void The_end_of_a_note_is_not_extended()
    {
        // Voz hasta 2,0 s y después solo "sonido sin tono" hasta el final: sin tono posterior no hay puente.
        var s = SingerSim.Run(Song(), Rules(), WithGap(0, 0, endAt: 2.0)).Snapshot;

        Assert.Equal(8, s.BeatsHit);
    }

    [Fact]
    public void Beats_in_a_gap_wait_for_the_following_tone_but_others_do_not()
    {
        var song = Song();
        var timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        var rules = Rules();
        var scorer = new VoiceScorer(song.Voices[0], timing, rules);
        var timeline = new PitchTimeline();
        var singer = WithGap(1.08, 1.25);

        // Justo cuando un beat común ya se evaluaría (instante + asentamiento) el tono posterior aún no llegó.
        SingerSim.Drive(scorer, singer, until: 1.125 + rules.Settling.TotalSeconds + 0.02, step: 0.0167, timeline);

        // Los beats 0 a 3 (con tono) ya están evaluados; el 4 (en el hueco) espera al tono posterior.
        Assert.Equal(4, scorer.Snapshot.BeatsEvaluated);

        // Pasado el límite de espera (instante + puente + asentamiento) se decide y avanza.
        SingerSim.Drive(scorer, singer, until: 1.125 + rules.GapBridge.TotalSeconds + rules.Settling.TotalSeconds + 0.1,
            step: 0.0167, timeline, from: 1.125 + rules.Settling.TotalSeconds + 0.02 + 0.0167,
            nextCenter: Math.Ceiling((1.125 + rules.Settling.TotalSeconds + 0.02 - SingerSim.ArrivalLag) / SingerSim.Hop) * SingerSim.Hop);
        Assert.True(scorer.Snapshot.BeatsEvaluated > 4);
    }

    [Fact]
    public void Bridging_does_not_depend_on_how_often_the_clock_is_advanced()
    {
        var song = Song();
        var singer = WithGap(1.08, 1.17);
        var reference = SingerSim.Run(song, Rules(), singer, step: 0.001).Snapshot;

        foreach (var step in new[] { 0.0167, 0.05, 0.25, 2.0 })
        {
            Assert.Equal(reference, SingerSim.Run(song, Rules(), singer, step: step).Snapshot);
        }
    }

    [Fact]
    public void Bridge_picks_the_nearest_voiced_neighbor_and_prefers_the_earlier_on_a_tie()
    {
        PitchSample S(double t, double hz) => new(TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(0.04), hz, 0.9, -20);
        var candidates = new List<PitchSample> { S(1.00, 220), S(1.03, 0), S(1.06, 0), S(1.09, 330) };
        var max = TimeSpan.FromMilliseconds(150);

        var nearBefore = BeatSampler.Bridge(TimeSpan.FromSeconds(1.04), candidates, -50, max, out _);
        var nearAfter = BeatSampler.Bridge(TimeSpan.FromSeconds(1.07), candidates, -50, max, out _);
        var tie = BeatSampler.Bridge(TimeSpan.FromSeconds(1.045), candidates, -50, max, out _);

        Assert.Equal(220, nearBefore!.Value.Frequency);
        Assert.Equal(330, nearAfter!.Value.Frequency);
        Assert.Equal(220, tie!.Value.Frequency);
    }
}
