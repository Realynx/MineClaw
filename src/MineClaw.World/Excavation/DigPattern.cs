using System.Globalization;
using System.Text;

namespace MineClaw.World;

/// <summary>
/// A parametric dug pattern: a unit of air cells and the transform every repeat applies to the one before: a quarter-turn
/// about a vertical axis (<see cref="Turn"/> degrees, + = clockwise seen from above, east → south), then a <see cref="Step"/>.
/// Repeat k is T^k(unit). One primitive covers a spiral stair (a 3-tall column, step 0,-1,0, turn -90 about the 2×2 core's
/// centre), a straight stair (column 3, step 1,-1,0), a corridor (column 2, step 1,0,0) or a wider spiral (a 2-column unit).
/// Live 10-30 CT 114 (01:30-02:01): Fox taught the stair as "mine them in vertical patterns of 3, and then drop a level rotate 90
/// deg" and asked to "remember this build so we can do it again elsewhere"; the capture stored a frozen block set ("The stair is
/// recorded at y38-60 only", so at 02:31 "no stair at y10-13 … No room dug"). A pattern is learned from the demonstrated cells
/// (<see cref="Learn"/>), or written as a rule (<see cref="Parse"/>), and re-applied anywhere, to any depth.
/// Coordinates are cell indices; the pivot is relative to <see cref="Anchor"/> and may sit on a cell corner (x.5).
/// </summary>
public sealed record DigPattern
{
    /// <summary>World cell the unit is relative to (repeat 0).</summary>
    public required BlockPos Anchor { get; init; }
    /// <summary>The unit's cells relative to <see cref="Anchor"/>.</summary>
    public required IReadOnlyList<BlockPos> Unit { get; init; }
    public BlockPos Step { get; init; }
    /// <summary>Degrees per repeat: 0, 90, 180 or -90.</summary>
    public int Turn { get; init; }
    public double PivotX { get; init; }
    public double PivotZ { get; init; }

    private int Quarter => ((Turn / 90) % 4 + 4) % 4;

    /// <summary>T(p): turn about the pivot, then step (world cells).</summary>
    public BlockPos Next(BlockPos p) => Rotate(p, Quarter) + Step;

    /// <summary>T⁻¹(p).</summary>
    public BlockPos Prev(BlockPos p) => Rotate(p - Step, (4 - Quarter) % 4);

    private BlockPos Rotate(BlockPos p, int quarters)
    {
        if (quarters == 0) return p;
        double px = Anchor.X + PivotX, pz = Anchor.Z + PivotZ;
        double dx = p.X - px, dz = p.Z - pz;
        for (int i = 0; i < quarters; i++) (dx, dz) = (-dz, dx); // clockwise from above: +x (east) → +z (south)
        return new BlockPos((int)Math.Round(px + dx), p.Y, (int)Math.Round(pz + dz));
    }

    /// <summary>World cells of repeat <paramref name="k"/>.</summary>
    public IEnumerable<BlockPos> Repeat(int k)
    {
        foreach (var u in Unit)
        {
            var p = Anchor + u;
            for (int i = 0; i < k; i++) p = Next(p);
            yield return p;
        }
    }

    /// <summary>Cells of repeats <paramref name="from"/>..from+count−1 with their repeat index, in order.</summary>
    public IEnumerable<(BlockPos Pos, int K)> Indexed(int from, int count)
    {
        var cur = Unit.Select(u => Anchor + u).ToArray();
        for (int k = 0; k < from + count; k++)
        {
            if (k >= from) foreach (var c in cur) yield return (c, k);
            for (int i = 0; i < cur.Length; i++) cur[i] = Next(cur[i]);
        }
    }

    public HashSet<BlockPos> Cells(int from, int count) => Indexed(from, count).Select(c => c.Pos).ToHashSet();

