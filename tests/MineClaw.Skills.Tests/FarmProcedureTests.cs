using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>Item 11 (Fox, live 10-27): per cell always harvest → hoe → plant; reverted farmland is re-tilled; a hoe is acquired.</summary>
public class FarmProcedureTests
{
    private static List<string> Order(FakeGame g, BlockPos crop) =>
        g.Log.Select(l => l.StartsWith($"dig {crop}") ? "harvest" : l.StartsWith($"use {crop.Down()}") ? "hoe" : l.StartsWith($"place {crop}") ? "plant" : null)
            .OfType<string>().ToList();

    [Fact]
    public async Task Each_cell_is_harvest_then_hoe_then_plant_even_on_farmland()
    {
        var g = LiquidTests.Island();
        g.Set(3, 63, 3, Blocks.Water);
        for (int x = 1; x <= 2; x++) { g.Set(x, 63, 2, Blocks.Farmland); g.Set(x, 64, 2, Blocks.Crops, 7); }
        g.Give(Items.StoneHoe, 1);
        var r = await g.Run("harvest_wheat");
        Assert.True(r.Ok, r.Text);
        foreach (var x in new[] { 1, 2 }) Assert.Equal(["harvest", "hoe", "plant"], Order(g, new BlockPos(x, 64, 2)));
        Assert.Contains("replanted 2", r.Text);
    }

    [Fact]
    public async Task Trampled_farmland_is_re_tilled_before_the_seed_goes_in()
    {
        var g = LiquidTests.Island();
        g.Set(3, 63, 3, Blocks.Water);
        g.Set(1, 63, 2, Blocks.Farmland);
        g.Set(1, 64, 2, Blocks.Crops, 7);
        g.Give(Items.WoodenHoe, 1);
        // Someone walked over it after harvesting started: the server reverted it to dirt (mc-dev BlockSoil.b, 1 in 4 per step).
        var reg = SkillRegistry.CreateDefault();
        var crop = new BlockPos(1, 64, 2);
        g.AfterDig = p => { if (p == crop) g.Set(1, 63, 2, Blocks.Dirt); };
        var r = await g.Run("harvest_wheat", null, reg);
        Assert.True(r.Ok, r.Text);
        Assert.Equal(["harvest", "hoe", "plant"], Order(g, crop));
        Assert.Equal(Blocks.Farmland, g.World.GetBlock(1, 63, 2));
        Assert.Equal(Blocks.Crops, g.World.GetBlock(1, 64, 2));
        Assert.Contains("re-tilled 1", r.Text);
    }

    [Fact]
    public async Task No_hoe_is_acquired_first_then_used()
    {
        var g = LiquidTests.Island();
        g.Set(1, 63, 2, Blocks.Farmland);
        g.Set(1, 64, 2, Blocks.Crops, 7);
        var reg = SkillRegistry.CreateDefault();
        int[]? asked = null;
        reg.AcquireMissing = (_, _, ids, _) =>
        {
            asked = ids;
            g.Give(Items.StoneHoe, 1); // the planner crafted one (2 sticks + 2 cobble)
            return Task.FromResult<string?>("crafted stone hoe");
        };
        var r = await g.Run("harvest_wheat", null, reg);
        Assert.True(r.Ok, r.Text);
        Assert.NotNull(asked);
        Assert.Contains(Items.StoneHoe, asked!);
        Assert.Equal(["harvest", "hoe", "plant"], Order(g, new BlockPos(1, 64, 2)));
        Assert.Contains("crafted stone hoe", r.Text);
    }

    [Fact]
    public async Task Empty_farmland_in_the_field_is_planted_too_and_the_real_count_is_reported()
    {
        // Live 10-28, 420: "done, maybe 3 empty plots left"; main: "the field has more farmland than crops".
        var g = LiquidTests.Island();
        g.Set(3, 63, 4, Blocks.Water);
        for (int x = 1; x <= 3; x++)
        {
            g.Set(x, 63, 2, Blocks.Farmland); g.Set(x, 64, 2, Blocks.Crops, 7); // ripe row
            g.Set(x, 63, 3, Blocks.Farmland);                                   // the gaps nobody replanted
        }
        g.Give(Items.StoneHoe, 1);
        var r = await g.Run("harvest_wheat");
        Assert.True(r.Ok, r.Text);
        for (int x = 1; x <= 3; x++) Assert.Equal(Blocks.Crops, g.World.GetBlock(x, 64, 3));
        Assert.Contains("replanted 6", r.Text);
        Assert.Contains("field: 6/6 farmland planted", r.Text);
    }

    [Fact]
    public async Task Short_on_seeds_says_how_many_plots_stay_empty()
    {
        var g = LiquidTests.Island();
        g.Set(3, 63, 4, Blocks.Water);
        for (int x = 1; x <= 2; x++) { g.Set(x, 63, 2, Blocks.Farmland); g.Set(x, 64, 2, Blocks.Crops, 7); }
        for (int x = 1; x <= 4; x++) g.Set(x, 63, 3, Blocks.Farmland);
        g.Give(Items.StoneHoe, 1);
        var r = await g.Run("harvest_wheat");
        Assert.True(r.Ok, r.Text);
        // 2 ripe crops drop 4 seeds: 2 replant their own cells, 2 go into the 4 gaps.
        Assert.Contains("field: 4/6 planted, 2 empty: out of seeds (need 2 more)", r.Text);
    }

    [Fact]
    public async Task Without_any_hoe_it_still_plants_on_farmland()
    {
        var g = LiquidTests.Island();
        g.Set(1, 63, 2, Blocks.Farmland);
        g.Set(1, 64, 2, Blocks.Crops, 7);
        var reg = SkillRegistry.CreateDefault();
        reg.AcquireMissing = (_, _, _, _) => Task.FromResult<string?>(null);
        var r = await g.Run("harvest_wheat", null, reg);
        Assert.True(r.Ok, r.Text);
        Assert.Equal(["harvest", "plant"], Order(g, new BlockPos(1, 64, 2)));
        Assert.Contains("no hoe", r.Text);
    }
}
