// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Songs;

public enum NoteKind
{
    Normal,
    Golden,
    Rap,
    RapGolden,
    Freestyle,
}

/// <summary>Una nota de la partitura. Los tiempos están en beats de UltraStar (un cuarto del BPM del archivo).</summary>
public readonly record struct Note(NoteKind Kind, int StartBeat, int Length, int Tone, string Text)
{
    public int EndBeat => StartBeat + Length;

    /// <summary>Las notas freestyle se muestran pero nunca puntúan.</summary>
    public bool IsScored => Kind != NoteKind.Freestyle;
}

/// <summary>Una frase: notas que se muestran juntas hasta el siguiente corte de línea.</summary>
public sealed class Line
{
    public Line(IReadOnlyList<Note> notes, int? breakBeat)
    {
        Notes = notes;
        BreakBeat = breakBeat;
    }

    public IReadOnlyList<Note> Notes { get; }

    /// <summary>Beat indicado por la línea "- N" que cierra la frase, si existe.</summary>
    public int? BreakBeat { get; }

    public int StartBeat => Notes[0].StartBeat;

    public int EndBeat => Notes[^1].EndBeat;

    public string Text => string.Concat(Notes.Select(n => n.Text));
}

/// <summary>Una voz cantable. Un tema mono tiene una; un dueto, dos. El modelo no impone máximo.</summary>
public sealed class Voice
{
    public Voice(string name, IReadOnlyList<Line> lines)
    {
        Name = name;
        Lines = lines;
    }

    public string Name { get; }

    public IReadOnlyList<Line> Lines { get; }

    public bool HasNotes => Lines.Count > 0;

    public int MinTone => Lines.SelectMany(l => l.Notes).Where(n => n.IsScored).Select(n => n.Tone).DefaultIfEmpty(0).Min();

    public int MaxTone => Lines.SelectMany(l => l.Notes).Where(n => n.IsScored).Select(n => n.Tone).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Índice de la frase a mostrar en <paramref name="beat"/>: la última que ya empezó,
    /// o la primera si todavía no empezó ninguna. -1 si la voz no tiene frases.
    /// </summary>
    public int LineIndexAt(double beat)
    {
        if (Lines.Count == 0)
        {
            return -1;
        }

        var index = 0;
        for (var i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].StartBeat <= beat)
            {
                index = i;
            }
            else
            {
                break;
            }
        }

        return index;
    }
}

public sealed class Song
{
    public Song(
        string title,
        string artist,
        double bpm,
        double gapMilliseconds,
        string? audioFile,
        IReadOnlyList<Voice> voices,
        IReadOnlyList<KeyValuePair<string, string>> tags,
        IReadOnlyList<string> warnings,
        string? videoFile = null,
        double videoGapSeconds = 0)
    {
        VideoFile = videoFile;
        VideoGapSeconds = videoGapSeconds;
        Title = title;
        Artist = artist;
        Bpm = bpm;
        GapMilliseconds = gapMilliseconds;
        AudioFile = audioFile;
        Voices = voices;
        Tags = tags;
        Warnings = warnings;
    }

    public string Title { get; }

    public string Artist { get; }

    /// <summary>BPM tal como figura en el archivo. El beat de UltraStar dura 60 / (Bpm * 4) segundos.</summary>
    public double Bpm { get; }

    public double GapMilliseconds { get; }

    /// <summary>Nombre de archivo de audio tal como figura en el TXT (#AUDIO, o #MP3 en el formato histórico).</summary>
    public string? AudioFile { get; }

    /// <summary>Nombre del archivo de video tal como figura en el TXT (#VIDEO), si lo hay.</summary>
    public string? VideoFile { get; }

    /// <summary>
    /// #VIDEOGAP en segundos: tiempo del video que corresponde al inicio del audio (el video se muestra en
    /// <c>posición del audio + VideoGapSeconds</c>; si es negativo, el video arranca después).
    /// </summary>
    public double VideoGapSeconds { get; }

    public IReadOnlyList<Voice> Voices { get; }

    /// <summary>Todas las etiquetas de cabecera en el orden original, incluidas las desconocidas.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Tags { get; }

    /// <summary>Advertencias de lectura: datos no soportados o líneas ignoradas.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Carpeta del TXT, cuando la canción se cargó desde un archivo.</summary>
    public string? Directory { get; init; }

    public string? Tag(string name) =>
        Tags.FirstOrDefault(t => string.Equals(t.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
