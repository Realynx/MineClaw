using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MineClaw.Scripting;

/// <summary>
/// Inline interrupt fixing. An interrupt a few lines of JS might fix (a danger pause, a fail(), ask('brain'), a stall, a trusted
/// chat tweak) is offered, on the script thread and at a safe point, to: the script's own <c>onInterrupt(kind, fn)</c> → the host's
/// patcher (a learned handler: no LLM call; else one small Opus patch turn) → the full brain turn (escalate, exactly the old path).
/// A patch runs in this session's engine with the script's scope (params, machine, blackboard, carry, SDK) under its own budget
/// (actions, seconds, statements); it waits while a safety reflex holds the body and a different danger aborts it. Then the
/// script resumes, restarts its step (machine state / last checkpoint) or stops. A patch that resumed or restarted is verified:
/// progress without the same interrupt within <see cref="ScriptOptions.PatchVerifySeconds"/> = it worked (the host learns it).
/// </summary>
public sealed partial class ScriptSession
{
    private sealed class PatchRun
    {
        public required ScriptInterrupt Interrupt;
        public required DateTimeOffset Deadline;
        public int Actions;
        public long Statements;
    }

    private sealed record Declared(string Pattern, Func<ScriptInterrupt, string?> Run);

    private sealed record Verify(PatchContext Ctx, ScriptPatch Patch, DateTimeOffset At, int OkActionsAt);

    private readonly List<Declared> _declared = new();
    private readonly Queue<ScriptInterrupt> _interruptReqs = new();
    private PatchRun? _patchRun; // script thread only
    private volatile string? _patching;
    private bool _inPatch;
    private int _patchCount, _restarts, _okActions;
    private Verify? _verify;
    private ScriptEvent? _pauseReqEvent;
    private volatile bool _pauseInFlight;

    /// <summary>"planning: danger lowhp" / "running learned handler (2 acts)" while an interrupt is being fixed inline, else null.</summary>
    public string? Patching => _patching;
    /// <summary>The interrupt kind being fixed inline right now (lowhp...), else null: the host keeps the same danger's wakes held.</summary>
    public string? PatchingKind => _patchingKind;
    private volatile string? _patchingKind;
    /// <summary>The last patch line for the brain ("patch escalated: needs a new chest"), shown in the state while paused.</summary>
    public string? PatchNote { get; private set; }
    /// <summary>The lane body this run drives (null = the main body).</summary>
    public string? LaneBody => _host.Body;

    /// <summary>Patches this run (declared, learned and patch turns).</summary>
    public int Patches => _patchCount;

    /// <summary>
    /// Any thread: would this interrupt be offered to a declared handler or the patcher (the host then holds the brain's wake)?
    /// False for Lua, when patches are exhausted, while a patch already runs, and when neither a handler nor the patcher wants it.
    /// </summary>
    public bool WouldPatch(ScriptInterrupt i)
    {
        if (!IsLive || Language != ScriptLanguage.Js || _patchCount >= _o.PatchMaxPerRun) return false;
        lock (_lock) if (_declared.Any(d => i.Matches(d.Pattern))) return true;
        if (!_o.Patches || _host.Patcher is not { } p) return false;
        try { return p.Wants(this, i); } catch (Exception) { return false; }
    }

    /// <summary>Any thread: handle <paramref name="i"/> at the next safe point (stall, chat tweak). <paramref name="cut"/> aborts the action in flight.</summary>
    public bool RequestInterrupt(ScriptInterrupt i, bool cut)
    {
        CancellationTokenSource? abort = null;
        lock (_lock)
        {
            if (!IsLive || _stopReason != null || Status == ScriptStatus.Paused) return false;
            if (_interruptReqs.Count >= 4) return false;
            _interruptReqs.Enqueue(i);
            if (cut && !_currentAtomic && _abortCts != null) { _abortedForPause = true; abort = _abortCts; }
        }
        try { abort?.Cancel(); } catch (ObjectDisposedException) { }
        _wake.Set();
        return true;
    }

    /// <summary><c>onInterrupt(kind, fn)</c>: the script's own inline fix, tried before any learned handler or LLM call.</summary>
    internal void OnInterrupt(string pattern, Func<ScriptInterrupt, string?> run)
    {
        var p = (pattern ?? "").Trim();
        if (p.Length == 0) throw Bug("onInterrupt(kind, fn) needs a kind: danger|lowhp|void|died|stuck|failed|needs_input|chat|*");
        lock (_lock)
        {
            if (_declared.Count >= 12) throw Bug("too many onInterrupt handlers");
            _declared.Add(new Declared(p, run));
        }
    }

