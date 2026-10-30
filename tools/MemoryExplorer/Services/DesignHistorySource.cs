using MineClaw.Agent.Rag;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;
using MineClaw.World.Designs;

namespace MineClaw.MemoryExplorer.Services;

/// <summary>A saved design in the list: its HEAD and how many revisions it has.</summary>
public sealed record DesignEntry(long Id, string Name, string Status, string? Requester, int Revisions, int Head, DateTimeOffset Updated, bool HasPicture);

/// <summary>One revision row (no block set).</summary>
public sealed record DesignRevisionInfo(int Rev, string Author, string AuthorKind, string Message, string? Tag, DateTimeOffset At, int Blocks);

/// <summary>The bot's last look at a design in the world (the drift watcher's <c>blueprint_world</c> row).</summary>
public sealed record DesignWorldInfo(int Rev, DateTimeOffset Seen, string SeenBy, string Diff, string Causes, DesignSnapshot World);

/// <summary>
/// Design revisions of an opened (read-only) db: <c>blueprints</c> + <c>blueprint_revisions</c> + <c>blueprint_world</c> (owned by
/// the bot's DesignRevisions store; plain SELECTs, every table may be missing in an older db). Diffs use the World layer's
/// <see cref="DesignDiff"/>, so the Explorer counts exactly what the bot's <c>design diff</c> counts. Block names follow the
/// bot's version (<c>server-version.txt</c> next to the db, else the data dir's): a 1.12.2 db names its 1.12.2 ids.
/// </summary>
public sealed class DesignHistorySource(AgentDb db, string? dbPath = null)
{
    public bool Available => db.TableExists("blueprints");

