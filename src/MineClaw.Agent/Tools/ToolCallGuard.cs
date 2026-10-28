using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MineClaw.Agent.Tools;

/// <summary>
/// Zero-token guard in front of long-running tool calls (the bot's activity):
/// <list type="bullet">
/// <item><b>in-flight dedupe</b>: an identical call, or the same tool on the same target, while that activity is still
///   running returns <c>already running: chop_tree #1 (started 20s ago)</c> instead of restarting it (a <c>delegate</c>
///   whose task names the running step's coordinates overlaps too);</item>
/// <item><b>failure memory</b>: a (tool, target) that failed in the last few minutes returns the previous failure reason
///   instead of running again, unless the target/args materially differ or a player explicitly asked to try again.
///   <c>delegate</c> shares targets with the game tools ("chop the tree at 11,65,0" after chop_tree 11,65,0 failed).</item>
/// </list>
/// Stops/supersedes are not failures; a success on a target clears its memory.
/// </summary>
public sealed class ToolCallGuard
{
    private sealed record Failure(string Tool, IReadOnlySet<string> Targets, string Signature, string Reason, DateTimeOffset At)
    {
        /// <summary>A delegate's task words (coordinates and boilerplate removed): its intent.</summary>
        public IReadOnlySet<string>? Task { get; init; }
        /// <summary>The body it failed on (null = the main body): one body's dead end never blocks another's try.</summary>
        public string? Body { get; init; }
    }

    /// <summary>Two delegated tasks are the same job at this content-word Jaccard (or more).</summary>
    public const double SameTaskThreshold = 0.7;