    // ------------------------------------------------------------------ entry points (script thread)

    /// <summary>A danger pause request at a safe point: true = fixed inline (the loop carries on), false = pause as before.</summary>
    private bool PatchPause(PauseKind kind, ScriptEvent? ev)
    {
        if (kind is not (PauseKind.Danger or PauseKind.Script) || ev is not { Kind: ScriptEventKind.Danger }) return false;
        var i = new ScriptInterrupt(ev.Topic ?? "danger", Short(OneLine(ev.Text), 120), ev.Player);
        // Still a pending pause while it is fixed inline (an interrupt-cancelled activity must not read it as superseded).
        _pauseInFlight = true;
        PatchOutcome? r;
        try { r = Interrupted(i); }
        catch { _pauseInFlight = false; throw; }
        if (r != null) _pauseInFlight = false; // else PauseHere clears it once paused

        if (r == null) return false;
        Apply(r);
        return true;
    }

    /// <summary>Queued stall/chat interrupts at a safe point. Escalation leaves the script running (the host re-posts the wake).</summary>
    private bool TakeInterruptRequest()
    {
        ScriptInterrupt? i;
        lock (_lock) if (!_interruptReqs.TryDequeue(out i)) return false;
        var r = Interrupted(i!);
        if (r != null) Apply(r);
        return true;
    }

    /// <summary><c>fail(msg)</c>: a patch may fix the cause (resume = fail returns and the script goes on); else it fails as before.</summary>
    internal void FailPoint(string? msg)
    {
        var ex = Fail(msg);
        if (Language == ScriptLanguage.Js && !_inPatch && _resumeTarget == null)
        {
            var r = Interrupted(new ScriptInterrupt("failed", Short(OneLine(ex.Message), 160)));
            if (r is { Then: PatchThen.Resume or PatchThen.RestartStep })
            {
                Apply(r);
                TouchSegment();
                return;
            }
        }
        throw ex;
    }

    /// <summary><c>ask('brain', q)</c>: a patch may answer (its returned string); else the script pauses for the brain.</summary>
    private bool PatchAsk(string question, out string? answer)
    {
        answer = null;
        if (_inPatch) return false;
        var r = Interrupted(new ScriptInterrupt("needs_input", question));
        if (r == null) return false;
        if (r.Then == PatchThen.Resume) { answer = r.Answer; return true; }
        Apply(r);
        return true;
    }

    private void Apply(PatchOutcome r)
    {
        switch (r.Then)
        {
            case PatchThen.RestartStep: RestartStep(); break;
            case PatchThen.Stop: Stop("stopped by patch" + (PatchNote is { } n ? ": " + n : "")); ThrowIfStopping(); break;
        }
    }

    /// <summary>Re-enter the running machine's current state, else restart the run at its last checkpoint.</summary>
    private void RestartStep()
    {
        if (++_restarts > Math.Max(0, _o.PatchMaxRestarts)) throw Bug($"patch restarted the step {_o.PatchMaxRestarts} times");
        Sdk.MachineRunner? m;
        lock (_machines) m = _machines.Count > 0 ? _machines[^1] : null;
        if (m != null) throw new Sdk.ScriptUnwind(m, m.State, "patch");
        throw new ScriptRestartException(Checkpoint);
    }

    // ------------------------------------------------------------------ the chain

    /// <summary>
    /// Declared handler → host patcher → null (escalate). Returns how the patch went (Ok) with what to do next; null = the caller
    /// takes the normal path (pause and wake, or fail).
    /// </summary>
    private PatchOutcome? Interrupted(ScriptInterrupt i)
    {
        if (_inPatch || _patchCount >= _o.PatchMaxPerRun || _resumeTarget != null || Language != ScriptLanguage.Js) return null;
        Locate();
        var sig = PatchSignature.Of(i, SourceLine(Line), _host.OnlinePlayers?.Invoke());
        CheckVerifyOnInterrupt(sig);
        var ctx = new PatchContext(this, i, sig, _patchCount + 1, "", _time.GetUtcNow());
        _patchingKind = i.Kind;
        try { return Chain(i, sig, ctx); }
        finally { _patchingKind = null; }
    }

