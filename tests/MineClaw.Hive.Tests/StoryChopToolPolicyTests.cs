using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryChopToolPolicyTests
{
    private static readonly P3 Tree = new(3, 64, 3), Chest = new(0, 64, 8);
    private static JsonObject Rule(string tier = "stone") => new()
    {
        ["op"] = "set", ["rule"] = new JsonObject { ["material"] = "group:logs", ["tool"] = "axe", ["tier"] = tier, ["fallback"] = "plain" },
    };
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Make(string route, bool rule = true)
    {
        var (c, h, w) = StoryRig.Make();
        c.Meta.Gear.AutoCraftTools = false;
        c.Meta.Gear.Policies.Add(new GearPolicy { Id = "default", ToolsByWork = false, Weapon = "none", Armor = "none" });
        foreach (var type in WorkTypes.All) c.Pawns.Values.Single().Priorities[type] = type == (route == "farm" ? WorkTypes.Farm : WorkTypes.Chop) ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0);
        w.Tree(Tree.X, Tree.Z);
        if (rule) Assert.Null(c.MaterialRulesOp(Rule()).Error);
        switch (route)
        {
            case "mark": Assert.Null(c.Designate("add", "chop", null, [Tree], null).Error); break;
            case "zone": Assert.Null(c.ZoneOp("add", "chop", new Area(2, 63, 2, 4, 70, 4)).Error); break;
            case "farm":
                w.Set(3, 63, 3, 2);
                Assert.Null(c.ZoneOp("add", "farm", new Area(3, 63, 3, 3, 70, 3), grow: new JsonObject { ["plant"] = "oak" }).Error);
                break;
            case "order":
                Assert.Null(c.GiveOrder([c.Pawns.Values.Single()], new JsonObject { ["kind"] = "chop", ["target"] = new JsonObject { ["x"] = Tree.X, ["y"] = Tree.Y, ["z"] = Tree.Z } }, false).Error);
                break;
        }
        return (c, h, w);
    }

    [Theory]
    [InlineData("mark")]
    [InlineData("zone")]
    [InlineData("order")]
    [InlineData("farm")]
    public void Every_tree_route_waits_for_the_required_axe_and_fetches_one_when_it_is_stored(string route)
    {
        var (c, h, _) = Make(route);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(271, 0, 1)); // wood does not meet stone
        var wait = c.Next();
        Assert.NotEqual("chop", wait["kind"]!.GetValue<string>());
        Assert.Null(c.StorageOp("set", [Chest], null, null, null).Error);
        c.AddContents(c.StorageAt(Chest)!, new Stack(275, 0, 1));
        c.Report(c.P(), wait["id"]!.GetValue<string>(), "done", null, null);
        var job = c.Next();
        Assert.Equal("chop", job["kind"]!.GetValue<string>());
        Assert.Equal("stone_axe", job["tool"]!.GetValue<string>());
        Assert.Contains(job["fetch"]!.AsArray(), f => f!["item"]!.GetValue<string>() == "stone_axe");
    }

    [Theory]
    [InlineData("mark")]
    [InlineData("zone")]
    [InlineData("order")]
    [InlineData("farm")]
    public void Editing_a_rule_interrupts_existing_hand_chopping_and_replans_with_the_carried_tool(string route)
    {
        var (c, h, _) = Make(route, rule: false);
        var old = c.Next();
        Assert.Equal("chop", old["kind"]!.GetValue<string>());
        Assert.Null(old["tool"]);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(275, 0, 1));
        Assert.Null(c.MaterialRulesOp(Rule()).Error);
        Assert.False(c.Holds(c.P(), old["id"]!.GetValue<string>()));
        Assert.Contains(h.Interrupted, text => text.Contains("material tool rule changed"));
        var next = c.Next();
        Assert.Equal("chop", next["kind"]!.GetValue<string>());
        Assert.Equal("stone_axe", next["tool"]!.GetValue<string>());
    }

    [Fact]
    public void A_log_rule_does_not_interrupt_unrelated_mining()
    {
        var (c, h, w) = StoryRig.Make();
        foreach (var type in WorkTypes.All) c.Pawns.Values.Single().Priorities[type] = type == WorkTypes.Mine ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 0, 1));
        w.Set(3, 64, 3, 1);
        c.Designate("add", "mine", null, [Tree], null);
        var mine = c.Next();
        Assert.Equal("dig", mine["kind"]!.GetValue<string>());
        c.MaterialRulesOp(Rule());
        Assert.True(c.Holds(c.P(), mine["id"]!.GetValue<string>()));
    }
}
