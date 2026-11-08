namespace MineClaw.Hive.Story;

/// <summary>
/// A zone's footprint (docs/web/story-mode.md "Zones"): a set of XZ columns. Zones are column-wise like the top-down map they are
/// painted on; the vertical part is the zone's level range (<see cref="ZoneState.Area"/> Y1..Y2, what it was painted at, the union
/// when zones merge), which the scans that need a height read (mine: ores in the range; farm: the ground near Y1). Stored as
/// normalised row runs <c>[z, x1, x2]</c> (sorted by z then x, maximal), held in memory as a hash set for O(1) membership.
/// </summary>
public sealed class ZoneCells
{
    private readonly HashSet<long> _set;

    public ZoneCells() => _set = new HashSet<long>();
    public ZoneCells(IEnumerable<long> keys) => _set = new HashSet<long>(keys);

    public static long Key(int x, int z) => ((long)x << 32) | (uint)z;
    public static int KX(long k) => (int)(k >> 32);
    public static int KZ(long k) => (int)k;

    public int Count => _set.Count;
    public bool Contains(int x, int z) => _set.Contains(Key(x, z));
    public bool Contains(long k) => _set.Contains(k);
    public IEnumerable<long> Keys => _set;
    public void Add(int x, int z) => _set.Add(Key(x, z));
    public void UnionWith(ZoneCells o) => _set.UnionWith(o._set);
    public void ExceptWith(ZoneCells o) => _set.ExceptWith(o._set);
    public bool Overlaps(ZoneCells o) => Count <= o.Count ? _set.Overlaps(o._set) : o._set.Overlaps(_set);
    public ZoneCells Clone() => new(_set);

    public static ZoneCells Rect(int x1, int z1, int x2, int z2)
    {
        var c = new ZoneCells();
        for (int x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x++)
            for (int z = Math.Min(z1, z2); z <= Math.Max(z1, z2); z++)
                c.Add(x, z);
        return c;
    }

    public static ZoneCells Rect(Area a) => Rect(a.X1, a.Z1, a.X2, a.Z2);

    /// <summary>From row runs <c>[z, x1, x2]</c> (any order; malformed rows are skipped).</summary>
    public static ZoneCells FromRows(IEnumerable<int[]> rows)
    {
        var c = new ZoneCells();
        foreach (var r in rows)
        {
            if (r is not { Length: 3 }) continue;
            for (int x = Math.Min(r[1], r[2]); x <= Math.Max(r[1], r[2]); x++) c.Add(x, r[0]);
        }
        return c;
    }

    /// <summary>Every cell, sorted by z then x (deterministic scans).</summary>
    public List<(int X, int Z)> Sorted()
    {
        var keys = _set.Select(k => (X: KX(k), Z: KZ(k))).ToList();
        keys.Sort((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));
        return keys;
    }

    /// <summary>Normalised row runs <c>[z, x1, x2]</c>: sorted by z then x1, each run maximal.</summary>
    public List<int[]> Rows()
    {
        var rows = new List<int[]>();
        int[]? cur = null;
        foreach (var (x, z) in Sorted())
        {
            if (cur != null && cur[0] == z && cur[2] == x - 1) { cur[2] = x; continue; }
            cur = [z, x, x];
            rows.Add(cur);
        }
        return rows;
    }

    /// <summary>The bounding box (x1, z1, x2, z2); zeros when empty.</summary>
    public (int X1, int Z1, int X2, int Z2) Bounds()
    {
        if (_set.Count == 0) return (0, 0, 0, 0);
        int x1 = int.MaxValue, z1 = int.MaxValue, x2 = int.MinValue, z2 = int.MinValue;
        foreach (var k in _set)
        {
            int x = KX(k), z = KZ(k);
            if (x < x1) x1 = x;
            if (x > x2) x2 = x;
            if (z < z1) z1 = z;
            if (z > z2) z2 = z;
        }
        return (x1, z1, x2, z2);
    }

    private static readonly (int Dx, int Dz)[] N4 = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>Does <paramref name="o"/> overlap these cells or touch one of them (4-neighbour adjacency)?</summary>
    public bool Touches(ZoneCells o)
    {
        var (small, big) = Count <= o.Count ? (this, o) : (o, this);
        foreach (var k in small._set)
        {
            if (big._set.Contains(k)) return true;
            int x = KX(k), z = KZ(k);
            foreach (var (dx, dz) in N4)
                if (big._set.Contains(Key(x + dx, z + dz))) return true;
        }
        return false;
    }

