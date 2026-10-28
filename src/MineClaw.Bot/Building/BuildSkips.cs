using System.Text.RegularExpressions;
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
    public const string AboveReach = "no stand: above my reach (I can't climb: no pillar or stair technique)";
    public const string NotNear = "no stand: I can't walk near it from here";
    public const string NoStand = "no stand within reach of a support face";
    public const string Waiting = "waiting on supports";
    public const string Protected = "protected";

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
        if (w.StartsWith("no support within", StringComparison.OrdinalIgnoreCase)) return "no support: over the void past my scaffold limit";
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
        return (items.Count, Line(items, max));
    }
}
