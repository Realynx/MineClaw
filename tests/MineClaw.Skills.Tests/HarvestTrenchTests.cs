using System.Diagnostics;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using Xunit;

namespace MineClaw.Skills.Tests;

public class HarvestTrenchTests
{
    [Theory]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("stall")]
    public async Task Water_drops_never_abort_or_hold_up_the_next_crop(string pickup)
    {
        var g = LiquidTests.Island(14);
        g.Stand(6.5, 64, 8.5);
        g.Set(new BlockPos(8, 63, 8), Blocks.Water);
        foreach (int z in new[] { 7, 9 })
        {
            g.Set(new BlockPos(7, 63, z), Blocks.Farmland);
            g.Set(new BlockPos(7, 64, z), Blocks.Crops, 7);
        }
        g.Give(Items.StoneHoe, 1);
        int id = 700;
        g.AfterDig = p => g.World.Entities.SpawnItem(++id, new ItemStack(Items.Wheat, 1, 0), new Vec3d(8.5, 63.1, 8.5));
        g.CollectDropsHook = async (_, ct) =>
        {
            if (pickup == "exception") throw new InvalidOperationException("water pickup route changed");
            if (pickup == "stall") await Task.Delay(Timeout.Infinite, ct);
            return ActionResult.Fail("no reachable drops nearby");
        };
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var watch = Stopwatch.StartNew();
        var registry = SkillRegistry.CreateDefault();
        var result = await registry.RunAsync(g.Context(registry), "harvest_wheat", FakeGame.J(new { radius = 8 }), limit.Token);
        Assert.True(result.Ok, result.Text);
        Assert.Contains("harvested 2", result.Text);
        Assert.Contains("replanted 2", result.Text);
        Assert.Contains("2 drop(s) left behind", result.Text);
        Assert.True(g.DryCollectionRequested, "harvest must request dry-ground/rim pickup, not enter a flowing trench");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"pickup held up harvesting for {watch.Elapsed}");
    }
}
