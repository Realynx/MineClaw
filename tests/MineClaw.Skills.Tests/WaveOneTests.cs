using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

public class FarmingTests
{
    [Fact]
    public async Task Harvest_wheat_only_ripe_and_replants()
    {
        var g = LiquidTests.Island();
        for (int x = 1; x <= 3; x++) { g.Set(x, 63, 2, Blocks.Farmland); g.Set(x, 64, 2, Blocks.Crops, x == 3 ? 3 : 7); }
        var r = await g.Run("harvest_wheat");
        Assert.True(r.Ok, r.Text);
        Assert.Contains("harvested 2", r.Text);
        Assert.Equal(Blocks.Crops, g.World.GetBlock(1, 64, 2));
        Assert.Equal(0, g.World.GetMeta(1, 64, 2)); // replanted
        Assert.Equal(3, g.World.GetMeta(3, 64, 2)); // unripe untouched
        Assert.Equal(2, g.World.Inventory.CountItem(Items.Wheat));
    }

    [Fact]
    public async Task Harvest_wheat_reports_nothing_ripe()
    {
        var g = LiquidTests.Island();
        g.Set(1, 63, 2, Blocks.Farmland);
        g.Set(1, 64, 2, Blocks.Crops, 2);
        var r = await g.Run("harvest_wheat");
        Assert.False(r.Ok);
        Assert.Contains("1 still growing", r.Text);
    }

    [Fact]
    public async Task Cane_and_cactus_keep_their_bottom_block()
    {
        var g = LiquidTests.Island();
        for (int y = 64; y <= 66; y++) g.Set(2, y, 2, Blocks.SugarCane);
        for (int y = 64; y <= 65; y++) g.Set(-2, y, -2, Blocks.Cactus);
        g.Set(3, 64, 3, Blocks.SugarCane); // 1 tall: leave it
        var r = await g.Run("harvest_cane");
        Assert.True(r.Ok, r.Text);
        Assert.Equal(Blocks.SugarCane, g.World.GetBlock(2, 64, 2));
        Assert.Equal(0, g.World.GetBlock(2, 65, 2));
        Assert.Equal(Blocks.SugarCane, g.World.GetBlock(3, 64, 3));
        Assert.Equal(2, g.World.Inventory.CountItem(Items.SugarCane));
        var c = await g.Run("harvest_cactus");
        Assert.True(c.Ok, c.Text);
        Assert.Equal(Blocks.Cactus, g.World.GetBlock(-2, 64, -2));
        Assert.Equal(0, g.World.GetBlock(-2, 65, -2));
        Assert.False((await g.Run("harvest_cactus")).Ok, "nothing taller than 1 left");
    }

    [Fact]
    public async Task Melons_break_stems_stay()
    {
        var g = LiquidTests.Island();
        g.Set(1, 64, 2, Blocks.MelonStem, 7);
        g.Set(2, 64, 2, Blocks.Melon);
        g.Set(-3, 64, -3, Blocks.Melon); // no stem: someone's build
        var r = await g.Run("harvest_melons");
        Assert.True(r.Ok, r.Text);
        Assert.Equal(0, g.World.GetBlock(2, 64, 2));
        Assert.Equal(Blocks.MelonStem, g.World.GetBlock(1, 64, 2));
        Assert.Equal(Blocks.Melon, g.World.GetBlock(-3, 64, -3));
    }

    [Fact]
    public async Task Till_and_plant_with_hoe_near_water_or_fail_without_primitive()
    {
        var g = LiquidTests.Island();
        g.Set(3, 63, 3, Blocks.Water);
        g.Give(Items.StoneHoe, 1);
        g.Give(Items.Seeds, 4);
        var r = await g.Run("till_soil", new { count = 2 });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("tilled 2, planted 2", r.Text);
        g.Supported = Primitive.None;
        var no = await g.Run("till_soil");
        Assert.False(no.Ok);
        Assert.Contains("client primitive", no.Text);
    }

