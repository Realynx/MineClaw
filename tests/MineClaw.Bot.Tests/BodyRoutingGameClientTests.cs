using System.Collections.Concurrent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Game;
using MineClaw.Scripting;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Tests;

/// <summary>
/// One brain, many bodies, step 1: the body router acts on the body in scope (pushed by the ActivityManager, the worker and the
/// script lane), falls back to the focus, warns when an activity forgot its scope; the registry holds exactly main.
/// </summary>
public class BodyRoutingGameClientTests
{
    /// <summary>Main ("maid_chan") plus a second body ("maid_chan_67") so a test can tell which one a call reached.</summary>
    private static (BodyRegistry Reg, Body Main, Body Clone, FakeGameClient MainC, FakeGameClient CloneC) TwoBodies()
    {
        var registry = new ToolRegistry(400);
        var events = new EventQueue();
        var mc = new FakeGameClient { Username = "maid_chan" };
        var cc = new FakeGameClient { Username = "maid_chan_67" };
        var main = new Body(mc.Username, isMain: true, mc, mc, new ActivityManager(registry, events));
        var clone = new Body(cc.Username, isMain: false, cc, cc, new ActivityManager(registry, events));
        var reg = new BodyRegistry(main);
        reg.Add(clone);
        return (reg, main, clone, mc, cc);
    }

    private static IAgentTool Probe(ToolRegistry tools, string name, Func<CancellationToken, Task<ToolResult>> run)
    {
        tools.Register(name, "probe", ToolSchema.Empty, (_, ct) => run(ct), longRunning: true);
        Assert.True(tools.TryGet(name, out var t));
        return t!;
    }

    [Fact]
    public void Unscoped_call_acts_on_the_focus_and_a_scope_overrides_it()
    {
        var (reg, _, clone, _, _) = TwoBodies();
        var router = new BodyRoutingGameClient(reg, b => b.Game);
        Assert.Equal("maid_chan", router.Username);
        using (BodyScope.Push(clone)) Assert.Equal("maid_chan_67", router.Username);
        Assert.Equal("maid_chan", router.Username);
        reg.SetFocus("67");
        Assert.Equal("maid_chan_67", router.Username);
        reg.SetFocus(null);
        Assert.Equal("maid_chan", router.Username);
        Assert.Equal(0, router.MissingScopeCount); // outside an activity nothing is suspicious
    }

