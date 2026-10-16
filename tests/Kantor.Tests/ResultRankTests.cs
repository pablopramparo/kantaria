// SPDX-License-Identifier: GPL-3.0-or-later
using Kantor.Core.Results;
using Xunit;

namespace Kantor.Tests;

public class ResultRankTests
{
    [Theory]
    [InlineData(0, ResultRank.SystemSurvived)]
    [InlineData(1999, ResultRank.SystemSurvived)]
    [InlineData(2000, ResultRank.NiceTry)]
    [InlineData(3999, ResultRank.NiceTry)]
    [InlineData(4000, ResultRank.Acceptable)]
    [InlineData(5999, ResultRank.Acceptable)]
    [InlineData(6000, ResultRank.NiceWork)]
    [InlineData(7999, ResultRank.NiceWork)]
    [InlineData(8000, ResultRank.Impressive)]
    [InlineData(8999, ResultRank.Impressive)]
    [InlineData(9000, ResultRank.Superstar)]
    [InlineData(9499, ResultRank.Superstar)]
    [InlineData(9500, ResultRank.Legendary)]
    [InlineData(9999, ResultRank.Legendary)]
    [InlineData(10000, ResultRank.Perfect)]
    public void ScoreMapsToRank(int score, ResultRank expected) => Assert.Equal(expected, ResultRanks.For(score));

    [Fact]
    public void EveryRankHasLabelsInBothLanguages()
    {
        foreach (var rank in Enum.GetValues<ResultRank>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ResultRanks.Label(rank, "es")));
            Assert.False(string.IsNullOrWhiteSpace(ResultRanks.Label(rank, "en")));
        }
    }

    [Fact]
    public void EnglishLabelsMatchTheSpecification()
    {
        Assert.Equal("SYSTEM SURVIVED", ResultRanks.Label(ResultRank.SystemSurvived, "en"));
        Assert.Equal("PERFECT!", ResultRanks.Label(ResultRank.Perfect, "en"));
        Assert.Equal("LEGENDARY!", ResultRanks.Label(ResultRank.Legendary, "en"));
    }
}

public class SpanishStampTests
{
    [Theory]
    [InlineData(ResultRank.SystemSurvived, "SISTEMA INTACTO")]
    [InlineData(ResultRank.NiceTry, "¡LO INTENTASTE!")]
    [InlineData(ResultRank.Acceptable, "APROBADO... CREO")]
    [InlineData(ResultRank.NiceWork, "¡NADA MAL!")]
    [InlineData(ResultRank.Impressive, "¡IMPRESIONANTE!")]
    [InlineData(ResultRank.Superstar, "¡SUPERSTAR!")]
    [InlineData(ResultRank.Legendary, "¡LEGENDARIO!")]
    [InlineData(ResultRank.Perfect, "¡PERFECTO!")]
    public void SpanishLabelsMatchTheApprovedTexts(ResultRank rank, string expected) =>
        Assert.Equal(expected, ResultRanks.Label(rank, "es"));
}

public class ResultsInputGateTests
{
    [Fact]
    public void EnterDuringAnimationSkipsAndNeverActivates()
    {
        var gate = new ResultsInputGate();
        Assert.Equal(ResultsKeyAction.Skip, gate.KeyDown(ResultsKey.Enter, isAnimating: true));
        Assert.True(gate.KeyUp(ResultsKey.Enter)); // la liberación no activa el botón
    }

    [Fact]
    public void KeyRepeatAfterSkipIsSwallowedEvenWhenAnimationHasEnded()
    {
        var gate = new ResultsInputGate();
        gate.KeyDown(ResultsKey.Space, isAnimating: true);
        Assert.Equal(ResultsKeyAction.Swallow, gate.KeyDown(ResultsKey.Space, isAnimating: false));
        Assert.Equal(ResultsKeyAction.Swallow, gate.KeyDown(ResultsKey.Space, isAnimating: false));
        Assert.True(gate.KeyUp(ResultsKey.Space));
    }

