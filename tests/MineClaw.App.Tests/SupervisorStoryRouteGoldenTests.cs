using System.Text;
using System.Text.Json;
using MineClaw.App.Supervisor;
using MineClaw.Hive.Story;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="SupervisorStoryRoutes"/>'s routing and validation (WP07): the gate, every method/path pair and every colony
/// action's validation branches, with the error texts and statuses the web reads. Bodies of 2xx replies that come from the colony
/// state (owned by the Story package) print only their top-level keys. Each case runs on a fresh colony, under a fixed clock.
/// </summary>
public class SupervisorStoryRouteGoldenTests
{
    private const string Token = "s3cret";
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static SupervisorStoryRoutes Make(Action<SupervisorOptions>? tune = null)
    {
        var o = new SupervisorOptions { Token = Token, Viewer = { Enabled = true } };
        tune?.Invoke(o);
        var w = new SupervisorStoryRouteTests.World();
        var h = new SupervisorStoryRouteTests.Host(w);
        var v = new FakeViewerData();
        var clock = new ManualClock(T0);
        var d = new StoryDirectives(v, new ViewerFeed(time: clock)) { Data = () => w.Data, World = () => w, SaveDesign = (_, g, _) => g.Count > 0 ? 57 : null };
        IReadOnlyList<BodyRow> Ai() =>
        [
            new("maid_chan", "main", "main", null, true, true, 0, 1.234, 64, -2.5, 20, 18, "mine", "opus", null, T0),
            new("maid_chan_7", "7", "hive", "maid_chan", false, false, -1, 0, 0, 0, 0, 0, null, null, null, T0, HasPos: false),
            new("maid_chan_8", "8", "clone", "maid_chan_7", false, true, 0, 3, 70, 3, 15, 10, null, "sonnet", new MineClaw.Agent.Telemetry.BodyScriptBrief("farm", "running", 3, false, 2), T0),
            new("maid_chan_101", "101", "hive", null, false, true, 0, 0, 64, 0, 20, 20, null, null, null, T0),
        ];
        return new SupervisorStoryRoutes(o, new StoryColonies(h, time: clock), d, Ai, v, clock);
    }

    private static async Task<SupervisorReply?> Call(SupervisorStoryRoutes r, string method, string path, object? body = null, string? token = Token, string? query = null)
    {
        var json = body == null ? null : body is string s ? s : JsonSerializer.Serialize(body);
        return await r.TryHandleAsync(new SupervisorRequest(method, path, RouteGolden.Query(query), token, json));
    }

    private static async Task<string> NewColony(SupervisorStoryRoutes r)
    {
        var reply = await Call(r, "POST", "/api/story/colonies", new { name = "Riverside", pawns = 2, home = new { x = 0, y = 64, z = 0 } });
        return System.Text.Json.Nodes.JsonNode.Parse(reply!.Text)!["id"]!.GetValue<string>();
    }

