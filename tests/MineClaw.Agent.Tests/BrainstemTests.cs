using System.Text.Json;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>Reflex matcher, debounce tuning, the priority stack, activity kinds (moves, queueing, speculation).</summary>
public class BrainstemTests
{
    private static readonly string[] Names = ["maid_chan", "maid"];

    // ------------------------------------------------------------------ reflex matcher: exact commands only

    [Theory]
    [InlineData("stop", IntentKind.Stop)]
    [InlineData("STOP!", IntentKind.Stop)]
    [InlineData("maid stop", IntentKind.Stop)]
    [InlineData("stop please", IntentKind.Stop)]
    [InlineData("wait", IntentKind.Stop)]
    [InlineData("hold on", IntentKind.Stop)]
    [InlineData("stop following me", IntentKind.Stop)]
    [InlineData("oh my got both of you stop!", IntentKind.Stop)]
    [InlineData("come here", IntentKind.Come)]
    [InlineData("maid, come here pls", IntentKind.Come)]
    [InlineData("come to me", IntentKind.Come)]
    [InlineData("get over here", IntentKind.Come)]
    [InlineData("follow me", IntentKind.Follow)]
    [InlineData("jump", IntentKind.Gesture)]
    [InlineData("spin around", IntentKind.Gesture)]
    [InlineData("wave", IntentKind.Gesture)]
    [InlineData("crouch", IntentKind.Gesture)]
    [InlineData("look at me", IntentKind.LookAtMe)]
    [InlineData("give me 5 cobblestone", IntentKind.Give)]
    [InlineData("drop 3 dirt", IntentKind.Give)]
    [InlineData("give me all your cobblestone", IntentKind.Give)]
    public void Reflex_matches_exact_command_shapes(string line, IntentKind kind)
    {
        var i = IntentMatcher.Reflex(line, Names, item => item is "cobblestone" or "dirt");
        Assert.Equal(kind, i.Kind);
    }

    [Theory]
    // Ambiguous, conversational or longer lines always go to the brain.
    [InlineData("wait that's the bridge not the edhe where I was")]
    [InlineData("wait what?")]
    [InlineData("don't stop")]
    [InlineData("please don't stop mining")]
    [InlineData("they never stop")]
    [InlineData("where's the bus stop")]
    [InlineData("stop?")]
    [InlineData("can I come?")]
    [InlineData("come here and build for me")]
    [InlineData("grab some cobble stone and come here I nee you to build for me")]
    [InlineData("follow me to the gen then mine")]
    [InlineData("jump in the hole")]
    [InlineData("just drop into the hole and mine your side to show me you can")]
    [InlineData("give me a hand")]
    [InlineData("give me some space")]
    [InlineData("drop it")]
    [InlineData("give me 5 diamonds")]          // not held: the brain answers
    [InlineData("drop everything")]
    [InlineData("look at this")]
    [InlineData("look at the gen")]
    [InlineData("see this ledge I'm by?")]
    [InlineData("you're mining the flooor")]
    [InlineData("hello 67? did you hear me about mining the right cell???")]
    [InlineData("I see more trees to be cut")]
    public void Reflex_never_fires_on_ambiguous_lines(string line)
    {
        var i = IntentMatcher.Reflex(line, Names, item => item is "cobblestone" or "dirt");
        Assert.False(i.IsReflex, $"'{line}' misfired as {i.Kind}");
    }

    [Theory]
    [InlineData("grab some cobble stone and come here I nee you to build for me", IntentKind.SpecToSpeaker)]
    [InlineData("can you get 67 and go work the cobble gen for me all night?", IntentKind.SpecGenerator)]
    [InlineData("mine 64 cobble", IntentKind.SpecGenerator)]
    [InlineData("can you harvest trees with 67?", IntentKind.SpecTree)]
    [InlineData("chop some wood", IntentKind.SpecTree)]
    [InlineData("bring me 40 cobble blocks", IntentKind.SpecBring)]
    [InlineData("go to the cobble gen", IntentKind.SpecPlace)]
    [InlineData("how can we get wheat?", IntentKind.None)]
    [InlineData("how do we get wood?", IntentKind.None)]
    [InlineData("don't mine the cobble", IntentKind.None)]
    [InlineData("stop mining and come here", IntentKind.None)]
    [InlineData("can I make food from cactus?", IntentKind.None)]
    [InlineData("you're mining the wrong blocks again", IntentKind.None)]
    public void Speculative_intents_are_clear_jobs_only(string line, IntentKind kind) =>
        Assert.Equal(kind, IntentMatcher.Speculative(line, Names).Kind);

