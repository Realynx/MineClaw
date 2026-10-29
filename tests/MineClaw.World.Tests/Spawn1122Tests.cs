using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Release1122;
using MineClaw.World.Versioning;
using Xunit;

namespace MineClaw.World.Tests;

/// <summary>
/// Multi-version P6: the 1.12.2 light tables and hostile-spawn block rules (<see cref="Light1122"/>, ported from the decompiled
/// client: <c>WorldEntitySpawner.canCreatureTypeSpawnAtLocation</c> / <c>isValidEmptySpawnBlock</c>, <c>Block.isTopSolid</c> and
/// its slab/stairs/snow/hopper/piston overrides, the registrations' light values) and the chunk biomes the spawn rule reads.
/// b1.8.1 keeps mc-dev's rule.
/// </summary>
public class Spawn1122Tests
{
    private static WorldState Dark(IPhysicsRules physics, int height)
    {
        var w = new WorldState(height: height, physics: physics);
        const int n = 80;
        // Unlit (sky 0) so only the block rules decide; spawn point far away. x/z -32..47: the light engine's 17-block
        // neighbourhood is loaded around the cells used.
        w.ApplyChunkRegion(-32, 0, -32, n, height, n, new byte[n * height * n], null, new byte[n * height * n], new byte[n * height * n]);
        w.Spawn = new BlockPos(1000, 64, 1000);
        w.Time = 18000;
        return w;
    }

    private static SpawnBlock At(WorldState w, int x, int y, int z) => SpawnRules.Check(w, new BlockPos(x, y, z), 11);

    [Fact]
    public void Top_slabs_and_upside_down_stairs_are_spawn_floors_in_1122_not_in_b181()
    {
        var modern = Dark(Release1122PhysicsRules.Instance, 256);
        var beta = Dark(Beta181PhysicsRules.Instance, 128);
        foreach (var w in new[] { modern, beta })
        {
            w.SetBlock(0, 63, 0, Blocks.Slab, 8);          // top stone slab
            w.SetBlock(2, 63, 0, Blocks.Slab, 0);          // bottom slab
            w.SetBlock(4, 63, 0, Blocks.CobblestoneStairs, 4); // upside-down stairs
            w.SetBlock(6, 63, 0, Blocks.Stone, 0);
        }
        Assert.Equal(SpawnBlock.Spawnable, At(modern, 0, 64, 0));
        Assert.Equal(SpawnBlock.NoFloor, At(modern, 2, 64, 0));
        Assert.Equal(SpawnBlock.Spawnable, At(modern, 4, 64, 0));
        Assert.Equal(SpawnBlock.Spawnable, At(modern, 6, 64, 0));
        Assert.Equal(SpawnBlock.NoFloor, At(beta, 0, 64, 0));     // mc-dev World.e: a slab is never a normal cube
        Assert.Equal(SpawnBlock.NoFloor, At(beta, 4, 64, 0));
        Assert.Equal(SpawnBlock.Spawnable, At(beta, 6, 64, 0));
        // Glowstone lights the cell above it anyway; as a floor: Material.GLASS is never top solid in 1.12.2, while b1.8.1's
        // glowstone is a normal (rock-material) cube.
        Assert.False(Release1122PhysicsRules.Instance.IsSpawnFloor(Blocks.Glowstone, 0));
        Assert.True(((IPhysicsRules)Beta181PhysicsRules.Instance).IsSpawnFloor(Blocks.Glowstone, 0));
    }

    [Fact]
    public void New_full_blocks_are_floors_bedrock_barrier_and_magma_are_not()
    {
        var w = Dark(Release1122PhysicsRules.Instance, 256);
        w.SetBlock(0, 63, 0, 159, 3);   // stained hardened clay (id 159: unknown to b1.8.1)
        w.SetBlock(2, 63, 0, 251, 0);   // concrete
        w.SetBlock(4, 63, 0, Blocks.Bedrock, 0);
        w.SetBlock(6, 63, 0, 166, 0);   // barrier
        w.SetBlock(8, 63, 0, 213, 0);   // magma: fire-immune mobs only
        w.SetBlock(10, 63, 0, 161, 0);  // leaves2: translucent
        Assert.Equal(SpawnBlock.Spawnable, At(w, 0, 64, 0));
        Assert.Equal(SpawnBlock.Spawnable, At(w, 2, 64, 0));
        Assert.Equal(SpawnBlock.NoFloor, At(w, 4, 64, 0));
        Assert.Equal(SpawnBlock.NoFloor, At(w, 6, 64, 0));
        Assert.Equal(SpawnBlock.NoFloor, At(w, 8, 64, 0));
        Assert.Equal(SpawnBlock.NoFloor, At(w, 10, 64, 0));
    }