    /// <summary>The 4-connected parts, largest first (ties: the one with the smallest sorted first cell first).</summary>
    public List<ZoneCells> Components()
    {
        var left = new HashSet<long>(_set);
        var parts = new List<(ZoneCells Part, long First)>();
        foreach (var (sx, sz) in Sorted())
        {
            long s = Key(sx, sz);
            if (!left.Remove(s)) continue;
            var part = new ZoneCells();
            var stack = new Stack<long>();
            stack.Push(s);
            while (stack.Count > 0)
            {
                long k = stack.Pop();
                part._set.Add(k);
                int x = KX(k), z = KZ(k);
                foreach (var (dx, dz) in N4)
                {
                    long n = Key(x + dx, z + dz);
                    if (left.Remove(n)) stack.Push(n);
                }
            }
            parts.Add((part, s));
        }
        return parts.Select((p, i) => (p.Part, i)).OrderByDescending(p => p.Part.Count).ThenBy(p => p.i).Select(p => p.Part).ToList();
    }
}

/// <summary>What a paint or an erase changed (the zones route's answer).</summary>
public sealed class ZonePaintResult
{
    /// <summary>The zone the painted cells are in now (add).</summary>
    public string? Id { get; set; }
    public List<string> Created { get; } = new();
    public List<(string Into, List<string> From)> Merged { get; } = new();
    /// <summary>Zones that took new cells without merging another (add).</summary>
    public List<string> Grown { get; } = new();
    public List<string> Shrunk { get; } = new();
    public List<(string From, List<string> Into)> Split { get; } = new();
    public List<string> Deleted { get; } = new();
    /// <summary>Every zone whose cells or settings changed (or that was made): the colony touches their revisions.</summary>
    public HashSet<ZoneState> Changed { get; } = new();
}

/// <summary>
/// RimWorld-style zone painting over a colony's zone list (list order = age: new zones are appended, a merge keeps the oldest's
/// place). Work zones (farm, chop, mine) are mutually exclusive: painting one overwrites the other work zones' cells under it. Home
/// areas and keep-out areas are overlay layers: they never overwrite and are never overwritten (each layer only merges with
/// itself). "Same type" is the same kind, and for growing zones also the same plant (a different plant overwrites). Pure: ids come
/// from <c>newId</c>, the caller touches revisions and caches.
/// </summary>
public static class ZonePainter
{
    public static readonly IReadOnlyList<string> Overlays = ["home", "forbidden", "dump", "preserve", "guard"];
    public const int MaxCells = 65_536;

    public static bool IsOverlay(string kind) => Overlays.Contains(kind);

