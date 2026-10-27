using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Rag;
using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

/// <summary>A closed activity session: "kiwi mined 23 cobblestone at the cobble gen (10-26 14:02-14:06)".</summary>
public sealed record ActivityRollup(string Player, DateTimeOffset Start, DateTimeOffset End, string Text, double Confidence)
{
    public long Id { get; init; }
    public PlaceRef? Place { get; init; }
    public BlockPos Min { get; init; }
    public BlockPos Max { get; init; }
    /// <summary>Category → count (structured, for later batch summarisation).</summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();
    public int Events { get; init; }
    /// <summary>Digest salience hint (deaths, big builds, unknown structures score higher).</summary>
    public double Salience { get; init; }
    /// <summary>Compact digest form without the time range.</summary>
    public string Short { get; init; } = "";
}

/// <summary>
/// Raw player-activity events (<c>activity_log</c>: structured columns + FTS5, never embedded) and their rollups
/// (<c>activity_rollups</c>; each also an embedded <c>episode</c> doc in <see cref="RagIndex"/> so recall/auto-retrieval
/// find them). Lives in the agent's SQLite db; tables are created here (Bot-owned), not by Agent migrations. Thread-safe.
/// </summary>
public sealed class ActivityLog
{
    public const string DocPrefix = "act:";

    private readonly AgentDb _db;
    private readonly RagIndex? _rag;
    private readonly ActivityOptions _o;
    private readonly TimeProvider _time;
    private readonly bool _fts;