    /// <summary>
    /// Repeats from 0 that stay at or above <paramref name="y"/> (a descending pattern: "dig it to y12" ends with the repeat whose
    /// lowest cell is y12) or at or below it (ascending); capped at <paramref name="max"/>. A level pattern has no depth: null.
    /// </summary>
    public int? RepeatsTo(int y, int max = 512)
    {
        if (Step.Y == 0) return null;
        int n = 0;
        foreach (var k in Enumerable.Range(0, max))
        {
            var cells = Repeat(k).ToList();
            bool inside = Step.Y < 0 ? cells.Min(c => c.Y) >= y : cells.Max(c => c.Y) <= y;
            if (!inside) break;
            n = k + 1;
        }
        return n;
    }

    /// <summary>The same pattern from another anchor ("dig the same shaft over there"), optionally turned as a whole by
    /// <paramref name="degrees"/> about the new anchor (unit, pivot and step all turn).</summary>
    public DigPattern At(BlockPos anchor, int degrees = 0)
    {
        int q = ((degrees / 90) % 4 + 4) % 4;
        if (q == 0) return this with { Anchor = anchor };
        (int, int) R(int x, int z) { for (int i = 0; i < q; i++) (x, z) = (-z, x); return (x, z); }
        (double, double) Rd(double x, double z) { for (int i = 0; i < q; i++) (x, z) = (-z, x); return (x, z); }
        var unit = Unit.Select(u => { var (x, z) = R(u.X, u.Z); return new BlockPos(x, u.Y, z); }).ToList();
        var (sx, sz) = R(Step.X, Step.Z);
        // Cell indices and the pivot turn alike about the anchor (index space), so a pivot on a corner stays on a corner.
        var (px, pz) = Rd(PivotX, PivotZ);
        return this with { Anchor = anchor, Unit = unit, Step = new BlockPos(sx, Step.Y, sz), PivotX = px, PivotZ = pz };
    }

    /// <summary>The rule text: "unit 0,0,0 0,-1,0 0,-2,0 step 0,-1,0 turn -90 pivot 0.5,0.5" (a column unit prints "column 3").</summary>
    public string Rule
    {
        get
        {
            var sb = new StringBuilder();
            bool column = Unit.Count > 0 && Unit.Select((u, i) => u == new BlockPos(0, -i, 0)).All(b => b);
            if (column) sb.Append("column ").Append(Unit.Count);
            else sb.Append("unit ").Append(string.Join(' ', Unit.Select(Fmt)));
            sb.Append(" step ").Append(Fmt(Step));
            if (Quarter != 0) sb.Append(" turn ").Append(Quarter == 3 ? -90 : Quarter * 90).Append(" pivot ").Append(D(PivotX)).Append(',').Append(D(PivotZ));
            return sb.ToString();
        }
    }

    public override string ToString() => $"{Rule} from {Fmt(Anchor)}";

