// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Feedback;
using Kantor.Core.Scoring;
using Kantor.Core.Songs;
using Xunit;

namespace Kantor.Tests;

public class PhraseFeedbackLanguageTests
{
    private static readonly PhraseFeedbackCatalog Es = PhraseFeedbackCatalog.Spanish;
    private static readonly PhraseFeedbackCatalog En = PhraseFeedbackCatalog.English;

    public static TheoryData<string> Languages => ["es", "en"];

    private static PhraseFeedbackCatalog Catalog(string language) => language == "en" ? En : Es;

    private static IEnumerable<string> All(PhraseFeedbackCatalog c) =>
        c.ByRating.Values.SelectMany(v => v).Concat(c.Streak).Concat(c.Recovery).Concat(c.Duel);

    // ---- Estructura de los catálogos ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_level_exists_with_several_variants(string language)
    {
        var catalog = Catalog(language);

        foreach (var rating in Enum.GetValues<PhraseRating>())
        {
            Assert.True(catalog.ByRating.TryGetValue(rating, out var list), $"{language}: falta {rating}");
            Assert.True(list!.Count >= 12, $"{language}/{rating}: {list.Count}");
        }

        Assert.Equal(Enum.GetValues<PhraseRating>().Length, catalog.ByRating.Count);
        Assert.NotEmpty(catalog.Streak);
        Assert.NotEmpty(catalog.Recovery);
        Assert.NotEmpty(catalog.Duel);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Messages_are_short_non_empty_and_not_repeated_within_a_list(string language)
    {
        var catalog = Catalog(language);
        var lists = catalog.ByRating.Values.Concat([catalog.Streak, catalog.Recovery, catalog.Duel]);

        foreach (var list in lists)
        {
            Assert.Equal(list.Count, list.Distinct().Count());
            foreach (var message in list)
            {
                Assert.False(string.IsNullOrWhiteSpace(message));
                Assert.Equal(message.Trim(), message);
                // las frases largas se encogen para entrar en la franja del escenario
                const int maxWords = 12;
                const int maxLength = 56;
                Assert.InRange(message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, 1, maxWords);
                Assert.True(message.Length <= maxLength, $"{language}: demasiado largo para la franja: {message}");
            }
        }
    }

    [Fact]
    public void English_messages_are_plain_uppercase_without_spanish_punctuation()
    {
        foreach (var message in All(En))
        {
            Assert.Equal(message.ToUpperInvariant(), message);
            Assert.True(message[^1] is '!' or '.' or '?', message); // el tono varía: los niveles bajos cierran con punto
            Assert.DoesNotContain('¡', message);
            Assert.DoesNotContain('’', message); // apóstrofo recto: la fuente pixelada no garantiza el tipográfico
        }
    }

    [Fact]
    public void English_has_the_special_messages_for_the_existing_events()
    {
        Assert.Contains("ON FIRE!", En.Streak);
        Assert.Contains("COMEBACK!", En.Recovery);
        Assert.Contains("DOUBLE PERFECT!", En.Duel);
        Assert.True(En.Streak.Count >= 8 && En.Recovery.Count >= 8 && En.Duel.Count >= 8);
    }

    [Fact]
    public void The_two_catalogs_are_independent()
    {
        Assert.NotSame(Es, En);
        Assert.Empty(All(Es).Intersect(All(En)));

        // Mismos niveles, pero listas propias: no hay correspondencia frase por frase ni la misma cantidad especial.
        Assert.NotEqual(Es.Streak.Count, En.Streak.Count);
        Assert.Equal(Es.ByRating.Keys.OrderBy(k => k), En.ByRating.Keys.OrderBy(k => k));
    }

    [Fact]
    public void The_spanish_catalog_is_large_enough_to_avoid_repeats()
    {
        foreach (var (rating, list) in Es.ByRating)
        {
            Assert.True(list.Count >= 100, $"{rating}: {list.Count}");
        }

        Assert.True(Es.Streak.Count >= 8 && Es.Recovery.Count >= 8 && Es.Duel.Count >= 8);
    }

    // ---- Selección de idioma y fallback --------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData("en-US")]
    [InlineData("en_GB")]
    public void English_codes_select_the_english_catalog(string language) =>
        Assert.Same(En, PhraseFeedbackCatalog.For(language));

    [Theory]
    [InlineData("es")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("eng")]
    [InlineData("end")]
    public void Anything_else_falls_back_to_spanish(string? language) =>
        Assert.Same(Es, PhraseFeedbackCatalog.For(language));

    [Theory]
    [InlineData("es-AR")]
    [InlineData("es_AR")]
    [InlineData("ES-ar")]
    public void The_argentine_code_selects_the_argentine_catalog(string language) =>
        Assert.Same(PhraseFeedbackCatalog.ArgentineSpanish, PhraseFeedbackCatalog.For(language));

    [Fact]
    public void The_argentine_catalog_differs_from_the_neutral_one_but_keeps_its_structure()
    {
        var ar = PhraseFeedbackCatalog.ArgentineSpanish;
        Assert.NotSame(Es, ar);
        Assert.Equal(Es.ByRating.Keys.OrderBy(k => k), ar.ByRating.Keys.OrderBy(k => k));
        foreach (var rating in Es.ByRating.Keys)
        {
            Assert.Equal(Es.ByRating[rating].Count, ar.ByRating[rating].Count);
        }

        Assert.Equal(Es.Streak.Count, ar.Streak.Count);
        Assert.Equal(Es.Recovery.Count, ar.Recovery.Count);
        Assert.Equal(Es.Duel.Count, ar.Duel.Count);
        Assert.NotEmpty(All(Es).Except(All(ar)));
    }

    // ---- El motor con cada idioma --------------------------------------------------------------------------------

    private static LineResult Line(double fraction, int sequence = 0) =>
        new(0, sequence, fraction, TimeSpan.FromSeconds(1));

    [Theory]
    [MemberData(nameof(Languages))]
    public void The_engine_only_uses_the_selected_catalog_for_every_kind_of_message(string language)
    {
        var catalog = Catalog(language);
        var allowed = All(catalog).ToHashSet();
        var engine = new PhraseFeedbackEngine(new Random(11), PhraseFeedbackCatalog.For(language));
        var texts = new List<string>();

        // Valoraciones de todos los niveles, una racha, una recuperación y un duelo.
        foreach (var fraction in new[] { 1.0, 0.92, 0.85, 0.7, 0.55, 0.4, 0.2, 0.0 })
        {
            texts.Add(Assert.Single(engine.Evaluate([new LineEvent(1, Line(fraction))], TimeSpan.Zero)).Text);
        }

        var fresh = new PhraseFeedbackEngine(new Random(5), PhraseFeedbackCatalog.For(language));
        for (var i = 0; i < 4; i++)
        {
            texts.Add(Assert.Single(fresh.Evaluate([new LineEvent(1, Line(0.85, i))], TimeSpan.Zero)).Text);
        }

        texts.Add(Assert.Single(fresh.Evaluate([new LineEvent(1, Line(0.1, 4))], TimeSpan.Zero)).Text);
        texts.Add(Assert.Single(fresh.Evaluate([new LineEvent(1, Line(0.1, 5))], TimeSpan.Zero)).Text);
        var recovery = Assert.Single(fresh.Evaluate([new LineEvent(1, Line(0.9, 6))], TimeSpan.Zero));
        Assert.Equal(FeedbackKind.Recovery, recovery.Kind);
        Assert.Contains(recovery.Text, catalog.Recovery);
        texts.Add(recovery.Text);

        var duel = fresh.Evaluate([new LineEvent(1, Line(1.0, 7)), new LineEvent(2, Line(1.0, 7))], TimeSpan.Zero);
        Assert.All(duel, d => Assert.Contains(d.Text, catalog.Duel));
        texts.AddRange(duel.Select(d => d.Text));

        Assert.All(texts, t => Assert.Contains(t, allowed));
    }

    [Fact]
    public void Streak_messages_come_from_the_english_streak_list()
    {
        var engine = new PhraseFeedbackEngine(new Random(2), En);
        PhraseFeedback? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = Assert.Single(engine.Evaluate([new LineEvent(1, Line(0.85, i))], TimeSpan.Zero));
        }

        Assert.Equal(FeedbackKind.Streak, last!.Kind);
        Assert.Contains(last.Text, En.Streak);
    }

