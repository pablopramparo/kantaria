// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using Kantor.Core.Characters;
using Microsoft.Data.Sqlite;

namespace Kantor.Core.Data;

/// <summary>
/// Datos históricos en SQLite: jugadores, sesiones, selección de participantes e interpretaciones con sus
/// resultados. La configuración general no vive acá (sigue en <c>settings.json</c>). No guarda estadísticas
/// agregadas: se calculan al consultar. Una sola conexión por instancia; seguro entre hilos por bloqueo.
/// Las reglas que una restricción SQL no puede expresar se validan dentro de la misma transacción.
/// </summary>
public sealed class KantorStore : IDisposable
{
    public const int MaxPlayersPerPerformance = 4;
    public const int MaxNameLength = 24;
    public const int MaxScore = 10_000;

    /// <summary>Versión del esquema que crea esta clase (<c>PRAGMA user_version</c>).</summary>
    public const int SchemaVersion = 3;

    private static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE player (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            name             TEXT    NOT NULL CHECK (length(trim(name)) > 0),
            character_id     TEXT    NOT NULL CHECK (length(character_id) > 0),
            created_at       TEXT    NOT NULL,
            last_activity_at TEXT    NOT NULL,
            archived         INTEGER NOT NULL DEFAULT 0 CHECK (archived IN (0, 1))
        );
        CREATE UNIQUE INDEX ux_player_active_name ON player (name COLLATE NOCASE) WHERE archived = 0;

        CREATE TABLE session (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            name       TEXT,
            started_at TEXT    NOT NULL,
            closed_at  TEXT,
            is_open    INTEGER NOT NULL CHECK (is_open IN (0, 1)),
            CHECK ((is_open = 1) = (closed_at IS NULL))
        );
        -- una sola sesión abierta a la vez
        CREATE UNIQUE INDEX ux_session_single_open ON session (is_open) WHERE is_open = 1;

        CREATE TABLE session_player (
            session_id INTEGER NOT NULL REFERENCES session (id) ON DELETE RESTRICT,
            player_id  INTEGER NOT NULL REFERENCES player (id)  ON DELETE RESTRICT,
            joined_at  TEXT    NOT NULL,
            PRIMARY KEY (session_id, player_id)
        ) WITHOUT ROWID;
        CREATE INDEX ix_session_player_player ON session_player (player_id);

        -- selección activa de la sesión: quién ocupa cada puesto P1–P4
        CREATE TABLE session_selection (
            session_id INTEGER NOT NULL,
            slot       INTEGER NOT NULL CHECK (slot BETWEEN 1 AND 4),
            player_id  INTEGER NOT NULL,
            PRIMARY KEY (session_id, slot),
            UNIQUE (session_id, player_id),
            FOREIGN KEY (session_id, player_id) REFERENCES session_player (session_id, player_id) ON DELETE RESTRICT
        ) WITHOUT ROWID;

        CREATE TABLE performance (
            id              TEXT    PRIMARY KEY,
            session_id      INTEGER NOT NULL REFERENCES session (id) ON DELETE RESTRICT,
            song_id         TEXT    NOT NULL CHECK (length(song_id) > 0),
            title           TEXT    NOT NULL,
            artist          TEXT    NOT NULL,
            finished_at     TEXT    NOT NULL,
            difficulty      INTEGER NOT NULL CHECK (difficulty BETWEEN 0 AND 2),
            scoring_version INTEGER NOT NULL CHECK (scoring_version >= 1)
        ) WITHOUT ROWID;
        CREATE INDEX ix_performance_session ON performance (session_id, finished_at);
        CREATE INDEX ix_performance_song    ON performance (song_id);

        CREATE TABLE performance_result (
            performance_id TEXT    NOT NULL REFERENCES performance (id) ON DELETE RESTRICT,
            player_id      INTEGER NOT NULL REFERENCES player (id)      ON DELETE RESTRICT,
            slot           INTEGER NOT NULL CHECK (slot BETWEEN 1 AND 4),
            score          INTEGER NOT NULL CHECK (score BETWEEN 0 AND 10000),
            PRIMARY KEY (performance_id, slot),
            UNIQUE (performance_id, player_id)
        ) WITHOUT ROWID;
        CREATE INDEX ix_result_player ON performance_result (player_id);

        -- red de seguridad: un jugador archivado no recibe resultados nuevos
        CREATE TRIGGER trg_result_active_player BEFORE INSERT ON performance_result
        WHEN (SELECT archived FROM player WHERE id = NEW.player_id) = 1
        BEGIN
            SELECT RAISE(ABORT, 'el jugador está archivado');
        END;
        """,
        // v2: cola de canciones de la partida
        """
        CREATE TABLE queue_item (
            session_id INTEGER NOT NULL REFERENCES session (id) ON DELETE RESTRICT,
            position   INTEGER NOT NULL CHECK (position >= 0),
            path       TEXT    NOT NULL CHECK (length(path) > 0),
            artist     TEXT    NOT NULL,
            title      TEXT    NOT NULL,
            PRIMARY KEY (session_id, position)
        ) WITHOUT ROWID;
        """,
        // v3: entradas de biblioteca con identificador estable, cola con estado y comentario por resultado
        """
        CREATE TABLE library_entry (
            entry_id      TEXT    PRIMARY KEY,
            path_key      TEXT    NOT NULL UNIQUE,
            path          TEXT    NOT NULL,
            content_hash  TEXT,
            mtime_ticks   INTEGER NOT NULL DEFAULT 0,
            length        INTEGER NOT NULL DEFAULT 0,
            first_seen_at TEXT    NOT NULL
        ) WITHOUT ROWID;
        CREATE INDEX ix_library_entry_hash ON library_entry (content_hash);