    [Fact]
    public void LaterPressActivatesTheSelectedAction()
    {
        var gate = new ResultsInputGate();
        gate.KeyDown(ResultsKey.Enter, isAnimating: true);
        gate.KeyUp(ResultsKey.Enter);
        Assert.Equal(ResultsKeyAction.Activate, gate.KeyDown(ResultsKey.Enter, isAnimating: false));
        Assert.False(gate.KeyUp(ResultsKey.Enter)); // esta liberación sí es del botón
    }

    [Fact]
    public void SpaceActivatesWhenNotAnimating()
    {
        var gate = new ResultsInputGate();
        Assert.Equal(ResultsKeyAction.Activate, gate.KeyDown(ResultsKey.Space, isAnimating: false));
    }

    [Fact]
    public void EscapeAlwaysCloses()
    {
        var gate = new ResultsInputGate();
        Assert.Equal(ResultsKeyAction.Close, gate.KeyDown(ResultsKey.Escape, isAnimating: true));
        Assert.Equal(ResultsKeyAction.Close, gate.KeyDown(ResultsKey.Escape, isAnimating: false));
    }

    [Fact]
    public void OtherKeysAreNotTheGatesBusiness()
    {
        var gate = new ResultsInputGate();
        Assert.Equal(ResultsKeyAction.None, gate.KeyDown(ResultsKey.Other, isAnimating: true));
        Assert.Equal(ResultsKeyAction.None, gate.KeyDown(ResultsKey.Left, isAnimating: true));
    }

    [Fact]
    public void SkippingWithOneKeyDoesNotSwallowTheOther()
    {
        var gate = new ResultsInputGate();
        gate.KeyDown(ResultsKey.Enter, isAnimating: true);
        Assert.Equal(ResultsKeyAction.Activate, gate.KeyDown(ResultsKey.Space, isAnimating: false));
    }
}

public class ResultStandingsTests
{
    [Fact]
    public void SinglePlayerHasNoWinnerNorTie()
    {
        var s = ResultStandings.Compute([9000]);
        Assert.False(s[0].IsWinner);
        Assert.False(s[0].IsTie);
    }

    [Fact]
    public void HighestScoreWinsAlone()
    {
        var s = ResultStandings.Compute([8413, 9652, 100]);
        Assert.Equal([false, true, false], s.Select(x => x.IsWinner));
        Assert.All(s, x => Assert.False(x.IsTie));
    }

    [Fact]
    public void TwoWayTieAtTopMarksOnlyTheTiedPlayers()
    {
        var s = ResultStandings.Compute([7000, 9000, 9000, 200]);
        Assert.Equal([false, true, true, false], s.Select(x => x.IsTie));
        Assert.All(s, x => Assert.False(x.IsWinner));
    }

    [Fact]
    public void ThreeWayAndFullTiesAreSupported()
    {
        Assert.Equal([true, true, true, false], ResultStandings.Compute([5000, 5000, 5000, 100]).Select(x => x.IsTie));
        Assert.All(ResultStandings.Compute([6000, 6000, 6000, 6000]), x => Assert.True(x.IsTie));
    }

    [Fact]
    public void StandingsDoNotChangeIndividualRanks()
    {
        // 9500 y 9999 empatan en rango "legendario" aunque uno gane.
        Assert.Equal(ResultRanks.For(9500), ResultRanks.For(9999));
        var s = ResultStandings.Compute([9500, 9999]);
        Assert.True(s[1].IsWinner);
    }
}

public class CeremonyKeyTests
{
    [Fact]
    public void EnterAfterAnimationAdvancesAndSwallowsRepeatAndRelease()
    {
        var gate = new ResultsInputGate();
        Assert.Equal(ResultsKeyAction.Advance, gate.KeyDown(ResultsKey.Enter, isAnimating: false, ceremonyActive: true));
        Assert.Equal(ResultsKeyAction.Swallow, gate.KeyDown(ResultsKey.Enter, isAnimating: false, ceremonyActive: true));
        Assert.True(gate.KeyUp(ResultsKey.Enter));
    }

