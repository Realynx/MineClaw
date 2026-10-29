using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Skills;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>Multi-version P6: skills in a normal (non-skyblock) world: ground under every edge, natural dirt, no generators.</summary>
public class NormalWorldSkillTests
{
    /// <summary>A 5x5 stone ledge at y 63 with the ground 13 below (no void anywhere).</summary>
    private static FakeGame Ledge()
    {
        var g = new FakeGame();
        g.Fill(-12, 50, -12, 12, 50, 12, Blocks.Stone);
        g.Fill(-2, 63, -2, 2, 63, 2, Blocks.Stone);
        g.Stand(0.5, 64, 0.5);
        return g;
    }

    [Fact]
    public async Task Expand_island_extends_a_ledge_over_a_drop_in_a_normal_world()
    {
        using var _ = WorldMode.Override(WorldMode.Normal);
        var g = Ledge();
        g.Give(Blocks.Cobblestone, 32);
        var r = await g.Run("expand_island", new { side = "east" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("extended the floor", r.Text);
        for (int z = -2; z <= 2; z++) Assert.Equal(Blocks.Cobblestone, g.World.GetBlock(3, 63, z));
    }

    [Fact]
    public async Task Skyblock_expand_island_still_only_grows_over_the_void()
    {
        var g = Ledge(); // ground below: not the void
        g.Give(Blocks.Cobblestone, 32);
        var r = await g.Run("expand_island", new { side = "east" });
        Assert.False(r.Ok);
        Assert.Contains("no open edge", r.Text);
    }

    [Fact]
    public void Extend_floor_names_resolve_to_expand_island()
    {
        var reg = SkillRegistry.CreateDefault();
        Assert.True(reg.TryGet("extend_floor", out var s));
        Assert.Equal("expand_island", s.Spec.Name);
        Assert.DoesNotContain("void", s.Spec.Summary);
        Assert.DoesNotContain("skyblock", s.Spec.Keywords);
    }

    [Fact]
    public void Dirt_is_a_building_block_before_logs_only_where_it_is_plentiful()
    {
        var g = new FakeGame();
        g.Give(Blocks.Log, 8);
        g.Give(Blocks.Dirt, 8);
        Assert.Equal(Blocks.Log, SkillWorld.BuildingBlock(g.Context()));
        using var _ = WorldMode.Override(WorldMode.Normal);
        Assert.Equal(Blocks.Dirt, SkillWorld.BuildingBlock(g.Context()));
        g.Give(Blocks.Cobblestone, 8);
        Assert.Equal(Blocks.Cobblestone, SkillWorld.BuildingBlock(g.Context()));
    }

    [Fact]
    public async Task Pillar_down_refuses_under_sand()
    {
        var g = new FakeGame();
        g.Fill(-4, 50, -4, 4, 63, 4, Blocks.Stone);
        g.Fill(-4, 64, -4, 4, 70, 4, 0);
        // Sand right above every stair's headroom (the first stair's c.Up(3) is y 66 one step out).
        foreach (var o in SkillWorld.Horizontal) g.Set(o.X, 66, o.Z, Blocks.Sand);
        g.Stand(0.5, 64, 0.5);
        var r = await g.Run("pillar_down", new { depth = 3 });
        Assert.False(r.Ok);
        Assert.Contains("sand", r.Text);
    }

    [Fact]
    public void Generator_skills_are_not_available_in_a_normal_world()
    {
        var g = new FakeGame();
        g.Give(Items.LavaBucket, 1);
        g.Give(Items.WaterBucket, 1);
        var reg = SkillRegistry.CreateDefault();
        Assert.True(reg.TryGet("build_cobble_gen", out var gen));
        Assert.Contains("skyblock", gen.Spec.Signature);
        Assert.Null(SkillRegistry.CheckPreconditions(g.Context(), gen.Spec, SkillJson.Empty));
        using var _ = WorldMode.Override(WorldMode.Normal);
        Assert.Contains("not available in this world", SkillRegistry.CheckPreconditions(g.Context(), gen.Spec, SkillJson.Empty));
    }

    [Fact]
    public async Task No_sleeping_in_the_end()
    {
        var g = new FakeGame();
        g.World.Dimension = 1;
        g.World.Time = 14000;
        var r = await g.Run("sleep_in_bed");
        Assert.False(r.Ok);
        Assert.Contains("overworld", r.Text);
    }

    [Fact]
    public void Crops_ripen_by_their_own_stage()
    {
        Assert.True(CropKinds.Ripe(Blocks.Crops, 7) && !CropKinds.Ripe(Blocks.Crops, 3));
        Assert.True(CropKinds.Ripe(CropKinds.Beetroots, 3));
        Assert.True(CropKinds.Ripe(CropKinds.Carrots, 7) && !CropKinds.Ripe(CropKinds.Potatoes, 6));
        Assert.Equal(CropKinds.PotatoItem, CropKinds.Seed(CropKinds.Potatoes));
    }

    [Fact]
    public void Trapped_chest_and_wood_button_and_weighted_plate()
    {
        Assert.True(StorageOps.IsChest(146));
        Assert.True(Openables.IsButton(143) && Openables.IsActuator(143));
        Assert.True(Openables.IsPlate(147) && Openables.IsActuatorOn(148, 3));
        Assert.False(Openables.OpensByHand(Openables.IronTrapdoor));
    }
}
