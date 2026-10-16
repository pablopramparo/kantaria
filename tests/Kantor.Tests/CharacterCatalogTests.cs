// SPDX-License-Identifier: GPL-3.0-or-later
using Kantor.Core.Characters;
using Xunit;

namespace Kantor.Tests;

public class CharacterCatalogTests
{
    [Fact]
    public void DefaultCatalogHasTheFactoryAvatarsAndFallsBackToTheFirst()
    {
        var all = CharacterCatalog.Default.All;
        Assert.Equal(CharacterCatalog.FactoryCount, all.Count);
        Assert.Equal("avatar_001", all[0].Id);
        Assert.Equal("avatar_030", all[^1].Id);
        Assert.Equal("avatar_001", CharacterCatalog.Default.GetOrDefault("provisional").Id);
        Assert.Same(CharacterCatalog.Fallback, CharacterCatalog.Default.GetOrDefault(null));
        Assert.Equal("Assets/Characters/avatar_003/portrait.png", all[2].ResolvedPortraitPath);
    }

    [Fact]
    public void ResourcePathsFollowTheConventionUnlessOverridden()
    {
        var byConvention = new CharacterDefinition("rockero", "Rockero");
        Assert.Equal("Assets/Characters/rockero/portrait.png", byConvention.ResolvedPortraitPath);
        Assert.Equal("Assets/Characters/rockero/fullbody.png", byConvention.ResolvedFullBodyPath);

        var custom = new CharacterDefinition("rockero", "Rockero", "Assets/x/p.png", "Assets/x/f.png");
        Assert.Equal("Assets/x/p.png", custom.ResolvedPortraitPath);
        Assert.Equal("Assets/x/f.png", custom.ResolvedFullBodyPath);
    }

    [Fact]
    public void LookupByIdAndFallbackForUnknownIds()
    {
        var catalog = new CharacterCatalog(
        [
            new CharacterDefinition("rockero", "Rockero", Tuning: new CharacterTuning(VisualScale: 1.3)),
            new CharacterDefinition("dino", "Dino", Tuning: new CharacterTuning(GroundOffset: 6)),
        ]);

        Assert.True(catalog.TryGet("dino", out var dino));
        Assert.Equal(6, dino.Tuning!.GroundOffset);
        Assert.False(catalog.TryGet("fantasma", out _));
        Assert.Equal("rockero", catalog.GetOrDefault("fantasma").Id);
        Assert.Equal("rockero", catalog.GetOrDefault(null).Id);
    }

    [Fact]
    public void TuningDefaultsKeepTheBaseBehaviour()
    {
        var t = new CharacterTuning();
        Assert.Equal(1.0, t.VisualScale);
        Assert.Equal(0.0, t.GroundOffset);
        Assert.Equal(0.12, t.Overflow);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Rockero")]
    [InlineData("con espacio")]
    [InlineData("ñandú")]
    public void InvalidIdsAreRejected(string id) =>
        Assert.Throws<ArgumentException>(() => new CharacterCatalog([new CharacterDefinition(id, "X")]));

    [Fact]
    public void DuplicateIdsEmptyCatalogsAndMissingNamesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new CharacterCatalog(
            [new CharacterDefinition("a", "A"), new CharacterDefinition("a", "Otra")]));
        Assert.Throws<ArgumentException>(() => new CharacterCatalog([]));
        Assert.Throws<ArgumentException>(() => new CharacterCatalog([new CharacterDefinition("a", " ")]));
    }
}