    [Fact]
    public void Simple_intent_classification()
    {
        Assert.True(IntentMatcher.IsSimple(["come here"], Names));
        Assert.True(IntentMatcher.IsSimple(["lol thanks maid"], Names));
        Assert.True(IntentMatcher.IsSimple(["chop some wood"], Names));
        Assert.False(IntentMatcher.IsSimple(["yes we're extending this way 2 rows just this side of the doors"], Names));
        Assert.False(IntentMatcher.IsSimple(["can you make 420 and 69 have a spar please? craft them two sword"], Names));
        Assert.False(IntentMatcher.IsSimple(["mine cobble then build a wall"], Names));
    }

    // ------------------------------------------------------------------ debounce tuning

    [Theory]
    [InlineData("come here!", true)]
    [InlineData("grab some cobble stone and come here", true)]
    [InlineData("can you make me 30 cobble slabs? :3", true)]
    [InlineData("i need you to", false)]
    [InlineData("so,", false)]
    [InlineData("and then", false)]
    [InlineData("hey maid", false)]
    public void Lines_that_look_complete_get_the_short_window(string line, bool complete) =>
        Assert.Equal(complete, new ChatDebounceOptions().LooksComplete(line));

    [Fact]
    public async Task Complete_lines_fire_after_the_short_quiet_window()
    {
        var d = new ChatDebounceOptions { QuietMs = 700, CompleteQuietMs = 60, MaxMs = 3000, ImmediateOnQuestion = false };
        var q = new EventQueue(new EventQueueOptions(), debounce: d);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        q.Post(GameEvent.Chat("fox", "come here!"));
        await q.WaitBatchAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        long complete = sw.ElapsedMilliseconds;
        sw.Restart();
        q.Post(GameEvent.Chat("fox", "so i was thinking"));
        await q.WaitBatchAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        long open = sw.ElapsedMilliseconds;
        Assert.True(complete < 500, $"complete line waited {complete} ms");
        Assert.True(open >= 650, $"open line waited only {open} ms");
    }

    // ------------------------------------------------------------------ priority stack

    [Fact]
    public void Priority_stack_orders_safety_job_speculation_social()
    {
        bool safety = false, job = false, spec = false, social = false;
        var s = new PriorityStack(() => safety, () => job, () => spec, () => social);
        Assert.Equal(BodyLayer.Idle, s.Top);
        Assert.True(s.MayAct(BodyLayer.Social));
        social = true;
        Assert.Equal(BodyLayer.Social, s.Top);
        Assert.True(s.MayAct(BodyLayer.Speculative)); // a higher layer may always preempt a lower one
        spec = true;
        Assert.Equal(BodyLayer.Speculative, s.Top);
        Assert.False(s.MayAct(BodyLayer.Social));
        job = true;
        Assert.Equal(BodyLayer.Job, s.Top);
        Assert.False(s.MayAct(BodyLayer.Speculative));
        Assert.True(s.MayAct(BodyLayer.Job));
        safety = true;
        Assert.Equal(BodyLayer.Safety, s.Top);
        Assert.False(s.MayAct(BodyLayer.Job));
        Assert.True(s.MayAct(BodyLayer.Safety));
    }

    // ------------------------------------------------------------------ activity kinds

    private sealed class Rig
    {
        public readonly ToolRegistry Tools = new();
        public readonly EventQueue Events = new(new EventQueueOptions { CoalesceWindowMs = 10, MaxBatchDelayMs = 20 });
        public readonly ActivityManager Activities;
        public readonly List<string> Ran = new();
        public TaskCompletionSource<ToolResult> Walk = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Walks;

        public Rig()
        {
            Activities = new ActivityManager(Tools, Events);
            Tools.Register("goto", "walk", ToolSchema.Create().Str("to").Build(), async (i, ct) =>
            {
                Interlocked.Increment(ref Walks);
                lock (Ran) Ran.Add("goto " + i.Str("to"));
                using var reg = ct.Register(() => Walk.TrySetResult(ToolResult.Error("cancelled")));
                return await Walk.Task;
            }, longRunning: true);
            Tools.Register("mine", "mine", ToolSchema.Create().Str("block").Build(), (i, _) =>
            {
                lock (Ran) Ran.Add("mine " + i.Str("block"));
                return Task.FromResult(ToolResult.Success("mined"));
            }, longRunning: true);
        }

        public IAgentTool T(string n) => Tools.TryGet(n, out var t) ? t : throw new InvalidOperationException(n);
        public static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
    }

    [Fact]
    public async Task A_move_returns_at_once_and_an_action_queued_behind_it_runs_on_arrival()
    {
        var r = new Rig();
        var move = await r.Activities.RunAsync([(r.T("goto"), Rig.J(new { to = "gen" }))], TimeSpan.FromMilliseconds(30), default, ActivityKind.Move);
        Assert.StartsWith("running in background", move[0].Text);
        var queued = await r.Activities.RunAsync([(r.T("mine"), Rig.J(new { block = "cobblestone" }))], TimeSpan.FromMilliseconds(30), default);
        Assert.StartsWith("queued behind goto", queued[0].Text);
        Assert.DoesNotContain("mine cobblestone", r.Ran); // not before arrival
        Assert.Contains(", then mine", r.Activities.Describe());
        r.Walk.SetResult(ToolResult.Success("arrived"));
        await Until(() => r.Ran.Contains("mine cobblestone"));
        Assert.Equal(["goto gen", "mine cobblestone"], r.Ran);
    }