    [Fact]
    public void EnterDuringAnimationStillSkipsOnlyTheAnimation()
    {
        var gate = new ResultsInputGate();
        Assert.Equal(ResultsKeyAction.Skip, gate.KeyDown(ResultsKey.Space, isAnimating: true, ceremonyActive: true));
        Assert.True(gate.KeyUp(ResultsKey.Space));
        Assert.Equal(ResultsKeyAction.Advance, gate.KeyDown(ResultsKey.Space, isAnimating: false, ceremonyActive: true));
    }

    [Fact]
    public void EndSkipsTheWholeCeremonyOnlyWhileItRuns()
    {
        var gate = new ResultsInputGate();
        Assert.Equal(ResultsKeyAction.SkipCeremony, gate.KeyDown(ResultsKey.End, isAnimating: true, ceremonyActive: true));
        Assert.Equal(ResultsKeyAction.None, gate.KeyDown(ResultsKey.End, isAnimating: false, ceremonyActive: false));
    }

    [Fact]
    public void AfterTheCeremonyEnterActivatesTheButton()
    {
        var gate = new ResultsInputGate();
        Assert.Equal(ResultsKeyAction.Activate, gate.KeyDown(ResultsKey.Enter, isAnimating: false, ceremonyActive: false));
    }
}

public class CharacterPlacementTests
{
    [Fact]
    public void DefaultsKeepTheBaseBehaviour()
    {
        // 160×300 en una zona de 250×440: ajustado por alto, anclado por los pies, centrado
        var r = CharacterPlacement.Compute(160, 300, 250, 440);
        var feet = 440 - CharacterPlacement.ShadowBand;
        var scale = Math.Min(250 * 1.15 / 160, feet * 1.12 / 300);
        Assert.Equal(feet, r.Y + r.Height, 6);
        Assert.Equal(160 * scale, r.Width, 6);
        Assert.Equal(125 - r.Width / 2, r.X, 6);
    }

    [Fact]
    public void AspectRatioIsAlwaysPreserved()
    {
        foreach (var (w, h) in new[] { (160, 300), (300, 200), (220, 220), (120, 180), (500, 90) })
        {
            var r = CharacterPlacement.Compute(w, h, 250, 440, visualScale: 1.4, groundOffset: 6, overflow: 0.2);
            Assert.Equal(w / (double)h, r.Width / r.Height, 6);
        }
    }

    [Fact]
    public void VisualScaleEnlargesWideSpritesButHeightStaysWithinTheOverflowLimit()
    {
        var normal = CharacterPlacement.Compute(300, 200, 250, 440);
        var bigger = CharacterPlacement.Compute(300, 200, 250, 440, visualScale: 1.5);
        Assert.True(bigger.Width > normal.Width);

        var tall = CharacterPlacement.Compute(160, 300, 250, 440, visualScale: 3);
        var limit = (440 - CharacterPlacement.ShadowBand) * 1.12;
        Assert.True(tall.Height <= limit + 1e-9);
    }

    [Fact]
    public void GroundOffsetMovesTheFeetLine()
    {
        var a = CharacterPlacement.Compute(220, 220, 250, 440);
        var b = CharacterPlacement.Compute(220, 220, 250, 440, groundOffset: 10);
        Assert.True(b.Y + b.Height > a.Y + a.Height);
    }

    [Fact]
    public void OverflowLimitIsAdjustablePerCharacter()
    {
        var def = CharacterPlacement.Compute(120, 400, 250, 440);
        var more = CharacterPlacement.Compute(120, 400, 250, 440, overflow: 0.30);
        Assert.True(more.Height > def.Height);
        Assert.True(more.Y < def.Y);
    }

    [Fact]
    public void InvalidSizesProduceAnEmptyRect()
    {
        Assert.Equal(new PlacementRect(0, 0, 0, 0), CharacterPlacement.Compute(0, 100, 250, 440));
    }
}