    private PatchOutcome? Chain(ScriptInterrupt i, string sig, PatchContext ctx)
    {

        // 1. The script's own onInterrupt(kind, fn).
        Declared? d;
        lock (_lock) d = _declared.FirstOrDefault(x => i.Matches(x.Pattern));
        if (d != null)
        {
            _patchCount++;
            var patch = new ScriptPatch("", PatchThen.Resume, PatchSources.Declared, $"onInterrupt('{d.Pattern}')");
            var outcome = RunGuarded(i, patch, () => d.Run(i), declared: true);
            SafeHost(p => p.Applied(ctx, patch, outcome));
            if (outcome.Ok && outcome.Then != PatchThen.Escalate) return Done(ctx, patch, outcome);
            if (!outcome.Ok) PatchNote = "onInterrupt failed: " + outcome.Error;
        }

        // 2. A learned handler or a patch turn.
        if (!_o.Patches || _host.Patcher is not { } patcher || _patchCount >= _o.PatchMaxPerRun) return Escalate(ctx, null);
        bool wants;
        try { wants = patcher.Wants(this, i); } catch (Exception) { wants = false; }
        if (!wants) return Escalate(ctx, null);
        _patchCount++;
        ctx = ctx with { Attempt = _patchCount, Prompt = PatchPrompt.Context(this, i) };
        _patching = "planning: " + i.Kind;
        ScriptPatch? plan;
        try { plan = WaitPlan(patcher, ctx, i); }
        finally { _patching = null; }
        if (plan == null || plan.Then == PatchThen.Escalate || (plan.Js.Trim().Length == 0 && plan.Then != PatchThen.Stop && plan.Then != PatchThen.Resume))
            return Escalate(ctx, plan?.Note ?? "no patch");
        if (plan.Js.Trim().Length > 0 && LintPatch(plan.Js) is { } lintError)
        {
            var bad = new PatchOutcome(false, plan.Then, lintError, 0, 0);
            SafeHost(p => p.Applied(ctx, plan, bad));
            return Escalate(ctx, "patch " + lintError);
        }
        var run = RunGuarded(i, plan, () => plan.Js.Trim().Length == 0 ? null : RunPatchCode(plan.Js, i), declared: false);
        SafeHost(p => p.Applied(ctx, plan, run));
        if (!run.Ok) return Escalate(ctx, "patch error: " + run.Error);
        if (run.Then == PatchThen.Escalate) return Escalate(ctx, plan.Note ?? "patch passed it on");
        return Done(ctx, plan, run);
    }

    private PatchOutcome Done(PatchContext ctx, ScriptPatch patch, PatchOutcome outcome)
    {
        PatchNote = $"{patch.Source}: {PatchThens.Tag(outcome.Then)}" + (patch.Note is { Length: > 0 } n ? " (" + Short(OneLine(n), 60) + ")" : "");
        Log($"patch ({patch.Source}) for {ctx.Interrupt.Kind}: {PatchThens.Tag(outcome.Then)} in {outcome.Seconds:0.#}s, {outcome.Actions} acts" +
            (patch.Note is { Length: > 0 } note ? " — " + note : ""));
        if (outcome.Then is PatchThen.Resume or PatchThen.RestartStep) StartVerify(ctx, patch);
        return outcome;
    }

    private PatchOutcome? Escalate(PatchContext ctx, string? why)
    {
        if (why != null)
        {
            PatchNote = "escalated: " + Short(OneLine(why), 100);
            Log("patch " + PatchNote);
        }
        SafeHost(p => p.Escalated(ctx, why ?? "no handler"));
        return null;
    }

    private void SafeHost(Action<IInterruptPatcher> a)
    {
        if (_host.Patcher is not { } p) return;
        try { a(p); } catch (Exception ex) { _host.Log?.Invoke($"script {Name}: patcher failed: {ex.Message}"); }
    }