    [Fact]
    public async Task Scope_flows_from_ActivityManager_into_Task_Run()
    {
        var (reg, _, clone, _, cc) = TwoBodies();
        var router = new BodyRoutingGameClient(reg, b => b.Game);
        var tools = new ToolRegistry(400);
        var activities = new ActivityManager(tools, new EventQueue()) { StepScope = () => BodyScope.Push(clone) };
        string? seen = null, inner = null;
        var probe = Probe(tools, "probe", async ct =>
        {
            seen = router.Username;
            // A tool that hops threads keeps the body: AsyncLocal flows into Task.Run.
            inner = await Task.Run(() => router.Username, ct);
            await router.EatAsync(ct);
            return ToolResult.Success("ok");
        });
        var r = await activities.RunAsync([(probe, ToolSchema.Empty)], TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(r[0].Ok, r[0].Text);
        Assert.Equal("maid_chan_67", seen);
        Assert.Equal("maid_chan_67", inner);
        Assert.Contains("eat", cc.CallList);
        Assert.Equal(0, router.MissingScopeCount);
        Assert.Null(BodyScope.Current); // the push never leaks to the caller
    }

    [Fact]
    public async Task Scope_reaches_the_script_thread_and_every_action()
    {
        var (reg, _, clone, _, cc) = TwoBodies();
        var router = new BodyRoutingGameClient(reg, b => b.Game);
        var seen = new ConcurrentQueue<string>();
        var host = new ScriptHost
        {
            World = new WorldState(),
            Username = clone.Name,
            Actions = [new ScriptAction("probe", [])],
            Act = async (_, _, ct) =>
            {
                seen.Enqueue(router.Username);
                seen.Enqueue(await Task.Run(() => router.Username, ct));
                await router.EatAsync(ct);
                return ActResult.Success("probed");
            },
        };
        using var lane = new ScriptController(host) { StepScope = () => BodyScope.Push(clone) };
        var (s, err) = lane.Start(new ScriptRequest { Name = "probe_twice", Code = "probe()\nprobe()" });
        Assert.Null(err);
        await s!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal(4, seen.Count);
        Assert.All(seen, n => Assert.Equal("maid_chan_67", n));
        Assert.Equal(2, cc.CallList.Count(c => c == "eat"));
        Assert.Null(BodyScope.Current);
    }

    [Fact]
    public void Worker_pushes_its_body_around_every_tool_call()
    {
        // The worker loop itself is covered by the Agent tests; here the hook BotHost installs pushes main for it.
        var (h, _) = TestBot.Create();
        using var _h = h;
        Assert.NotNull(h.Runtime.Worker!.StepScope);
        using (h.Runtime.Worker.StepScope!()) Assert.Same(h.Body, BodyScope.Current);
        Assert.Null(BodyScope.Current);
    }

    [Fact]
    public async Task Missing_scope_inside_an_activity_warns_and_acts_on_the_focus()
    {
        var (reg, _, _, mc, cc) = TwoBodies();
        var log = new ListLogger();
        var router = new BodyRoutingGameClient(reg, b => b.Game, log);
        var missing = new ConcurrentQueue<(string Member, string Body)>();
        router.MissingScope += (m, b) => missing.Enqueue((m, b.Name));
        var tools = new ToolRegistry(400);
        var activities = new ActivityManager(tools, new EventQueue()); // no StepScope: a forgotten push
        var probe = Probe(tools, "probe", async ct => { await router.EatAsync(ct); await router.EatAsync(ct); return ToolResult.Success("ok"); });
        var r = await activities.RunAsync([(probe, ToolSchema.Empty)], TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(r[0].Ok, r[0].Text);
        Assert.Contains("eat", mc.CallList); // the focus (main) acted
        Assert.DoesNotContain("eat", cc.CallList);
        Assert.Equal(2, router.MissingScopeCount);
        Assert.All(missing, x => Assert.Equal(("EatAsync", "maid_chan"), x));
        var warn = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning); // rate-limited: one line for both calls
        Assert.Contains("EatAsync", warn.Text);
        Assert.Contains("without a BodyScope", warn.Text);
    }

    [Fact]
    public async Task Registry_holds_exactly_main_and_the_tools_act_on_it()
    {
        var (h, c) = TestBot.Create();
        using var _h = h;
        var main = Assert.Single(h.Bodies!.All);
        Assert.Same(h.Body, main);
        Assert.True(main.IsMain);
        Assert.Equal(c.Username, main.Name);
        Assert.Equal("main", main.Short);
        Assert.Same(main, h.Bodies.Main);
        Assert.Same(main, h.Bodies.Focus);
        Assert.Same(main, h.Bodies.Find(c.Username));
        Assert.Same(main, h.Bodies.Find("main"));
        Assert.Equal(BodyState.Online, main.State);

        // Main's per-body pieces are the host's (and the runtime's activity slot is main's: an alias).
        Assert.Same(h.Runtime.Activities, main.Activities);
        Assert.Same(h.Runtime.Agent.Orders, main.Orders);
        Assert.Same(h.Bridge, main.Bridge);
        Assert.Same(h.Brainstem, main.Brainstem);
        Assert.Same(h.BodyGestures, main.Gestures);
        Assert.Same(h.Watchdog, main.Watchdog);
        Assert.Same(h.Handovers, main.Handovers);
        Assert.NotNull(main.Hunger);
        Assert.Same(GameTools.ScriptTools.Of(h.Runtime), h.Body!.ScriptBridge);
        Assert.NotNull(main.Scripts!.StepScope);
        Assert.NotNull(main.Activities.StepScope);
        Assert.Same(c, main.Adapter);
        Assert.Same(c, main.Game); // no claim board: the game tools see the plain adapter
        Assert.Same(main.Activities, h.Runtime.Agent.ActivitiesFor(null));
        Assert.Same(main.Activities, h.Runtime.Agent.ActivitiesFor(c.Username));
        Assert.Same(main.Activities, h.Runtime.Agent.ActivitiesFor("maid_chan_404")); // unknown body: main

        // Both routers resolve to main, and the type checks see what they saw before routing.
        Assert.Same(c, h.Brain!.Tools.Target);
        Assert.Same(c, h.Brain.Adapter.Target);
        Assert.Same(c, GameClients.Resolve(h.Brain.Tools));
        Assert.Null(GameClients.Claimed(h.Brain.Tools));
        Assert.NotNull(GameTools.Acquisition.Of(h.Brain.Tools)); // main's sources are found through the router

        // Tools act on main: an instant read, a direct action, and a job through the activity slot (scoped: no WARN).
        Assert.Equal(c.DescribeInventory(false, h.Runtime.Tools.MaxResultLength), (await h.Run("inventory", new { })).Text);
        var eat = await h.Run("eat", new { });
        Assert.True(eat.Ok, eat.Text);
        Assert.Contains("eat", c.CallList);
        Assert.True(h.Runtime.Tools.TryGet("goto", out var go));
        IBody? inScope = null;
        h.Runtime.Tools.Register("body_probe", "probe", ToolSchema.Empty, (_, _) => { inScope = BodyScope.Current; return Task.FromResult(ToolResult.Success("ok")); }, longRunning: true);
        Assert.True(h.Runtime.Tools.TryGet("body_probe", out var probe));
        var r = await h.Runtime.Activities.RunAsync([(go!, TestBot.J(new { to = "1,64,0" })), (probe!, ToolSchema.Empty)], TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.All(r, x => Assert.True(x.Ok, x.Text));
        Assert.Contains(c.CallList, x => x.StartsWith("goto", StringComparison.Ordinal));
        Assert.Same(main, inScope);
        Assert.Equal(0, h.Brain.Tools.MissingScopeCount);
        Assert.Equal(0, h.Brain.Adapter.MissingScopeCount);
    }

    [Fact]
    public void The_main_attach_refuses_a_clone_body_which_CreateClone_builds_instead()
    {
        var (h, c) = TestBot.Create();
        using var _h = h;
        var clone = new Body("maid_chan_67", isMain: false, new FakeGameClient { Username = "maid_chan_67" }, c,
            new ActivityManager(h.Runtime.Tools, h.Runtime.Events));
        Assert.Throws<NotSupportedException>(() => BodyFactory.Attach(h.Brain!, clone));
        Assert.Single(h.Bodies!.All);
        Assert.Throws<ArgumentException>(() => new BodyRegistry(clone));
        Assert.Equal("67", clone.Short);
    }

    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
