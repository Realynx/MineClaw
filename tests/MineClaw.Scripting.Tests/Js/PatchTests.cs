using System.Collections.Concurrent;
using MineClaw.Scripting;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>A scripted <see cref="IInterruptPatcher"/>: plans from a function, records everything the session reports.</summary>
public sealed class FakePatcher : IInterruptPatcher
{
    public Func<PatchContext, ScriptPatch?> Plan { get; set; } = _ => null;
    public bool Want { get; set; } = true;
    public ConcurrentQueue<PatchContext> Plans { get; } = new();
    public ConcurrentQueue<(PatchContext Ctx, ScriptPatch Patch, PatchOutcome Outcome)> Applied { get; } = new();
    public ConcurrentQueue<(PatchContext Ctx, ScriptPatch Patch, bool Worked, string Why)> Verdicts { get; } = new();
    public ConcurrentQueue<(PatchContext Ctx, string Why)> Escalations { get; } = new();
    public TimeSpan Delay { get; set; }

    public bool Wants(ScriptSession session, ScriptInterrupt interrupt) => Want;

    public async Task<ScriptPatch?> PlanAsync(PatchContext ctx, CancellationToken ct)
    {
        Plans.Enqueue(ctx);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        return Plan(ctx);
    }

    public void Applied_(PatchContext ctx, ScriptPatch patch, PatchOutcome outcome) => Applied.Enqueue((ctx, patch, outcome));
    void IInterruptPatcher.Applied(PatchContext ctx, ScriptPatch patch, PatchOutcome outcome) => Applied_(ctx, patch, outcome);
    public void Verdict(PatchContext ctx, ScriptPatch patch, bool worked, string why) => Verdicts.Enqueue((ctx, patch, worked, why));
    public void Escalated(PatchContext ctx, string why) => Escalations.Enqueue((ctx, why));

    public static ScriptPatch Js(string js, PatchThen then = PatchThen.Resume, string? note = null) => new(js, then, PatchSources.Llm, note);
}

/// <summary>
/// Inline interrupt patches (the session side): a patch runs inside the paused script's scope, may read/write its params, machine
/// and blackboard, then resumes / restarts the step / stops; budgets and errors escalate; onInterrupt wins; safety preempts.
/// </summary>
public class PatchTests
{
    private static (Rig Rig, FakePatcher P, List<ScriptSession> Pauses) Setup(ScriptOptions? o = null)
    {
        var rig = Rig.Js(o ?? new ScriptOptions { ChatAutoResumeSeconds = 1, MaxPauseMinutes = 1 });
        var p = new FakePatcher();
        rig.Host.Patcher = p;
        var pauses = new List<ScriptSession>();
        rig.C.Paused += s => { lock (pauses) pauses.Add(s); };
        return (rig, p, pauses);
    }

    /// <summary>The first <c>mine</c> blocks until cut (a danger lands mid-action); later calls succeed at once.</summary>
    private static void FirstMineBlocks(Rig rig, TaskCompletionSource started)
    {
        int n = 0;
        rig.On["mine"] = async (_, ct) =>
        {
            if (Interlocked.Increment(ref n) > 1) return ActResult.Success("mined 1");
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return ActResult.Fail("cancelled");
        };
    }

    private static ScriptEvent Danger(string topic = "lowhp", string text = "hp 5/20 zombie") => new(ScriptEventKind.Danger, text, Topic: topic);

    [Fact]
    public async Task A_patch_runs_in_the_scripts_scope_rewrites_params_and_resumes_at_the_cut_action()
    {
        var (rig, p, pauses) = Setup();
        using var _ = rig;
        var started = new TaskCompletionSource();
        FirstMineBlocks(rig, started);
        p.Plan = ctx => FakePatcher.Js("P.n = 9; carry.fixed = ev.kind + ':' + P.n; wait(0.05)", note: "retuned");
        var s = rig.Start("const P = params({ n: 4 })\nmine('cobblestone', P.n)\nmine('cobblestone', P.n)\nreturn 'n=' + P.n + ' fixed=' + carry.fixed");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        s.Deliver(Danger());
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Contains("n=9 fixed=lowhp:9", s.Outcome);
        var mines = rig.CallList.Where(c => c.StartsWith("mine")).ToList();
        Assert.Equal(3, mines.Count);                       // cut, re-run after the patch, then the next line
        Assert.Contains("\"count\":4", mines[1]);           // the cut action re-ran with its own args
        Assert.Contains("\"count\":9", mines[2]);           // the patched params drive the rest of the run
        Assert.Empty(pauses);                               // never paused: no brain wake
        var (ctx, _, outcome) = Assert.Single(p.Applied);
        Assert.True(outcome.Ok);
        Assert.Equal("lowhp", ctx.Interrupt.Kind);
        Assert.Contains("> ", ctx.Prompt);                  // the current line marked in the source excerpt
        Assert.Contains("params P: {\"n\":4}", ctx.Prompt);
        Assert.Contains("\"n\":9", s.ParamsJson);           // telemetry follows the retune
        Assert.Empty(p.Escalations);
    }

