using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Authoring;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Tests;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// Fake Opus for the patch turn: <c>purpose=patch</c> calls answer with one <c>patch</c> tool call from <see cref="Patch"/>; brain
/// calls take scripted steps (tool calls / text), else end the turn. Every call can take <see cref="Delay"/> (latency benchmark).
/// </summary>
public sealed class PatchLlm : ILlmClient
{
    private readonly ConcurrentQueue<Func<LlmRequest, LlmResponse>> _brain = new();
    public ConcurrentQueue<LlmRequest> Requests { get; } = new();
    public Func<LlmRequest, (string? Js, string Then, string? Note)> Patch { get; set; } = _ => (null, "escalate", "no patch");
    public TimeSpan Delay { get; set; }
    public int Patches => Requests.Count(r => r.Purpose == PatchAgent.Purpose);
    public int Brain => Requests.Count(r => r.Tier == ModelTier.Main && r.Purpose != PatchAgent.Purpose);

    public PatchLlm BrainTool(string name, object input, string id = "tu_1")
    {
        _brain.Enqueue(r => new LlmResponse([new LlmToolUse(id, name, JsonSerializer.SerializeToElement(input))], StopReasons.ToolUse, Usage, Model(r)));
        return this;
    }

    public PatchLlm BrainText(string text)
    {
        _brain.Enqueue(r => new LlmResponse([new LlmText(text)], StopReasons.EndTurn, Usage, Model(r)));
        return this;
    }

    private static readonly LlmUsage Usage = new(40, 120, 30_000, 0);
    private static string Model(LlmRequest r) => r.Model ?? "claude-opus-5-5";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (request.Purpose == PatchAgent.Purpose)
        {
            var (js, then, note) = Patch(request);
            var input = JsonSerializer.SerializeToElement(new { js, then, note });
            return new LlmResponse([new LlmToolUse("p1", PatchAgent.ToolName, input)], StopReasons.ToolUse, new LlmUsage(900, 80, 1_400, 0), Model(request));
        }
        if (request.Tier == ModelTier.Main && _brain.TryDequeue(out var step)) return step(request);
        return new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, Model(request));
    }
}

/// <summary>
/// The patch turn end to end on the in-memory bot: a danger during a saved script becomes ONE small Opus call whose js runs inside
/// the script, no brain turn; the patch that worked is learned as a handler (the next identical interrupt makes no LLM call);
/// a handler that fails twice is demoted; only Opus writes patches; the patch tool never joins the brain's tools.
/// </summary>
public class PatchTurnTests
{
    private const string Loop = "// mine and count patches\nfor (let i = 1; i <= 40; i++) { wait(0.05); prog(i * 2.5) }\nreturn 'k=' + carry.k";

    private static (BotHost H, FakeGameClient C, ScriptBridge B) Bot(PatchLlm llm, ScriptOptions? scripts = null, Action<AgentOptions>? configure = null) =>
        BotScriptTests.Bot(o => { o.InlineToolWaitSeconds = 1; o.Self.TrustedPlayers = ["kiwi"]; configure?.Invoke(o); },
            scripts ?? new ScriptOptions { PatchVerifySeconds = 0.3, MaxPauseMinutes = 10 }, llm);

    private static async Task<ScriptSession> Running(BotHost h, ScriptBridge b, string name = "loopy")
    {
        var r = await h.Run("script", new { name });
        Assert.True(r.Ok, r.Text);
        await Rig.Until(() => b.Controller.Current?.ProgressPct > 0, 5000);
        return b.Controller.Current!;
    }

    private static async Task<InterruptRecord> Record(BotHost h, int count)
    {
        await Rig.Until(() => h.Runtime.Interrupts.Recent().Count >= count, 8000);
        return h.Runtime.Interrupts.Recent()[count - 1];
    }

    [Fact]
    public async Task A_danger_gets_one_small_opus_patch_call_whose_js_runs_inside_the_script_and_no_brain_turn()
    {
        var llm = new PatchLlm { Patch = _ => ("carry.k = (carry.k || 0) + 1; wait(0.05)", "resume", "waited it out") };
        var (h, _, b) = Bot(llm);
        b.Library.Save("loopy", Loop);
        var cts = new CancellationTokenSource();
        _ = h.Runtime.Agent.RunAsync(cts.Token);
        try
        {
            var s = await Running(h, b);
            h.Runtime.Events.Post(GameEvent.LowHealth(5, cause: "zombie"));
            var rec = await Record(h, 1);
            Assert.Equal("patch", rec.Path);
            Assert.Equal("lowhp", rec.Kind);
            Assert.Equal("resume", rec.Then);
            Assert.Equal(1, rec.Calls);                   // one call for the whole interrupt
            Assert.Equal(0, llm.Brain);                   // no full brain turn
            Assert.Equal(ScriptStatus.Running, s.Status); // it never paused
            await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("k=1", s.Outcome);

            var call = Assert.Single(llm.Requests, r => r.Purpose == PatchAgent.Purpose);
            Assert.Equal("claude-opus-5-5", call.Model);
            Assert.Equal("low", call.Effort);
            Assert.Equal(600, call.MaxTokens);
            Assert.Equal(PatchAgent.ToolName, Assert.Single(call.Tools).Name); // the patch tool lives only here
            Assert.True(call.System[0].Cache);                                  // static part cached
            var context = call.Messages[0].Content.OfType<LlmText>().Single().Text;
            Assert.Contains("interrupt: lowhp", context);
            Assert.Contains("script: loopy v1", context);
            Assert.Contains("body: hp", context);
            Assert.True(context.Length / 3.5 <= 2500, $"uncached ≈{context.Length / 3.5:0} tokens");
        }
        finally { cts.Cancel(); }
    }

