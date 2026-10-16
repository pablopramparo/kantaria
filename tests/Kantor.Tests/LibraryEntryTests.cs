// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Data;
using Kantor.Core.Library;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kantor.Tests;

public sealed class LibraryEntryTests : IDisposable
{
    private readonly List<string> _files = [];
    private readonly TickingClock _clock = new();

    public void Dispose()
    {
        foreach (var file in _files.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    private string TempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kantor-lib-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    private static LibraryFile F(string path, long ticks = 1, long length = 100) => new(path, ticks, length);

    /// <summary>Hashes y existencia simulados: una "carpeta" en memoria.</summary>
    private sealed class FakeDisk
    {
        public Dictionary<string, string> Content { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Hash(string path) => Content.TryGetValue(path, out var c) ? "h:" + c : null;

        public bool Exists(string path) => Content.ContainsKey(path);
    }

    private static string Key(string path) => LibraryEntryKeys.KeyOf(path);

    [Fact]
    public void The_same_file_keeps_its_id_across_scans_edits_and_path_case()
    {
        var disk = new FakeDisk();
        disk.Content[@"C:\Songs\A\a.txt"] = "uno";
        using var store = new KantorStore(":memory:", _clock.Next);

        var first = store.SyncLibraryEntries([F(@"C:\Songs\A\a.txt")], disk.Hash, disk.Exists);
        disk.Content[@"C:\Songs\A\a.txt"] = "editado"; // se retocó el GAP
        var second = store.SyncLibraryEntries([F(@"C:\Songs\A\a.txt", ticks: 2)], disk.Hash, disk.Exists);
        var third = store.SyncLibraryEntries([F(@"c:\SONGS\a\A.TXT", ticks: 2)], disk.Hash, disk.Exists);

        var id = first[Key(@"C:\Songs\A\a.txt")];
        Assert.Equal(id, second[Key(@"C:\Songs\A\a.txt")]);
        Assert.Equal(id, third[Key(@"c:\songs\a\a.txt")]);
        Assert.Single(store.ListLibraryEntries());
    }

    [Fact]
    public void A_moved_file_with_identical_content_inherits_the_id_of_the_one_that_disappeared()
    {
        var disk = new FakeDisk();
        disk.Content[@"C:\Songs\old\a.txt"] = "contenido-a";
        disk.Content[@"C:\Songs\b.txt"] = "contenido-b";
        using var store = new KantorStore(":memory:", _clock.Next);
        var before = store.SyncLibraryEntries([F(@"C:\Songs\old\a.txt"), F(@"C:\Songs\b.txt")], disk.Hash, disk.Exists);

        disk.Content.Remove(@"C:\Songs\old\a.txt");
        disk.Content[@"C:\Songs\new\a.txt"] = "contenido-a";
        var after = store.SyncLibraryEntries([F(@"C:\Songs\new\a.txt"), F(@"C:\Songs\b.txt")], disk.Hash, disk.Exists);

        Assert.Equal(before[Key(@"C:\Songs\old\a.txt")], after[Key(@"C:\Songs\new\a.txt")]);
        Assert.Equal(before[Key(@"C:\Songs\b.txt")], after[Key(@"C:\Songs\b.txt")]);
        Assert.Equal(2, store.ListLibraryEntries().Count);
        Assert.Equal(@"C:\Songs\new\a.txt", store.GetLibraryEntry(after[Key(@"C:\Songs\new\a.txt")])!.Path);
    }

    [Fact]
    public void A_moved_and_edited_file_is_a_new_entry_and_the_old_one_stays_unavailable()
    {
        var disk = new FakeDisk();
        disk.Content[@"C:\Songs\a.txt"] = "uno";
        using var store = new KantorStore(":memory:", _clock.Next);
        var before = store.SyncLibraryEntries([F(@"C:\Songs\a.txt")], disk.Hash, disk.Exists);

        disk.Content.Remove(@"C:\Songs\a.txt");
        disk.Content[@"C:\Songs\moved\a.txt"] = "dos"; // otro contenido: no se adivina
        var after = store.SyncLibraryEntries([F(@"C:\Songs\moved\a.txt")], disk.Hash, disk.Exists);

        Assert.NotEqual(before[Key(@"C:\Songs\a.txt")], after[Key(@"C:\Songs\moved\a.txt")]);
        Assert.NotNull(store.GetLibraryEntry(before[Key(@"C:\Songs\a.txt")])); // sigue conocida (queda no disponible)
        Assert.Equal(2, store.ListLibraryEntries().Count);
    }

    [Fact]
    public void Two_new_files_with_the_same_content_do_not_inherit_anything()
    {
        var disk = new FakeDisk();
        disk.Content[@"C:\Songs\a.txt"] = "igual";
        using var store = new KantorStore(":memory:", _clock.Next);
        var before = store.SyncLibraryEntries([F(@"C:\Songs\a.txt")], disk.Hash, disk.Exists);

        disk.Content.Remove(@"C:\Songs\a.txt");
        disk.Content[@"C:\Songs\x\a.txt"] = "igual";
        disk.Content[@"C:\Songs\y\a.txt"] = "igual"; // dos copias: ambigüedad
        var after = store.SyncLibraryEntries([F(@"C:\Songs\x\a.txt"), F(@"C:\Songs\y\a.txt")], disk.Hash, disk.Exists);

        var original = before[Key(@"C:\Songs\a.txt")];
        Assert.NotEqual(original, after[Key(@"C:\Songs\x\a.txt")]);
        Assert.NotEqual(original, after[Key(@"C:\Songs\y\a.txt")]);
        Assert.NotEqual(after[Key(@"C:\Songs\x\a.txt")], after[Key(@"C:\Songs\y\a.txt")]);
    }

    [Fact]
    public void An_entry_whose_file_still_exists_elsewhere_is_never_inherited()
    {
        var disk = new FakeDisk();
        disk.Content[@"C:\Songs\a.txt"] = "igual";
        using var store = new KantorStore(":memory:", _clock.Next);
        var before = store.SyncLibraryEntries([F(@"C:\Songs\a.txt")], disk.Hash, disk.Exists);

        // el original sigue en disco (aunque ya no esté en las carpetas del escaneo): una copia no lo hereda
        disk.Content[@"D:\Copia\a.txt"] = "igual";
        var after = store.SyncLibraryEntries([F(@"D:\Copia\a.txt")], disk.Hash, disk.Exists);

        Assert.NotEqual(before[Key(@"C:\Songs\a.txt")], after[Key(@"D:\Copia\a.txt")]);
    }

    [Fact]
    public void Ids_survive_reopening_the_database()
    {
        var path = TempDb();
        var disk = new FakeDisk();
        disk.Content[@"C:\Songs\a.txt"] = "uno";
        string id;
        using (var store = new KantorStore(path, _clock.Next))
        {
            id = store.SyncLibraryEntries([F(@"C:\Songs\a.txt")], disk.Hash, disk.Exists)[Key(@"C:\Songs\a.txt")];
        }

        using var reopened = new KantorStore(path, _clock.Next);
        var again = reopened.SyncLibraryEntries([F(@"C:\Songs\a.txt")], disk.Hash, disk.Exists);

        Assert.Equal(id, again[Key(@"C:\Songs\a.txt")]);
    }

    [Fact]
    public void The_v2_queue_is_converted_in_order_with_entries_and_identities()
    {
        var path = TempDb();
        // una base como la dejaba la versión anterior (esquema 2, cola por ruta)
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            foreach (var sql in new[]
            {
                "CREATE TABLE player (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, character_id TEXT NOT NULL, created_at TEXT NOT NULL, last_activity_at TEXT NOT NULL, archived INTEGER NOT NULL DEFAULT 0)",
                "CREATE TABLE session (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, started_at TEXT NOT NULL, closed_at TEXT, is_open INTEGER NOT NULL)",
                "CREATE TABLE session_player (session_id INTEGER NOT NULL, player_id INTEGER NOT NULL, joined_at TEXT NOT NULL, PRIMARY KEY (session_id, player_id))",
                "CREATE TABLE session_selection (session_id INTEGER NOT NULL, slot INTEGER NOT NULL, player_id INTEGER NOT NULL, PRIMARY KEY (session_id, slot))",
                "CREATE TABLE performance (id TEXT PRIMARY KEY, session_id INTEGER NOT NULL, song_id TEXT NOT NULL, title TEXT NOT NULL, artist TEXT NOT NULL, finished_at TEXT NOT NULL, difficulty INTEGER NOT NULL, scoring_version INTEGER NOT NULL)",
                "CREATE TABLE performance_result (performance_id TEXT NOT NULL, player_id INTEGER NOT NULL, slot INTEGER NOT NULL, score INTEGER NOT NULL, PRIMARY KEY (performance_id, slot))",
                "CREATE TABLE queue_item (session_id INTEGER NOT NULL, position INTEGER NOT NULL, path TEXT NOT NULL, artist TEXT NOT NULL, title TEXT NOT NULL, PRIMARY KEY (session_id, position))",
                "INSERT INTO session (id, name, started_at, closed_at, is_open) VALUES (1, NULL, '2026-10-01T00:00:00.0000000Z', NULL, 1)",
                @"INSERT INTO queue_item VALUES (1, 0, 'C:\Songs\one.txt', 'Queen', 'Radio Ga Ga')",
                @"INSERT INTO queue_item VALUES (1, 1, 'C:\Songs\two.txt', 'Abba', 'Waterloo')",
                "PRAGMA user_version = 2",
            })
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        using var store = new KantorStore(path, _clock.Next);

        Assert.Equal(KantorStore.SchemaVersion, store.CurrentSchemaVersion);
        var queue = store.GetQueueEntries(1);
        Assert.Equal(["Radio Ga Ga", "Waterloo"], queue.Select(q => q.Title));
        Assert.All(queue, q => Assert.Equal(QueueState.Pending, q.State));
        Assert.Equal(SongIdentity.For("Queen", "Radio Ga Ga"), queue[0].SongIdentity);
        Assert.Equal(@"C:\Songs\one.txt", store.GetLibraryEntry(queue[0].LibraryEntryId)!.Path);
        Assert.Equal(2, queue.Select(q => q.ItemId).Distinct().Count());
    }

    // ---- Catálogo y búsqueda ------------------------------------------------------------------------------

    private static SongEntry Entry(string path, string artist, string title, string? edition = null,
        string? genre = null, string? year = null, string? language = null, string? cover = null) =>
        new(path, title, artist, cover, HasVideo: false, genre, year, language, edition);

    private static LibrarySnapshot Snapshot(params SongEntry[] entries)
    {
        var ids = entries.ToDictionary(e => LibraryEntryKeys.KeyOf(e.Path), e => LibraryIds.FromPath(e.Path));
        var catalog = new LibraryCatalog();
        catalog.Replace(LibraryCatalog.Build(entries, ids));
        return catalog.Current;
    }

    [Fact]
    public void Editions_of_the_same_song_are_separate_entries_that_share_the_song_identity()
    {
        var snapshot = Snapshot(
            Entry(@"C:\S\a1.txt", "Queen", "Radio Ga Ga", edition: "Karaoke"),
            Entry(@"C:\S\a2.txt", "Queen", "Radio Ga Ga", edition: "Original"),
            Entry(@"C:\S\b.txt", "Abba", "Waterloo"));

        Assert.Equal(3, snapshot.Items.Count);
        var queen = snapshot.Items.Where(i => i.Entry.Artist == "Queen").ToList();
        Assert.NotEqual(queen[0].EntryId, queen[1].EntryId);
        Assert.Equal(queen[0].SongIdentity, queen[1].SongIdentity);

        var byIdentity = LibrarySearch.Run(snapshot, new LibraryQuery(SongIdentity: queen[0].SongIdentity));
        Assert.Equal(["Karaoke", "Original"], byIdentity.Select(i => i.Entry.Edition));
    }

    [Fact]
    public void Search_uses_the_local_rules_including_edition_and_ranks_prefix_matches_first()
    {
        var snapshot = Snapshot(
            Entry(@"C:\S\1.txt", "The Killers", "Mr. Brightside"),
            Entry(@"C:\S\2.txt", "Brightside Band", "Canción", edition: "Remaster"),
            Entry(@"C:\S\3.txt", "Otro", "Algo", edition: "Deluxe"));

        var bright = LibrarySearch.Run(snapshot, new LibraryQuery("bright"));
        Assert.Equal(["Brightside Band", "The Killers"], bright.Select(i => i.Entry.Artist)); // empieza con la consulta primero

        Assert.Single(LibrarySearch.Run(snapshot, new LibraryQuery("deluxe"))); // la edición también se busca
        Assert.Single(LibrarySearch.Run(snapshot, new LibraryQuery("CANCION")));  // sin acentos ni mayúsculas
    }

    [Fact]
    public void Filters_and_sort_are_normalised_and_the_order_is_stable()
    {
        var snapshot = Snapshot(
            Entry(@"C:\S\1.txt", "Zeta", "Alfa", genre: "Rock", year: "1999", language: "Spanish"),
            Entry(@"C:\S\2.txt", "Alfa", "Zeta", genre: "rock", year: "1999", language: "English"),
            Entry(@"C:\S\3.txt", "Medio", "Medio", genre: "Pop", year: "2005"));

        Assert.Equal(2, LibrarySearch.Run(snapshot, new LibraryQuery(Genre: "ROCK")).Count);
        Assert.Single(LibrarySearch.Run(snapshot, new LibraryQuery(Year: "2005")));
        Assert.Equal(["Alfa", "Medio", "Zeta"],
            LibrarySearch.Run(snapshot, new LibraryQuery(Sort: LibrarySort.Title)).Select(i => i.Entry.Title));
        Assert.Equal(["Alfa", "Medio", "Zeta"],
            LibrarySearch.Run(snapshot, new LibraryQuery()).Select(i => i.Entry.Artist));
        var twice = LibrarySearch.Run(snapshot, new LibraryQuery());
        Assert.Equal(twice.Select(i => i.EntryId), LibrarySearch.Run(snapshot, new LibraryQuery()).Select(i => i.EntryId));
    }

    [Fact]
    public void Facets_group_without_case_and_count_entries()
    {
        var snapshot = Snapshot(
            Entry(@"C:\S\1.txt", "A", "1", genre: "Rock", year: "1999"),
            Entry(@"C:\S\2.txt", "B", "2", genre: "rock", year: "1999"),
            Entry(@"C:\S\3.txt", "C", "3", genre: "Rock", year: "2005"),
            Entry(@"C:\S\4.txt", "D", "4"));

        var facets = LibrarySearch.Facets(snapshot);

        Assert.Equal([new FacetValue("Rock", 3)], facets.Genres);
        Assert.Equal([new FacetValue("1999", 2), new FacetValue("2005", 1)], facets.Years);
        Assert.Empty(facets.Languages);
    }

    [Fact]
    public void Replacing_with_the_same_content_does_not_advance_the_revision()
    {
        var entries = new[] { Entry(@"C:\S\1.txt", "A", "1") };
        var ids = entries.ToDictionary(e => LibraryEntryKeys.KeyOf(e.Path), e => LibraryIds.FromPath(e.Path));
        var catalog = new LibraryCatalog();

        Assert.True(catalog.Replace(LibraryCatalog.Build(entries, ids)));
        var rev = catalog.Current.Rev;
        Assert.False(catalog.Replace(LibraryCatalog.Build(entries, ids)));
        Assert.Equal(rev, catalog.Current.Rev);
    }
}