    [Fact]
    public async Task Bonemeal_grows_crops_and_needs_bonemeal()
    {
        var g = LiquidTests.Island();
        g.Set(1, 63, 2, Blocks.Farmland);
        g.Set(1, 64, 2, Blocks.Crops, 1);
        Assert.False((await g.Run("apply_bonemeal")).Ok);
        g.Give(Items.Dye, 3, Items.BoneMeal);
        var r = await g.Run("apply_bonemeal");
        Assert.True(r.Ok, r.Text);
        Assert.Equal(7, g.World.GetMeta(1, 64, 2));
    }

    [Fact]
    public async Task Sapling_needs_room_and_a_sapling()
    {
        var g = LiquidTests.Island();
        Assert.False((await g.Run("plant_sapling")).Ok);
        g.Give(Blocks.Sapling, 1);
        var r = await g.Run("plant_sapling");
        Assert.True(r.Ok, r.Text);
        Assert.Single(g.World.FindBlocks(new Vec3d(0, 64, 0), 8, Blocks.Sapling));
    }

    [Fact]
    public async Task Plant_seeds_on_empty_farmland()
    {
        var g = LiquidTests.Island();
        g.Set(2, 63, 2, Blocks.Farmland);
        g.Give(Items.Seeds, 2);
        Assert.True((await g.Run("plant_seeds")).Ok);
        Assert.Equal(Blocks.Crops, g.World.GetBlock(2, 64, 2));
        Assert.False((await g.Run("plant_seeds")).Ok, "no empty farmland left");
    }
}

public class BuildingTests
{
    [Fact]
    public async Task Expand_island_places_only_supported_edge_cells_and_never_stands_over_void()
    {
        var g = LiquidTests.Island(2);
        g.Give(Blocks.Cobblestone, 64);
        var r = await g.Run("expand_island", new { side = "east", rows = 2 });
        Assert.True(r.Ok, r.Text);
        for (int z = -2; z <= 2; z++)
        {
            Assert.Equal(Blocks.Cobblestone, g.World.GetBlock(3, 63, z));
            Assert.Equal(Blocks.Cobblestone, g.World.GetBlock(4, 63, z));
        }
        Assert.Equal(0, g.World.GetBlock(-3, 63, 0)); // other sides untouched
        Assert.Empty(g.Violations);
        Assert.All(g.Visited, v => Assert.False(Safety.IsPlayerOverVoid(g.World, v)));
        // EdgeGuard agrees every visited position is on solid footing (near edge = sneak, never over void).
        Assert.All(g.Visited, v => Assert.False(g.World.IsPlayerOverVoid(v)));
    }

    [Fact]
    public async Task Expand_island_without_blocks_or_with_a_mob_close_refuses()
    {
        var g = LiquidTests.Island(2);
        Assert.Contains("no building blocks", (await g.Run("expand_island")).Text);
        g.Give(Blocks.Cobblestone, 16);
        g.World.Entities.SpawnMob(7, MobTypes.Zombie, new Vec3d(2.5, 64, 0.5), 0, 0);
        var r = await g.Run("expand_island");
        Assert.False(r.Ok);
        Assert.Contains("zombie", r.Text);
    }

    [Fact]
    public async Task Fill_floor_and_wall_and_line()
    {
        var g = LiquidTests.Island(3);
        g.Give(Blocks.Cobblestone, 64);
        Assert.True((await g.Run("fill_floor", new { from = "4,63,-1", to = "5,63,1" })).Ok);
        Assert.Equal(Blocks.Cobblestone, g.World.GetBlock(5, 63, 1));
        Assert.True((await g.Run("build_wall", new { from = "-3,64,-3", to = "-3,64,-1", height = 2 })).Ok);
        Assert.Equal(Blocks.Cobblestone, g.World.GetBlock(-3, 65, -2));
        Assert.True((await g.Run("place_line", new { from = "0,63,4", to = "0,63,6" })).Ok);
        Assert.Equal(Blocks.Cobblestone, g.World.GetBlock(0, 63, 6));
        Assert.False((await g.Run("place_line", new { from = "0,63,4", to = "3,63,6" })).Ok, "diagonal line refused");
        Assert.False((await g.Run("fill_floor", new { from = "0,63,4", to = "1,64,6" })).Ok, "non-flat floor refused");
        Assert.Empty(g.Violations);
    }

