// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Feedback;
using Kantor.Core.Pitch;
using Kantor.Core.Scoring;
using Kantor.Core.Songs;
using Kantor.Core.Timing;
using Xunit;

namespace Kantor.Tests;

public class PhraseFeedbackTests
{
    // 60 BPM de archivo: un beat dura 0,25 s. Línea 1 termina en 2,0 s; línea 2, en 4,75 s.
    private const string TwoLines = """
        #TITLE:Dos
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

    private static Song Parse(string text = TwoLines) => UltraStarParser.Parse(text);

    private static LineResult Line(double fraction, int sequence = 0, double endSeconds = 1) =>
        new(0, sequence, fraction, TimeSpan.FromSeconds(endSeconds));

    // ---- Clasificación ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(100, PhraseRating.Perfect)]
    [InlineData(98, PhraseRating.Perfect)]
    [InlineData(97.9, PhraseRating.Excellent)]
    [InlineData(90, PhraseRating.Excellent)]
    [InlineData(89.9, PhraseRating.VeryGood)]
    [InlineData(80, PhraseRating.VeryGood)]
    [InlineData(79.9, PhraseRating.Good)]
    [InlineData(65, PhraseRating.Good)]
    [InlineData(64.9, PhraseRating.Acceptable)]
    [InlineData(50, PhraseRating.Acceptable)]
    [InlineData(49.9, PhraseRating.Poor)]
    [InlineData(30, PhraseRating.Poor)]
    [InlineData(29.9, PhraseRating.Bad)]
    [InlineData(10, PhraseRating.Bad)]
    [InlineData(9.9, PhraseRating.Disaster)]
    [InlineData(0, PhraseRating.Disaster)]
    public void Ranges_include_their_lower_limit(double percent, PhraseRating expected) =>
        Assert.Equal(expected, PhraseRatings.Classify(percent / 100));

    [Fact]
    public void Ratio_of_integer_weights_lands_on_the_right_side_of_a_limit()
    {
        Assert.Equal(PhraseRating.Perfect, PhraseRatings.Classify(49.0 / 50.0));
        Assert.Equal(PhraseRating.Good, PhraseRatings.Classify(13.0 / 20.0));
        Assert.Equal(PhraseRating.Excellent, PhraseRatings.Classify(9.0 / 10.0));
        Assert.Equal(PhraseRating.Perfect, PhraseRatings.Classify(1.5));
        Assert.Equal(PhraseRating.Disaster, PhraseRatings.Classify(-1));
    }

    // ---- Porcentaje por frase (scoring real) -------------------------------------------------------------------

    [Fact]
    public void Perfect_singing_closes_every_scored_line_at_100_percent_and_does_not_change_the_score()
    {
        var song = Parse();
        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));

        Assert.Equal(10000, scorer.Snapshot.Total);
        Assert.Collection(scorer.LineResults,
            first =>
            {
                Assert.Equal(0, first.Line);
                Assert.Equal(1.0, first.Fraction);
                Assert.Equal(TimeSpan.FromSeconds(2.0), first.EndTime);
            },
            second =>
            {
                Assert.Equal(1, second.Line);
                Assert.Equal(1.0, second.Fraction);
                Assert.Equal(TimeSpan.FromSeconds(4.75), second.EndTime);
            });
    }

    [Fact]
    public void Silence_closes_lines_at_zero()
    {
        var song = Parse();
        var scorer = SingerSim.Run(song, new ScoringRules(), _ => (0, -80));

        Assert.Equal(2, scorer.LineResults.Count);
        Assert.All(scorer.LineResults, r => Assert.Equal(0.0, r.Fraction));
    }

    [Fact]
    public void Fraction_uses_the_same_weights_as_the_score_so_golden_notes_count_double()
    {
        // Normal de 2 beats (peso 2) + dorada de 2 beats (peso 4): cantar solo la normal vale 2/6.
        const string text = """
            #TITLE:T
            #ARTIST:T
            #MP3:a.mp3
            #BPM:60
            #GAP:0
            : 0 2 0 a
            * 2 2 5 b
            E
            """;
        var song = Parse(text);
        var perfect = SingerSim.Perfect(song);
        SingerSim.Voice onlyFirstNote = seconds => seconds < 0.5 + 0.01 ? perfect(seconds) : (0, -80);
        var scorer = SingerSim.Run(song, new ScoringRules(), onlyFirstNote);

        var result = Assert.Single(scorer.LineResults);
        Assert.Equal(2.0 / 6.0, result.Fraction, 3);
    }

    [Fact]
    public void Lines_without_scored_notes_produce_no_result()
    {
        const string text = """
            #TITLE:T
            #ARTIST:T
            #MP3:a.mp3
            #BPM:60
            #GAP:0
            : 0 4 0 a
            - 6
            F 8 4 0 libre
            - 14
            : 16 4 0 b
            E
            """;
        var song = Parse(text);
        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));

        Assert.Equal(2, scorer.LineResults.Count);
        Assert.Equal([0, 2], scorer.LineResults.Select(r => r.Line));
    }

    [Fact]
    public void Reset_clears_line_results_and_changes_generation()
    {
        var song = Parse();
        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));
        var generation = scorer.Generation;

        scorer.Reset();

        Assert.Empty(scorer.LineResults);
        Assert.NotEqual(generation, scorer.Generation);
    }

    // ---- Catálogo ---------------------------------------------------------------------------------------------

    [Fact]
    public void Every_message_has_at_most_eight_words_and_lists_have_no_duplicates()
    {
        var catalog = PhraseFeedbackCatalog.Spanish;
        var lists = catalog.ByRating.Values.Concat([catalog.Streak, catalog.Recovery, catalog.Duel]).ToList();

        foreach (var list in lists)
        {
            Assert.Equal(list.Count, list.Distinct().Count());
            foreach (var message in list)
            {
                var words = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.InRange(words.Length, 1, 8);
            }
        }

        foreach (var rating in Enum.GetValues<PhraseRating>())
        {
            Assert.True(catalog.ByRating[rating].Count >= 8, rating.ToString());
        }
    }

    // ---- Selección --------------------------------------------------------------------------------------------

    private static PhraseFeedbackEngine Engine(int seed = 1) => new(new Random(seed));

    private static PhraseFeedback One(PhraseFeedbackEngine engine, int player, double fraction, int sequence = 0) =>
        Assert.Single(engine.Evaluate([new LineEvent(player, Line(fraction, sequence))], TimeSpan.FromSeconds(1)));

    [Theory]
    [InlineData(1.0, PhraseRating.Perfect)]
    [InlineData(0.92, PhraseRating.Excellent)]
    [InlineData(0.85, PhraseRating.VeryGood)]
    [InlineData(0.70, PhraseRating.Good)]
    [InlineData(0.55, PhraseRating.Acceptable)]
    [InlineData(0.40, PhraseRating.Poor)]
    [InlineData(0.20, PhraseRating.Bad)]
    [InlineData(0.0, PhraseRating.Disaster)]
    public void The_message_comes_from_the_level_of_the_phrase(double fraction, PhraseRating level)
    {
        var message = One(Engine(), 1, fraction);

        Assert.Equal(level, message.Rating);
        Assert.Contains(message.Text, PhraseFeedbackCatalog.Spanish.ByRating[level]);
        Assert.Equal(FeedbackKind.Rating, message.Kind);
    }

    [Fact]
    public void A_player_never_repeats_one_of_their_last_four_messages()
    {
        var engine = Engine(7);
        var texts = Enumerable.Range(0, 300).Select(i => One(engine, 1, 0.85, i).Text).ToList();

        // 85 % no dispara racha ni recuperación solo si se alterna; acá todas son buenas, así que se filtran las especiales.
        var rated = texts.Where(t => PhraseFeedbackCatalog.Spanish.ByRating[PhraseRating.VeryGood].Contains(t)).ToList();
        Assert.True(rated.Count > 150);

        for (var i = 0; i < texts.Count; i++)
        {
            for (var back = 1; back <= PhraseFeedbackEngine.HistorySize && i - back >= 0; back++)
            {
                if (PhraseFeedbackCatalog.Spanish.ByRating[PhraseRating.VeryGood].Contains(texts[i]) &&
                    PhraseFeedbackCatalog.Spanish.ByRating[PhraseRating.VeryGood].Contains(texts[i - back]))
                {
                    Assert.NotEqual(texts[i - back], texts[i]);
                }
            }
        }
    }

    [Fact]
    public void Consecutive_messages_differ_even_with_a_tiny_pool()
    {
        var catalog = new PhraseFeedbackCatalog
        {
            ByRating = Enum.GetValues<PhraseRating>().ToDictionary(r => r, r => (IReadOnlyList<string>)["¡A!", "¡B!"]),
            Streak = ["¡S!"],
            Recovery = ["¡R!"],
            Duel = ["¡D!"],
        };
        var engine = new PhraseFeedbackEngine(new Random(3), catalog);

        string? previous = null;
        for (var i = 0; i < 50; i++)
        {
            var text = One(engine, 1, 0.55, i).Text;
            Assert.NotEqual(previous, text);
            previous = text;
        }
    }

    [Fact]
    public void Players_keep_independent_histories()
    {
        var catalog = new PhraseFeedbackCatalog
        {
            ByRating = Enum.GetValues<PhraseRating>().ToDictionary(r => r,
                r => (IReadOnlyList<string>)["¡A!", "¡B!", "¡C!", "¡D!", "¡E!"]),
            Streak = ["¡S!"],
            Recovery = ["¡R!"],
            Duel = ["¡D!"],
        };

        var secondPlayerRepeatedAFirstPlayerText = false;
        for (var seed = 0; seed < 40 && !secondPlayerRepeatedAFirstPlayerText; seed++)
        {
            var engine = new PhraseFeedbackEngine(new Random(seed), catalog);
            var first = Enumerable.Range(0, 4).Select(i => One(engine, 1, 0.55, i).Text).ToList();
            Assert.Equal(4, first.Distinct().Count());

            // Con historial compartido, el segundo jugador solo podría recibir el único texto que falta.
            secondPlayerRepeatedAFirstPlayerText = first.Contains(One(engine, 2, 0.55).Text);
        }

        Assert.True(secondPlayerRepeatedAFirstPlayerText);
    }

    // ---- Especiales ------------------------------------------------------------------------------------------

    [Fact]
    public void Four_good_phrases_in_a_row_trigger_a_streak_message_only_on_the_fourth()
    {
        var engine = Engine();
        var kinds = Enumerable.Range(0, 4).Select(i => One(engine, 1, 0.85, i).Kind).ToList();

        Assert.Equal([FeedbackKind.Rating, FeedbackKind.Rating, FeedbackKind.Rating, FeedbackKind.Streak], kinds);
    }

    [Fact]
    public void A_perfect_phrase_keeps_its_own_rating_over_the_streak()
    {
        var engine = Engine();
        for (var i = 0; i < 3; i++)
        {
            One(engine, 1, 0.85, i);
        }

        var fourth = One(engine, 1, 1.0, 3);
        Assert.Equal(FeedbackKind.Rating, fourth.Kind);
        Assert.Contains(fourth.Text, PhraseFeedbackCatalog.Spanish.ByRating[PhraseRating.Perfect]);
    }

    [Fact]
    public void A_weak_phrase_breaks_the_streak()
    {
        var engine = Engine();
        One(engine, 1, 0.85, 0);
        One(engine, 1, 0.85, 1);
        One(engine, 1, 0.40, 2);
        var kinds = Enumerable.Range(3, 3).Select(i => One(engine, 1, 0.85, i).Kind).ToList();

        Assert.All(kinds, k => Assert.Equal(FeedbackKind.Rating, k));
    }

    [Fact]
    public void A_good_phrase_after_two_weak_ones_is_a_recovery_but_after_one_it_is_not()
    {
        var engine = Engine();
        One(engine, 1, 0.20, 0);
        One(engine, 1, 0.30, 1);
        var recovery = One(engine, 1, 0.90, 2);
        Assert.Equal(FeedbackKind.Recovery, recovery.Kind);
        Assert.Contains(recovery.Text, PhraseFeedbackCatalog.Spanish.Recovery);

        var other = Engine();
        One(other, 1, 0.20, 0);
        Assert.Equal(FeedbackKind.Rating, One(other, 1, 0.90, 1).Kind);

        // Se consume: la frase buena siguiente ya no es recuperación.
        Assert.Equal(FeedbackKind.Rating, One(engine, 1, 0.90, 3).Kind);
    }

    [Fact]
    public void Two_players_with_a_perfect_phrase_together_get_the_duel_message()
    {
        var engine = Engine();
        var messages = engine.Evaluate(
            [new LineEvent(1, Line(1.0)), new LineEvent(2, Line(0.99))], TimeSpan.FromSeconds(1));

        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal(FeedbackKind.Duel, m.Kind));
        Assert.All(messages, m => Assert.Contains(m.Text, PhraseFeedbackCatalog.Spanish.Duel));
        Assert.Equal(messages[0].Text, messages[1].Text);
        Assert.Equal([1, 2], messages.Select(m => m.Player));
    }

    [Fact]
    public void Simultaneous_events_that_are_not_both_perfect_keep_their_own_messages()
    {
        var engine = Engine();
        var messages = engine.Evaluate(
            [new LineEvent(1, Line(1.0)), new LineEvent(2, Line(0.50))], TimeSpan.FromSeconds(1));

        Assert.Equal([PhraseRating.Perfect, PhraseRating.Acceptable], messages.Select(m => m.Rating));
        Assert.All(messages, m => Assert.Equal(FeedbackKind.Rating, m.Kind));
    }

    [Fact]
    public void Hidden_events_update_streaks_without_producing_messages()
    {
        var engine = Engine();
        var first = engine.Evaluate(
        [
            new LineEvent(1, Line(0.85, 0), Show: false),
            new LineEvent(1, Line(0.85, 1), Show: false),
            new LineEvent(1, Line(0.85, 2), Show: false),
        ], TimeSpan.FromSeconds(1));
        Assert.Empty(first);

        Assert.Equal(FeedbackKind.Streak, One(engine, 1, 0.85, 3).Kind);
    }

    [Fact]
    public void Reset_forgets_streaks_of_that_player_only()
    {
        var engine = Engine();
        for (var i = 0; i < 3; i++)
        {
            One(engine, 1, 0.85, i);
            One(engine, 2, 0.85, i);
        }

        engine.Reset(1);

        Assert.Equal(FeedbackKind.Rating, One(engine, 1, 0.85, 3).Kind);
        Assert.Equal(FeedbackKind.Streak, One(engine, 2, 0.85, 3).Kind);
    }

    // ---- Seguimiento sobre puntuadores reales -----------------------------------------------------------------

    [Fact]
    public void Tracker_shows_only_the_latest_fresh_phrase_and_consumes_it_once()
    {
        var song = Parse();
        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));
        var tracker = new PhraseFeedbackTracker(Engine());
        tracker.SetPlayers([(1, scorer)]);

        var shown = tracker.Poll(TimeSpan.FromSeconds(5.0));

        var message = Assert.Single(shown);
        Assert.Equal(1, message.Player);
        Assert.Equal(PhraseRating.Perfect, message.Rating);
        Assert.Equal(TimeSpan.FromSeconds(5.0), message.ShownAt);
        Assert.Empty(tracker.Poll(TimeSpan.FromSeconds(5.1)));
    }

    [Fact]
    public void Tracker_drops_phrases_that_closed_long_ago()
    {
        var song = Parse();
        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));
        var tracker = new PhraseFeedbackTracker(Engine());
        tracker.SetPlayers([(1, scorer)]);

        // Después de una pausa larga o un salto, la frase ya es vieja: no se muestra.
        Assert.Empty(tracker.Poll(TimeSpan.FromSeconds(4.75) + PhraseFeedbackTracker.MaxAge + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Tracker_forgets_what_it_saw_when_the_scorer_restarts()
    {
        var song = Parse();
        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));
        var tracker = new PhraseFeedbackTracker(Engine());
        tracker.SetPlayers([(1, scorer)]);
        Assert.Single(tracker.Poll(TimeSpan.FromSeconds(5.0)));

        scorer.Reset();
        Assert.Empty(tracker.Poll(TimeSpan.FromSeconds(0.1)));

        SingerSim.Drive(scorer, SingerSim.Perfect(song), 6.0, 0.0167, new PitchTimeline());
        Assert.Single(tracker.Poll(TimeSpan.FromSeconds(5.0)));
    }

    [Fact]
    public void Tracker_keeps_players_independent_and_simultaneous_perfects_make_a_duel()
    {
        var song = Parse();
        var a = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));
        var b = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));
        var weak = SingerSim.Run(song, new ScoringRules(), _ => (0, -80));
        var tracker = new PhraseFeedbackTracker(Engine());

        tracker.SetPlayers([(1, a), (2, b)]);
        var duel = tracker.Poll(TimeSpan.FromSeconds(5.0));
        Assert.Equal(2, duel.Count);
        Assert.All(duel, m => Assert.Equal(FeedbackKind.Duel, m.Kind));

        tracker.SetPlayers([(1, a), (3, weak)]);
        var mixed = tracker.Poll(TimeSpan.FromSeconds(5.0));
        Assert.Equal([PhraseRating.Disaster], mixed.Select(m => m.Rating)); // el jugador 1 ya había consumido lo suyo
        Assert.Equal(3, Assert.Single(mixed).Player);
    }

    [Fact]
    public void Tracker_keeps_state_when_the_roster_is_refreshed_with_the_same_scorer()
    {
        var song = Parse();
        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));
        var tracker = new PhraseFeedbackTracker(Engine());
        tracker.SetPlayers([(1, scorer)]);
        Assert.Single(tracker.Poll(TimeSpan.FromSeconds(5.0)));

        tracker.SetPlayers([(1, scorer)]);

        Assert.Empty(tracker.Poll(TimeSpan.FromSeconds(5.0)));
    }
}
