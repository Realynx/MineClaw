using System.Globalization;
using System.Text.RegularExpressions;
using MineClaw.Agent.Rag;
using MineClaw.World;
using MineClaw.World.Designs;

namespace MineClaw.Bot.Blueprints;

/// <summary>Who made a revision: a player (a capture of their build, their edit committed, their revert), the bot (the brain's
/// designer, its own commit), or a body (a hive body's work).</summary>
public static class RevisionAuthor
{
    public const string Player = "player", Bot = "bot", Body = "body";
}

/// <summary>
/// One revision of a design (git-style, linear: r1, r2…; the newest is HEAD, older ones stay): who, why, when, the DSL it
/// builds from and its block set (<see cref="DesignSnapshot"/>, loaded on demand). <see cref="Tag"/> = an optional name ("good").
/// </summary>
public sealed record DesignRevision(long Design, int Rev, string Author, string AuthorKind, string Message, string? Tag, DateTimeOffset At,
    int Dim, string Dsl, BlockPos? Origin, string Hash, int Blocks)
{
    public string Label => $"r{Rev}";

    /// <summary>"r2 Fox 10-30 14:10 'step added' [good]".</summary>
    public string Line(DateTimeOffset now) =>
        $"r{Rev} {Author} {Ago(now)} '{Message}'" + (Tag is { Length: > 0 } t ? $" [{t}]" : "");

    private string Ago(DateTimeOffset now)
    {
        var d = now - At;
        return d < TimeSpan.FromMinutes(1) ? "just now" : d < TimeSpan.FromHours(1) ? $"{(int)d.TotalMinutes}m ago"
            : d < TimeSpan.FromDays(1) ? $"{(int)d.TotalHours}h ago" : At.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}

/// <summary>The drift watcher's last look at a design in the world (for the Memory Explorer's revision-vs-world diff and restarts).</summary>
public sealed record DesignWorldView(long Design, int Rev, DateTimeOffset Seen, string SeenBy, DesignSnapshot World, string Diff, string Causes);

/// <summary>
/// Revisions of saved designs in the shared db (<c>blueprint_revisions</c>: every hive process sees one history), full snapshots
/// (a design is hundreds to a few thousand blocks: a few KB per revision as x-runs). Migration: a design saved before revisions
/// gets r1 from its current DSL the first time anything looks at its history (<see cref="DesignHistory.Ensure"/>), and every
/// existing one at startup. <c>blueprint_world</c> keeps the drift watcher's last world view per design.
/// </summary>
public sealed partial class DesignRevisions
{
    private readonly AgentDb _db;
    private readonly TimeProvider _time;

    public DesignRevisions(AgentDb db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
        lock (_db.Gate)
            _db.Exec("""
                CREATE TABLE IF NOT EXISTS blueprint_revisions(blueprint_id INTEGER NOT NULL, rev INTEGER NOT NULL, author TEXT NOT NULL,
                    author_kind TEXT NOT NULL, message TEXT NOT NULL, tag TEXT, created INTEGER NOT NULL, dim INTEGER NOT NULL, dsl TEXT NOT NULL,
                    ox INTEGER, oy INTEGER, oz INTEGER, blocks TEXT NOT NULL, air TEXT NOT NULL, n INTEGER NOT NULL, hash TEXT NOT NULL,
                    PRIMARY KEY(blueprint_id, rev));
                CREATE TABLE IF NOT EXISTS blueprint_world(blueprint_id INTEGER PRIMARY KEY, rev INTEGER NOT NULL, seen INTEGER NOT NULL,
                    seen_by TEXT NOT NULL, blocks TEXT NOT NULL, air TEXT NOT NULL, diff TEXT NOT NULL, causes TEXT NOT NULL);
                """);
    }

    public AgentDb Db => _db;

    private const string Cols = "blueprint_id,rev,author,author_kind,message,tag,created,dim,dsl,ox,oy,oz,hash,n";

    private static DesignRevision Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), AgentDb.StrOrNull(r, 5), AgentDb.FromUnixMs(r.GetInt64(6)),
        r.GetInt32(7), r.GetString(8), r.IsDBNull(9) ? null : new BlockPos(r.GetInt32(9), r.GetInt32(10), r.GetInt32(11)), r.GetString(12), r.GetInt32(13));

    /// <summary>Every revision of a design, newest first.</summary>
    public List<DesignRevision> List(long design, int max = 100)
    {
        lock (_db.Gate) return _db.Query($"SELECT {Cols} FROM blueprint_revisions WHERE blueprint_id=$id ORDER BY rev DESC LIMIT $n", Map, ("$id", design), ("$n", max));
    }

    public DesignRevision? Head(long design)
    {
        lock (_db.Gate) return _db.Query($"SELECT {Cols} FROM blueprint_revisions WHERE blueprint_id=$id ORDER BY rev DESC LIMIT 1", Map, ("$id", design)).FirstOrDefault();
    }

    public DesignRevision? Get(long design, int rev)
    {
        lock (_db.Gate) return _db.Query($"SELECT {Cols} FROM blueprint_revisions WHERE blueprint_id=$id AND rev=$r", Map, ("$id", design), ("$r", rev)).FirstOrDefault();
    }

    /// <summary>The block set of a revision (decoded on demand).</summary>
    public DesignSnapshot? Snapshot(long design, int rev)
    {
        lock (_db.Gate)
            return _db.Query("SELECT blocks, air FROM blueprint_revisions WHERE blueprint_id=$id AND rev=$r",
                r => DesignSnapshot.Decode(r.GetString(0), r.GetString(1)), ("$id", design), ("$r", rev)).FirstOrDefault();
    }

    /// <summary>Designs with a history (the drift watcher's candidates).</summary>
    public List<long> Designs()
    {
        lock (_db.Gate) return _db.Query("SELECT DISTINCT blueprint_id FROM blueprint_revisions", r => r.GetInt64(0));
    }

    /// <summary>
    /// A new revision on top of HEAD (r = HEAD + 1), or HEAD itself when nothing changed (same DSL, origin and block set: a save
    /// that changes nothing is no revision).
    /// </summary>
    public DesignRevision Add(long design, DesignSnapshot snap, string dsl, BlockPos? origin, int dim, string author, string kind, string message,
        string? tag = null, DateTimeOffset? at = null)
    {
        var hash = snap.Hash();
        lock (_db.Gate)
        {
            var head = Head(design);
            if (head != null && head.Hash == hash && head.Dsl == dsl && head.Origin == origin) return head;
            int rev = (head?.Rev ?? 0) + 1;
            var when = at ?? _time.GetUtcNow();
            _db.Exec("""
                INSERT INTO blueprint_revisions(blueprint_id,rev,author,author_kind,message,tag,created,dim,dsl,ox,oy,oz,blocks,air,n,hash)
                VALUES($id,$r,$a,$k,$m,$t,$c,$d,$dsl,$ox,$oy,$oz,$b,$air,$n,$h)
                """, ("$id", design), ("$r", rev), ("$a", author), ("$k", kind), ("$m", Trim(message, 120)), ("$t", tag), ("$c", AgentDb.ToUnixMs(when)),
                ("$d", dim), ("$dsl", dsl), ("$ox", origin?.X), ("$oy", origin?.Y), ("$oz", origin?.Z),
                ("$b", DesignSnapshot.EncodeStates(snap.Blocks)), ("$air", DesignSnapshot.EncodeCells(snap.Air)), ("$n", snap.Count), ("$h", hash));
            return new DesignRevision(design, rev, author, kind, Trim(message, 120), tag, when, dim, dsl, origin, hash, snap.Count);
        }
    }

    /// <summary>Names a revision ("good"); a tag names one revision per design (moved off any older one).</summary>
    public bool TagRev(long design, int rev, string tag)
    {
        lock (_db.Gate)
        {
            _db.Exec("UPDATE blueprint_revisions SET tag=NULL WHERE blueprint_id=$id AND tag=$t", ("$id", design), ("$t", tag));
            return _db.Exec("UPDATE blueprint_revisions SET tag=$t WHERE blueprint_id=$id AND rev=$r", ("$t", tag), ("$id", design), ("$r", rev)) > 0;
        }
    }

    [GeneratedRegex(@"^(?:r|rev|revision\s*)?(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RevRx();

    [GeneratedRegex(@"^(?:head|latest|current)(?:\s*(?:~|-|\^)\s*(\d*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadRx();

    /// <summary>
    /// "r2" / "2" / "rev 2" / "HEAD" / "HEAD~1" / "head^" / "previous" / "last" / a tag ("good") → that revision; null = no such
    /// revision. Empty = HEAD.
    /// </summary>
    public DesignRevision? Resolve(long design, string? spec)
    {
        var s = (spec ?? "").Trim().Trim('"', '\'').TrimStart('#');
        var head = Head(design);
        if (head == null) return null;
        if (s.Length == 0) return head;
        if (RevRx().Match(s) is { Success: true } m) return Get(design, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        if (HeadRx().Match(s) is { Success: true } h)
        {
            int back = h.Groups[1].Success ? h.Groups[1].Value.Length == 0 ? 1 : int.Parse(h.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            return Get(design, head.Rev - back);
        }
        if (s.ToLowerInvariant() is "previous" or "prev" or "last" or "before" or "the one before" or "undo") return Get(design, head.Rev - 1);
        if (s.ToLowerInvariant() is "first" or "original") return Get(design, 1);
        lock (_db.Gate)
            return _db.Query($"SELECT {Cols} FROM blueprint_revisions WHERE blueprint_id=$id AND tag=$t COLLATE NOCASE ORDER BY rev DESC LIMIT 1", Map,
                ("$id", design), ("$t", s)).FirstOrDefault();
    }

    // ------------------------------------------------------------------ the drift watcher's last world view

    public void SaveWorld(long design, int rev, DesignSnapshot world, string seenBy, string diff, string causes)
    {
        lock (_db.Gate)
            _db.Exec("""
                INSERT INTO blueprint_world(blueprint_id,rev,seen,seen_by,blocks,air,diff,causes) VALUES($id,$r,$s,$by,$b,$a,$d,$c)
                ON CONFLICT(blueprint_id) DO UPDATE SET rev=$r, seen=$s, seen_by=$by, blocks=$b, air=$a, diff=$d, causes=$c
                """, ("$id", design), ("$r", rev), ("$s", AgentDb.ToUnixMs(_time.GetUtcNow())), ("$by", seenBy),
                ("$b", DesignSnapshot.EncodeStates(world.Blocks)), ("$a", DesignSnapshot.EncodeCells(world.Air)), ("$d", diff), ("$c", causes));
    }

    public DesignWorldView? World(long design)
    {
        lock (_db.Gate)
            return _db.Query("SELECT blueprint_id,rev,seen,seen_by,blocks,air,diff,causes FROM blueprint_world WHERE blueprint_id=$id",
                r => new DesignWorldView(r.GetInt64(0), r.GetInt32(1), AgentDb.FromUnixMs(r.GetInt64(2)), r.GetString(3),
                    DesignSnapshot.Decode(r.GetString(4), r.GetString(5)), r.GetString(6), r.GetString(7)), ("$id", design)).FirstOrDefault();
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
