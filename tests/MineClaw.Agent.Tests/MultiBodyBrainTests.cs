using System.Text.Json;
using MineClaw.Agent.Bodies;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Agent.Worker;

namespace MineClaw.Agent.Tests;

/// <summary>
/// One brain, many bodies, step 3 (docs/design/one-brain-many-bodies.md section 2), the Agent side: the [bodies] line and its
/// token cap, the wake digest that coalesces clone events, body addressing in chat, the worker pool's cap, the "[67 task ok]"
/// rendering, and the brain loop running one call on several bodies (one Opus turn, one activity per body).
/// </summary>
public class MultiBodyBrainTests
{
    // ------------------------------------------------------------------ [bodies]

    [Fact]
    public void Status_block_is_capped_at_120_tokens_with_each_body_at_most_25_and_offline_bodies_collapsed()
    {
        var verbose = string.Join(" ", Enumerable.Repeat("harvest_generator #12 @2 E of the cobble_generator by the main house hp18 f12 stone_pickaxe free8", 3));
        var parts = new List<BodyStatusBlock.Part>
        {
            new("main", true, verbose), new("67", true, verbose), new("69", true, verbose), new("420", true, "DANGER hp6 " + verbose),
            new("1337", false, ""), new("42", false, ""),
        };
        var line = BodyStatusBlock.Render(parts, focus: "67");
        Assert.StartsWith("[bodies] ", line);
        Assert.True(BodyStatusBlock.Tokens(line) <= BodyStatusBlock.MaxTokens, $"{BodyStatusBlock.Tokens(line)} tok: {line}");
        foreach (var part in line["[bodies] ".Length..].Split(" | "))
            Assert.True(BodyStatusBlock.Tokens(part) <= BodyStatusBlock.MaxPartTokens, $"part {BodyStatusBlock.Tokens(part)} tok: {part}");
        Assert.DoesNotContain(" 67 ", " " + line + " "); // the focus body keeps its own header instead
        Assert.Contains("main ", line);
        Assert.Contains("420 DANGER", line);
        Assert.EndsWith("1337,42 off", line);

        // The shape the design doc shows fits easily.
        var doc = BodyStatusBlock.Render(
        [
            new("main", true, "idle @main_house hp20 f20"), new("67", true, "gen@cobble_gen 42/64 hp18 f12 stone_pick free8"),
            new("69", true, "idle @main_house hp20 f20"), new("420", true, "DANGER hp6 retreating"), new("1337", false, ""),
        ], focus: null);
        Assert.Equal("[bodies] 67 gen@cobble_gen 42/64 hp18 f12 stone_pick free8 | 69 idle @main_house hp20 f20 | 420 DANGER hp6 retreating | 1337 off", doc);
        // One body online (or none besides the focus): no line at all.
        Assert.Equal("", BodyStatusBlock.Render([new("main", true, "idle"), new("67", false, "")], null));
    }

    // ------------------------------------------------------------------ the wake digest

    [Fact]
    public void Digest_holds_a_crews_results_until_the_last_body_is_done_and_the_batch_reads_them_as_one_line()
    {
        var busy = new HashSet<string> { "67", "69" };
        var digest = new BodyEventDigest(() => null, b => busy.Contains(b));
        var q = new EventQueue { Intake = digest.Apply };
        digest.BeginGroup(["67", "69"]);

        busy.Remove("67");
        q.Post(GameEvent.TaskDone("#3 harvest_generator: dug 64 cobblestone at 5,64,7") with { Body = "67" });
        Assert.False(q.HasWaking); // 69 still mines: no wake yet
        busy.Remove("69");
        q.Post(GameEvent.TaskDone("#4 harvest_generator: dug 64 cobblestone at 7,64,7") with { Body = "69" });
        Assert.True(q.HasWaking);
        var batch = q.TryTakeBatch()!;
        var text = batch.Format();
        Assert.Equal("[bodies] 67 ok: dug 64 cobblestone at 5,64,7; 69 ok: dug 64 cobblestone at 7,64,7", text);
        Assert.Single(text.Split('\n'));
    }