    private static string Fmt(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
    private static string D(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a rule ("column 3 step 0,-1,0 turn -90 pivot 0.5,0.5" or "unit 0,0,0 0,1,0 step 1,0,0") anchored at
    /// <paramref name="anchor"/>. Null with <paramref name="error"/> when it doesn't describe a pattern.
    /// </summary>
    public static DigPattern? Parse(string rule, BlockPos anchor, out string? error)
    {
        error = null;
        var unit = new List<BlockPos>();
        BlockPos? step = null;
        int turn = 0;
        double px = 0, pz = 0;
        var t = rule.Replace(';', ' ').Replace('=', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < t.Length; i++)
        {
            switch (t[i].ToLowerInvariant())
            {
                case "column":
                    if (i + 1 >= t.Length || !int.TryParse(t[++i], out int n) || n is < 1 or > 16) { error = "column needs a height 1..16"; return null; }
                    for (int k = 0; k < n; k++) unit.Add(new BlockPos(0, -k, 0));
                    break;
                case "unit":
                    while (i + 1 < t.Length && Triple(t[i + 1]) is { } u) { unit.Add(u); i++; }
                    break;
                case "step":
                    if (i + 1 >= t.Length || Triple(t[++i]) is not { } s) { error = "step needs dx,dy,dz"; return null; }
                    step = s;
                    break;
                case "turn":
                    if (i + 1 >= t.Length || !int.TryParse(t[++i].TrimEnd('°'), out turn) || turn % 90 != 0) { error = "turn is 0, 90, 180 or -90"; return null; }
                    break;
                case "pivot" or "about":
                    var v = i + 1 < t.Length ? t[++i].Split(',') : [];
                    if (v.Length != 2 || !double.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out px)
                        || !double.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out pz)) { error = "pivot needs x,z (relative, may end in .5)"; return null; }
                    break;
                default:
                    error = $"unknown word '{t[i]}' (rule: column N | unit x,y,z …, step dx,dy,dz, turn deg, pivot x,z)";
                    return null;
            }
        }
        if (unit.Count == 0) { error = "no unit cells (column N or unit x,y,z …)"; return null; }
        if (step is not { } st || st == default && turn % 360 == 0) { error = "needs a step dx,dy,dz"; return null; }
        var p = new DigPattern { Anchor = anchor, Unit = unit.Distinct().ToList(), Step = st, Turn = Norm(turn), PivotX = px, PivotZ = pz };
        if (p.Quarter % 2 == 1 && !SameFraction(px, pz)) { error = "a 90° turn needs a pivot on a cell centre or corner (x and z both .0 or both .5)"; return null; }
        if (p.Quarter != 0 && st.Y == 0) { error = "a turning pattern needs a vertical step (it would dig the same cells again)"; return null; }
        return p;
    }

    private static int Norm(int deg) => (((deg / 90) % 4 + 4) % 4) switch { 1 => 90, 2 => 180, 3 => -90, _ => 0 };
    private static bool SameFraction(double a, double b) => Math.Abs(Frac(a) - Frac(b)) < 1e-6;
    private static double Frac(double v) => v - Math.Floor(v);

    private static BlockPos? Triple(string s)
    {
        var v = s.Split(',');
        if (v.Length != 3) return null;
        return int.TryParse(v[0], out int x) && int.TryParse(v[1], out int y) && int.TryParse(v[2], out int z) ? new BlockPos(x, y, z) : null;
    }

    /// <summary>What <see cref="Learn"/> found: the pattern, how many repeats the cells show, and the cells it doesn't explain.</summary>
    public sealed record Learned(DigPattern Pattern, int Repeats, IReadOnlyList<BlockPos> Outliers, int Missing)
    {
        public string Summary => $"{Pattern.Rule}: {Repeats} repeats of {Pattern.Unit.Count} cell{(Pattern.Unit.Count == 1 ? "" : "s")} from {Fmt(Pattern.Anchor)}"
            + (Outliers.Count > 0 ? $"; {Outliers.Count} cell{(Outliers.Count == 1 ? "" : "s")} off the pattern (e.g. {Fmt(Outliers[0])})" : "")
            + (Missing > 0 ? $"; {Missing} not dug yet" : "");
    }

