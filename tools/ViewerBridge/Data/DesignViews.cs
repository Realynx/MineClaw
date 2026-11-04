using System.Text;
using MineClaw.Agent.Rag;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;
using MineClaw.World.Designs;

namespace MineClaw.ViewerBridge.Data;

/// <param name="Version">The game version the design is drawn for (its DSL's <c>version</c> line; none = the server's).</param>
/// <param name="Kind">"build" or "mine" (its DSL's <c>kind mine</c> line: a mine design at absolute y, docs/web/api.md "Mine designs").</param>
public sealed record DesignSummary(long Id, string Name, string Status, string? Requester, int Rev, int Revisions, long? Updated, bool HasPicture,
    string? Version = null, string? Kind = null);
public sealed record DesignListResponse(long Total, long RevisionsTotal, int Offset, int Limit, IReadOnlyList<DesignSummary> Items);
public sealed record DesignRevision(int Rev, string? Author, string? AuthorKind, string? Message, string? Tag, long Created, int N, string? Version = null,
    string? Kind = null);
public sealed record DesignWorldNote(int Rev, long Seen, string? SeenBy, string? Diff, string? Causes, string Text);
public sealed record DesignDiffPair(string A, string B);
public sealed record DesignDetail(DesignSummary Design, IReadOnlyList<DesignRevision> Revisions, DesignWorldNote? World, DesignDiffPair DefaultDiff);
public sealed record DesignDiffCell(string Op, int X, int Y, int Z, string Name, string? From = null, string? To = null);
public sealed record DesignDiffDto(long Id, string A, string B, int Added, int Removed, int Changed, int Unseen, string Summary, IReadOnlyList<DesignDiffCell> Cells,
    int Total, bool Truncated, string Text);

/// <summary>
/// Designs (explorer-spec §10) from the read-only db: <c>blueprints</c> + <c>blueprint_revisions</c> + <c>blueprint_world</c>, diffed with
/// the World layer's <see cref="DesignDiff"/> (so the counts match the bot's own <c>design diff</c>). Block names follow the server version.
/// Ported from tools/MemoryExplorer/Services/DesignHistorySource.cs.
/// </summary>
public sealed class DesignViews(AgentDb db, string serverVersion)
{
    public bool Available => db.TableExists("blueprints");
    private bool Revs => db.TableExists("blueprint_revisions");

    private string SummarySql => Revs
        ? """
          SELECT b.id, b.name, b.status, b.requester, b.updated, b.png IS NOT NULL,
                 (SELECT count(*) FROM blueprint_revisions r WHERE r.blueprint_id = b.id), (SELECT max(rev) FROM blueprint_revisions r WHERE r.blueprint_id = b.id), b.dsl
          FROM blueprints b WHERE b.status <> 'deleted'
          """
        : "SELECT b.id, b.name, b.status, b.requester, b.updated, b.png IS NOT NULL, 0, 0, b.dsl FROM blueprints b WHERE b.status <> 'deleted'";

