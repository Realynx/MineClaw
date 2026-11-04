using System.Text.Json.Nodes;
using MineClaw.App.Supervisor;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.App.Tests;

/// <summary>Story Mode's routes: the viewer's gate, AllowStory, validation and the JSON shapes (docs/web/story-mode.md).</summary>
public sealed class SupervisorStoryRouteTests
{
    private const string Token = "s3cret";

    internal sealed class World : IStoryWorld
    {
        public IGameData Data { get; init; } = Beta181GameData.Instance;
        public long TimeOfDay { get; set; } = 6000;
        public int Height => 128;
        public Dictionary<P3, (int, int)> Blocks { get; } = new();
        public List<StoryEntity> Ents { get; } = new();
        public (int Id, int Meta) Block(int x, int y, int z) => Blocks.TryGetValue(new P3(x, y, z), out var b) ? b : y <= 63 ? (1, 0) : (0, 0);
        public IReadOnlyList<StoryEntity> Entities() => Ents;
    }

    internal sealed class Host(World w) : IStoryHost
    {
        public IStoryWorld? World => w;
        public int N = 101;
        public HashSet<string> Released = new();
        public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer) =>
            (Enumerable.Range(0, n).Select(_ => "maid_chan_" + N++).ToList(), null);
        public int Release(string colony) { Released.Add(colony); return 1; }
        public PawnLive Live(string account) => new(true, 20, 20, 0.5, 64, 0.5, 0, "stone_axe", [new Stack(17, 0, 3)], true);
        public string? StartPawn(string account, string colony) => null;
        public void Interrupt(string account, string why) { }
    }

    private static (SupervisorStoryRoutes R, Host H, World W, FakeViewerData V, ViewerFeed F) Make(Action<SupervisorOptions>? tune = null)
    {
        var o = new SupervisorOptions { Token = Token, Viewer = { Enabled = true } };
        tune?.Invoke(o);
        var w = new World();
        var h = new Host(w);
        var v = new FakeViewerData();
        var f = new ViewerFeed();
        var d = new StoryDirectives(v, f) { Data = () => w.Data, World = () => w, SaveDesign = (_, g, _) => g.Count > 0 ? 57 : null };
        return (new SupervisorStoryRoutes(o, new StoryColonies(h), d, null, v), h, w, v, f);
    }

    private static async Task<(int Status, JsonNode? Json)> Call(SupervisorStoryRoutes r, string method, string path, object? body = null,
        string? token = Token, Dictionary<string, string>? query = null)
    {
        var json = body == null ? null : body is string s ? s : System.Text.Json.JsonSerializer.Serialize(body);
        var reply = await r.TryHandleAsync(new SupervisorRequest(method, path, query ?? new(), token, json));
        Assert.NotNull(reply);
        return (reply!.Status, reply.Body.Length > 0 ? JsonNode.Parse(reply.Text) : null);
    }

    private static async Task<string> Colony(SupervisorStoryRoutes r)
    {
        var (s, j) = await Call(r, "POST", "/api/story/colonies", new { name = "Riverside", pawns = 2, home = new { x = 0, y = 64, z = 0 } });
        Assert.Equal(200, s);
        return j!["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task Pawns_are_added_and_removed_and_growing_zones_take_a_plant_with_the_exact_wire_shape()
    {
        var (r, _, w, _, _) = Make();
        var id = await Colony(r);
        var (s, j) = await Call(r, "POST", $"/api/story/colonies/{id}/pawns", new { op = "add", count = 2 });
        Assert.Equal(200, s);
        Assert.Equal(4, j!["pawns"]!.AsArray().Count);
        Assert.Equal(4, j["wanted"]!.GetValue<int>());
        Assert.Empty(j["pending"]!.AsArray());
        Assert.Empty(j["blocked"]!.AsArray());
        Assert.Equal(400, (await Call(r, "POST", $"/api/story/colonies/{id}/pawns", new { op = "add", count = 0 })).Status);
        Assert.Equal(400, (await Call(r, "POST", $"/api/story/colonies/{id}/pawns", new { op = "add", count = 9 })).Status); // past 12
        Assert.Equal(400, (await Call(r, "POST", $"/api/story/colonies/{id}/pawns", new { op = "kick" })).Status);
        Assert.Equal(404, (await Call(r, "POST", $"/api/story/colonies/{id}/pawns", new { op = "remove", pawn = "nobody" })).Status);
        var gone = j["pawns"]![0]!.GetValue<string>();
        (s, j) = await Call(r, "POST", $"/api/story/colonies/{id}/pawns", new { op = "remove", pawn = gone });
        Assert.Equal(200, s);
        Assert.Equal(3, j!["wanted"]!.GetValue<int>());
        Assert.Equal(gone, j["leaving"]![0]!.GetValue<string>());

        // A growing zone: a plant this version grows, update, seek seeds.
        w.Blocks[new P3(10, 63, 0)] = (60, 0);
        (s, j) = await Call(r, "POST", $"/api/story/colonies/{id}/zones",
            new { op = "add", kind = "farm", area = new[] { 10, 63, 0, 10, 63, 0 }, plant = "wheat" });
        Assert.Equal(200, s);
        var zone = j!["id"]!.GetValue<string>();
        Assert.Equal(400, (await Call(r, "POST", $"/api/story/colonies/{id}/zones",
            new { op = "add", kind = "farm", area = new[] { 10, 63, 0, 10, 63, 0 }, plant = "carrots" })).Status); // not in b1.8.1
        Assert.Equal(200, (await Call(r, "POST", $"/api/story/colonies/{id}/zones", new { op = "update", id = zone, harvest = false })).Status);
        var (_, state) = await Call(r, "GET", $"/api/story/colonies/{id}");
        var z = state!["zones"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == zone)!;
        Assert.Equal("wheat", z["plant"]!.GetValue<string>());
        Assert.False(z["harvest"]!.GetValue<bool>());
        Assert.Equal("seek_seeds", z["seeds"]!["action"]!.GetValue<string>());
        Assert.Contains(state["plants"]!.AsArray(), p => p!["id"]!.GetValue<string>() == "wheat");
        Assert.Equal(400, (await Call(r, "POST", $"/api/story/colonies/{id}/zones", new { op = "seek_seeds", id = zone })).Status); // no grass near
        w.Blocks[new P3(12, 64, 3)] = (31, 1);
        (s, j) = await Call(r, "POST", $"/api/story/colonies/{id}/zones", new { op = "seek_seeds", id = zone });
        Assert.Equal(200, s);
        Assert.Equal(1, j!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_viewer_gate_the_token_and_AllowStory_guard_every_route()
    {
        var (off, _, _, _, _) = Make(o => o.Viewer.Enabled = false);
        Assert.Equal(404, (await Call(off, "GET", "/api/story")).Status);
        var (noToken, _, _, _, _) = Make(o => o.Token = null);
        Assert.Equal(403, (await Call(noToken, "GET", "/api/story")).Status);
        var (r, _, _, _, _) = Make();
        Assert.Equal(403, (await Call(r, "GET", "/api/story", token: null)).Status);
        Assert.Equal(403, (await Call(r, "GET", "/api/story", token: "wrong!")).Status);
        Assert.Equal(200, (await Call(r, "GET", "/api/story")).Status);
        Assert.Equal(405, (await Call(r, "DELETE", "/api/story")).Status);
        Assert.Equal(405, (await Call(r, "POST", "/api/story", new { })).Status);
        Assert.Equal(404, (await Call(r, "GET", "/api/story/colonies/story-9")).Status);
        Assert.Equal(404, (await Call(r, "GET", "/api/story/nope")).Status);
        Assert.Equal(400, (await Call(r, "POST", "/api/story/colonies", "not json")).Status);
        Assert.True(SupervisorStoryRoutes.Writes(new SupervisorOptions { Viewer = { Enabled = true } })); // default on with the viewer
        var (ro, _, _, _, _) = Make(o => o.Viewer.AllowStory = false);
        Assert.Equal(403, (await Call(ro, "POST", "/api/story/colonies", new { name = "x", pawns = 1 })).Status);
        Assert.Equal(200, (await Call(ro, "GET", "/api/story")).Status); // viewers can still watch
        Assert.Null(await r.TryHandleAsync(new SupervisorRequest("GET", "/api/viewer/feed", new Dictionary<string, string>())));
    }

    [Fact]
    public async Task Colonies_are_created_listed_paused_released_and_their_state_has_the_contract_shape()
    {
        var (r, h, _, _, _) = Make();
        Assert.Equal(400, (await Call(r, "POST", "/api/story/colonies", new { name = "x", pawns = 0 })).Status);
        Assert.Equal(400, (await Call(r, "POST", "/api/story/colonies", new { name = "", pawns = 2 })).Status);
        Assert.Equal(400, (await Call(r, "POST", "/api/story/colonies", new { name = "x", pawns = 13 })).Status);
        var id = await Colony(r);
        Assert.Equal("story-1", id);
        var (_, list) = await Call(r, "GET", "/api/story");
        var row = list!["colonies"]![0]!;
        foreach (var k in new[] { "id", "name", "created", "paused", "pawns", "version" }) Assert.NotNull(row.AsObject().ContainsKey(k) ? row : null);
        Assert.Equal(2, row["pawns"]!.AsArray().Count);
        Assert.NotNull(list["ai"]);
        Assert.NotNull(list["directives"]);

        var (s, st) = await Call(r, "GET", $"/api/story/colonies/{id}");
        Assert.Equal(200, s);
        foreach (var k in new[] { "rev", "at", "time_of_day", "hour", "paused", "work_types", "pawns", "zones", "designations", "orders", "alerts", "builds", "ghosts", "storages", "workbenches", "categories", "ai", "directives" })
            Assert.True(st!.AsObject().ContainsKey(k), k);
        Assert.Equal(12, st!["hour"]!.GetValue<int>());
        Assert.Equal(WorkTypes.All, st["work_types"]!.AsArray().Select(x => x!.GetValue<string>()));
        var pawn = st["pawns"]![0]!;
        Assert.Equal("pawn", pawn["kind"]!.GetValue<string>());
        Assert.Equal(3, pawn["pos"]!.AsArray().Count);
        Assert.Equal(new JsonArray(17, 0, 3).ToJsonString(), pawn["inventory"]![0]!.ToJsonString());
        long rev = st["rev"]!.GetValue<long>();
        Assert.Equal(400, (await Call(r, "GET", $"/api/story/colonies/{id}", query: new() { ["since"] = "x" })).Status);
        var (_, delta) = await Call(r, "GET", $"/api/story/colonies/{id}", query: new() { ["since"] = rev.ToString() });
        Assert.False(delta!["full"]!.GetValue<bool>());
        Assert.Null(delta["categories"]);

        Assert.Equal(400, (await Call(r, "POST", $"/api/story/colonies/{id}/pause", new { paused = "yes" })).Status);
        Assert.True((await Call(r, "POST", $"/api/story/colonies/{id}/pause", new { paused = true })).Json!["paused"]!.GetValue<bool>());
        Assert.Equal(200, (await Call(r, "POST", $"/api/story/colonies/{id}/release", new { })).Status);
        Assert.Contains(id, h.Released);
        Assert.False((await Call(r, "GET", "/api/story")).Json!["colonies"]![0]!["active"]!.GetValue<bool>());
        Assert.Equal(200, (await Call(r, "POST", $"/api/story/colonies/{id}/resume", new { })).Status);
    }

    [Fact]
    public async Task Every_colony_write_validates_and_answers_its_shape()
    {
        var (r, _, w, _, _) = Make();
        var id = await Colony(r);
        var b = $"/api/story/colonies/{id}/";
        var (_, st) = await Call(r, "GET", "/api/story/colonies/" + id);
        var pawn = st!["pawns"]![0]!["name"]!.GetValue<string>();

        Assert.Equal(400, (await Call(r, "POST", b + "priorities", new { pawns = new[] { "nobody" }, work = new { chop = 1 } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "priorities", new { pawns = new[] { pawn }, work = new { dance = 1 } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "priorities", new { pawns = new[] { pawn }, work = new { chop = 5 } })).Status);
        Assert.Equal(200, (await Call(r, "POST", b + "priorities", new { pawns = new[] { pawn }, work = new { chop = 1, haul = 0 } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "schedule", new { pawns = new[] { pawn }, hours = new Dictionary<string, string> { ["24"] = "work" } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "schedule", new { pawns = new[] { pawn }, hours = new Dictionary<string, string> { ["3"] = "party" } })).Status);
        Assert.Equal(200, (await Call(r, "POST", b + "schedule", new { pawns = new[] { pawn }, hours = new Dictionary<string, string> { ["3"] = "work", ["23"] = "sleep" } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "draft", new { pawns = new[] { pawn } })).Status);
        Assert.Equal(200, (await Call(r, "POST", b + "draft", new { pawns = new[] { pawn }, drafted = true })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "orders", new { pawns = new[] { pawn }, order = new { kind = "dance" } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "orders", new { pawns = new[] { pawn }, order = new { kind = "move" } })).Status);
        var (os, oj) = await Call(r, "POST", b + "orders", new { pawns = new[] { pawn }, order = new { kind = "move", target = new { x = 4, y = 64, z = 4 } }, queue = true });
        Assert.Equal(200, os);
        Assert.Single(oj!["ids"]!.AsArray());
        Assert.Equal(400, (await Call(r, "POST", b + "zones", new { op = "add", kind = "stockpile", area = new[] { 0, 60, 0, 4, 70, 4 } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "zones", new { op = "add", kind = "farm", area = new[] { 0, 60 } })).Status);
        Assert.StartsWith("z", (await Call(r, "POST", b + "zones", new { op = "add", kind = "home", area = new[] { -8, 60, -8, 8, 70, 8 } })).Json!["id"]!.GetValue<string>());
        w.Blocks[new P3(3, 64, 3)] = (17, 0); w.Blocks[new P3(3, 65, 3)] = (17, 0);
        var (ds, dj) = await Call(r, "POST", b + "designations", new { op = "add", kind = "chop", area = new[] { 0, 60, 0, 6, 70, 6 } });
        Assert.Equal(200, ds);
        Assert.Equal(1, dj!["count"]!.GetValue<int>());
        Assert.Equal("[[3,64,3]]", dj["targets"]!.ToJsonString());
        Assert.Equal(400, (await Call(r, "POST", b + "designations", new { op = "add", kind = "mine", area = new[] { 0, 60, 0, 6, 70, 6 }, filter = "gold" })).Status);
        Assert.Equal(1, (await Call(r, "POST", b + "designations", new { op = "cancel", area = new[] { 0, 60, 0, 6, 70, 6 } })).Json!["count"]!.GetValue<int>());
        var (bs, bj) = await Call(r, "POST", b + "builds", new
        {
            op = "add", items = new object[] { new { kind = "wall", area = new[] { 10, 64, 10, 12, 64, 12 }, material = "cobblestone", height = 2, roof = new { style = "flat" } } },
        });
        Assert.Equal(200, bs);
        var bid = bj!["ids"]![0]!.GetValue<string>();
        Assert.Equal(400, (await Call(r, "POST", b + "builds", new { op = "add", items = new object[] { new { kind = "tower", area = new[] { 0, 64, 0, 1, 64, 1 } } } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "builds", new { op = "add", items = new object[] { new { kind = "wall", area = new[] { 0, 64, 0, 1, 64, 1 }, material = "unobtanium" } } })).Status);
        Assert.Equal(200, (await Call(r, "POST", b + "builds", new { op = "pause", ids = new[] { bid } })).Status);
        var (_, st2) = await Call(r, "GET", "/api/story/colonies/" + id);
        var build = st2!["builds"]![0]!;
        Assert.Equal("paused", build["status"]!.GetValue<string>());
        Assert.Equal(8 * 2 + 9, build["blocks_total"]!.GetValue<int>());
        Assert.Equal(7, st2["ghosts"]!["blocks"]![0]!.AsArray().Count); // [x,y,z,id,meta,build_id,state]
        w.Blocks[new P3(1, 64, 1)] = (54, 0);
        var (ss, sj) = await Call(r, "POST", b + "storage", new { op = "assign_area", area = new[] { 0, 60, 0, 3, 70, 3 }, filter = new { allow = new[] { "wood" } }, priority = "critical" });
        Assert.Equal(200, ss);
        Assert.Equal("[[1,64,1]]", sj!["chests"]!.ToJsonString());
        Assert.Equal(400, (await Call(r, "POST", b + "storage", new { op = "set", chests = new[] { new[] { 1, 64, 1 } }, priority = "max" })).Status);
        Assert.Equal(200, (await Call(r, "POST", b + "storage", new { op = "copy", from = new[] { 1, 64, 1 }, to = new[] { new[] { 2, 64, 1 } } })).Status);
        var storages = (await Call(r, "GET", "/api/story/colonies/" + id)).Json!["storages"]!.AsArray();
        Assert.Equal(2, storages.Count);
        Assert.All(storages, x => Assert.Equal("critical", x!["priority"]!.GetValue<string>()));
        w.Blocks[new P3(5, 64, 5)] = (58, 0);
        var (bls, blj) = await Call(r, "POST", b + "bills", new { op = "add", bench = new[] { 5, 64, 5 }, bill = new { recipe = "craft:stick", mode = "count", target = 8 } });
        Assert.Equal(200, bls);
        Assert.StartsWith("bill", blj!["id"]!.GetValue<string>());
        Assert.Equal(400, (await Call(r, "POST", b + "bills", new { op = "add", bench = new[] { 9, 64, 9 }, bill = new { recipe = "craft:stick" } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "bills", new { op = "add", bench = new[] { 5, 64, 5 }, bill = new { recipe = "craft:stick", mode = "sometimes" } })).Status);
        var bench = (await Call(r, "GET", "/api/story/colonies/" + id)).Json!["workbenches"]![0]!;
        Assert.Equal("crafting_table", bench["kind"]!.GetValue<string>());
        Assert.Equal("craft:stick", bench["bills"]![0]!["recipe"]!.GetValue<string>());
        var (rs, recipes) = await Call(r, "GET", "/api/story/recipes", query: new() { ["workbench"] = "furnace" });
        Assert.Equal(200, rs);
        Assert.Contains(recipes!.AsArray(), x => x!["id"]!.GetValue<string>() == "smelt:porkchop" && x["work_type"]!.GetValue<string>() == "cook");
        Assert.Equal(400, (await Call(r, "GET", "/api/story/recipes", query: new() { ["workbench"] = "oven" })).Status);
        Assert.Equal(404, (await Call(r, "POST", b + "teleport", new { })).Status);
    }

    [Fact]
    public async Task Directives_reach_AI_bots_as_one_compact_line_with_commands_gated_and_status_from_their_replies()
    {
        var (r, _, w, v, feed) = Make();
        for (int i = 0; i < 14; i++) { w.Blocks[new P3(120 + i, 64, 250)] = (17, 0); }
        var area = new[] { 120, 64, 250, 140, 70, 270 };
        Assert.Equal(403, (await Call(r, "POST", "/api/story/directives", new { bots = new[] { "main" }, directive = new { kind = "chop", area }, player = "stranger" })).Status);
        Assert.Equal(404, (await Call(r, "POST", "/api/story/directives", new { bots = new[] { "nobody" }, directive = new { kind = "chop", area }, player = "poofimafox" })).Status);
        Assert.Equal(400, (await Call(r, "POST", "/api/story/directives", new { bots = new[] { "main" }, directive = new { kind = "teleport" }, player = "poofimafox" })).Status);
        var (s, j) = await Call(r, "POST", "/api/story/directives",
            new { bots = new[] { "main", "67" }, directive = new { kind = "chop", area, text = "stash logs in the Wood chest" }, player = "poofimafox", allow_commands = true, request_id = "req-1" });
        Assert.Equal(200, s);
        Assert.Equal(["maid_chan", "maid_chan_67"], j!["delivered"]!.AsArray().Select(x => x!.GetValue<string>()).Order());
        var line = j["line"]!.GetValue<string>();
        Assert.StartsWith("poofimafox (story map): chop the 14 trees in the area 120,64,250 → 140,70,270 — stash logs in the Wood chest", line);
        Assert.True(line.Length <= StoryDirectives.MaxLine);
        Assert.Equal(2, v.Sent.Count);
        Assert.Contains(v.Sent, c => c.Target == "maid_chan_67"); // a hive's body: its owning brain's router takes the line, addressed to it
        Assert.All(v.Sent, c => { Assert.Equal("viewer", c.Origin); Assert.False(c.AllowCommands); }); // Viewer:AllowCommands is off
        var id = j["id"]!.GetValue<string>();

        // The bot's own next line correlates: acknowledged, then done when it says so.
        feed.Add("maid_chan", "said", "maid_chan", "on it, heading over", "bot");
        Assert.Equal("acknowledged", (await Call(r, "GET", "/api/story/directives/" + id)).Json!["status"]!.GetValue<string>());
        feed.Add("maid_chan", "said", "maid_chan", "done, all chopped", "bot");
        Assert.Equal("done", (await Call(r, "GET", "/api/story/directives/" + id)).Json!["status"]!.GetValue<string>());
        Assert.Equal(14, (await Call(r, "GET", "/api/story/directives/" + id)).Json!["detail"]!["targets"]!.AsArray().Count);

        // A build becomes a saved design; the line names it.
        var (bs, bj) = await Call(r, "POST", "/api/story/directives", new
        {
            bots = new[] { "main" }, player = "poofimafox",
            directive = new { kind = "build", build = new { kind = "wall", name = "hut", area = new[] { 130, 64, 260, 133, 64, 263 }, material = "planks", height = 3, roof = new { style = "flat" } } },
        });
        Assert.Equal(200, bs);
        Assert.Equal(57, bj!["design"]!.GetValue<long>());
        Assert.Contains("build design #57 'hut'", bj["line"]!.GetValue<string>());

        // Commands only with Viewer:AllowCommands on and asked for.
        var (r2, _, _, v2, _) = Make(o => o.Viewer.AllowCommands = true);
        await Call(r2, "POST", "/api/story/directives", new { bots = new[] { "main" }, directive = new { kind = "text", text = "make it day" }, player = "poofimafox", allow_commands = true });
        Assert.True(v2.Sent.Single().AllowCommands);
    }

    [Theory]
    [InlineData("chop")] [InlineData("cut")] [InlineData("harvest")] [InlineData("mine")] [InlineData("haul")] [InlineData("hunt")]
    [InlineData("deconstruct")] [InlineData("build")] [InlineData("move")] [InlineData("attack")] [InlineData("guard")] [InlineData("craft")] [InlineData("text")]
    public void Every_directive_kind_renders_one_short_line(string kind)
    {
        var d = new JsonObject
        {
            ["kind"] = kind, ["area"] = new JsonArray(-1200, 64, 250, -1180, 70, 270), ["text"] = kind == "text" ? "come to the farm please" : null,
            ["build"] = new JsonObject { ["kind"] = "wall", ["material"] = "cobblestone", ["height"] = 3 }, ["bill"] = new JsonObject { ["recipe"] = "craft:stick", ["target"] = 8 },
        };
        var line = StoryDirectives.Render("poofimafox", kind, d, "dir12", count: 14);
        Assert.True(line.Length <= StoryDirectives.MaxLine, line);
        Assert.StartsWith("poofimafox (story map): ", line);
        Assert.EndsWith("[dir12]", line);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public async Task The_supervisor_routes_story_paths_and_posts_to_it()
    {
        var (r, _, _, _, _) = Make();
        await using var server = new SupervisorServer(new SupervisorOptions { Token = Token, Viewer = { Enabled = true } }, new FakeSupervisorData()) { Story = r };
        var post = await server.HandleAsync(new SupervisorRequest("POST", "/api/story/colonies", new Dictionary<string, string>(), Token, """{"name":"A","pawns":1}"""));
        Assert.Equal(200, post.Status);
        Assert.Equal(200, (await server.HandleAsync(new SupervisorRequest("GET", "/api/story", new Dictionary<string, string>(), Token))).Status);
        await using var none = new SupervisorServer(new SupervisorOptions { Token = Token }, new FakeSupervisorData());
        Assert.Equal(404, (await none.HandleAsync(new SupervisorRequest("GET", "/api/story", new Dictionary<string, string>(), Token))).Status);
    }

    [Fact]
    public async Task Home_moves_a_colony_home_with_the_exact_wire_shape()
    {
        var (r, _, _, _, _) = Make();
        var id = await Colony(r);
        var (s, j) = await Call(r, "POST", $"/api/story/colonies/{id}/home", new { home = new { x = 69, y = 64, z = 215 } });
        Assert.Equal(200, s);
        Assert.Equal("""{"home":[69,64,215]}""", j!.ToJsonString());
        var (_, st) = await Call(r, "GET", $"/api/story/colonies/{id}");
        Assert.Equal(new[] { 69, 64, 215 }, st!["home"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray());
        Assert.Equal(new P3(69, 64, 215), r.Colonies.Get(id)!.Meta.Home);
        // The pawns were far from the new home: no teleport on this host, so each walks there (a move order before any work), each to
        // its own spot by home (never one cell for all).
        var moves = r.Colonies.Get(id)!.Pawns.Values.Select(p => Assert.Single(p.Queue, o => o.Kind == "move").At!.Value).ToList();
        Assert.Equal(new P3(69, 64, 215), moves[0]);
        Assert.All(moves, m => Assert.True(Math.Abs(m.X - 69) <= 4 && Math.Abs(m.Z - 215) <= 4, m.ToString()));
        Assert.Equal(moves.Count, moves.Distinct().Count());

        Assert.Equal(400, (await Call(r, "POST", $"/api/story/colonies/{id}/home", new { })).Status);
        Assert.Equal(400, (await Call(r, "POST", $"/api/story/colonies/{id}/home", new { home = new { x = 1, y = 64 } })).Status);
        Assert.Equal(400, (await Call(r, "POST", $"/api/story/colonies/{id}/home", new { home = new { x = "a", y = 64, z = 1 } })).Status);
        Assert.Equal(400, (await Call(r, "POST", $"/api/story/colonies/{id}/home", new { home = new[] { 1, 64, 1 } })).Status);
        Assert.Equal(400, (await Call(r, "POST", $"/api/story/colonies/{id}/home", new { home = new { x = 1, y = 9999, z = 1 } })).Status);
        Assert.Equal(404, (await Call(r, "POST", "/api/story/colonies/story-9/home", new { home = new { x = 1, y = 64, z = 1 } })).Status);
        Assert.Equal(405, (await Call(r, "GET", $"/api/story/colonies/{id}/home")).Status);
    }

    [Fact]
    public void The_gate_arms_one_story_teleport_for_a_managed_account_only_and_the_blocklist_still_applies()
    {
        var gate = new MineClaw.Agent.Chat.CommandGate(new MineClaw.Agent.Chat.CommandOptions(), () => ["poofimafox"])
        {
            ManagedAccount = n => n.StartsWith("maid_chan_", StringComparison.Ordinal),
        };
        Assert.False(gate.AllowOutgoing("/tp maid_chan_101 69 64 215")); // not armed
        Assert.True(gate.ArmManagedTeleport("/tp maid_chan_101 69 64 215", "story story-1: home moved"));
        Assert.True(gate.AllowOutgoing("/tp maid_chan_101 69 64 215"));
        Assert.False(gate.AllowOutgoing("/tp maid_chan_101 69 64 215")); // one arm, one send
        Assert.False(gate.ArmManagedTeleport("/tp poofimafox 69 64 215", "x"));          // a player: never
        Assert.False(gate.ArmManagedTeleport("/tp maid_chan_101 poofimafox", "x"));      // only coordinates
        Assert.False(gate.ArmManagedTeleport("/tp maid_chan_101 1 2 3 4", "x"));
        Assert.False(gate.ArmManagedTeleport("/tp maid_chan_101 ~ 64 ~", "x"));
        Assert.False(gate.ArmManagedTeleport("/op maid_chan_101", "x"));
        Assert.False(gate.AllowOutgoing("/tp maid_chan_102 1 64 1"));
        var strict = new MineClaw.Agent.Chat.CommandGate(new MineClaw.Agent.Chat.CommandOptions { Blocked = ["/tp"] }, () => ["poofimafox"])
        {
            ManagedAccount = _ => true,
        };
        Assert.False(strict.ArmManagedTeleport("/tp maid_chan_101 1 64 1", "x")); // the config blocks /tp: never
        var none = new MineClaw.Agent.Chat.CommandGate(new MineClaw.Agent.Chat.CommandOptions(), () => ["poofimafox"]);
        Assert.False(none.ArmManagedTeleport("/tp maid_chan_101 1 64 1", "x")); // no hive: no exception
    }

    private sealed class Ticker : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Time_is_set_from_an_op_account_main_first_one_change_a_second()
    {
        var accounts = new List<OpAccount> { new("maid_chan", true, null), new("maid_chan_101", false, true) };
        var sent = new List<(string Account, string Line)>();
        var clock = new Ticker();
        var tc = new StoryTimeControl(() => accounts, (a, line, _) => { sent.Add((a, line)); return Task.FromResult<string?>(null); }, clock);
        var o = new SupervisorOptions { Token = Token, Viewer = { Enabled = true } };
        var r = new SupervisorStoryRoutes(o, new StoryColonies(new Host(new World())), timeControl: tc);

        // main's op level unknown, a body is op: that body.
        var (_, list) = await Call(r, "GET", "/api/story");
        Assert.Equal("""{"available":true,"by":"maid_chan_101"}""", list!["time_control"]!.ToJsonString());
        var (s, j) = await Call(r, "POST", "/api/story/time", new { time = 18000 });
        Assert.Equal(200, s);
        Assert.Equal("""{"time":18000,"by":"maid_chan_101"}""", j!.ToJsonString());
        Assert.Equal(("maid_chan_101", "/time set 18000"), Assert.Single(sent));

        // More than one a second: 429; a second later it goes, and main (op now) is preferred.
        accounts[0] = new("maid_chan", true, true);
        Assert.Equal(429, (await Call(r, "POST", "/api/story/time", new { time = 1000 })).Status);
        clock.Now += StoryTimeControl.MinGap;
        (s, j) = await Call(r, "POST", "/api/story/time", new { time = 0 });
        Assert.Equal(200, s);
        Assert.Equal("maid_chan", j!["by"]!.GetValue<string>());
        Assert.Equal(2, sent.Count);

        // Bad bodies.
        clock.Now += StoryTimeControl.MinGap;
        foreach (var bad in new object[] { new { }, new { time = -1 }, new { time = 24000 }, new { time = "noon" }, new { time = 1.5 } })
            Assert.Equal(400, (await Call(r, "POST", "/api/story/time", bad)).Status);
        Assert.Equal(405, (await Call(r, "GET", "/api/story/time")).Status);
        Assert.Equal(2, sent.Count);

        // Nobody op: 409, and the state says so.
        accounts[0] = new("maid_chan", true, false);
        accounts[1] = new("maid_chan_101", false, null);
        (s, j) = await Call(r, "POST", "/api/story/time", new { time = 6000 });
        Assert.Equal(409, s);
        Assert.Equal("no op account", j!["error"]!.GetValue<string>());
        var (_, again) = await Call(r, "GET", "/api/story");
        Assert.Equal("""{"available":false,"by":null}""", again!["time_control"]!.ToJsonString());
        // main known not op: the colony state carries an alert (once) and the same time_control.
        var (_, made) = await Call(r, "POST", "/api/story/colonies", new { name = "A", pawns = 1 });
        var id = made!["id"]!.GetValue<string>();
        var (_, st) = await Call(r, "GET", $"/api/story/colonies/{id}");
        Assert.Equal("""{"available":false,"by":null}""", st!["time_control"]!.ToJsonString());
        Assert.Contains(st["alerts"]!.AsArray(), a => a!["text"]!.GetValue<string>().Contains("maid_chan (main) isn't op"));
        await Call(r, "GET", $"/api/story/colonies/{id}");
        Assert.Single(r.Colonies.Get(id)!.Alerts, a => a.Text.Contains("isn't op"));

        // No time control wired at all: 409 too.
        var (plain, _, _, _, _) = Make();
        Assert.Equal(409, (await Call(plain, "POST", "/api/story/time", new { time = 6000 })).Status);
        Assert.Equal("""{"available":false,"by":null}""", (await Call(plain, "GET", "/api/story")).Json!["time_control"]!.ToJsonString());
    }

    [Fact]
    public void The_gate_arms_one_story_time_set_and_nothing_else()
    {
        var gate = new MineClaw.Agent.Chat.CommandGate(new MineClaw.Agent.Chat.CommandOptions(), () => ["poofimafox"]);
        Assert.False(gate.AllowOutgoing("/time set 6000"));
        Assert.True(gate.ArmStoryTime("/time set 6000", "story time slider (poofimafox)"));
        Assert.True(gate.AllowOutgoing("/time set 6000"));
        Assert.False(gate.AllowOutgoing("/time set 6000"));
        Assert.False(gate.ArmStoryTime("/time add 6000", "x"));
        Assert.False(gate.ArmStoryTime("/time set day", "x"));
        Assert.False(gate.ArmStoryTime("/time set 6000; /op x", "x"));
        var strict = new MineClaw.Agent.Chat.CommandGate(new MineClaw.Agent.Chat.CommandOptions { Blocked = ["/time"] }, () => ["poofimafox"]);
        Assert.False(strict.ArmStoryTime("/time set 6000", "x"));
    }

    [Theory]
    [InlineData(24, 0)] [InlineData(25, 1)] [InlineData(26, 2)] [InlineData(27, 3)] [InlineData(28, 4)]
    public void An_op_level_comes_from_entity_status_24_to_28(int status, int level)
    {
        Assert.Equal(level, MineClaw.Client.MinecraftClient.OpLevelOf(status));
        Assert.False(MineClaw.Protocol.Release1122.EntityIds1122.IsHurtStatus(status)); // the 1.12.2 decode passes it through unchanged
    }

    [Theory]
    [InlineData(2)] [InlineData(9)] [InlineData(23)] [InlineData(29)] [InlineData(35)]
    public void Other_statuses_carry_no_op_level(int status) => Assert.Null(MineClaw.Client.MinecraftClient.OpLevelOf(status));

    [Fact]
    public async Task Beds_are_assigned_and_unassigned_with_the_exact_wire_shape_and_zones_rename()
    {
        var (r, _, w, _, _) = Make();
        var id = await Colony(r);
        w.Blocks[new P3(3, 64, 3)] = (26, 0);
        w.Blocks[new P3(3, 64, 4)] = (26, 8);
        var b = $"/api/story/colonies/{id}/";
        var pawn = r.Colonies.Get(id)!.Pawns.Keys.First();
        var (s, j) = await Call(r, "POST", b + "beds", new { op = "assign", bed = new { x = 3, y = 64, z = 4 }, pawn });
        Assert.Equal(200, s);
        Assert.Equal($$"""{"beds":[{"pos":[3,64,3],"dim":0,"pawn":"{{pawn}}","zone":null}]}""", j!.ToJsonString());
        var (_, st) = await Call(r, "GET", b.TrimEnd('/'));
        Assert.Equal(pawn, st!["beds"]![0]!["pawn"]!.GetValue<string>());
        (s, j) = await Call(r, "POST", b + "beds", new { op = "unassign", bed = new { x = 3, y = 64, z = 3 } });
        Assert.Equal(200, s);
        Assert.Null(j!["beds"]![0]!["pawn"]);
        Assert.Equal(400, (await Call(r, "POST", b + "beds", new { op = "sleep", bed = new { x = 3, y = 64, z = 3 }, pawn })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "beds", new { op = "assign", pawn })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "beds", new { op = "assign", bed = new { x = 9, y = 64, z = 9 }, pawn })).Status); // not a bed
        Assert.Equal(400, (await Call(r, "POST", b + "beds", new { op = "assign", bed = new { x = 3, y = 64, z = 3 } })).Status);
        Assert.Equal(404, (await Call(r, "POST", b + "beds", new { op = "assign", bed = new { x = 3, y = 64, z = 3 }, pawn = "nobody" })).Status);
        Assert.Equal(404, (await Call(r, "POST", "/api/story/colonies/story-9/beds", new { op = "unassign", bed = new { x = 3, y = 64, z = 3 } })).Status);

        // Houses: home zones with names, renamed and removed by id.
        (s, j) = await Call(r, "POST", b + "zones", new { op = "add", kind = "home", area = new[] { 100, 60, 100, 110, 70, 110 }, name = "Farmhouse" });
        Assert.Equal(200, s);
        var zid = j!["id"]!.GetValue<string>();
        (s, j) = await Call(r, "POST", b + "zones", new { op = "rename", id = zid, name = "Old farmhouse" });
        Assert.Equal(200, s);
        Assert.Equal("Old farmhouse", j!["name"]!.GetValue<string>());
        (_, st) = await Call(r, "GET", b.TrimEnd('/'));
        Assert.Equal("Old farmhouse", st!["zones"]!.AsArray().Single()!["name"]!.GetValue<string>());
        Assert.Equal(400, (await Call(r, "POST", b + "zones", new { op = "rename", id = "z999", name = "x" })).Status);
        (s, j) = await Call(r, "POST", b + "zones", new { op = "remove", id = zid });
        Assert.Equal(200, s);
        Assert.Equal(zid, j!["removed"]![0]!.GetValue<string>());
    }
}
