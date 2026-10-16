// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Data;

namespace Kantor.Core.Library;

/// <summary>
/// Una entrada concreta de la biblioteca (un archivo, con su edición) con su identificador estable y la identidad
/// de la canción (artista + título) que comparte con otras ediciones.
/// </summary>
/// <param name="CoverId">Identifica la imagen de portada de ESTA entrada (ruta, fecha y tamaño); <c>null</c> si no tiene.</param>
public sealed record LibraryItem(string EntryId, SongEntry Entry, string SongIdentity, string? CoverId);

/// <summary>Foto inmutable de la biblioteca: se puede leer desde cualquier hilo sin bloqueos.</summary>
public sealed class LibrarySnapshot
{
    public static readonly LibrarySnapshot Empty = new(0, []);

    private readonly Dictionary<string, LibraryItem> _byId;
    private readonly Dictionary<string, LibraryItem> _byPath;

    public LibrarySnapshot(int rev, IReadOnlyList<LibraryItem> items)
    {
        Rev = rev;
        Items = items;
        _byId = new Dictionary<string, LibraryItem>(StringComparer.Ordinal);
        _byPath = new Dictionary<string, LibraryItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            _byId[item.EntryId] = item;
            _byPath[LibraryEntryKeys.KeyOf(item.Entry.Path)] = item;
        }
    }

    /// <summary>Cambia cuando el contenido cambia (no por cada escaneo que da lo mismo).</summary>
    public int Rev { get; }

    public IReadOnlyList<LibraryItem> Items { get; }

    public LibraryItem? Find(string entryId) => _byId.GetValueOrDefault(entryId);

    public LibraryItem? FindByPath(string path) => _byPath.GetValueOrDefault(LibraryEntryKeys.KeyOf(path));
}

/// <summary>
/// La biblioteca vigente para el control remoto. Es el mismo índice que muestra la lista local: no hay otra
/// biblioteca paralela; solo se le suma el identificador de cada entrada.
/// </summary>
public sealed class LibraryCatalog
{
    private volatile LibrarySnapshot _current = LibrarySnapshot.Empty;

    public LibrarySnapshot Current => _current;

    /// <summary>La biblioteca cambió de contenido.</summary>
    public event Action? Changed;

    /// <summary>Estado del escaneo en segundo plano (para publicarlo).</summary>
    public volatile bool Scanning;

    /// <summary>Cantidad de carpetas configuradas (para publicarlo).</summary>
    public volatile int FolderCount;

    /// <summary>
    /// Arma los elementos a partir del índice y de los identificadores asignados (ruta normalizada → id).
    /// <paramref name="coverStamp"/> devuelve una marca de la portada de una ruta (p. ej. fecha y tamaño) o
    /// <c>null</c>; sirve para que la caché del cliente sepa cuándo cambió la imagen.
    /// </summary>
    public static List<LibraryItem> Build(IEnumerable<SongEntry> entries, IReadOnlyDictionary<string, string> idsByKey,
        Func<string, string?>? coverStamp = null)
    {
        var items = new List<LibraryItem>();
        foreach (var entry in SongSearch.SortByArtistAndTitle(entries))
        {
            if (!idsByKey.TryGetValue(LibraryEntryKeys.KeyOf(entry.Path), out var id))
            {
                continue; // sin identificador no se puede direccionar: no se publica
            }

            string? cover = null;
            if (entry.CoverPath is { } coverPath && coverStamp?.Invoke(coverPath) is { } stamp)
            {
                cover = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(LibraryEntryKeys.KeyOf(coverPath) + "|" + stamp)))[..16].ToLowerInvariant();
            }

            items.Add(new LibraryItem(id, entry, Data.SongIdentity.For(entry.Artist, entry.Title), cover));
        }

        return items;
    }

    /// <summary>Reemplaza la biblioteca. Devuelve <c>false</c> (y no avanza la revisión) si el contenido es el mismo.</summary>
    public bool Replace(IReadOnlyList<LibraryItem> items)
    {
        var previous = _current;
        if (previous.Items.Count == items.Count && previous.Items.SequenceEqual(items))
        {
            return false;
        }

        _current = new LibrarySnapshot(previous.Rev + 1, items);
        Changed?.Invoke();
        return true;
    }
}

