using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent.Tools;
using MineClaw.App.Supervisor;
using MineClaw.Hive.Story;

namespace MineClaw.App.Tests;

public sealed class StoryControllerTests
{
    private static (SupervisorStoryRoutes Routes, StoryColonies Colonies, ToolRegistry Tools, FakeViewerData Viewer, ViewerFeed Feed, SupervisorOptions Options) Setup(bool main = true)
    {
        var world = new SupervisorStoryRouteTests.World();
        var colonies = new StoryColonies(new SupervisorStoryRouteTests.Host(world));
        var viewer = new FakeViewerData();
        var feed = new ViewerFeed();
        var options = new SupervisorOptions { Token = "test-token", Viewer = { Enabled = true } };
        var routes = new SupervisorStoryRoutes(options, colonies, new StoryDirectives(viewer, feed),
            () => main ? [new BodyRow("maid_chan_67", "67", "main", null, true, true, 0, 0, 64, 0, 20, 20, null, "main", null, DateTimeOffset.UtcNow)] : [], viewer);
        var tools = new ToolRegistry();
        routes.RegisterControllerTools(tools);
        return (routes, colonies, tools, viewer, feed, options);
    }

    private static Task<ToolResult> Run(ToolRegistry tools, object args) => tools.ExecuteAsync("colony_control", JsonSerializer.SerializeToElement(args), CancellationToken.None);
    private static JsonObject Json(ToolResult result) { Assert.True(result.Ok, result.Text); return JsonNode.Parse(result.Text)!.AsObject(); }
    private static async Task<string> Create(ToolRegistry tools) => Json(await Run(tools, new { op = "act", action = "create", body = new { name = "Controller test", pawns = 2, home = new { x = 0, y = 64, z = 0 } } }))["id"]!.GetValue<string>();

    [Fact]
    public async Task Brain_edits_roles_schedule_and_zones_in_the_same_model_the_viewer_reads()
    {
        var (routes, colonies, tools, _, _, _) = Setup();
        var id = await Create(tools);
        var c = colonies.Get(id)!;
        var names = c.Pawns.Keys.ToArray();
        Json(await Run(tools, new { op = "act", colony = id, action = "priorities", body = new { pawns = new[] { names[0] }, role = "miner" } }));
        Json(await Run(tools, new { op = "act", colony = id, action = "schedule", body = new { pawns = new[] { names[0] }, hours = new Dictionary<string, string> { ["0"] = "sleep", ["6"] = "work" } } }));
        var zone = Json(await Run(tools, new { op = "act", colony = id, action = "zones", body = new { op = "add", kind = "guard", area = new[] { 2, 64, 2, 8, 64, 8 }, name = "Entrance" } }));
        Assert.NotNull(zone);
        var reply = await routes.TryHandleAsync(new SupervisorRequest("GET", $"/api/story/colonies/{id}", new Dictionary<string, string>(), "test-token"));
        var state = JsonNode.Parse(reply!.Text)!;
        var pawn = state["pawns"]!.AsArray().First(p => p!["name"]!.GetValue<string>() == names[0])!;
        Assert.Equal("miner", pawn["role"]!.GetValue<string>());
        Assert.Equal(1, pawn["priorities"]!["mine"]!.GetValue<int>());
        Assert.Equal("sleep", pawn["schedule"]![0]!.GetValue<string>());
        Assert.Equal("work", pawn["schedule"]![6]!.GetValue<string>());
        Assert.Contains(state["zones"]!.AsArray(), z => z!["kind"]!.GetValue<string>() == "guard" && z["name"]!.GetValue<string>() == "Entrance");
        Assert.NotEqual("miner", c.Pawns[names[1]].Role);
        Assert.False((await Run(tools, new { op = "act", colony = id, action = "priorities", body = new { pawns = names, work = new { mine = 9 } } })).Ok);
        Assert.Equal(1, c.Pawns[names[0]].Priorities["mine"]);
    }

    [Fact]
    public async Task Read_paginates_and_write_gate_still_applies_to_the_brain()
    {
        var (_, _, tools, _, _, options) = Setup();
        var id = await Create(tools);
        var page = Json(await Run(tools, new { op = "read", colony = id, section = "pawns", limit = 1 }));
        Assert.Equal(2, page["total"]!.GetValue<int>());
        Assert.Single(page["items"]!.AsArray());
        Assert.Equal(1, page["next_offset"]!.GetValue<int>());
        options.Viewer.AllowStory = false;
        Assert.True((await Run(tools, new { op = "read", colony = id, section = "zones" })).Ok);
        Assert.False((await Run(tools, new { op = "act", colony = id, action = "pause", body = new { paused = true } })).Ok);
        Assert.False((await Run(tools, new { op = "act", action = "time", body = new { time = 0 } })).Ok);
        options.Viewer.Enabled = false;
        Assert.False((await Run(tools, new { op = "read", colony = id })).Ok);
    }