    [Fact]
    public async Task Pillar_up_and_down_staircase()
    {
        var g = new FakeGame();
        g.Fill(-4, 50, -4, 4, 63, 4, Blocks.Stone);
        g.Stand(0.5, 64, 0.5);
        g.Give(Blocks.Cobblestone, 8);
        var up = await g.Run("pillar_up", new { height = 3 });
        Assert.True(up.Ok, up.Text);
        Assert.Equal(67, g.World.Player.Position.ToBlockPos().Y);
        g = new FakeGame();
        g.Fill(-4, 50, -4, 4, 63, 4, Blocks.Stone);
        g.Stand(0.5, 64, 0.5);
        var down = await g.Run("pillar_down", new { depth = 3 });
        Assert.True(down.Ok, down.Text);
        Assert.Equal(61, g.World.Player.Position.ToBlockPos().Y);
        var g2 = LiquidTests.Island(1); // only 3 deep: a 3-step staircase would open the void
        Assert.False((await g2.Run("pillar_down", new { depth = 3 })).Ok);
    }

    [Fact]
    public async Task Light_area_places_spaced_torches_in_the_dark()
    {
        var g = LiquidTests.Island(4);
        g.Give(Blocks.Torch, 4);
        var r = await g.Run("light_area", new { radius = 6 });
        Assert.True(r.Ok, r.Text);
        var torches = g.World.FindBlocks(new Vec3d(0, 64, 0), 8, Blocks.Torch);
        Assert.InRange(torches.Count, 1, 4);
        Assert.False((await new FakeGame().Run("light_area")).Ok, "no torches");
    }

    [Fact]
    public async Task Ladder_must_stick_to_a_wall()
    {
        var g = LiquidTests.Island();
        g.Fill(3, 64, 0, 3, 67, 0, Blocks.Stone);
        g.Give(Blocks.Ladder, 4);
        var r = await g.Run("place_ladder", new { pos = "2,64,0", height = 3 });
        Assert.True(r.Ok, r.Text);
        Assert.Equal(4, g.World.GetMeta(2, 65, 0));
        Assert.False((await g.Run("place_ladder", new { pos = "-2,64,-2" })).Ok, "no wall");
    }
}

public class MiscWaveOneTests
{
    [Fact]
    public async Task Smelt_batch_picks_fuel_and_refuses_unsmeltable()
    {
        var g = LiquidTests.Island();
        g.Set(2, 64, 2, Blocks.Furnace);
        g.Give(Blocks.Cobblestone, 20);
        g.Give(Items.Coal, 3);
        var r = await g.Run("smelt_batch", new { item = "stone", count = 16 });
        Assert.True(r.Ok, r.Text);
        Assert.Contains($"smelt 4 16 fuel {Items.Coal}", g.Log.Last(l => l.StartsWith("smelt")));
        Assert.False((await g.Run("smelt_batch", new { item = "dirt" })).Ok);
        var logs = LiquidTests.Island();
        logs.Set(2, 64, 2, Blocks.Furnace);
        logs.Give(Blocks.Log, 8);
        logs.Give(Blocks.Planks, 4);
        var ch = await logs.Run("smelt_batch", new { item = "charcoal" });
        Assert.True(ch.Ok, ch.Text);
        Assert.Contains($"fuel {Blocks.Planks}", logs.Log.Last(l => l.StartsWith("smelt")));
    }

