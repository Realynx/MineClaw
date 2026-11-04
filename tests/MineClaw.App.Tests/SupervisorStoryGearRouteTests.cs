using System.Text.Json.Nodes;
using MineClaw.App.Supervisor;
using MineClaw.Hive.Story;

namespace MineClaw.App.Tests;

/// <summary>Story Mode's gear and combat routes (docs/web/story-mode.md "Tools and gear", "Drafted combat"): policies, material rules,
/// bulk gear ops, the gear options and the attack order's confirmation.</summary>
public sealed class SupervisorStoryGearRouteTests
{
    private const string Token = "s3cret";

    private static (SupervisorStoryRoutes R, SupervisorStoryRouteTests.World W) Make()
    {
        var o = new SupervisorOptions { Token = Token, Viewer = { Enabled = true } };
        var w = new SupervisorStoryRouteTests.World();
        return (new SupervisorStoryRoutes(o, new StoryColonies(new SupervisorStoryRouteTests.Host(w))), w);
    }

    private static async Task<(int Status, JsonNode? Json)> Call(SupervisorStoryRoutes r, string method, string path, object? body = null)
    {
        var json = body == null ? null : System.Text.Json.JsonSerializer.Serialize(body);
        var reply = await r.TryHandleAsync(new SupervisorRequest(method, path, new Dictionary<string, string>(), Token, json));
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
    public async Task An_attack_on_a_player_answers_409_until_the_owner_confirms()
    {
        var (r, w) = Make();
        var id = await Colony(r);
        var b = $"/api/story/colonies/{id}/";
        var pawn = r.Colonies.Get(id)!.Pawns.Keys.First();
        w.Ents.Add(new StoryEntity(60, "player", "PoofimaFox", 4.5, 64, 4.5));
        w.Ents.Add(new StoryEntity(61, "mob", "zombie", 5.5, 64, 5.5, Hostile: true));
        Assert.Equal(200, (await Call(r, "POST", b + "draft", new { pawns = new[] { pawn }, drafted = true })).Status);
        var (s, j) = await Call(r, "POST", b + "orders", new { pawns = new[] { pawn }, order = new { kind = "attack", target = new { entity = 60 } } });
        Assert.Equal(409, s);
        Assert.Equal("player", j!["confirm"]!["kind"]!.GetValue<string>());
        Assert.Equal("PoofimaFox", j["confirm"]!["name"]!.GetValue<string>());
        Assert.Equal(60, j["confirm"]!["entity"]!.GetValue<int>());
        Assert.Equal(200, (await Call(r, "POST", b + "orders", new { pawns = new[] { pawn }, order = new { kind = "attack", target = new { entity = 60 }, confirm = true } })).Status);
        Assert.Equal(200, (await Call(r, "POST", b + "orders", new { pawns = new[] { pawn }, order = new { kind = "attack", target = new { entity = 61 } } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "orders", new { pawns = new[] { pawn }, order = new { kind = "attack", target = new { entity = 999 } } })).Status);
    }

    [Fact]
    public async Task Policies_material_rules_options_and_gear_ops_validate_and_answer()
    {
        var (r, _) = Make();
        var id = await Colony(r);
        var b = $"/api/story/colonies/{id}/";
        var pawns = r.Colonies.Get(id)!.Pawns.Keys.ToArray();
        // Material rules: never below the floor.
        var (rs, rj) = await Call(r, "POST", b + "material-rules", new { op = "set", rule = new { material = "iron_ore", tier = "wood" } });
        Assert.Equal(400, rs);
        Assert.Contains("needs at least a stone pickaxe", rj!["error"]!.GetValue<string>());
        (rs, rj) = await Call(r, "POST", b + "material-rules", new { op = "set", rule = new { material = "group:ores", tool = "pickaxe", tier = "iron", fallback = "plain" } });
        Assert.Equal(200, rs);
        Assert.Equal("iron", rj!["rule"]!["tier"]!.GetValue<string>());
        Assert.Equal(400, (await Call(r, "POST", b + "material-rules", new { op = "set", rule = new { material = "iron_ore", enchant = "fortune" } })).Status); // b1.8.1
        // Policies.
        var (ps, pj) = await Call(r, "POST", b + "policies", new { op = "set", policy = new { name = "Miners", tools = new[] { "pickaxe", "shovel" }, weapon = "none", armor = "none", food = 2 } });
        Assert.Equal(200, ps);
        var pid = pj!["id"]!.GetValue<string>();
        Assert.Equal(200, (await Call(r, "POST", b + "policies", new { op = "assign", id = pid, pawns = new[] { pawns[0] } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "policies", new { op = "assign", id = "pol-nope", pawns = new[] { pawns[0] } })).Status);
        // Options.
        var (os, oj) = await Call(r, "POST", b + "options", new { auto_craft_tools = false, spare_tools = new { count = 2, kinds = new[] { "pickaxe", "axe" } } });
        Assert.Equal(200, os);
        Assert.False(oj!["auto_craft_tools"]!.GetValue<bool>());
        Assert.Equal(2, oj["spare_tools"]!["count"]!.GetValue<int>());
        Assert.Equal(400, (await Call(r, "POST", b + "options", new { spare_tools = new { count = 99 } })).Status);
        // Gear ops: one op for the selection, one summary.
        var (gs, gj) = await Call(r, "POST", b + "gear", new { op = "arm", pawns });
        Assert.Equal(200, gs);
        Assert.Equal(2, gj!["results"]!.AsArray().Count);
        Assert.StartsWith("Arm: ", gj["summary"]!.GetValue<string>());
        Assert.Equal(400, (await Call(r, "POST", b + "gear", new { op = "juggle", pawns })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "gear", new { op = "pickup", pawns })).Status); // no item
        Assert.Equal(404, (await Call(r, "POST", b + "gear", new { op = "arm", pawns = new[] { "nobody" } })).Status);
        // The state carries it all.
        var (_, st) = await Call(r, "GET", $"/api/story/colonies/{id}");
        Assert.Equal(2, st!["policies"]!.AsArray().Count);
        Assert.Single(st["material_rules"]!.AsArray());
        Assert.NotEmpty(st["materials"]!.AsArray());
        var pawn0 = st["pawns"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == pawns[0])!;
        Assert.Equal(pid, pawn0["policy"]!.GetValue<string>());
        Assert.NotNull(pawn0["equipment"]!["tools"]);
        Assert.Equal("axe", pawn0["equipment"]!["held"]!["kind"]!.GetValue<string>());
        Assert.Equal("stone", pawn0["equipment"]!["held"]!["tier"]!.GetValue<string>());
    }
}