    private DesignSummary Map(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), AgentDb.StrOrNull(r, 3), r.IsDBNull(7) ? 0 : (int)r.GetInt64(7), (int)r.GetInt64(6), AgentDb.LongOrNull(r, 4), r.GetInt64(5) != 0,
            VersionOf(AgentDb.StrOrNull(r, 8)), KindOf(AgentDb.StrOrNull(r, 8)));

    private static readonly System.Text.RegularExpressions.Regex MineLine =
        new(@"^\s*kind\s+mine\b", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"mine" for a mine design's DSL, else "build".</summary>
    private static string KindOf(string? dsl) => dsl != null && MineLine.IsMatch(dsl) ? "mine" : "build";

    private static readonly System.Text.RegularExpressions.Regex VersionLine =
        new(@"^\s*version\s+(b1\.8\.1|1\.12\.2)\b", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The design's version: its DSL's <c>version</c> line, else the server's (the bot's migration rule: a design saved before
    /// versions were recorded is the version of the server whose db holds it).</summary>
    private string VersionOf(string? dsl) => dsl != null && VersionLine.Match(dsl) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : serverVersion;

    public DesignListResponse List(string? q, int offset, int limit)
    {
        if (!Available) return new DesignListResponse(0, 0, offset, limit, []);
        var filter = "";
        var args = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            filter = " AND instr(lower(CAST(b.id AS TEXT) || ' ' || b.name || ' ' || b.status || ' ' || coalesce(b.requester, '')), $q) > 0";
            args.Add(("$q", q.Trim().ToLowerInvariant()));
        }
        long total = db.ScalarLong("SELECT count(*) FROM blueprints b WHERE b.status <> 'deleted'" + filter, args.ToArray());
        long revTotal = Revs ? db.ScalarLong("SELECT count(*) FROM blueprint_revisions r JOIN blueprints b ON b.id = r.blueprint_id WHERE b.status <> 'deleted'" + filter, args.ToArray()) : 0;
        args.Add(("$limit", limit));
        args.Add(("$offset", offset));
        var items = db.Query(SummarySql + filter + " ORDER BY b.id DESC LIMIT $limit OFFSET $offset", Map, args.ToArray());
        return new DesignListResponse(total, revTotal, offset, limit, items);
    }

    public DesignSummary? Summary(long id) => !Available ? null : db.Query(SummarySql + " AND b.id = $id", Map, ("$id", id)).FirstOrDefault();

    public List<DesignRevision> Revisions(long id) => !Revs ? [] :
        db.Query("SELECT rev, author, author_kind, message, tag, created, n, dsl FROM blueprint_revisions WHERE blueprint_id = $id ORDER BY rev DESC",
            r => new DesignRevision(r.GetInt32(0), AgentDb.StrOrNull(r, 1), AgentDb.StrOrNull(r, 2), AgentDb.StrOrNull(r, 3), AgentDb.StrOrNull(r, 4), r.GetInt64(5), r.GetInt32(6),
                VersionOf(AgentDb.StrOrNull(r, 7)), KindOf(AgentDb.StrOrNull(r, 7))),
            ("$id", id));

    private (DesignWorldNote Note, DesignSnapshot World)? WorldOf(long id) => !db.TableExists("blueprint_world") ? null :
        db.Query("SELECT rev, seen, seen_by, diff, causes, blocks, air FROM blueprint_world WHERE blueprint_id = $id", r =>
        {
            int rev = r.GetInt32(0);
            string by = AgentDb.StrOrNull(r, 2) ?? "?", diff = AgentDb.StrOrNull(r, 3) ?? "", causes = AgentDb.StrOrNull(r, 4) ?? "";
            var text = $"last seen by {by} at r{rev}: {(diff.Length > 0 ? diff : "no drift")}" + (causes.Length > 0 ? $" ({causes})" : "");
            return ((DesignWorldNote, DesignSnapshot)?)(new DesignWorldNote(rev, r.GetInt64(1), by, diff, causes, text), DesignSnapshot.Decode(AgentDb.StrOrNull(r, 5), AgentDb.StrOrNull(r, 6)));
        }, ("$id", id)).FirstOrDefault();

    public DesignDetail? Detail(long id)
    {
        if (Summary(id) is not { } s) return null;
        var revs = Revisions(id);
        var world = WorldOf(id);
        var head = revs.FirstOrDefault()?.Rev ?? 0;
        var pair = world != null && head > 0 ? new DesignDiffPair(head.ToString(System.Globalization.CultureInfo.InvariantCulture), "world")
            : revs.Count >= 2 ? new DesignDiffPair(revs[1].Rev.ToString(System.Globalization.CultureInfo.InvariantCulture), head.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new DesignDiffPair(head.ToString(System.Globalization.CultureInfo.InvariantCulture), head.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new DesignDetail(s, revs, world?.Note, pair);
    }

    public byte[]? Picture(long id) => Available ? db.Scalar("SELECT png FROM blueprints WHERE id = $id AND status <> 'deleted'", ("$id", id)) as byte[] : null;

    private DesignSnapshot? Snapshot(long id, int rev) => !Revs ? null :
        db.Query("SELECT blocks, air FROM blueprint_revisions WHERE blueprint_id = $id AND rev = $r",
            r => DesignSnapshot.Decode(AgentDb.StrOrNull(r, 0), AgentDb.StrOrNull(r, 1)), ("$id", id), ("$r", rev)).FirstOrDefault();

    /// <summary>a/b = a revision number or "world"; null when a side is missing. Errors (bad pair) throw ArgumentException.</summary>
    public DesignDiffDto? Diff(long id, string? a, string? b, int max)
    {
        var detail = Detail(id);
        if (detail == null) return null;
        a ??= detail.DefaultDiff.A;
        b ??= detail.DefaultDiff.B;
        if (a == "world" && b == "world") throw new ArgumentException("compare a revision with the world, not the world with itself");
        using var _ = serverVersion == "1.12.2" ? GameData.Override(Release1122GameData.Instance) : null;
        DesignDiff d;
        if (a == "world" || b == "world")
        {
            var rev = int.Parse(a == "world" ? b : a, System.Globalization.CultureInfo.InvariantCulture);
            if (Snapshot(id, rev) is not { } s || WorldOf(id) is not { } w) return null;
            d = DesignDiff.Against(s, new SeenWorld(w.World));
            if (a == "world") d = d.Inverse();
        }
        else
        {
            int ra = int.Parse(a, System.Globalization.CultureInfo.InvariantCulture), rb = int.Parse(b, System.Globalization.CultureInfo.InvariantCulture);
            if (Snapshot(id, ra) is not { } x || Snapshot(id, rb) is not { } y) return null;
            d = DesignDiff.Between(x, y);
        }
        static string N(int bid) => Blocks.Get(bid).Name.Replace('_', ' ');
        static string NM(int bid, int meta) => N(bid) + (meta != 0 ? ":" + meta : "");
        var cells = d.Added.Select(c => new DesignDiffCell("+", c.Pos.X, c.Pos.Y, c.Pos.Z, NM(c.ToId, c.ToMeta)))
            .Concat(d.Removed.Select(c => new DesignDiffCell("-", c.Pos.X, c.Pos.Y, c.Pos.Z, NM(c.FromId, c.FromMeta))))
            .Concat(d.Changed.Select(c => new DesignDiffCell("~", c.Pos.X, c.Pos.Y, c.Pos.Z, NM(c.ToId, c.ToMeta), $"{N(c.FromId)}:{c.FromMeta}", $"{N(c.ToId)}:{c.ToMeta}")))
            .ToList();
        var counts = $"{d.Added.Count} added, {d.Removed.Count} removed, {d.Changed.Count} changed" + (d.Unknown > 0 ? $", {d.Unknown} not seen" : "");
        var summary = $"{d.Text(6)}  ({counts})";
        var shown = cells.Take(max).ToList();
        var text = new StringBuilder().AppendLine($"r{a} → {(b == "world" ? "world" : "r" + b)}: {summary}");
        foreach (var c in shown)
            text.AppendLine(c.Op == "~" ? $"~ {c.X},{c.Y},{c.Z}  {c.From} → {c.To}" : $"{(c.Op == "+" ? "+" : "−")} {c.X},{c.Y},{c.Z}  {c.Name}");
        return new DesignDiffDto(id, a, b, d.Added.Count, d.Removed.Count, d.Changed.Count, d.Unknown, summary, shown, cells.Count, cells.Count > shown.Count, text.ToString());
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
