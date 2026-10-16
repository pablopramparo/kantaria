// SPDX-License-Identifier: GPL-3.0-or-later
using Kantor.Core.Library;
using Kantor.Core.Settings;
using Xunit;

namespace Kantor.Tests;

public class SongSearchTests
{
    private static SongEntry E(string artist, string title, string? genre = null, string? year = null) =>
        new($"c:/{artist}/{title}.txt", title, artist, null, false, genre, year, null, null);

    private static readonly List<SongEntry> Songs =
    [
        E("Queen", "Bohemian Rhapsody", "Rock", "1975"),
        E("Queen", "Don't Stop Me Now", "Rock", "1979"),
        E("Soda Stereo", "Canción Animal", "Rock", "1990"),
        E("Mägo de Oz", "La Danza del Fuego", "Metal", "2000"),
        E("Shakira", "Hips Don't Lie", "Pop", "2006"),
    ];

    [Fact]
    public void EmptyQueryReturnsEverything() => Assert.Equal(5, SongSearch.Filter(Songs, "  ").Count);

    [Theory]
    [InlineData("queen", 2)]
    [InlineData("QUEEN rhap", 1)]
    [InlineData("cancion", 1)]       // sin acento encuentra "Canción"
    [InlineData("canción", 1)]       // con acento encuentra igual
    [InlineData("mago", 1)]          // "Mägo"
    [InlineData("rock", 3)]          // por género
    [InlineData("1979", 1)]          // por año
    [InlineData("zzz", 0)]
    public void MatchesEveryWordIgnoringCaseAndAccents(string query, int expected) =>
        Assert.Equal(expected, SongSearch.Filter(Songs, query).Count);

    [Fact]
    public void WordsCanBeInAnyOrder()
    {
        var r = SongSearch.Filter(Songs, "rhapsody queen");
        Assert.Single(r);
        Assert.Equal("Bohemian Rhapsody", r[0].Title);
    }

    [Fact]
    public void PrefixMatchesComeFirst()
    {
        var songs = new List<SongEntry> { E("The Don", "Mean Song"), E("Zed", "Don Quijote"), E("Abba", "Medley Don") };
        var r = SongSearch.Filter(songs, "don");
        Assert.Equal("Zed", r[0].Artist);      // el título empieza con "don"
        Assert.Equal(["Zed", "Abba", "The Don"], r.Select(s => s.Artist)); // luego los que solo contienen "don", por orden
    }

    [Fact]
    public void SortsByArtistThenTitleIgnoringAccents()
    {
        var sorted = SongSearch.SortByArtistAndTitle([E("Zeta", "B"), E("Álamo", "Z"), E("Alamo", "A")]);
        Assert.Equal(["A", "Z", "B"], sorted.Select(s => s.Title));
    }
}

public class SongHeaderReaderTests
{
    private static string Write(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReadsHeaderAndFindsCoverByConvention()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kantor-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "song.jpg"), [1]);
            var path = Write(dir, "song.txt",
                "#TITLE:Canción\n#ARTIST:Soda Stereo\n#MP3:s.mp3\n#BPM:120\n#GENRE:Rock\n#YEAR:1990\n#VIDEO:s.avi\n: 0 4 0 la\nE\n");
            var e = SongHeaderReader.Read(path)!;
            Assert.Equal("Canción", e.Title);
            Assert.Equal("Soda Stereo", e.Artist);
            Assert.Equal("Rock", e.Genre);
            Assert.True(e.HasVideo);
            Assert.EndsWith("song.jpg", e.CoverPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FilesWithoutBpmOrAudioAreNotSongs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kantor-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(SongHeaderReader.Read(Write(dir, "a.txt", "#TITLE:x\n#MP3:a.mp3\n")));
            Assert.Null(SongHeaderReader.Read(Write(dir, "b.txt", "#TITLE:x\n#BPM:100\n")));
            Assert.Null(SongHeaderReader.Read(Write(dir, "notes.txt", "hola, esto es un texto cualquiera")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ScannerFindsSongsRecursively()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kantor-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "a", "b"));
        try
        {
            Write(Path.Combine(dir, "a"), "one.txt", "#TITLE:Uno\n#ARTIST:B\n#MP3:x.mp3\n#BPM:100\n: 0 1 0 a\nE\n");
            Write(Path.Combine(dir, "a", "b"), "two.txt", "#TITLE:Dos\n#ARTIST:A\n#MP3:y.mp3\n#BPM:100\n: 0 1 0 a\nE\n");
            var list = SongLibraryScanner.Scan([dir, dir]);
            Assert.Equal(["Dos", "Uno"], list.Select(s => s.Title));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LibraryPathsAreNormalized()
    {
        var s = new UserSettings { LibraryPaths = ["  D:/Music ", "d:/music", "", "E:/Otra"] }.Normalized();
        Assert.Equal(["D:/Music", "E:/Otra"], s.LibraryPaths);
        Assert.Null(new UserSettings { LibraryPaths = [" "] }.Normalized().LibraryPaths);
    }
}

public class SongIndexTests
{
    [Fact]
    public void IndexReusesUnchangedFilesAndRereadsModifiedOnes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kantor-idx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "#TITLE:Uno\n#ARTIST:A\n#MP3:x.mp3\n#BPM:100\n: 0 1 0 a\nE\n");

            var first = SongLibraryScanner.ScanIndexed([dir], null);
            var indexFile = Path.Combine(dir, "idx.json");
            SongIndexStore.Save(indexFile, first);
            var loaded = SongIndexStore.Load(indexFile);
            Assert.Single(loaded);
            Assert.Equal("Uno", loaded[0].Entry.Title);

            // Sin cambios: se reutiliza la misma entrada (mismo objeto del índice previo).
            var again = SongLibraryScanner.ScanIndexed([dir], loaded);
            Assert.Same(loaded[0], again[0]);

            // Modificado: se vuelve a leer.
            File.WriteAllText(path, "#TITLE:Uno bis\n#ARTIST:A\n#MP3:x.mp3\n#BPM:100\n: 0 1 0 a\nE\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            var changed = SongLibraryScanner.ScanIndexed([dir], loaded);
            Assert.Equal("Uno bis", changed[0].Entry.Title);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MissingOrCorruptIndexLoadsEmpty()
    {
        Assert.Empty(SongIndexStore.Load(Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid() + ".json")));
        var bad = Path.Combine(Path.GetTempPath(), "bad-" + Guid.NewGuid() + ".json");
        File.WriteAllText(bad, "{ no es json");
        try
        {
            Assert.Empty(SongIndexStore.Load(bad));
        }
        finally
        {
            File.Delete(bad);
        }
    }
}