    [Fact]
    public async Task Sleep_only_at_night_safe_and_not_in_the_nether()
    {
        var g = LiquidTests.Island();
        g.Set(2, 64, 2, Blocks.Bed, 8);
        Assert.Contains("day", (await g.Run("sleep_in_bed")).Text);
        g.World.Time = 14000;
        g.World.Entities.SpawnMob(9, MobTypes.Skeleton, new Vec3d(4.5, 64, 2.5), 0, 0);
        Assert.Contains("skeleton", (await g.Run("sleep_in_bed")).Text);
        g.World.Entities.Destroy(9);
        var r = await g.Run("sleep_in_bed");
        Assert.True(r.Ok, r.Text);
        Assert.NotEqual(0, g.World.GetMeta(2, 64, 2) & 4);
        g.World.Dimension = -1;
        Assert.False((await g.Run("sleep_in_bed")).Ok);
    }

    [Fact]
    public async Task Hold_equip_drop_and_give()
    {
        var g = LiquidTests.Island();
        g.SetSlot(36, Blocks.Dirt, 10);
        g.SetSlot(20, Items.IronSword, 1);
        g.SetSlot(21, Items.IronHelmet, 1);
        var h = await g.Run("hold_item", new { item = "iron sword" });
        Assert.True(h.Ok, h.Text);
        Assert.Equal(Items.IronSword, g.World.Inventory.HeldItem(0).Id);
        Assert.True((await g.Run("equip_armor")).Ok);
        Assert.Equal(Items.IronHelmet, g.World.Inventory.PlayerWindow.Get(5).Id);
        var d = await g.Run("drop_items", new { item = "dirt", count = 3 });
        Assert.True(d.Ok, d.Text);
        Assert.Equal(7, g.World.Inventory.CountItem(Blocks.Dirt));
        Assert.False((await g.Run("give_items", new { player = "nobody", item = "dirt" })).Ok);
        g.World.Entities.SpawnPlayer(11, "kiwi", new Vec3d(2.5, 64, 2.5), 0, 0);
        var gv = await g.Run("give_items", new { player = "kiwi", item = "dirt", count = 5 });
        Assert.True(gv.Ok, gv.Text);
        Assert.Equal(2, g.World.Inventory.CountItem(Blocks.Dirt));
        Assert.Empty(g.Violations);
    }

    [Fact]
    public async Task Repeater_cake_jukebox_signs()
    {
        var g = LiquidTests.Island();
        g.Set(2, 64, 2, Blocks.RepeaterOff, 1);
        Assert.True((await g.Run("set_repeater", new { pos = "2,64,2", delay = 3 })).Ok);
        Assert.Equal(2, (g.World.GetMeta(2, 64, 2) >> 2) & 3);
        g.Set(-2, 64, -2, Blocks.Cake, 0);
        Assert.False((await g.Run("eat_cake")).Ok, "not hungry");
        g.World.Player.SetHealth(20, 12, 0);
        Assert.True((await g.Run("eat_cake")).Ok);
        g.SignText[new BlockPos(1, 64, -2)] = ["Iron", "storage", "", ""];
        Assert.Contains("Iron | storage", (await g.Run("read_sign")).Text);
        g.Give(Items.Sign, 1);
        Assert.True((await g.Run("write_sign", new { pos = "-1,64,2", text = "ores here" })).Ok);
        Assert.Equal("ores here", g.SignText[new BlockPos(-1, 64, 2)][0]);
    }

    [Fact]
    public async Task Stubs_and_missing_primitives_fail_honestly()
    {
        var g = LiquidTests.Island();
        var r = await g.Run("go_fishing");
        Assert.False(r.Ok);
        Assert.Contains("not implemented", r.Text);
        g.Supported = Primitive.None;
        g.Give(Items.Shears, 1);
        Assert.Contains("client primitive", (await g.Run("shear_sheep")).Text);
        Assert.Contains("no skill 'chop_the_moon'", (await g.Run("chop_the_moon")).Text);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var g = LiquidTests.Island();
        g.Give(Blocks.Cobblestone, 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await SkillRegistry.CreateDefault().RunAsync(g.Context(), "expand_island", SkillJson.Empty, cts.Token);
        Assert.False(r.Ok);
        Assert.Contains("cancelled", r.Text);
    }
}