/// <summary>Identificadores de entrada sin base de datos: derivados de la ruta (estables mientras no se mueva el archivo).</summary>
public static class LibraryIds
{
    public static string FromPath(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(LibraryEntryKeys.KeyOf(path))))[..32].ToLowerInvariant();
}

public enum LibrarySort
{
    Artist,
    Title,
}

public sealed record LibraryQuery(
    string? Text = null,
    string? SongIdentity = null,
    string? Genre = null,
    string? Year = null,
    string? Language = null,
    LibrarySort Sort = LibrarySort.Artist);

/// <summary>Un valor de una faceta con la cantidad de entradas que lo tienen.</summary>
public sealed record FacetValue(string Value, int Count);

public sealed record LibraryFacets(
    IReadOnlyList<FacetValue> Genres, IReadOnlyList<FacetValue> Years, IReadOnlyList<FacetValue> Languages);

/// <summary>Búsqueda sobre una foto de la biblioteca, con las mismas reglas que la lista local.</summary>
public static class LibrarySearch
{
    /// <summary>
    /// Todas las entradas que cumplen, en orden estable: coincidencia con el inicio (solo con texto), luego el
    /// orden pedido, luego artista, título, edición e identificador.
    /// </summary>
    public static IReadOnlyList<LibraryItem> Run(LibrarySnapshot snapshot, LibraryQuery query)
    {
        var tokens = SongSearch.Normalize(query.Text ?? string.Empty)
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var genre = Norm(query.Genre);
        var year = Norm(query.Year);
        var language = Norm(query.Language);

        IEnumerable<LibraryItem> items = snapshot.Items;
        if (query.SongIdentity is { Length: > 0 } identity)
        {
            items = items.Where(i => string.Equals(i.SongIdentity, identity, StringComparison.OrdinalIgnoreCase));
        }

        if (genre is not null)
        {
            items = items.Where(i => Norm(i.Entry.Genre) == genre);
        }

        if (year is not null)
        {
            items = items.Where(i => Norm(i.Entry.Year) == year);
        }

        if (language is not null)
        {
            items = items.Where(i => Norm(i.Entry.Language) == language);
        }

        if (tokens.Length > 0)
        {
            items = items.Where(i => tokens.All(t => i.Entry.SearchText.Contains(t, StringComparison.Ordinal)));
        }

        var ordered = items
            .OrderBy(i => tokens.Length > 0 ? SongSearch.Rank(i.Entry, tokens) : 0)
            .ThenBy(i => query.Sort == LibrarySort.Title ? SongSearch.Normalize(i.Entry.Title) : SongSearch.Normalize(i.Entry.Artist),
                StringComparer.Ordinal)
            .ThenBy(i => SongSearch.Normalize(i.Entry.Artist), StringComparer.Ordinal)
            .ThenBy(i => SongSearch.Normalize(i.Entry.Title), StringComparer.Ordinal)
            .ThenBy(i => SongSearch.Normalize(i.Entry.Edition ?? string.Empty), StringComparer.Ordinal)
            .ThenBy(i => i.EntryId, StringComparer.Ordinal);
        return [.. ordered];
    }

    /// <summary>Géneros, años e idiomas con su cantidad de entradas (agrupados sin distinguir mayúsculas ni acentos).</summary>
    public static LibraryFacets Facets(LibrarySnapshot snapshot) => new(
        Facet(snapshot.Items.Select(i => i.Entry.Genre)),
        Facet(snapshot.Items.Select(i => i.Entry.Year)),
        Facet(snapshot.Items.Select(i => i.Entry.Language)));

    private static IReadOnlyList<FacetValue> Facet(IEnumerable<string?> values) =>
        [.. values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => SongSearch.Normalize(v!.Trim()), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new FacetValue(
                g.GroupBy(v => v!.Trim()).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal)
                    .First().Key,
                g.Count()))];

    private static string? Norm(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : SongSearch.Normalize(value.Trim());
}
