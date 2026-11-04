using System.Text.Json.Nodes;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>Work roles: named priority templates a pawn's row follows (docs/web/story-mode.md "Roles").</summary>
public sealed class StoryRolesTests
{
    private static JsonObject Op(string op, JsonObject? role = null, string? id = null)
    {
        var o = new JsonObject { ["op"] = op };
        if (role != null) o["role"] = role;
        if (id != null) o["id"] = id;
        return o;
    }

    private static JsonObject Prios(params (string T, int P)[] ps) => new(ps.Select(x => new KeyValuePair<string, JsonNode?>(x.T, x.P)));

    [Fact]
    public void The_built_in_roles_are_seeded_and_cover_every_work_type()
    {
        var (c, _, _) = StoryRig.Make();
        var roles = c.State()["roles"]!.AsArray();
        var ids = roles.Select(r => r!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(["farmer", "miner", "woodcutter", "builder", "hauler", "crafter", "cook", "fisher", "guard", "generalist"], ids);
        Assert.All(roles, r =>
        {
            Assert.True(r!["builtin"]!.GetValue<bool>());
            Assert.Equal(WorkTypes.All, r["priorities"]!.AsObject().Select(kv => kv.Key));
        });
        var farmer = roles.First(r => r!["id"]!.GetValue<string>() == "farmer")!["priorities"]!;
        Assert.Equal(1, farmer["farm"]!.GetValue<int>());
        Assert.Equal(0, farmer["mine"]!.GetValue<int>());
        var gen = roles.First(r => r!["id"]!.GetValue<string>() == "generalist")!["priorities"]!;
        Assert.Equal(2, gen["haul"]!.GetValue<int>());
        Assert.Equal(3, gen["fish"]!.GetValue<int>());
    }

    [Fact]
    public void A_role_drives_the_pawns_effective_priorities_and_the_job_picker()
    {
        var (c, _, w) = StoryRig.Make();
        w.Tree(10, 0);
        w.Set(2, 64, 0, 4);
        c.Designate("add", "chop", null, [new P3(10, 66, 0)], null);
        c.Designate("add", "mine", null, [new P3(2, 64, 0)], null);
        var (ps, _) = c.Resolve([c.P()]);
        Assert.Null(c.SetRole(ps, "woodcutter"));
        var p = ps[0];
        Assert.Equal("woodcutter", p.Role);
        Assert.Equal(1, c.EffectivePriorities(p)["chop"]);
        Assert.Equal(0, c.EffectivePriorities(p)["mine"]);
        Assert.Equal(["chop", "haul"], c.WorkOrder(p));
        Assert.Equal("chop", c.Next()["kind"]!.GetValue<string>());
        Assert.Equal("woodcutter", c.State()["pawns"]![0]!["role"]!.GetValue<string>());

        Assert.Null(c.SetRole(ps, "miner"));
        Assert.Equal(["mine", "haul", "build"], c.WorkOrder(p));
        Assert.Contains("no such role", c.SetRole(ps, "astronaut"));
        // Clearing the role keeps the values (Custom).
        Assert.Null(c.SetRole(ps, null));
        Assert.Null(p.Role);
        Assert.Equal(1, p.Priorities["mine"]);
    }

    [Fact]
    public void Editing_a_role_updates_every_pawn_with_it_and_a_cell_edit_detaches_one()
    {
        var (c, _, _) = StoryRig.Make(3);
        var (two, _) = c.Resolve([c.P(0), c.P(1)]);
        c.SetRole(two, "farmer");
        var other = c.Pawns[c.P(2)];
        int otherFarm = other.Priorities["farm"];

        var r = c.RolesOp(Op("update", new JsonObject { ["id"] = "farmer", ["priorities"] = Prios(("farm", 2), ("chop", 1)) }));
        Assert.Null(r.Error);
        Assert.All(two, p => { Assert.Equal(2, p.Priorities["farm"]); Assert.Equal(1, p.Priorities["chop"]); Assert.Equal(3, p.Priorities["haul"]); });
        Assert.Equal(otherFarm, other.Priorities["farm"]); // no role: untouched
        var farmer = r.Result!["roles"]!.AsArray().First(x => x!["id"]!.GetValue<string>() == "farmer")!;
        Assert.Equal([c.P(0), c.P(1)], farmer["pawns"]!.AsArray().Select(x => x!.GetValue<string>()));

        // A cell edit on a role row: that pawn turns Custom, its other values kept; the other stays a Farmer.
        Assert.Null(c.SetPriorities([two[0]], new Dictionary<string, int> { ["mine"] = 4 }));
        Assert.Null(two[0].Role);
        Assert.Equal(2, two[0].Priorities["farm"]);
        Assert.Equal(4, two[0].Priorities["mine"]);
        Assert.Equal("farmer", two[1].Role);
        c.RolesOp(Op("update", new JsonObject { ["id"] = "farmer", ["priorities"] = Prios(("farm", 1)) }));
        Assert.Equal(2, two[0].Priorities["farm"]);
        Assert.Equal(1, two[1].Priorities["farm"]);
    }

    [Fact]
    public void Add_rename_remove_and_reset_defaults()
    {
        var (c, _, _) = StoryRig.Make(2);
        var add = c.RolesOp(Op("add", new JsonObject { ["name"] = "Night miner", ["icon"] = "pickaxe", ["priorities"] = Prios(("mine", 1), ("guard", 2)) }));
        Assert.Null(add.Error);
        var id = add.Result!["id"]!.GetValue<string>();
        Assert.Equal("night-miner", id);
        Assert.Contains("taken", c.RolesOp(Op("add", new JsonObject { ["name"] = "farmer" })).Error);
        Assert.Contains("unknown work type", c.RolesOp(Op("add", new JsonObject { ["name"] = "X", ["priorities"] = Prios(("dance", 1)) })).Error);
        Assert.Contains("0 (off) .. 4", c.RolesOp(Op("add", new JsonObject { ["name"] = "Y", ["priorities"] = Prios(("mine", 5)) })).Error);
        Assert.Contains("op must be", c.RolesOp(Op("explode")).Error);
        Assert.Null(c.RolesOp(Op("update", new JsonObject { ["id"] = id, ["name"] = "Deep miner" })).Error);

        var (ps, _) = c.Resolve([c.P(0)]);
        c.SetRole(ps, id);
        Assert.Equal(2, ps[0].Priorities["guard"]);
        // Deleting a role: its pawns turn Custom with the same values.
        var gone = c.RolesOp(Op("remove", id: id));
        Assert.Null(gone.Error);
        Assert.Null(ps[0].Role);
        Assert.Equal(1, ps[0].Priorities["mine"]);
        Assert.Equal(2, ps[0].Priorities["guard"]);

        // Reset: built-ins back as shipped (an edited one reverts and its pawns follow; a removed one returns), own roles kept.
        c.RolesOp(Op("add", new JsonObject { ["name"] = "Keeper" }));
        c.SetRole(ps, "farmer");
        c.RolesOp(Op("update", new JsonObject { ["id"] = "farmer", ["name"] = "Grower", ["priorities"] = Prios(("farm", 4)) }));
        c.RolesOp(Op("remove", id: "fisher"));
        Assert.Equal(4, ps[0].Priorities["farm"]);
        var reset = c.RolesOp(Op("reset_defaults"));
        Assert.Null(reset.Error);
        var names = reset.Result!["roles"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Farmer", names);
        Assert.Contains("Fisher", names);
        Assert.Contains("Keeper", names);
        Assert.Equal(1, ps[0].Priorities["farm"]);
        Assert.Equal("farmer", ps[0].Role);
    }

    [Fact]
    public void Roles_and_assignments_survive_a_restart()
    {
        using var db = AgentDb.Open(null);
        var store = new StoryStore(db);
        var w = new FakeStoryWorld();
        var colonies = new StoryColonies(new FakeStoryHost(w), store);
        var (c, err) = colonies.Create("Roles", 2, new P3(1, 64, 1));
        Assert.Null(err);
        c!.RolesOp(Op("add", new JsonObject { ["name"] = "Tender", ["priorities"] = Prios(("farm", 2), ("haul", 1)) }));
        c.RolesOp(Op("update", new JsonObject { ["id"] = "miner", ["priorities"] = Prios(("mine", 2)) }));
        c.RolesOp(Op("remove", id: "cook"));
        var (ps, _) = c.Resolve([c.P(0)]);
        c.SetRole(ps, "tender");
        c.Save(force: true);

        var again = new StoryColonies(new FakeStoryHost(w), store);
        Assert.Equal(1, again.Load());
        var r = again.Get(c.Id)!;
        var roles = r.State()["roles"]!.AsArray();
        Assert.Contains(roles, x => x!["id"]!.GetValue<string>() == "tender" && !x["builtin"]!.GetValue<bool>());
        Assert.DoesNotContain(roles, x => x!["id"]!.GetValue<string>() == "cook");
        Assert.Equal(2, roles.First(x => x!["id"]!.GetValue<string>() == "miner")!["priorities"]!["mine"]!.GetValue<int>());
        var p = r.Pawns[c.P(0)];
        Assert.Equal("tender", p.Role);
        Assert.Equal(1, p.Priorities["haul"]);
        Assert.Null(r.Pawns[c.P(1)].Role);
    }
}