    /// <summary>Every design with a history (and those saved before revisions: 0 revisions until the bot looks), newest first.</summary>
    public List<DesignEntry> Designs()
    {
        if (!Available) return [];
        bool revs = db.TableExists("blueprint_revisions");
        var sql = revs
            ? """
              SELECT b.id, b.name, b.status, b.requester, b.updated, b.png IS NOT NULL,
                     (SELECT count(*) FROM blueprint_revisions r WHERE r.blueprint_id = b.id), (SELECT max(rev) FROM blueprint_revisions r WHERE r.blueprint_id = b.id)
              FROM blueprints b WHERE b.status <> 'deleted' ORDER BY b.id DESC
              """
            : "SELECT id, name, status, requester, updated, png IS NOT NULL, 0, 0 FROM blueprints WHERE status <> 'deleted' ORDER BY id DESC";
        return db.Query(sql, r => new DesignEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), AgentDb.StrOrNull(r, 3), (int)r.GetInt64(6),
            r.IsDBNull(7) ? 0 : (int)r.GetInt64(7), AgentDb.FromUnixMs(r.GetInt64(4)), r.GetInt64(5) != 0));
    }

    public List<DesignRevisionInfo> Revisions(long id) => !db.TableExists("blueprint_revisions") ? []
        : db.Query("SELECT rev, author, author_kind, message, tag, created, n FROM blueprint_revisions WHERE blueprint_id = $id ORDER BY rev DESC",
            r => new DesignRevisionInfo(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), AgentDb.StrOrNull(r, 4), AgentDb.FromUnixMs(r.GetInt64(5)), r.GetInt32(6)),
            ("$id", id));

    public DesignSnapshot? Snapshot(long id, int rev) => !db.TableExists("blueprint_revisions") ? null
        : db.Query("SELECT blocks, air FROM blueprint_revisions WHERE blueprint_id = $id AND rev = $r",
            r => DesignSnapshot.Decode(r.GetString(0), r.GetString(1)), ("$id", id), ("$r", rev)).FirstOrDefault();

    public DesignWorldInfo? World(long id) => !db.TableExists("blueprint_world") ? null
        : db.Query("SELECT rev, seen, seen_by, diff, causes, blocks, air FROM blueprint_world WHERE blueprint_id = $id",
            r => new DesignWorldInfo(r.GetInt32(0), AgentDb.FromUnixMs(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.GetString(4),
                DesignSnapshot.Decode(r.GetString(5), r.GetString(6))), ("$id", id)).FirstOrDefault();

    /// <summary>The design's preview sheet (the bot's rendered thumbnail), or null.</summary>
    public byte[]? Picture(long id) => db.Scalar("SELECT png FROM blueprints WHERE id = $id", ("$id", id)) as byte[];

    /// <summary>rA → rB ("+12 stone, −3 planks, ~2 oak stairs facing", and the cells).</summary>
    public (string Summary, IReadOnlyList<string> Cells)? Between(long id, int a, int b)
    {
        if (Snapshot(id, Math.Min(a, b)) is not { } x || Snapshot(id, Math.Max(a, b)) is not { } y) return null;
        using var _ = Names();
        return Lines(DesignDiff.Between(x, y));
    }

    /// <summary>A revision against the world as the bot last saw it (the cells it looked at; the rest is unknown).</summary>
    public (string Summary, IReadOnlyList<string> Cells)? AgainstWorld(long id, int rev)
    {
        if (Snapshot(id, rev) is not { } s || World(id) is not { } w) return null;
        using var _ = Names();
        return Lines(DesignDiff.Against(s, new SeenWorld(w.World)));
    }

    private static (string, IReadOnlyList<string>) Lines(DesignDiff d)
    {
        static string N(int id) => Blocks.Get(id).Name.Replace('_', ' ');
        var cells = d.Added.Select(c => $"+ {c.Pos.X},{c.Pos.Y},{c.Pos.Z}  {N(c.ToId)}{(c.ToMeta != 0 ? ":" + c.ToMeta : "")}")
            .Concat(d.Removed.Select(c => $"− {c.Pos.X},{c.Pos.Y},{c.Pos.Z}  {N(c.FromId)}{(c.FromMeta != 0 ? ":" + c.FromMeta : "")}"))
            .Concat(d.Changed.Select(c => $"~ {c.Pos.X},{c.Pos.Y},{c.Pos.Z}  {N(c.FromId)}:{c.FromMeta} → {N(c.ToId)}:{c.ToMeta}")).ToList();
        var counts = $"{d.Added.Count} added, {d.Removed.Count} removed, {d.Changed.Count} changed" + (d.Unknown > 0 ? $", {d.Unknown} not seen" : "");
        return ($"{d.Text(6)}  ({counts})", cells);
    }

    /// <summary>Block names in the bot's version for the duration of a diff.</summary>
    private IDisposable? Names()
    {
        try
        {
            var dirs = new[] { dbPath is { Length: > 0 } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : null, ExplorerPaths.DataDir };
            foreach (var dir in dirs.OfType<string>())
            {
                var f = Path.Combine(dir, "server-version.txt");
                if (File.Exists(f)) return File.ReadAllText(f).Contains("1.12.2", StringComparison.Ordinal) ? GameData.Override(Release1122GameData.Instance) : null;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>The cells the bot saw, as a world: a column counts as seen when any of its cells was looked at.</summary>
    private sealed class SeenWorld(DesignSnapshot seen) : IBlockAccess
    {
        private readonly HashSet<(int, int)> _cols = seen.Blocks.Keys.Concat(seen.Air).Select(p => (p.X, p.Z)).ToHashSet();
        public int GetBlock(int x, int y, int z) => seen.Blocks.TryGetValue(new BlockPos(x, y, z), out var s) ? s.Id : 0;
        public int GetMeta(int x, int y, int z) => seen.Blocks.TryGetValue(new BlockPos(x, y, z), out var s) ? s.Meta : 0;
        public bool IsLoaded(int x, int z) => _cols.Contains((x, z));
        public BlockInfo Info(int id) => Blocks.Get(id);
        public int WorldHeight => 256;
    }
}
