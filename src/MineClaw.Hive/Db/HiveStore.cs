using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Ipc;
using Microsoft.Data.Sqlite;

namespace MineClaw.Hive.Db;

/// <summary>A clone's login history: <see cref="Joined"/> = it has been in game before; <see cref="Good"/> = last position on
/// verified ground (x, y, z, dimension, when); <see cref="Void"/> = where it was left holding over the void (its saved spot).</summary>
public sealed record CloneHistory(bool Joined, (double X, double Y, double Z, int Dimension, DateTimeOffset At)? Good, (double X, double Y, double Z)? Void);

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
            // Last known body (hp/food survive a logout; main restarts lost them: live 10-27 15:44 69's hp 20/food 20 was unknown).
            foreach (var c in new[] { "hp", "food" }) db.EnsureColumn("hive_inventory", c, "INTEGER");
            foreach (var c in new[] { "pos_x", "pos_y", "pos_z" }) db.EnsureColumn("hive_inventory", c, "REAL");
            db.EnsureColumn("hive_inventory", "vitals_at", "INTEGER");
            // Where a clone was left holding over the void (its saved login spot: it would spawn there again).
            foreach (var c in new[] { "void_x", "void_y", "void_z" }) db.EnsureColumn("hive_inventory", c, "REAL");
            db.EnsureColumn("hive_inventory", "void_at", "INTEGER");
            // First time in game ("never joined" = null: its first login lands at the server's fresh-player spawn) and the last
            // position it stood on verified ground (never a void hold).
            db.EnsureColumn("hive_inventory", "joined_at", "INTEGER");
            foreach (var c in new[] { "good_x", "good_y", "good_z" }) db.EnsureColumn("hive_inventory", c, "REAL");
            db.EnsureColumn("hive_inventory", "good_dim", "INTEGER");
            db.EnsureColumn("hive_inventory", "good_at", "INTEGER");
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

    /// <summary>The clone's last known hp/food/position (kept when items change; items untouched).</summary>
    public void SaveVitals(string clone, int hp, int food, double? x, double? y, double? z, int? dimension = null) =>
        Db.Exec("""
            INSERT INTO hive_inventory(clone, items, updated, dimension, hp, food, pos_x, pos_y, pos_z, vitals_at)
            VALUES ($c, '[]', $u, coalesce($d, 0), $hp, $f, $x, $y, $z, $u)
            ON CONFLICT(clone) DO UPDATE SET hp = $hp, food = $f, pos_x = coalesce($x, pos_x), pos_y = coalesce($y, pos_y),
                pos_z = coalesce($z, pos_z), vitals_at = $u, dimension = coalesce($d, dimension)
            """, ("$c", clone), ("$u", Now), ("$d", dimension), ("$hp", hp), ("$f", food), ("$x", x), ("$y", y), ("$z", z));

    /// <summary>(hp, food, x, y, z, dimension, at) last saved for a clone; null = never seen.</summary>
    public (int Hp, int Food, double? X, double? Y, double? Z, int Dimension, DateTimeOffset At)? Vitals(string clone) =>
        Db.Query("SELECT hp, food, pos_x, pos_y, pos_z, dimension, vitals_at FROM hive_inventory WHERE clone = $c AND hp IS NOT NULL",
            r => ((int Hp, int Food, double? X, double? Y, double? Z, int Dimension, DateTimeOffset At)?)(r.GetInt32(0), r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetDouble(2), r.IsDBNull(3) ? null : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.GetInt32(5), AgentDb.FromUnixMs(r.IsDBNull(6) ? 0 : r.GetInt64(6))), ("$c", clone)).FirstOrDefault();

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

    // ---------------- void spawns ----------------

    /// <summary>
    /// The clone is holding still over the void at (x, y, z): the server saves that spot, so its next login lands there again
    /// (live 10-27 07:49/07:51: fresh maid_chan_420 spawned twice at -5.5,0.62,-8.5). Cleared once it stands on ground.
    /// </summary>
    public void MarkVoidSpawn(string clone, double x, double y, double z) =>
        Db.Exec("""
            INSERT INTO hive_inventory(clone, items, updated, joined_at, void_x, void_y, void_z, void_at) VALUES ($c, '[]', $u, $u, $x, $y, $z, $u)
            ON CONFLICT(clone) DO UPDATE SET void_x = $x, void_y = $y, void_z = $z, void_at = $u, joined_at = coalesce(joined_at, $u)
            """, ("$c", clone), ("$u", Now), ("$x", x), ("$y", y), ("$z", z));

    public void ClearVoidSpawn(string clone) =>
        Db.Exec("UPDATE hive_inventory SET void_x = NULL, void_y = NULL, void_z = NULL, void_at = NULL WHERE clone = $c", ("$c", clone));

    /// <summary>Where the clone was last left over the void (its saved login spot), or null.</summary>
    public (double X, double Y, double Z, DateTimeOffset At)? VoidSpawn(string clone) =>
        Db.Query("SELECT void_x, void_y, void_z, void_at FROM hive_inventory WHERE clone = $c AND void_x IS NOT NULL",
            r => ((double X, double Y, double Z, DateTimeOffset At)?)(r.GetDouble(0), r.GetDouble(1), r.GetDouble(2),
                AgentDb.FromUnixMs(r.IsDBNull(3) ? 0 : r.GetInt64(3))), ("$c", clone)).FirstOrDefault();

    // ---------------- join history ----------------

    /// <summary>The clone is in game (first time = its joined_at; later calls keep the first).</summary>
    public void NoteJoined(string clone) =>
        Db.Exec("""
            INSERT INTO hive_inventory(clone, items, updated, joined_at) VALUES ($c, '[]', $u, $u)
            ON CONFLICT(clone) DO UPDATE SET joined_at = coalesce(joined_at, $u)
            """, ("$c", clone), ("$u", Now));

    /// <summary>The clone stands on verified ground here (its last known good position).</summary>
    public void SaveGoodPosition(string clone, double x, double y, double z, int dimension) =>
        Db.Exec("""
            INSERT INTO hive_inventory(clone, items, updated, joined_at, good_x, good_y, good_z, good_dim, good_at)
            VALUES ($c, '[]', $u, $u, $x, $y, $z, $d, $u)
            ON CONFLICT(clone) DO UPDATE SET joined_at = coalesce(joined_at, $u), good_x = $x, good_y = $y, good_z = $z, good_dim = $d, good_at = $u
            """, ("$c", clone), ("$u", Now), ("$x", x), ("$y", y), ("$z", z), ("$d", dimension));

    /// <summary>
    /// What the hive knows about a clone's logins: joined before (joined_at, or any saved vitals: rows from before the column),
    /// its last good position, and where it was left over the void. A saved position below y 1 in the overworld counts as a
    /// void spot too (nothing stands there in a skyblock; 420's 07:49 vitals were saved at -5.5,0.62,-8.5 before void marks).
    /// </summary>
    public CloneHistory History(string clone)
    {
        var row = Db.Query("""
            SELECT joined_at, hp, good_x, good_y, good_z, good_dim, good_at, void_x, void_y, void_z, pos_x, pos_y, pos_z, dimension
            FROM hive_inventory WHERE clone = $c
            """, r => (Joined: !r.IsDBNull(0) || !r.IsDBNull(1),
                Good: r.IsDBNull(2) ? ((double, double, double, int, DateTimeOffset)?)null
                    : (r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.IsDBNull(5) ? 0 : r.GetInt32(5), AgentDb.FromUnixMs(r.IsDBNull(6) ? 0 : r.GetInt64(6))),
                Void: r.IsDBNull(7) ? ((double, double, double)?)null : (r.GetDouble(7), r.GetDouble(8), r.GetDouble(9)),
                Pos: r.IsDBNull(10) || r.IsDBNull(11) || r.IsDBNull(12) ? ((double, double, double)?)null : (r.GetDouble(10), r.GetDouble(11), r.GetDouble(12)),
                Dim: r.GetInt32(13)), ("$c", clone)).FirstOrDefault();
        if (row == default) return new CloneHistory(false, null, null);
        var voidAt = row.Void ?? (row.Pos is { } p && row.Dim == 0 && p.Item2 < 1.0 && row.Good is null ? p : null);
        return new CloneHistory(row.Joined, row.Good, voidAt);
    }

    /// <summary>Where a fresh account's first login landed over the void (any clone's first-login void hold, else any saved
    /// overworld position below y 1), or null when the hive has never seen one.</summary>
    public (double X, double Y, double Z)? FreshVoidSpawn()
    {
        var t = Db.Query("SELECT text FROM hive_events WHERE kind = 'fresh_void_spawn' ORDER BY at DESC LIMIT 1", r => r.GetString(0)).FirstOrDefault();
        if (t != null && t.Split(',') is { Length: 3 } parts && parts.All(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            var v = parts.Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return (v[0], v[1], v[2]);
        }
        return Db.Query("SELECT pos_x, pos_y, pos_z FROM hive_inventory WHERE dimension = 0 AND pos_y IS NOT NULL AND pos_y < 1.0 ORDER BY vitals_at DESC LIMIT 1",
            r => ((double, double, double)?)(r.GetDouble(0), r.GetDouble(1), r.GetDouble(2))).FirstOrDefault();
    }

    /// <summary>A clone's FIRST login held over the void here: the server's fresh-player spawn (world spawn ±10, top block).</summary>
    public void NoteFreshVoidSpawn(string clone, double x, double y, double z) =>
        RecordOnce("fresh_void_spawn", "fresh_void_spawn:" + clone, clone,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x:0.###},{y:0.###},{z:0.###}"));

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