    [Fact]
    public void Digest_lets_danger_and_must_wake_through_folds_minor_events_and_rate_limits_a_non_focus_body()
    {
        var time = new ManualTime();
        var digest = new BodyEventDigest(() => "69", _ => false, time);
        // Danger and MustWake: always, at once.
        Assert.True(digest.Apply(GameEvent.LowHealth(5) with { Body = "67" })!.Wakes);
        Assert.True(digest.Apply(GameEvent.TaskFail("#2 mine: no pickaxe") with { Body = "67", MustWake = true })!.Wakes);
        // A minor event of a non-focus body rides along.
        Assert.False(digest.Apply(GameEvent.Notice("joined a gen cell") with { Body = "67" })!.Wakes);
        // A normal event of a non-focus body: once per 20 s.
        Assert.True(digest.Apply(GameEvent.TaskDone("#5 deposit: ok") with { Body = "67" })!.Wakes);
        Assert.False(digest.Apply(GameEvent.TaskDone("#6 craft: ok") with { Body = "67" })!.Wakes);
        time.Advance(TimeSpan.FromSeconds(21));
        Assert.True(digest.Apply(GameEvent.TaskDone("#7 craft: ok") with { Body = "67" })!.Wakes);
        // A player whispering to a body always wakes (addressed chat is never rate-limited).
        Assert.True(digest.Apply(GameEvent.Whisper("Fox", "come here") with { Body = "67" })!.Wakes);
        Assert.True(digest.Apply(GameEvent.Whisper("Fox", "now please") with { Body = "67" })!.Wakes);
        // The focus body and brain-level events are untouched.
        Assert.True(digest.Apply(GameEvent.Notice("hi") with { Body = "69" })!.Wakes);
        Assert.True(digest.Apply(GameEvent.Notice("budget fine"))!.Wakes);
    }

    [Fact]
    public void Body_events_render_with_their_body_and_main_or_hive_events_as_before()
    {
        Assert.Equal("[67 task ok] #3 mine: ok", (GameEvent.TaskDone("#3 mine: ok") with { Body = "67" }).Format());
        Assert.Equal("[69 DANGER] hp 5/20", (GameEvent.LowHealth(5) with { Body = "maid_chan_69" }).Format());
        Assert.Equal("[task ok] #3 mine: ok", GameEvent.TaskDone("#3 mine: ok").Format());
        Assert.Equal("[task ok] #3 mine: ok", (GameEvent.TaskDone("#3 mine: ok") with { Body = "main" }).Format());
        Assert.Equal("[note] [bodies] 67 crashed", (GameEvent.Notice("[bodies] 67 crashed") with { Body = "67" }).Format());
        // A single body's result is its own line (no coalescing with one).
        var one = new EventBatch([GameEvent.TaskDone("#3 mine: ok") with { Body = "67" }, GameEvent.Chat("Fox", "nice")]);
        Assert.Equal("[67 task ok] #3 mine: ok\n[chat] Fox: nice", one.Format().ReplaceLineEndings("\n"));
    }

    // ------------------------------------------------------------------ addressing

    [Theory]
    [InlineData("67, come here", "67")]
    [InlineData("hey 69 bring me wood", "69")]
    [InlineData("67 and 69 mine the gen", "67,69")]
    [InlineData("maid chan 420 come here", "420")]
    [InlineData("#1337 stop", "1337")]
    [InlineData("good job 67!", "67")]
    [InlineData("also 69, that chest is yours", "69")]
    [InlineData("get 67 cobble", "")]
    [InlineData("I counted 69!", "")]
    [InlineData("go to 6,67,8", "")]
    [InlineData("with 67?", "")]
    [InlineData("Clawd come here", "main")]
    [InlineData("main spin", "main")]
    public void Addressing_finds_the_bodies_a_line_names(string text, string expected)
    {
        var a = new BodyAddressing("Clawd_bot", ["Clawd"], ["maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"]);
        var got = a.Addressed(text).Select(n => n == "Clawd_bot" ? "main" : BodyAddressing.NumberOf(n)!);
        Assert.Equal(expected, string.Join(",", got));
    }

    [Fact]
    public void Turn_focus_is_the_named_body_a_group_address_is_main_else_the_standing_focus()
    {
        var dir = new FakeDirectory(TestRuntime.Create().Rt, "67", "69") { Standing = "69" };
        EventBatch B(string text) => new([GameEvent.Chat("Fox", text)]);
        Assert.Equal("67", BodyFocus.For(B("67, what are you doing"), dir));
        Assert.Null(BodyFocus.For(B("both of you come here"), dir));
        Assert.Null(BodyFocus.For(B("Clawd come here"), dir));
        Assert.Equal("69", BodyFocus.For(B("nice weather"), dir));
        Assert.Equal("67", BodyFocus.For(new EventBatch([GameEvent.Whisper("Fox", "come here") with { Body = "67" }]), dir));
    }