    private static bool SameType(ZoneState z, string kind, string? plant) =>
        z.Kind == kind && (kind != "farm" || string.Equals(z.Plant, plant, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Paint <paramref name="cells"/> as a zone of <paramref name="kind"/> (growing: <paramref name="plant"/>). With <paramref name="into"/>
    /// the paint takes that zone's type and extends it (it keeps its id even when an older zone of the type is merged in).
    /// <paramref name="make"/> builds a new zone's settings (id, name, growing settings) when the cells touch none of the type.
    /// Error (nothing changed) when the result would pass <see cref="MaxCells"/>.
    /// </summary>
    public static (ZonePaintResult? Result, string? Error) Paint(List<ZoneState> zones, string kind, string? plant, ZoneCells cells, int y1, int y2,
        ZoneState? into, Func<ZoneState> make, Func<string> newId)
    {
        if (into != null) { kind = into.Kind; plant = into.Plant; }
        var touched = TouchedOfType(zones, kind, plant, cells, into);
        var target = into != null && touched.Contains(into) ? into : touched.FirstOrDefault();
        long size = cells.Count + touched.Sum(z => (long)z.Cells.Count);
        if (size > MaxCells) return (null, $"zone too large ({MaxCells} cells max)");

        var r = new ZonePaintResult();
        // Overwrite: the other work zones lose the painted cells (overlays neither overwrite nor are overwritten).
        if (!IsOverlay(kind))
            foreach (var z in zones.Where(z => !IsOverlay(z.Kind) && !SameType(z, kind, plant) && z.Cells.Overlaps(cells)).ToList())
                Subtract(zones, z, cells, r, newId);
        r.Id = target == null ? NewZone(zones, kind, plant, cells, y1, y2, make, r) : GrowZone(zones, target, touched, cells, y1, y2, r);
        return (r, null);
    }

    /// <summary>The zones of the paint's type its cells touch (and the zone painted into, when it touches them).</summary>
    private static List<ZoneState> TouchedOfType(List<ZoneState> zones, string kind, string? plant, ZoneCells cells, ZoneState? into)
    {
        var touched = zones.Where(z => SameType(z, kind, plant) && z.Cells.Touches(cells)).ToList();
        if (into != null && !touched.Contains(into) && into.Cells.Touches(cells)) touched.Add(into);
        return touched;
    }

    /// <summary>The cells touch no zone of the type: a new one (settings from <paramref name="make"/>).</summary>
    private static string NewZone(List<ZoneState> zones, string kind, string? plant, ZoneCells cells, int y1, int y2, Func<ZoneState> make, ZonePaintResult r)
    {
        var z = make();
        z.Kind = kind;
        if (kind == "farm" && z.Plant == null && plant != null) z.Plant = plant;
        z.SetCells(cells.Clone(), y1, y2);
        zones.Add(z);
        r.Created.Add(z.Id);
        r.Changed.Add(z);
        return z.Id;
    }

    /// <summary>The target grows by the cells, and the other touched zones of the type merge into it (it keeps its place and id).</summary>
    private static string GrowZone(List<ZoneState> zones, ZoneState target, List<ZoneState> touched, ZoneCells cells, int y1, int y2, ZonePaintResult r)
    {
        var all = target.Cells.Clone();
        int before = all.Count;
        all.UnionWith(cells);
        int lo = Math.Min(target.Area.Y1, y1), hi = Math.Max(target.Area.Y2, y2);
        var from = new List<string>();
        foreach (var o in touched.Where(o => o != target))
        {
            all.UnionWith(o.Cells);
            lo = Math.Min(lo, o.Area.Y1);
            hi = Math.Max(hi, o.Area.Y2);
            zones.Remove(o);
            from.Add(o.Id);
        }
        if (from.Count > 0) r.Merged.Add((target.Id, from));
        else if (all.Count > before) r.Grown.Add(target.Id);
        if (all.Count != before || lo != target.Area.Y1 || hi != target.Area.Y2)
        {
            target.SetCells(all, lo, hi);
            r.Changed.Add(target);
        }
        return target.Id;
    }

    /// <summary>Erase <paramref name="cells"/> from every zone of <paramref name="kind"/> (null = any kind).</summary>
    public static ZonePaintResult Erase(List<ZoneState> zones, string? kind, ZoneCells cells, Func<string> newId)
    {
        var r = new ZonePaintResult();
        foreach (var z in zones.Where(z => (kind == null || z.Kind == kind) && z.Cells.Overlaps(cells)).ToList())
            Subtract(zones, z, cells, r, newId);
        return r;
    }

    /// <summary>
    /// Takes <paramref name="cells"/> out of <paramref name="z"/>: an empty zone goes; a zone cut into parts keeps the largest and
    /// the others become zones of their own (same settings, the name numbered " 2", " 3").
    /// </summary>
    private static void Subtract(List<ZoneState> zones, ZoneState z, ZoneCells cells, ZonePaintResult r, Func<string> newId)
    {
        var rest = z.Cells.Clone();
        rest.ExceptWith(cells);
        if (rest.Count == z.Cells.Count) return;
        if (rest.Count == 0)
        {
            zones.Remove(z);
            r.Deleted.Add(z.Id);
            r.Changed.Remove(z);
            return;
        }
        var parts = rest.Components();
        z.SetCells(parts[0], z.Area.Y1, z.Area.Y2);
        r.Shrunk.Add(z.Id);
        r.Changed.Add(z);
        if (parts.Count == 1) return;
        var made = new List<string>();
        int at = zones.IndexOf(z);
        for (int i = 1; i < parts.Count; i++)
        {
            var n = z.CopySettings(newId());
            if (z.Name is { Length: > 0 } name)
            {
                var suffix = $" {i + 1}";
                n.Name = (name.Length + suffix.Length > 40 ? name[..(40 - suffix.Length)] : name) + suffix;
            }
            n.SetCells(parts[i], z.Area.Y1, z.Area.Y2);
            zones.Insert(++at, n);
            made.Add(n.Id);
            r.Changed.Add(n);
        }
        r.Split.Add((z.Id, made));
    }
}
