using System.Text.RegularExpressions;
using MineClaw.Bot.Blueprints;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>
/// Why the cells a build left are left, in one line grouped by reason with a count and a sample position: "13 no stand: above
/// my reach (I can't climb) e.g. -27,71,13; 676 waiting on supports e.g. -33,76,7; 0 protected". Built from the plan's
/// infeasible cells (the slicer's reasons) and the steps' own notes (protected, someone else's block, a missing item, a wait),
/// so the brain and the worker read the cause instead of guessing it (live 10-28: "placed 0, 689 missing" and nothing else).
/// Zero tokens beyond the line itself.
/// </summary>
public static partial class BuildSkips
{
    /// <summary>The slicer's reasons (prefixes; details follow after "; "). Grouping uses <see cref="Category"/>.</summary>
    /// <summary>Wholly above every stand near it and no climb (pillar, stair) possible; the reason follows: "(I can't climb: every
    /// pillar or stair spot near it is beside the void)".</summary>
    public const string AboveReach = "no stand: above my reach";
    public const string NotNear = "no stand: I can't walk near it from here";
    public const string NoStand = "no stand within reach of a support face";
    public const string Waiting = "waiting on supports";
    public const string Protected = "protected";
    /// <summary>The group of "no support within N scaffold blocks (over the void)".</summary>
    public const string OverVoid = "no support: over the void past my scaffold limit";

    /// <summary>The group a reason belongs to: a fixed label (coordinates, counts and details dropped).</summary>
    public static string Category(string? why)
    {
        if (string.IsNullOrWhiteSpace(why)) return "not reached";
        var w = why.Trim();
        if (w.StartsWith("not reached", StringComparison.OrdinalIgnoreCase)) return "not reached (the run stopped first)";
        if (w.StartsWith(Waiting, StringComparison.OrdinalIgnoreCase) || w.StartsWith("waits for an earlier step", StringComparison.OrdinalIgnoreCase))
            return Waiting;
        if (w.StartsWith(AboveReach, StringComparison.OrdinalIgnoreCase)) return "no stand: above my reach (I can't climb)";
        if (w.StartsWith(NotNear, StringComparison.OrdinalIgnoreCase)) return NotNear;
        if (w.StartsWith("no support within", StringComparison.OrdinalIgnoreCase)) return OverVoid;
        if (w.StartsWith("no stand reaches scaffold cell", StringComparison.OrdinalIgnoreCase)) return "no stand for its scaffold";
        if (w.StartsWith("no stand", StringComparison.OrdinalIgnoreCase)) return "no stand in reach";
        if (w.Contains("water bucket", StringComparison.OrdinalIgnoreCase)) return "below the floor: needs a water bucket";
        if (w.StartsWith("water descent", StringComparison.OrdinalIgnoreCase) || w.StartsWith("climbing back up", StringComparison.OrdinalIgnoreCase)
            || w.StartsWith("no edge with open space", StringComparison.OrdinalIgnoreCase)) return "no safe water column";
        if (w.Contains("wall me in", StringComparison.OrdinalIgnoreCase)) return "would wall me in";
        if (w.StartsWith("I'm not standing", StringComparison.OrdinalIgnoreCase)) return "I'm not standing anywhere walkable";
        if (w.StartsWith(Protected, StringComparison.OrdinalIgnoreCase)) return Protected;
        if (w.Contains("someone", StringComparison.OrdinalIgnoreCase) || w.Contains("not mine", StringComparison.OrdinalIgnoreCase)) return "someone else's block there";
        if (w.StartsWith("not ours", StringComparison.OrdinalIgnoreCase)) return "not mine: left";
        if (w.StartsWith("wrong block", StringComparison.OrdinalIgnoreCase)) return "a wrong block there";
        if (w.StartsWith("missing ", StringComparison.OrdinalIgnoreCase)) return "no " + Detail().Replace("missing ", "") + " on hand";
        if (w.Contains("another lane", StringComparison.OrdinalIgnoreCase)) return "waiting on another lane";
        if (w.StartsWith("needs step", StringComparison.OrdinalIgnoreCase)) return "needs a skipped step";
        if (w.StartsWith("waited too long", StringComparison.OrdinalIgnoreCase)) return "waited too long";
        if (w.StartsWith("goto:", StringComparison.OrdinalIgnoreCase)) return "no path to its stand";
        return "failed: " + Detail();

        // The reason's head without positions or counts ("missing cobblestone slab (need 40, have 0): ..." → "missing cobblestone slab").
        string Detail()
        {
            var head = w.Split([';', '(', ':'], 2)[0].Trim();
            head = Coords().Replace(head, "").Trim();
            return head.Length > 48 ? head[..48] : head;
        }
    }