    // ------------------------------------------------------------------ the worker pool

    [Fact]
    public async Task Worker_pool_runs_one_worker_per_body_up_to_the_cap_and_queues_the_rest()
    {
        var (rt, _, _) = TestRuntime.Create();
        string? current = null;
        var configured = new List<string>();
        var pool = new WorkerPool(rt.Worker!, 2, () => current, (b, _) => configured.Add(b));
        var queued = new List<string>();
        pool.Queued += (_, text) => { lock (queued) queued.Add(text); };

        Assert.Same(rt.Worker, pool.For(null));
        Assert.Same(rt.Worker, pool.For("main"));
        var w67 = pool.For("67");
        Assert.NotSame(rt.Worker, w67);
        Assert.Same(w67, pool.For("67"));
        Assert.Equal(rt.Worker!.SystemPrompt[0].Text, w67.SystemPrompt[0].Text); // one shared cached worker prefix
        Assert.Equal(["67"], configured);

        var gates = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        int started = 0;
        Task<int> Run(string body, int i) => pool.RunAsync(body, async () => { Interlocked.Increment(ref started); return await gates[i].Task; }, CancellationToken.None);
        var a = Run("main", 0);
        var b = Run("67", 1);
        var c = Run("69", 2);
        await TestWaits.UntilAsync(() => Volatile.Read(ref started) == 2 && pool.Waiting == 1);
        Assert.Equal(2, pool.Running);
        Assert.Equal(["worker queued for 69, 1 ahead"], queued);
        gates[0].SetResult(1);
        Assert.Equal(1, await a);
        await TestWaits.UntilAsync(() => Volatile.Read(ref started) == 3); // the queued run takes the freed slot
        gates[1].SetResult(2);
        gates[2].SetResult(3);
        Assert.Equal(2, await b);
        Assert.Equal(3, await c);
        Assert.Equal(0, pool.Running);
    }

    // ------------------------------------------------------------------ the brain loop over several bodies

    private static readonly AsyncLocal<string?> Scope = new();

    /// <summary>Three bodies: main (the runtime's slot) and two clones with their own slots, scope = an AsyncLocal name.</summary>
    private sealed class FakeDirectory : IBodyDirectory
    {
        private readonly AgentRuntime _rt;
        public readonly Dictionary<string, ActivityManager> Slots = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<IReadOnlyList<string>> Crews = new();
        public string? Standing;
        private readonly BodyAddressing _addr;

        public FakeDirectory(AgentRuntime rt, params string[] clones)
        {
            _rt = rt;
            foreach (var c in clones)
            {
                var name = c;
                Slots[c] = new ActivityManager(rt.Tools, rt.Events) { Body = c, StepScope = () => Push(name) };
            }
            _addr = new BodyAddressing(rt.Chat.Username, [rt.Chat.DisplayName], clones.Select(c => "maid_chan_" + c));
        }

        public bool Active => true;
        public IReadOnlyList<string> Online() => ["main", .. Slots.Keys];
        public IReadOnlyList<string> Known() => Online();
        public string? Canonical(string? name) => name is "main" ? "main" : name != null && Slots.ContainsKey(name) ? name : null;
        public ActivityManager? Activities(string body) => body == "main" ? _rt.Activities : Slots.GetValueOrDefault(body);
        public string? Holder(string body) => null;
        public IDisposable? Push(string body)
        {
            var prev = Scope.Value;
            Scope.Value = body;
            return new Restore(prev);
        }
        public string? GuardKey(string body) => body == "main" ? null : "maid_chan_" + body;
        public string? Stop(string body) => Slots.GetValueOrDefault(body)?.Cancel("stop");
        public string? Pick(string job, IReadOnlyList<string> candidates, out string? why) { why = null; return candidates.FirstOrDefault(); }
        public string? StandingFocus => Standing;
        public IReadOnlyList<string> Addressed(string text, bool emote = false) =>
            _addr.Addressed(text, emote).Select(n => n == _rt.Chat.Username ? "main" : BodyAddressing.NumberOf(n)!).ToList();
        public string StatusBlock(string? focus) => "[bodies] test";
        public string? StateHeader(string body) => body == "main" ? null : $"pos of {body}";
        public void Started(IReadOnlyList<string> bodies) { lock (Crews) Crews.Add(bodies); }

        private sealed class Restore(string? prev) : IDisposable { public void Dispose() => Scope.Value = prev; }
    }