    /// <summary>Blocks the script thread for the patch turn: a stop ends it, a new danger or the timeout escalates.</summary>
    private ScriptPatch? WaitPlan(IInterruptPatcher patcher, PatchContext ctx, ScriptInterrupt i)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token, _hardCt);
        Task<ScriptPatch?> task;
        try { task = patcher.PlanAsync(ctx, cts.Token); }
        catch (Exception ex) { _host.Log?.Invoke($"script {Name}: patch plan failed: {ex.Message}"); return null; }
        var end = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(1, _o.PatchPlanTimeoutSeconds));
        _blocked = true;
        try
        {
            while (!task.IsCompleted)
            {
                try { task.Wait(TimeSpan.FromMilliseconds(50)); } catch (Exception) { break; }
                if (_stopReason != null) { cts.Cancel(); ThrowIfStopping(); }
                if (OtherDangerPending(i) is { } why) { cts.Cancel(); return ScriptPatch.Escalate(why); }
                if (_time.GetUtcNow() >= end) { cts.Cancel(); return ScriptPatch.Escalate("patch turn timed out"); }
            }
        }
        finally { WaitProbe(); }
        try { return task.GetAwaiter().GetResult(); }
        catch (Exception ex) { _host.Log?.Invoke($"script {Name}: patch plan failed: {OneLine(ex.Message)}"); return null; }
    }

    /// <summary>A stop, or a danger of another kind than the one being fixed, waiting for this script (null = none).</summary>
    private string? OtherDangerPending(ScriptInterrupt i)
    {
        lock (_lock)
        {
            if (_pauseReq is not { } k) return null;
            if (k == PauseKind.Stop) return "a player said stop";
            if (k != PauseKind.Danger) return null;
            var name = _pauseReqEvent?.Name ?? "danger";
            if (i.IsDanger && string.Equals(name, i.Kind, StringComparison.OrdinalIgnoreCase))
            {
                // The same danger again (hp dropping while the patch handles it): coalesced into this one.
                _pauseReq = null;
                _pauseReqEvent = null;
                return null;
            }
            return "danger during the patch: " + (_pauseReqReason ?? name);
        }
    }

    // ------------------------------------------------------------------ running a patch

    private PatchOutcome RunGuarded(ScriptInterrupt i, ScriptPatch patch, Func<string?> body, bool declared)
    {
        var sw = Stopwatch.StartNew();
        var run = new PatchRun { Interrupt = i, Deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(0.5, _o.PatchMaxSeconds)) };
        _patchRun = run;
        _inPatch = true;
        _patching = $"running {(declared ? "onInterrupt" : patch.Source == PatchSources.Learned ? "learned handler" : "patch")} for {i.Kind}";
        bool wasActive = _active.IsRunning;
        if (!wasActive) _active.Start();
        TouchSegment();
        try
        {
            WaitForSafety(run);
            var ret = body();
            var then = patch.Then;
            if (declared) then = PatchThens.Parse(ret);
            return new PatchOutcome(true, then, null, run.Actions, Math.Round(sw.Elapsed.TotalSeconds, 2), declared ? null : ret);
        }
        catch (PatchAbortException a) { return new PatchOutcome(false, patch.Then, a.Message, run.Actions, Math.Round(sw.Elapsed.TotalSeconds, 2)); }
        catch (ScriptStopException) { throw; }
        catch (ScriptLimitException) { throw; }
        catch (Sdk.ScriptUnwind) { throw; } // m.go('x') from a patch: the machine takes the transition
        catch (ScriptFailException f) { return new PatchOutcome(false, patch.Then, "fail(): " + Short(OneLine(f.Message), 100), run.Actions, Math.Round(sw.Elapsed.TotalSeconds, 2)); }
        catch (Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is ScriptStopException or ScriptLimitException or Sdk.ScriptUnwind) throw e;
                else if (e is PatchAbortException pa) return new PatchOutcome(false, patch.Then, pa.Message, run.Actions, Math.Round(sw.Elapsed.TotalSeconds, 2));
            string why;
            try { why = _runtime?.Describe(ex) is { } d ? (d.Line > 0 && !declared ? $"L{d.Line} " : "") + d.Reason : ex.GetType().Name + ": " + OneLine(ex.Message); }
            catch (Exception) { why = ex.GetType().Name; }
            return new PatchOutcome(false, patch.Then, Short(why, 140), run.Actions, Math.Round(sw.Elapsed.TotalSeconds, 2));
        }
        finally
        {
            _patchRun = null;
            _inPatch = false;
            _patching = null;
            if (!wasActive) _active.Stop();
            TouchSegment();
        }
    }

    private string? RunPatchCode(string js, ScriptInterrupt i) =>
        _runtime is IPatchRuntime pr ? pr.RunPatch(js, i) : throw new PatchAbortException("patches need the JS runtime");

    /// <summary>The patch's own lint: syntax and forbidden constructs (lines as the model wrote them), or null when clean.</summary>
    private string? LintPatch(string js)
    {
        try
        {
            var names = Callables(_ => true).Select(c => c.Global);
            var report = Lint.JsLint.Check("async function __patch(ev, P, m, bb) {\n" + js + "\n}", names, int.MaxValue);
            var errs = report.Errors.Where(f => f.Rule != "crew").Select(f => $"L{Math.Max(1, f.Line - 1)} {f.Message}").ToList();
            return errs.Count == 0 ? null : "lint: " + Short(string.Join("; ", errs), 160);
        }
        catch (Exception ex) { return "lint: " + OneLine(ex.Message); }
    }

    /// <summary>A safety reflex holds the body: the patch waits (bounded by its deadline); patches never override safety.</summary>
    private void WaitForSafety(PatchRun run)
    {
        if (_host.SafetyBusy is not { } busy) return;
        while (SafeBusy(busy))
        {
            ThrowIfStopping();
            if (_time.GetUtcNow() >= run.Deadline) throw new PatchAbortException("safety reflex still busy");
            Thread.Sleep(50);
        }
    }

    private static bool SafeBusy(Func<bool> busy)
    {
        try { return busy(); } catch (Exception) { return false; }
    }

    /// <summary>RunAction in a patch: the action budget, the deadline, a waiting safety reflex, a danger it doesn't own.</summary>
    private void PatchBeforeAction(PatchRun run)
    {
        ThrowIfStopping();
        if (OtherDangerPending(run.Interrupt) is { } why) throw new PatchAbortException(why);
        if (++run.Actions > Math.Max(1, _o.PatchMaxActions)) throw new PatchAbortException($"patch budget: over {_o.PatchMaxActions} actions");
        if (_time.GetUtcNow() >= run.Deadline) throw new PatchAbortException($"patch budget: over {ScriptText.Dur(TimeSpan.FromSeconds(_o.PatchMaxSeconds))}");
        WaitForSafety(run);
    }

    /// <summary>The budget hook in a patch: its statement cap and deadline (abort the patch, never the script).</summary>
    private void PatchBudget(PatchRun run, int units)
    {
        run.Statements += units;
        if (run.Statements > _o.PatchMaxStatements) throw new PatchAbortException($"patch budget: over {_o.PatchMaxStatements} statements");
        if (_time.GetUtcNow() >= run.Deadline) throw new PatchAbortException($"patch budget: over {ScriptText.Dur(TimeSpan.FromSeconds(_o.PatchMaxSeconds))}");
    }

    // ------------------------------------------------------------------ verification (learning)

    private void StartVerify(PatchContext ctx, ScriptPatch patch)
    {
        FinishVerify(null, "");
        _verify = new Verify(ctx, patch, _time.GetUtcNow(), _okActions);
    }

    private void CheckVerifyOnInterrupt(string sig)
    {
        if (_verify is not { } v) return;
        if (v.Ctx.Signature == sig) FinishVerify(false, "the same interrupt came back");
    }

    /// <summary>After a successful action / from the watchdog: the window passed with progress → it worked.</summary>
    private void CheckVerifyProgress()
    {
        if (_verify is not { } v) return;
        if (_time.GetUtcNow() - v.At >= TimeSpan.FromSeconds(_o.PatchVerifySeconds) && _okActions > v.OkActionsAt)
            FinishVerify(true, "progressed without a repeat");
    }

    /// <summary>The run ended: done = it worked; failed within the window = it didn't; cancelled = no verdict.</summary>
    private void VerifyAtEnd(ScriptStatus final)
    {
        if (_verify is not { } v) return;
        if (final == ScriptStatus.Done) FinishVerify(true, "the script finished");
        else if (final == ScriptStatus.Failed) FinishVerify(_time.GetUtcNow() - v.At >= TimeSpan.FromSeconds(_o.PatchVerifySeconds) && _okActions > v.OkActionsAt,
            "the script failed after it: " + Short(Reason ?? "", 60));
        else FinishVerify(null, "cancelled");
    }

    private void FinishVerify(bool? worked, string why)
    {
        var v = _verify;
        _verify = null;
        if (v == null || worked == null) return;
        SafeHost(p => p.Verdict(v.Ctx, v.Patch, worked.Value, why));
    }

    internal void NoteActionOk() { _okActions++; if (_verify != null) CheckVerifyProgress(); }

    // ------------------------------------------------------------------ prompt material (script thread)

    /// <summary>The source around <paramref name="line"/> (±<paramref name="span"/>), the line itself marked "&gt;".</summary>
    internal string SourceAround(int line, int span)
    {
        if (_sourceLines.Length == 0) return "";
        int at = line > 0 ? line : 1, from = Math.Max(1, at - span), to = Math.Min(_sourceLines.Length, at + span);
        var sb = new StringBuilder();
        sb.Append(line > 0 ? $"at L{line}:\n" : "source (line unknown):\n");
        for (int n = from; n <= to; n++)
        {
            var text = _sourceLines[n - 1].TrimEnd('\r');
            if (text.Length > 140) text = text[..137] + "...";
            sb.Append(n == line ? "> " : "  ").Append(n.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append("| ").Append(text).Append('\n');
        }
        return sb.ToString();
    }

    internal string? MachineBlackboard()
    {
        Sdk.MachineRunner? m;
        lock (_machines) m = _machines.Count > 0 ? _machines[^1] : null;
        try { return m?.Blackboard()?.ToJsonString(); } catch (Exception) { return null; }
    }

    internal string? CurrentCarryText()
    {
        try { return _runtime?.CarryJson(); } catch (Exception) { return null; }
    }

    internal IReadOnlyList<string> RecentLogs(int n)
    {
        lock (_lock) return Logs.TakeLast(n).ToList();
    }

    /// <summary>"hp 5/20 food 14 | at 3,64,2 | holding stone pickaxe | inv 44 cobblestone, 2 bread … | 12 free | hostiles zombie 3m | night".</summary>
    internal string BodyLine()
    {
        try
        {
            var w = _host.World;
            var p = w.Player.Position;
            var sb = new StringBuilder();
            sb.Append($"hp {w.Player.Health:0}/20 food {w.Player.Food}");
            sb.Append(" | at ").Append(FmtPos(new World.BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y + 1e-6), (int)Math.Floor(p.Z))));
            var counts = InventoryCounts().OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Value} {SafeName(kv.Key.Item1, kv.Key.Item2)}").ToList();
            sb.Append(" | inv ").Append(counts.Count == 0 ? "empty" : string.Join(", ", counts));
            sb.Append(" | ").Append(w.Inventory.EmptySlotCount()).Append(" free");
            var mobs = w.Entities.Nearby(p, 16, e => e.IsHostileMob).OrderBy(e => e.Position.DistanceTo(p)).Take(4)
                .Select(e => $"{e.DisplayName.ToLowerInvariant()} {e.Position.DistanceTo(p):0}m").ToList();
            sb.Append(" | hostiles ").Append(mobs.Count == 0 ? "none" : string.Join(", ", mobs));
            if (w.IsNight) sb.Append(" | night");
            if (_host.SafetyBusy is { } busy && SafeBusy(busy)) sb.Append(" | safety reflex active");
            return sb.ToString();
        }
        catch (Exception) { return "unknown"; }
    }
}