    [Fact]
    public async Task Params_stay_read_only_to_the_script_itself()
    {
        var (rig, _, _) = Setup();
        using var _r = rig;
        var s = await rig.Run("const P = params({ n: 1 })\nP.n = 5\nreturn P.n");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("read only", s.Outcome);
    }

    [Fact]
    public async Task A_patch_reads_and_writes_the_machine_blackboard_and_restart_step_reenters_the_state()
    {
        var (rig, p, pauses) = Setup();
        using var _ = rig;
        var started = new TaskCompletionSource();
        FirstMineBlocks(rig, started);
        p.Plan = ctx => FakePatcher.Js("bb.seen = m.state; bb.skip = true", PatchThen.RestartStep);
        var s = rig.Start("""
            const r = machine({ initial: 'work', bb: { enters: 0 }, states: {
              work: { enter: (bb) => { bb.enters++ }, tick: (bb) => { if (!bb.skip) mine('cobblestone', 1); else return 'done' } },
              done: { final: true } } })
            return 'end'
            """);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        s.Deliver(Danger("danger", "zombie close"));
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Empty(pauses);
        Assert.Contains(s.Logs, l => l.Contains("work -> work (patch)"));  // restart_step = the state re-entered
        var snap = s.CheckpointCarry["@m"];
        Assert.Contains("\"seen\":\"work\"", snap);
        Assert.Contains("\"enters\":2", snap);
    }

    [Fact]
    public async Task Restart_step_without_a_machine_starts_over_at_the_last_checkpoint_keeping_carry()
    {
        var (rig, p, _) = Setup();
        using var _ = rig;
        var started = new TaskCompletionSource();
        int n = 0;
        rig.On["deposit"] = (_, _) => Task.FromResult(Interlocked.Increment(ref n) == 1 ? ActResult.Fail("all 2 known chests full") : ActResult.Success("stored 12"));
        p.Plan = ctx => FakePatcher.Js("carry.fixes = (carry.fixes || 0) + 1", PatchThen.RestartStep);
        var s = await rig.Run("""
            mine('cobblestone', 2)
            cp('store')
            const r = deposit()
            if (!r.ok) fail(r.msg)
            return 'stored, fixes=' + carry.fixes
            """);
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Contains("stored, fixes=1", s.Outcome);
        Assert.Single(rig.CallList, c => c.StartsWith("mine")); // fast-forwarded past the mining to the checkpoint
        Assert.Equal("failed", Assert.Single(p.Plans).Interrupt.Kind);
    }

