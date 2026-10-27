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
    ];

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

    private const string RtreeSql = """
        CREATE VIRTUAL TABLE docs_rtree USING rtree(id, min_x, max_x, min_y, max_y, min_z, max_z);
        CREATE TRIGGER docs_rt_ai AFTER INSERT ON docs WHEN new.min_x IS NOT NULL BEGIN
            INSERT INTO docs_rtree VALUES (new.id, new.min_x, new.max_x, new.min_y, new.max_y, new.min_z, new.max_z); END;
        CREATE TRIGGER docs_rt_ad AFTER DELETE ON docs BEGIN DELETE FROM docs_rtree WHERE id = old.id; END;
        CREATE TRIGGER docs_rt_au AFTER UPDATE OF min_x, min_y, min_z, max_x, max_y, max_z ON docs BEGIN
            DELETE FROM docs_rtree WHERE id = old.id;
            INSERT INTO docs_rtree SELECT new.id, new.min_x, new.max_x, new.min_y, new.max_y, new.min_z, new.max_z WHERE new.min_x IS NOT NULL; END;
        INSERT INTO docs_rtree SELECT id, min_x, max_x, min_y, max_y, min_z, max_z FROM docs WHERE min_x IS NOT NULL;
        """;

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
            Tx(() =>
            {
                Exec(Migrations[v]);
                Exec($"PRAGMA user_version = {v + 1};");
            });
            log.LogInformation("db schema migrated to v{Version}", v + 1);
        }
        SchemaVersion = Migrations.Length;

        bool ftsOk = !disableFts && Probe("CREATE VIRTUAL TABLE temp.__fts_probe USING fts5(x); DROP TABLE temp.__fts_probe;");
        bool rtOk = !disableRtree && Probe("CREATE VIRTUAL TABLE temp.__rt_probe USING rtree(id, a, b); DROP TABLE temp.__rt_probe;");
        HasFts = ftsOk && EnsureVirtual("docs_fts", FtsSql, log);
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
