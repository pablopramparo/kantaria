// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Characters;
using Xunit;

namespace Kantor.Tests;

public class CharacterEditionTests
{
    [Fact]
    public void The_list_accepts_semicolons_and_line_breaks_and_ignores_blanks()
    {
        var parsed = CharacterEdition.Parse("avatar_003;\r\n avatar_007 ;\n\n;avatar_010\r\n");
        Assert.Equal(["avatar_003", "avatar_007", "avatar_010"], parsed.Order());
        Assert.Empty(CharacterEdition.Parse(string.Empty));
        Assert.Empty(CharacterEdition.Parse(" \r\n ; \n"));
    }

    [Fact]
    public void The_store_catalog_is_the_local_one_minus_the_excluded_keeping_the_order()
    {
        var local = CharacterCatalog.BuildFactory(new HashSet<string>());
        var store = CharacterCatalog.BuildFactory(new HashSet<string> { "avatar_002", "avatar_029" });
        Assert.Equal(CharacterCatalog.FactoryCount, local.Count);
        Assert.Equal(local.Count - 2, store.Count);
        Assert.DoesNotContain(store, c => c.Id is "avatar_002" or "avatar_029");
        Assert.Equal(local.Select(c => c.Id).Where(id => id is not ("avatar_002" or "avatar_029")), store.Select(c => c.Id));
        Assert.True(store.Select(c => c.Id).ToHashSet().IsSubsetOf(local.Select(c => c.Id).ToHashSet()));
    }

    [Fact]
    public void Excluding_the_first_one_moves_the_generic_avatar_to_the_next()
    {
        var store = CharacterCatalog.BuildFactory(new HashSet<string> { "avatar_001" });
        Assert.Equal("avatar_002", store[0].Id);
        var catalog = new CharacterCatalog(store);
        // un jugador creado en la edición local con un avatar que la Store no trae se ve con el genérico
        Assert.Equal("avatar_002", catalog.GetOrDefault("avatar_001").Id);
    }

    [Fact]
    public void Excluding_everything_still_leaves_one_character()
    {
        var all = Enumerable.Range(1, CharacterCatalog.FactoryCount).Select(i => $"avatar_{i:000}").ToHashSet();
        Assert.Equal(["avatar_001"], CharacterCatalog.BuildFactory(all).Select(c => c.Id));
    }

    [Fact]
    public void The_store_exclusion_list_only_names_real_factory_avatars()
    {
        // valida el archivo store-excluded.txt de verdad (un id mal escrito dejaría pasar un avatar sin licencia)
        var valid = Enumerable.Range(1, CharacterCatalog.FactoryCount).Select(i => $"avatar_{i:000}").ToHashSet();
        var list = CharacterEdition.StoreExcludedList();
        Assert.True(list.IsSubsetOf(valid), "Hay ids en store-excluded.txt que no son avatares de fábrica: "
                                            + string.Join(", ", list.Where(id => !valid.Contains(id))));
    }

    [Fact]
    public void The_running_edition_matches_its_catalog()
    {
        if (CharacterEdition.IsStore)
        {
            Assert.DoesNotContain(CharacterCatalog.Factory, c => CharacterEdition.Excluded.Contains(c.Id));
            Assert.Equal(CharacterCatalog.FactoryCount - CharacterEdition.Excluded.Count, CharacterCatalog.Factory.Count);
        }
        else
        {
            Assert.Empty(CharacterEdition.Excluded);
            Assert.Equal(CharacterCatalog.FactoryCount, CharacterCatalog.Factory.Count);
        }
    }
}
