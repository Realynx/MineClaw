using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28 16:21: respawned at night with no food, main walked west (task #3) to the tower floor near the mob chamber, hit low
/// hp with "no food!" and died again. A new job that leaves home ground at night (or with hostiles near) needs the reflex's OK
/// while there is no food on hand. And Fox's rule #6 ("the drop tower only in daytime, at night mine cobble"): a running build
/// stops itself at dusk through the generic directive check and the body comes home.
/// </summary>
public class NightSafetyTests
{
    private static (GameBridge Bridge, FakeGameClient C) Bridge()
    {
        var c = new FakeGameClient();
        c.World.Spawn = new BlockPos(0, 64, 0);
        var rt = AgentRuntime.Create(TestBot.Options(), (_, _) => Task.CompletedTask, new NoLlm(), persist: false);
        return (new GameBridge(c, rt), c);
    }

    private static (string, JsonElement) Step(string tool, object input) => (tool, JsonSerializer.SerializeToElement(input));

    [Fact]
    public void At_night_with_no_food_a_job_away_from_home_is_refused_with_one_line()
    {
        var (b, c) = Bridge();
        c.World.Time = 18000; // midnight
        var no = b.BodySafety([Step("goto", new { to = "-30,64,0" })]);
        Assert.NotNull(no);
        Assert.StartsWith("night + no food: ", no);
        Assert.Contains("not starting goto now", no);
        Assert.NotNull(b.BodySafety([Step("mine", new { block = "cobblestone" })]));
        // Getting food, the chests, the gen and walks home stay allowed.
        Assert.Null(b.BodySafety([Step("eat", new { })]));
        Assert.Null(b.BodySafety([Step("withdraw", new { item = "bread" })]));
        Assert.Null(b.BodySafety([Step("harvest_generator", new { })]));
        Assert.Null(b.BodySafety([Step("goto", new { to = "3,64,-4" })]));
        Assert.Null(b.BodySafety([Step("goto", new { to = "home" })]));
    }

    [Fact]
    public void By_day_or_with_food_the_reflex_says_go()
    {
        var (b, c) = Bridge();
        c.World.Time = 6000;
        Assert.Null(b.BodySafety([Step("goto", new { to = "-30,64,0" })]));
        c.World.Time = 18000;
        c.World.Inventory.SetSlot(0, 36, new ItemStack((short)Items.Bread, 3, 0));
        Assert.Null(b.BodySafety([Step("goto", new { to = "-30,64,0" })]));
    }

    [Fact]
    public async Task A_rule_hold_stops_the_build_between_steps_and_walks_the_body_back_to_where_it_started()
    {
        var w = BlueprintTests.Island();
        var (plan, _) = BlueprintNormalizer.FromText("size 6x1x1\npalette C=cobblestone\nlayer 0\nCCCCCC", new BlockPos(-3, 64, -3));
        using var db = AgentDb.Open(null);
        int placed = 0;
        var ex = new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db))
        {
            Hold = _ => placed >= 2 ? "rule#6 (Fox): the drop tower only in daytime, at night mine cobble; it's getting dark: stopped until day" : null,
        };
        var body = new SimBuildBody(w) { AfterPlace = n => placed = n };
        var start = body.Feet0;
        var report = await ex.RunAsync(4, plan!, BuildPart.Whole(plan!), body, CancellationToken.None);
        var text = report.Text("tower");
        Assert.Equal(2, report.Placed);
        Assert.StartsWith("rule#6 (Fox): the drop tower only in daytime", report.Stopped);
        Assert.Contains("stopped: rule#6", text);
        Assert.Equal("paused", report.Plan!.State);
        // Home before dark: back where the build started (its recorded ground).
        Assert.Equal(start, report.Plan.Home);
        Assert.Equal(start, body.Feet0);
        // By day it carries on from there.
        ex.Hold = null;
        var rest = await ex.RunAsync(4, plan!, BuildPart.Whole(plan!), body, CancellationToken.None);
        Assert.True(rest.Complete, rest.Text("tower"));
        Assert.Equal(4, rest.Placed);
    }
}
