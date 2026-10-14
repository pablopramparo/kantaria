// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Songs;

namespace Kantor.Tests;

public class SongMediaTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "kantor-songs", "Artista - Tema");

    [Fact]
    public void Resolves_file_inside_song_directory()
    {
        var path = SongMedia.ResolveInside(Dir, "Tema con espacios y acentos ñ.mp3");

        Assert.Equal(Path.Combine(Dir, "Tema con espacios y acentos ñ.mp3"), path);
    }

    [Theory]
    [InlineData("..\\otro\\x.mp3")]
    [InlineData("../otro/x.mp3")]
    [InlineData("C:\\Windows\\x.mp3")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_names_that_escape_or_are_empty(string? name)
    {
        Assert.Null(SongMedia.ResolveInside(Dir, name));
    }

    [Fact]
    public void Sibling_folder_with_same_prefix_is_not_inside()
    {
        Assert.Null(SongMedia.ResolveInside(Dir, "..\\Artista - Tema 2\\x.mp3"));
    }
}