    [Fact]
    public async Task A_patch_that_worked_is_learned_and_the_second_identical_interrupt_makes_no_llm_call()
    {
        var llm = new PatchLlm { Patch = _ => ("carry.k = (carry.k || 0) + 1", "resume", "carry on") };
        var (h, _, b) = Bot(llm);
        b.Library.Save("loopy", Loop);

        var s1 = await Running(h, b);
        h.Runtime.Events.Post(GameEvent.LowHealth(5, cause: "zombie"));
        await s1.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var handler = Assert.Single(b.Library.Get("loopy")!.Script!.Handlers);
        Assert.Equal("lowhp", handler.Kind);
        Assert.Equal(1, handler.Successes);
        Assert.Contains("carry.k", handler.Js);
        Assert.Equal(1, llm.Patches);

        var s2 = await Running(h, b);
        h.Runtime.Events.Post(GameEvent.LowHealth(4, cause: "zombie")); // the same situation (numbers differ)
        await s2.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("k=1", s2.Outcome);
        Assert.Equal(1, llm.Patches);                          // no LLM call the second time
        var rec = await Record(h, 2);
        Assert.Equal("learned", rec.Path);
        Assert.Equal(0, rec.Calls);
        Assert.Equal(2, b.Library.Get("loopy")!.Script!.Handlers[0].Successes);

        // The Explorer's view: the handler is a child of its script.
        var entry = Assert.Single(Agent.Routines.ScriptLibraryReport.List([b.Library.Get("loopy")!]));
        Assert.Equal("lowhp", Assert.Single(entry.Handlers!).Kind);
    }

    [Fact]
    public async Task A_handler_that_fails_twice_is_demoted_and_the_patch_turn_runs_again()
    {
        var llm = new PatchLlm { Patch = _ => ("carry.k = 7", "resume", "fresh patch") };
        var (h, _, b) = Bot(llm);
        b.Library.Save("loopy", Loop);
        var s0 = await Running(h, b);
        h.Runtime.Events.Post(GameEvent.LowHealth(5, cause: "zombie"));
        await s0.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var r = b.Library.Get("loopy")!;
        r.Script!.Handlers[0].Js = "throw new Error('stale handler')";
        b.Library.Memory.SaveRoutine(r);

        for (int i = 0; i < 2; i++)
        {
            var s = await Running(h, b);
            h.Runtime.Events.Post(GameEvent.LowHealth(5, cause: "zombie"));
            await Rig.Until(() => s.Status == ScriptStatus.Paused, 8000); // the failed handler escalates to the brain
            Assert.Contains("patch escalated", s.State());
            s.Stop("test");
            await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var h0 = b.Library.Get("loopy")!.Script!.Handlers[0];
        Assert.True(h0.Demoted);
        Assert.Equal(2, h0.Failures);
        Assert.Equal(1, llm.Patches);

        var s3 = await Running(h, b);
        h.Runtime.Events.Post(GameEvent.LowHealth(5, cause: "zombie"));
        await s3.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, llm.Patches);                // demoted: the patch turn ran again
        Assert.Contains("k=7", s3.Outcome);
        Assert.False(b.Library.Get("loopy")!.Script!.Handlers[0].Demoted); // and its fix replaced the handler
    }

    [Fact]
    public async Task Only_opus_writes_patches_a_non_opus_brain_still_runs_saved_handlers()
    {
        var llm = new PatchLlm { Patch = _ => ("carry.k = 3", "resume", null) };
        var (h, _, b) = Bot(llm, configure: o => o.Llm.ApiKey = "test-key");
        b.Library.Save("loopy", Loop);
        var s0 = await Running(h, b);
        h.Runtime.Events.Post(GameEvent.LowHealth(5, cause: "zombie"));
        await s0.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, llm.Patches);

        Assert.Null(h.Runtime.Options.Llm.ApplyBrainProfile("haiku")); // this brain is no longer Opus
        Assert.NotNull(b.Patcher.Agent!.Refusal());
        var s1 = await Running(h, b);
        h.Runtime.Events.Post(GameEvent.LowHealth(5, cause: "zombie"));
        await s1.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("k=3", s1.Outcome);          // the saved handler ran
        Assert.Equal(1, llm.Patches);                // no new patch written

