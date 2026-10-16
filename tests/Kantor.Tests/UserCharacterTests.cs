// SPDX-License-Identifier: GPL-3.0-or-later
using Kantor.Core.Characters;
using Xunit;

namespace Kantor.Tests;

public sealed class UserCharacterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kantor-chars-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Add_then_load_returns_the_character_with_absolute_paths()
    {
        var store = new UserCharacterStore(_root);
        var saved = store.Add([1, 2, 3], [4, 5]);

        var loaded = Assert.Single(store.Load());
        Assert.Equal(saved.Id, loaded.Id);
        Assert.StartsWith(UserCharacterStore.IdPrefix, loaded.Id);
        Assert.True(loaded.IsUser);
        Assert.True(File.Exists(loaded.PortraitPath));
        Assert.True(File.Exists(loaded.FullBodyPath));
        Assert.True(Path.IsPathRooted(loaded.ResolvedPortraitPath));
    }

    [Fact]
    public void A_character_may_have_only_one_image()
    {
        var store = new UserCharacterStore(_root);
        var onlyFace = store.Add(null, [1]);
        Assert.NotNull(onlyFace.PortraitPath);
        Assert.Null(onlyFace.FullBodyPath);
    }

    [Fact]
    public void Invalid_input_is_rejected_and_leaves_nothing_behind()
    {
        var store = new UserCharacterStore(_root);
        Assert.Throws<ArgumentException>(() => store.Add(null, null));
        Assert.Throws<ArgumentException>(() => store.Add([], []));
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Delete_removes_only_user_characters_and_ignores_unknown_ids()
    {
        var store = new UserCharacterStore(_root);
        var saved = store.Add([1], [1]);
        store.Delete("provisional");
        store.Delete("usuario-noexiste");
        Assert.Single(store.Load());

        store.Delete(saved.Id);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Broken_folders_are_ignored()
    {
        var store = new UserCharacterStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "usuario-vacio"));
        Directory.CreateDirectory(Path.Combine(_root, "ajena"));
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Catalog_combines_factory_and_user_characters_and_replaces_them()
    {
        var catalog = new CharacterCatalog([new CharacterDefinition("rockero", "Rockero")]);
        catalog.SetUserCharacters([new CharacterDefinition("usuario-1", "Ana"), new CharacterDefinition("rockero", "Copia")]);

        Assert.Equal(["rockero", "usuario-1"], catalog.All.Select(c => c.Id));
        Assert.True(catalog.TryGet("usuario-1", out var ana));
        Assert.True(ana.IsUser);
        Assert.Equal("Rockero", catalog.GetOrDefault("rockero").Name);

        catalog.SetUserCharacters([]);
        Assert.Equal(["rockero"], catalog.All.Select(c => c.Id));
        Assert.Equal("rockero", catalog.GetOrDefault("usuario-1").Id);
    }

    [Fact]
    public void Suggested_crop_targets_the_top_of_a_tall_image_and_the_whole_of_a_square_one()
    {
        var tall = SquareCrop.Suggested(400, 1000);
        Assert.Equal(350, tall.Size, 3);
        Assert.Equal(25, tall.X, 3);
        Assert.True(tall.Y < 50);

        var square = SquareCrop.Suggested(300, 300);
        Assert.Equal(new SquareCrop(0, 0, 300), square);
    }

    [Fact]
    public void Crop_never_leaves_the_image_or_stops_being_square()
    {
        var crop = new SquareCrop(90, 90, 50);
        var moved = crop.Moved(500, 500, 100, 200);
        Assert.Equal(50, moved.Size);
        Assert.Equal(50, moved.X);
        Assert.Equal(150, moved.Y);

        var big = crop.Resized(10_000, 100, 200);
        Assert.Equal(100, big.Size);
        Assert.Equal(0, big.X);

        var tiny = crop.Resized(1, 100, 200);
        Assert.Equal(SquareCrop.MinSize, tiny.Size);

        var (x, y, size) = new SquareCrop(-5, 3.6, 40.4).ToPixels(100, 200);
        Assert.Equal((0, 4, 40), (x, y, size));
    }
}
