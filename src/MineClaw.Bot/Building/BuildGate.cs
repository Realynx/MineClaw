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

    /// <summary>
    /// Reads an answer at the gate. Turns and mirrors pivot on <paramref name="anchor"/> (the first gate block); a mirror flips
    /// across <paramref name="axis"/> (the build's main axis). Null = not an answer to the gate.
    /// </summary>
    public static GateReply? Parse(string text, string by, BlockPos anchor, char axis)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        if (UndoRx().IsMatch(t)) return new GateReply(GateVerdict.Undo, by, t);
        int quarters = 0, dx = 0, dy = 0, dz = 0;
        bool mirror = false, any = false;
        if (AroundRx().IsMatch(t)) { quarters += 2; any = true; }
        else if (TurnRx().Match(t) is { Success: true } tm)
        {
            var d = tm.Groups["dir"].Value.ToLowerInvariant();
            quarters += d.StartsWith("left", StringComparison.Ordinal) || d.StartsWith("counter", StringComparison.Ordinal) || d.StartsWith("anti", StringComparison.Ordinal) ? 3 : 1;
            any = true;
        }
        if (MirrorRx().IsMatch(t)) { mirror = true; any = true; }
        foreach (Match m in MoveRx().Matches(t))
        {
            int n = m.Groups["n"].Value.ToLowerInvariant() switch
            {
                "" or "a" or "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, var v => int.TryParse(v, out var k) ? k : 1,
            };
            switch (m.Groups["dir"].Value.ToLowerInvariant())
            {
                case "north": dz -= n; break;
                case "south": dz += n; break;
                case "east": dx += n; break;
                case "west": dx -= n; break;
                case "up": dy += n; break;
                case "down": dy -= n; break;
                default: continue; // left/right without a frame: ask instead
            }
            any = true;
        }
        if (any) return new GateReply(GateVerdict.Correct, by, t, new BuildTransform(quarters % 4, mirror, dx, dy, dz, anchor, axis));
        if (ApproveRx().IsMatch(t)) return new GateReply(GateVerdict.Approve, by, t);
        if (HoldRx().IsMatch(t)) return new GateReply(GateVerdict.Hold, by, t);
        return null;
    }

    /// <summary>
    /// "First 5 blocks down: the hut starts just east of the main house and runs east, away from the main house toward the
    /// cactus farm, 8×3, ending at the cactus farm. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' /
    /// 'move 2 east'; preview /view/buildplan/12)". Landmarks and relative terms, compass words only as a fallback.
    /// </summary>
    public static string Question(BuildPlan bp, VoxelPlan plan, string name, Func<BlockPos, string?> landmark, Func<BlockPos, NamedPlace?>? place,
        string? previewUrl)
    {
        var gate = bp.Steps.Where(s => s.Gate && s.Finished).Select(s => s.Target).ToList();
        if (gate.Count == 0) gate = bp.Steps.Where(s => s.Action == StepAction.Place && s.Finished).Take(5).Select(s => s.Target).ToList();
        var anchor = gate.Count > 0 ? gate[0] : plan.Min;
        bool alongX = plan.Max.X - plan.Min.X >= plan.Max.Z - plan.Min.Z;
        var far = alongX ? (anchor.X <= (plan.Min.X + plan.Max.X) / 2 ? plan.Max.X : plan.Min.X) : (anchor.Z <= (plan.Min.Z + plan.Max.Z) / 2 ? plan.Max.Z : plan.Min.Z);
        var end = alongX ? anchor with { X = far } : anchor with { Z = far };
        int dx = Math.Sign(end.X - anchor.X), dz = Math.Sign(end.Z - anchor.Z);
        string runs = dx > 0 ? "east" : dx < 0 ? "west" : dz > 0 ? "south" : dz < 0 ? "north" : "in place";
        var bits = new List<string>();
        var start = landmark(anchor);
        bits.Add($"First {gate.Count} block{(gate.Count == 1 ? "" : "s")} down: the {name} starts {start ?? "here"}");
        var rel = new List<string>();
        if (place?.Invoke(anchor) is { } from)
        {
            int ox = anchor.X - from.At.X, oz = anchor.Z - from.At.Z;
            rel.Add(ox * dx + oz * dz > 0 ? $"away from {from.Name}" : $"toward {from.Name}");
            if (place(end) is { } to && to.Name != from.Name) rel.Add($"toward {to.Name}");
        }
        else if (place?.Invoke(end) is { } to) rel.Add($"toward {to.Name}");
        bits.Add($"runs {runs}" + (rel.Count > 0 ? ", " + string.Join(" ", rel) : ""));
        var (fx, fz) = plan.Blueprint.Footprint;
        bits.Add($"{fx}×{fz}" + (plan.Blueprint.H > 1 ? $"×{plan.Blueprint.H}" : ""));
        var endText = landmark(end);
        if (endText != null && endText != start) bits.Add($"ending {endText}");
        if (!bp.Transform.IsNone) bits.Add($"({bp.Transform.Text})");
        return string.Join(", ", bits) + ". Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'"
               + (previewUrl != null ? $"; preview {previewUrl}" : "") + ")";
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