    [Fact]
    public async Task A_fail_the_patch_fixes_returns_and_the_script_goes_on()
    {
        var (rig, p, _) = Setup();
        using var _ = rig;
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("all 3 known chests full"));
        p.Plan = ctx => FakePatcher.Js("place('chest', '2,65,0')");
        var s = await rig.Run("const r = deposit()\nif (!r.ok) fail(r.msg)\nreturn 'went on'");
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Contains("went on", s.Outcome);
        Assert.Contains(rig.CallList, c => c.StartsWith("place"));
        Assert.Contains("all # known chests full", Assert.Single(p.Plans).Signature);
    }

    [Fact]
    public async Task Ask_brain_can_be_answered_by_a_patch()
    {
        var (rig, p, pauses) = Setup();
        using var _ = rig;
        p.Plan = ctx => FakePatcher.Js("return 'the left one'");
        var s = await rig.Run("const a = ask('brain', 'which chest?')\nreturn 'answer ' + a");
        Assert.Contains("answer the left one", s.Outcome);
        Assert.Empty(pauses);
    }

    [Fact]
    public async Task The_action_budget_aborts_the_patch_and_escalates_to_the_normal_pause()
    {
        var (rig, p, pauses) = Setup(new ScriptOptions { PatchMaxActions = 3, MaxPauseMinutes = 1 });
        using var _ = rig;
        var started = new TaskCompletionSource();
        FirstMineBlocks(rig, started);
        p.Plan = ctx => FakePatcher.Js("for (let i = 0; i < 20; i++) dig('1,64,1')");
        var s = rig.Start("mine('cobblestone', 1)\nreturn 'resumed by the brain'");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        s.Deliver(Danger());
        await Rig.Until(() => s.Status == ScriptStatus.Paused);
        Assert.Single(pauses);                                   // escalated: paused for the brain as before
        Assert.Contains("patch budget", Assert.Single(p.Escalations).Why);
        Assert.Equal(3, rig.CallList.Count(c => c.StartsWith("dig")));
        Assert.Contains("patch escalated", s.State());
        s.Resume();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ScriptStatus.Done, s.Status);
    }

    [Fact]
    public async Task A_busy_patch_loop_aborts_the_patch_never_the_script()
    {
        var (rig, p, pauses) = Setup(new ScriptOptions { PatchMaxStatements = 5_000, MaxPauseMinutes = 1 });
        using var _ = rig;
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("chest full"));
        p.Plan = ctx => FakePatcher.Js("let x = 0; for (let i = 0; i < 1e9; i++) { x++ }");
        var s = await rig.Run("const r = deposit()\nif (!r.ok) fail(r.msg)\nreturn 'no'");
        Assert.Equal(ScriptStatus.Failed, s.Status);   // the fail() went ahead (escalated)
        Assert.Contains("chest full", s.Outcome);
        var why = Assert.Single(p.Escalations).Why;
        Assert.True(why.Contains("statements"), why);
    }

    [Theory]
    [InlineData("noSuchThing()", "lint")]
    [InlineData("const a = 1\nthrow new Error('boom')", "L2 error: boom")]
    [InlineData("fail('nope')", "fail(): nope")]
    public async Task Patch_errors_escalate_with_the_error_line(string js, string expected)
    {
        var (rig, p, _) = Setup();
        using var _ = rig;
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("chest full"));
        p.Plan = ctx => FakePatcher.Js(js);
        var s = await rig.Run("const r = deposit()\nif (!r.ok) fail(r.msg)\nreturn 'no'");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        var why = Assert.Single(p.Escalations).Why;
        Assert.True(why.Contains(expected), why);
    }

    [Fact]
    public async Task A_declared_onInterrupt_wins_and_no_patcher_is_asked()
    {
        var (rig, p, pauses) = Setup();
        using var _ = rig;
        var started = new TaskCompletionSource();
        FirstMineBlocks(rig, started);
        p.Plan = ctx => throw new InvalidOperationException("the patcher must not be asked");
        var s = rig.Start("onInterrupt('danger', ev => { carry.handled = ev.kind; wait(0.05) })\nmine('cobblestone', 1)\nreturn 'h=' + carry.handled");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        s.Deliver(Danger("void", "falling toward void"));
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("h=void", s.Outcome);
        Assert.Empty(p.Plans);
        Assert.Empty(pauses);
        Assert.Equal(PatchSources.Declared, Assert.Single(p.Applied).Patch.Source);
    }

    [Fact]
    public async Task A_declared_handler_that_escalates_passes_it_on_to_the_patcher()
    {
        var (rig, p, _) = Setup();
        using var _ = rig;
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("chest full"));
        p.Plan = ctx => FakePatcher.Js("log('patched')");
        var s = await rig.Run("onInterrupt('failed', ev => 'escalate')\nconst r = deposit()\nif (!r.ok) fail(r.msg)\nreturn 'went on'");
        Assert.Contains("went on", s.Outcome);
        Assert.Single(p.Plans);
    }

    [Fact]
    public async Task Safety_reflexes_preempt_patch_actions_wait_for_them()
    {
        var (rig, p, _) = Setup();
        using var _ = rig;
        bool safety = true;
        rig.Host.SafetyBusy = () => Volatile.Read(ref safety);
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("chest full"));
        p.Plan = ctx => FakePatcher.Js("eat()");
        var s = rig.Start("const r = deposit()\nif (!r.ok) fail(r.msg)\nreturn 'went on'");
        await Rig.Until(() => !p.Plans.IsEmpty);
        await Task.Delay(300);
        Assert.DoesNotContain(rig.CallList, c => c.StartsWith("eat")); // the reflex holds the body: the patch waits
        Assert.Contains("patching", s.State());
        Volatile.Write(ref safety, false);
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("went on", s.Outcome);
        Assert.Contains(rig.CallList, c => c.StartsWith("eat"));
    }

    [Fact]
    public async Task A_new_danger_during_a_patch_aborts_it_and_pauses_for_the_brain()
    {
        var (rig, p, pauses) = Setup();
        using var _ = rig;
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("chest full"));
        var release = new TaskCompletionSource();
        rig.On["goto"] = Rig.Blocking(release);
        p.Plan = ctx => FakePatcher.Js("go('5,65,5')");
        var s = rig.Start("const r = deposit()\nif (!r.ok) fail(r.msg)\nreturn 'went on'");
        await Rig.Until(() => rig.CallList.Any(c => c.StartsWith("goto")));
        s.Deliver(Danger("void", "falling toward void"));
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ScriptStatus.Failed, s.Status); // the fail() went ahead: the patch never overrode the danger
        Assert.Contains("danger during the patch", Assert.Single(p.Escalations).Why);
    }

    [Fact]
    public async Task A_patch_that_worked_gets_a_good_verdict_and_a_repeat_a_bad_one()
    {
        var (rig, p, _) = Setup(new ScriptOptions { PatchVerifySeconds = 5, MaxPauseMinutes = 1 });
        using var _ = rig;
        int n = 0;
        rig.On["deposit"] = (_, _) => Task.FromResult(Interlocked.Increment(ref n) <= 2 ? ActResult.Fail("chest full") : ActResult.Success("stored"));
        p.Plan = ctx => FakePatcher.Js("wait(0.01)");
        // deposit fails, patched; fails again (same situation → the first patch didn't work); patched; then the run progresses.
        var s = await rig.Run("for (let i = 0; i < 3; i++) { const r = deposit(); if (!r.ok) fail(r.msg); wait(0.1); mine('dirt', 1) }\nreturn 'ok'");
        Assert.Equal(ScriptStatus.Done, s.Status);
        var v = p.Verdicts.ToList();
        Assert.Equal(2, v.Count);
        Assert.False(v[0].Worked);
        Assert.Contains("came back", v[0].Why);
        Assert.True(v[1].Worked);
    }

    [Fact]
    public async Task Without_a_patcher_interrupts_behave_exactly_as_before()
    {
        using var rig = Rig.Js(new ScriptOptions { MaxPauseMinutes = 1 });
        int pauses = 0;
        rig.C.Paused += _ => Interlocked.Increment(ref pauses);
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("chest full"));
        var s = await rig.Run("const r = deposit()\nif (!r.ok) fail(r.msg)\nreturn 'no'");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("chest full", s.Outcome);
        Assert.Equal(0, pauses);
    }

    [Fact]
    public void The_patch_context_stays_under_its_token_cap()
    {
        var (rig, _, _) = Setup();
        using var _r = rig;
        // A long script near the size cap with long lines, big params and logs: the uncached part stays ≤ 2.5K tokens.
        var lines = Enumerable.Range(0, 20).Select(i => $"mine('cobblestone', {i}) // {new string('x', 100)}");
        var s = rig.Start("const P = params({ " + string.Join(", ", Enumerable.Range(0, 15).Select(i => $"p{i}: 'value number {i} with words'")) + " })\n"
            + "ask('brain', '" + new string('q', 150) + "')\n" + string.Join("\n", lines));
        Rig.Until(() => s.Status == ScriptStatus.Paused).Wait();
        var text = PatchPrompt.Context(s, new ScriptInterrupt("failed", new string('e', 400)));
        int tokens = (int)Math.Ceiling(text.Length / 3.5);
        Assert.True(tokens <= 2500, $"patch context ≈{tokens} tokens");
        var system = PatchPrompt.System(new string('g', 900), 8, 45);
        Assert.True(system.Length / 3.5 <= 1500, $"patch system ≈{system.Length / 3.5:0} tokens");
        s.Stop("done");
    }

    [Fact]
    public void Signatures_ignore_numbers_positions_and_players()
    {
        var a = PatchSignature.Of(new ScriptInterrupt("failed", "deposit: all 3 known chests full at 12,64,-5 (Fox)"), "expect(deposit('chest'))", ["Fox"]);
        var b = PatchSignature.Of(new ScriptInterrupt("failed", "deposit: all 4 known chests full at 1,70,9 (fox)"), "expect(deposit('chest'))", ["Fox"]);
        Assert.Equal(a, b);
        Assert.NotEqual(a, PatchSignature.Of(new ScriptInterrupt("lowhp", "deposit: all 3 known chests full"), "expect(deposit('chest'))"));
    }
}