/// <summary>The JS runtime's patch side: run a few lines in the paused script's scope.</summary>
internal interface IPatchRuntime
{
    /// <summary>Runs <paramref name="js"/> as <c>async (ev, P, m, bb) =&gt; { js }</c> on the script thread; its string return value, if any.</summary>
    string? RunPatch(string js, ScriptInterrupt i);
}

/// <summary>
/// The patch turn's prompt: a small static system part (cached) and a compact per-interrupt context (≤ ~2K tokens): the interrupt,
/// the script, the current line ±5, machine/params/checkpoint/carry, the body and the last logs.
/// </summary>
public static class PatchPrompt
{
    public const int MaxContextChars = 6_500;

    /// <summary>The static system part (rules + one-screen SDK cheat-sheet + this bot's game functions): one cache entry.</summary>
    public static string System(string gameFunctions, int maxActions, double maxSeconds) => $"""
        You fix a running Minecraft bot script that just hit an interrupt. Answer with ONE `patch` tool call, fast, no prose.
        The script is JavaScript on the MineClaw SDK; host calls are synchronous and return {"{ok,msg,n}"}. Your js runs INSIDE the paused script
        (its engine, its scope), then the script continues per `then`. Body of `async (ev, P, m, bb) => {"{ your js }"}`:
        - ev {"{kind,text,from,line}"} · P the script's params (assign P.x = v to retune the rest of this run) · m the running machine
          (m.state, m.history, m.go('state')) or null · bb m's blackboard (writable) · carry (kept across restarts) · return "text" answers ask('brain').
        - Budget: ≤ {maxActions} actions, ≤ {maxSeconds:0} s. A safety reflex (shelter, retreat, eat, defence) always runs first; your actions wait for it.
          A new danger aborts you. Never fight or wander at low hp; never run server commands.
        then: resume = carry on (the cut action re-runs; after fail() the script goes on at the next line, so redo the failed step in js) ·
        restart_step = re-enter the machine state / restart from the last checkpoint · stop = end it · escalate = needs the brain (talk,
        judgment, new code, unclear) — js empty. Prefer the smallest fix: wait for a mob to leave, step aside, fetch/craft the missing thing,
        pick another chest, eat, re-tool, retune P. A player's chat tweak: change P (and say() one short line). Unsure → escalate.
        # SDK (all globals)
        core: ok(r) expect(r,msg) fail(msg) log(..) wait(s) waitFor(kind,filter,s) say(t) ask(p,q,s) cp(label) params(d) carry args
        world: me.hp/food/pos/x/y/z/night inv(item) free() block(x,y,z) near(kind,r)->{"{x,y,z,d}"} hostile(r)->{"{name,d,x,y,z}"}|null where(item) night() time() players() dist(a,b)
        flow: retry(n,fn) upto(n,fn) until(cond,fn,max) timeout(s,fn) room(n) ensure(kind|{"{item,n}"})
        jobs: store(chest?) restock(item,n) mineGen(opts) harvestFarm() chopTrees(n) deliver(player,item,n) waitForDay()
        {gameFunctions}
        """;

