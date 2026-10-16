// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using System.Text;

namespace Kantor.Core.Songs;

public sealed class SongFormatException : Exception
{
    public SongFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// Lector de TXT UltraStar. Cubre lo que traen las bibliotecas reales: cabecera con coma decimal,
/// BOM, UTF-8 o Windows-1252, notas normales/doradas/rap/freestyle, cortes de frase y duetos (P1/P2).
/// Lo que no cubre (cambios de BPM, modo relativo) se informa como advertencia en lugar de fallar.
/// </summary>
public static class UltraStarParser
{
    public static Song ParseFile(string path)
    {
        var text = Decode(File.ReadAllBytes(path));
        var song = Parse(text);
        return new Song(song.Title, song.Artist, song.Bpm, song.GapMilliseconds, song.AudioFile,
            song.Voices, song.Tags, song.Warnings, song.VideoFile, song.VideoGapSeconds)
        {
            Directory = Path.GetDirectoryName(Path.GetFullPath(path)),
        };
    }

    /// <summary>BOM → UTF-8; sin BOM, UTF-8 estricto; si no es UTF-8 válido, Windows-1252.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(bytes);
        }
    }

    public static Song Parse(string text)
    {
        var tags = new List<KeyValuePair<string, string>>();
        var warnings = new List<string>();
        var voices = new List<VoiceBuilder>();
        var current = GetVoice(voices, 0);
        var lineNumber = 0;
        var ended = false;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            if (ended)
            {
                break;
            }

            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            switch (line[0])
            {
                case '#':
                    ParseTag(line, tags);
                    break;

                case ':':
                case '*':
                case 'F':
                case 'R':
                case 'G':
                    if (TryParseNote(line, out var note))
                    {
                        current.Pending.Add(note);
                    }
                    else
                    {
                        warnings.Add($"Línea {lineNumber}: nota ilegible, se ignora.");
                    }

                    break;

                case '-':
                    current.CloseLine(TryParseLeadingInt(line.AsSpan(1), out var breakBeat) ? breakBeat : null);
                    break;

                case 'E':
                    ended = true;
                    break;

                case 'P':
                    if (int.TryParse(line.AsSpan(1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p >= 1)
                    {
                        current.CloseLine(null);
                        current = GetVoice(voices, p - 1);
                    }
                    else
                    {
                        warnings.Add($"Línea {lineNumber}: marcador de voz no reconocido '{line}'.");
                    }

                    break;

                case 'B':
                    warnings.Add($"Línea {lineNumber}: cambio de BPM no soportado, se ignora.");
                    break;

                default:
                    warnings.Add($"Línea {lineNumber}: contenido no reconocido, se ignora.");
                    break;
            }
        }

        foreach (var v in voices)
        {
            v.CloseLine(null);
        }

        string Tag(string name) =>
            tags.FirstOrDefault(t => string.Equals(t.Key, name, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;

        if (!TryParseDouble(Tag("BPM"), out var bpm) || bpm <= 0)
        {
            throw new SongFormatException("El archivo no tiene un #BPM válido.");
        }

        var gap = 0.0;
        if (Tag("GAP").Length > 0 && !TryParseDouble(Tag("GAP"), out gap))
        {
            warnings.Add("#GAP no es un número; se usa 0.");
            gap = 0;
        }

        if (string.Equals(Tag("RELATIVE"), "YES", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("#RELATIVE:YES no está soportado; los tiempos de las notas pueden ser incorrectos.");
        }

        var audio = Tag("AUDIO");
        if (audio.Length == 0)
        {
            audio = Tag("MP3");
        }

        var videoGap = 0.0;
        if (Tag("VIDEOGAP").Length > 0 && !TryParseDouble(Tag("VIDEOGAP"), out videoGap))
        {
            warnings.Add("#VIDEOGAP no es un número; se usa 0.");
            videoGap = 0;
        }

        var video = Tag("VIDEO");

        var built = voices
            .Select((v, i) => new Voice(v.Name ?? $"P{i + 1}", v.Lines))
            .Where(v => v.HasNotes)
            .ToList();

        if (built.Count == 0)
        {
            throw new SongFormatException("El archivo no contiene notas.");
        }

        return new Song(Tag("TITLE"), Tag("ARTIST"), bpm, gap, audio.Length == 0 ? null : audio, built, tags, warnings,
            video.Length == 0 ? null : video, videoGap);
    }

    private static VoiceBuilder GetVoice(List<VoiceBuilder> voices, int index)
    {
        while (voices.Count <= index)
        {
            voices.Add(new VoiceBuilder { Name = $"P{voices.Count + 1}" });
        }

        return voices[index];
    }

    private static void ParseTag(string line, List<KeyValuePair<string, string>> tags)
    {
        var colon = line.IndexOf(':');
        if (colon < 0)
        {
            return;
        }

        var name = line[1..colon].Trim();
        if (name.Length == 0)
        {
            return;
        }

        tags.Add(new KeyValuePair<string, string>(name, line[(colon + 1)..].Trim()));
    }

    /// <summary>"T inicio duración tono texto". El texto conserva el espacio inicial salvo el separador.</summary>
    private static bool TryParseNote(string line, out Note note)
    {
        note = default;
        var kind = line[0] switch
        {
            ':' => NoteKind.Normal,
            '*' => NoteKind.Golden,
            'R' => NoteKind.Rap,
            'G' => NoteKind.RapGolden,
            _ => NoteKind.Freestyle,
        };

        var pos = 1;
        if (!ReadInt(line, ref pos, out var start) ||
            !ReadInt(line, ref pos, out var length) ||
            !ReadInt(line, ref pos, out var tone))
        {
            return false;
        }

        if (pos < line.Length && line[pos] == ' ')
        {
            pos++;
        }

        note = new Note(kind, start, length, tone, line[pos..]);
        return true;
    }

    private static bool ReadInt(string s, ref int pos, out int value)
    {
        while (pos < s.Length && s[pos] == ' ')
        {
            pos++;
        }

        var begin = pos;
        if (pos < s.Length && (s[pos] == '-' || s[pos] == '+'))
        {
            pos++;
        }

        while (pos < s.Length && char.IsAsciiDigit(s[pos]))
        {
            pos++;
        }

        return int.TryParse(s.AsSpan(begin, pos - begin), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseLeadingInt(ReadOnlySpan<char> s, out int value)
    {
        s = s.TrimStart();
        var end = 0;
        if (end < s.Length && (s[end] == '-' || s[end] == '+'))
        {
            end++;
        }

        while (end < s.Length && char.IsAsciiDigit(s[end]))
        {
            end++;
        }

        return int.TryParse(s[..end], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private sealed class VoiceBuilder
    {
        public string? Name { get; init; }

        public List<Note> Pending { get; } = [];

        public List<Line> Lines { get; } = [];

        public void CloseLine(int? breakBeat)
        {
            if (Pending.Count == 0)
            {
                return;
            }

            Lines.Add(new Line(Pending.ToArray(), breakBeat));
            Pending.Clear();
        }
    }
}
