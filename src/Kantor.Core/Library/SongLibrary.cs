// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kantor.Core.Songs;

namespace Kantor.Core.Library;

/// <summary>Una canción de la biblioteca: lo justo para listarla y buscarla sin leer sus notas.</summary>
public sealed record SongEntry(
    string Path,
    string Title,
    string Artist,
    string? CoverPath,
    bool HasVideo,
    string? Genre,
    string? Year,
    string? Language,
    string? Edition)
{
    /// <summary>Texto normalizado (minúsculas, sin acentos) sobre el que busca <see cref="SongSearch"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SearchText { get; } = SongSearch.Normalize(
        $"{Artist} {Title} {Genre} {Year} {Language} {Edition}");
}

/// <summary>Con qué audio y desde dónde (segundos) sonar la vista previa de una canción.</summary>
public sealed record SongPreviewInfo(string AudioPath, double? StartSeconds, string? VideoPath = null, double VideoGapSeconds = 0);

/// <summary>Lee solo la cabecera (#ETIQUETAS) de un TXT UltraStar: rápido incluso con miles de archivos.</summary>
public static class SongHeaderReader
{
    private static readonly string[] CoverExtensions = [".jpg", ".jpeg", ".png"];

    /// <summary>Las etiquetas (#NOMBRE:valor) del principio del archivo, o <c>null</c> si no se pudo leer.</summary>
    private static Dictionary<string, string>? ReadTags(string path)
    {
        string text;
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[8192];
            var read = stream.Read(buffer, 0, buffer.Length);
            text = UltraStarParser.Decode(buffer.AsSpan(0, read).ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] != '#')
            {
                break; // empiezan las notas
            }

