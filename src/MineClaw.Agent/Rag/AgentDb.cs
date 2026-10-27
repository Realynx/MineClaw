using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Rag;

/// <summary>
/// The agent's single SQLite database (memory, RAG index, chat log, world observations, self-profile).
/// One connection, serialized by <see cref="Gate"/> (re-entrant), WAL mode, versioned migrations
/// (<c>PRAGMA user_version</c>). FTS5 and R*Tree are probed at open; when missing the store degrades to
/// LIKE scans and plain range queries. A null path opens a private in-memory database (tests, persist=false).
/// </summary>
public sealed class AgentDb : IDisposable
{
    /// <summary>Ordered migrations; index i upgrades user_version i → i+1. Append only.</summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE docs(
            id INTEGER PRIMARY KEY,
            ref TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL,
            tag TEXT,
            title TEXT NOT NULL,
            body TEXT NOT NULL,
            hash TEXT NOT NULL,
            source TEXT,
            embed INTEGER NOT NULL DEFAULT 1,
            weight REAL NOT NULL DEFAULT 1.0,
            created INTEGER NOT NULL,
            updated INTEGER NOT NULL,
            min_x REAL, min_y REAL, min_z REAL, max_x REAL, max_y REAL, max_z REAL);
        CREATE INDEX docs_kind ON docs(kind, updated);
        CREATE INDEX docs_source ON docs(source);
        CREATE TABLE vectors(
            doc_id INTEGER PRIMARY KEY REFERENCES docs(id) ON DELETE CASCADE,
            model TEXT NOT NULL, dims INTEGER NOT NULL, hash TEXT NOT NULL, vec BLOB NOT NULL);
        CREATE TABLE embed_cache(
            hash TEXT NOT NULL, model TEXT NOT NULL, dims INTEGER NOT NULL, vec BLOB NOT NULL, created INTEGER NOT NULL,
            PRIMARY KEY(hash, model, dims)) WITHOUT ROWID;
        CREATE TABLE chat_log(id INTEGER PRIMARY KEY, at INTEGER NOT NULL, kind TEXT NOT NULL, player TEXT, text TEXT NOT NULL);
        CREATE INDEX chat_log_at ON chat_log(at);
        CREATE INDEX chat_log_player ON chat_log(player COLLATE NOCASE, at);
        CREATE TABLE knowledge_files(path TEXT PRIMARY KEY, hash TEXT NOT NULL, indexed INTEGER NOT NULL);
        CREATE TABLE waypoints(name TEXT PRIMARY KEY COLLATE NOCASE, x INTEGER NOT NULL, y INTEGER NOT NULL, z INTEGER NOT NULL,
            note TEXT, updated INTEGER NOT NULL);
        CREATE TABLE structures(name TEXT PRIMARY KEY COLLATE NOCASE, kind TEXT NOT NULL, taught_by TEXT, confidence REAL NOT NULL,
            data TEXT NOT NULL, updated INTEGER NOT NULL);
        CREATE TABLE notes(id INTEGER PRIMARY KEY, subject TEXT NOT NULL, text TEXT NOT NULL, created INTEGER NOT NULL);
        CREATE TABLE routines(name TEXT PRIMARY KEY COLLATE NOCASE, data TEXT NOT NULL, updated INTEGER NOT NULL);
        CREATE TABLE identity(id INTEGER PRIMARY KEY CHECK (id = 1), display_name TEXT NOT NULL, aliases TEXT NOT NULL,
            named_by TEXT, updated INTEGER NOT NULL);
        CREATE TABLE observations(
            id INTEGER PRIMARY KEY, kind TEXT NOT NULL, key TEXT NOT NULL,
            min_x REAL, min_y REAL, min_z REAL, max_x REAL, max_y REAL, max_z REAL,
            fields TEXT, text TEXT NOT NULL, source TEXT NOT NULL, confidence REAL NOT NULL, annotation TEXT,
            first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, seen_count INTEGER NOT NULL, removed INTEGER,
            UNIQUE(kind, key));
        CREATE INDEX observations_kind ON observations(kind, removed);
        CREATE TABLE container_items(
            obs_id INTEGER NOT NULL REFERENCES observations(id) ON DELETE CASCADE,
            item_id INTEGER NOT NULL, damage INTEGER NOT NULL DEFAULT 0, name TEXT NOT NULL, count INTEGER NOT NULL,
            PRIMARY KEY(obs_id, item_id, damage));
        CREATE INDEX container_items_item ON container_items(item_id);
        CREATE INDEX container_items_name ON container_items(name);
        CREATE TABLE self_profile(id INTEGER PRIMARY KEY, section TEXT NOT NULL, player TEXT, text TEXT NOT NULL,
            strength REAL NOT NULL, created INTEGER NOT NULL, reinforced INTEGER NOT NULL, reason TEXT);
        CREATE TABLE self_profile_history(version INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, op TEXT NOT NULL,
            section TEXT, entry_id INTEGER, text TEXT, reason TEXT, source TEXT NOT NULL, episodes TEXT, snapshot TEXT NOT NULL);
        """,
        // v2: generic node shape for hierarchical (RAPTOR-style) indexes: docs are nodes (level 0 = leaf, >0 = summary),
        // edges allow multiple parents (soft clustering), token_count for budgeting, dirty = text changed since last tree build.
        """
        ALTER TABLE docs ADD COLUMN level INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE docs ADD COLUMN token_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE docs ADD COLUMN dirty INTEGER NOT NULL DEFAULT 1;
        CREATE INDEX docs_level ON docs(level, kind);
        CREATE TABLE doc_edges(
            parent INTEGER NOT NULL REFERENCES docs(id) ON DELETE CASCADE,
            child INTEGER NOT NULL REFERENCES docs(id) ON DELETE CASCADE,
            weight REAL NOT NULL DEFAULT 1.0,
            PRIMARY KEY(parent, child)) WITHOUT ROWID;
        CREATE INDEX doc_edges_child ON doc_edges(child);
        UPDATE docs SET token_count = (length(title) + length(body) + 3) / 4;
        """,
        // v3: RAPTOR tree mapping (Rag/Raptor/AgentDbRaptorStore). raptor_idx = RaptorNode.Index (leaves: the doc itself;
        // summaries: kind 'summary', ref 'raptor:{idx}'); raptor_flags = pinned 1 | dirty 2 | summary-pending 4;
        // raptor_seq = insertion sequence. Tree metadata lives in meta('raptor_meta').
        """
        ALTER TABLE docs ADD COLUMN raptor_idx INTEGER;
        ALTER TABLE docs ADD COLUMN raptor_flags INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE docs ADD COLUMN raptor_seq INTEGER NOT NULL DEFAULT 0;
        CREATE UNIQUE INDEX docs_raptor ON docs(raptor_idx) WHERE raptor_idx IS NOT NULL;
        """,
        // v4: belief system (Rag/Beliefs). Player-told facts are claims with a status, never facts; evidence rows link
        // seed refs (gd:*), observation refs (obs:*) and experiment results. IF NOT EXISTS: hive processes share the file.
        """
        CREATE TABLE IF NOT EXISTS claims(
            id INTEGER PRIMARY KEY, claimant TEXT NOT NULL, text TEXT NOT NULL, norm TEXT NOT NULL, spo TEXT,
            subject TEXT, predicate TEXT, object TEXT, x INTEGER, y INTEGER, z INTEGER, tx INTEGER, ty INTEGER, tz INTEGER,
            category TEXT NOT NULL, stakes TEXT NOT NULL, confidence REAL NOT NULL, status TEXT NOT NULL, reason TEXT,
            plan TEXT NOT NULL DEFAULT 'none', risk TEXT NOT NULL DEFAULT 'safe', directive INTEGER NOT NULL DEFAULT 0,
            time_based INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0, checked INTEGER,
            created INTEGER NOT NULL, updated INTEGER NOT NULL, clone_id TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS claims_norm ON claims(norm);
        CREATE INDEX IF NOT EXISTS claims_spo ON claims(spo);
        CREATE INDEX IF NOT EXISTS claims_plan ON claims(plan, status);
        CREATE TABLE IF NOT EXISTS claim_sources(
            claim_id INTEGER NOT NULL REFERENCES claims(id) ON DELETE CASCADE, claimant TEXT NOT NULL COLLATE NOCASE,
            at INTEGER NOT NULL, signaled TEXT, PRIMARY KEY(claim_id, claimant)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS claim_evidence(
            id INTEGER PRIMARY KEY, claim_id INTEGER NOT NULL REFERENCES claims(id) ON DELETE CASCADE, kind TEXT NOT NULL,
            ref TEXT, polarity INTEGER NOT NULL, weight REAL NOT NULL, note TEXT, at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS claim_evidence_claim ON claim_evidence(claim_id, at);
        """,
        // v5: dimension-aware memory (0 overworld, -1 nether). Every position-bearing row gets `dimension` (backfilled 0)
        // by MigrateV5 (column adds are idempotent: hive processes share the file); observations are rebuilt so the
        // idempotency key is (kind, key, dimension); the R*Tree gains a dimension axis (Init). Portal pairs live here.
        """
        CREATE TABLE IF NOT EXISTS portal_links(
            id INTEGER PRIMARY KEY, a_dim INTEGER NOT NULL, a_key TEXT NOT NULL, a_x INTEGER NOT NULL, a_y INTEGER NOT NULL, a_z INTEGER NOT NULL,
            b_dim INTEGER NOT NULL, b_key TEXT NOT NULL, b_x INTEGER NOT NULL, b_y INTEGER NOT NULL, b_z INTEGER NOT NULL,
            created INTEGER NOT NULL, updated INTEGER NOT NULL, uses INTEGER NOT NULL DEFAULT 1,
            UNIQUE(a_dim, a_key, b_dim, b_key));
        CREATE INDEX IF NOT EXISTS portal_links_a ON portal_links(a_dim, a_x, a_z);
        CREATE INDEX IF NOT EXISTS portal_links_b ON portal_links(b_dim, b_x, b_z);
        """,
    ];

    /// <summary>Code steps run after the SQL of the same index (same transaction).</summary>
    private static readonly Dictionary<int, Action<AgentDb>> CodeMigrations = new() { [4] = MigrateV5 };

    /// <summary>Base columns of observations (v1) in order; anything else found (hive clone_id...) is carried over verbatim.</summary>
    private static readonly string[] ObservationColumns =
    [
        "id", "kind", "key", "min_x", "min_y", "min_z", "max_x", "max_y", "max_z", "fields", "text", "source", "confidence", "annotation",
        "first_seen", "last_seen", "seen_count", "removed",
    ];

    private static void MigrateV5(AgentDb db)
    {
        foreach (var t in new[] { "docs", "waypoints", "structures", "claims" })
            db.EnsureColumn(t, "dimension", "INTEGER NOT NULL DEFAULT 0");
        if (db.TableExists("observations") && !db.HasColumn("observations", "dimension")) RebuildObservations(db);
    }

    /// <summary>
    /// UNIQUE(kind, key) can't be altered in SQLite: copy observations + container_items into v5 tables keyed by
    /// (kind, key, dimension), every existing row as overworld. container_items is copied out first so dropping the
    /// parent doesn't cascade-delete contents. Unknown extra columns (hive clone_id) are carried over.
    /// </summary>
    private static void RebuildObservations(AgentDb db)
    {
        static List<(string Name, string Decl)> Extras(AgentDb db, string table, ICollection<string> known) =>
            db.Query($"PRAGMA table_info({table})", r => (Name: r.GetString(1), Type: r.IsDBNull(2) ? "" : r.GetString(2),
                    NotNull: r.GetInt64(3) != 0, Default: r.IsDBNull(4) ? null : r.GetValue(4)?.ToString()))
                .Where(c => !known.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                .Select(c => (c.Name, $"{c.Name} {c.Type}" + (c.NotNull && c.Default != null ? " NOT NULL" : "") + (c.Default != null ? $" DEFAULT {c.Default}" : "")))
                .ToList();
        static string Decls(List<(string Name, string Decl)> e) => string.Concat(e.Select(x => ", " + x.Decl));
        static string Names(List<(string Name, string Decl)> e) => string.Concat(e.Select(x => ", " + x.Name));
        var obsExtra = Extras(db, "observations", ObservationColumns);
        string[] ciBase = ["obs_id", "item_id", "damage", "name", "count"];
        var ciExtra = Extras(db, "container_items", ciBase);
        var obsCols = string.Join(", ", ObservationColumns) + Names(obsExtra);
        var ciCols = string.Join(", ", ciBase) + Names(ciExtra);
        db.Exec($"""
            CREATE TABLE observations_v5(
                id INTEGER PRIMARY KEY, kind TEXT NOT NULL, key TEXT NOT NULL,
                min_x REAL, min_y REAL, min_z REAL, max_x REAL, max_y REAL, max_z REAL,
                fields TEXT, text TEXT NOT NULL, source TEXT NOT NULL, confidence REAL NOT NULL, annotation TEXT,
                first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, seen_count INTEGER NOT NULL, removed INTEGER{Decls(obsExtra)},
                dimension INTEGER NOT NULL DEFAULT 0,
                UNIQUE(kind, key, dimension));
            INSERT INTO observations_v5({obsCols}, dimension) SELECT {obsCols}, 0 FROM observations;
            CREATE TABLE container_items_v5 AS SELECT * FROM container_items;
            DROP TABLE container_items;
            DROP TABLE observations;
            ALTER TABLE observations_v5 RENAME TO observations;
            CREATE TABLE container_items(
                obs_id INTEGER NOT NULL REFERENCES observations(id) ON DELETE CASCADE,
                item_id INTEGER NOT NULL, damage INTEGER NOT NULL DEFAULT 0, name TEXT NOT NULL, count INTEGER NOT NULL{Decls(ciExtra)},
                dimension INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(obs_id, item_id, damage));
            INSERT INTO container_items({ciCols}, dimension) SELECT {ciCols}, 0 FROM container_items_v5;
            DROP TABLE container_items_v5;
            CREATE INDEX observations_kind ON observations(kind, removed);
            CREATE INDEX observations_dim ON observations(dimension, kind, removed);
            CREATE INDEX container_items_item ON container_items(item_id);
            CREATE INDEX container_items_name ON container_items(name);
            """);
    }

    private const string FtsSql = """
        CREATE VIRTUAL TABLE docs_fts USING fts5(title, body, content='docs', content_rowid='id', tokenize='porter unicode61 remove_diacritics 2');
        CREATE TRIGGER docs_fts_ai AFTER INSERT ON docs BEGIN
            INSERT INTO docs_fts(rowid, title, body) VALUES (new.id, new.title, new.body); END;
        CREATE TRIGGER docs_fts_ad AFTER DELETE ON docs BEGIN
            INSERT INTO docs_fts(docs_fts, rowid, title, body) VALUES ('delete', old.id, old.title, old.body); END;
        CREATE TRIGGER docs_fts_au AFTER UPDATE OF title, body ON docs BEGIN
            INSERT INTO docs_fts(docs_fts, rowid, title, body) VALUES ('delete', old.id, old.title, old.body);
            INSERT INTO docs_fts(rowid, title, body) VALUES (new.id, new.title, new.body); END;
        CREATE VIRTUAL TABLE chat_fts USING fts5(player, text, content='chat_log', content_rowid='id', tokenize='porter unicode61 remove_diacritics 2');
        CREATE TRIGGER chat_fts_ai AFTER INSERT ON chat_log BEGIN
            INSERT INTO chat_fts(rowid, player, text) VALUES (new.id, new.player, new.text); END;
        CREATE TRIGGER chat_fts_ad AFTER DELETE ON chat_log BEGIN
            INSERT INTO chat_fts(chat_fts, rowid, player, text) VALUES ('delete', old.id, old.player, old.text); END;
        INSERT INTO docs_fts(docs_fts) VALUES ('rebuild');
        INSERT INTO chat_fts(chat_fts) VALUES ('rebuild');
        """;

    /// <summary>
    /// Spatial index with the dimension as a 4th axis (min_d = max_d = dimension): one R*Tree, and a spatial match in one
    /// dimension never visits the other's nodes (faster than a post-join filter, simpler than one tree per dimension).
    /// </summary>
    private const string RtreeSql = """
        CREATE VIRTUAL TABLE docs_rtree USING rtree(id, min_x, max_x, min_y, max_y, min_z, max_z, min_d, max_d);
        CREATE TRIGGER docs_rt_ai AFTER INSERT ON docs WHEN new.min_x IS NOT NULL BEGIN
            INSERT INTO docs_rtree VALUES (new.id, new.min_x, new.max_x, new.min_y, new.max_y, new.min_z, new.max_z, new.dimension, new.dimension); END;
        CREATE TRIGGER docs_rt_ad AFTER DELETE ON docs BEGIN DELETE FROM docs_rtree WHERE id = old.id; END;
        CREATE TRIGGER docs_rt_au AFTER UPDATE OF min_x, min_y, min_z, max_x, max_y, max_z, dimension ON docs BEGIN
            DELETE FROM docs_rtree WHERE id = old.id;
            INSERT INTO docs_rtree SELECT new.id, new.min_x, new.max_x, new.min_y, new.max_y, new.min_z, new.max_z, new.dimension, new.dimension
                WHERE new.min_x IS NOT NULL; END;
        INSERT INTO docs_rtree SELECT id, min_x, max_x, min_y, max_y, min_z, max_z, dimension, dimension FROM docs WHERE min_x IS NOT NULL;
        """;

    /// <summary>A pre-v5 tree (no dimension axis) is dropped here and rebuilt from docs by <see cref="EnsureVirtual"/>.</summary>
    private void DropStaleRtree(ILogger log)
    {
        if (Scalar("SELECT sql FROM sqlite_master WHERE name = 'docs_rtree'") is not string sql
            || sql.Contains("min_d", StringComparison.OrdinalIgnoreCase)) return;
        Tx(() => Exec("""
            DROP TRIGGER IF EXISTS docs_rt_ai; DROP TRIGGER IF EXISTS docs_rt_ad; DROP TRIGGER IF EXISTS docs_rt_au;
            DROP TABLE docs_rtree;
            """));
        log.LogInformation("R*Tree rebuilt with a dimension axis");
    }

    private readonly SqliteConnection _conn;
    private SqliteTransaction? _tx;
    private int _txDepth;
    private bool _disposed;

    private AgentDb(SqliteConnection conn, string? path)
    {
        _conn = conn;
        Path = path;
    }

    public string? Path { get; }
    /// <summary>Serializes all access; re-entrant (Monitor), so callers may hold it across several calls.</summary>
    public object Gate { get; } = new();
    public bool HasFts { get; private set; }
    public bool HasRtree { get; private set; }
    public int SchemaVersion { get; private set; }
    public static int LatestSchemaVersion => Migrations.Length;

    /// <param name="path">Database file (created if missing); null = in-memory.</param>
    /// <param name="seedPath">If <paramref name="path"/> doesn't exist yet and this file does, it is copied in first
    /// (pre-generated "clean state" knowledge); migrations then bring it up to date.</param>
    /// <param name="disableFts">Force the no-FTS5 fallback (tests).</param>
    public static AgentDb Open(string? path, ILogger? log = null, bool disableFts = false, bool disableRtree = false, string? seedPath = null)
    {
        log ??= NullLogger.Instance;
        if (path != null && seedPath != null && !File.Exists(path) && File.Exists(seedPath))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            File.Copy(seedPath, path);
            log.LogInformation("db bootstrapped from seed {Seed}", seedPath);
        }
        string cs;
        if (path == null) cs = "Data Source=:memory:;Pooling=False";
        else
        {
            var full = System.IO.Path.GetFullPath(path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            cs = new SqliteConnectionStringBuilder { DataSource = full, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        }
        var conn = new SqliteConnection(cs);
        conn.Open();
        var db = new AgentDb(conn, path);
        try
        {
            db.Init(log, disableFts, disableRtree);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
        return db;
    }

    private void Init(ILogger log, bool disableFts, bool disableRtree)
    {
        if (Path != null)
        {
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA synchronous=NORMAL;");
        }
        Exec("PRAGMA foreign_keys=ON;");
        Exec("PRAGMA busy_timeout=5000;");

        int version = (int)ScalarLong("PRAGMA user_version;");
        if (version > Migrations.Length)
            throw new InvalidOperationException($"database schema v{version} is newer than this build (v{Migrations.Length})");
        for (int v = version; v < Migrations.Length; v++)
        {
            bool ran = Tx(() =>
            {
                // Another hive process may have migrated between our read and this (IMMEDIATE) transaction.
                if ((int)ScalarLong("PRAGMA user_version;") > v) return false;
                Exec(Migrations[v]);
                if (CodeMigrations.TryGetValue(v, out var code)) code(this);
                Exec($"PRAGMA user_version = {v + 1};");
                return true;
            });
            if (ran) log.LogInformation("db schema migrated to v{Version}", v + 1);
        }
        SchemaVersion = Migrations.Length;

        bool ftsOk = !disableFts && Probe("CREATE VIRTUAL TABLE temp.__fts_probe USING fts5(x); DROP TABLE temp.__fts_probe;");
        bool rtOk = !disableRtree && Probe("CREATE VIRTUAL TABLE temp.__rt_probe USING rtree(id, a, b); DROP TABLE temp.__rt_probe;");
        HasFts = ftsOk && EnsureVirtual("docs_fts", FtsSql, log);
        if (rtOk) DropStaleRtree(log);
        HasRtree = rtOk && EnsureVirtual("docs_rtree", RtreeSql, log);
        if (!HasFts) log.LogWarning("SQLite FTS5 unavailable: memory search falls back to LIKE scans");
        if (!HasRtree) log.LogInformation("SQLite R*Tree unavailable: spatial queries use range scans");
    }

    private bool Probe(string sql)
    {
        try { Exec(sql); return true; }
        catch (SqliteException) { return false; }
    }

    private bool EnsureVirtual(string table, string sql, ILogger log)
    {
        if (ScalarLong("SELECT count(*) FROM sqlite_master WHERE name = $n", ("$n", table)) > 0) return true;
        try
        {
            Tx(() => Exec(sql));
            return true;
        }
        catch (SqliteException ex)
        {
            log.LogWarning(ex, "could not create {Table}", table);
            return false;
        }
    }

    // ---------------- helpers ----------------

    public bool TableExists(string table) =>
        ScalarLong("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $n", ("$n", table)) > 0;

    public bool HasColumn(string table, string column) =>
        Query($"PRAGMA table_info({table})", r => r.GetString(1)).Any(n => n.Equals(column, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a column when the table exists and lacks it (idempotent; safe when another process races us).</summary>
    public void EnsureColumn(string table, string column, string declaration)
    {
        lock (Gate)
        {
            if (!TableExists(table) || HasColumn(table, column)) return;
            try { Exec($"ALTER TABLE {table} ADD COLUMN {column} {declaration}"); }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
        }
    }

    public SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        lock (Gate)
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        lock (Gate)
        {
            using var cmd = Command(sql, args);
            var r = cmd.ExecuteScalar();
            return r is DBNull ? null : r;
        }
    }

    public long ScalarLong(string sql, params (string Name, object? Value)[] args) => Scalar(sql, args) is { } o ? Convert.ToInt64(o) : 0;

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        lock (Gate)
        {
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    /// <summary>Runs <paramref name="body"/> in a transaction (nested calls join the outer one).</summary>
    public void Tx(Action body) => Tx(() => { body(); return 0; });

    public T Tx<T>(Func<T> body)
    {
        lock (Gate)
        {
            if (_txDepth > 0)
            {
                _txDepth++;
                try { return body(); }
                finally { _txDepth--; }
            }
            _tx = _conn.BeginTransaction();
            _txDepth = 1;
            try
            {
                var result = body();
                _tx.Commit();
                return result;
            }
            catch
            {
                try { _tx.Rollback(); } catch (SqliteException) { }
                throw;
            }
            finally
            {
                _tx.Dispose();
                _tx = null;
                _txDepth = 0;
            }
        }
    }

    public string? GetMeta(string key) => Scalar("SELECT value FROM meta WHERE key = $k", ("$k", key)) as string;

    public void SetMeta(string key, string? value) =>
        Exec("INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

    public static long ToUnixMs(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    public static DateTimeOffset FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    public static string? StrOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static double? DblOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    public static long? LongOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            if (Path != null)
            {
                try { using var c = Command("PRAGMA wal_checkpoint(TRUNCATE);"); c.ExecuteNonQuery(); } catch (SqliteException) { }
            }
            _disposed = true;
            _conn.Dispose();
        }
    }
}
