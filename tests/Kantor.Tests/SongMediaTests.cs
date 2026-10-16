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

public class VideoTagTests
{
    private const string Notes = "#TITLE:T\n#ARTIST:A\n#BPM:120\n#GAP:0\n: 0 4 0 la\nE\n";

    [Fact]
    public void ParsesVideoAndVideoGap()
    {
        var song = Kantor.Core.Songs.UltraStarParser.Parse("#VIDEO:clip.avi\n#VIDEOGAP:-1,5\n" + Notes);
        Assert.Equal("clip.avi", song.VideoFile);
        Assert.Equal(-1.5, song.VideoGapSeconds);
    }

    [Fact]
    public void NoVideoTagsMeansNoVideoAndZeroGap()
    {
        var song = Kantor.Core.Songs.UltraStarParser.Parse(Notes);
        Assert.Null(song.VideoFile);
        Assert.Equal(0, song.VideoGapSeconds);
    }

    [Fact]
    public void InvalidVideoGapWarnsAndUsesZero()
    {
        var song = Kantor.Core.Songs.UltraStarParser.Parse("#VIDEO:x.mp4\n#VIDEOGAP:abc\n" + Notes);
        Assert.Equal(0, song.VideoGapSeconds);
        Assert.Contains(song.Warnings, w => w.Contains("VIDEOGAP"));
    }
}

public class VideoSyncTests
{
    [Fact]
    public void TargetIsAudioPositionPlusGap()
    {
        Assert.Equal(TimeSpan.FromSeconds(13), Kantor.Core.Songs.VideoSync.Target(TimeSpan.FromSeconds(10), 3));
        Assert.Equal(TimeSpan.FromSeconds(8.5), Kantor.Core.Songs.VideoSync.Target(TimeSpan.FromSeconds(10), -1.5));
    }

    [Fact]
    public void NegativeTargetMeansTheVideoHasNotStarted()
    {
        var t = Kantor.Core.Songs.VideoSync.Target(TimeSpan.FromSeconds(0.5), -2);
        Assert.False(Kantor.Core.Songs.VideoSync.HasStarted(t));
        Assert.True(Kantor.Core.Songs.VideoSync.HasStarted(TimeSpan.Zero));
    }

    [Theory]
    [InlineData(10.0, 10.2, false)]
    [InlineData(10.0, 10.4, true)]
    [InlineData(10.4, 10.0, true)]
    public void CorrectsOnlyBeyondTolerance(double video, double target, bool expected) =>
        Assert.Equal(expected, Kantor.Core.Songs.VideoSync.NeedsCorrection(
            TimeSpan.FromSeconds(video), TimeSpan.FromSeconds(target)));
}

public class VideoTagFileTests
{
    [Fact]
    public void ParseFileKeepsVideoFieldsAndDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kantor-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "s.txt");
            File.WriteAllText(path, "#TITLE:T\n#ARTIST:A\n#BPM:120\n#GAP:0\n#VIDEO:clip.avi\n#VIDEOGAP:2,5\n: 0 4 0 la\nE\n");
            var song = Kantor.Core.Songs.UltraStarParser.ParseFile(path);
            Assert.Equal("clip.avi", song.VideoFile);
            Assert.Equal(2.5, song.VideoGapSeconds);
            Assert.Equal(dir, song.Directory);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class LyricCueTests
{
    private static Kantor.Core.Songs.Voice Voice() => new("P1",
    [
        new Kantor.Core.Songs.Line([new Kantor.Core.Songs.Note(Kantor.Core.Songs.NoteKind.Normal, 10, 4, 0, "uno ")], null),
        new Kantor.Core.Songs.Line([new Kantor.Core.Songs.Note(Kantor.Core.Songs.NoteKind.Normal, 40, 4, 0, "dos")], null),
    ]);

    [Theory]
    [InlineData(3.0, null)]      // todavía lejos
    [InlineData(2.0, 0.0)]       // aparece
    [InlineData(1.0, 0.5)]       // a mitad de camino
    [InlineData(0.0, null)]      // ya empezó
    [InlineData(-1.0, null)]
    public void ApproachGoesFromZeroToOneInsideTheWindow(double seconds, double? expected)
    {
        var p = Kantor.Core.Songs.LyricCue.Approach(seconds);
        if (expected is null) { Assert.Null(p); }
        else { Assert.Equal(expected.Value, p!.Value, 6); }
    }

    [Fact]
    public void SungFractionIsClamped()
    {
        Assert.Equal(0, Kantor.Core.Songs.LyricCue.Sung(5, 10, 20));
        Assert.Equal(0.5, Kantor.Core.Songs.LyricCue.Sung(15, 10, 20));
        Assert.Equal(1, Kantor.Core.Songs.LyricCue.Sung(30, 10, 20));
    }

    [Fact]
    public void UpcomingIsTheFirstLineBeforeSingingStartsThenTheNextOne()
    {
        var voice = Voice();
        Assert.Equal(0, Kantor.Core.Songs.LyricCue.UpcomingIndex(voice, 2));    // antes de la primera
        Assert.Equal(1, Kantor.Core.Songs.LyricCue.UpcomingIndex(voice, 12));   // cantando la primera: viene la segunda
        Assert.Equal(1, Kantor.Core.Songs.LyricCue.UpcomingIndex(voice, 30));   // pausa entre frases
        Assert.Equal(-1, Kantor.Core.Songs.LyricCue.UpcomingIndex(voice, 41));  // ya canta la última
    }
}

public class LyricFillTests
{
    private static readonly (double, double)[] Notes = [(10, 14), (14, 18), (24, 28)];
    private static readonly Kantor.Core.Songs.LyricCue.Span[] Spans =
    [
        new(100, 200), new(200, 260), new(270, 400),
    ];

    [Theory]
    [InlineData(5, 100)]     // antes de empezar: en el inicio del texto
    [InlineData(12, 150)]    // mitad de la primera sílaba
    [InlineData(14, 200)]    // la primera terminó
    [InlineData(16, 230)]    // mitad de la segunda
    [InlineData(20, 260)]    // pausa entre sílabas: se queda quieto
    [InlineData(26, 335)]    // mitad de la tercera
    [InlineData(40, 400)]    // frase terminada: entera
    public void FillFollowsEachSyllablesRealTiming(double beat, double expected) =>
        Assert.Equal(expected, Kantor.Core.Songs.LyricCue.FillX(Notes, Spans, beat, textStart: 100), 6);

    [Fact]
    public void DisplayedLineAdvancesOnlyAfterThePhraseEnds()
    {
        var voice = new Kantor.Core.Songs.Voice("P1",
        [
            new Kantor.Core.Songs.Line([new Kantor.Core.Songs.Note(Kantor.Core.Songs.NoteKind.Normal, 10, 4, 0, "uno")], null),
            new Kantor.Core.Songs.Line([new Kantor.Core.Songs.Note(Kantor.Core.Songs.NoteKind.Normal, 40, 4, 0, "dos")], null),
        ]);
        Assert.Equal(0, Kantor.Core.Songs.LyricCue.DisplayIndex(voice, 12, 2));  // cantando la primera
        Assert.Equal(0, Kantor.Core.Songs.LyricCue.DisplayIndex(voice, 15, 2));  // terminó hace 1 beat: sigue arriba
        Assert.Equal(1, Kantor.Core.Songs.LyricCue.DisplayIndex(voice, 17, 2));  // ya pasó la espera: arriba la que viene
        Assert.Equal(1, Kantor.Core.Songs.LyricCue.DisplayIndex(voice, 41, 2));  // cantando la segunda
    }
}
