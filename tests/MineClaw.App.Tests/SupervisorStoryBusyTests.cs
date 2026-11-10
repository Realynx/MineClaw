using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent.Tools;
using MineClaw.App.Supervisor;
using MineClaw.Hive.Story;

namespace MineClaw.App.Tests;

/// <summary>
/// A colony command or read over HTTP waits for the colony no longer than its budget (docs/web/story-mode.md "The command wire"):
/// past it the answer is 503 busy with Retry-After, and a busy command was not applied and is not applied later. The colony's
/// lock is held by a gate (a pick parked in the host), never by a clock.
/// </summary>
public sealed class SupervisorStoryBusyTests
{
    private const string Token = "s3cret";
    private const string CommandBusy = """{"error":"busy: the colony did not take the change"}""";
    private const string ReadBusy = """{"error":"busy: the colony could not be read in time"}""";

    /// <summary>The route tests' host with a hook in <c>Live()</c>, which the colony calls under its lock while it picks a job.</summary>
    private sealed class GatedHost(SupervisorStoryRouteTests.World world) : IStoryHost
    {
        private readonly SupervisorStoryRouteTests.Host _inner = new(world);
        public Action? InLive;
        public IStoryWorld? World => _inner.World;
        public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer) => _inner.Spawn(colony, n, prefer);
        public int Release(string colony) => _inner.Release(colony);
        public PawnLive Live(string account) { InLive?.Invoke(); return _inner.Live(account); }
        public string? StartPawn(string account, string colony) => _inner.StartPawn(account, colony);
        public void Interrupt(string account, string why) => _inner.Interrupt(account, why);
    }

    /// <summary>A pick parked in the host's <c>Live()</c>: another thread holds the colony lock until this is disposed.</summary>
    private sealed class Held : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(), _release = new();
        private readonly GatedHost _host;
        private readonly Task _pick;

        public Held(StoryColony c, GatedHost host)
        {
            _host = host;
            string pawn = c.PawnNames()[0];
            int first = 1;
            host.InLive = () =>
            {
                if (Interlocked.Exchange(ref first, 0) == 0) return; // only the pick's first look parks
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(30));
            };
            _pick = Task.Run(() => c.Next(pawn));
            Assert.True(_entered.Wait(TimeSpan.FromSeconds(30)), "the pick never reached the host");
        }

        public void Dispose()
        {
            _release.Set();
            _pick.Wait(TimeSpan.FromSeconds(30));
            _host.InLive = null;
            _entered.Dispose();
            _release.Dispose();
        }
    }

    /// <summary>A colony behind the routes, with a chest block at 1,64,1 and a short budget (the waits here are all refused).</summary>
    private static (SupervisorStoryRoutes Routes, StoryColony Colony, GatedHost Host, string Path) Make()
    {
        var world = new SupervisorStoryRouteTests.World();
        world.Blocks[new P3(1, 64, 1)] = (54, 0);
        var host = new GatedHost(world);
        var routes = new SupervisorStoryRoutes(new SupervisorOptions { Token = Token, Viewer = { Enabled = true } }, new StoryColonies(host))
        {
            LockBudget = TimeSpan.FromMilliseconds(50),
        };
        var (colony, err) = routes.Colonies.Create("Riverside", 2, new P3(0, 64, 0));
        Assert.Null(err);
        return (routes, colony!, host, $"/api/story/colonies/{colony!.Id}");
    }

    private static async Task<SupervisorReply> Call(SupervisorStoryRoutes r, string method, string path, object? body = null, Dictionary<string, string>? query = null)
    {
        var reply = await r.TryHandleAsync(new SupervisorRequest(method, path, query ?? new(), Token, body == null ? null : JsonSerializer.Serialize(body)));
        Assert.NotNull(reply);
        return reply!;
    }

    private static void AssertBusy(string expected, SupervisorReply reply)
    {
        Assert.Equal(503, reply.Status);
        Assert.Equal(expected, reply.Text);
        Assert.Equal("1", reply.Headers!["Retry-After"]);
    }

    [Fact]
    public void The_budget_is_six_seconds_and_a_busy_answer_beats_the_bridges_command_timeout()
    {
        var routes = new SupervisorStoryRoutes(new SupervisorOptions { Token = Token, Viewer = { Enabled = true } },
            new StoryColonies(new GatedHost(new SupervisorStoryRouteTests.World())));
        Assert.Equal(TimeSpan.FromSeconds(6), routes.LockBudget);
        // The bridge (tools/ViewerBridge, SupervisorProxy.StoryCommandTimeout 14 s, SupervisorClient.CommandQueueBudget 4 s) gives
        // a command its queue, then the supervisor's 2 s slot wait, then this budget: with a second for the handler, all inside.
        var worst = TimeSpan.FromSeconds(4) + TimeSpan.FromSeconds(2) + routes.LockBudget + TimeSpan.FromSeconds(1);
        Assert.True(worst < TimeSpan.FromSeconds(14), $"{worst}");
    }

    [Fact]
    public void A_body_the_bridge_let_through_fits_after_it_adds_who()
    {
        const int bridgeLimit = 256 * 1024; // BridgeAuthMiddleware.JsonBodyLimit
        var who = $",\"who\":\"{new string('u', 200)}\"";
        Assert.True(bridgeLimit + System.Text.Encoding.UTF8.GetByteCount(who) <= SupervisorStoryRoutes.MaxBodyBytes);
    }

    [Fact]
    public async Task While_the_colony_is_held_a_settings_command_answers_busy_and_is_not_applied_later()
    {
        var (routes, colony, host, path) = Make();
        var body = new { op = "set", chests = new[] { new[] { 1, 64, 1 } }, filter = new { allow = new[] { "wood" } }, priority = "critical" };
        using (new Held(colony, host)) AssertBusy(CommandBusy, await Call(routes, "POST", path + "/storage", body));
        // Refused means not applied: no handler was left waiting on the lock to apply it once the pick let go.
        Assert.Empty(colony.Storages);

        var ok = await Call(routes, "POST", path + "/storage", body);
        Assert.Equal(200, ok.Status);
        var json = JsonNode.Parse(ok.Text)!;
        Assert.Equal("[[1,64,1]]", json["chests"]!.ToJsonString());
        long rev = json["rev"]!.GetValue<long>();
        Assert.Equal(colony.Rev, rev);
        // A reader one revision behind the answer finds the changed row in the delta that reaches it.
        var delta = JsonNode.Parse((await Call(routes, "GET", path, query: new() { ["since"] = (rev - 1).ToString() })).Text)!;
        Assert.True(delta["rev"]!.GetValue<long>() >= rev);
        var row = Assert.Single(delta["storages"]!.AsArray())!;
        Assert.Equal("[1,64,1]", row["chest"]!.ToJsonString());
        Assert.Equal("critical", row["priority"]!.GetValue<string>());
        Assert.Equal("""{"allow":["wood"],"deny":[]}""", row["filter"]!.ToJsonString());
    }

    [Theory]
    [InlineData("designations")] [InlineData("zones")] [InlineData("builds")] [InlineData("orders")] [InlineData("draft")]
    [InlineData("storage")] [InlineData("bills")] [InlineData("workbenches")] [InlineData("beds")] [InlineData("STORAGE")]
    public async Task Each_bounded_action_answers_busy_while_the_colony_is_held(string action)
    {
        var (routes, colony, host, path) = Make();
        using (new Held(colony, host)) AssertBusy(CommandBusy, await Call(routes, "POST", $"{path}/{action}", new { }));
        // The same request once the colony is free: the handler's own answer (its validation), no longer busy.
        Assert.Equal(400, (await Call(routes, "POST", $"{path}/{action}", new { })).Status);
    }

    [Fact]
    public async Task While_the_colony_is_held_its_state_and_the_list_answer_busy_and_what_needs_no_colony_answers_as_before()
    {
        var (routes, colony, host, path) = Make();
        using (new Held(colony, host))
        {
            AssertBusy(ReadBusy, await Call(routes, "GET", path));
            AssertBusy(ReadBusy, await Call(routes, "GET", path, query: new() { ["since"] = "0" }));
            AssertBusy(ReadBusy, await Call(routes, "GET", "/api/story"));
            Assert.Equal(404, (await Call(routes, "POST", "/api/story/colonies/story-9/storage", new { })).Status);
            Assert.Equal(400, (await Call(routes, "GET", path, query: new() { ["since"] = "x" })).Status);
            Assert.Equal(200, (await Call(routes, "GET", "/api/story/recipes")).Status);
            Assert.Equal(200, (await Call(routes, "GET", "/api/story/directives")).Status);
        }
        var state = await Call(routes, "GET", path);
        Assert.Equal(200, state.Status);
        Assert.Equal(colony.Id, JsonNode.Parse(state.Text)!["id"]!.GetValue<string>());
        var list = await Call(routes, "GET", "/api/story");
        Assert.Equal(200, list.Status);
        Assert.Equal(colony.Id, JsonNode.Parse(list.Text)!["colonies"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_colony_control_tool_answers_as_before_with_no_revision_added()
    {
        var (routes, colony, _, _) = Make();
        var tools = new ToolRegistry();
        routes.RegisterControllerTools(tools);
        Task<ToolResult> Run(object args) => tools.ExecuteAsync("colony_control", JsonSerializer.SerializeToElement(args), CancellationToken.None);

        // The brain reads these texts: the route's stamp must never reach them.
        var act = await Run(new { op = "act", colony = colony.Id, action = "storage", body = new { op = "set", chests = new[] { new[] { 1, 64, 1 } } } });
        Assert.True(act.Ok, act.Text);
        Assert.Equal("""{"chests":[[1,64,1]]}""", act.Text);
        var draft = await Run(new { op = "act", colony = colony.Id, action = "draft", body = new { pawns = new[] { colony.PawnNames()[0] }, drafted = true } });
        Assert.True(draft.Ok, draft.Text);
        Assert.Equal("""{"ok":true}""", draft.Text);
        var list = await Run(new { op = "list" });
        Assert.True(list.Ok, list.Text);
        Assert.Equal(colony.Id, JsonNode.Parse(list.Text)!["colonies"]![0]!["id"]!.GetValue<string>());
    }
}
