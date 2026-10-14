using System.Text;
using Kantor.Core.Songs;

namespace Kantor.Tests;

public class UltraStarParserTests
{
    private const string Mono = """
        #TITLE:Estranged
        #ARTIST:Guns N' Roses
        #MP3:Estranged .mp3
        #BACKGROUND:
        #RELATIVE: No
        #BPM:178,90
        #GAP:13509,00
        #CUSTOMTAG:valor
        : 0 1 7 When
        : 2 1 7  you're
        * 4 1 14  tal
        - 6
        R 8 2 0 yeah
        F 10 2 0  la
        : 12 3 -4  low
        E
        """;

    [Fact]
    public void Reads_header_with_decimal_comma()
    {
        var song = UltraStarParser.Parse(Mono);

        Assert.Equal("Estranged", song.Title);
        Assert.Equal("Guns N' Roses", song.Artist);
        Assert.Equal(178.90, song.Bpm, 3);
        Assert.Equal(13509.0, song.GapMilliseconds, 3);
        Assert.Equal("Estranged .mp3", song.AudioFile);
    }

    [Fact]
    public void Keeps_unknown_tags_in_original_order()
    {
        var song = UltraStarParser.Parse(Mono);

        Assert.Equal("valor", song.Tag("CUSTOMTAG"));
        Assert.Equal(["TITLE", "ARTIST", "MP3", "BACKGROUND", "RELATIVE", "BPM", "GAP", "CUSTOMTAG"],
            song.Tags.Select(t => t.Key));
    }

    [Fact]
    public void Splits_notes_into_lines_and_keeps_kinds_and_negative_tones()
    {
        var voice = Assert.Single(UltraStarParser.Parse(Mono).Voices);

        Assert.Equal(2, voice.Lines.Count);
        Assert.Equal(6, voice.Lines[0].BreakBeat);
        Assert.Equal([NoteKind.Normal, NoteKind.Normal, NoteKind.Golden], voice.Lines[0].Notes.Select(n => n.Kind));
        Assert.Equal([NoteKind.Rap, NoteKind.Freestyle, NoteKind.Normal], voice.Lines[1].Notes.Select(n => n.Kind));
        Assert.Equal(-4, voice.Lines[1].Notes[2].Tone);
        Assert.Null(voice.Lines[1].BreakBeat);
    }

    [Fact]
    public void Note_text_keeps_leading_space_after_single_separator()
    {
        var voice = UltraStarParser.Parse(Mono).Voices[0];

        Assert.Equal("When", voice.Lines[0].Notes[0].Text);
        Assert.Equal(" you're", voice.Lines[0].Notes[1].Text);
        Assert.Equal("When you're tal", voice.Lines[0].Text);
    }

    [Fact]
    public void Freestyle_is_not_scored_and_ignored_for_tone_range()
    {
        var voice = UltraStarParser.Parse(Mono).Voices[0];

        Assert.False(voice.Lines[1].Notes[1].IsScored);
        Assert.Equal(-4, voice.MinTone);
        Assert.Equal(14, voice.MaxTone);
    }

    [Fact]
    public void Duet_markers_produce_one_voice_per_singer()
    {
        const string duet = """
            #TITLE:Duo
            #ARTIST:A
            #BPM:120
            #GAP:0
            P1
            : 0 2 5 Hola
            - 4
            P2
            : 4 2 7 Chau
            - 8
            P1
            : 8 2 5 Otra
            E
            """;

        var song = UltraStarParser.Parse(duet);

        Assert.Equal(2, song.Voices.Count);
        Assert.Equal(["Hola", "Otra"], song.Voices[0].Lines.Select(l => l.Text));
        Assert.Equal(["Chau"], song.Voices[1].Lines.Select(l => l.Text));
    }

    [Fact]
    public void More_than_two_voices_are_supported()
    {
        const string trio = "#TITLE:T\n#ARTIST:A\n#BPM:100\nP1\n: 0 1 1 a\nP2\n: 2 1 1 b\nP3\n: 4 1 1 c\nE\n";

        Assert.Equal(3, UltraStarParser.Parse(trio).Voices.Count);
    }

    [Fact]
    public void Unsupported_content_becomes_warnings_not_failures()
    {
        const string text = "#TITLE:T\n#ARTIST:A\n#BPM:100\n#RELATIVE:YES\n: 0 1 1 a\nB 8 120\n: x y z\nE\n";

        var song = UltraStarParser.Parse(text);

        Assert.Equal(3, song.Warnings.Count);
        Assert.Single(song.Voices[0].Lines);
    }

    [Fact]
    public void Tolerates_crlf_line_endings()
    {
        var song = UltraStarParser.Parse(Mono.Replace("\n", "\r\n"));

        Assert.Equal("Estranged", song.Title);
        Assert.Equal(2, song.Voices[0].Lines.Count);
        Assert.Equal("yeah", song.Voices[0].Lines[1].Notes[0].Text);
    }

    [Fact]
    public void Missing_bpm_is_a_format_error()
    {
        Assert.Throws<SongFormatException>(() => UltraStarParser.Parse("#TITLE:T\n: 0 1 1 a\nE\n"));
    }

    [Fact]
    public void File_without_notes_is_a_format_error()
    {
        Assert.Throws<SongFormatException>(() => UltraStarParser.Parse("#TITLE:T\n#BPM:100\nE\n"));
    }

    [Fact]
    public void Prefers_audio_tag_over_mp3()
    {
        var song = UltraStarParser.Parse("#TITLE:T\n#BPM:100\n#MP3:old.mp3\n#AUDIO:new.ogg\n: 0 1 1 a\nE\n");

        Assert.Equal("new.ogg", song.AudioFile);
    }

    [Fact]
    public void Decode_strips_utf8_bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("#TITLE:Canción")).ToArray();

        Assert.Equal("#TITLE:Canción", UltraStarParser.Decode(bytes));
    }

    [Fact]
    public void Decode_reads_utf8_without_bom()
    {
        Assert.Equal("Canción", UltraStarParser.Decode(Encoding.UTF8.GetBytes("Canción")));
    }

    [Fact]
    public void Decode_falls_back_to_windows_1252_when_not_utf8()
    {
        // "Canción ñandú" en Windows-1252 no es UTF-8 válido.
        var bytes = new byte[] { 0x43, 0x61, 0x6E, 0x63, 0x69, 0xF3, 0x6E, 0x20, 0xF1, 0x61, 0x6E, 0x64, 0xFA };

        Assert.Equal("Canción ñandú", UltraStarParser.Decode(bytes));
    }

    [Fact]
    public void ParseFile_records_song_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kantor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "song.txt");
            File.WriteAllText(path, Mono);

            var song = UltraStarParser.ParseFile(path);

            Assert.Equal(dir, song.Directory);
            Assert.Equal("Estranged", song.Title);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
