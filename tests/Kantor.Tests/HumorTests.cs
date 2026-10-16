// SPDX-License-Identifier: GPL-3.0-or-later
using Kantor.Core.Humor;
using Kantor.Core.Settings;
using Xunit;

namespace Kantor.Tests;

/// <summary>Fuente de azar determinista: devuelve valores de una cola (y cicla) o una semilla fija.</summary>
internal sealed class ScriptedRandom : IRandomSource
{
    private readonly Queue<double> _doubles;
    private readonly Random _ints;

    public ScriptedRandom(int seed = 1, params double[] doubles)
    {
        _ints = new Random(seed);
        _doubles = new Queue<double>(doubles);
    }

    public int Next(int maxExclusive) => _ints.Next(maxExclusive);

    public double NextDouble() => _doubles.Count > 0 ? _doubles.Dequeue() : 0.99;
}

public class HumorRangeTests
{
    [Theory]
    [InlineData(0, HumorCategory.Disaster)]
    [InlineData(1999, HumorCategory.Disaster)]
    [InlineData(2000, HumorCategory.Trying)]
    [InlineData(3999, HumorCategory.Trying)]
    [InlineData(4000, HumorCategory.Technically)]
    [InlineData(5999, HumorCategory.Technically)]
    [InlineData(6000, HumorCategory.Good)]
    [InlineData(7999, HumorCategory.Good)]
    [InlineData(8000, HumorCategory.Suspicious)]
    [InlineData(8999, HumorCategory.Suspicious)]
    [InlineData(9000, HumorCategory.Superstar)]
    [InlineData(9499, HumorCategory.Superstar)]
    [InlineData(9500, HumorCategory.Mythical)]
    [InlineData(9999, HumorCategory.Mythical)]
    [InlineData(10000, HumorCategory.Divine)]
    public void ScoreFallsInExpectedCategory(int score, HumorCategory expected) =>
        Assert.Equal(expected, HumorRanges.CategoryFor(score));
}

public class HumorBankTests
{
    private static readonly Dictionary<HumorCategory, int> Expected = new()
    {
        [HumorCategory.Disaster] = 200, [HumorCategory.Trying] = 200, [HumorCategory.Technically] = 200,
        [HumorCategory.Good] = 200, [HumorCategory.Suspicious] = 200, [HumorCategory.Superstar] = 200,
        [HumorCategory.Mythical] = 200, [HumorCategory.Divine] = 50, [HumorCategory.Random] = 100,
    };

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public void BankHasExpectedCountsAndNoEmptyTexts(string language)
    {
        var bank = HumorBank.Load(language);
        Assert.Equal(1550, bank.AllIds.Count);
        foreach (var (category, count) in Expected)
        {
            Assert.Equal(count, bank.Ids(category).Count);
        }

        Assert.All(bank.AllIds, id => Assert.False(string.IsNullOrWhiteSpace(bank.Text(id)), id));
    }

