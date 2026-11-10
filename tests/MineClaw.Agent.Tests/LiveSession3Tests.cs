using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>Regressions from the 2026-10-27 live session (session 3, 14:05-).</summary>
public class LiveSession3Tests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    // ------------------------------------------------------------------ worker tier model (14:19:49 "worker call 0: claude-opus-5-5")

    [Fact]
    public async Task Delegate_runs_on_the_worker_model_per_tier_with_and_without_a_profile()
    {
        var (rt, llm, _) = TestRuntime.Create(o =>
        {
            o.Llm.ApiKey = "sk-ant-test";
            o.Llm.Profiles["opus_worker"] = new LlmProfile { Model = "claude-opus-5-5", Worker = "opus" }; // explicit opt-in
        });
        var o = rt.Options.Llm;
        Assert.Equal("claude-opus-5-5", o.ModelFor(ModelTier.Main));
        Assert.Equal("claude-sonnet-5-5", o.ModelFor(ModelTier.Worker));
        Assert.Equal("claude-haiku-5-5", o.ModelFor(ModelTier.Cheap));

        async Task<LlmRequest> Delegate(string? profile)
        {
            llm.Then(r => llm.Resp(r, StopReasons.EndTurn, new LlmText("ok: done")));
            object input = profile == null ? new { task = "fill the farm trench with water", done_when = "full" }
                : new { task = "fill the farm trench with water", done_when = "full", profile };
            var res = await rt.Tools.ExecuteAsync("delegate", J(input), default);
            Assert.True(res.Ok, res.Text);
            return llm.Requests[^1];
        }
        static string ModelOf(LlmRequest r, LlmOptions o) => r.Model ?? o.ModelFor(r.Tier);

        var none = await Delegate(null);
        Assert.Equal(ModelTier.Worker, none.Tier);
        Assert.Equal("claude-sonnet-5-5", ModelOf(none, o));

        var opus = await Delegate("opus"); // the brain asked for opus: the worker still runs on sonnet
        Assert.Equal(ModelTier.Worker, opus.Tier);
        Assert.Equal("claude-sonnet-5-5", ModelOf(opus, o));
        Assert.Null(opus.Profile);

        var haiku = await Delegate("haiku"); // cheaper than the worker tier: allowed
        Assert.Equal("claude-haiku-5-5", ModelOf(haiku, o));

        var optIn = await Delegate("opus_worker"); // the profile explicitly says so
        Assert.Equal("claude-opus-5-5", ModelOf(optIn, o));

        // Every one of these is worker spend in /usage, whatever model answered.
        Assert.Equal(4, rt.Usage.SnapshotTotals().Today.Tier(ModelTier.Worker).Calls);
    }

    [Fact]
    public async Task Worker_stops_after_two_identical_failures()
    {
        var (rt, llm, _) = TestRuntime.Create();
        int calls = 0;
        rt.Tools.Register("fill_bucket", "fill a bucket", ToolSchema.Create().Int("x").Build(),
            (input, _) => { calls++; return Task.FromResult(ToolResult.Error($"no water source at {input.GetProperty("x").GetInt32()},64,-9")); },
            tiers: ToolTiers.Worker);
        llm.ThenTool("fill_bucket", new { x = 1 }).ThenTool("fill_bucket", new { x = 2 }, id: "tu_2")
           .ThenTool("fill_bucket", new { x = 3 }, id: "tu_3").ThenText("failed: never reached");
        var res = await rt.Tools.ExecuteAsync("delegate", J(new { task = "fill the bucket", done_when = "bucket full" }), default);
        Assert.False(res.Ok);
        Assert.Contains("fill_bucket failed 2x", res.Text);
        Assert.Equal(2, calls);
        Assert.Equal(2, llm.Requests.Count); // no third round, no wrap-up call
    }

    [Fact]
    public void Failure_key_ignores_coordinates_but_not_the_reason()
    {
        Assert.Equal(Worker.WorkerAgent.FailureKey("fill_bucket", "no water source at 1,64,-9"),
                     Worker.WorkerAgent.FailureKey("fill_bucket", "no water source at 1,63,-9"));
        Assert.NotEqual(Worker.WorkerAgent.FailureKey("fill_bucket", "no water source"),
                        Worker.WorkerAgent.FailureKey("fill_bucket", "bucket is full"));
        Assert.NotEqual(Worker.WorkerAgent.FailureKey("goto", "no water source"),
                        Worker.WorkerAgent.FailureKey("fill_bucket", "no water source"));
    }

    // ------------------------------------------------------------------ addressing (14:20:52 "give me my bucket back" ignored for 72 s)

    private static (ChatRouter Router, EventQueue Queue, ManualTime Time) Live()
    {
        var time = new ManualTime();
        var o = new ChatOptions { Username = "maid_chan", DisplayName = "maid chan", Aliases = ["maidchan", "maid"], LineDelayMs = 0 }.Regulars();
        var q = new EventQueue();
        var r = new ChatRouter(o, q, (_, _) => Task.CompletedTask, time);
        r.SetOnlinePlayers(["PoofimaFox", "kiwi"]);
        return (r, q, time);
    }

    private static bool Woke(EventQueue q, string text) => q.Snapshot().Any(e => e.Wakes && e.Text == text);

    [Fact]
    public async Task A_reply_after_the_bot_spoke_to_the_player_continues_the_conversation()
    {
        var (r, q, time) = Live();
        r.OnServerChat("<PoofimaFox> I want you to finish filling the water in the farm like I showed you"); // 14:19:31
        time.Advance(TimeSpan.FromSeconds(61));
        // 14:20:32: a task-event turn (no audience) speaks to Fox without naming him.
        await r.SayAsync("master, i tried but couldn't scoop anything, the water in the farm seems to be flowing water");
        time.Advance(TimeSpan.FromSeconds(20));
        r.OnServerChat("<PoofimaFox> give me my bucket back, I already showed you the infinite pool"); // 14:20:52
        Assert.True(Woke(q, "give me my bucket back, I already showed you the infinite pool"));
    }

    [Fact]
    public void Continuity_holds_for_90_seconds_after_the_player_talked_to_the_bot()
    {
        var (r, q, time) = Live();
        r.OnServerChat("<PoofimaFox> maid I want you to finish the farm");
        time.Advance(TimeSpan.FromSeconds(81));
        Assert.Equal(AddressReason.Continuity, r.ReasonFor(r.Parse("<PoofimaFox> the pool is over there")));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(AddressReason.No, r.ReasonFor(r.Parse("<PoofimaFox> the pool is over there")));
    }

    [Theory]
    [InlineData("give me my bucket back, I already showed you the infinite pool")]
    [InlineData("GIV ME THE FREAKIN BUCKET")]
    [InlineData("go farm the trees")]
    [InlineData("stop")]
    [InlineData("come here")]
    [InlineData("finish the farm")]
    [InlineData("you know you can shift off stairways")]
    [InlineData("have 69 come in and do the same procedure with you in parallel")]
    [InlineData("and have clone 69 harvest and replant trees")]
    public void Imperatives_about_the_bot_count_as_addressed_with_two_players_online(string line)
    {
        var (r, _, _) = Live();
        Assert.True(r.IsAddressed(r.Parse($"<PoofimaFox> {line}")), line);
    }

    [Theory]
    [InlineData("<PoofimaFox> kiwi look at this")]
    [InlineData("<PoofimaFox> robin, come see the farm")]
    [InlineData("<kiwi> foxy you ok?")]
    [InlineData("<kiwi> hey fox give me the hoe")]
    [InlineData("<PoofimaFox> nice day")]
    public void Player_to_player_chat_never_wakes_the_bot(string line)
    {
        var (r, q, _) = Live();
        r.OnServerChat(line);
        Assert.False(q.HasWaking, line);
    }

    [Fact]
    public void Player_to_player_wins_over_continuity_but_a_mere_mention_does_not()
    {
        var (r, _, _) = Live();
        r.OnServerChat("<kiwi> maidchan wyd");
        Assert.False(r.IsAddressed(r.Parse("<kiwi> foxy, look what she built")));
        Assert.True(r.IsAddressed(r.Parse("<kiwi> go listen to foxy")));
        Assert.True(r.IsAddressed(r.Parse("<kiwi> go listen to poofimafox")));
    }

    [Fact]
    public async Task Unsure_lines_from_regulars_near_the_bot_ask_the_classifier_once()
    {
        var (r, q, time) = Live();
        int calls = 0;
        r.IsRegular = p => p.Equals("PoofimaFox", StringComparison.OrdinalIgnoreCase);
        r.IsNear = _ => true;
        r.Classifier = (m, ctx, _) => { calls++; return Task.FromResult<bool?>(m.Text.Contains("pool")); };
        r.OnServerChat("<PoofimaFox> nice day");
        await TestWaits.UntilAsync(() => Volatile.Read(ref calls) >= 1);
        r.OnServerChat("<PoofimaFox> the infinite pool is by the house");
        await TestWaits.UntilAsync(() => Volatile.Read(ref calls) >= 2 && Woke(q, "the infinite pool is by the house"));
        Assert.Equal(2, calls);                       // one call per unsure line
        Assert.True(Woke(q, "the infinite pool is by the house"));
        Assert.False(Woke(q, "nice day"));
        r.OnServerChat("<kiwi> the pool is nice");    // not a regular: no call
        r.OnServerChat("<PoofimaFox> kiwi look");     // player-to-player: no call
        await Task.Delay(50);
        Assert.Equal(2, calls);
        Assert.Equal(1, r.Addressing.ClassifierYes);
        time.Advance(TimeSpan.Zero);
    }

    [Fact]
    public void A_line_repeated_by_name_counts_as_a_miss()
    {
        var (r, _, time) = Live();
        string? missed = null;
        r.AddressMissed += (_, line, _) => missed = line;
        r.OnServerChat("<PoofimaFox> the pool is right there");
        time.Advance(TimeSpan.FromSeconds(30));
        r.OnServerChat("<PoofimaFox> MAID!");
        Assert.Equal(1, r.Addressing.Misses);
        Assert.Equal("the pool is right there", missed);
    }

    // ------------------------------------------------------------------ background failures always wake (task #5)

    private static IAgentTool Fake(AgentRuntime rt, string name, Func<int, ToolResult> result, TimeSpan? delay = null)
    {
        int n = 0;
        rt.Tools.Register(name, name, ToolSchema.Create().Build(), async (_, ct) =>
        {
            if (delay is { } d) await Task.Delay(d, ct);
            return result(++n);
        }, tiers: ToolTiers.All);
        Assert.True(rt.Tools.TryGet(name, out var t));
        return t;
    }

    private static async Task<GameEvent> WaitEvent(AgentRuntime rt, Func<GameEvent, bool> match)
    {
        for (int i = 0; i < 3000; i++) // ≤ 30 s: returns at once when posted (2 s flaked under full-suite load)
        {
            if (rt.Events.Snapshot().FirstOrDefault(match) is { } e) return e;
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException("event not posted: " + string.Join(" | ", rt.Events.Snapshot().Select(x => x.Format())));
    }

    private static readonly JsonElement NoArgs = J(new { });

    [Fact]
    public async Task A_background_job_failure_posts_a_must_wake_event_with_the_reason()
    {
        var (rt, _, _) = TestRuntime.Create();
        var mine = Fake(rt, "mine", _ => ToolResult.Error("no pickaxe"), TimeSpan.FromMilliseconds(30));
        await rt.Activities.RunAsync([(mine, NoArgs)], TimeSpan.Zero, default);
        var e = await WaitEvent(rt, x => x.Kind == EventKind.TaskFailed);
        Assert.True(e.Wakes && e.MustWake);
        Assert.Contains("no pickaxe", e.Text);
        Assert.True(e.Priority < rt.Options.InterruptPriority); // wakes the brain, never interrupts the body
    }

    [Fact]
    public async Task A_walk_that_cannot_arrive_reports_and_the_queued_job_never_runs()
    {
        var (rt, _, _) = TestRuntime.Create();
        var walk = Fake(rt, "goto", _ => ToolResult.Error("no path to 14,64,-5 (door 13,64,-4 closed)"), TimeSpan.FromMilliseconds(30));
        int ran = 0;
        var withdraw = Fake(rt, "withdraw", _ => { ran++; return ToolResult.Success("took 1 bucket"); });
        await rt.Activities.RunAsync([(walk, NoArgs)], TimeSpan.Zero, default, ActivityKind.Move);
        await rt.Activities.RunAsync([(withdraw, NoArgs)], TimeSpan.Zero, default);
        var e = await WaitEvent(rt, x => x.Kind == EventKind.TaskFailed);
        Assert.True(e.MustWake);
        Assert.Contains("no path to 14,64,-5", e.Text);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task A_failure_ahead_of_a_do_queue_job_is_still_reported()
    {
        var (rt, _, _) = TestRuntime.Create();
        var first = Fake(rt, "chop_tree", _ => ToolResult.Error("no grown tree reachable"), TimeSpan.FromMilliseconds(40));
        var second = Fake(rt, "deposit", _ => ToolResult.Success("deposited 4 logs"));
        await rt.Activities.RunAsync([(first, NoArgs)], TimeSpan.Zero, default);
        await rt.Activities.RunAsync([(second, NoArgs)], TimeSpan.Zero, default, queueBehindCurrent: true);
        var e = await WaitEvent(rt, x => x.Kind == EventKind.TaskFailed);
        Assert.True(e.MustWake);
        Assert.Contains("no grown tree reachable", e.Text);
        await WaitEvent(rt, x => x.Kind == EventKind.TaskCompleted && x.Text.Contains("deposited"));
    }

    [Fact]
    public async Task A_delegate_worker_failure_in_the_background_wakes()
    {
        var (rt, llm, _) = TestRuntime.Create();
        llm.Then(r => llm.Resp(r, StopReasons.EndTurn, new LlmText("failed: no water source to scoop")));
        Assert.True(rt.Tools.TryGet("delegate", out var del));
        await rt.Activities.RunAsync([(del, J(new { task = "fill the bucket", done_when = "full" }))], TimeSpan.Zero, default);
        var e = await WaitEvent(rt, x => x.Kind == EventKind.TaskFailed);
        Assert.True(e.Wakes && e.MustWake);
        Assert.Contains("no water source", e.Text);
    }

    [Fact]
    public async Task An_interrupted_job_does_not_double_wake()
    {
        var (rt, _, _) = TestRuntime.Create();
        var mine = Fake(rt, "mine", _ => ToolResult.Error("cancelled"), TimeSpan.FromMilliseconds(200));
        await rt.Activities.RunAsync([(mine, NoArgs)], TimeSpan.Zero, default);
        await Task.Delay(20);
        rt.Activities.Cancel("interrupted by LowHealth");
        var e = await WaitEvent(rt, x => x.Kind == EventKind.TaskFailed);
        Assert.False(e.MustWake); // the interrupting event explains it
    }

    // ------------------------------------------------------------------ standing order "nothing to do yet" backoff

    [Fact]
    public async Task Nothing_to_do_yet_backs_off_two_four_eight_fifteen_minutes_without_waking()
    {
        var time = new ManualTime();
        var (rt, _, _) = TestRuntime.Create(o => o.Idle.MinResumeIntervalSeconds = 0, time: time);
        int passes = 0;
        rt.Tools.Register("chop_farm", "chop", ToolSchema.Create().Build(), (_, _) =>
        {
            passes++;
            return Task.FromResult(ToolResult.Success("no grown oak trees; 17 saplings still growing. Nothing chopped"));
        }, tiers: ToolTiers.All);
        var orders = rt.Agent.Orders!;
        bool? ready = null;
        orders.WorkReady = _ => ready;
        rt.Memory.SetStandingOrder(new MineClaw.Agent.Memory.StandingOrder { Bot = rt.Options.Chat.Username, Job = "work the tree farm", Tool = "chop_farm", Input = NoArgs });

        async Task Pass()
        {
            Assert.True(orders.ResumeIfIdle("test"));
            for (int i = 0; i < 3000 && rt.Activities.IsBusy; i++) await Task.Delay(10);
            await Task.Delay(30);
        }

        await Pass();
        Assert.Equal(time.Now + TimeSpan.FromMinutes(2), orders.BackoffUntil);
        Assert.DoesNotContain(rt.Events.Snapshot(), e => e.Wakes); // no LLM turn for an empty pass
        Assert.False(orders.ResumeIfIdle("idle"));                  // backing off
        time.Advance(TimeSpan.FromMinutes(2));
        await Pass();
        Assert.Equal(time.Now + TimeSpan.FromMinutes(4), orders.BackoffUntil);
        time.Advance(TimeSpan.FromMinutes(4));
        await Pass();
        Assert.Equal(time.Now + TimeSpan.FromMinutes(8), orders.BackoffUntil);
        time.Advance(TimeSpan.FromMinutes(8));
        await Pass();
        Assert.Equal(time.Now + TimeSpan.FromMinutes(15), orders.BackoffUntil);
        Assert.Equal(0, rt.Memory.GetStandingOrder(rt.Options.Chat.Username)!.FailuresInRow); // not failures

        // A tree grew (world model): the probe ends the backoff at once.
        Assert.False(orders.ProbeTick());
        ready = true;
        Assert.True(orders.ProbeTick());
        for (int i = 0; i < 3000 && passes < 5; i++) await Task.Delay(10);
        Assert.Equal(5, passes);
    }

    /// <summary>
    /// Live 10-28 11:10/12:05: gen_loop failed "store: chest is full" every pass, three in a row would pause the order for good.
    /// Every known chest full is a wait, not a failure: back off like work not ready (a regenerated gen cell can't cut it short),
    /// no failure counted, and the brain hears the actionable line once.
    /// </summary>
    [Fact]
    public async Task All_chests_full_backs_off_and_wakes_the_brain_once_instead_of_three_failures()
    {
        var time = new ManualTime();
        var (rt, _, _) = TestRuntime.Create(o => o.Idle.MinResumeIntervalSeconds = 0, time: time);
        const string line = "all 3 known chests full (14,64,-5, 5,64,4, 11,68,-5); 141 cobblestone in bag; craft+place a chest (have 300 logs) or keep carrying";
        int passes = 0;
        rt.Tools.Register("gen_job", "gen", ToolSchema.Create().Build(), (_, _) =>
        {
            passes++;
            return Task.FromResult(ToolResult.Error($"failed: script gen_loop at line 13 (store: {line}): cp 1/1, 29 acts, +141 cobblestone, 9m33s"));
        }, tiers: ToolTiers.All);
        var orders = rt.Agent.Orders!;
        orders.WorkReady = _ => true; // the gen cell has regenerated: no reason to retry while every chest is full
        var bot = rt.Options.Chat.Username;
        rt.Memory.SetStandingOrder(new MineClaw.Agent.Memory.StandingOrder { Bot = bot, Job = "mine the cobble gen for Fox", Tool = "gen_job", Input = NoArgs });

        var seen = new List<GameEvent>();
        async Task Pass()
        {
            Assert.True(orders.ResumeIfIdle("test"));
            for (int i = 0; i < 3000 && rt.Activities.IsBusy; i++) await Task.Delay(10);
            await Task.Delay(30);
            seen.AddRange(rt.Events.Snapshot()); // the brain takes its events
            rt.Events.RemovePending(_ => true);
        }

        await Pass();
        Assert.Equal(time.Now + TimeSpan.FromMinutes(2), orders.BackoffUntil);
        Assert.False(orders.ResumeIfIdle("idle"));  // backing off although WorkReady says yes
        Assert.False(orders.ProbeTick());
        Assert.StartsWith("storage full on the last pass", orders.WhyNot());
        time.Advance(TimeSpan.FromMinutes(2));
        await Pass();
        Assert.Equal(time.Now + TimeSpan.FromMinutes(4), orders.BackoffUntil);
        time.Advance(TimeSpan.FromMinutes(4));
        await Pass();
        Assert.Equal(time.Now + TimeSpan.FromMinutes(8), orders.BackoffUntil);
        Assert.Equal(3, passes);

        var order = rt.Memory.GetStandingOrder(bot)!;
        Assert.Equal(0, order.FailuresInRow); // three full passes, no failure pause
        var wake = Assert.Single(seen, e => e.MustWake || e.Wakes);
        Assert.Contains("\"mine the cobble gen for Fox\" is waiting", wake.Text);
        Assert.Contains(line, wake.Text);
        Assert.Equal(3, seen.Count(e => e.Kind == EventKind.TaskFailed)); // each pass is context, never a wake
        Assert.DoesNotContain(seen, e => e.Text.Contains("keeps failing"));
    }

    // ------------------------------------------------------------------ speech guards (task #34 remainder)

    [Fact]
    public void Memory_housekeeping_narration_never_reaches_chat()
    {
        var kept = MetaSpeech.Strip("\"main house\" (just a guess) and noted not to break it. I tried to delete a duplicate note about the " +
            "other new plank build, but nothing matched. Want me to help?", out var dropped);
        Assert.DoesNotContain("duplicate note", kept);
        Assert.Contains("Want me to help?", kept);
        Assert.Single(dropped);
    }

    [Fact]
    public async Task Food_is_asked_at_most_once_per_player_per_ten_minutes()
    {
        var (r, _, time) = Live();
        var sent = new List<string>();
        var router = new ChatRouter(new ChatOptions { Username = "maid_chan", DisplayName = "maid chan", LineDelayMs = 0 }.Regulars(), new EventQueue(),
            (l, _) => { sent.Add(l); return Task.CompletedTask; }, time);
        router.SetOnlinePlayers(["PoofimaFox", "kiwi"]);
        await router.SayAsync("On my way master fox uwu, also i'm starving lol, got any food?", audience: ["PoofimaFox"]);
        await router.SayAsync("omw with my axe master, also could i have one? i'm at 0 food lol", audience: ["PoofimaFox"]);
        time.Advance(TimeSpan.FromMinutes(3));
        await router.SayAsync("Still at 0 food and half health.", audience: ["PoofimaFox"]);
        await router.SayAsync("ROBIN hiii uwu, reporting for tree duty, you got any food?", audience: ["kiwi"]); // another player: once
        Assert.Equal(1, sent.Count(l => FoodAsks.IsFoodLine(l) && !l.Contains("ROBIN")));
        Assert.Contains(sent, l => l.StartsWith("omw with my axe master"));
        Assert.Contains(sent, l => l.Contains("ROBIN") && l.Contains("food"));
        time.Advance(TimeSpan.FromMinutes(8));
        await router.SayAsync("lowkey starving rn, got any food?", audience: ["PoofimaFox"]);
        Assert.Equal(2, sent.Count(l => FoodAsks.IsFoodLine(l) && !l.Contains("ROBIN")));
        _ = r;
    }

    [Fact]
    public async Task A_player_asking_about_food_gets_an_answer()
    {
        var (r, _, time) = Live();
        await r.SayAsync("got any food? i'm starving", audience: ["PoofimaFox"]);
        r.OnServerChat("<PoofimaFox> maid are you still hungry?");
        var lines = await r.SayAsync("yeah still starving lol", audience: ["PoofimaFox"]);
        Assert.Single(lines);
        _ = time;
    }

    [Fact]
    public async Task Robin_is_greeted_once_on_join()
    {
        var (r, _, time) = Live();
        var first = await r.SayAsync("heyy robin welcome back, it's giving cozy night shift uwu", audience: ["kiwi"]);
        Assert.Single(first);
        time.Advance(TimeSpan.FromSeconds(4));
        var second = await r.SayAsync("ROBIN hiii uwu, maid-chan reporting for tree duty", audience: ["kiwi"]);
        Assert.Equal("maid-chan reporting for tree duty", Assert.Single(second));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.StartsWith("hii robin", Assert.Single(await r.SayAsync("hii robin", audience: ["kiwi"])));
    }

    [Fact]
    public async Task Startup_chatter_is_two_lines_at_most_until_someone_talks_to_the_bot()
    {
        var (r, _, _) = Live();
        r.BeginSession();
        var a = await r.SayAsync("ooh new builds on the island, is the big plank one with the slabs a house or smth? what should i call it?");
        var b = await r.SayAsync("I asked the others what the big plank-and-slab build is. Until someone answers, I've saved it as the main house.");
        Assert.Equal(2, a.Count + b.Count);
        Assert.Empty(await r.SayAsync("one more thing"));
        r.OnServerChat("<PoofimaFox> maid come here");
        Assert.Single(await r.SayAsync("On my way master!"));
    }
}
