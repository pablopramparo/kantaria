// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Songs;
using Xunit;

namespace Kantor.Tests;

/// <summary>El pack de canciones de demostración (samples/kantor-demo-songs) que se enlaza en las notas de certificación.</summary>
public class DemoSongPackTests
{
    private static string PackDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "kantor-demo-songs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("No se encontró samples/kantor-demo-songs.");
    }

    public static IEnumerable<object[]> Songs() =>
        Directory.GetDirectories(PackDirectory()).OrderBy(d => d).Select(d => new object[] { Path.GetFileName(d) });

    [Fact]
    public void The_pack_has_a_single_voice_song_a_dueto_and_more()
    {
        var names = Songs().Select(s => (string)s[0]).ToList();
        Assert.Equal(3, names.Count);
        Assert.All(names, n => Assert.StartsWith("Kantor Demo - ", n, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Songs))]
    public void Every_demo_song_parses_and_points_to_real_audio_and_cover(string folder)
    {
        var dir = Path.Combine(PackDirectory(), folder);
        var txt = Path.Combine(dir, folder + ".txt");
        var song = UltraStarParser.ParseFile(txt);

        Assert.Equal(120, song.Bpm);
        Assert.Empty(song.Warnings);
        Assert.NotEmpty(song.Voices);
        Assert.All(song.Voices, v => Assert.NotEmpty(v.Lines));
        var audio = SongMedia.ResolveInside(song.Directory!, song.AudioFile);
        Assert.NotNull(audio);
        Assert.True(File.Exists(audio), audio);
        Assert.True(File.Exists(Path.Combine(dir, folder + ".jpg")));
        Assert.Equal(folder.Contains("Duo", StringComparison.Ordinal) ? 2 : 1, song.Voices.Count);
    }

    [Theory]
    [MemberData(nameof(Songs))]
    public void The_audio_is_a_valid_wav_as_long_as_the_song(string folder)
    {
        var dir = Path.Combine(PackDirectory(), folder);
        var song = UltraStarParser.ParseFile(Path.Combine(dir, folder + ".txt"));
        using var reader = new WavInfo(Path.Combine(dir, folder + ".wav"));
        var lastBeat = song.Voices.SelectMany(v => v.Lines).SelectMany(l => l.Notes).Max(n => n.StartBeat + n.Length);
        var songSeconds = song.GapMilliseconds / 1000.0 + lastBeat / (song.Bpm * 4.0 / 60.0);
        Assert.True(reader.Seconds >= songSeconds, $"audio {reader.Seconds:0.0} s < canción {songSeconds:0.0} s");
    }

    /// <summary>Lector mínimo de WAV PCM 16 bits mono (sin dependencias): alcanza para medir la duración.</summary>
    private sealed class WavInfo : IDisposable
    {
        public WavInfo(string path)
        {
            using var s = File.OpenRead(path);
            using var r = new BinaryReader(s);
            Assert.Equal("RIFF", new string(r.ReadChars(4)));
            r.ReadInt32();
            Assert.Equal("WAVE", new string(r.ReadChars(4)));
            Assert.Equal("fmt ", new string(r.ReadChars(4)));
            r.ReadInt32();
            Assert.Equal(1, r.ReadInt16()); // PCM
            var channels = r.ReadInt16();
            var rate = r.ReadInt32();
            r.ReadInt32();
            r.ReadInt16();
            var bits = r.ReadInt16();
            Assert.Equal("data", new string(r.ReadChars(4)));
            var bytes = r.ReadInt32();
            Seconds = bytes / (double)(rate * channels * (bits / 8));
        }

        public double Seconds { get; }

        public void Dispose()
        {
        }
    }
}