    private static IEnumerable<(string Action, object Body)> ActionCases()
    {
        object[] none = [];
        var bed = new { x = 1, y = 64, z = 1 };
        var area = new[] { 0, 63, 0, 1, 63, 1 };
        yield return ("release", new { });
        yield return ("resume", new { });
        yield return ("RESUME", new { });
        yield return ("bogus", new { });
        yield return ("pawns", new { });
        yield return ("pawns", new { op = "kick" });
        yield return ("pawns", new { op = "add" });
        yield return ("pawns", new { op = "add", count = "x" });
        yield return ("pawns", new { op = "add", count = 0 });
        yield return ("pawns", new { op = "add", count = 13 });
        yield return ("pawns", new { op = "add", count = 11 });
        yield return ("pawns", new { op = "add", count = 1 });
        yield return ("pawns", new { op = "remove" });
        yield return ("pawns", new { op = "remove", pawn = "" });
        yield return ("pawns", new { op = "remove", pawn = "nobody" });
        yield return ("pawns", new { op = "remove", pawn = "maid_chan_101" });
        yield return ("online", new { });
        yield return ("online", new { pawns = (object?)null });
        yield return ("online", new { pawns = new object[] { 5 } });
        yield return ("online", new { pawns = new[] { " " } });
        yield return ("online", new { pawns = none });
        yield return ("online", new { pawns = "x" });
        yield return ("online", new { over_cap = "y" });
        yield return ("online", new { over_cap = true });
        yield return ("online", new { pawns = new[] { "nobody" } });
        yield return ("online", new { pawns = new[] { "maid_chan_101" }, over_cap = false });
        yield return ("home", new { });
        yield return ("home", new { home = new[] { 1, 2, 3 } });
        yield return ("home", new { home = new { x = 1.5, y = 64, z = 0 } });
        yield return ("home", new { home = new { x = 1, y = 500, z = 0 } });
        yield return ("home", new { home = new { x = 1, y = -65, z = 0 } });
        yield return ("home", new { home = new { x = 30_000_001, y = 64, z = 0 } });
        yield return ("home", new { home = new { x = 0, y = 64, z = -30_000_001 } });
        yield return ("home", new { home = new { x = 1, y = 70, z = 2 } });
        yield return ("beds", new { });
        yield return ("beds", new { op = "x" });
        yield return ("beds", new { op = "assign" });
        yield return ("beds", new { op = "assign", bed });
        yield return ("beds", new { op = "assign", bed, pawn = "" });
        yield return ("beds", new { op = "assign", bed, pawn = "nobody" });
        yield return ("beds", new { op = "assign", bed, pawn = "maid_chan_101" });
        yield return ("beds", new { op = "unassign", bed });
        yield return ("pause", new { });
        yield return ("pause", new { paused = "yes" });
        yield return ("pause", new { paused = true });
        yield return ("pause", new { paused = false });
        yield return ("priorities", new { pawns = new[] { "nobody" } });
        yield return ("priorities", new { role = "x", work = new { } });
        yield return ("priorities", new { role = 5 });
        yield return ("priorities", new { role = "" });
        yield return ("priorities", new { role = (string?)null });
        yield return ("priorities", new { role = "nosuch" });
        yield return ("priorities", new { });
        yield return ("priorities", new { work = 5 });
        yield return ("priorities", new { work = new { mine = 2 } });
        yield return ("priorities", new { work = new { bogus = 9, mine = "x" } });
        yield return ("schedule", new { });
        yield return ("schedule", new { pawns = new[] { "nobody" } });
        yield return ("schedule", new { hours = new Dictionary<string, object> { ["0"] = "sleep" } });
        yield return ("schedule", new { hours = new Dictionary<string, object> { ["0"] = 5 } });
        yield return ("draft", new { });
        yield return ("draft", new { drafted = 1 });
        yield return ("draft", new { drafted = true });
        yield return ("draft", new { pawns = new[] { "nobody" }, drafted = true });
        yield return ("orders", new { });
        yield return ("orders", new { order = 5 });
        yield return ("orders", new { pawns = new[] { "nobody" }, order = new { kind = "move" } });
        yield return ("orders", new { order = new { kind = "dance" } });
        yield return ("orders", new { order = new { kind = "move", target = new { x = 4, y = 64, z = 4 } } });
        yield return ("orders", new { order = new { kind = "move", target = new { x = 4, y = 64, z = 4 } }, queue = true });
        yield return ("orders", new { order = new { kind = "attack", target = "poofimafox" } });
        yield return ("roles", new { });
        yield return ("roles", new { op = "bogus" });
        yield return ("policies", new { });
        yield return ("material-rules", new { });
        yield return ("gear", new { });
        yield return ("gear", new { pawns = new[] { "nobody" } });
        yield return ("gear", new { pawns = 5 });
        yield return ("gear", new { op = "x" });
        yield return ("zones", new { });
        yield return ("zones", new { area = 5 });
        yield return ("zones", new { op = "bogus" });
        yield return ("zones", new { op = "add", kind = "farm", area });
        yield return ("zones", new { op = "add", kind = "home", area = new[] { -8, 60, -8, 8, 70, 8 } });
        yield return ("designations", new { });
        yield return ("designations", new { area = "x" });
        yield return ("designations", new { blocks = new[] { new[] { 1, 2 } } });
        yield return ("designations", new { op = "bogus" });
        yield return ("designations", new { op = "add", kind = "mine", blocks = new[] { new[] { 1, 60, 1 } } });
        yield return ("designations", new { op = "add", kind = "chop", area = new[] { 0, 60, 0, 6, 70, 6 } });
        yield return ("designations", new { op = "remove", ids = new[] { "d1" } });
        yield return ("builds", new { });
        yield return ("builds", new { op = "retry", cells = new[] { 5 } });
        yield return ("builds", new { op = "retry", id = "x" });
        yield return ("builds", new { op = "retry", ids = new[] { "x", "y" }, cells = new[] { new[] { 1, 64, 1 } } });
        yield return ("builds", new { op = "retry_all" });
        yield return ("builds", new { op = "bogus" });
        yield return ("builds", new { op = "remove", ids = new[] { "b1" } });
        yield return ("storage", new { });
        yield return ("storage", new { op = "add", chests = new[] { new[] { 1, 2 } } });
        yield return ("storage", new { op = "add", chests = new[] { new[] { 1, 64, 1 } } });
        yield return ("storage", new { op = "add", chests = new[] { new[] { 1, 64, 1 } }, filter = new { allow = new[] { "stone" } } });
        yield return ("storage", new { op = "update", chests = new[] { new[] { 1, 64, 1 } }, filter = new { deny = new[] { "dirt" } }, priority = "high", label = "Stone" });
        yield return ("storage", new { op = "copy", to = new[] { new[] { 1, 64, 1 } }, from = new[] { 0, 64, 0 } });
        yield return ("storage", new { op = "copy", to = new[] { new[] { 1 } }, from = new[] { 0, 64, 0 } });
        yield return ("storage", new { op = "zone", area });
        yield return ("bills", new { });
        yield return ("bills", new { op = "bogus" });
        yield return ("workbenches", new { });
        yield return ("workbenches", new { op = "designate", pos = new[] { 1, 64, 1 } });
        yield return ("workbenches", new { op = "fuel", pos = new[] { 1, 64, 1 }, fuel = "coal" });
        yield return ("options", new { });
        yield return ("options", new { auto_place_benches = "x" });
        yield return ("options", new { spare_tools = 5 });
        yield return ("options", new { idle = "wander" });
        yield return ("options", new { idle = 5 });
        yield return ("options", new { idle = "dance" });
        yield return ("options", new { pillar_stack = 99 });
        yield return ("options", new { pillar_stack = 8 });
        yield return ("options", new { pillar_stack = "x" });
        yield return ("options", new { stuck_teleport_after_s = 1 });
        yield return ("options", new { stuck_teleport_after_s = 60 });
        yield return ("options", new { speed = "fast" });
        yield return ("options", new { speed = "warp" });
        yield return ("options", new { speed = 3 });
        yield return ("options", new { speed_effect = true });
        yield return ("options", new { allow_lava = true });
        yield return ("options", new { auto_craft_tools = true });
        yield return ("options", new { spare_tools = new { count = 2, kinds = new[] { "pickaxe" } } });
        yield return ("options", new { rescue_teleport = true });
        yield return ("options", new { unfiltered_storage_takes_anything = false });
        yield return ("options", new { keep_disallowed = true });
        yield return ("options", new { auto_place_benches = true });
        yield return ("options", new { detect_benches = false, idle = "stay" });
        yield return ("options", new { auto_place_benches = (bool?)null, detect_benches = (bool?)null });
    }