        // A new situation with no handler: no patch turn off-Opus, the interrupt goes to the brain as before.
        var s2 = await Running(h, b);
        h.Runtime.Events.Post(GameEvent.VoidFall(10));
        await Rig.Until(() => s2.Status == ScriptStatus.Paused, 8000);
        Assert.Equal(1, llm.Patches);
        s2.Stop("test");
    }

    [Fact]
    public void The_patch_tool_is_not_in_the_brains_tool_list()
    {
        var (h, _, _) = BotScriptTests.Bot(llm: new PatchLlm()); // the default bot the shared brain budget test measures
        var brain = h.Runtime.Tools.Definitions(ModelTier.Main);
        Assert.DoesNotContain(brain, d => d.Name == PatchAgent.ToolName);
        Assert.DoesNotContain(h.Runtime.Tools.Definitions(ModelTier.Worker), d => d.Name == PatchAgent.ToolName);
        int tokens(LlmToolDefinition d) => (int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6;
        Assert.True(tokens(PatchAgent.Tool) <= 140, $"patch tool ≈{tokens(PatchAgent.Tool)} tok");
    }

    [Fact]
    public async Task Someone_elses_danger_and_unfixable_failures_skip_the_patch_turn()
    {
        var llm = new PatchLlm { Patch = _ => ("log('x')", "resume", null) };
        var (_, _, b) = Bot(llm);
        var s = b.Controller.Start(new ScriptRequest { Name = "t", Code = "wait(5)", Language = ScriptLanguage.Js }).Session!;
        Assert.False(b.Patcher.Wants(s, new ScriptInterrupt("danger", "Robin, a zombie is attacking you", "kiwi")));
        Assert.False(b.Patcher.Wants(s, new ScriptInterrupt("failed", "mined nothing")));
        Assert.True(b.Patcher.Wants(s, new ScriptInterrupt("failed", "deposit: all 3 known chests full")));
        Assert.True(b.Patcher.Wants(s, new ScriptInterrupt("lowhp", "hp 5/20")));
        s.Stop("test");
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Chat_tweaks_are_recognized_conservatively()
    {
        string[] ps = ["count", "chest"];
        Assert.True(ScriptPatcher.LooksLikeTweak("make it 32 instead", ps));
        Assert.True(ScriptPatcher.LooksLikeTweak("use the other chest", ps));
        Assert.False(ScriptPatcher.LooksLikeTweak("how's it going?", ps));
        Assert.False(ScriptPatcher.LooksLikeTweak("nice work", ps));
    }

    /// <summary>
    /// Latency benchmark with a fake Opus (every call 250 ms): the full-turn path (a brain turn: wake, decide, script op=resume,
    /// final round) against the patch path (one patch call). Both measured by the same interrupt tracker.
    /// </summary>
    [Fact]
    public async Task Benchmark_the_patch_path_makes_fewer_calls_and_resumes_sooner_than_the_full_turn()
    {
        var delay = TimeSpan.FromMilliseconds(250);
        // Full turn (patches off, the old path): the brain wakes on the pause and resumes the script.
        var full = new PatchLlm { Delay = delay }.BrainTool("script", new { op = "resume" }).BrainText("");
        var (hf, _, bf) = Bot(full, new ScriptOptions { Patches = false, MaxPauseMinutes = 10 });
        bf.Library.Save("loopy", Loop);
        var cf = new CancellationTokenSource();
        _ = hf.Runtime.Agent.RunAsync(cf.Token);
        InterruptRecord fr;
        try
        {
            var s = await Running(hf, bf);
            hf.Runtime.Events.Post(GameEvent.LowHealth(5, cause: "zombie"));
            fr = await Record(hf, 1);
            await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { cf.Cancel(); }

        // Patch path.
        var patch = new PatchLlm { Delay = delay, Patch = _ => ("wait(0.05)", "resume", "waited") };
        var (hp, _, bp) = Bot(patch);
        bp.Library.Save("loopy", Loop);
        var cp = new CancellationTokenSource();
        _ = hp.Runtime.Agent.RunAsync(cp.Token);
        InterruptRecord pr;
        try
        {
            var s = await Running(hp, bp);
            hp.Runtime.Events.Post(GameEvent.LowHealth(5, cause: "zombie"));
            pr = await Record(hp, 1);
            await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { cp.Cancel(); }

        Assert.Equal("full", fr.Path);
        Assert.Equal("patch", pr.Path);
        Assert.True(fr.Calls >= 2, $"full turn: {InterruptJsonlWriter.Serialize(fr)} all: {string.Join(" ; ", hf.Runtime.Interrupts.Recent().Select(InterruptJsonlWriter.Serialize))}");
        Assert.Equal(1, pr.Calls);
        Assert.True(pr.Ms < fr.Ms, $"patch {pr.Ms} ms vs full {fr.Ms} ms");
        Assert.True(pr.InputTokens + pr.CacheReadTokens < fr.InputTokens + fr.CacheReadTokens, "patch context is smaller");
        Console.WriteLine($"benchmark: full {fr.Ms} ms / {fr.Calls} calls vs patch {pr.Ms} ms / {pr.Calls} call");
    }
}