    [GeneratedRegex(@"-?\d+(,-?\d+){1,2}|#\d+|\d+")]
    private static partial Regex Coords();

    /// <summary>
    /// "13 no stand: above my reach (I can't climb) e.g. -27,71,13; 676 waiting on supports e.g. -33,76,7; 0 protected" (most
    /// first, at most <paramref name="max"/> groups); null when nothing was skipped. The protected count is always named, even
    /// at 0 (it is the usual first guess).
    /// </summary>
    public static string? Line(IEnumerable<(BlockPos Pos, string? Why)> items, int max = 4)
    {
        var groups = items.GroupBy(i => Category(i.Why))
            .Select(g => (Label: g.Key, Count: g.Count(), Sample: g.Select(i => i.Pos).OrderBy(p => p.Y).ThenBy(p => p.X).ThenBy(p => p.Z).First()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Label, StringComparer.Ordinal).ToList();
        if (groups.Count == 0) return null;
        var parts = groups.Take(max).Select(g => $"{g.Count} {g.Label} e.g. {g.Sample.X},{g.Sample.Y},{g.Sample.Z}").ToList();
        if (groups.Count > max) parts.Add($"{groups.Skip(max).Sum(g => g.Count)} other");
        if (!groups.Any(g => g.Label == Protected)) parts.Add("0 protected");
        return string.Join("; ", parts);
    }

    /// <summary>
    /// The reason for every cell in <paramref name="left"/> (design cells not standing after a run): the slicer's infeasible
    /// reason, else the note of the step that targets it (skipped: protected, someone's block…; pending: a missing item, a wait),
    /// else <paramref name="unplanned"/> (held back: no item), else "not reached" (the run stopped first). Steps skipped on other
    /// cells (a protected obstruction) count too.
    /// </summary>
    public static List<(BlockPos Pos, string? Why)> Of(BuildPlan bp, IEnumerable<BlockPos> left, Func<BlockPos, string?>? unplanned = null,
        IEnumerable<int>? steps = null)
    {
        List<PlanStep> list;
        List<Infeasible> infeasible;
        lock (bp)
        {
            list = (steps?.Where(i => i >= 0 && i < bp.Steps.Count).Select(i => bp.Steps[i]) ?? bp.Steps).ToList();
            infeasible = bp.Infeasible.ToList();
        }
        var inf = new Dictionary<BlockPos, string>();
        foreach (var i in infeasible) inf.TryAdd(i.Pos, i.Why);
        var byTarget = new Dictionary<BlockPos, PlanStep>();
        foreach (var s in list.Where(s => s.Action is StepAction.Place or StepAction.Clear)) byTarget[s.Target] = s;
        var result = new List<(BlockPos, string?)>();
        var seen = new HashSet<BlockPos>();
        foreach (var p in left)
        {
            if (!seen.Add(p)) continue;
            if (inf.TryGetValue(p, out var why)) result.Add((p, why));
            else if (byTarget.TryGetValue(p, out var s)) result.Add((p, s.Note));
            else result.Add((p, unplanned?.Invoke(p)));
        }
        foreach (var s in list.Where(s => s.Status == StepStatus.Skipped && !seen.Contains(s.Target)))
            if (seen.Add(s.Target)) result.Add((s.Target, s.Note));
        return result;
    }

    // ------------------------------------------------------------------ the foundation suggestion

    /// <summary>A design's footprint in world coordinates: x/z of every planned cell and its y range.</summary>
    public readonly record struct Area(int X0, int Z0, int X1, int Z1, int Y0, int Y1)
    {
        /// <summary>"footprint x -33..-18, z 7..22, y 64..79".</summary>
        public string Text => $"footprint x {X0}..{X1}, z {Z0}..{Z1}, y {Y0}..{Y1}";
    }

    public static Area? Footprint(IEnumerable<BlockPos> cells)
    {
        int x0 = int.MaxValue, z0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue, y1 = int.MinValue;
        foreach (var p in cells)
        {
            x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
            z0 = Math.Min(z0, p.Z); z1 = Math.Max(z1, p.Z);
            y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
        }
        return x0 == int.MaxValue ? null : new Area(x0, z0, x1, z1, y0, y1);
    }

    /// <summary>"waiting on supports; rests on -27,75,16 ..." to -27,75,16.</summary>
    public static BlockPos? RestsOn(string? why)
    {
        if (why == null) return null;
        int i = why.IndexOf("rests on ", StringComparison.Ordinal);
        if (i < 0) return null;
        var c = why[(i + 9)..].Split([',', ' ', ';'], 4);
        return c.Length >= 3 && int.TryParse(c[0], out var x) && int.TryParse(c[1], out var y) && int.TryParse(c[2], out var z) ? new BlockPos(x, y, z) : null;
    }

    /// <summary>
    /// "lay a floor: expand_island area=-33,7,-18,22 y=63 (192 cells, 192 cobble)" when most skipped cells hang over the void (their
    /// own reason or a "rests on" chain ending in one: past the scaffold limit, or above reach with every pillar or stair spot
    /// beside the void, live 10-28 design #4's "I can't climb"): the exact rectangle of the design's
    /// <paramref name="footprint"/> one below its bottom, counting the cells there still empty in <paramref name="world"/>. A
    /// suggestion only: the brain decides to lay it (and then resumes the build). Null otherwise.
    /// </summary>
    public static string? FloorHint(IReadOnlyList<(BlockPos Pos, string? Why)> items, Area? footprint, IBlockAccess world)
    {
        if (items.Count == 0 || footprint is not { } f) return null;
        var why = new Dictionary<BlockPos, string?>();
        foreach (var (p, w) in items) why.TryAdd(p, w);
        int hanging = 0;
        foreach (var (p, _) in items)
        {
            var seen = new HashSet<BlockPos>();
            for (BlockPos? q = p; q is { } c && seen.Add(c) && why.TryGetValue(c, out var w);)
            {
                var cat = Category(w);
                // Hanging: past the scaffold limit over the void, or no way up because every pillar/stair spot is beside it.
                if (cat == OverVoid || w?.Contains("the void", StringComparison.OrdinalIgnoreCase) == true) { hanging++; break; }
                if (cat != Waiting) break;
                q = RestsOn(w);
            }
        }
        if (hanging * 2 <= items.Count) return null;
        int y = f.Y0 - 1;
        if (y < 1) return null;
        int empty = 0;
        for (int x = f.X0; x <= f.X1; x++)
            for (int z = f.Z0; z <= f.Z1; z++)
                if (!BuildBlocks.Supports(world.GetBlock(x, y, z))) empty++;
        if (empty == 0) return null;
        return $"lay a floor: expand_island area={f.X0},{f.Z0},{f.X1},{f.Z1} y={y} ({empty} cells, {empty} cobble)";
    }

    /// <summary><paramref name="line"/> with the foundation suggestion appended ("...; lay a floor: ...").</summary>
    public static string? WithFloor(string? line, string? hint) => hint == null ? line : line == null ? hint : line + "; " + hint;

    /// <summary>The plan's own skips (no world at hand: the design status, the resume notice): its infeasible cells and skipped
    /// or pending-with-a-reason steps.</summary>
    public static string? OfPlan(BuildPlan bp, int max = 4) => PlanSkips(bp, max).Line;

    /// <summary><see cref="OfPlan"/> with how many cells it covers ("can't place 689: …").</summary>
    public static (int Count, string? Line) PlanSkips(BuildPlan bp, int max = 4)
    {
        List<(BlockPos, string?)> items;
        lock (bp)
            items = bp.Infeasible.Select(i => (i.Pos, (string?)i.Why))
                .Concat(bp.Steps.Where(s => s.Action is StepAction.Place && (s.Status == StepStatus.Skipped || !s.Finished && s.Note != null))
                    .Select(s => (s.Target, s.Note)))
                .ToList();
        return (items.Count, items.Count == 0 ? null : WithFloor(Line(items, max), bp.Foundation));
    }
}