    /// <summary>The per-interrupt context (the uncached part). Script thread.</summary>
    public static string Context(ScriptSession s, ScriptInterrupt i)
    {
        var sb = new StringBuilder();
        sb.Append("interrupt: ").Append(i.Kind).Append(": ").Append(ScriptSession.Short(ScriptSession.OneLine(i.Text), 200));
        if (i.Player != null) sb.Append(" (from ").Append(i.Player).Append(')');
        sb.Append('\n');
        sb.Append("script: ").Append(s.Name).Append(s.Request.Version is { } v ? " v" + v : "").Append(" | ").Append(s.Actions).Append(" acts, ")
            .Append(ScriptText.Dur(s.Elapsed)).Append(s.Patches > 1 ? $" | patch #{s.Patches} this run" : "").Append('\n');
        sb.Append(s.SourceAround(s.Line, 5));
        if (s.MachineState != null) sb.Append("machine: state ").Append(s.MachineState).Append($" ({s.Transitions} moves)").Append(s.MachineBlackboard() is { } bb ? " bb " + ScriptSession.Short(bb, 300) : "").Append('\n');
        if (s.ParamsJson != null) sb.Append("params P: ").Append(ScriptSession.Short(s.ParamsJson, 300)).Append('\n');
        if (s.Checkpoint != null) sb.Append("checkpoint: '").Append(s.Checkpoint).Append('\'').Append(s.CheckpointTotal > 0 ? $" {s.CheckpointsPassed}/{s.CheckpointTotal}" : "").Append('\n');
        if (s.CurrentCarryText() is { Length: > 2 } carry) sb.Append("carry: ").Append(ScriptSession.Short(carry, 200)).Append('\n');
        if (s.LastFailure != null) sb.Append("last fail: ").Append(s.LastFailure).Append('\n');
        sb.Append("body: ").Append(s.BodyLine()).Append('\n');
        var logs = s.RecentLogs(3);
        if (logs.Count > 0) sb.Append("logs: ").Append(ScriptSession.Short(string.Join(" | ", logs), 300)).Append('\n');
        if (s.PatchNote != null) sb.Append("previous patch: ").Append(s.PatchNote).Append('\n');
        var text = sb.ToString();
        return text.Length <= MaxContextChars ? text : text[..(MaxContextChars - 3)] + "...";
    }
}