    private static (AgentRuntime Rt, FakeLlm Llm, FakeDirectory Dir, List<string> Ran) BrainWithBodies()
    {
        var (rt, llm, _) = TestRuntime.Create();
        var ran = new List<string>();
        rt.Tools.Register("mine", "mine", ToolSchema.Create().Str("block").Build(), async (input, ct) =>
        {
            await Task.Delay(30, ct);
            lock (ran) ran.Add($"{Scope.Value ?? "unscoped"}:{input.Str("block")}");
            return ToolResult.Success($"mined {input.Str("block")} as {Scope.Value}");
        }, longRunning: true);
        rt.Tools.Register("status", "status", ToolSchema.Empty, (_, _) => Task.FromResult(ToolResult.Success("status of " + (Scope.Value ?? "unscoped"))));
        DoTool.Register(rt.Tools);
        var dir = new FakeDirectory(rt, "67", "69");
        rt.Agent.Bodies = dir;
        return (rt, llm, dir, ran);
    }

    [Fact]
    public async Task One_brain_call_with_two_bodies_runs_a_job_on_each_bodys_slot_in_one_turn()
    {
        var (rt, llm, dir, ran) = BrainWithBodies();
        llm.ThenTool("do", new { action = "mine", args = new { block = "cobblestone" }, body = new[] { "67", "69" } })
           .ThenText("67 and 69 are on it");
        var o = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Fox", "67 and 69 mine cobble")]), CancellationToken.None);
        Assert.Equal(2, o.Calls); // one turn: the call + the closing line
        await TestWaits.UntilAsync(() => { lock (ran) return ran.Count == 2; });
        Assert.Equal(["67:cobblestone", "69:cobblestone"], ran.OrderBy(x => x));
        var result = llm.Requests[1].Messages[^1].Content.OfType<LlmToolResult>().Single();
        Assert.StartsWith("67: ", result.Content);
        Assert.Contains("; 69: ", result.Content);
        Assert.Equal("67", rt.Agent.TurnFocus); // the line named 67 first
        Assert.Single(dir.Crews);
        Assert.Equal(["67", "69"], dir.Crews[0].OrderBy(x => x));
        // The turn head: the focus body's state and the other bodies' line (uncached tail).
        var head = llm.Requests[0].Messages[^1].Content.OfType<LlmText>().First().Text;
        Assert.StartsWith("67: pos of 67", head);
        Assert.Contains("[bodies] test", head);
    }

    [Fact]
    public async Task Without_a_body_a_body_tool_acts_on_the_focus_and_an_unknown_body_is_an_error()
    {
        var (rt, llm, _, _) = BrainWithBodies();
        llm.ThenTool("status", new { }, id: "a").ThenTool("status", new { body = "420" }, id: "b").ThenText("ok");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Fox", "69, how are you")]), CancellationToken.None);
        var first = llm.Requests[1].Messages[^1].Content.OfType<LlmToolResult>().Single();
        Assert.Equal("status of 69", first.Content);
        var second = llm.Requests[2].Messages[^1].Content.OfType<LlmToolResult>().Single();
        Assert.True(second.IsError);
        Assert.StartsWith("no body called 420", second.Content);
    }

    [Fact]
    public async Task Without_a_directory_a_body_naming_another_bot_is_refused_and_one_naming_itself_runs_unchanged()
    {
        // Live 10-29: without a directory a call naming 420 ran on main's own body and main reported 420's arrival. Now it is
        // refused (nothing runs) with how to reach that bot; a body naming this bot itself still runs exactly as sent.
        var (rt, llm, _) = TestRuntime.Create();
        rt.Tools.Register("status", "status", ToolSchema.Empty, (input, _) => Task.FromResult(ToolResult.Success("status " + input.GetRawText())));
        llm.ThenTool("status", new { body = "67" }).ThenTool("status", new { body = "main" }, id: "tu_2").ThenText("ok");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Fox", "67, status")]), CancellationToken.None);
        var r = llm.Requests[1].Messages[^1].Content.OfType<LlmToolResult>().Single();
        Assert.True(r.IsError);
        Assert.StartsWith("not done: 67 isn't a body I drive", r.Content);
        var own = llm.Requests[2].Messages[^1].Content.OfType<LlmToolResult>().Single();
        Assert.Equal("status {\"body\":\"main\"}", own.Content); // its own body: the tool sees the input exactly as sent
        Assert.Null(rt.Agent.TurnFocus);
    }
}