        CREATE TABLE queue_entry (
            item_id          TEXT    PRIMARY KEY,
            session_id       INTEGER NOT NULL REFERENCES session (id) ON DELETE RESTRICT,
            position         INTEGER NOT NULL CHECK (position >= 0),
            library_entry_id TEXT    NOT NULL CHECK (length(library_entry_id) > 0),
            song_identity    TEXT    NOT NULL,
            artist           TEXT    NOT NULL,
            title            TEXT    NOT NULL,
            edition          TEXT,
            state            INTEGER NOT NULL CHECK (state BETWEEN 0 AND 4),
            slots            TEXT    NOT NULL DEFAULT '[]',
            added_by_id      TEXT,
            added_by_name    TEXT,
            added_at         TEXT    NOT NULL,
            finished_at      TEXT,
            performance_id   TEXT
        ) WITHOUT ROWID;
        CREATE INDEX ix_queue_entry_session ON queue_entry (session_id, position);

        ALTER TABLE performance ADD COLUMN library_entry_id TEXT;
        ALTER TABLE performance ADD COLUMN edition TEXT;
        ALTER TABLE performance_result ADD COLUMN comment_id TEXT;
        """,
    ];

    /// <summary>Índice (base 0) de la migración que además necesita código: la cola v2 pasa a <c>queue_entry</c>.</summary>
    private const int LegacyQueueMigration = 2;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="path">Archivo de la base (se crea si falta) o <c>:memory:</c>.</param>
    /// <param name="now">Reloj inyectable para las pruebas; por omisión, la hora actual.</param>
    public KantorStore(string path, Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        var directory = path == ":memory:" ? null : Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Pooling apagado: al cerrar, el archivo queda libre (reinicio de la aplicación, borrado en pruebas).
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Migrate();
    }

    public static string DefaultPath() => Path.Combine(Settings.AppPaths.DataDirectory(), "kantor.db");

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    /// <summary>Versión del esquema de la base abierta.</summary>
    public int CurrentSchemaVersion
    {
        get
        {
            lock (_gate)
            {
                return Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
            }
        }
    }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > Migrations.Length)
        {
            throw new InvalidOperationException(
                $"La base tiene el esquema {version}, más nuevo que el que conoce esta versión ({Migrations.Length}).");
        }

        for (var v = version; v < Migrations.Length; v++)
        {
            using var transaction = _connection.BeginTransaction();
            Execute(Migrations[v]);
            if (v == LegacyQueueMigration)
            {
                ConvertLegacyQueue();
            }

            Execute($"PRAGMA user_version = {v + 1}");
            transaction.Commit();
        }
    }

    /// <summary>
    /// Pasa la cola de la v2 (solo ruta, artista y título) al formato nuevo: cada ruta recibe su entrada de
    /// biblioteca y cada elemento un identificador. Todo lo viejo queda pendiente, en el mismo orden.
    /// </summary>
    private void ConvertLegacyQueue()
    {
        var rows = Query("SELECT session_id, position, path, artist, title FROM queue_item ORDER BY session_id, position",
            r => (Session: r.GetInt64(0), Position: r.GetInt32(1), Path: r.GetString(2), Artist: r.GetString(3),
                Title: r.GetString(4)));
        var now = Stamp(_now());
        foreach (var row in rows)
        {
            var entryId = EnsureLibraryEntryUnlocked(row.Path, now);
            Execute("""
                INSERT INTO queue_entry (item_id, session_id, position, library_entry_id, song_identity, artist, title,
                                         edition, state, slots, added_at)
                VALUES ($id, $s, $p, $entry, $identity, $artist, $title, NULL, 0, '[]', $now)
                """, ("$id", Guid.NewGuid().ToString("N")), ("$s", row.Session), ("$p", row.Position),
                ("$entry", entryId), ("$identity", SongIdentity.For(row.Artist, row.Title)),
                ("$artist", row.Artist), ("$title", row.Title), ("$now", now));
        }

        Execute("DROP TABLE queue_item");
    }

    // ---- Jugadores ----------------------------------------------------------------------------------------

    public PlayerRecord CreatePlayer(string name, string? characterId = null)
    {
        name = ValidName(name);
        lock (_gate)
        {
            var now = Stamp(_now());
            try
            {
                Execute("""
                    INSERT INTO player (name, character_id, created_at, last_activity_at, archived)
                    VALUES ($name, $character, $now, $now, 0)
                    """, ("$name", name), ("$character", characterId ?? CharacterCatalog.Fallback.Id), ("$now", now));
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)
            {
                throw new StoreRuleException($"Ya existe un jugador activo llamado '{name}'.");
            }

            return GetPlayerUnlocked(Convert.ToInt64(Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture))!;
        }
    }

    public void RenamePlayer(long playerId, string name)
    {
        name = ValidName(name);
        lock (_gate)
        {
            var player = RequirePlayer(playerId);
            try
            {
                Execute("UPDATE player SET name = $name WHERE id = $id", ("$name", name), ("$id", player.Id));
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)
            {
                throw new StoreRuleException($"Ya existe un jugador activo llamado '{name}'.");
            }
        }
    }

    /// <summary>
    /// Asocia un personaje. El ID se guarda tal cual aunque ya no esté en el catálogo: el fallback se aplica al
    /// mostrarlo (<see cref="CharacterCatalog.GetOrDefault"/>), nunca al guardar.
    /// </summary>
    public void SetCharacter(long playerId, string characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId))
        {
            throw new StoreRuleException("El personaje no puede estar vacío.");
        }

        lock (_gate)
        {
            RequirePlayer(playerId);
            Execute("UPDATE player SET character_id = $c WHERE id = $id", ("$c", characterId), ("$id", playerId));
        }
    }

    /// <summary>
    /// Archiva al jugador: deja de poder elegirse y de recibir resultados, y sale de la selección de la sesión
    /// abierta. Su historial no se toca.
    /// </summary>
    public void ArchivePlayer(long playerId)
    {
        lock (_gate)
        {
            RequirePlayer(playerId);
            using var transaction = _connection.BeginTransaction();
            Execute("UPDATE player SET archived = 1 WHERE id = $id", ("$id", playerId));
            Execute("""
                DELETE FROM session_selection
                WHERE player_id = $id AND session_id IN (SELECT id FROM session WHERE is_open = 1)
                """, ("$id", playerId));
            transaction.Commit();
        }
    }

    public PlayerRecord? GetPlayer(long playerId)
    {
        lock (_gate)
        {
            return GetPlayerUnlocked(playerId);
        }
    }

    public IReadOnlyList<PlayerRecord> ListPlayers(bool includeArchived = false)
    {
        lock (_gate)
        {
            var sql = "SELECT id, name, character_id, created_at, last_activity_at, archived FROM player " +
                      (includeArchived ? string.Empty : "WHERE archived = 0 ") + "ORDER BY name COLLATE NOCASE, id";
            return Query(sql, ReadPlayer);
        }
    }

    // ---- Sesiones -----------------------------------------------------------------------------------------

    public SessionRecord? GetOpenSession()
    {
        lock (_gate)
        {
            return OpenSessionUnlocked();
        }
    }

    public IReadOnlyList<SessionRecord> ListSessions()
    {
        lock (_gate)
        {
            return Query("SELECT id, name, started_at, closed_at FROM session ORDER BY started_at DESC, id DESC",
                ReadSession);
        }
    }

    /// <summary>
    /// Inicia una sesión nueva. Si había una abierta, la cierra en la misma transacción (el usuario eligió
    /// "iniciar nueva"): nunca quedan dos abiertas.
    /// </summary>
    public SessionRecord StartSession(string? name = null, IReadOnlyList<SelectionEntry>? selection = null)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var session = StartSessionUnlocked(name, selection);
            transaction.Commit();
            return session;
        }
    }

    /// <summary>Cierre explícito. Devuelve <c>false</c> si ya estaba cerrada o no existe.</summary>
    public bool CloseSession(long sessionId)
    {
        lock (_gate)
        {
            return Execute("""
                UPDATE session SET closed_at = $now, is_open = 0 WHERE id = $id AND is_open = 1
                """, ("$now", Stamp(_now())), ("$id", sessionId)) > 0;
        }
    }

    public IReadOnlyList<PlayerRecord> SessionPlayers(long sessionId)
    {
        lock (_gate)
        {
            return Query("""
                SELECT p.id, p.name, p.character_id, p.created_at, p.last_activity_at, p.archived
                FROM session_player sp JOIN player p ON p.id = sp.player_id
                WHERE sp.session_id = $s ORDER BY sp.joined_at, p.id
                """, ReadPlayer, ("$s", sessionId));
        }
    }

    /// <summary>Quién ocupa cada puesto P1–P4 en la selección guardada de la sesión, por orden de puesto.</summary>
    public IReadOnlyList<SelectionEntry> GetSelection(long sessionId)
    {
        lock (_gate)
        {
            return Query("SELECT slot, player_id FROM session_selection WHERE session_id = $s ORDER BY slot",
                r => new SelectionEntry(r.GetInt32(0), r.GetInt64(1)), ("$s", sessionId));
        }
    }

    /// <summary>
    /// Reemplaza la selección activa de la sesión abierta. Los elegidos tienen que ser jugadores de la sesión
    /// (ver <see cref="AddPlayerToSession"/>): elegir no los incorpora.
    /// </summary>
    public void SetSelection(long sessionId, IReadOnlyList<SelectionEntry> selection)
    {
        lock (_gate)
        {
            RequireOpenSession(sessionId);
            using var transaction = _connection.BeginTransaction();
            ApplySelectionUnlocked(sessionId, selection, addMembers: false);
            transaction.Commit();
        }
    }

    /// <summary>Incorpora un jugador activo a la sesión abierta. Si ya estaba, no cambia nada.</summary>
    public void AddPlayerToSession(long sessionId, long playerId)
    {
        lock (_gate)
        {
            RequireOpenSession(sessionId);
            var player = RequirePlayer(playerId);
            if (player.Archived)
            {
                throw new StoreRuleException($"El jugador '{player.Name}' está archivado.");
            }

            Execute("INSERT OR IGNORE INTO session_player (session_id, player_id, joined_at) VALUES ($s, $p, $now)",
                ("$s", sessionId), ("$p", playerId), ("$now", Stamp(_now())));
        }
    }

    /// <summary>
    /// Saca a un jugador de la sesión abierta y de su selección. Lo que ya cantó queda en el historial (los
    /// resultados cuelgan del jugador y de la interpretación, no de la membresía).
    /// </summary>
    public void RemovePlayerFromSession(long sessionId, long playerId)
    {
        lock (_gate)
        {
            RequireOpenSession(sessionId);
            using var transaction = _connection.BeginTransaction();
            Execute("DELETE FROM session_selection WHERE session_id = $s AND player_id = $p",
                ("$s", sessionId), ("$p", playerId));
            Execute("DELETE FROM session_player WHERE session_id = $s AND player_id = $p",
                ("$s", sessionId), ("$p", playerId));
            transaction.Commit();
        }
    }

    private void RequireOpenSession(long sessionId)
    {
        var session = Query("SELECT id, name, started_at, closed_at FROM session WHERE id = $id",
            ReadSession, ("$id", sessionId)).FirstOrDefault();
        if (session is null || !session.IsOpen)
        {
            throw new StoreRuleException("La partida no existe o está cerrada.");
        }
    }

    // ---- Cola de canciones --------------------------------------------------------------------------------

    /// <summary>
    /// Todos los elementos de la cola de la sesión (también los terminados, cancelados y quitados), en orden.
    /// <see cref="QueueEntry.Order"/> es la posición en esa lista completa.
    /// </summary>
    public IReadOnlyList<QueueEntry> GetQueueEntries(long sessionId)
    {
        lock (_gate)
        {
            var rows = Query("""
                SELECT item_id, library_entry_id, song_identity, artist, title, edition, state, slots,
                       added_by_id, added_by_name, added_at, finished_at, performance_id
                FROM queue_entry WHERE session_id = $s ORDER BY position
                """, r => new QueueEntry(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), (QueueState)r.GetInt32(6), 0,
                ParseSlots(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
                Parse(r.GetString(10)), r.IsDBNull(11) ? null : Parse(r.GetString(11)),
                r.IsDBNull(12) ? null : r.GetString(12)), ("$s", sessionId));
            return [.. rows.Select((e, i) => e with { Order = i })];
        }
    }

    /// <summary>Reemplaza la cola de la sesión por esa lista, en ese orden (en una transacción).</summary>
    public void SetQueueEntries(long sessionId, IReadOnlyList<QueueEntry> items)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            Execute("DELETE FROM queue_entry WHERE session_id = $s", ("$s", sessionId));
            for (var i = 0; i < items.Count; i++)
            {
                var e = items[i];
                Execute("""
                    INSERT INTO queue_entry (item_id, session_id, position, library_entry_id, song_identity, artist,
                                             title, edition, state, slots, added_by_id, added_by_name, added_at,
                                             finished_at, performance_id)
                    VALUES ($id, $s, $p, $entry, $identity, $artist, $title, $edition, $state, $slots, $byId,
                            $byName, $added, $finished, $perf)
                    """, ("$id", e.ItemId), ("$s", sessionId), ("$p", i), ("$entry", e.LibraryEntryId),
                    ("$identity", e.SongIdentity), ("$artist", e.Artist), ("$title", e.Title), ("$edition", e.Edition),
                    ("$state", (int)e.State), ("$slots", SerializeSlots(e.Slots)), ("$byId", e.AddedById),
                    ("$byName", e.AddedByName), ("$added", Stamp(e.AddedAt)),
                    ("$finished", e.FinishedAt is { } f ? Stamp(f) : null), ("$perf", e.PerformanceId));
            }

            transaction.Commit();
        }
    }

    private static string SerializeSlots(IReadOnlyList<QueueSlot> slots) =>
        System.Text.Json.JsonSerializer.Serialize(slots.Select(s => new SlotJson(s.Slot, s.PlayerId)).ToList());

    private static IReadOnlyList<QueueSlot> ParseSlots(string json)
    {
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<SlotJson>>(json) ?? [];
            return [.. parsed.Select(s => new QueueSlot(s.Slot, s.PlayerId))];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private readonly record struct SlotJson(int Slot, long PlayerId);

    // ---- Entradas de la biblioteca (identificador estable) ------------------------------------------------

    /// <summary>La entrada con ese identificador, o <c>null</c> si nunca existió.</summary>
    public LibraryEntryRow? GetLibraryEntry(string entryId)
    {
        lock (_gate)
        {
            return Query("SELECT entry_id, path, content_hash FROM library_entry WHERE entry_id = $id",
                r => new LibraryEntryRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)),
                ("$id", entryId)).FirstOrDefault();
        }
    }

    /// <summary>Todas las entradas conocidas (también las que ya no están en disco).</summary>
    public IReadOnlyList<LibraryEntryRow> ListLibraryEntries()
    {
        lock (_gate)
        {
            return Query("SELECT entry_id, path, content_hash FROM library_entry ORDER BY path_key",
                r => new LibraryEntryRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }
    }

    /// <summary>La entrada de esa ruta; si no existe la crea (sin hash de contenido).</summary>
    public string EnsureLibraryEntry(string path)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var id = EnsureLibraryEntryUnlocked(path, Stamp(_now()));
            transaction.Commit();
            return id;
        }
    }

    private string EnsureLibraryEntryUnlocked(string path, string now)
    {
        var key = LibraryEntryKeys.KeyOf(path);
        if (Scalar("SELECT entry_id FROM library_entry WHERE path_key = $k", ("$k", key)) is string existing)
        {
            return existing;
        }

        var id = Guid.NewGuid().ToString("N");
        Execute("""
            INSERT INTO library_entry (entry_id, path_key, path, content_hash, mtime_ticks, length, first_seen_at)
            VALUES ($id, $k, $path, NULL, 0, 0, $now)
            """, ("$id", id), ("$k", key), ("$path", path), ("$now", now));
        return id;
    }

    /// <summary>
    /// Asigna un identificador estable a cada archivo de la biblioteca y devuelve ruta (clave) → identificador.
    /// El mismo archivo (misma ruta, sin distinguir mayúsculas) conserva su identificador aunque se edite. Una ruta
    /// nueva hereda el de una entrada cuyo archivo ya no existe SOLO si el SHA-256 de su contenido coincide con el
    /// de exactamente esa entrada y ningún otro archivo nuevo tiene ese mismo contenido; en cualquier otro caso se
    /// crea una entrada nueva. Nunca se reasocia por artista y título.
    /// </summary>
    /// <param name="hash">Hash del contenido de un archivo (inyectable para las pruebas); <c>null</c> si no se puede leer.</param>
    /// <param name="exists">Si el archivo de una entrada conocida sigue existiendo (inyectable).</param>
    public IReadOnlyDictionary<string, string> SyncLibraryEntries(IReadOnlyList<LibraryFile> files,
        Func<string, string?>? hash = null, Func<string, bool>? exists = null)
    {
        hash ??= LibraryEntryKeys.HashFile;
        exists ??= File.Exists;

        // 1) foto de lo conocido; el trabajo de disco (hashes) se hace fuera del candado
        var known = ListLibraryEntriesFull();
        var byKey = known.ToDictionary(k => k.PathKey, StringComparer.Ordinal);
        var current = new Dictionary<string, LibraryFile>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            current[LibraryEntryKeys.KeyOf(file.Path)] = file;
        }

        var hashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, file) in current)
        {
            if (!byKey.TryGetValue(key, out var row) || row.MtimeTicks != file.ModifiedTicks || row.Length != file.Length
                || row.ContentHash is null)
            {
                hashes[key] = hash(file.Path);
            }
        }

        var orphans = known
            .Where(k => !current.ContainsKey(k.PathKey) && !exists(k.Path) && k.ContentHash is not null)
            .ToList();

        // 2) aplicar todo junto
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var now = Stamp(_now());
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var newKeys = new List<string>();
            foreach (var (key, file) in current)
            {
                if (byKey.TryGetValue(key, out var row))
                {
                    result[key] = row.EntryId;
                    if (hashes.TryGetValue(key, out var h))
                    {
                        Execute("UPDATE library_entry SET content_hash = $h, mtime_ticks = $m, length = $l, path = $p " +
                                "WHERE entry_id = $id", ("$h", h), ("$m", file.ModifiedTicks), ("$l", file.Length),
                            ("$p", file.Path), ("$id", row.EntryId));
                    }
                }
                else
                {
                    newKeys.Add(key);
                }
            }

            // un contenido compartido por 2 o más archivos nuevos es ambiguo: ninguno hereda
            var sharedByNew = newKeys.Where(k => hashes.GetValueOrDefault(k) is not null)
                .GroupBy(k => hashes[k]!).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in newKeys)
            {
                var file = current[key];
                var h = hashes.GetValueOrDefault(key);
                string? inherited = null;
                if (h is not null && !sharedByNew.Contains(h))
                {
                    var candidates = orphans.Where(o => o.ContentHash == h && !consumed.Contains(o.EntryId)).ToList();
                    if (candidates.Count == 1)
                    {
                        inherited = candidates[0].EntryId;
                        consumed.Add(inherited);
                    }
                }

                if (inherited is not null)
                {
                    Execute("""
                        UPDATE library_entry SET path_key = $k, path = $p, content_hash = $h, mtime_ticks = $m,
                                                 length = $l WHERE entry_id = $id
                        """, ("$k", key), ("$p", file.Path), ("$h", h), ("$m", file.ModifiedTicks), ("$l", file.Length),
                        ("$id", inherited));
                    result[key] = inherited;
                }
                else
                {
                    var id = Guid.NewGuid().ToString("N");
                    Execute("""
                        INSERT INTO library_entry (entry_id, path_key, path, content_hash, mtime_ticks, length, first_seen_at)
                        VALUES ($id, $k, $p, $h, $m, $l, $now)
                        """, ("$id", id), ("$k", key), ("$p", file.Path), ("$h", h), ("$m", file.ModifiedTicks),
                        ("$l", file.Length), ("$now", now));
                    result[key] = id;
                }
            }

            transaction.Commit();
            return result;
        }
    }

    private sealed record FullEntry(string EntryId, string PathKey, string Path, string? ContentHash, long MtimeTicks,
        long Length);

    private List<FullEntry> ListLibraryEntriesFull()
    {
        lock (_gate)
        {
            return Query("SELECT entry_id, path_key, path, content_hash, mtime_ticks, length FROM library_entry",
                r => new FullEntry(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetInt64(4), r.GetInt64(5)));
        }
    }

    // ---- Mantenimiento ------------------------------------------------------------------------------------

    /// <summary>
    /// Borra una partida CERRADA con sus interpretaciones y resultados (los jugadores no se tocan). La abierta no se
    /// borra: primero hay que cerrarla. Devuelve <c>false</c> si no existe o está abierta.
    /// </summary>
    public bool DeleteSession(long sessionId)
    {
        lock (_gate)
        {
            var session = Query("SELECT id, name, started_at, closed_at FROM session WHERE id = $id", ReadSession,
                ("$id", sessionId)).FirstOrDefault();
            if (session is null || session.IsOpen)
            {
                return false;
            }

            using var transaction = _connection.BeginTransaction();
            Execute("""
                DELETE FROM performance_result
                WHERE performance_id IN (SELECT id FROM performance WHERE session_id = $id)
                """, ("$id", sessionId));
            Execute("DELETE FROM performance WHERE session_id = $id", ("$id", sessionId));
            Execute("DELETE FROM queue_entry WHERE session_id = $id", ("$id", sessionId));
            Execute("DELETE FROM session_selection WHERE session_id = $id", ("$id", sessionId));
            Execute("DELETE FROM session_player WHERE session_id = $id", ("$id", sessionId));
            Execute("DELETE FROM session WHERE id = $id", ("$id", sessionId));
            transaction.Commit();
            return true;
        }
    }

    /// <summary>
    /// Copia consistente de toda la base en <paramref name="path"/> (un archivo nuevo; si existe, falla). Es un
    /// archivo SQLite completo: para restaurar, con Kantor cerrado, reemplazá <c>kantor.db</c> por la copia.
    /// </summary>
    public void BackupTo(string path)
    {
        if (File.Exists(path))
        {
            throw new IOException("El archivo de respaldo ya existe: " + path);
        }

        lock (_gate)
        {
            Execute("VACUUM INTO $path", ("$path", path));
        }
    }

    // ---- Interpretaciones ---------------------------------------------------------------------------------

    /// <summary>
    /// Guarda una interpretación terminada con todos sus resultados en una única transacción: o queda todo o no
    /// queda nada. Si no hay sesión abierta, crea una (con <paramref name="selectionForNewSession"/> como
    /// selección). Guardar de nuevo la misma ejecución no cambia nada.
    /// </summary>
    public SaveOutcome SavePerformance(NewPerformance performance,
        IReadOnlyList<SelectionEntry>? selectionForNewSession = null)
    {
        ValidatePerformance(performance);

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            var existing = Scalar("SELECT session_id FROM performance WHERE id = $id", ("$id", performance.Id));
            if (existing is not null)
            {
                return new SaveOutcome(Convert.ToInt64(existing, CultureInfo.InvariantCulture), AlreadySaved: true,
                    SessionCreated: false);
            }

            foreach (var result in performance.Results)
            {
                var player = GetPlayerUnlocked(result.PlayerId)
                             ?? throw new StoreRuleException($"El jugador {result.PlayerId} no existe.");
                if (player.Archived)
                {
                    throw new StoreRuleException($"El jugador '{player.Name}' está archivado.");
                }
            }

            var session = OpenSessionUnlocked();
            var created = false;
            if (session is null)
            {
                // Sin sesión abierta: se crea una con los participantes de esta interpretación.
                var selection = selectionForNewSession
                                ?? [.. performance.Results.Select(r => new SelectionEntry(r.Slot, r.PlayerId))];
                session = StartSessionUnlocked(null, selection);
                created = true;
                var joinedAt = Stamp(_now());
                foreach (var result in performance.Results)
                {
                    Execute("INSERT OR IGNORE INTO session_player (session_id, player_id, joined_at) VALUES ($s, $p, $at)",
                        ("$s", session.Id), ("$p", result.PlayerId), ("$at", joinedAt));
                }
            }
            else
            {
                foreach (var result in performance.Results)
                {
                    var isMember = Scalar("SELECT 1 FROM session_player WHERE session_id = $s AND player_id = $p",
                        ("$s", session.Id), ("$p", result.PlayerId)) is not null;
                    if (!isMember)
                    {
                        throw new StoreRuleException(
                            $"El jugador {result.PlayerId} no está en la partida abierta: hay que agregarlo primero.");
                    }
                }
            }

            var finishedAt = Stamp(_now());
            Execute("""
                INSERT INTO performance (id, session_id, song_id, title, artist, finished_at, difficulty, scoring_version,
                                         library_entry_id, edition)
                VALUES ($id, $session, $song, $title, $artist, $at, $difficulty, $version, $entry, $edition)
                """, ("$id", performance.Id), ("$session", session.Id), ("$song", performance.SongId),
                ("$title", performance.Title), ("$artist", performance.Artist), ("$at", finishedAt),
                ("$difficulty", performance.Difficulty), ("$version", performance.ScoringVersion),
                ("$entry", performance.LibraryEntryId), ("$edition", performance.Edition));

            foreach (var result in performance.Results)
            {
                Execute("""
                    INSERT INTO performance_result (performance_id, player_id, slot, score, comment_id)
                    VALUES ($p, $player, $slot, $score, $comment)
                    """, ("$p", performance.Id), ("$player", result.PlayerId), ("$slot", result.Slot),
                    ("$score", result.Score), ("$comment", result.CommentId));
                Execute("UPDATE player SET last_activity_at = $at WHERE id = $player",
                    ("$at", finishedAt), ("$player", result.PlayerId));
            }

            transaction.Commit();
            return new SaveOutcome(session.Id, AlreadySaved: false, created);
        }
    }

    /// <summary>Interpretaciones de la sesión, en orden, con sus resultados y la posición de cada jugador.</summary>
    public IReadOnlyList<PerformanceView> GetSessionHistory(long sessionId) => ReadViews(sessionId);

    /// <summary>Todas las interpretaciones guardadas (de todas las sesiones), en orden: base del ranking global.</summary>
    public IReadOnlyList<PerformanceView> GetAllHistory() => ReadViews(null);

    /// <summary>Una interpretación guardada con sus resultados, o <c>null</c> si no existe.</summary>
    public PerformanceView? GetPerformance(string performanceId) =>
        ReadViews(null, performanceId).FirstOrDefault();

    private IReadOnlyList<PerformanceView> ReadViews(long? sessionId, string? performanceId = null)
    {
        lock (_gate)
        {
            const string Columns = """
                SELECT id, session_id, song_id, title, artist, finished_at, difficulty, scoring_version,
                       library_entry_id, edition FROM performance
                """;
            var performances = performanceId is not null
                ? Query(Columns + " WHERE id = $id", ReadPerformance, ("$id", performanceId))
                : sessionId is { } id0
                    ? Query(Columns + " WHERE session_id = $s ORDER BY finished_at, id", ReadPerformance, ("$s", id0))
                    : Query(Columns + " ORDER BY finished_at, id", ReadPerformance);

            var views = new List<PerformanceView>();
            foreach (var performance in performances)
            {
                var rows = Query("""
                    SELECT r.player_id, p.name, r.slot, r.score, r.comment_id
                    FROM performance_result r JOIN player p ON p.id = r.player_id
                    WHERE r.performance_id = $id ORDER BY r.slot
                    """, r => (PlayerId: r.GetInt64(0), Name: r.GetString(1), Slot: r.GetInt32(2), Score: r.GetInt32(3),
                        Comment: r.IsDBNull(4) ? null : r.GetString(4)),
                    ("$id", performance.Id));
                var positions = ScoreRanking.Positions(rows.Select(r => r.Score).ToList());
                views.Add(new PerformanceView(performance, rows
                    .Select((r, i) => new PerformanceResultView(r.PlayerId, r.Name, r.Slot, r.Score, positions[i],
                        r.Comment))
                    .ToList()));
            }

            return views;
        }
    }

    /// <summary>Todo lo que cantó un jugador (también estando archivado), de lo más nuevo a lo más viejo.</summary>
    public IReadOnlyList<PlayerHistoryRow> GetPlayerHistory(long playerId)
    {
        lock (_gate)
        {
            return Query("""
                SELECT p.id, p.session_id, p.song_id, p.title, p.artist, p.finished_at, p.difficulty, p.scoring_version,
                       p.library_entry_id, p.edition, r.slot, r.score,
                       1 + (SELECT COUNT(*) FROM performance_result o
                            WHERE o.performance_id = r.performance_id AND o.score > r.score)
                FROM performance_result r JOIN performance p ON p.id = r.performance_id
                WHERE r.player_id = $player ORDER BY p.finished_at DESC, p.id
                """, r => new PlayerHistoryRow(ReadPerformance(r), r.GetInt64(1), r.GetInt32(10), r.GetInt32(11),
                r.GetInt32(12)), ("$player", playerId));
        }
    }

    // ---- Internos -----------------------------------------------------------------------------------------

    private static void ValidatePerformance(NewPerformance p)
    {
        if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.SongId))
        {
            throw new StoreRuleException("La interpretación necesita identificador y canción.");
        }

        if (p.Difficulty is < 0 or > 2)
        {
            throw new StoreRuleException("Dificultad fuera de rango.");
        }

        if (p.ScoringVersion < 1)
        {
            throw new StoreRuleException("Versión de puntuación inválida.");
        }

        if (p.Results.Count is < 1 or > MaxPlayersPerPerformance)
        {
            throw new StoreRuleException($"Una interpretación tiene de 1 a {MaxPlayersPerPerformance} resultados.");
        }

        if (p.Results.Any(r => r.Slot is < 1 or > MaxPlayersPerPerformance))
        {
            throw new StoreRuleException("El puesto debe estar entre P1 y P4.");
        }

        if (p.Results.Any(r => r.Score is < 0 or > MaxScore))
        {
            throw new StoreRuleException($"El puntaje debe estar entre 0 y {MaxScore}.");
        }

        if (p.Results.Select(r => r.Slot).Distinct().Count() != p.Results.Count
            || p.Results.Select(r => r.PlayerId).Distinct().Count() != p.Results.Count)
        {
            throw new StoreRuleException("Un puesto o un jugador no puede repetirse en la misma interpretación.");
        }
    }

    private static string ValidName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new StoreRuleException("El nombre no puede estar vacío.");
        }

        if (name.Length > MaxNameLength)
        {
            throw new StoreRuleException($"El nombre admite hasta {MaxNameLength} caracteres.");
        }

        return name;
    }

    private SessionRecord StartSessionUnlocked(string? name, IReadOnlyList<SelectionEntry>? selection)
    {
        var now = Stamp(_now());
        Execute("UPDATE session SET closed_at = $now, is_open = 0 WHERE is_open = 1", ("$now", now));
        Execute("INSERT INTO session (name, started_at, closed_at, is_open) VALUES ($name, $now, NULL, 1)",
            ("$name", string.IsNullOrWhiteSpace(name) ? null : name.Trim()), ("$now", now));
        var id = Convert.ToInt64(Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        if (selection is { Count: > 0 })
        {
            ApplySelectionUnlocked(id, selection, addMembers: true);
        }

        return Query("SELECT id, name, started_at, closed_at FROM session WHERE id = $id", ReadSession, ("$id", id))[0];
    }

    private void ApplySelectionUnlocked(long sessionId, IReadOnlyList<SelectionEntry> selection, bool addMembers)
    {
        if (selection.Count > MaxPlayersPerPerformance)
        {
            throw new StoreRuleException($"Hasta {MaxPlayersPerPerformance} participantes.");
        }

        if (selection.Any(s => s.Slot is < 1 or > MaxPlayersPerPerformance)
            || selection.Select(s => s.Slot).Distinct().Count() != selection.Count
            || selection.Select(s => s.PlayerId).Distinct().Count() != selection.Count)
        {
            throw new StoreRuleException("Puesto o jugador repetido, o puesto fuera de P1–P4.");
        }

        foreach (var entry in selection)
        {
            var player = GetPlayerUnlocked(entry.PlayerId)
                         ?? throw new StoreRuleException($"El jugador {entry.PlayerId} no existe.");
            if (player.Archived)
            {
                throw new StoreRuleException($"El jugador '{player.Name}' está archivado.");
            }

            if (!addMembers && Scalar("SELECT 1 FROM session_player WHERE session_id = $s AND player_id = $p",
                    ("$s", sessionId), ("$p", entry.PlayerId)) is null)
            {
                throw new StoreRuleException($"'{player.Name}' no está en la partida: hay que agregarlo primero.");
            }
        }

        var now = Stamp(_now());
        Execute("DELETE FROM session_selection WHERE session_id = $s", ("$s", sessionId));
        foreach (var entry in selection)
        {
            if (addMembers)
            {
                Execute("INSERT OR IGNORE INTO session_player (session_id, player_id, joined_at) VALUES ($s, $p, $now)",
                    ("$s", sessionId), ("$p", entry.PlayerId), ("$now", now));
            }

            Execute("INSERT INTO session_selection (session_id, slot, player_id) VALUES ($s, $slot, $p)",
                ("$s", sessionId), ("$slot", entry.Slot), ("$p", entry.PlayerId));
        }
    }

    private SessionRecord? OpenSessionUnlocked() =>
        Query("SELECT id, name, started_at, closed_at FROM session WHERE is_open = 1", ReadSession).FirstOrDefault();

    private PlayerRecord? GetPlayerUnlocked(long id) =>
        Query("SELECT id, name, character_id, created_at, last_activity_at, archived FROM player WHERE id = $id",
            ReadPlayer, ("$id", id)).FirstOrDefault();

    private PlayerRecord RequirePlayer(long id) =>
        GetPlayerUnlocked(id) ?? throw new StoreRuleException($"El jugador {id} no existe.");

    private static PlayerRecord ReadPlayer(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), Parse(r.GetString(3)), Parse(r.GetString(4)), r.GetInt32(5) == 1);

    private static SessionRecord ReadSession(SqliteDataReader r) => new(
        r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), Parse(r.GetString(2)),
        r.IsDBNull(3) ? null : Parse(r.GetString(3)));

    private static PerformanceRecord ReadPerformance(SqliteDataReader r) => new(
        r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), Parse(r.GetString(5)),
        r.GetInt32(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9));

    private static string Stamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }
}
