namespace MineClaw.Hive.Story;

/// <summary>
/// What a pick reads over and over, built once per state revision instead of per candidate (live 11-03: ~70% CPU with 10 pawns; a pick
/// scanned every designation for "forbidden" once per candidate, O(n²) with a few hundred designations): designations by kind with
/// their lease keys, the forbidden cells and drops, the forbidden zones; the world's entities once per pick; where pawns and players
/// stand (a block under someone's feet is never offered: "won't dig: someone is standing on it").
/// </summary>
public sealed partial class StoryColony
{
    private sealed class PickIndex
    {
        public long Stamp = long.MinValue;
        public readonly Dictionary<string, List<(DesignationState D, string Key, P3 Target)>> ByKind = new(StringComparer.Ordinal);
        public readonly HashSet<P3> ForbidCells = new();
        public readonly HashSet<int> ForbidEntities = new();
        /// <summary>Keep-out zones' columns and their level range (from the zones' cell sets: O(1) per test).</summary>
        public readonly Dictionary<long, (int Y1, int Y2)> ForbidCols = new();
    }

    private readonly PickIndex _index = new();
    private long _indexCheckedRev = -1;
    private IReadOnlyList<StoryEntity>? _pickEnts;

    /// <summary>The designations' and zones' stamp: every change touches an object's revision (or the count, a removal).</summary>
    private long IndexStamp()
    {
        long s = Designations.Count * 1_000_003L + Zones.Count * 7919L;
        foreach (var d in Designations) s = s * 31 + d.Rev;
        foreach (var z in Zones) s = s * 31 + z.Rev;
        return s;
    }

    /// <summary>The index, rebuilt only when a designation or zone changed (an integer pass decides; no allocation).</summary>
    private PickIndex Index()
    {
        var ix = _index;
        if (_indexCheckedRev == _rev) return ix; // nothing at all changed since the last look (every change bumps the revision)
        _indexCheckedRev = _rev;
        long stamp = IndexStamp();
        if (ix.Stamp == stamp) return ix;
        ix.ByKind.Clear();
        ix.ForbidCells.Clear();
        ix.ForbidEntities.Clear();
        ix.ForbidCols.Clear();
        foreach (var z in Zones)
            if (z.Kind == "forbidden") IndexForbiddenZone(ix, z);
        foreach (var d in Designations) IndexDesignation(ix, d);
        ix.Stamp = stamp;
        return ix;
    }

    /// <summary>A keep-out zone's columns and their level range (merged with another zone's over the same column).</summary>
    private static void IndexForbiddenZone(PickIndex ix, ZoneState z)
    {
        foreach (var k in z.Cells.Keys)
            ix.ForbidCols[k] = ix.ForbidCols.TryGetValue(k, out var r) ? (Math.Min(r.Y1, z.Area.Y1), Math.Max(r.Y2, z.Area.Y2)) : (z.Area.Y1, z.Area.Y2);
    }

    /// <summary>A forbid designation's cell or entity; any other block designation under its kind, by the block its job works.</summary>
    private void IndexDesignation(PickIndex ix, DesignationState d)
    {
        if (d.Kind == "forbid")
        {
            if (d.At is { } fa) ix.ForbidCells.Add(fa);
            if (d.Entity is { } fe) ix.ForbidEntities.Add(fe);
            return;
        }
        if (d.At is not { } at) return;
        var target = BlockTarget(d.Kind, at);
        if (!ix.ByKind.TryGetValue(d.Kind, out var list)) ix.ByKind[d.Kind] = list = new();
        list.Add((d, "b:" + target, target));
    }

    /// <summary>The block a designation's job works: a tall plant's lower half for any block kind (cut, mine, deconstruct: breaking either
    /// half takes both, so the two halves are ONE target and one lease), else the cell itself.</summary>
    private P3 BlockTarget(string kind, P3 at) => kind is "cut" or "mine" or "deconstruct" ? PlantBase(at) : at;

    /// <summary>The world's entities, read once per pick (each read walks every entity the view tracks).</summary>
    private IReadOnlyList<StoryEntity> Ents() => _pickEnts ??= World?.Entities() ?? [];

    /// <summary>The bodies a block can't be placed into (<see cref="Bodies"/>), and the entity list they were made from.</summary>
    private readonly List<(string Name, double X, double Y, double Z)> _bodies = new();
    private IReadOnlyList<StoryEntity>? _bodiesOf;

    /// <summary>
    /// Living bodies where they stand: monsters, animals and players the world sees, and online pawns by their own live position (the
    /// world's copy of a pawn lags behind it). Made once per pick, with <see cref="Ents"/>: it is remade whenever that list is, so
    /// none of the places that start a fresh entity read has to know about it.
    /// </summary>
    private List<(string Name, double X, double Y, double Z)> Bodies()
    {
        var ents = Ents();
        if (ReferenceEquals(ents, _bodiesOf)) return _bodies;
        _bodies.Clear();
        foreach (var e in ents)
            if (e.Kind == "mob" || e.Kind == "player" && !Pawns.ContainsKey(e.Name)) _bodies.Add((e.Name, e.X, e.Y, e.Z));
        foreach (var o in Pawns.Values)
            if (Host.Live(o.Name) is { Online: true } l) _bodies.Add((o.Name, l.X, l.Y, l.Z));
        _bodiesOf = ents;
        return _bodies;
    }

