using System.Text.RegularExpressions;
using MineClaw.Agent.Authoring;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Telemetry;
using MineClaw.Scripting;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// The bot's <see cref="IInterruptPatcher"/>: a learned handler from the script library first (deterministic, no LLM call, any tier
/// may run it), else one small Opus 5.5 patch turn (<see cref="PatchAgent"/>; refused off-Opus, so other tiers only run saved
/// handlers). A patch that the session verified as working is saved as a handler of its script, keyed by (interrupt kind,
/// normalized situation signature); a handler that fails <see cref="ScriptOptions.HandlerDemoteAfter"/> times in a row is demoted
/// and the patch turn runs again. Every interrupt is measured (<see cref="InterruptTracker"/>: path, latency, calls, tokens, $).
/// </summary>
public sealed class ScriptPatcher : IInterruptPatcher
{
    private readonly ScriptLibrary _library;
    private readonly ScriptOptions _o;
    private readonly InterruptTracker _tracker;
    private readonly ILogger? _log;
    private readonly Lazy<string> _system;

    public ScriptPatcher(ScriptLibrary library, ScriptOptions options, InterruptTracker tracker, Func<string> system, ILogger? log = null)
    {
        _library = library;
        _o = options;
        _tracker = tracker;
        _log = log;
        _system = new Lazy<string>(system);
    }

    /// <summary>The patch turn (null = learned handlers only).</summary>
    public PatchAgent? Agent { get; set; }
    /// <summary>The body tag for telemetry (null = main).</summary>
    public string? Body { get; set; }
    /// <summary>An escalated stall/chat interrupt: the host re-posts the wake it held.</summary>
    public Action<ScriptSession, ScriptInterrupt>? OnEscalated { get; set; }

    /// <summary>The cached system part of every patch call (rules + cheat-sheet + this bot's game functions).</summary>
    public string System => _system.Value;

    public static string Key(ScriptSession s) => "s" + s.Id + "@" + (s.LaneBody ?? "main");

    public bool Wants(ScriptSession s, ScriptInterrupt i)
    {
        if (!_o.Patches) return false;
        // Someone else's danger (a warning about another player) needs the brain's voice, not a patch.
        if (i.IsDanger && i.Player != null) return false;
        if (HasLearned(s.Name, i.Kind)) return true;
        // A fail() is worth a patch only when its cause is the kind a few lines fix (else the brain hears it as before, at no cost).
        if (i.Kind == "failed" && !FixableFailure.IsMatch(i.Text)) return false;
        return Agent is { } a && a.Refusal() == null;
    }

