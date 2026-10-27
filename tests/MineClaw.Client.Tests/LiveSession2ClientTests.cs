using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Second live session: "couldn't replant cuz it's too dark" at night right after chopping.</summary>
public class SaplingLightTests
{
    /// <summary>A 16x16 column region whose sky light is <paramref name="sky"/> everywhere except <paramref name="dark"/> cells (0).</summary>
    private static WorldState World(byte sky, params BlockPos[] dark)
    {
        var w = new WorldState();
        int vol = 16 * 128 * 16;
        var light = new byte[vol];
        Array.Fill(light, sky);
        foreach (var d in dark) light[(d.X * 16 + d.Z) * 128 + d.Y] = 0;
        w.ApplyChunkRegion(0, 0, 0, 16, 128, 16, new byte[vol], null, null, light);
        return w;
    }

    [Fact]
    public void Chopped_trunk_cell_is_relit_from_its_neighbours_not_left_at_its_stale_zero()
    {
        // Trunk cell 5,64,5 was a log (opaque: stored light 0); block-change packets carry no light.
        var w = World(15, new BlockPos(5, 64, 5), new BlockPos(5, 65, 5));
        w.SetBlock(5, 63, 5, Blocks.Dirt, 0);
        Assert.Equal(0, w.GetSkyLight(5, 64, 5)); // the stale value the old check read ("too dark")
        Assert.True(TreeScan.SaplingLight(w, new BlockPos(5, 64, 5)) >= 8);
    }

    [Fact]
    public void Night_does_not_lower_the_light_saplings_need()
    {
        // b1.8.1 World.k = raw max(sky, block) light, no night subtraction: an open spot at night is 15.
        var w = World(15);
        Assert.Equal(15, TreeScan.SaplingLight(w, new BlockPos(3, 64, 3)));
    }

    [Fact]
    public void A_sealed_dark_cell_is_still_too_dark()
    {
        var w = World(0);
        foreach (var f in BlockFaceExtensions.All) { var n = new BlockPos(8, 40, 8).Offset(f); w.SetBlock(n.X, n.Y, n.Z, Blocks.Stone, 0); }
        Assert.True(TreeScan.SaplingLight(w, new BlockPos(8, 40, 8)) < 8);
    }
}

/// <summary>Second live session: "I'm right under you foxy but can't find a way up" with 5 logs and no cobblestone.</summary>
public class PillarMaterialTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private Func<ClientOptions, ClientOptions> Logged => o => o with { Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } } };

    private static void Floor(FakeServer s)
    {
        for (int x = 2; x <= 14; x++)
        for (int z = 2; z <= 14; z++)
            s.Blocks[(x, 63, z)] = Blocks.Stone;
        s.SpawnAt = (4.5, 64, 8.5);
        s.ConsumeOnPlace = true;
    }

    [Fact]
    public async Task Pillars_with_logs_never_dirt_and_says_what_it_used()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            srv.Inventory[36] = Harness.Stack(Blocks.Dirt, 10);
            srv.Inventory[37] = Harness.Stack(Blocks.Log, 5);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.GotoAsync(new GoalBlock(8, 67, 8), GotoOptions.Default with { AllowPlace = true, AllowPartial = false });
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("built with", r.Message);
        Assert.Contains("log", r.Message);
        Assert.Equal(10, c.World.Inventory.CountItem(Blocks.Dirt));
        Assert.DoesNotContain(s.Blocks, kv => kv.Value == Blocks.Dirt);
    }

    [Fact]
    public async Task Crafts_planks_from_a_log_when_short_of_blocks()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            srv.Inventory[36] = Harness.Stack(Blocks.Dirt, 10);
            srv.Inventory[37] = Harness.Stack(Blocks.Log, 1);
        }, Logged);
        await using var _ = c;
        using var __ = s;
        var r = await c.GotoAsync(new GoalBlock(8, 67, 8), GotoOptions.Default with { AllowPlace = true, AllowPartial = false });
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("crafted 4 planks from 1 log", r.Message);
        Assert.Contains("built with 3 planks", r.Message);
        Assert.Equal(10, c.World.Inventory.CountItem(Blocks.Dirt));
    }

    [Fact]
    public async Task Dirt_alone_is_not_counted_as_pillar_material()
    {
        var (c, s) = await Harness.StartAsync(srv => { Floor(srv); srv.Inventory[36] = Harness.Stack(Blocks.Dirt, 16); });
        await using var _ = c;
        using var __ = s;
        var r = await c.GotoAsync(new GoalBlock(8, 67, 8), GotoOptions.Default with { AllowPartial = false });
        Assert.False(r.Ok);
        Assert.Contains("have 0", r.Message);
        Assert.Contains("never dirt", r.Message);
    }

    [Fact]
    public void Scaffold_list_has_no_dirt_grass_or_falling_blocks()
    {
        Assert.DoesNotContain(Blocks.Dirt, MinecraftClient.ScaffoldBlocks);
        Assert.DoesNotContain(Blocks.Grass, MinecraftClient.ScaffoldBlocks);
        Assert.DoesNotContain(Blocks.Sand, MinecraftClient.ScaffoldBlocks);
        Assert.Contains(Blocks.Log, MinecraftClient.ScaffoldBlocks);
        Assert.Contains(Blocks.Planks, MinecraftClient.ScaffoldBlocks);
    }
}