    public ActivityLog(AgentDb db, RagIndex? rag = null, ActivityOptions? options = null, TimeProvider? time = null)
    {
        _db = db;
        _rag = rag;
        _o = options ?? new ActivityOptions();
        _time = time ?? TimeProvider.System;
        _db.Exec("""
            CREATE TABLE IF NOT EXISTS activity_log(
                id INTEGER PRIMARY KEY, at INTEGER NOT NULL, player TEXT NOT NULL, kind TEXT NOT NULL, category TEXT NOT NULL,
                block_id INTEGER, item_id INTEGER, count INTEGER NOT NULL DEFAULT 1, x INTEGER, y INTEGER, z INTEGER,
                place_kind TEXT, place_key TEXT, place_name TEXT, confidence REAL NOT NULL, detail TEXT, text TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT 'auto', rollup_id INTEGER);
            CREATE INDEX IF NOT EXISTS ix_activity_player_at ON activity_log(player, at);
            CREATE INDEX IF NOT EXISTS ix_activity_at ON activity_log(at);
            CREATE TABLE IF NOT EXISTS activity_rollups(
                id INTEGER PRIMARY KEY, player TEXT NOT NULL, start INTEGER NOT NULL, end INTEGER NOT NULL,
                place_kind TEXT, place_key TEXT, place_name TEXT, min_x INTEGER, min_y INTEGER, min_z INTEGER, max_x INTEGER, max_y INTEGER, max_z INTEGER,
                counts TEXT, text TEXT NOT NULL, short TEXT, confidence REAL NOT NULL, salience REAL NOT NULL DEFAULT 0, events INTEGER NOT NULL, doc_ref TEXT);
            CREATE INDEX IF NOT EXISTS ix_rollups_player_end ON activity_rollups(player, end);
            CREATE INDEX IF NOT EXISTS ix_rollups_end ON activity_rollups(end);
            """);
        if (db.HasFts)
        {
            try
            {
                _db.Exec("CREATE VIRTUAL TABLE IF NOT EXISTS activity_fts USING fts5(text, player, tokenize='unicode61')");
                _fts = true;
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { _fts = false; }
        }
    }

    public bool HasFts => _fts;

    // ---------------- writes ----------------

    /// <summary>Stores a batch of raw events in one transaction; returns them with ids.</summary>
    public List<ActivityEvent> Append(IReadOnlyList<ActivityEvent> events)
    {
        if (events.Count == 0) return [];
        return _db.Tx(() =>
        {
            var list = new List<ActivityEvent>(events.Count);
            foreach (var e in events)
            {
                var text = e.Text;
                var id = (long)_db.Scalar("""
                    INSERT INTO activity_log(at, player, kind, category, block_id, item_id, count, x, y, z, place_kind, place_key, place_name, confidence, detail, text)
                    VALUES ($at, $p, $k, $c, $b, $i, $n, $x, $y, $z, $pk, $pkey, $pn, $conf, $d, $t) RETURNING id
                    """, ("$at", AgentDb.ToUnixMs(e.At)), ("$p", e.Player), ("$k", e.Kind), ("$c", e.Category), ("$b", e.BlockId), ("$i", e.ItemId),
                    ("$n", e.Count), ("$x", e.Pos?.X), ("$y", e.Pos?.Y), ("$z", e.Pos?.Z), ("$pk", e.Place?.Kind), ("$pkey", e.Place?.Key),
                    ("$pn", e.Place?.Name), ("$conf", e.Confidence), ("$d", e.Detail), ("$t", text))!;
                if (_fts) _db.Exec("INSERT INTO activity_fts(rowid, text, player) VALUES ($id, $t, $p)", ("$id", id), ("$t", text), ("$p", e.Player));
                list.Add(e with { Id = id });
            }
            return list;
        });
    }

    /// <summary>Stores a rollup, links its raw events and indexes it as an embedded episode doc.</summary>
    public ActivityRollup AddRollup(ActivityRollup r, IEnumerable<long> eventIds)
    {
        return _db.Tx(() =>
        {
            var docRef = $"{DocPrefix}{r.Player.ToLowerInvariant()}:{AgentDb.ToUnixMs(r.Start)}";
            var id = (long)_db.Scalar("""
                INSERT INTO activity_rollups(player, start, end, place_kind, place_key, place_name, min_x, min_y, min_z, max_x, max_y, max_z,
                    counts, text, short, confidence, salience, events, doc_ref)
                VALUES ($p, $s, $e, $pk, $pkey, $pn, $x0, $y0, $z0, $x1, $y1, $z1, $c, $t, $sh, $conf, $sal, $n, $ref) RETURNING id
                """, ("$p", r.Player), ("$s", AgentDb.ToUnixMs(r.Start)), ("$e", AgentDb.ToUnixMs(r.End)), ("$pk", r.Place?.Kind), ("$pkey", r.Place?.Key),
                ("$pn", r.Place?.Name), ("$x0", r.Min.X), ("$y0", r.Min.Y), ("$z0", r.Min.Z), ("$x1", r.Max.X), ("$y1", r.Max.Y), ("$z1", r.Max.Z),
                ("$c", JsonSerializer.Serialize(r.Counts)), ("$t", r.Text), ("$sh", r.Short), ("$conf", r.Confidence), ("$sal", r.Salience),
                ("$n", r.Events), ("$ref", docRef))!;
            foreach (var e in eventIds) _db.Exec("UPDATE activity_log SET rollup_id = $r WHERE id = $id", ("$r", id), ("$id", e));
            _rag?.Upsert(new RagDoc(docRef, "episode", $"{r.Player} activity", r.Text)
            {
                Source = "activity",
                Bounds = Box.Of(r.Min.X, r.Min.Y, r.Min.Z, r.Max.X, r.Max.Y, r.Max.Z),
                At = r.End,
            });
            return r with { Id = id };
        });
    }

    /// <summary>Deletes rolled-up raw events older than <see cref="ActivityOptions.RetainDays"/> (and anything older than twice that).</summary>
    public int Prune(DateTimeOffset? now = null)
    {
        var t = now ?? _time.GetUtcNow();
        long cut = AgentDb.ToUnixMs(t - TimeSpan.FromDays(Math.Max(0, _o.RetainDays)));
        long hard = AgentDb.ToUnixMs(t - TimeSpan.FromDays(Math.Max(0, _o.RetainDays) * 2.0));
        return _db.Tx(() =>
        {
            const string where = "(at < $cut AND rollup_id IS NOT NULL) OR at < $hard";
            if (_fts) _db.Exec($"DELETE FROM activity_fts WHERE rowid IN (SELECT id FROM activity_log WHERE {where})", ("$cut", cut), ("$hard", hard));
            return _db.Exec($"DELETE FROM activity_log WHERE {where}", ("$cut", cut), ("$hard", hard));
        });
    }

    // ---------------- reads ----------------

    public int Count(string? player = null) =>
        (int)_db.ScalarLong("SELECT count(*) FROM activity_log WHERE $p IS NULL OR player = $p COLLATE NOCASE", ("$p", player));

    public List<(long Id, string Player, string Kind, DateTimeOffset At, double Confidence, string Text, long? Rollup)> Events(
        string? player = null, DateTimeOffset? since = null, int limit = 50) =>
        _db.Query("""
            SELECT id, player, kind, at, confidence, text, rollup_id FROM activity_log
            WHERE ($p IS NULL OR player = $p COLLATE NOCASE) AND at >= $s ORDER BY at DESC LIMIT $n
            """, r => (r.GetInt64(0), r.GetString(1), r.GetString(2), AgentDb.FromUnixMs(r.GetInt64(3)), r.GetDouble(4), r.GetString(5), AgentDb.LongOrNull(r, 6)),
            ("$p", player), ("$s", since is { } s ? AgentDb.ToUnixMs(s) : 0L), ("$n", limit));

    public List<ActivityRollup> Rollups(string? player = null, DateTimeOffset? since = null, DateTimeOffset? until = null, int limit = 20) =>
        _db.Query("""
            SELECT id, player, start, end, text, short, confidence, salience, events, place_kind, place_key, place_name,
                   min_x, min_y, min_z, max_x, max_y, max_z FROM activity_rollups
            WHERE ($p IS NULL OR player = $p COLLATE NOCASE) AND end >= $s AND start <= $u ORDER BY end DESC LIMIT $n
            """, r => new ActivityRollup(r.GetString(1), AgentDb.FromUnixMs(r.GetInt64(2)), AgentDb.FromUnixMs(r.GetInt64(3)), r.GetString(4), r.GetDouble(6))
            {
                Id = r.GetInt64(0), Short = AgentDb.StrOrNull(r, 5) ?? "", Salience = r.GetDouble(7), Events = (int)r.GetInt64(8),
                Place = r.IsDBNull(9) ? null : new PlaceRef(r.GetString(9), r.GetString(10), r.GetString(11), default, default),
                Min = new BlockPos(r.GetInt32(12), r.GetInt32(13), r.GetInt32(14)), Max = new BlockPos(r.GetInt32(15), r.GetInt32(16), r.GetInt32(17)),
            },
            ("$p", player), ("$s", since is { } s ? AgentDb.ToUnixMs(s) : 0L), ("$u", until is { } u ? AgentDb.ToUnixMs(u) : long.MaxValue), ("$n", limit));

    public IReadOnlyList<string> Players() =>
        _db.Query("SELECT DISTINCT player FROM activity_rollups UNION SELECT DISTINCT player FROM activity_log", r => r.GetString(0));

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "did", "do", "does", "doing", "the", "a", "an", "today", "yesterday", "hour", "last", "this", "morning", "tonight", "has",
        "have", "been", "up", "to", "is", "was", "were", "who", "where", "when", "recently", "lately", "since", "in", "on", "at", "of",
    };