    private static Task<SupervisorReply?> Send(SupervisorStoryRoutes routes, string colony, string text, string player = "poofimafox", string request = "controller-request-1") =>
        routes.TryHandleAsync(new SupervisorRequest("POST", "/api/story/directives", new Dictionary<string, string>(), "test-token", JsonSerializer.Serialize(new
        {
            player, bots = new[] { "maid_chan" }, directive = new { kind = "text", colony, text }, request_id = request,
        })));

    [Fact]
    public async Task Controller_delivery_uses_the_actual_main_preserves_the_full_request_and_deduplicates_retries()
    {
        var (routes, _, tools, viewer, feed, _) = Setup();
        var id = await Create(tools);
        var text = "Give the following workers a changed schedule: " + new string('a', 900) + " and leave everyone else unchanged.";
        var sent = await Send(routes, id, text);
        Assert.Equal(200, sent!.Status);
        var did = JsonNode.Parse(sent.Text)!["id"]!.GetValue<string>();
        var message = Assert.Single(viewer.Sent);
        Assert.Equal("maid_chan_67", message.Target); // not a hardcoded account or the client-supplied bot
        Assert.Contains("colony_control", message.Text);
        Assert.Contains(did, message.Text);
        var lookup = await tools.ExecuteAsync("story_directive", JsonSerializer.SerializeToElement(new { id = did }), CancellationToken.None);
        var full = Json(lookup);
        Assert.Equal(text, full["request"]!.GetValue<string>());
        Assert.Equal(id, full["colony"]!.GetValue<string>());
        Assert.Equal(text, Assert.Single(feed.Since(0, 10)).Text);
        Assert.Equal(200, (await Send(routes, id, text))!.Status);
        Assert.Single(viewer.Sent);
        Assert.Single(feed.Since(0, 10));
    }

    [Fact]
    public async Task Controller_chat_requires_a_trusted_player_valid_colony_and_main_bot()
    {
        var (routes, _, tools, viewer, _, _) = Setup();
        var id = await Create(tools);
        Assert.Equal(403, (await Send(routes, id, "change the roles", "stranger"))!.Status);
        Assert.Equal(404, (await Send(routes, "missing", "change the roles"))!.Status);
        Assert.Equal(400, (await Send(routes, id, new string('x', 4001)))!.Status);
        Assert.Empty(viewer.Sent);
        var absent = Setup(main: false);
        var other = await Create(absent.Tools);
        Assert.Equal(503, (await Send(absent.Routes, other, "change the roles"))!.Status);
    }
    [Fact]
    public async Task A_failed_controller_delivery_can_retry_the_same_request_without_replaying_success()
    {
        var (routes, _, tools, viewer, feed, _) = Setup();
        var colony = await Create(tools);
        viewer.Accept = _ => false;
        var first = JsonNode.Parse((await Send(routes, colony, "Set pawn 101 to miner"))!.Text)!;
        Assert.Empty(first["delivered"]!.AsArray());
        Assert.Single(viewer.Sent);
        feed.Add("maid_chan_67", "said", "maid_chan_67", "An unrelated reply before the retry", "bot");
        viewer.Accept = _ => true;
        var second = JsonNode.Parse((await Send(routes, colony, "Set pawn 101 to miner"))!.Text)!;
        Assert.Equal(first["id"]!.GetValue<string>(), second["id"]!.GetValue<string>());
        Assert.Equal("maid_chan_67", Assert.Single(second["delivered"]!.AsArray())!.GetValue<string>());
        Assert.Equal(2, viewer.Sent.Count);
        Assert.All(viewer.Sent, m => Assert.Equal("maid_chan_67", m.Target));
        Assert.True(second["feed_seq"]!.GetValue<long>() > first["feed_seq"]!.GetValue<long>());
        await Send(routes, colony, "Set pawn 101 to miner");
        Assert.Equal(2, viewer.Sent.Count);
    }

    [Fact]
    public async Task A_multi_bot_retry_only_delivers_to_the_previously_refused_targets()
    {
        var viewer = new FakeViewerData { Accept = m => m.Target == "maid_chan_67" };
        var directives = new StoryDirectives(viewer, new ViewerFeed());
        var request = new JsonObject { ["kind"] = "text", ["text"] = "Come here" };
        var (first, error) = await directives.SendAsync(["main", "67"], request, "poofimafox", false, "retry-multi", CancellationToken.None);
        Assert.Null(error);
        Assert.Equal("maid_chan_67", Assert.Single(first!.Delivered));
        viewer.Accept = _ => true;
        var (second, _) = await directives.SendAsync(["main", "67"], request, "poofimafox", false, "retry-multi", CancellationToken.None);
        Assert.Same(first, second);
        Assert.Equal(2, second!.Delivered.Count);
        Assert.Equal(2, viewer.Sent.Count(m => m.Target == "maid_chan"));
        Assert.Single(viewer.Sent.Where(m => m.Target == "maid_chan_67"));
        await directives.SendAsync(["main", "67"], request, "poofimafox", false, "retry-multi", CancellationToken.None);
        Assert.Equal(3, viewer.Sent.Count);
    }

}