    private static readonly HashSet<string> Boilerplate = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "from", "with", "into", "onto", "then", "that", "this", "your", "you", "near", "next", "chest", "house",
        "stash", "storage", "food", "eat", "pickaxe", "tool", "tools", "deposit", "inventory", "when", "done", "until", "all", "any",
        "some", "use", "get", "put", "back", "at", "to", "of", "in", "on", "it", "if", "or", "a", "an", "is", "be", "by", "keep",
    };

    /// <summary>Content words of a delegate's task/done_when (lower case, coordinates and boilerplate dropped).</summary>
    public static IReadOnlySet<string> TaskWords(JsonElement input)
    {
        var text = (input.Str("task") ?? "") + " " + (input.Str("done_when") ?? "");
        text = Coords.Replace(text.ToLowerInvariant(), " ");
        return Regex.Matches(text, "[a-z_]{3,}").Select(m => m.Value).Where(w => !Boilerplate.Contains(w)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameTask(JsonElement a, JsonElement b) => Jaccard(TaskWords(a), TaskWords(b)) >= SameTaskThreshold;

    private static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = a.Count(b.Contains);
        return inter / (double)(a.Count + b.Count - inter);
    }

    /// <summary>
    /// Forgets every remembered failure (the world changed: a player said something new, e.g. "opened the door for you").
    /// The guard only stops the brain from repeating a dead end on its own, never from trying what a player asks next.
    /// </summary>
    public int ForgetAll()
    {
        lock (_lock)
        {
            int n = _failures.Count;
            _failures.Clear();
            return n;
        }
    }

    public const string DelegateTool = "delegate";
    private static readonly Regex Coords = new(@"(-?\d{1,7})(?:\.\d+)?\s*[, ]\s*(-?\d{1,3})(?:\.\d+)?\s*[, ]\s*(-?\d{1,7})(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex RetryWords = new(@"\b(?:try (?:it |that |again|one more)|try again|retry|do it again|one more (?:time|try)|again pls|again please)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ActivityManager _activities;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly List<Failure> _failures = new();

    public ToolCallGuard(ActivityManager activities, TimeProvider? time = null, TimeSpan? memory = null)
    {
        _activities = activities;
        _time = time ?? TimeProvider.System;
        Memory = memory ?? TimeSpan.FromMinutes(10);
        _activities.StepFinished += OnStepFinished;
    }

    /// <summary>How long a failure is remembered.</summary>
    public TimeSpan Memory { get; set; }

    /// <summary>
    /// One brain, many bodies: also remembers the failures of another body's activity slot, keyed by <paramref name="body"/>
    /// (a clone's name). <see cref="Check"/> with that body sees only its own failures.
    /// </summary>
    public void Watch(ActivityManager other, string body) =>
        other.StepFinished += (tool, input, r, cancel) => OnStepFinished(tool, input, r, cancel, body);

    /// <summary>True if a player's line explicitly asks to try again ("try again", "retry", "one more time").</summary>
    public static bool IsRetryRequest(string? text) => text != null && RetryWords.IsMatch(text);

    /// <summary>
    /// Null = go ahead. Otherwise the result to return instead of running: (ok=true, "already running ...") or
    /// (ok=false, "not retried: ...").
    /// </summary>
    /// <param name="retryRequested">A player said "try again" this turn: failure memory is bypassed.</param>
    /// <param name="stopping">The same model turn also calls stop: the running activity is going away, no dedupe.</param>
    public ToolResult? Check(string tool, JsonElement input, bool retryRequested = false, bool stopping = false, string? body = null,
        ActivityManager? activities = null)
    {
        var sig = Signature(tool, input);
        var targets = Targets(tool, input);
        var now = _time.GetUtcNow();

        // In flight on the body asked about (another body's identical job is a second job, never "already running").
        if (!stopping && (activities ?? _activities).Current is { } run)
        {
            foreach (var (t, i) in run.Steps)
            {
                // Same call, same tool on the same target, or the same delegated job. A delegate never shadows a game
                // tool (or the reverse) just because both mention the house chest's coordinates.
                bool same = Signature(t, i) == sig
                    || (string.Equals(t, tool, StringComparison.OrdinalIgnoreCase) && tool != DelegateTool && Overlap(Targets(t, i), targets))
                    || (tool == DelegateTool && t == DelegateTool && SameTask(i, input));
                if (same)
                    return ToolResult.Success($"already running: {run.Label} #{run.Id} (started {(int)(now - run.Started).TotalSeconds}s ago); " +
                        "wait for its [task] event, or stop it first");
            }
        }

        if (retryRequested) return null;
        // Movement is never refused from memory: a door opens, a bridge gets built, the player moves; just try.
        if (ActivityManager.MovementTools.Contains(tool)) return null;
        lock (_lock)
        {
            _failures.RemoveAll(f => now - f.At > Memory);
            for (int k = _failures.Count - 1; k >= 0; k--)
            {
                var f = _failures[k];
                // Keyed on (tool, intent): the same game tool on the same target, or the same delegated job (task words,
                // coordinates and boilerplate ignored). Never across tools.
                if (!string.Equals(f.Tool, tool, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(f.Body, body, StringComparison.OrdinalIgnoreCase)) continue; // another body's dead end
                bool sameTarget = tool == DelegateTool
                    ? f.Signature == sig || (f.Task != null && Jaccard(f.Task, TaskWords(input)) >= SameTaskThreshold)
                    : targets.Count > 0 || f.Targets.Count > 0 ? Overlap(f.Targets, targets) : f.Signature == sig;
                if (!sameTarget) continue;
                return ToolResult.Error($"not retried: {f.Tool} failed here {Ago(now - f.At)} ago: {f.Reason}. " +
                    "Try something else or tell the player; it reruns if they say try again");
            }
        }
        return null;
    }

    private static readonly Regex CombatBusy = new(@"(?:busy|interrupted): fighting", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The body was briefly held by a combat reflex ("busy: fighting skeleton 11.4m ...", "interrupted: fighting ..."): says
    /// nothing about the job itself, so it is never remembered or counted as the job failing (live 10-28: "not retried:
    /// withdraw failed here 13s ago: busy: fighting spider" kept a deposit from running after the fight ended).
    /// </summary>
    public static bool IsTransientCombat(string? reason) => reason != null && CombatBusy.IsMatch(reason);

    /// <summary>Records a failure (normally fed by <see cref="ActivityManager.StepFinished"/>).</summary>
    public void RecordFailure(string tool, JsonElement input, string reason, string? body = null)
    {
        // A question back to the brain is not a failure, and a walk is never remembered as impossible.
        if (reason.Contains("needs_input", StringComparison.OrdinalIgnoreCase) || ActivityManager.MovementTools.Contains(tool)) return;
        if (IsTransientCombat(reason)) return;
        var f = new Failure(tool, Targets(tool, input), Signature(tool, input), TextUtil.Truncate(TextUtil.OneLine(reason), 160), _time.GetUtcNow())
        {
            Task = tool == DelegateTool ? TaskWords(input) : null,
            Body = body,
        };
        lock (_lock)
        {
            _failures.RemoveAll(o => o.Signature == f.Signature && string.Equals(o.Body, body, StringComparison.OrdinalIgnoreCase));
            _failures.Add(f);
            if (_failures.Count > 50) _failures.RemoveAt(0);
        }
    }

    public void Forget(string tool, JsonElement input, string? body = null)
    {
        var targets = Targets(tool, input);
        var sig = Signature(tool, input);
        lock (_lock) _failures.RemoveAll(f => string.Equals(f.Body, body, StringComparison.OrdinalIgnoreCase)
            && (f.Signature == sig || (targets.Count > 0 && Overlap(f.Targets, targets))));
    }

    private void OnStepFinished(string tool, JsonElement input, ToolResult r, string? cancelReason) => OnStepFinished(tool, input, r, cancelReason, null);

    private void OnStepFinished(string tool, JsonElement input, ToolResult r, string? cancelReason, string? body)
    {
        if (r.Ok) { Forget(tool, input, body); return; }
        if (cancelReason != null) return; // stopped / superseded / interrupted: not the tool's fault
        var text = r.Text;
        if (text.StartsWith("cancelled", StringComparison.OrdinalIgnoreCase) || text.StartsWith("skipped", StringComparison.OrdinalIgnoreCase)) return;
        RecordFailure(tool, input, text, body);
    }

    // ---------------------------------------------------------------- keys

    /// <summary>"tool:{a=1,b=x}" with keys sorted and whitespace/case normalised.</summary>
    public static string Signature(string tool, JsonElement input)
    {
        var sb = new StringBuilder(tool.ToLowerInvariant()).Append(':');
        Append(sb, input);
        return sb.ToString();

        static void Append(StringBuilder sb, JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    sb.Append('{');
                    foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                        sb.Append(p.Name.ToLowerInvariant()).Append('=');
                        Append(sb, p.Value);
                        sb.Append(';');
                    }
                    sb.Append('}');
                    break;
                case JsonValueKind.Array:
                    sb.Append('[');
                    foreach (var x in e.EnumerateArray()) { Append(sb, x); sb.Append(','); }
                    sb.Append(']');
                    break;
                case JsonValueKind.String:
                    sb.Append(Regex.Replace(e.GetString()!.Trim().ToLowerInvariant(), @"\s+", " "));
                    break;
                case JsonValueKind.Undefined:
                    break;
                default:
                    sb.Append(e.GetRawText());
                    break;
            }
        }
    }

    /// <summary>
    /// What a call acts on: block coordinates found in its args ("x,y,z" strings, x/y/z numbers, or coords in a delegate's
    /// task text), else its string args (player, item, waypoint names). Empty = no target (e.g. "nearest tree").
    /// </summary>
    public static IReadOnlySet<string> Targets(string tool, JsonElement input)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (input.ValueKind != JsonValueKind.Object) return set;
        int? x = input.Int("x"), y = input.Int("y"), z = input.Int("z");
        if (x != null && y != null && z != null) set.Add($"{x},{y},{z}");
        var strings = new List<string>();
        foreach (var p in input.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            var s = p.Value.GetString() ?? "";
            foreach (Match m in Coords.Matches(s)) set.Add($"{m.Groups[1].Value},{m.Groups[2].Value},{m.Groups[3].Value}");
            // free text (delegate task/done_when) only contributes coordinates; short args are names
            if (tool != DelegateTool && p.Name is not ("task" or "done_when" or "note" or "why" or "reason") && s.Length is > 0 and <= 40 && !Coords.IsMatch(s))
                strings.Add(s.Trim().ToLowerInvariant());
        }
        if (set.Count == 0) foreach (var s in strings) set.Add("n:" + s);
        return set;
    }

    private static bool Overlap(IReadOnlySet<string> a, IReadOnlySet<string> b) => a.Count > 0 && b.Count > 0 && a.Overlaps(b);

    private static string Ago(TimeSpan t) => t.TotalSeconds < 90 ? $"{(int)t.TotalSeconds}s" : $"{(int)t.TotalMinutes}m";
}