    /// <summary>Raw-event full-text search (FTS5; LIKE fallback), newest first.</summary>
    public List<(long Id, string Player, DateTimeOffset At, double Confidence, string Text)> Search(string query, DateTimeOffset? since = null, int limit = 20)
    {
        var terms = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}_]+").Select(m => m.Value).Where(t => !Stop.Contains(t)).Distinct().ToList();
        if (terms.Count == 0) return [];
        long s = since is { } x ? AgentDb.ToUnixMs(x) : 0;
        if (_fts)
        {
            var match = string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "") + "\"*"));
            return _db.Query("""
                SELECT l.id, l.player, l.at, l.confidence, l.text FROM activity_fts f JOIN activity_log l ON l.id = f.rowid
                WHERE activity_fts MATCH $m AND l.at >= $s ORDER BY l.at DESC LIMIT $n
                """, r => (r.GetInt64(0), r.GetString(1), AgentDb.FromUnixMs(r.GetInt64(2)), r.GetDouble(3), r.GetString(4)),
                ("$m", match), ("$s", s), ("$n", limit));
        }
        var like = string.Join(" OR ", terms.Select((_, i) => $"text LIKE $t{i}"));
        var args = terms.Select((t, i) => ($"$t{i}", (object?)$"%{t}%")).Append(("$s", s)).Append(("$n", limit)).ToArray();
        return _db.Query($"SELECT id, player, at, confidence, text FROM activity_log WHERE ({like}) AND at >= $s ORDER BY at DESC LIMIT $n",
            r => (r.GetInt64(0), r.GetString(1), AgentDb.FromUnixMs(r.GetInt64(2)), r.GetDouble(3), r.GetString(4)), args);
    }

    /// <summary>
    /// Answers "what did robin do today" deterministically: resolves player names and a time window (today, yesterday,
    /// last hour, this morning...) and lists rollups, then raw events not yet rolled up. Bounded output.
    /// </summary>
    public string Ask(string question, DateTimeOffset? now = null, int maxChars = 500)
    {
        var t = now ?? _time.GetUtcNow();
        var q = question.ToLowerInvariant();
        var local = t.ToLocalTime();
        DateTimeOffset since = t - TimeSpan.FromDays(1), until = t;
        if (q.Contains("yesterday")) { since = new DateTimeOffset(local.Date.AddDays(-1), local.Offset); until = new DateTimeOffset(local.Date, local.Offset); }
        else if (q.Contains("today") || q.Contains("tonight")) since = new DateTimeOffset(local.Date, local.Offset);
        else if (q.Contains("this morning")) since = new DateTimeOffset(local.Date, local.Offset);
        else if (q.Contains("last hour") || q.Contains("past hour")) since = t - TimeSpan.FromHours(1);
        else if (q.Contains("this week") || q.Contains("lately")) since = t - TimeSpan.FromDays(7);

        var players = Players();
        var who = players.Where(p => Regex.IsMatch(q, $@"\b{Regex.Escape(p.ToLowerInvariant())}\b")).ToList();
        var sb = new StringBuilder();
        void Line(string s)
        {
            if (sb.Length + s.Length + 2 > maxChars) return;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(s);
        }
        var targets = who.Count > 0 ? who.Cast<string?>().ToList() : [null];
        foreach (var p in targets)
        {
            foreach (var r in Rollups(p, since, until, 12).OrderBy(r => r.Start)) Line(r.Text);
            var raw = Events(p, since, 30).Where(e => e.Rollup == null && e.At <= until && e.Confidence >= _o.MediumConfidence).Reverse().ToList();
            for (int i = 0; i < raw.Count;)
            {
                int j = i;
                while (j + 1 < raw.Count && raw[j + 1].Text == raw[i].Text) j++; // collapse repeats: "... x5"
                var e = raw[i];
                Line((e.Confidence < _o.HighConfidence ? "probably " : "") + e.Text + (j > i ? $" x{j - i + 1}" : "") + " " +
                     e.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
                i = j + 1;
            }
        }
        if (sb.Length == 0 && who.Count == 0)
            foreach (var e in Search(question, since, 8)) Line(e.Text);
        return sb.Length == 0 ? (who.Count > 0 ? $"nothing recorded for {string.Join(", ", who)} in that window" : "no matching activity") : sb.ToString();
    }
}