    [Fact]
    public void English_never_repeats_one_of_the_last_four_messages_of_a_player()
    {
        var engine = new PhraseFeedbackEngine(new Random(9), En);
        var texts = Enumerable.Range(0, 300)
            .Select(i => Assert.Single(engine.Evaluate([new LineEvent(1, Line(0.55, i))], TimeSpan.Zero)).Text)
            .ToList();

        for (var i = 0; i < texts.Count; i++)
        {
            for (var back = 1; back <= PhraseFeedbackEngine.HistorySize && i - back >= 0; back++)
            {
                Assert.NotEqual(texts[i - back], texts[i]);
            }
        }

        Assert.True(texts.Distinct().Count() >= 10); // usa casi todo el nivel, no un par de frases
    }

    [Fact]
    public void Changing_the_catalog_midway_switches_language_without_mixing()
    {
        var engine = new PhraseFeedbackEngine(new Random(4), Es);
        var spanish = Assert.Single(engine.Evaluate([new LineEvent(1, Line(1.0, 0))], TimeSpan.Zero));
        Assert.Contains(spanish.Text, Es.ByRating[PhraseRating.Perfect]);

        engine.Catalog = En;
        var english = Assert.Single(engine.Evaluate([new LineEvent(1, Line(1.0, 1))], TimeSpan.Zero));
        Assert.Contains(english.Text, En.ByRating[PhraseRating.Perfect]);
    }