    /// <summary>Failures a few lines can fix: storage full, a missing item or tool, a blocked/unreachable spot, a mob or player in the way, food.</summary>
    public static readonly Regex FixableFailure = new(
        @"\b(full|no room|missing|not enough|need(s|ed)?|no (pick|axe|shovel|tool|chest|food|route|path)|broke|broken|blocked|can'?t reach|cannot reach|unreachable|" +
        @"occupied|standing|in the way|hostile|mob|zombie|skeleton|spider|creeper|hungry|starving|food)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private bool HasLearned(string script, string kind)
    {
        try { return _library.Get(script)?.Script?.Handlers.Any(h => !h.Demoted && h.Kind == kind) == true; }
        catch (Exception) { return false; }
    }

    public async Task<ScriptPatch?> PlanAsync(PatchContext ctx, CancellationToken ct)
    {
        var key = Key(ctx.Session);
        _tracker.Open(key, ctx.Script, ctx.Interrupt.Kind, PatchSources.Llm, Body, ctx.Signature, ctx.At);
        ScriptHandler? h = null;
        try { h = _library.FindHandler(ctx.Script, ctx.Interrupt.Kind, ctx.Signature); } catch (Exception ex) { _log?.LogWarning(ex, "handler lookup failed"); }
        if (h != null)
        {
            _tracker.Relabel(key, PatchSources.Learned);
            _log?.LogInformation("script {Name}: learned handler for {Kind} ({Ok}/{Fail}): {Note}", ctx.Script, h.Kind, h.Successes, h.Failures, h.Note ?? h.Signature);
            return new ScriptPatch(h.Js, PatchThens.Parse(h.Then), PatchSources.Learned, h.Note, h.Key);
        }
        if (Agent is not { } agent) return null;
        if (agent.Refusal() is { } no) return ScriptPatch.Escalate(no);
        PatchReply reply;
        try { reply = await agent.RunAsync(System, ctx.Prompt, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _log?.LogWarning("patch turn for {Name} failed: {Error}", ctx.Script, ex.Message);
            return ScriptPatch.Escalate("patch turn failed: " + ex.Message);
        }
        if (reply.Escalates) return ScriptPatch.Escalate(reply.Blocker ?? reply.Note ?? "escalate");
        return new ScriptPatch(reply.Js ?? "", PatchThens.Parse(reply.Then), PatchSources.Llm, reply.Note);
    }

    public void Applied(PatchContext ctx, ScriptPatch patch, PatchOutcome outcome)
    {
        var key = Key(ctx.Session);
        if (!outcome.Ok)
        {
            _log?.LogInformation("script {Name}: {Source} patch for {Kind} failed: {Error}", ctx.Script, patch.Source, ctx.Interrupt.Kind, outcome.Error);
            if (patch.Source == PatchSources.Learned) Record(ctx, false);
            return;
        }
        if (outcome.Then == PatchThen.Escalate) return;
        if (!_tracker.IsOpen(key)) _tracker.Open(key, ctx.Script, ctx.Interrupt.Kind, patch.Source, Body, ctx.Signature, ctx.At);
        _tracker.Relabel(key, patch.Source);
        _tracker.Close(key, PatchThens.Tag(outcome.Then), true, Clip(patch.Note));
    }

    public void Verdict(PatchContext ctx, ScriptPatch patch, bool worked, string why)
    {
        _log?.LogInformation("script {Name}: {Source} patch for {Kind} {Verdict} ({Why})", ctx.Script, patch.Source, ctx.Interrupt.Kind,
            worked ? "worked" : "didn't work", why);
        switch (patch.Source)
        {
            case PatchSources.Learned: Record(ctx, worked); break;
            case PatchSources.Llm when worked && patch.Js.Trim().Length > 0 && patch.Then != PatchThen.Stop:
                try
                {
                    var h = _library.LearnHandler(ctx.Script, ctx.Interrupt.Kind, ctx.Signature, patch.Js.Trim(), PatchThens.Tag(patch.Then), patch.Note,
                        "patch/" + (Agent?.Model ?? "opus"), ctx.Session.Request.Version);
                    if (h != null) _log?.LogInformation("script {Name}: learned a handler for {Kind}: {Sig}", ctx.Script, h.Kind, h.Signature);
                }
                catch (Exception ex) { _log?.LogWarning(ex, "saving the handler failed"); }
                break;
        }
    }

    private void Record(PatchContext ctx, bool ok)
    {
        try
        {
            var h = _library.RecordHandler(ctx.Script, ctx.Interrupt.Kind, ctx.Signature, ok, _o.HandlerDemoteAfter);
            if (h is { Demoted: true } && !ok) _log?.LogInformation("script {Name}: handler for {Kind} demoted after {N} failures", ctx.Script, h.Kind, h.FailStreak);
        }
        catch (Exception ex) { _log?.LogWarning(ex, "handler stats failed"); }
    }

    public void Escalated(PatchContext ctx, string why)
    {
        var key = Key(ctx.Session);
        var path = _tracker.PathOf(key) is PatchSources.Llm or PatchSources.Learned ? "escalated" : "full";
        if (!_tracker.IsOpen(key)) _tracker.Open(key, ctx.Script, ctx.Interrupt.Kind, path, Body, ctx.Signature, ctx.At);
        else _tracker.Relabel(key, path, Clip(why));
        if (ctx.Interrupt.Kind is "stuck" or "chat")
        {
            // The script keeps running: the brain hears the held event now; the measurement ends with that hand-over.
            _tracker.Close(key, "escalated", false, Clip(why));
            try { OnEscalated?.Invoke(ctx.Session, ctx.Interrupt); } catch (Exception) { }
        }
    }

    private static string? Clip(string? s) => s == null ? null : s.Length <= 120 ? s : s[..117] + "...";

    private static readonly Regex TweakWord = new(@"\b(instead|more|less|fewer|only|also|switch|until|up to|make it|use|keep|change|stop at|put|deposit|chest|faster|slower|bigger|smaller)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A trusted line that changes the job, not a conversation: names one of its params, or a tweak word with a number.</summary>
    public static bool LooksLikeTweak(string text, IEnumerable<string> paramNames)
    {
        var t = text ?? "";
        if (t.Length == 0 || t.Length > 160 || t.Contains('?')) return false;
        foreach (var p in paramNames)
            if (p.Length >= 3 && Regex.IsMatch(t, @"\b" + Regex.Escape(p) + @"\b", RegexOptions.IgnoreCase)) return true;
        return TweakWord.IsMatch(t) && Regex.IsMatch(t, @"\d");
    }
}
