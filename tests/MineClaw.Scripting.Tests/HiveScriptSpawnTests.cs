using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Tests;
using MineClaw.Hive;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;

namespace MineClaw.Scripting.Tests;

/// <summary>Model profiles for clones and delegates, and clones started directly on a script with their brain asleep.</summary>
public class HiveScriptSpawnTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class IdleProcess : ICloneProcess
    {
        public int Id => 4242;
        public bool HasExited => false;
        public int? ExitCode => null;
        public Task WaitForExitAsync(CancellationToken ct = default) => Task.Delay(Timeout.Infinite, ct);
        public void Kill() { }
        public void Dispose() { }
    }

    private sealed class RecordingLauncher : IProcessLauncher
    {
        public List<string> Started { get; } = new();
        public ICloneProcess Start(string clone, string pipeName) { lock (Started) Started.Add(clone); return new IdleProcess(); }
    }

    private static (BotHost H, ScriptBridge B, HiveSupervisor Sup, RecordingLauncher L) MainHive(Action<AgentOptions>? configure = null)
    {
        var (h, _, b) = BotScriptTests.Bot(o => { o.Llm.ApiKey = "sk-ant-test"; configure?.Invoke(o); });
        var cfg = new HiveConfig { CloneUsernames = ["maid_chan_67", "maid_chan_69"], StartTimeoutSeconds = 600 };
        var launcher = new RecordingLauncher();
        var sup = new HiveSupervisor(cfg, "maid_chan", launcher, new ClaimBoard(TimeSpan.FromSeconds(60)),
            new BudgetLedger(new BudgetOptions(), cfg.Budget, () => (0m, 0m)), new ChatArbiter("maid_chan", "maid_chan", cfg.Clones()), null,
            () => new SlotFacts(20, ["maid_chan"]), []);
        // Same wiring as the App's MainHive.
        var llm = h.Runtime.Options.Llm;
        sup.ProfileCheck = name => llm.CheckProfile(name);
        sup.ScriptPrep = (script, code) => b.PrepareForHive(script, code, ToolCaller.Current?.Tag);
        HiveTools.RegisterMain(h.Runtime.Tools, sup, llm);
        return (h, b, sup, launcher);
    }

    private const string Loop = "cp('start')\nmine('cobblestone', 2)\nwait(0.3)\ncp('half')\nmine('cobblestone', 2)\nreturn 'mined 4'";

    [Fact]
    public async Task Inline_code_is_opus_brain_only_and_lint_failures_block_the_spawn()
    {
        var (h, b, sup, launcher) = MainHive();
        await using var _ = sup;
        var spawn = new { op = "spawn", name = "67", script = "cobble_loop", code = Loop, profile = "haiku", after = "sleep" };

        var worker = await h.RunAs(ModelTier.Worker, "hive", spawn);
        Assert.False(worker.Ok);
        Assert.Equal(ScriptPrompts.BrainOnly, worker.Text);
        var none = await h.RunAs(null, "hive", spawn);
        Assert.False(none.Ok);
        Assert.Empty(launcher.Started);

        var lint = await h.Run("hive", new { op = "spawn", name = "67", code = "eval('rm -rf /')\nmine('cobblestone', 4)" });
        Assert.False(lint.Ok);
        Assert.StartsWith("error: lint errors", lint.Text); // the findings go back to main's brain
        output.WriteLine(lint.Text);
        Assert.Empty(launcher.Started);

        var ok = await h.Run("hive", spawn);
        Assert.True(ok.Ok, ok.Text);
        Assert.Equal(["maid_chan_67"], launcher.Started);
        var task = sup.Slot("maid_chan_67")!.Task!;
        Assert.Equal(("cobble_loop", "haiku", "sleep"), (task.Script, task.Profile, task.After));
        Assert.Contains("pickaxe", task.Loadout!); // the code mines: fetch a pickaxe first
        Assert.Equal("main/claude-opus-5-5", b.Library.Get("cobble_loop")!.Script!.Versions[^1].Author);
        Assert.StartsWith("67: script cobble_loop 0% (brain asleep, profile haiku)", sup.List());
    }

    [Fact]
    public async Task Unknown_unconfigured_or_missing_profiles_and_scripts_are_rejected()
    {
        var (h, _, sup, launcher) = MainHive(o => o.Llm.Profiles["local"] = new LlmProfile { Provider = "ollama", Model = "llama3.2" });
        await using var _ = sup;
        var nope = await h.Run("hive", new { op = "spawn", task = "mine", profile = "nope" });
        Assert.Contains("no profile 'nope'", nope.Text);
        var local = await h.Run("hive", new { op = "spawn", task = "mine", profile = "local" });
        Assert.Contains("needs Agent:Llm:Ollama:Endpoint", local.Text); // never used while the endpoint is empty
        var missing = await h.Run("hive", new { op = "spawn", script = "no_such_script" });
        Assert.False(missing.Ok);
        Assert.Contains("no_such_script", missing.Text);
        Assert.Empty(launcher.Started);
    }

    [Fact]
    public async Task A_clone_on_the_haiku_profile_thinks_on_haiku_and_can_run_but_not_write_scripts()
    {
        var llm = new CountingLlm();
        var (h, c, b) = BotScriptTests.Bot(o =>
        {
            o.Llm.ApiKey = "sk-ant-test";
            Assert.Null(o.Llm.ApplyBrainProfile("haiku")); // what CloneMode does with the spawn's profile
        }, llm: llm);
        var rt = h.Runtime;
        Assert.Equal("claude-haiku-5-5", rt.Options.Llm.ModelFor(ModelTier.Main));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loop = Task.Run(() => rt.Agent.RunAsync(cts.Token));
        rt.Events.Post(GameEvent.Chat("poofimafox", "can you mine some cobble for the furnace?")); // needs the brain (not cheap-only small talk)
        await Until(() => llm.Brain > 0);
        var brain = llm.Requests.First(r => r.Tier == ModelTier.Main);
        Assert.Equal("haiku", rt.Options.Llm.ProfileOfModel(rt.Options.Llm.ModelFor(brain.Tier)));

        var write = await h.Run("script", new { op = "run", name = "x", code = "mine('cobblestone', 1)" }); // as this brain
        Assert.False(write.Ok);
        Assert.Equal(ScriptPrompts.BrainOnly, write.Text);
        b.Library.SaveVersion("dig1", "mine('cobblestone', 1)\nreturn 'dug'", author: "main/claude-opus-5-5");
        var run = await h.Run("script", new { op = "run", name = "dig1" });
        Assert.True(run.Ok, run.Text);
        Assert.Equal("haiku", rt.Options.Llm.MainProfile);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
    }

    private static async Task Until(Func<bool> cond, double seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("condition not met");
            await Task.Delay(25);
        }
    }

    private sealed record Result(bool Ok, string Text, bool Logout);

    private static (BotHost H, ScriptBridge B, CountingLlm Llm, CloneScriptRunner Runner, List<Result> Results, List<string> Progress) Clone(string code)
    {
        var llm = new CountingLlm();
        var (h, _, b) = BotScriptTests.Bot(o => o.Chat.Username = "maid_chan_67", llm: llm);
        b.Library.SaveVersion("cobble_loop", code, author: "main/claude-opus-5-5");
        var results = new List<Result>();
        var progress = new List<string>();
        var runner = new CloneScriptRunner(h.Runtime, b,
            (t, _) => { lock (progress) progress.Add(t); return Task.CompletedTask; },
            (ok, t, logout) => { lock (results) results.Add(new(ok, t, logout)); return Task.CompletedTask; },
            poll: TimeSpan.FromMilliseconds(40));
        return (h, b, llm, runner, results, progress);
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("sleep")]
    public async Task A_script_spawn_makes_zero_calls_reports_once_and_honours_after(string after)
    {
        var (h, b, llm, runner, results, _) = Clone(Loop);
        using var __ = runner;
        var rt = h.Runtime;
        var task = new TaskMsg("t1", "script cobble_loop", Script: "cobble_loop", After: after);
        var woke = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var g0 = rt.Events.Gate!;
        rt.Events.Gate = e => { var kept = g0(e); if (kept?.Wakes == true) woke.Enqueue(kept.Kind + ":" + kept.Format()); return kept; };
        runner.Prepare(task); // process start: asleep before anything happens
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var loop = Task.Run(() => rt.Agent.RunAsync(cts.Token));
        rt.Events.Post(GameEvent.Joined("kiwi"));                   // would normally wake the brain
        rt.Events.Post(GameEvent.Notice("[hive] something happened")); // Low notice
        Assert.True(runner.Start(task), b.Controller.Status());
        Assert.Equal("cobble_loop", rt.Memory.GetStandingOrder("maid_chan_67")!.Job.Replace("script ", ""));
        await Until(() => results.Count > 0);
        await Task.Delay(400); // the [task ok] event has passed the gate by now
        var r = Assert.Single(results);
        Assert.True(r.Ok, r.Text);
        Assert.Contains("mined 4", r.Text);
        Assert.Equal(after == "logout", r.Logout);
        Assert.True(llm.Requests.IsEmpty, string.Join(" || ", llm.Requests.Select(q => $"{q.Tier}/{q.Purpose}: {string.Join(" / ", q.Messages.SelectMany(m => m.Content).OfType<LlmText>().Select(t => t.Text))}")) + " WOKE " + string.Join(" | ", woke)); // zero LLM calls
        Assert.Null(rt.Memory.GetStandingOrder("maid_chan_67"));    // a finished script leaves no order behind
        Assert.True(runner.Asleep);
        if (after == "sleep") Assert.Equal("asleep", b.BrainState());

        // Chat addressed to this clone wakes the brain once; then it's asleep again.
        rt.Events.Post(GameEvent.Chat("poofimafox", "67, you done?"));
        await Until(() => llm.Brain == 1);
        await Task.Delay(300);
        rt.Events.Post(GameEvent.Joined("someone"));
        rt.Events.Post(GameEvent.Idle());
        await Task.Delay(400);
        Assert.Equal(1, llm.Brain);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task A_restart_resumes_the_script_from_its_last_checkpoint()
    {
        const string code = "cp('a')\nmine('cobblestone', 1)\ncp('b')\nwait(30)\nreturn 'done'";
        var (h, b, _, runner, _, progress) = Clone(code);
        using var __ = runner;
        var rt = h.Runtime;
        var task = new TaskMsg("t9", "script cobble_loop", Script: "cobble_loop");
        runner.Prepare(task);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var loop = Task.Run(() => rt.Agent.RunAsync(cts.Token)); // the brain takes the stop's [task failed], as live
        Assert.True(runner.Start(task), b.Controller.Status());
        string? Resume() => rt.Memory.GetStandingOrder("maid_chan_67") is { } o && o.Input.TryGetProperty("resume_from", out var r) ? r.GetString() : null;
        await Until(() => Resume() == "b");
        Assert.Contains(progress, p => p.Contains("cp 'b'"));

        // The clone process dies mid-script (stop = gone; the order stays in the shared db) and main restarts it.
        b.Controller.Current!.Stop("crash");
        await Until(() => b.Controller.Current == null);
        runner.Start(task with { Restarted = true }); // may queue until the old activity has let go of the body
        await Until(() => b.Controller.Current != null);
        Assert.Equal("b", b.Controller.Current!.Request.ResumeFrom); // not from scratch
        b.Controller.Current.Stop("test over");
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
    }

    [Fact]
    public void Tool_budget_of_the_profile_and_script_spawn_fields()
    {
        static int Tok(LlmToolDefinition d) => (int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6;
        var (plain, _, _) = BotScriptTests.Bot();
        var (h, _, sup, _) = MainHive();
        var del0 = Tok(plain.Runtime.Tools.Definitions(ModelTier.Main).Single(d => d.Name == "delegate"));
        var del1 = Tok(h.Runtime.Tools.Definitions(ModelTier.Main).Single(d => d.Name == "delegate"));
        var hive = Tok(h.Runtime.Tools.Definitions(ModelTier.Main).Single(d => d.Name == "hive"));
        int brain = h.Runtime.Tools.Definitions(ModelTier.Main).Sum(Tok);
        output.WriteLine($"delegate {del0} → {del1} (+{del1 - del0}); hive with script+profile {hive}; brain total incl. hive {brain}");
        Assert.True(del1 - del0 <= 8, $"delegate profile field ≈{del1 - del0} tok");
        Assert.True(hive <= Agent.Tools.ToolBudgets.HiveTool, $"hive tool ≈{hive} tok");
        Assert.True(brain <= Agent.Tools.ToolBudgets.Brain, $"brain tools with every profile/script field + hive ≈{brain}"); // the one shared cap
        _ = sup.DisposeAsync();
    }
}
