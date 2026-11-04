using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Human;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Agent.Worker;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins the delegate worker loop (WP06 refactor of <c>WorkerAgent.RunAsync</c>): for scripted tasks, every request the
/// Anthropic client would send (system, tools, messages, cache_control, model/profile/max tokens/thinking) plus the outcome
/// (ok, summary, calls, tool calls, cost, stop reason), the trail, the Asked/Finished events and the delegate tool's result.
/// The dumps are hashed (line-ending independent) against <see cref="Expected"/>; on a mismatch the dump lands in the temp dir.
/// </summary>
public class WorkerPayloadGoldenTests
{
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["hooks_ok"] = "EE9464D1FD190D6B73F047ACDF1956D801E3D8548E21778748EB2EF27EC04398",
        ["max_tokens"] = "5F713E63D4A05755C95BA32E79FB9BE5BB3B976F8CB49F385ED409B5775BD87E",
        ["needs_default"] = "67EF663D4713E5C0441CD57A8F7A790EE773FCF16BBC8CF57A96E4F49BB64E47",
        ["repeated_failure"] = "A55CC5206140D3E4E5623049ABCF3F3535F0E4A71AB338950DC3DF3044984FC4",
        ["protection"] = "B05BDF95FC69C1B6AA5CDCAE6FC5D63FEF245E4759BAFF59E209EAA3919A361E",
        ["unknown_tools"] = "727C197539E9A95A133528CB9D29328620384B749132537BB926B988D4C39ED3",
        ["max_rounds_wrap"] = "826866D0B333350FAB807F869FA99D1C25ECB80D3ACB3210FD2D2D70CE89ED82",
        ["wrap_truncated"] = "6B962A1D5CB531EE7F4D6FE532476311B173E2844267008C033FF1DB6BF56A6F",
        ["wrap_tools_only"] = "979C8F059446FD54447C9520C9878811585648778F6D18249B02E1B7C09B7F9D",
        ["cost_cap"] = "35AD9F4777BF4DB56119C42C68AA3CD68E5D9C486E0469394542330C0325F303",
        ["context_full"] = "A6B0310F979D21228AAFD5FE59E6D70B252BEB911AA38694B4A6D395BE94BD59",
        ["refusal"] = "FE20684D0263EF24261DAF6C6A29F352C95738D2C0FAA5E9CE9CF936EB46DD6B",
        ["error"] = "6E61EA6A5A01A1395FB387DC67E2F14BA98A7DAA411BC624008087393ADF48CA",
        ["cancelled"] = "FB08DC16552B6DD2965F243039BEE844EAFD6612B0EEF0A44725EE8FAB980FA9",
        ["delegate_tool"] = "5E35976BE1864E4D7D1A72C500105C7D6E7DD73D430EA0A393B899B70B30909A",
        ["refusals"] = "ABE645B7CB866BF820924F83F20351446CA7108CE803E020DB8AEBF8DA90425A",
        ["profile"] = "1B555E582EDD66C759B7C68C1BB54C9967ED7F66E9CE1AB9C091053AA62E5F3F",
    };

    private sealed class Run
    {
        public required AgentRuntime Rt;
        public required FakeLlm Llm;
        public readonly List<string> Events = [];
        public WorkerAgent W => Rt.Worker!;
    }

    private static Run Create(Action<AgentOptions>? configure = null)
    {
        var (rt, llm, _) = TestRuntime.Create(o => { o.Llm.ApiKey = "sk-test-not-real"; configure?.Invoke(o); }, time: new ManualTime());
        rt.Tools.Register("status", "Status.", ToolSchema.Empty, (_, _) => Task.FromResult(ToolResult.Success("pos 0,64,0 hp 20")), cheapTier: true);
        rt.Tools.Register("mine", "Mine.", ToolSchema.Create().Str("block").Int("count", required: false).Build(),
            (i, _) => Task.FromResult(ToolResult.Success($"mined {i.Int("count") ?? 8} {i.Str("block")}")), longRunning: true);
        rt.ApplyBrainSplit();
        var run = new Run { Rt = rt, Llm = llm };
        rt.Worker!.Finished += o => { lock (run.Events) run.Events.Add($"finished {o.Ok} {o.StopReason} {o.Summary}"); };
        rt.Worker.Asked += (q, d) => { lock (run.Events) run.Events.Add($"asked {q} | {d}"); };
        return run;
    }

    private static LlmToolUse Use(string id, string name, object input) => new(id, name, JsonSerializer.SerializeToElement(input));

    private static string Dump(Run run, WorkerOutcome? o, string? extra = null)
    {
        var client = new AnthropicLlmClient(run.Rt.Options.Llm);
        var sb = new StringBuilder();
        List<LlmRequest> reqs;
        lock (run.Llm.Requests) reqs = [.. run.Llm.Requests];
        foreach (var r in reqs)
        {
            sb.Append("== request tier=").Append(r.Tier).Append(" model=").Append(r.Model ?? "-").Append(" profile=").Append(r.Profile ?? "-")
              .Append(" purpose=").Append(r.Purpose).Append(" thinking=").Append(r.Thinking ?? "-")
              .Append(" maxTokens=").Append(r.MaxTokens?.ToString() ?? "-").Append('\n');
            sb.Append(JsonSerializer.Serialize(client.BuildParams(r, r.Model ?? run.Rt.Options.Llm.ModelFor(r.Tier)))).Append('\n');
        }
        if (o != null)
            sb.Append("== outcome ").Append(o.Ok).Append(" calls=").Append(o.Calls).Append(" tools=").Append(o.ToolCalls)
              .Append(" cost=").Append(o.CostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" stop=").Append(o.StopReason)
              .Append(" needs=").Append(o.NeedsInput).Append('\n').Append(o.Summary).Append('\n');
        sb.Append("== trail ").Append(run.W.Trail ?? "-").Append('\n');
        sb.Append("== running ").Append(run.W.IsRunning).Append(" live ").Append(run.W.LastLiveAmendment ?? "-").Append('\n');
        lock (run.Events) foreach (var e in run.Events) sb.Append("> ").Append(e).Append('\n');
        if (extra != null) sb.Append("== extra\n").Append(extra).Append('\n');
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\\r\\n", "\\n", StringComparison.Ordinal);
    }

    private static void Check(string name, string dump)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden", "worker-payloads");
        if (!Expected.TryGetValue(name, out var want) || want != hash)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".txt"), dump);
        }
        Assert.True(Expected.TryGetValue(name, out want), $"no golden for {name}: [\"{name}\"] = \"{hash}\",");
        Assert.True(want == hash, $"{name} payload changed: {hash} (dump in {dir})");
    }

    [Fact]
    public async Task Tool_round_with_hooks_amendments_and_ok()
    {
        var run = Create();
        var w = run.W;
        w.StateHeader = () => "pos 1,64,2 hp 20/20 food 20 held air";
        w.RoundNote = _ => Task.FromResult<string?>("  [vision] cobble gen 3 blocks north  ");
        w.GainsTracker = () => () => "8 cobblestone";
        run.Rt.Tools.Register("look", "Look.", ToolSchema.Empty, (_, _) =>
        {
            w.Amend("and bring me 10 sand too");
            w.AmendFromChat("fox", "just like 5");
            return Task.FromResult(ToolResult.Success("cobble gen north"));
        });
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.ToolUse, new LlmText("looking"), Use("w1", "look", new { })))
               .Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w2", "mine", new { block = "cobblestone", count = 5 }), Use("w3", "status", new { })))
               .Then(r => run.Llm.Resp(r, StopReasons.EndTurn, new LlmText("  "), new LlmText("Mined it. ok: mined 5 cobblestone, inv 5")));
        var o = await w.RunAsync("mine 5 cobblestone at the gen", "5 cobblestone in inventory", 3, default);
        Check("hooks_ok", Dump(run, o));
    }

    [Fact]
    public async Task Max_tokens_mid_task()
    {
        var run = Create();
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.MaxTokens, new LlmText("mining"), Use("w1", "mine", new { block = "dirt" }), Use("w2", "status", new { })));
        var o = await run.W.RunAsync("mine dirt", null, null, default);
        Check("max_tokens", Dump(run, o));
    }

    [Fact]
    public async Task Needs_input_with_a_default_carries_on_then_ends()
    {
        var run = Create();
        run.W.Questions = new QuestionOptions();
        run.Llm.ThenText("Two chests here. needs_input: which chest, the west or east one? default: the west one.")
               .ThenText("needs_input: should I break the wall to get in (default: yes)")
               .ThenText("needs_input: put the extra cobble in 4,64,6? default: yes")
               .ThenText("unreachable");
        var o = await run.W.RunAsync("store the cobble", null, null, default);
        Check("needs_default", Dump(run, o));
    }

    [Fact]
    public async Task The_same_failure_twice_ends_the_task()
    {
        var run = Create();
        run.Rt.Tools.Register("fill_bucket", "Fill.", ToolSchema.Create().Str("at").Build(),
            (i, _) => Task.FromResult(ToolResult.Error($"no water source at {i.Str("at")}")));
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w1", "fill_bucket", new { at = "1,64,-9" })))
               .Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w2", "fill_bucket", new { at = "3,64,-2" }), Use("w3", "fill_bucket", new { at = "5,64,-2" })))
               .ThenText("unreachable");
        var o = await run.W.RunAsync("fill a bucket", null, null, default);
        Check("repeated_failure", Dump(run, o));
    }

    [Fact]
    public async Task A_protection_refusal_ends_the_task_as_is()
    {
        var run = Create();
        run.Rt.Tools.Register("dig", "Dig.", ToolSchema.Create().Str("at").Build(),
            (_, _) => Task.FromResult(ToolResult.Error("oak_log at 4,65,2 is protected (fox's build); say 'clawd you can break that'")));
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w1", "dig", new { at = "4,65,2" }), Use("w2", "status", new { })))
               .ThenText("unreachable");
        var o = await run.W.RunAsync("chop the tree", null, null, default);
        Check("protection", Dump(run, o));
    }

    [Fact]
    public async Task Unknown_and_brain_only_tools_are_refused()
    {
        var run = Create();
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w1", "fly", new { }), Use("w2", WorkerAgent.ToolName, new { task = "x" })))
               .Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w3", "fly", new { to = "up" })))
               .ThenText("cannot fly");
        var o = await run.W.RunAsync("fly up", null, null, default);
        Check("unknown_tools", Dump(run, o));
    }

    [Fact]
    public async Task Out_of_rounds_wraps_up_with_one_summary()
    {
        var run = Create(o => o.Worker.MaxRounds = 2);
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w1", "mine", new { block = "cobblestone" })))
               .Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w2", "mine", new { block = "cobblestone" })))
               .ThenText("failed: mined 16 of 64 cobblestone, gen too slow");
        var o = await run.W.RunAsync("mine 64 cobble", "64 cobble", null, default);
        Check("max_rounds_wrap", Dump(run, o));
    }

    [Fact]
    public async Task A_truncated_wrap_up()
    {
        var run = Create(o => o.Worker.MaxRounds = 1);
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w1", "mine", new { block = "cobblestone" })))
               .Then(r => run.Llm.Resp(r, StopReasons.MaxTokens, new LlmText("summary"), Use("w2", "mine", new { block = "sand" })));
        var o = await run.W.RunAsync("mine cobble", null, null, default);
        Check("wrap_truncated", Dump(run, o));
    }

    [Fact]
    public async Task A_wrap_up_without_text_keeps_the_stop_summary()
    {
        var run = Create(o => o.Worker.MaxRounds = 1);
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w1", "mine", new { block = "cobblestone" })))
               .Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w2", "mine", new { block = "sand" })));
        var o = await run.W.RunAsync("mine cobble", null, null, default);
        Check("wrap_tools_only", Dump(run, o));
    }

    [Fact]
    public async Task The_cost_cap_stops_the_task()
    {
        var run = Create(o => o.Worker.MaxCostUsd = 0.0005m);
        run.Llm.UsagePerCall = new LlmUsage(100, 100, 0, 0);
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w1", "mine", new { block = "dirt" })))
               .ThenText("unprefixed summary of the dirt");
        var o = await run.W.RunAsync("mine", null, null, default);
        Check("cost_cap", Dump(run, o));
    }

    [Fact]
    public async Task A_tiny_context_ends_the_task()
    {
        var run = Create(o => o.Worker.MaxContextTokens = 50);
        var o = await run.W.RunAsync("mine", null, null, default);
        Check("context_full", Dump(run, o));
    }

    [Fact]
    public async Task A_refusal_ends_the_task()
    {
        var run = Create();
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.Refusal, new LlmText("needs_input: no (default: nothing)"), Use("w1", "mine", new { block = "dirt" })));
        run.W.Questions = new QuestionOptions();
        var o = await run.W.RunAsync("mine", null, null, default);
        Check("refusal", Dump(run, o));
    }

    [Fact]
    public async Task A_model_error_fails_the_task()
    {
        var run = Create();
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w1", "mine", new { block = "dirt" })))
               .Then(_ => throw new InvalidOperationException("overloaded\nretry later"));
        var o = await run.W.RunAsync("mine", null, null, default);
        Check("error", Dump(run, o));
    }

    [Fact]
    public async Task Cancelled_mid_tool()
    {
        var run = Create();
        using var cts = new CancellationTokenSource();
        run.Rt.Tools.Register("wait_here", "Wait.", ToolSchema.Empty, async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Success("never");
        });
        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w1", "mine", new { block = "dirt" })))
               .Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w2", "wait_here", new { })));
        var o = await run.W.RunAsync("mine", null, null, cts.Token);
        Check("cancelled", Dump(run, o));
    }

    [Fact]
    public async Task Budget_and_profile_refusals()
    {
        var run = Create(o => { o.Budget.DailyCapUsd = 1m; o.Budget.MonthlyCapUsd = 100m; o.Budget.CheapOnlyFraction = 0.8; });
        var sb = new StringBuilder();
        var bad = await run.W.RunAsync("mine", null, null, default, profile: "nope");
        sb.Append(bad).Append('\n');
        run.Rt.Usage.Record("claude-opus-5-5", new LlmUsage(0, 42_500, 0, 0));
        var budget = await run.W.RunAsync("mine", null, null, default);
        sb.Append(budget).Append('\n');
        Check("refusals", Dump(run, null, sb.ToString()));
    }

    [Fact]
    public async Task A_profile_runs_the_loop_on_its_model()
    {
        var run = Create(o => o.Llm.Profiles["quick"] = new LlmProfile { Model = "claude-haiku-5-5", MaxTokens = 900, Thinking = "off" });
        run.Llm.ThenText("ok: done").ThenText("ok: on the worker model");
        var o1 = await run.W.RunAsync("sort the chest", null, 50, default, profile: " quick ");
        var o2 = await run.W.RunAsync("sort the chest", null, 0, default, profile: "opus");
        Check("profile", Dump(run, o2, o1.ToString()));
    }

    [Fact]
    public async Task The_delegate_tool_annotates_and_counts_actions()
    {
        var run = Create();
        run.W.Landmark = (x, y, z) => x == 4 ? "the west gen cell" : null;
        Assert.True(run.Rt.Tools.TryGet(WorkerAgent.ToolName, out var del));
        Assert.True(run.Rt.Tools.TryGet(WorkerAgent.AmendToolName, out var amend));
        var sb = new StringBuilder();
        sb.Append(JsonSerializer.Serialize(run.Rt.Tools.Definitions(ModelTier.Main).Where(d => d.Name is WorkerAgent.ToolName or WorkerAgent.AmendToolName)
            .Select(d => new { d.Name, d.Description, Schema = d.InputSchema.GetRawText() }))).Append('\n');
        var idle = await run.Rt.Tools.ExecuteAsync(amend, JsonSerializer.SerializeToElement(new { note = "more" }), default);
        sb.Append(idle.Ok).Append(' ').Append(idle.Text).Append('\n');

        run.Llm.Then(r => run.Llm.Resp(r, StopReasons.ToolUse, Use("w1", "mine", new { block = "cobblestone" })))
               .ThenText("needs_input: which chest, 4,64,6 or 9,64,1?");
        var r1 = await run.Rt.Tools.ExecuteAsync(del, JsonSerializer.SerializeToElement(new { task = "store cobble", done_when = "stored" }), default);
        sb.Append(r1.Ok).Append(' ').Append(r1.Text).Append('\n');
        run.Llm.ThenText("ok: stored 8 cobblestone");
        var r2 = await run.Rt.Tools.ExecuteAsync(del, JsonSerializer.SerializeToElement(new { task = "store cobble", done_when = "stored", max_minutes = 2 }), default);
        sb.Append(r2.Ok).Append(' ').Append(r2.Text).Append('\n');
        var r3 = await run.Rt.Tools.ExecuteAsync(del, JsonSerializer.SerializeToElement(new { task = "store cobble", done_when = "stored", profile = "nope" }), default);
        sb.Append(r3.Ok).Append(' ').Append(r3.Text).Append('\n');
        Check("delegate_tool", Dump(run, null, sb.ToString()));
    }
}