    /// <summary>
    /// Learns the unit and transform behind a demonstrated set of dug cells (the stair's air): for every candidate transform
    /// (a step within 4 down / 3 across, no turn; or a vertical step with a quarter or half turn about the cells' centre axis) the
    /// cells split into chains c, T(c), T²(c)…; the chain starts are the unit. The smallest unit that explains the cells wins
    /// (score: 4 per unit cell + cells off the pattern + cells a full repeat would still need), at least 3 repeats; ties go to a
    /// downward step. Null when nothing repeats.
    /// </summary>
    public static Learned? Learn(IReadOnlyCollection<BlockPos> cells)
    {
        var set = cells as IReadOnlySet<BlockPos> ?? cells.ToHashSet();
        if (set.Count < 3 || set.Count > 8192) return null;
        int minX = set.Min(c => c.X), maxX = set.Max(c => c.X), minZ = set.Min(c => c.Z), maxZ = set.Max(c => c.Z);
        // Axis candidates: the cells' box centre, and their mean on the half-block grid (a stray wall hole moves the box, not the mean).
        double Half(double v) => Math.Round(v * 2) / 2;
        var axes = new HashSet<(double X, double Z)>
        {
            ((minX + maxX) / 2.0, (minZ + maxZ) / 2.0),
            (Half(set.Average(c => c.X)), Half(set.Average(c => c.Z))),
        };
        var top = set.OrderByDescending(c => c.Y).ThenBy(c => c.X).ThenBy(c => c.Z).First();
        var candidates = new List<(int Turn, BlockPos Step, double Cx, double Cz)>();
        for (int dy = -4; dy <= 4; dy++)
        for (int dx = -3; dx <= 3; dx++)
        for (int dz = -3; dz <= 3; dz++)
            if (dx != 0 || dy != 0 || dz != 0) candidates.Add((0, new BlockPos(dx, dy, dz), 0, 0));
        foreach (var (ax, az) in axes)
        foreach (int turn in new[] { 90, -90, 180 })
        {
            if (turn != 180 && !SameFraction(ax, az)) continue;
            for (int dy = -4; dy <= 4; dy++) if (dy != 0) candidates.Add((turn, new BlockPos(0, dy, 0), ax, az));
        }
        Learned? best = null;
        double bestScore = double.MaxValue;
        foreach (var (turn, step, cx, cz) in candidates)
        {
            var probe = new DigPattern { Anchor = top, Unit = [default], Step = step, Turn = turn, PivotX = cx - top.X, PivotZ = cz - top.Z };
            var starts = set.Where(c => !set.Contains(probe.Prev(c))).ToList();
            if (starts.Count == 0 || starts.Count * 3 > set.Count) continue; // a cycle, or fewer than 3 repeats on average
            var len = new Dictionary<BlockPos, int>(starts.Count);
            foreach (var s in starts)
            {
                int n = 0;
                for (var p = s; set.Contains(p) && n <= set.Count; p = probe.Next(p)) n++;
                len[s] = n;
            }
            int reps = len.Values.Max();
            if (reps < 3) continue;
            var unit = starts.Where(s => len[s] * 2 >= reps).ToList();
            var outliers = starts.Where(s => len[s] * 2 < reps).SelectMany(s => Chain(probe, s, len[s])).ToList();
            int missing = unit.Sum(s => reps - len[s]);
            double score = 4 * unit.Count + outliers.Count + missing + (step.Y < 0 ? 0 : 0.5) + 0.01 * (Math.Abs(step.X) + Math.Abs(step.Z));
            if (score >= bestScore) continue;
            // The unit sits relative to its own top cell: anchor = its highest, then west-most, then north-most cell.
            var anchor = unit.OrderByDescending(c => c.Y).ThenBy(c => c.X).ThenBy(c => c.Z).First();
            var pattern = new DigPattern
            {
                Anchor = anchor, Unit = unit.Select(u => u - anchor).OrderByDescending(u => u.Y).ThenBy(u => u.X).ThenBy(u => u.Z).ToList(),
                Step = step, Turn = turn, PivotX = cx - anchor.X, PivotZ = cz - anchor.Z,
            };
            if (pattern.Quarter == 0) pattern = pattern with { PivotX = 0, PivotZ = 0 };
            bestScore = score;
            best = new Learned(pattern, reps, outliers, missing);
        }
        return best;
    }

    private static IEnumerable<BlockPos> Chain(DigPattern t, BlockPos s, int n)
    {
        var p = s;
        for (int i = 0; i < n; i++) { yield return p; p = t.Next(p); }
    }

    /// <summary>Cells a set of dug cells stands on: each cell's floor that isn't itself dug (a stair's treads, a room's floor).</summary>
    public static HashSet<BlockPos> Floors(IReadOnlySet<BlockPos> air)
    {
        var f = new HashSet<BlockPos>();
        foreach (var c in air) if (!air.Contains(c.Down())) f.Add(c.Down());
        return f;
    }
}
