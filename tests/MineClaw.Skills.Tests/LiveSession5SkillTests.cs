using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>Live 10-27: "skill failed 2x the same way: 9,65,3 is occupied". Replanting is an idempotent goal.</summary>
public class LiveSession5SkillTests
{
    [Fact]
    public async Task Replanting_a_spot_that_already_has_a_sapling_or_tree_is_ok()
    {
        var g = LiquidTests.Island();
        g.Give(Blocks.Sapling, 2);
        g.Set(1, 64, 1, Blocks.Sapling);
        var r = await g.Run("plant_sapling", new { pos = "1,64,1" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("already planted at 1,64,1", r.Text);
        g.Set(2, 64, 2, Blocks.Log);
        r = await g.Run("plant_sapling", new { pos = "2,64,2" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("already a tree", r.Text);
        Assert.Equal(2, g.World.Inventory.CountItem(Blocks.Sapling)); // nothing used
    }

    [Fact]
    public async Task A_player_standing_on_the_spot_is_skipped_not_a_failure()
    {
        var g = LiquidTests.Island();
        g.Give(Blocks.Sapling, 1);
        g.World.Entities.SpawnPlayer(700, "PoofimaFox", new Vec3d(1.5, 64, 1.5), 0, 0);
        var r = await g.Run("plant_sapling", new { pos = "1,64,1" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("skipped 1,64,1 (PoofimaFox standing there)", r.Text);
        Assert.Equal(1, g.World.Inventory.CountItem(Blocks.Sapling));
    }
}
