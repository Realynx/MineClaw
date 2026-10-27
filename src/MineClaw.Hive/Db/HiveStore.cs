using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Ipc;
using Microsoft.Data.Sqlite;

namespace MineClaw.Hive.Db;

/// <summary>A clone's stranded inventory (it crashed or was killed holding these).</summary>
public sealed record StrandedRecord(string Bot, IReadOnlyList<ItemMsg> Items, DateTimeOffset At, string Reason)
{
    public int Count => Items.Sum(i => i.Count);
}

/// <summary>
/// Hive tables in the shared SQLite db (created idempotently here — not an AgentDb migration — so processes may open the
/// file in any order): live per-clone inventory snapshots + the stranded flag, and once-only hive events (deaths). Also
/// tags every row a process writes with its bot (<c>clone_id</c>) using TEMP triggers on this process's connection, so
/// no insert site has to change. Uses the AgentDb gate (one writer per process) and short IMMEDIATE transactions; other
/// processes are handled by WAL + busy_timeout.
/// </summary>
public sealed class HiveStore
{
    /// <summary>Tables whose rows carry the writing bot.</summary>
    public static readonly string[] TaggedTables = ["docs", "chat_log", "notes", "observations", "waypoints", "structures", "routines"];

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS hive_inventory(
            clone TEXT PRIMARY KEY COLLATE NOCASE, items TEXT NOT NULL, updated INTEGER NOT NULL,
            stranded INTEGER NOT NULL DEFAULT 0, stranded_at INTEGER, reason TEXT);
        CREATE TABLE IF NOT EXISTS hive_events(
            id INTEGER PRIMARY KEY, at INTEGER NOT NULL, clone TEXT NOT NULL, kind TEXT NOT NULL, key TEXT NOT NULL UNIQUE, text TEXT NOT NULL);
        """;

    public HiveStore(AgentDb db, string cloneId, TimeProvider? time = null)
    {
        Db = db;
        CloneId = cloneId;
        _time = time ?? TimeProvider.System;
        db.Tx(() =>
        {
            db.Exec(Schema);
            db.EnsureColumn("hive_inventory", "dimension", "INTEGER NOT NULL DEFAULT 0"); // where the clone was (0 overworld, -1 nether)
            foreach (var t in TaggedTables)
            {
                if (!TableExists(t) || HasColumn(t, "clone_id")) continue;
                try { db.Exec($"ALTER TABLE {t} ADD COLUMN clone_id TEXT"); }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
            }
        });
        // Per-connection identity + TEMP triggers (live only for this process's connection).
        db.Exec("CREATE TEMP TABLE IF NOT EXISTS hive_whoami(id TEXT NOT NULL)");
        db.Exec("DELETE FROM temp.hive_whoami");
        db.Exec("INSERT INTO temp.hive_whoami(id) VALUES ($c)", ("$c", cloneId));
        foreach (var t in TaggedTables)
        {
            if (!TableExists(t)) continue;
            db.Exec($"""
                CREATE TEMP TRIGGER IF NOT EXISTS hive_tag_{t} AFTER INSERT ON {t} WHEN new.clone_id IS NULL
                BEGIN UPDATE {t} SET clone_id = (SELECT id FROM temp.hive_whoami LIMIT 1) WHERE rowid = new.rowid; END;
                """);
        }
    }

    private readonly TimeProvider _time;
    public AgentDb Db { get; }
    public string CloneId { get; }

    private bool TableExists(string t) => Db.ScalarLong("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $n", ("$n", t)) > 0;
    private bool HasColumn(string t, string c) => Db.Query($"PRAGMA table_info({t})", r => r.GetString(1)).Any(n => n.Equals(c, StringComparison.OrdinalIgnoreCase));

    private long Now => AgentDb.ToUnixMs(_time.GetUtcNow());

    // ---------------- inventory ledger ----------------

    /// <summary>Live snapshot of a clone's inventory (keeps a pending stranded flag: only a deposit clears it).</summary>
    /// <param name="dimension">The clone's dimension when known (null keeps the stored one).</param>
    public void SaveInventory(string clone, IReadOnlyList<ItemMsg> items, int? dimension = null) =>
        Db.Exec("""
            INSERT INTO hive_inventory(clone, items, updated, dimension) VALUES ($c, $i, $u, coalesce($d, 0))
            ON CONFLICT(clone) DO UPDATE SET items = excluded.items, updated = excluded.updated, dimension = coalesce($d, dimension)
            """, ("$c", clone), ("$i", JsonSerializer.Serialize(items, HiveJson.Options)), ("$u", Now), ("$d", dimension));

    /// <summary>Dimension of the clone's last inventory snapshot (stranded items are there).</summary>
    public int InventoryDimension(string clone) =>
        (int)Db.ScalarLong("SELECT dimension FROM hive_inventory WHERE clone = $c", ("$c", clone));

    public IReadOnlyList<ItemMsg> Inventory(string clone) =>
        Db.Query("SELECT items FROM hive_inventory WHERE clone = $c", r => r.GetString(0), ("$c", clone)).FirstOrDefault() is { } j
            ? JsonSerializer.Deserialize<List<ItemMsg>>(j, HiveJson.Options) ?? [] : [];

    /// <summary>Crash/kill: the last snapshot becomes "stranded" (the next spawn of that clone deposits first). Returns the item count.</summary>
    public int MarkStranded(string clone, string reason, IReadOnlyList<ItemMsg>? latest = null)
    {
        return Db.Tx(() =>
        {
            if (latest != null) SaveInventory(clone, latest);
            var items = Inventory(clone);
            int n = items.Sum(i => i.Count);
            if (n == 0) return 0;
            Db.Exec("UPDATE hive_inventory SET stranded = 1, stranded_at = $t, reason = $r WHERE clone = $c", ("$t", Now), ("$r", reason), ("$c", clone));
            return n;
        });
    }

    public StrandedRecord? Stranded(string clone) =>
        Db.Query("SELECT items, stranded_at, reason FROM hive_inventory WHERE clone = $c AND stranded = 1",
            r => new StrandedRecord(clone, JsonSerializer.Deserialize<List<ItemMsg>>(r.GetString(0), HiveJson.Options) ?? [],
                AgentDb.FromUnixMs(r.IsDBNull(1) ? 0 : r.GetInt64(1)), AgentDb.StrOrNull(r, 2) ?? ""), ("$c", clone)).FirstOrDefault();

    public void ClearStranded(string clone) =>
        Db.Exec("UPDATE hive_inventory SET stranded = 0, stranded_at = NULL, reason = NULL WHERE clone = $c", ("$c", clone));

    // ---------------- once-only events ----------------

    /// <summary>Records a hive event once (by key). False if it was already recorded (e.g. by another process).</summary>
    public bool RecordOnce(string kind, string key, string clone, string text) =>
        Db.Exec("INSERT OR IGNORE INTO hive_events(at, clone, kind, key, text) VALUES ($a, $c, $k, $key, $t)",
            ("$a", Now), ("$c", clone), ("$k", kind), ("$key", key), ("$t", text)) > 0;

    public int CountEvents(string kind, string? clone = null) =>
        (int)Db.ScalarLong("SELECT count(*) FROM hive_events WHERE kind = $k AND ($c IS NULL OR clone = $c)", ("$k", kind), ("$c", clone));

    public static string FormatItems(IEnumerable<ItemMsg> items, int max = 6)
    {
        var list = items.Where(i => i.Count > 0).OrderByDescending(i => i.Count).ToList();
        if (list.Count == 0) return "nothing";
        var s = string.Join(", ", list.Take(max).Select(i => $"{i.Count} {i.Name}"));
        return list.Count > max ? s + $" +{list.Count - max} more" : s;
    }
}

/// <summary>
/// Polls <c>PRAGMA data_version</c> (it changes only when ANOTHER connection committed) and refreshes this process's
/// in-memory caches: memory model, vector cache, and (debounced) the RAPTOR tree. Cheap: one pragma every interval.
/// </summary>
public sealed class DataVersionWatcher : IDisposable
{
    private readonly AgentDb _db;
    private readonly Action _onChange;
    private readonly Action? _slowRefresh;
    private readonly TimeSpan _slowEvery;
    private readonly Timer _timer;
    private long _last;
    private DateTimeOffset _lastSlow = DateTimeOffset.MinValue;
    private int _busy;
    private bool _slowPending;

    public DataVersionWatcher(AgentDb db, Action onChange, TimeSpan? every = null, Action? slowRefresh = null, TimeSpan? slowEvery = null)
    {
        _db = db;
        _onChange = onChange;
        _slowRefresh = slowRefresh;
        _slowEvery = slowEvery ?? TimeSpan.FromMinutes(2);
        _last = Read();
        var e = every ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => Tick(), null, e, e);
    }

    public int Refreshes { get; private set; }

    private long Read() { try { return _db.ScalarLong("PRAGMA data_version"); } catch (Exception) { return _last; } }

    /// <summary>One poll (public for tests).</summary>
    public bool Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return false;
        try
        {
            var v = Read();
            bool changed = v != _last;
            _last = v;
            if (changed)
            {
                Refreshes++;
                try { _onChange(); } catch (Exception) { }
                _slowPending = true;
            }
            if (_slowPending && _slowRefresh != null && DateTimeOffset.UtcNow - _lastSlow >= _slowEvery)
            {
                _slowPending = false;
                _lastSlow = DateTimeOffset.UtcNow;
                try { _slowRefresh(); } catch (Exception) { }
            }
            return changed;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