    // ---- Seguimiento y scoring ------------------------------------------------------------------------------------

    private const string Song = """
        #TITLE:Dos
        #ARTIST:T
        #MP3:a.mp3
        #BPM:60
        #GAP:0
        : 0 4 0 uno
        * 4 2 7 dos
        - 10
        : 12 4 -5 cinco
        E
        """;

    [Fact]
    public void The_tracker_follows_the_interface_language_and_scoring_is_untouched()
    {
        var song = UltraStarParser.Parse(Song);
        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));
        var before = scorer.Snapshot;
        var lines = scorer.LineResults.ToList();

        var tracker = new PhraseFeedbackTracker(new PhraseFeedbackEngine(new Random(1)));
        tracker.SetLanguage("en");
        tracker.SetPlayers([(1, scorer)]);
        var message = Assert.Single(tracker.Poll(TimeSpan.FromSeconds(4.0)));

        Assert.Contains(message.Text, En.ByRating[PhraseRating.Perfect]);
        Assert.Equal(before, scorer.Snapshot);
        Assert.Equal(10000, scorer.Snapshot.Total);
        Assert.Equal(lines, scorer.LineResults);
    }

    [Fact]
    public void The_tracker_falls_back_to_spanish_for_unknown_languages()
    {
        var song = UltraStarParser.Parse(Song);
        var scorer = SingerSim.Run(song, new ScoringRules(), SingerSim.Perfect(song));
        var tracker = new PhraseFeedbackTracker(new PhraseFeedbackEngine(new Random(1)));
        tracker.SetLanguage("fr");
        tracker.SetPlayers([(1, scorer)]);

        var message = Assert.Single(tracker.Poll(TimeSpan.FromSeconds(4.0)));

        Assert.Contains(message.Text, Es.ByRating[PhraseRating.Perfect]);
    }
}
