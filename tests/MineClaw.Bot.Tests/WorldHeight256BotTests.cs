using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Bot.Tests.WorldIndex;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>Bot consumers on a 1.12.2 (256-high) world: the index, blueprints and the planner's world see above y 127
/// (multi-version P3). On a 128-high world the same calls keep the b1.8.1 bounds.</summary>
public class WorldHeight256BotTests
{
    private static string K(int x, int y, int z) => ObservationStore.KeyFor(x, y, z);

    [Fact]
    public void The_index_finds_stations_above_y_127()
    {
        using var f = new IndexFixture(height: 256);
        f.Island(top: 200);
        f.Set(1, 201, 1, Blocks.Workbench);
        f.Set(-1, 201, 1, Blocks.Furnace);
        f.Pump();
        Assert.NotNull(f.Row("crafting_table", K(1, 201, 1)));
        Assert.NotNull(f.Row("furnace", K(-1, 201, 1)));
    }

    [Fact]
    public void A_blueprint_above_y_127_fits_a_256_high_world_only()
    {
        var at = new BlockPos(-2, 200, -2);
        var (plan, issues) = BlueprintNormalizer.FromText(BlueprintTests.Hut, at, worldHeight: 256);
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Code == "bounds");
        var (_, old) = BlueprintNormalizer.FromText(BlueprintTests.Hut, at);
        Assert.Contains(old, i => i.Code == "bounds" && i.Text.Contains("1..127", StringComparison.Ordinal));
    }

    [Fact]
    public void The_planner_world_carries_the_real_height()
    {
        var w = new WorldState(height: 256);
        Assert.Equal(256, new PlanWorld(w.PlanningView).WorldHeight);
        Assert.Equal(128, new PlanWorld(new WorldState().PlanningView).WorldHeight);
    }
}