    [Fact]
    public void Rails_and_power_sources_block_the_cell_in_1122_only()
    {
        var modern = Dark(Release1122PhysicsRules.Instance, 256);
        var beta = Dark(Beta181PhysicsRules.Instance, 128);
        foreach (var w in new[] { modern, beta })
        {
            for (int x = 0; x <= 6; x += 2) w.SetBlock(x, 63, 0, Blocks.Stone, 0);
            w.SetBlock(0, 64, 0, Blocks.Rail, 0);
            w.SetBlock(2, 64, 0, Blocks.StonePressurePlate, 0);
            w.SetBlock(4, 64, 0, Blocks.RedstoneWire, 0);
            w.SetBlock(6, 64, 0, Blocks.TallGrass, 1);
        }
        Assert.Equal(SpawnBlock.Blocked, At(modern, 0, 64, 0));
        Assert.Equal(SpawnBlock.Blocked, At(modern, 2, 64, 0));
        Assert.Equal(SpawnBlock.Blocked, At(modern, 4, 64, 0));
        Assert.Equal(SpawnBlock.Spawnable, At(modern, 6, 64, 0));
        Assert.Equal(SpawnBlock.Spawnable, At(beta, 0, 64, 0));
        Assert.Equal(SpawnBlock.Spawnable, At(beta, 2, 64, 0));
        Assert.Equal(SpawnBlock.Spawnable, At(beta, 6, 64, 0));
    }

    [Fact]
    public void Mushroom_island_biomes_spawn_no_monsters()
    {
        var w = Dark(Release1122PhysicsRules.Instance, 256);
        w.SetBlock(0, 63, 0, 110, 0);  // mycelium
        Assert.Equal(-1, w.GetBiome(0, 0));
        Assert.Equal(SpawnBlock.Spawnable, At(w, 0, 64, 0));
        var biomes = new byte[256];
        Array.Fill(biomes, (byte)14);   // mushroom island
        w.SetBiomes(0, 0, biomes);
        Assert.Equal(14, w.GetBiome(0, 0));
        Assert.Equal(SpawnBlock.Biome, At(w, 0, 64, 0));
        Array.Fill(biomes, (byte)4);    // forest
        biomes[15 << 4 | 3] = 15;       // (x 3, z 15): the shore
        w.SetBiomes(0, 0, biomes);
        Assert.Equal(SpawnBlock.Spawnable, At(w, 0, 64, 0));
        Assert.Equal(15, w.GetBiome(3, 15));
    }

    [Fact]
    public void The_1122_light_tables_know_the_new_blocks_and_stained_glass()
    {
        Assert.Equal(15, Light1122.Emission(169));   // sea lantern
        Assert.Equal(14, Light1122.Emission(198));   // end rod
        Assert.Equal(15, Light1122.Emission(124));   // lit redstone lamp
        Assert.Equal(0, Light1122.Emission(123));    // unlit lamp
        Assert.Equal(3, Light1122.Emission(213));    // magma (0.2F)
        Assert.Equal(7, Light1122.Emission(130));    // ender chest
        Assert.Equal(0, Light1122.Emission(95));     // stained glass (b1.8.1's 95, the locked chest, is 15)
        Assert.Equal(15, LightTables.Emission(95));
        Assert.Equal(0, Light1122.Opacity(95));
        Assert.Equal(255, Light1122.Opacity(159));   // stained clay
        Assert.Equal(255, Light1122.Opacity(162));   // log2
        Assert.Equal(1, Light1122.Opacity(161));     // leaves2
        Assert.Equal(255, Light1122.Opacity(126));   // wooden slab
        Assert.Equal(255, Light1122.Opacity(163));   // acacia stairs
        Assert.Equal(0, Light1122.Opacity(165));     // slime (BlockBreakable)
        Assert.Equal(0, Light1122.Opacity(154));     // hopper
        Assert.Equal(3, Light1122.Opacity(212));     // frosted ice
        // Old ids keep mc-dev's values.
        Assert.Equal(LightTables.Opacity(Blocks.Water), Light1122.Opacity(Blocks.Water));
        Assert.Equal(LightTables.Emission(Blocks.Torch), Light1122.Emission(Blocks.Torch));
    }

    [Fact]
    public void A_placed_sea_lantern_lights_its_neighbourhood_in_a_1122_world()
    {
        var w = Dark(Release1122PhysicsRules.Instance, 256);
        w.SetBlock(0, 70, 0, 169, 0);
        Assert.Equal(14, w.LightAt(new BlockPos(1, 70, 0)).Block);
        Assert.Equal(12, w.LightAt(new BlockPos(3, 70, 0)).Block);
        var beta = Dark(Beta181PhysicsRules.Instance, 128);
        beta.SetBlock(0, 70, 0, 169, 0);              // no such block in b1.8.1: no light
        Assert.Equal(0, beta.LightAt(new BlockPos(1, 70, 0)).Block);
    }
}