    [Fact]
    public void LanguagesShareExactlyTheSameIds()
    {
        var es = HumorBank.Load("es").AllIds.OrderBy(i => i, StringComparer.Ordinal);
        var en = HumorBank.Load("en").AllIds.OrderBy(i => i, StringComparer.Ordinal);
        Assert.Equal(en, es);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public void NoDuplicateTextInsideACategory(string language)
    {
        var bank = HumorBank.Load(language);
        foreach (HumorCategory c in Enum.GetValues<HumorCategory>())
        {
            var texts = bank.Ids(c).Select(bank.Text).ToList();
            Assert.Equal(texts.Count, texts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void UnknownLanguageFallsBackToDefault() =>
        Assert.Equal(HumorBank.DefaultLanguage, HumorBank.NormalizeLanguage("fr"));
}

public class CommentSelectorTests
{
    private static HumorBank Bank { get; } = HumorBank.Load("es");

    [Fact]
    public void BagDoesNotRepeatUntilExhaustedAndAvoidsBoundaryRepeat()
    {
        var items = Enumerable.Range(0, 20).Select(i => $"x{i}").ToList();
        var bag = new ShuffleBag(items, new ScriptedRandom(7));
        string? previous = null;
        for (var round = 0; round < 50; round++)
        {
            var drawn = Enumerable.Range(0, 20).Select(_ => bag.Next()).ToList();
            Assert.Equal(20, drawn.Distinct().Count());
            Assert.NotEqual(previous, drawn[0]);
            previous = drawn[^1];
        }
    }

    [Fact]
    public void NoRepeatsWithinACategoryForItsWholeBank()
    {
        var selector = new CommentSelector(Bank, new ScriptedRandom(3), nonsenseProbability: 0);
        var ids = Enumerable.Range(0, 200).Select(_ => selector.Pick(500).Id).ToList();
        Assert.Equal(200, ids.Distinct().Count());
    }

    [Fact]
    public void PerfectScoreOnlyUsesDivineBank()
    {
        // Aun con el azar forzando el easter egg, 10000 usa solo "divine".
        var selector = new CommentSelector(Bank, new ScriptedRandom(1, Enumerable.Repeat(0.0, 100).ToArray()), 1.0);
        for (var i = 0; i < 100; i++)
        {
            var pick = selector.Pick(10000);
            Assert.Equal(HumorCategory.Divine, pick.Category);
            Assert.StartsWith("divine_", pick.Id);
            Assert.False(pick.Nonsense);
        }
    }

    [Fact]
    public void NonsenseReplacesCommentButNeverTwiceInARow()
    {
        var selector = new CommentSelector(Bank, new ScriptedRandom(1, 0.0, 0.0, 0.0, 0.0), 0.03);
        var first = selector.Pick(5000);
        var second = selector.Pick(5000);
        var third = selector.Pick(5000);
        Assert.True(first.Nonsense);
        Assert.StartsWith("random_", first.Id);
        Assert.False(second.Nonsense);
        Assert.Equal(HumorCategory.Technically, second.Category);
        Assert.True(third.Nonsense);
    }

    [Fact]
    public void NonsenseDoesNotResetPerfectScoreRule()
    {
        var selector = new CommentSelector(Bank, new ScriptedRandom(1, 0.0, 0.0), 1.0);
        Assert.True(selector.Pick(100).Nonsense);
        Assert.Equal(HumorCategory.Divine, selector.Pick(10000).Category);
    }

    [Fact]
    public void ZeroProbabilityNeverProducesNonsense()
    {
        var selector = new CommentSelector(Bank, new ScriptedRandom(2, Enumerable.Repeat(0.0, 500).ToArray()), 0.0);
        for (var i = 0; i < 500; i++)
        {
            Assert.False(selector.Pick(i * 19 % 10000).Nonsense);
        }
    }

    [Fact]
    public void ObservedNonsenseRateIsNearConfiguredProbability()
    {
        var selector = new CommentSelector(Bank, new SystemRandomSource(new Random(42)), 0.03);
        var n = 20000;
        var hits = Enumerable.Range(0, n).Count(i => selector.Pick(i % 10000).Nonsense);
        var rate = hits / (double)n;
        Assert.InRange(rate, 0.02, 0.04);
    }

    [Fact]
    public void EveryPickBelongsToItsScoreRange()
    {
        var selector = new CommentSelector(Bank, new ScriptedRandom(5), 0.0);
        foreach (var score in new[] { 0, 1999, 2000, 3999, 4000, 5999, 6000, 7999, 8000, 8999, 9000, 9499, 9500, 9999, 10000 })
        {
            var pick = selector.Pick(score);
            Assert.Equal(HumorRanges.CategoryFor(score), pick.Category);
            Assert.StartsWith(HumorRanges.Prefix(pick.Category) + "_", pick.Id);
        }
    }

    [Fact]
    public void SameIdGivesTextInEachLanguage()
    {
        var selector = new CommentSelector(Bank, new ScriptedRandom(9), 0.0);
        var id = selector.Pick(7000).Id;
        var es = HumorBank.Load("es").Text(id);
        var en = HumorBank.Load("en").Text(id);
        Assert.NotEqual(es, en);
    }
}

public class HumorSettingsTests
{
    [Fact]
    public void LanguageAndProbabilityAreNormalized()
    {
        var s = new UserSettings { CommentLanguage = "xx", NonsenseProbability = 7.5 }.Normalized();
        Assert.Equal("es", s.CommentLanguage);
        Assert.Equal(1.0, s.NonsenseProbability);
        var d = new UserSettings { NonsenseProbability = double.NaN }.Normalized();
        Assert.Equal(CommentSelector.DefaultNonsenseProbability, d.NonsenseProbability);
    }
}
