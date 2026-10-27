using System.Globalization;

namespace MineClaw.Agent.Rag;

/// <summary>One side of a nether portal: its dimension, observation key (frame min corner) and the exact spot to stand in.</summary>
public sealed record PortalEnd(int Dim, string Key, int X, int Y, int Z)
{
    public string Pos => string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Z}");
    public double Distance(double x, double y, double z) => Math.Sqrt((X + 0.5 - x) * (X + 0.5 - x) + (Y - y) * (Y - y) + (Z + 0.5 - z) * (Z + 0.5 - z));
}

/// <summary>A linked pair (the bot went through <see cref="A"/> and came out at <see cref="B"/>, or back).</summary>
public sealed record PortalLink(long Id, PortalEnd A, PortalEnd B, DateTimeOffset Updated, int Uses)
{
    /// <summary>The end in <paramref name="dim"/> and the one it leads to, or null when the pair doesn't touch that dimension.</summary>
    public (PortalEnd From, PortalEnd To)? From(int dim) => A.Dim == dim ? (A, B) : B.Dim == dim ? (B, A) : null;
}

/// <summary>Cross-dimension route: walk to <see cref="Entry"/> (current dimension), step in, come out at <see cref="Exit"/>.</summary>
public sealed record PortalRoute(PortalEnd Entry, PortalEnd Exit, double Cost);

/// <summary>
/// Portal pairs recorded when the bot actually goes through a portal (entry and exit as one linked pair, table
/// <c>portal_links</c>), plus the travel planner for "goto a place in the other dimension": the pair that minimises
/// walking to its entry here + walking from its exit to the target there. No pair → no route (callers fail honestly).
/// Each end is also a <c>portal</c> observation (frame box + standing spot) written by the world indexer.
/// </summary>
public sealed class PortalLinks
{
    public const string Kind = "portal";

    private readonly ObservationStore _store;
    private readonly AgentDb _db;
    private readonly TimeProvider _time;

    internal PortalLinks(ObservationStore store, AgentDb db, TimeProvider time)
    {
        _store = store;
        _db = db;
        _time = time;
    }

    /// <summary>Records (or refreshes) a pair. Order-insensitive: stored overworld side first.</summary>
    public PortalLink Link(PortalEnd a, PortalEnd b)
    {
        if (a.Dim == b.Dim) throw new ArgumentException("a portal pair links two different dimensions");
        if (a.Dim != WorldDim.Overworld && b.Dim == WorldDim.Overworld) (a, b) = (b, a);
        long now = AgentDb.ToUnixMs(_time.GetUtcNow());
        return _db.Tx(() =>
        {
            // A re-entered portal that now leads elsewhere (rebuilt exit): the newest pairing of an end wins.
            _db.Exec("""
                DELETE FROM portal_links WHERE (a_dim = $ad AND a_key = $ak AND NOT (b_dim = $bd AND b_key = $bk))
                   OR (b_dim = $bd AND b_key = $bk AND NOT (a_dim = $ad AND a_key = $ak))
                """, ("$ad", a.Dim), ("$ak", a.Key), ("$bd", b.Dim), ("$bk", b.Key));
            _db.Exec("""
                INSERT INTO portal_links(a_dim, a_key, a_x, a_y, a_z, b_dim, b_key, b_x, b_y, b_z, created, updated, uses)
                VALUES ($ad, $ak, $ax, $ay, $az, $bd, $bk, $bx, $by, $bz, $t, $t, 1)
                ON CONFLICT(a_dim, a_key, b_dim, b_key) DO UPDATE SET a_x = excluded.a_x, a_y = excluded.a_y, a_z = excluded.a_z,
                    b_x = excluded.b_x, b_y = excluded.b_y, b_z = excluded.b_z, updated = excluded.updated, uses = uses + 1
                """, ("$ad", a.Dim), ("$ak", a.Key), ("$ax", a.X), ("$ay", a.Y), ("$az", a.Z),
                ("$bd", b.Dim), ("$bk", b.Key), ("$bx", b.X), ("$by", b.Y), ("$bz", b.Z), ("$t", now));
            return All().First(l => l.A.Dim == a.Dim && l.A.Key == a.Key && l.B.Dim == b.Dim && l.B.Key == b.Key);
        });
    }

    public List<PortalLink> All() =>
        _db.Query("SELECT id, a_dim, a_key, a_x, a_y, a_z, b_dim, b_key, b_x, b_y, b_z, updated, uses FROM portal_links ORDER BY updated DESC",
            r => new PortalLink(r.GetInt64(0),
                new PortalEnd((int)r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)),
                new PortalEnd((int)r.GetInt64(6), r.GetString(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10)),
                AgentDb.FromUnixMs(r.GetInt64(11)), (int)r.GetInt64(12)));

    /// <summary>Where a known portal leads (null when never travelled).</summary>
    public PortalEnd? LinkOf(int dim, string key) =>
        All().Select(l => l.From(dim)).FirstOrDefault(t => t is { } p && p.From.Key == key)?.To;

    /// <summary>
    /// Best linked portal from (<paramref name="fromDim"/>, here) to (<paramref name="toDim"/>, target): least walking on both
    /// sides (block distances in each dimension). Null when no pair connects the two dimensions.
    /// </summary>
    public PortalRoute? Plan(int fromDim, (double X, double Y, double Z) from, int toDim, (double X, double Y, double Z) to)
    {
        if (fromDim == toDim) return null;
        PortalRoute? best = null;
        foreach (var l in All())
        {
            if (l.From(fromDim) is not { } p || p.To.Dim != toDim) continue;
            double cost = p.From.Distance(from.X, from.Y, from.Z) + p.To.Distance(to.X, to.Y, to.Z);
            if (best == null || cost < best.Cost) best = new PortalRoute(p.From, p.To, cost);
        }
        return best;
    }

    /// <summary>Unlinked portal observations in a dimension (seen but never travelled), nearest first.</summary>
    public List<ObservationRow> Unlinked(int dim, double x, double y, double z) =>
        _store.Nearest(Kind, x, y, z, 5, 4096, dim).Select(t => t.Row).Where(r => LinkOf(dim, r.Key) == null).ToList();
}
