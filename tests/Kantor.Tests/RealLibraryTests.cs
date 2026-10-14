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
}