    internal static async Task<string> Dump()
    {
        var sb = new StringBuilder();
        // The gate.
        RouteGolden.Append(sb, "not story", await Call(Make(), "GET", "/api/storyx"));
        RouteGolden.Append(sb, "viewer off", await Call(Make(o => o.Viewer.Enabled = false), "GET", "/api/story"));
        RouteGolden.Append(sb, "no token", await Call(Make(o => o.Token = null), "GET", "/api/story"));
        RouteGolden.Append(sb, "wrong token", await Call(Make(), "GET", "/api/story", token: "nope"));
        RouteGolden.Append(sb, "no header", await Call(Make(), "GET", "/api/story", token: null));
        RouteGolden.Append(sb, "PUT", await Call(Make(), "PUT", "/api/story"));
        RouteGolden.Append(sb, "writes off", await Call(Make(o => o.Viewer.AllowStory = false), "POST", "/api/story/colonies", new { pawns = 1 }));
        RouteGolden.Append(sb, "too large", await Call(Make(), "POST", "/api/story/colonies", SupervisorAdminRoutes.TooLarge));
        RouteGolden.Append(sb, "array body", await Call(Make(), "POST", "/api/story/colonies", "[1]"));
        RouteGolden.Append(sb, "bad json", await Call(Make(), "POST", "/api/story/colonies", "{nope"));
        RouteGolden.Append(sb, "blank body", await Call(Make(), "POST", "/api/story/colonies", "  "));
        // Top-level routes.
        var r = Make();
        var id = await NewColony(r);
        RouteGolden.Append(sb, "GET /", await Call(r, "GET", "/api/story/"), keysOnly: true);
        RouteGolden.Append(sb, "HEAD /", await Call(r, "HEAD", "/api/story"), keysOnly: true);
        RouteGolden.Append(sb, "GET / ai", await Call(r, "GET", "/api/story"), maxBody: 4000);
        RouteGolden.Append(sb, "POST /", await Call(r, "POST", "/api/story"));
        RouteGolden.Append(sb, "GET time", await Call(r, "GET", "/api/story/time"));
        RouteGolden.Append(sb, "POST time -1", await Call(r, "POST", "/api/story/time", new { time = -1 }));
        RouteGolden.Append(sb, "POST time x", await Call(r, "POST", "/api/story/time", new { time = "x" }));
        RouteGolden.Append(sb, "POST time 24000", await Call(r, "POST", "/api/story/time", new { time = 24000 }));
        RouteGolden.Append(sb, "POST time 100", await Call(r, "POST", "/api/story/time", new { time = 100 }));
        RouteGolden.Append(sb, "POST recipes", await Call(r, "POST", "/api/story/recipes"));
        RouteGolden.Append(sb, "GET recipes", await Call(r, "GET", "/api/story/recipes"), keysOnly: true);
        RouteGolden.Append(sb, "GET recipes furnace", await Call(r, "GET", "/api/story/recipes", query: "workbench=furnace"), keysOnly: true);
        RouteGolden.Append(sb, "GET recipes bogus", await Call(r, "GET", "/api/story/recipes", query: "workbench=bogus"));
        RouteGolden.Append(sb, "GET colonies", await Call(r, "GET", "/api/story/colonies"));
        RouteGolden.Append(sb, "POST colonies {}", await Call(r, "POST", "/api/story/colonies", new { }));
        RouteGolden.Append(sb, "POST colonies pawns x", await Call(r, "POST", "/api/story/colonies", new { pawns = "x" }));
        RouteGolden.Append(sb, "POST colonies home 5", await Call(r, "POST", "/api/story/colonies", new { pawns = 2, home = 5 }));
        RouteGolden.Append(sb, "POST colonies home {}", await Call(r, "POST", "/api/story/colonies", new { pawns = 2, home = new { } }));
        RouteGolden.Append(sb, "POST colonies no name", await Call(r, "POST", "/api/story/colonies", new { pawns = 2 }));
        RouteGolden.Append(sb, "POST colonies 99", await Call(r, "POST", "/api/story/colonies", new { name = "Big", pawns = 99 }));
        RouteGolden.Append(sb, "POST colonies name 5", await Call(r, "POST", "/api/story/colonies", new { name = 5, pawns = 2 }));
        RouteGolden.Append(sb, "POST colonies ok", await Call(r, "POST", "/api/story/colonies", new { name = "Hilltop", pawns = 1 }));
        RouteGolden.Append(sb, "POST colony", await Call(r, "POST", $"/api/story/colonies/{id}"));
        RouteGolden.Append(sb, "GET colony nobody", await Call(r, "GET", "/api/story/colonies/nobody"));
        RouteGolden.Append(sb, "GET colony since x", await Call(r, "GET", $"/api/story/colonies/{id}", query: "since=x"));
        RouteGolden.Append(sb, "GET colony since -1", await Call(r, "GET", $"/api/story/colonies/{id}", query: "since=-1"));
        RouteGolden.Append(sb, "GET colony", await Call(r, "GET", $"/api/story/colonies/{id}"), keysOnly: true);
        RouteGolden.Append(sb, "GET colony since 0", await Call(r, "GET", $"/api/story/colonies/{id}/", query: "since=0"), keysOnly: true);
        RouteGolden.Append(sb, "POST architect", await Call(r, "POST", $"/api/story/colonies/{id}/architect"));
        RouteGolden.Append(sb, "GET architect nobody", await Call(r, "GET", "/api/story/colonies/nobody/architect"));
        RouteGolden.Append(sb, "GET architect", await Call(r, "GET", $"/api/story/colonies/{id}/architect"), keysOnly: true);
        RouteGolden.Append(sb, "GET action", await Call(r, "GET", $"/api/story/colonies/{id}/pause"));
        RouteGolden.Append(sb, "POST action nobody", await Call(r, "POST", "/api/story/colonies/nobody/pause", new { paused = true }));
        RouteGolden.Append(sb, "GET directives", await Call(r, "GET", "/api/story/directives"));
        RouteGolden.Append(sb, "POST directives {}", await Call(r, "POST", "/api/story/directives", new { }));
        RouteGolden.Append(sb, "POST directives no directive", await Call(r, "POST", "/api/story/directives", new { player = "PoofImaFox " }));
        RouteGolden.Append(sb, "POST directives no bots", await Call(r, "POST", "/api/story/directives", new { player = "poofimafox", directive = new { kind = "chop" } }));
        RouteGolden.Append(sb, "POST directives bad rid", await Call(r, "POST", "/api/story/directives",
            new { player = "poofimafox", directive = new { kind = "chop" }, bots = new[] { "main" }, request_id = "a b" }));
        RouteGolden.Append(sb, "POST directives no bot", await Call(r, "POST", "/api/story/directives",
            new { player = "poofimafox", directive = new { kind = "chop", area = new[] { 0, 60, 0, 4, 70, 4 } }, bots = new[] { "nobody" } }));
        RouteGolden.Append(sb, "POST directives bad kind", await Call(r, "POST", "/api/story/directives",
            new { player = "poofimafox", directive = new { kind = "teleport" }, bots = new[] { "main" } }));
        RouteGolden.Append(sb, "POST directives ok", await Call(r, "POST", "/api/story/directives",
            new { player = "poofimafox", directive = new { kind = "chop", area = new[] { 0, 60, 0, 4, 70, 4 } }, bots = new[] { "main", "67" }, request_id = "req-1" }));
        RouteGolden.Append(sb, "POST directive", await Call(r, "POST", "/api/story/directives/x"));
        RouteGolden.Append(sb, "GET directive nobody", await Call(r, "GET", "/api/story/directives/nobody"));
        RouteGolden.Append(sb, "GET deep", await Call(r, "GET", "/api/story/a/b/c/d"));
        RouteGolden.Append(sb, "GET escaped", await Call(r, "GET", "/api/story/colonies/no%20body"));
        // Directives with no viewer path.
        var bare = new SupervisorStoryRoutes(new SupervisorOptions { Token = Token, Viewer = { Enabled = true } }, new StoryColonies(new SupervisorStoryRouteTests.Host(new())));
        RouteGolden.Append(sb, "bare GET /", await Call(bare, "GET", "/api/story"));
        RouteGolden.Append(sb, "bare POST directives", await Call(bare, "POST", "/api/story/directives", new { player = "poofimafox" }));
        RouteGolden.Append(sb, "bare GET directive", await Call(bare, "GET", "/api/story/directives/x"));
        // Every colony action, each on a fresh colony.
        foreach (var (action, body) in ActionCases())
        {
            var rc = Make();
            var cid = await NewColony(rc);
            RouteGolden.Append(sb, $"{action} {JsonSerializer.Serialize(body)}", await Call(rc, "POST", $"/api/story/colonies/{cid}/{action}", body), maxBody: 300);
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Story_routes_match_the_golden_dump() =>
        RouteGolden.Check("story-routes", SupervisorStoryRouteGoldenData.Expected, await Dump());
}