    [Fact]
    public async Task A_failed_move_cancels_the_queued_action_with_the_reason()
    {
        var r = new Rig();
        await r.Activities.RunAsync([(r.T("goto"), Rig.J(new { to = "gen" }))], TimeSpan.FromMilliseconds(30), default, ActivityKind.Move);
        var mine = r.Activities.RunAsync([(r.T("mine"), Rig.J(new { block = "cobblestone" }))], TimeSpan.FromSeconds(5), default);
        await Task.Delay(30);
        r.Walk.SetResult(ToolResult.Error("no path to within 3 of the gen"));
        var res = await mine;
        Assert.False(res[0].Ok);
        Assert.Contains("not run: goto failed: no path to within 3 of the gen", res[0].Text);
        Assert.DoesNotContain("mine cobblestone", r.Ran);
    }

    [Fact]
    public async Task Speculation_is_adopted_by_a_move_to_the_same_place()
    {
        var r = new Rig();
        bool? adopted = null;
        r.Activities.SpeculationResolved += (a, _) => adopted = a;
        r.Activities.SpeculationPolicy = (spec, next) => next[0].Tool == "goto" && next[0].Input.Str("to") == spec[0].Input.Str("to")
            ? SpeculationVerdict.Adopt : SpeculationVerdict.Cancel;
        Assert.True(r.Activities.TryStartFiller(ActivityKind.Speculative, [(r.T("goto"), Rig.J(new { to = "gen" }))]));
        Assert.False(r.Activities.IsBusy); // a filler is not a job
        await Until(() => r.Walks == 1);
        var chain = r.Activities.RunAsync([(r.T("goto"), Rig.J(new { to = "gen" })), (r.T("mine"), Rig.J(new { block = "cobblestone" }))],
            TimeSpan.FromSeconds(5), default);
        await Task.Delay(30);
        Assert.True(adopted);
        Assert.True(r.Activities.IsBusy); // the adopted walk is the brain's job now
        r.Walk.SetResult(ToolResult.Success("arrived"));
        var res = await chain;
        Assert.Equal(1, r.Walks); // the walk was never restarted
        Assert.True(res.All(x => x.Ok), string.Join("; ", res.Select(x => x.Text)));
        Assert.Equal(["goto gen", "mine cobblestone"], r.Ran);
    }

    [Fact]
    public async Task Speculation_is_cancelled_and_redirected_by_an_unrelated_job()
    {
        var r = new Rig();
        bool? adopted = null;
        r.Activities.SpeculationResolved += (a, _) => adopted = a;
        r.Activities.SpeculationPolicy = (_, _) => SpeculationVerdict.Cancel;
        Assert.True(r.Activities.TryStartFiller(ActivityKind.Speculative, [(r.T("goto"), Rig.J(new { to = "tree" }))]));
        await Until(() => r.Walks == 1);
        var res = await r.Activities.RunAsync([(r.T("mine"), Rig.J(new { block = "dirt" }))], TimeSpan.FromSeconds(5), default);
        Assert.True(res[0].Ok);
        Assert.False(adopted);
        Assert.Equal("cancelled", (await r.Walk.Task).Text); // the speculative walk's token was cancelled
        Assert.Equal(["goto tree", "mine dirt"], r.Ran);
    }

    [Fact]
    public async Task Fillers_never_start_over_a_job_and_never_show_as_busy()
    {
        var r = new Rig();
        _ = r.Activities.RunAsync([(r.T("goto"), Rig.J(new { to = "gen" }))], TimeSpan.FromMilliseconds(10), default);
        await Until(() => r.Walks == 1);
        Assert.False(r.Activities.TryStartFiller(ActivityKind.Social, [(r.T("goto"), Rig.J(new { to = "fox" }))]));
        Assert.False(r.Activities.TryStartFiller(ActivityKind.Speculative, [(r.T("goto"), Rig.J(new { to = "fox" }))]));
        r.Walk.SetResult(ToolResult.Success("ok"));
        await Until(() => !r.Activities.IsBusy);
        r.Walk = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(r.Activities.TryStartFiller(ActivityKind.Social, [(r.T("goto"), Rig.J(new { to = "fox" }))]));
        Assert.False(r.Activities.IsBusy);
        Assert.Null(r.Activities.Current);
        Assert.NotNull(r.Activities.Filler);
    }

    internal static async Task Until(Func<bool> cond, int ms = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }
}