    /// <summary>A body standing at x,y,z (0.6 wide, 1.8 tall, as a player; most monsters are about that) overlaps the cell: the server
    /// refuses a block there.</summary>
    internal static bool InBox(P3 cell, double x, double y, double z) =>
        x + 0.3 > cell.X && x - 0.3 < cell.X + 1 && z + 0.3 > cell.Z && z - 0.3 < cell.Z + 1 && y + 1.8 > cell.Y && y < cell.Y + 1;

    /// <summary>Someone is in the cell now (read fresh, not the pick's list: a pawn's report comes between picks).</summary>
    private bool BodyIn(P3 cell)
    {
        if (World is { } w)
            foreach (var e in w.Entities())
                if (e.Kind is "mob" or "player" && InBox(cell, e.X, e.Y, e.Z)) return true;
        foreach (var o in Pawns.Values)
            if (Host.Live(o.Name) is { Online: true } l && InBox(cell, l.X, l.Y, l.Z)) return true;
        return false;
    }

    /// <summary>Is the cell open to a pawn: air, a plant, snow, a torch, a liquid (a dig can be made from or through it)?</summary>
    private static bool OpenCell(int id) => id == 0 || StoryBlocks.Replaceable.Contains(id) || StoryBlocks.Plants.Contains(id) || id is 50 or 78;

    /// <summary>
    /// A block to dig is exposed when a face of it touches an open cell (live 11-03: "can't reach dirt" ×46: a multi-layer mine
    /// designation handed out buried cells before the ones over and beside them were gone). A buried cell just waits: it is no
    /// failure, and it opens once its neighbour is dug (top-down, outside-in by itself).
    /// </summary>
    private bool Exposed(IStoryWorld w, P3 at)
    {
        if (!w.Loaded(at.X, at.Z)) return true; // can't tell: the pawn walks over and its own view decides
        return OpenCell(w.Block(at.X, at.Y + 1, at.Z).Id) || OpenCell(w.Block(at.X + 1, at.Y, at.Z).Id) || OpenCell(w.Block(at.X - 1, at.Y, at.Z).Id)
            || OpenCell(w.Block(at.X, at.Y, at.Z + 1).Id) || OpenCell(w.Block(at.X, at.Y, at.Z - 1).Id)
            || at.Y > 0 && OpenCell(w.Block(at.X, at.Y - 1, at.Z).Id);
    }

    /// <summary>The blocks under a body standing at x,y,z (its 0.6-wide footprint can rest on up to four).</summary>
    internal static IEnumerable<P3> Support(double x, double y, double z)
    {
        int sy = (int)Math.Floor(y - 0.01);
        int x0 = (int)Math.Floor(x - 0.3), x1 = (int)Math.Floor(x + 0.3), z0 = (int)Math.Floor(z - 0.3), z1 = (int)Math.Floor(z + 0.3);
        for (int bx = x0; bx <= x1; bx++)
            for (int bz = z0; bz <= z1; bz++)
                yield return new P3(bx, sy, bz);
    }

    /// <summary>Blocks someone else stands on now (other pawns, players): never handed out to dig (the client refuses them anyway).</summary>
    private HashSet<P3> StoodOnByOthers(string pawn)
    {
        var set = new HashSet<P3>();
        foreach (var o in Pawns.Values)
        {
            if (string.Equals(o.Name, pawn, StringComparison.OrdinalIgnoreCase)) continue;
            var l = Host.Live(o.Name);
            if (l.Online) foreach (var c in Support(l.X, l.Y, l.Z)) set.Add(c);
        }
        foreach (var e in Ents())
            if (e.Kind == "player" && !string.Equals(e.Name, pawn, StringComparison.OrdinalIgnoreCase))
                foreach (var c in Support(e.X, e.Y, e.Z)) set.Add(c);
        return set;
    }

    /// <summary>A cell beside <paramref name="at"/> to dig it from (floor under two open cells, not itself a target), for a pawn standing
    /// on its own target: it steps off first instead of refusing to dig its own floor.</summary>
    private P3? StandBeside(IStoryWorld w, P3 at, Func<P3, bool> isTarget)
    {
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) })
            for (int dy = 1; dy >= 0; dy--)
            {
                var c = at.Add(dx, dy, dz);
                var floor = c.Add(0, -1, 0);
                if (isTarget(floor)) continue;
                int f = w.Block(floor.X, floor.Y, floor.Z).Id;
                if (OpenCell(f) || StoryBlocks.Liquids.Contains(f)) continue;
                if (!OpenCell(w.Block(c.X, c.Y, c.Z).Id) || !OpenCell(w.Block(c.X, c.Y + 1, c.Z).Id)) continue;
                return c;
            }
        return null;
    }
}
