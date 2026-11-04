using System.Text.RegularExpressions;

namespace MineClaw.Scripting;

/// <summary>
/// Something that stopped a running script that a few lines of JS might fix in place (a patch turn instead of a full brain turn).
/// <paramref name="Kind"/>: danger | lowhp | void | died (a danger pause), stuck (the stall watchdog), failed (a <c>fail()</c>),
/// needs_input (<c>ask('brain', q)</c>), chat (a trusted player's line that tweaks the job).
/// </summary>
public sealed record ScriptInterrupt(string Kind, string Text, string? Player = null)
{
    public static readonly IReadOnlySet<string> DangerKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "danger", "lowhp", "void", "died" };

    public bool IsDanger => DangerKinds.Contains(Kind);

    /// <summary>An <c>onInterrupt(kind, fn)</c> pattern: "*", the exact kind, or "danger" for the whole danger family.</summary>
    public bool Matches(string pattern)
    {
        var p = (pattern ?? "").Trim();
        if (p.Length == 0) return false;
        if (p == "*" || p.Equals(Kind, StringComparison.OrdinalIgnoreCase)) return true;
        return p.Equals("danger", StringComparison.OrdinalIgnoreCase) && IsDanger;
    }

    public override string ToString() => $"{Kind}: {Text}" + (Player != null ? $" (from {Player})" : "");
}

/// <summary>What happens to the script after a patch ran.</summary>
public enum PatchThen
{
    /// <summary>Carry on where it stopped: the cut action re-runs; after a fail() the script continues on the next line.</summary>
    Resume,
    /// <summary>Re-enter the machine's current state, else restart from the last checkpoint (carry and patched params kept).</summary>
    RestartStep,
    /// <summary>End the script (a fail() then fails as it would have).</summary>
    Stop,
    /// <summary>Not fixable inline: the normal full brain turn.</summary>
    Escalate,
}

public static class PatchThens
{
    private static readonly Dictionary<string, PatchThen> Words = new(StringComparer.Ordinal)
    {
        [""] = PatchThen.Resume, ["resume"] = PatchThen.Resume, ["continue"] = PatchThen.Resume, ["ok"] = PatchThen.Resume,
        ["restart_step"] = PatchThen.RestartStep, ["restart"] = PatchThen.RestartStep, ["retry"] = PatchThen.RestartStep, ["redo"] = PatchThen.RestartStep,
        ["stop"] = PatchThen.Stop, ["cancel"] = PatchThen.Stop, ["end"] = PatchThen.Stop,
    };

    /// <summary>The word (any case, '-' as '_'); "escalate", "brain", "pass" and anything unknown escalate.</summary>
    public static PatchThen Parse(string? s) =>
        Words.TryGetValue((s ?? "").Trim().ToLowerInvariant().Replace('-', '_'), out var then) ? then : PatchThen.Escalate;

    public static string Tag(PatchThen t) => t switch
    {
        PatchThen.Resume => "resume",
        PatchThen.RestartStep => "restart_step",
        PatchThen.Stop => "stop",
        _ => "escalate",
    };
}

/// <summary>Where a patch came from, in the order they are tried.</summary>
public static class PatchSources
{
    /// <summary>The script's own <c>onInterrupt(kind, fn)</c>.</summary>
    public const string Declared = "declared";
    /// <summary>A saved handler that worked before (no LLM call).</summary>
    public const string Learned = "learned";
    /// <summary>A fresh patch turn (one small Opus call).</summary>
    public const string Llm = "patch";
}

/// <summary>A few lines of JS to run inside the paused script, and what to do after.</summary>
/// <param name="Key">The learned handler's key (null for a fresh patch); used to count its successes and failures.</param>
public sealed record ScriptPatch(string Js, PatchThen Then, string Source, string? Note = null, string? Key = null)
{
    public static ScriptPatch Escalate(string why, string source = PatchSources.Llm) => new("", PatchThen.Escalate, source, why);
}

/// <summary>How a patch run went: error (the line and why), actions it used, seconds, and the string it returned (ask's answer).</summary>
public sealed record PatchOutcome(bool Ok, PatchThen Then, string? Error, int Actions, double Seconds, string? Answer = null);

/// <summary>
/// One interrupt offered to the patcher: the session, the interrupt, its normalized situation signature (the learned handler's key
/// inside the script), the attempt number this run and the compact patch prompt (built on the script thread).
/// </summary>
public sealed record PatchContext(ScriptSession Session, ScriptInterrupt Interrupt, string Signature, int Attempt, string Prompt, DateTimeOffset At)
{
    public string Script => Session.Name;
}

/// <summary>
/// The host's side of inline interrupt fixing (Bot layer: learned handlers from the script library, then a patch turn on Opus 5.5).
/// The session asks in order: its declared <c>onInterrupt</c> → <see cref="PlanAsync"/> (learned handler, else the patch turn) →
/// escalate (the normal full brain turn). Safety reflexes never wait for any of it, and a patch never runs while one is engaged.
/// </summary>
public interface IInterruptPatcher
{
    /// <summary>Any thread, cheap: would this interrupt get a learned handler or a patch turn (the host then holds its brain wake)?</summary>
    bool Wants(ScriptSession session, ScriptInterrupt interrupt);
    /// <summary>A learned handler or a fresh patch; null or <see cref="PatchThen.Escalate"/> = the full brain turn.</summary>
    Task<ScriptPatch?> PlanAsync(PatchContext ctx, CancellationToken ct);
    /// <summary>A patch ran (declared ones included): telemetry. A failed run escalates right after.</summary>
    void Applied(PatchContext ctx, ScriptPatch patch, PatchOutcome outcome);
    /// <summary>Learning: after a resume/restart the script progressed without the same interrupt within the verify window (or not).</summary>
    void Verdict(PatchContext ctx, ScriptPatch patch, bool worked, string why);
    /// <summary>This interrupt goes to the full brain turn (why: the patch's reason, an error line, a budget hit...).</summary>
    void Escalated(PatchContext ctx, string why);
}

/// <summary>Normalized situation signatures: the same situation reads the same whatever the numbers, positions or players.</summary>
public static class PatchSignature
{
    private static readonly Regex Pos = new(@"-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex Num = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string? text, int max = 60, IEnumerable<string>? names = null)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (names != null)
            foreach (var n in names.Where(n => n.Length >= 3)) t = t.Replace(n.ToLowerInvariant(), "<p>", StringComparison.Ordinal);
        t = Pos.Replace(t, "<pos>");
        t = Num.Replace(t, "#");
        t = Ws.Replace(t, " ").Trim();
        return t.Length <= max ? t : t[..max];
    }

    /// <summary>"failed|deposit: all # known chests full|expect(store('chest'))".</summary>
    public static string Of(ScriptInterrupt i, string? sourceLine, IEnumerable<string>? names = null) =>
        $"{i.Kind.ToLowerInvariant()}|{Normalize(i.Text, 60, names)}|{Normalize(sourceLine, 40)}";
}

/// <summary>A patch stopped by its own budget (actions, seconds, statements), a stop, or a danger it doesn't own. Never visible to JS.</summary>
internal sealed class PatchAbortException(string message) : Exception(message);

/// <summary>restart_step without a running machine: the run starts over at its last checkpoint (Body catches it).</summary>
internal sealed class ScriptRestartException(string? label) : Exception("restart at " + (label ?? "the top"))
{
    public string? Label { get; } = label;
}