            var colon = line.IndexOf(':');
            if (colon > 1)
            {
                tags[line[1..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return tags;
    }

    /// <summary>
    /// Audio y punto de inicio de la vista previa de una canción (<c>#PREVIEWSTART</c>, en segundos, si lo tiene).
    /// <c>null</c> si el archivo no se puede leer o su audio no está dentro de la carpeta de la canción.
    /// </summary>
    public static SongPreviewInfo? ReadPreview(string path)
    {
        if (ReadTags(path) is not { } tags)
        {
            return null;
        }

        var audio = tags.TryGetValue("AUDIO", out var a) && a.Length > 0 ? a
            : tags.TryGetValue("MP3", out var m) ? m : string.Empty;
        var directory = System.IO.Path.GetDirectoryName(path)!;
        var resolved = SongMedia.ResolveInside(directory, audio.Length == 0 ? null : audio);
        if (resolved is null || !File.Exists(resolved))
        {
            return null;
        }

        double? start = null;
        if (tags.TryGetValue("PREVIEWSTART", out var raw)
            && double.TryParse(raw.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            start = seconds;
        }

        // video (opcional) con su #VIDEOGAP, para mostrarlo junto a la vista previa de audio
        string? videoPath = null;
        if (tags.TryGetValue("VIDEO", out var video) && video.Length > 0
            && SongMedia.ResolveInside(directory, video) is { } resolvedVideo && File.Exists(resolvedVideo))
        {
            videoPath = resolvedVideo;
        }

        var gap = 0.0;
        if (tags.TryGetValue("VIDEOGAP", out var rawGap))
        {
            double.TryParse(rawGap.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out gap);
        }

        return new SongPreviewInfo(resolved, start, videoPath, gap);
    }

    /// <summary>Devuelve la entrada, o <c>null</c> si el archivo no parece una canción utilizable.</summary>
    public static SongEntry? Read(string path)
    {
        if (ReadTags(path) is not { } tags)
        {
            return null;
        }

        string Tag(string name) => tags.TryGetValue(name, out var v) ? v : string.Empty;

        var audio = Tag("AUDIO").Length > 0 ? Tag("AUDIO") : Tag("MP3");
        if (Tag("BPM").Length == 0 || audio.Length == 0)
        {
            return null;
        }

        var directory = System.IO.Path.GetDirectoryName(path)!;
        var baseName = System.IO.Path.GetFileNameWithoutExtension(path);
        var title = Tag("TITLE").Length > 0 ? Tag("TITLE") : baseName;
        var artist = Tag("ARTIST");

        string? Optional(string name) => Tag(name).Length > 0 ? Tag(name) : null;

        return new SongEntry(path, title, artist, FindCover(directory, baseName, Tag("COVER")),
            Tag("VIDEO").Length > 0, Optional("GENRE"), Optional("YEAR"), Optional("LANGUAGE"), Optional("EDITION"));
    }

    private static string? FindCover(string directory, string baseName, string coverTag)
    {
        var declared = SongMedia.ResolveInside(directory, coverTag.Length == 0 ? null : coverTag);
        if (declared is not null && File.Exists(declared))
        {
            return declared;
        }

        foreach (var extension in CoverExtensions)
        {
            foreach (var name in new[] { baseName + extension, baseName + " [CO]" + extension })
            {
                var candidate = System.IO.Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

/// <summary>Una canción del índice con la "firma" del archivo con la que se leyó (fecha y tamaño).</summary>
public sealed record IndexItem(string Path, long ModifiedTicks, long Length, SongEntry Entry);

/// <summary>Índice persistente de la biblioteca: evita volver a leer cada archivo en cada arranque.</summary>
public static class SongIndexStore
{
    private const int Version = 1;

    private sealed record Document(int Version, List<IndexItem> Items);

    public static List<IndexItem> Load(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var doc = System.Text.Json.JsonSerializer.Deserialize<Document>(stream);
            return doc is { } d && d.Version == Version ? d.Items ?? [] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                      or NotSupportedException)
        {
            return []; // sin índice o dañado: se reconstruye
        }
    }

    public static void Save(string file, IEnumerable<IndexItem> items)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(new Document(Version, items.ToList())));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // el índice es una optimización: si no se puede guardar, no pasa nada
        }
    }
}

/// <summary>Recorre carpetas buscando canciones UltraStar.</summary>
public static class SongLibraryScanner
{
    public static List<SongEntry> Scan(IEnumerable<string> roots, IProgress<int>? progress = null,
        CancellationToken cancellation = default) =>
        SongSearch.SortByArtistAndTitle(ScanIndexed(roots, null, progress, cancellation).Select(i => i.Entry));

    /// <summary>
    /// Igual que <see cref="Scan"/> pero devuelve el índice y reutiliza lo ya leído de <paramref name="previous"/>
    /// cuando el archivo no cambió (misma fecha y tamaño): solo se abren los archivos nuevos o modificados.
    /// </summary>
    public static List<IndexItem> ScanIndexed(IEnumerable<string> roots, IEnumerable<IndexItem>? previous,
        IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        var known = (previous ?? []).GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, IndexItem>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var info in new DirectoryInfo(root).EnumerateFiles("*.txt", options))
            {
                cancellation.ThrowIfCancellationRequested();
                var file = info.FullName;
                if (found.ContainsKey(file))
                {
                    continue;
                }

                var ticks = info.LastWriteTimeUtc.Ticks;
                if (known.TryGetValue(file, out var old) && old.ModifiedTicks == ticks && old.Length == info.Length)
                {
                    found[file] = old;
                }
                else if (SongHeaderReader.Read(file) is { } entry)
                {
                    found[file] = new IndexItem(file, ticks, info.Length, entry);
                }
                else
                {
                    continue;
                }

                if (found.Count % 50 == 0)
                {
                    progress?.Report(found.Count);
                }
            }
        }

        progress?.Report(found.Count);
        return [.. found.Values];
    }
}

/// <summary>Búsqueda y orden de la lista de canciones.</summary>
public static class SongSearch
{
    /// <summary>Minúsculas y sin diacríticos: "Canción" y "cancion" son lo mismo.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    public static List<SongEntry> SortByArtistAndTitle(IEnumerable<SongEntry> entries) =>
        entries.OrderBy(e => Normalize(e.Artist), StringComparer.Ordinal)
            .ThenBy(e => Normalize(e.Title), StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Todas las palabras de la consulta deben aparecer (en cualquier orden) en artista, título, género, año,
    /// idioma o edición. Los resultados que empiezan con la consulta en el artista o el título van primero.
    /// Consulta vacía: todo, ordenado por artista y título.
    /// </summary>
    public static List<SongEntry> Filter(IReadOnlyList<SongEntry> entries, string? query)
    {
        var tokens = Normalize(query ?? string.Empty)
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return [.. entries];
        }

        return entries
            .Where(e => tokens.All(t => e.SearchText.Contains(t, StringComparison.Ordinal)))
            .OrderBy(e => Rank(e, tokens))
            .ThenBy(e => Normalize(e.Artist), StringComparer.Ordinal)
            .ThenBy(e => Normalize(e.Title), StringComparer.Ordinal)
            .ToList();
    }

    internal static int Rank(SongEntry entry, string[] tokens)
    {
        var artist = Normalize(entry.Artist);
        var title = Normalize(entry.Title);
        var first = tokens[0];
        if (artist.StartsWith(first, StringComparison.Ordinal) || title.StartsWith(first, StringComparison.Ordinal))
        {
            return 0;
        }

        return artist.Contains(first, StringComparison.Ordinal) || title.Contains(first, StringComparison.Ordinal) ? 1 : 2;
    }
}
