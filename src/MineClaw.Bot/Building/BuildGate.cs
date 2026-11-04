using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MineClaw.Bot.Blueprints;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>A player's answer at the confirmation gate.</summary>
public sealed record GateReply(GateVerdict Verdict, string By, string Text, BuildTransform? Correction = null);

public enum GateVerdict { Approve, Correct, Undo, Hold }

/// <summary>A named place near the build (memory waypoint or structure): for relative wording, never coordinates.</summary>
public readonly record struct NamedPlace(string Name, BlockPos At);

/// <summary>
/// The confirmation gate (user decision, 10-28: a clone built a bridge the right size but the wrong way, sure it was right).
/// No preview blocks or scaffolding: the plan's first 4–5 real placements show the start edge and the main axis, then the
/// build pauses and asks once, in landmark terms. The answer has NO default: a timeout leaves it paused. Only a trusted
/// player's yes / "keep going" continues; "wrong way", "turn it", "flip it", "move 2 east" re-plan (the gate blocks come
/// down, the plan is transformed, the gate repeats); "undo" takes the whole build down.
/// </summary>
public static partial class BuildGate
{
    [GeneratedRegex(@"^\W*(?:yes|yeah|yep|yup|ok(?:ay)?|sure|correct|right|perfect|good|looks? (?:good|right|fine|great)|that'?s (?:right|it|good|perfect)|keep going|carry on|go on|go ahead|continue|proceed)\b(?:[\s,!.]*(?:keep going|carry on|go on|go ahead|continue|that'?s (?:right|it)|please|pls|thanks?))*\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex ApproveRx();

    [GeneratedRegex(@"\b(?:wrong (?:way|direction)|other (?:way|direction)|backwards?|reverse(?:d| it)?|opposite (?:way|direction)|turn it around|180)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AroundRx();

    [GeneratedRegex(@"\b(?:turn|rotate)(?: it)?(?: (?<dir>left|right|counter-?clockwise|anti-?clockwise|clockwise))?\b|\b(?<dir>counter-?clockwise|anti-?clockwise|clockwise)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TurnRx();

    [GeneratedRegex(@"\b(?:flip|mirror)(?:ped|red)?(?: it)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex MirrorRx();

    [GeneratedRegex(@"\b(?:move|shift|put|push)(?: it| the \w+)?(?: over)? (?<n>\d+|a|one|two|three|four|five)?(?: blocks?)? ?(?:to the |further |more )?(?<dir>north|south|east|west|up|down|left|right)\b|\b(?<n>\d+)(?: blocks?)? (?:further |more )?(?<dir>north|south|east|west|up|down)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MoveRx();

    [GeneratedRegex(@"^\W*(?:undo(?: (?:it|that|the build|everything))?|take it (?:all )?down|remove (?:it|the build)|tear it down)\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex UndoRx();

    [GeneratedRegex(@"^\W*(?:no|nope|stop|wait|hold on|not (?:there|like that)|wrong(?: spot| place)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HoldRx();

    public static bool IsUndo(string text) => UndoRx().IsMatch(text.Trim());

    /// <summary>After a gate "no": the exact blocks standing and the way to take them down, "gate blocks at 2,64,3 3,64,3 …; undo
    /// with design undo{id=9}" (live 10-28: a worker asked to "remove the 5 blocks just placed" couldn't find them). Null when
    /// the build placed nothing.</summary>
    public static string? Placed(BuildPlan bp, long blueprintId)
    {
        var cells = bp.Gate.Blocks.Concat(bp.Placed).Distinct().ToList();
        if (cells.Count == 0) return null;
        var gate = bp.Gate.Blocks.Count > 0 ? bp.Gate.Blocks : cells;
        var list = string.Join(" ", gate.Take(8).Select(p => $"{p.X},{p.Y},{p.Z}")) + (gate.Count > 8 ? $" +{gate.Count - 8}" : "");
        var more = cells.Count > gate.Count ? $" ({cells.Count} placed in all)" : "";
        return $"{(bp.Gate.Blocks.Count > 0 ? "gate blocks" : "placed")} at {list}{more}; undo with design undo{{id={blueprintId}}}";
    }

    /// <summary>
    /// Reads an answer at the gate. Turns and mirrors pivot on <paramref name="anchor"/> (the first gate block); a mirror flips
    /// across <paramref name="axis"/> (the build's main axis). Null = not an answer to the gate.
    /// </summary>
    public static GateReply? Parse(string text, string by, BlockPos anchor, char axis)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        if (UndoRx().IsMatch(t)) return new GateReply(GateVerdict.Undo, by, t);
        var (quarters, mirror, turned) = Turns(t);
        var (dx, dy, dz, moved) = Moves(t);
        if (turned || moved) return new GateReply(GateVerdict.Correct, by, t, new BuildTransform(quarters % 4, mirror, dx, dy, dz, anchor, axis));
        if (ApproveRx().IsMatch(t)) return new GateReply(GateVerdict.Approve, by, t);
        if (HoldRx().IsMatch(t)) return new GateReply(GateVerdict.Hold, by, t);
        return null;
    }

    /// <summary>"wrong way" (half a turn), "turn it left" (three quarters) / "turn it" (one), "flip it" (a mirror).</summary>
    private static (int Quarters, bool Mirror, bool Any) Turns(string t)
    {
        int quarters = 0;
        bool any = false;
        if (AroundRx().IsMatch(t)) { quarters = 2; any = true; }
        else if (TurnRx().Match(t) is { Success: true } tm) { quarters = Quarters(tm.Groups["dir"].Value.ToLowerInvariant()); any = true; }
        bool mirror = MirrorRx().IsMatch(t);
        return (quarters, mirror, any || mirror);
    }

    private static int Quarters(string dir) =>
        dir.StartsWith("left", StringComparison.Ordinal) || dir.StartsWith("counter", StringComparison.Ordinal) || dir.StartsWith("anti", StringComparison.Ordinal) ? 3 : 1;

    /// <summary>Every "move 2 east" / "3 blocks further north" summed; left/right without a frame don't count (ask instead).</summary>
    private static (int Dx, int Dy, int Dz, bool Any) Moves(string t)
    {
        int dx = 0, dy = 0, dz = 0;
        bool any = false;
        foreach (Match m in MoveRx().Matches(t))
        {
            if (Direction(m.Groups["dir"].Value.ToLowerInvariant()) is not { } d) continue;
            int n = Count(m.Groups["n"].Value.ToLowerInvariant());
            (dx, dy, dz) = (dx + d.X * n, dy + d.Y * n, dz + d.Z * n);
            any = true;
        }
        return (dx, dy, dz, any);
    }

    private static int Count(string n) => n switch
    {
        "" or "a" or "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, var v => int.TryParse(v, out var k) ? k : 1,
    };

    private static (int X, int Y, int Z)? Direction(string dir) => dir switch
    {
        "north" => (0, 0, -1),
        "south" => (0, 0, 1),
        "east" => (1, 0, 0),
        "west" => (-1, 0, 0),
        "up" => (0, 1, 0),
        "down" => (0, -1, 0),
        _ => null,
    };

    /// <summary>
    /// "First 5 blocks down: the hut starts just east of the main house and runs east, away from the main house toward the
    /// cactus farm, 8×3, ending at the cactus farm. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' /
    /// 'move 2 east'; preview /view/buildplan/12)". Landmarks and relative terms, compass words only as a fallback.
    /// </summary>
    public static string Question(BuildPlan bp, VoxelPlan plan, string name, Func<BlockPos, string?> landmark, Func<BlockPos, NamedPlace?>? place,
        string? previewUrl)
    {
        // Never describe the build relative to itself ("away from mob drop tower" for the mob drop tower).
        landmark = NotSelf(landmark, name);
        place = NotSelf(place, name, plan);
        var gate = GateCells(bp);
        var anchor = gate.Count > 0 ? gate[0] : plan.Min;
        var end = FarEnd(plan, anchor);
        int dx = Math.Sign(end.X - anchor.X), dz = Math.Sign(end.Z - anchor.Z);
        var bits = new List<string>();
        var start = landmark(anchor);
        bits.Add($"First {gate.Count} block{(gate.Count == 1 ? "" : "s")} down: the {name} starts {start ?? "here"}");
        bits.Add($"runs {Runs(dx, dz)}" + Relative(place, anchor, end, dx, dz));
        var (fx, fz) = plan.Blueprint.Footprint;
        bits.Add($"{fx}×{fz}" + (plan.Blueprint.H > 1 ? $"×{plan.Blueprint.H}" : ""));
        var endText = landmark(end);
        if (endText != null && endText != start) bits.Add($"ending {endText}");
        if (!bp.Transform.IsNone) bits.Add($"({bp.Transform.Text})");
        return string.Join(", ", bits) + ". Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'"
               + (previewUrl != null ? $"; preview {previewUrl}" : "") + ")";
    }

    private static bool Self(string text, string name) =>
        text.Contains(name, StringComparison.OrdinalIgnoreCase) || text.Contains(name.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase);

    private static Func<BlockPos, string?> NotSelf(Func<BlockPos, string?> landmark, string name) =>
        p => landmark(p) is { } t && !Self(t, name) ? t : null;

    /// <summary>A named place, never the build itself nor one inside its footprint.</summary>
    private static Func<BlockPos, NamedPlace?>? NotSelf(Func<BlockPos, NamedPlace?>? place, string name, VoxelPlan plan)
    {
        if (place == null) return null;
        bool InFootprint(BlockPos p) => p.X >= plan.Min.X && p.X <= plan.Max.X && p.Z >= plan.Min.Z && p.Z <= plan.Max.Z;
        return p => place(p) is { } np && !Self(np.Name, name) && !InFootprint(np.At) ? np : null;
    }

    /// <summary>The gate blocks: as recorded (placement order keeps the wording stable across restarts; step states can re-verify
    /// differently), else the finished gate steps, else the first five finished placements.</summary>
    private static List<BlockPos> GateCells(BuildPlan bp)
    {
        var gate = bp.Gate.Blocks.Count > 0 ? bp.Gate.Blocks.Distinct().ToList() : bp.Steps.Where(s => s.Gate && s.Finished).Select(s => s.Target).ToList();
        return gate.Count > 0 ? gate : bp.Steps.Where(s => s.Action == StepAction.Place && s.Finished).Take(5).Select(s => s.Target).ToList();
    }

    /// <summary>The far end of the build's long axis from <paramref name="anchor"/>.</summary>
    private static BlockPos FarEnd(VoxelPlan plan, BlockPos anchor)
    {
        bool alongX = plan.Max.X - plan.Min.X >= plan.Max.Z - plan.Min.Z;
        if (alongX) return anchor with { X = anchor.X <= (plan.Min.X + plan.Max.X) / 2 ? plan.Max.X : plan.Min.X };
        return anchor with { Z = anchor.Z <= (plan.Min.Z + plan.Max.Z) / 2 ? plan.Max.Z : plan.Min.Z };
    }

    private static string Runs(int dx, int dz) => (dx, dz) switch
    {
        ( > 0, _) => "east",
        ( < 0, _) => "west",
        (_, > 0) => "south",
        (_, < 0) => "north",
        _ => "in place",
    };

    /// <summary>", away from the well toward the farm": where it starts and ends relative to named places.</summary>
    private static string Relative(Func<BlockPos, NamedPlace?>? place, BlockPos anchor, BlockPos end, int dx, int dz)
    {
        var rel = new List<string>();
        if (place?.Invoke(anchor) is { } from)
        {
            int ox = anchor.X - from.At.X, oz = anchor.Z - from.At.Z;
            rel.Add(ox * dx + oz * dz > 0 ? $"away from {from.Name}" : $"toward {from.Name}");
            if (place(end) is { } to && to.Name != from.Name) rel.Add($"toward {to.Name}");
        }
        else if (place?.Invoke(end) is { } to) rel.Add($"toward {to.Name}");
        return rel.Count > 0 ? ", " + string.Join(" ", rel) : "";
    }
}

/// <summary>
/// Waiting builds at their gate (per blueprint): the running executor waits here; a chat answer is delivered here, or, when
/// no executor waits (after a restart), stored in the plan by the service, which starts the build again.
/// </summary>
public sealed class BuildGates
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<GateReply>> _waiting = new();

    public IReadOnlyCollection<long> Waiting => _waiting.Keys.ToList();

    public bool IsWaiting(long blueprintId) => _waiting.ContainsKey(blueprintId);

    /// <summary>Waits for an answer; null on timeout (the build stays paused: no default).</summary>
    public async Task<GateReply?> WaitAsync(long blueprintId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = _waiting.GetOrAdd(blueprintId, _ => new TaskCompletionSource<GateReply>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return done == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally { _waiting.TryRemove(new KeyValuePair<long, TaskCompletionSource<GateReply>>(blueprintId, tcs)); }
    }

    /// <summary>Delivers an answer to a waiting executor; false = nobody waits right now.</summary>
    public bool Answer(long blueprintId, GateReply reply) => _waiting.TryGetValue(blueprintId, out var t) && t.TrySetResult(reply);
}
