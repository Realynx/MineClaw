using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rules;
using MineClaw.Data;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-28 16:15–16:22 (alice): a restart woke main on the drop tower at night and zombies killed it; after the respawn a
/// walk west with no food killed it again; and Robin's quiet rule plus the restart's startup cap kept "Robin, a zombie is
/// attacking you" out of chat ("not said"). Safety comes first: warnings override the quiet gates, a day-only rule stops a
/// build at dusk (the generic directive check), and the startup wake waits for the body's own safety check.
/// </summary>
public class SafetyFirstTests
{
    private static ChatRouter Router(out ManualTime time)
    {
        time = new ManualTime();
        var o = new ChatOptions { Username = "maid_chan", DisplayName = "maid chan", Aliases = ["maid"], LineDelayMs = 0 };
        var r = new ChatRouter(o, new EventQueue(), (_, _) => Task.CompletedTask, time);
        r.SetOnlinePlayers(["PoofimaFox", "kiwi"]);
        return r;
    }

    [Fact]
    public async Task A_safety_warning_is_never_capped_as_startup_chatter_but_is_rate_limited_per_threat()
    {
        var r = Router(out var time);
        r.BeginSession();
        Assert.Single(await r.SayAsync("back online~ the tower's done!"));
        Assert.Single(await r.SayAsync("gonna check the chests"));
        Assert.Empty(await r.SayAsync("one more thing")); // the startup cap: two lines until someone talks to the bot
        // 16:21: the warning goes out anyway.
        Assert.Equal(["Robin, a zombie is attacking you"], await r.SayAsync("Robin, a zombie is attacking you", audience: ["kiwi"]));
        // Once per threat per 30 s: the same warning again right away is chatter again (capped).
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(await r.SayAsync("robin the zombie is still attacking you", audience: ["kiwi"]));
        // Another threat is its own warning.
        Assert.Single(await r.SayAsync("CREEPER behind you robin!", audience: ["kiwi"]));
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Single(await r.SayAsync("robin, zombie on you again!", audience: ["kiwi"]));
    }

    [Theory]
    [InlineData("Robin, a zombie is attacking you", "zombie")]
    [InlineData("creeper behind you!!", "creeper")]
    [InlineData("watch out fox, lava", "lava")]
    [InlineData("fox nooo you're falling into the void!", "void")]
    [InlineData("skeleton shooting at you, careful", "skeleton")]
    [InlineData("hi robin~ the zombie farm is done", null)]
    [InlineData("I mined 32 cobble", null)]
    public void Warnings_are_recognised_by_threat_and_urgency(string line, string? threat) =>
        Assert.Equal(threat, SafetyWarnings.Threat(line));

    [Fact]
    public async Task A_quiet_turn_says_a_safety_warning_even_with_nobody_engaged()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        // Robin's rule #5 "stay quiet unless spoken to" stands; the turn is a task event (nobody engaged).
        llm.ThenTool(BotAgent.SayToolName, new { text = "Robin, a zombie is attacking you" }).ThenText("");
        var o = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("#3 harvest_generator: ok: mined 32 cobblestone")]), default);
        Assert.Equal(["Robin, a zombie is attacking you"], sent);
        var result = llm.Requests[1].Messages[^1].Content.OfType<LlmToolResult>().Single();
        Assert.Equal("said", result.Content);
        // A routine line in the same kind of turn is still kept internal.
        llm.ThenTool(BotAgent.SayToolName, new { text = "cobble's stocked up~" }).ThenText("");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("#4 harvest_generator: ok: mined 32 cobblestone")]), default);
        Assert.Single(sent);
    }

    [Theory]
    [InlineData("the drop tower only in daytime, at night mine cobble", "day")]
    [InlineData("only build in daytime though to avoid mobs", "day")]
    [InlineData("no building at night", "day")]
    [InlineData("build by day only", "day")]
    [InlineData("only mine at night", "night")]
    [InlineData("always build with cobble, never planks", null)]
    [InlineData("mine cobble every night", null)]
    public void A_rule_names_its_time_window(string text, string? window) => Assert.Equal(window, DirectiveParser.OnlyWhen(text));

    [Fact]
    public void A_day_only_rule_holds_its_build_from_dusk_and_lets_it_run_by_day()
    {
        var t = new ManualTime();
        var db = AgentDb.Open(null);
        var rules = DirectiveStore.For(db, new RagIndex(db, new RagOptions(), time: t, backgroundEmbedding: false));
        rules.Time = t;
        rules.IsTrusted = p => p == "PoofimaFox";
        rules.Nick = p => p == "PoofimaFox" ? "fox" : p;
        var r = rules.Add("PoofimaFox", "design #4: the drop tower only in daytime, at night mine cobble");
        Assert.True(r.Stored, r.Text);
        Assert.Null(rules.TimeHold(4, 6000));                      // noon: build on
        Assert.Null(rules.TimeHold(4, 11000));                     // late afternoon
        var dusk = rules.TimeHold(4, 11800);                        // sunset coming: stop and get home before dark
        Assert.NotNull(dusk);
        Assert.StartsWith($"{r.Rule!.Ref} (Fox): ", dusk);
        Assert.EndsWith("it's getting dark: stopped until day", dusk);
        Assert.EndsWith("it's night: stopped until day", rules.TimeHold(4, 18000));
        Assert.Null(rules.TimeHold(4, 23500));                     // dawn
        Assert.Null(rules.TimeHold(9, 18000));                     // another design: not this rule's
    }

    [Fact]
    public async Task The_startup_wake_waits_for_the_bodys_safety_check_and_leads_with_it()
    {
        using var dir = new TempDir();
        var llm = new FakeLlm { Fallback = _ => new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, "claude-opus-5-5") };
        var o = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0, DataDirectory = dir.Path };
        o.Chat.Username = "maid_chan";
        o.Chat.DisplayName = "maid_chan";
        o.Chat.Aliases = [];
        o.Chat.LineDelayMs = 0;
        o.Self.Enabled = false;
        o.Rag.SeedFile = "";
        o.Idle.ResumeOnStartSeconds = 0;
        o.ChatDebounce.QuietMs = 10;
        o.ChatDebounce.MaxMs = 50;
        using var rt = AgentRuntime.Create(o, (_, _) => Task.CompletedTask, llm, persist: true);
        rt.Agent.AddPendingWork(() => "the mob drop tower build (#4) is done");
        var safe = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rt.Agent.SafetyFirst = _ => safe.Task;
        using var cts = new CancellationTokenSource();
        var run = rt.Agent.RunAsync(cts.Token);
        await Task.Delay(400);
        Assert.Equal(0, rt.Agent.StartupWakes); // nothing asks the brain before the body is safe
        safe.SetResult("woke up stranded at -26,77,14 (night, no way down): boxed myself in at -26,77,14: 7 zombies, hp 20; waiting it out");
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!llm.Requests.Any(r => Text(r).Contains("restarted:")) && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Equal(1, rt.Agent.StartupWakes);
        var wake = Text(llm.Requests.First(r => Text(r).Contains("restarted:")));
        Assert.Contains("restarted: woke up stranded at -26,77,14 (night, no way down): boxed myself in", wake);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    private static string Text(LlmRequest r) => string.Concat(r.Messages[^1].Content.OfType<LlmText>().Select(t => t.Text));
}
