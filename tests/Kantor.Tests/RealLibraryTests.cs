// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Songs;
using Xunit.Abstractions;

namespace Kantor.Tests;

/// <summary>Se omite salvo que KANTOR_SONGS_DIR apunte a una carpeta con canciones UltraStar reales.</summary>
public sealed class RealLibraryFactAttribute : FactAttribute
{
    public const string Variable = "KANTOR_SONGS_DIR";

    public RealLibraryFactAttribute()
    {
        var dir = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Skip = $"Definí {Variable} con una carpeta de canciones UltraStar para ejecutar esta prueba.";
        }
    }
}

public class RealLibraryTests(ITestOutputHelper output)
{
    [RealLibraryFact]
    public void Every_real_song_parses_and_names_a_resolvable_audio_file()
    {
        var root = Environment.GetEnvironmentVariable(RealLibraryFactAttribute.Variable)!;
        var failures = new List<string>();
        var malformed = new List<string>();
        var songs = 0;
        var notSongs = 0;

        foreach (var txt in Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories))
        {
            // Readmes y archivos de metadatos conviven con las canciones: sin líneas de nota no son una canción.
            if (!File.ReadLines(txt).Any(l => l.StartsWith(':') || l.StartsWith('*')))
            {
                notSongs++;
                continue;
            }

            songs++;
            try
            {
                var song = UltraStarParser.ParseFile(txt);
                if (song.AudioFile is null || SongMedia.ResolveInside(song.Directory!, song.AudioFile) is null)
                {
                    failures.Add($"{txt}: sin audio resoluble");
                }
            }
            catch (SongFormatException e) when (e.Message.Contains("#BPM"))
            {
                // Archivo realmente incompleto: no hay de dónde inferir el tempo.
                malformed.Add(txt);
            }
            catch (SongFormatException e)
            {
                failures.Add($"{txt}: {e.Message}");
            }
        }

        output.WriteLine($"Canciones: {songs}; otros .txt: {notSongs}; sin #BPM: {malformed.Count}");
        foreach (var path in malformed)
        {
            output.WriteLine($"  sin #BPM: {path}");
        }

        Assert.True(songs > 0, "La carpeta no contiene canciones.");
        Assert.True(failures.Count == 0, $"{failures.Count} de {songs} fallaron:\n" + string.Join('\n', failures.Take(50)));
    }

    [RealLibraryFact]
    public void Singing_every_real_song_perfectly_scores_exactly_10000()
    {
        var root = Environment.GetEnvironmentVariable(RealLibraryFactAttribute.Variable)!;
        var wrong = new List<string>();
        var scored = 0;
        var overlapping = 0;

        // Se recorre un subconjunto determinista (ordenado por ruta) para que la prueba dure segundos.
        var files = Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories).Order().Take(150);
        foreach (var txt in files)
        {
            Song song;
            try
            {
                song = UltraStarParser.ParseFile(txt);
            }
            catch (SongFormatException)
            {
                continue;
            }

            if (song.Voices.Count == 0 || !song.Voices[0].HasNotes || song.Voices[0].Lines.All(l => l.Notes.All(n => !n.IsScored)))
            {
                continue;
            }

            // Con notas solapadas de distinto tono, un solo cantante no puede acertar los dos beats a la vez
            // (WorldParty puntúa cada nota por separado, igual que Kantor): el máximo no es alcanzable.
            if (HasOverlappingNotes(song.Voices[0]))
            {
                overlapping++;
                continue;
            }

            scored++;
            var total = SingerSim.Run(song, new Kantor.Core.Scoring.ScoringRules(), SingerSim.Perfect(song), step: 0.05).Snapshot.Total;
            if (total != 10000)
            {
                wrong.Add($"{total}: {txt}");
            }
        }

        output.WriteLine($"Canciones puntuadas: {scored}; omitidas por notas solapadas: {overlapping}");
        Assert.True(scored > 0);
        Assert.True(wrong.Count == 0, $"{wrong.Count} de {scored} no dieron 10000:\n" + string.Join('\n', wrong.Take(30)));
    }

    private static bool HasOverlappingNotes(Voice voice)
    {
        var notes = voice.Lines.SelectMany(l => l.Notes).Where(n => n.IsScored && n.Length > 0).OrderBy(n => n.StartBeat).ToList();
        var end = int.MinValue;
        foreach (var note in notes)
        {
            if (note.StartBeat < end)
            {
                return true;
            }

            end = Math.Max(end, note.EndBeat);
        }

        return false;
    }
}
