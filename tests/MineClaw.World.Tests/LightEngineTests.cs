using System.Diagnostics;
using MineClaw.Data;
using MineClaw.World;
using Xunit;
using Xunit.Abstractions;

namespace MineClaw.World.Tests;

/// <summary>
/// The b1.8.1 light engine against hand-computed mc-dev values (World.b's BFS: each step loses max(1, opacity); sky = 15 at
/// or above the height map) and the hostile spawn rule's edge cases (SpawnerCreature / EntityMonster / EntityLiving).
/// </summary>
public class LightEngineTests(ITestOutputHelper output)
{
    /// <summary>An empty 80×128×80 world (5×5 columns, x/z -32..47) at noon, lit like a fresh chunk: every cell within
    /// x/z -15..30 has its 17-block neighbourhood loaded (mc-dev's areChunksLoaded guard).</summary>
    internal static WorldState Empty()
    {
        var w = new WorldState();
        const int n = 80;
        w.ApplyChunkRegion(-32, 0, -32, n, WorldState.Height, n, new byte[n * WorldState.Height * n], null, null, null);
        w.Time = 6000;
        return w;
    }

    internal static void Fill(WorldState w, int x0, int y0, int z0, int x1, int y1, int z1, int id)
    {
        for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
        for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
        for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
            w.SetBlock(x, y, z, id, 0);
    }

    private static int Block(WorldState w, int x, int y, int z) => w.LightAt(new BlockPos(x, y, z)).Block;
    private static int Sky(WorldState w, int x, int y, int z) => w.LightAt(new BlockPos(x, y, z)).Sky;

    [Fact]
    public void Tables_match_mc_dev()
    {
        Assert.Equal(14, LightTables.Emission(Blocks.Torch));
        Assert.Equal(15, LightTables.Emission(Blocks.Glowstone));
        Assert.Equal(13, LightTables.Emission(Blocks.LitFurnace));
        Assert.Equal(7, LightTables.Emission(Blocks.RedstoneTorch));
        Assert.Equal(1, LightTables.Emission(Blocks.BrownMushroom));
        Assert.Equal(11, LightTables.Emission(Blocks.Portal));
        Assert.Equal(1, LightTables.Opacity(Blocks.Leaves));
        Assert.Equal(3, LightTables.Opacity(Blocks.Water));
        Assert.Equal(3, LightTables.Opacity(Blocks.Ice));
        Assert.Equal(255, LightTables.Opacity(Blocks.Lava));
        Assert.Equal(255, LightTables.Opacity(Blocks.Slab));
        Assert.Equal(255, LightTables.Opacity(Blocks.CobblestoneStairs));
        Assert.Equal(255, LightTables.Opacity(Blocks.Farmland));
        Assert.Equal(0, LightTables.Opacity(Blocks.Glass));
        Assert.Equal(0, LightTables.Opacity(Blocks.Chest));
        Assert.Equal(0, LightTables.Opacity(Blocks.Torch));
        Assert.Equal(255, LightTables.Opacity(Blocks.Stone));
        // Spawn floors: normal cubes only.
        Assert.True(LightTables.IsNormalCube(Blocks.Stone));
        Assert.True(LightTables.IsNormalCube(Blocks.DoubleSlab));
        Assert.True(LightTables.IsNormalCube(Blocks.Chest));
        foreach (var id in new[] { Blocks.Slab, Blocks.Glass, Blocks.Leaves, Blocks.Ice, Blocks.Tnt, Blocks.CobblestoneStairs, Blocks.Farmland, 0, Blocks.Water })
            Assert.False(LightTables.IsNormalCube(id), Blocks.Get(id).Name);
    }

    [Fact]
    public void Torch_light_falls_off_by_one_per_block_from_14()
    {
        var w = Empty();
        Fill(w, -10, 63, -10, 10, 63, 10, Blocks.Stone);
        w.SetBlock(0, 64, 0, Blocks.Torch, 5);
        Assert.Equal(14, Block(w, 0, 64, 0));
        Assert.Equal(13, Block(w, 1, 64, 0));
        Assert.Equal(13, Block(w, 0, 65, 0));
        Assert.Equal(10, Block(w, 2, 64, -2));
        Assert.Equal(8, Block(w, 3, 64, 3));
        Assert.Equal(7, Block(w, 4, 64, 3));      // Manhattan 7: dark enough to spawn
        Assert.Equal(1, Block(w, 13, 64, 0));
        Assert.Equal(0, Block(w, 14, 64, 0));
        Assert.Equal(0, Block(w, 0, 63, 0));      // inside the stone floor: opaque, no emission
        Assert.Equal(0, Block(w, 0, 62, 0));      // under the floor: the only way round is past the edge, 11+ blocks away
    }

    [Fact]
    public void Leaves_and_water_dim_a_light_shaft_by_their_opacity()
    {
        var w = Empty();
        // Three 1×1 shafts in a stone block, each open to the sky through its top cell (y 70): air, leaves, water.
        Fill(w, -4, 60, -4, 4, 70, 4, Blocks.Stone);
        foreach (var (x, top) in new[] { (-2, 0), (0, Blocks.Leaves), (2, Blocks.Water) })
        {
            Fill(w, x, 61, 0, x, 70, 0, 0);
            if (top != 0) w.SetBlock(x, 70, 0, top, 0);
        }
        // Open shaft: the height map is its floor, so every cell sees the sky.
        Assert.Equal(15, Sky(w, -2, 61, 0));
        // Leaves (opacity 1): the leaves cell itself 15 - 1, then 1 per air block down.
        Assert.Equal(14, Sky(w, 0, 70, 0));
        Assert.Equal(13, Sky(w, 0, 69, 0));
        Assert.Equal(5, Sky(w, 0, 61, 0));
        // Water (opacity 3): 15 - 3 = 12 in the water, then 11, 10...
        Assert.Equal(12, Sky(w, 2, 70, 0));
        Assert.Equal(11, Sky(w, 2, 69, 0));
        Assert.Equal(3, Sky(w, 2, 61, 0));

        // Block light through water and leaves in a tunnel: a torch at x=-4 of a corridor z=-2, water at x=-1.
        var u = Empty();
        Fill(u, -6, 60, -4, 8, 66, 0, Blocks.Stone);
        Fill(u, -5, 62, -2, 7, 62, -2, 0);
        u.SetBlock(-5, 62, -2, Blocks.Torch, 1);
        u.SetBlock(-2, 62, -2, Blocks.Water, 0);
        u.SetBlock(2, 62, -2, Blocks.Leaves, 0);
        Assert.Equal(14, Block(u, -5, 62, -2));
        Assert.Equal(12, Block(u, -3, 62, -2));
        Assert.Equal(12 - 3, Block(u, -2, 62, -2)); // into the water: opacity 3
        Assert.Equal(8, Block(u, -1, 62, -2));
        Assert.Equal(5, Block(u, 2, 62, -2));        // into the leaves: opacity 1 (same as air)
        Assert.Equal(4, Block(u, 3, 62, -2));
    }

    [Fact]
    public void Sky_light_under_an_overhang_drops_one_per_block_from_the_open_edge()
    {
        var w = Empty();
        Fill(w, -10, 63, -10, 10, 63, 10, Blocks.Stone);
        Fill(w, 0, 67, -10, 4, 67, 10, Blocks.Cobblestone); // a roof strip x 0..4 over the whole floor (open at x -1 and x 5)
        Assert.Equal(15, Sky(w, -1, 64, 0));
        Assert.Equal(14, Sky(w, 0, 64, 0));
        Assert.Equal(13, Sky(w, 1, 64, 0));
        Assert.Equal(12, Sky(w, 2, 64, 0));
        Assert.Equal(13, Sky(w, 3, 64, 0));
        Assert.Equal(14, Sky(w, 4, 66, 0));
        Assert.Equal(0, Sky(w, 2, 63, 0)); // the floor itself
        // Reloading the same blocks without light arrays and relighting (initLighting + Chunk.i) lands on the same values.
        var again = Reload(w);
        foreach (var x in new[] { -1, 0, 1, 2, 3, 4 })
            Assert.Equal(Sky(w, x, 64, 0), Sky(again, x, 64, 0));
    }

    private static WorldState Reload(WorldState w)
    {
        const int n = 80;
        var ids = new byte[n * WorldState.Height * n];
        for (int dx = 0; dx < n; dx++)
        for (int dz = 0; dz < n; dz++)
        for (int y = 0; y < WorldState.Height; y++)
            ids[(dx * n + dz) * WorldState.Height + y] = (byte)w.GetBlock(dx - 32, y, dz - 32);
        var r = new WorldState();
        r.ApplyChunkRegion(-32, 0, -32, n, WorldState.Height, n, ids, null, null, null);
        for (int cx = -2; cx <= 2; cx++) for (int cz = -2; cz <= 2; cz++) r.Light.Relight(cx, cz);
        return r;
    }

    [Fact]
    public void Placing_then_removing_a_torch_restores_the_dark()
    {
        var w = Empty();
        Fill(w, -8, 63, -8, 8, 63, 8, Blocks.Stone);
        Fill(w, -8, 66, -8, 8, 66, 8, Blocks.Stone); // a closed room y 64..65 (walls below)
        Fill(w, -8, 64, -8, 8, 65, -8, Blocks.Stone);
        Fill(w, -8, 64, 8, 8, 65, 8, Blocks.Stone);
        Fill(w, -8, 64, -8, -8, 65, 8, Blocks.Stone);
        Fill(w, 8, 64, -8, 8, 65, 8, Blocks.Stone);
        var cell = new BlockPos(3, 64, 2);
        Assert.Equal(new LightLevel(0, 0), w.LightAt(cell));
        Assert.True(w.CanHostileSpawn(cell, 6000), "a closed room is dark at noon");
        w.SetBlock(0, 64, 0, Blocks.Torch, 5);
        Assert.Equal(9, w.LightAt(cell).Block);
        Assert.False(w.CanHostileSpawn(cell));
        Assert.Equal(7, Block(w, 4, 64, 3));
        Assert.True(w.CanHostileSpawn(new BlockPos(4, 64, 3)), "Manhattan 7 from a torch is light 7: can spawn");
        Assert.False(w.CanHostileSpawn(new BlockPos(3, 64, 3)), "Manhattan 6: light 8");
        w.SetBlock(0, 64, 0, 0, 0);
        for (int x = -7; x <= 7; x++)
        for (int z = -7; z <= 7; z++)
            Assert.Equal(0, Block(w, x, 64, z));
        Assert.True(w.CanHostileSpawn(cell));
    }

    [Fact]
    public void A_column_without_its_neighbours_loaded_is_left_alone_like_mc_dev()
    {
        // One lone column (no columns within 17 blocks): World.b's areChunksLoaded guard skips every update, so a full
        // floor over it costs nothing (it used to cascade for a minute: the floor's underside enclosed by unloaded columns).
        var w = new WorldState();
        w.ApplyChunkRegion(320, 0, 320, 16, WorldState.Height, 16, new byte[16 * WorldState.Height * 16], null, null, null);
        var sw = Stopwatch.StartNew();
        Fill(w, 320, 63, 320, 335, 63, 335, Blocks.Cobblestone);
        w.SetBlock(327, 64, 327, Blocks.Torch, 5);
        Assert.True(sw.ElapsedMilliseconds < 500, $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(0, w.Light.Updates);
        Assert.Equal(0, Block(w, 327, 64, 327));
        Assert.Equal(64, w.Light.Height(327, 327)); // the height map is still kept
    }

    [Fact]
    public void Skylight_subtracted_follows_the_day()
    {
        Assert.Equal(0, LightEngine.SkylightSubtracted(6000));
        Assert.Equal(11, LightEngine.SkylightSubtracted(18000));
        Assert.Equal(3, LightEngine.SkylightSubtracted(6000, rain: 1f));
        Assert.Equal(11, LightEngine.SkylightSubtracted(18000, rain: 1f));
        Assert.InRange(LightEngine.SkylightSubtracted(12500), 1, 10); // dusk
    }

    [Fact]
    public void Spawn_rule_edge_cases()
    {
        var w = Empty();
        Fill(w, -12, 63, -12, 12, 63, 12, Blocks.Stone);
        var open = new BlockPos(0, 64, 0);
        Assert.False(w.CanHostileSpawn(open, 6000), "open sky at noon: light 15");
        Assert.True(w.CanHostileSpawn(open), "open sky after dark: 15 - 11 = 4");
        Assert.Equal(SpawnBlock.Lit, SpawnRules.Check(w, open, 0));

        // Floors: slab, glass, leaves, ice, stairs, farmland, TNT never; a double slab and a chest do.
        foreach (var (id, ok) in new[] { (Blocks.Slab, false), (Blocks.Glass, false), (Blocks.Leaves, false), (Blocks.Ice, false),
                     (Blocks.CobblestoneStairs, false), (Blocks.Farmland, false), (Blocks.Tnt, false), (Blocks.DoubleSlab, true), (Blocks.Chest, true) })
        {
            w.SetBlock(5, 63, 5, id, 0);
            Assert.True(ok == w.CanHostileSpawn(new BlockPos(5, 64, 5)), $"{Blocks.Get(id).Name} floor");
        }
        w.SetBlock(5, 63, 5, Blocks.Stone, 0);

        // Headroom: a normal cube at y+1 blocks it; water in the cell blocks it.
        w.SetBlock(-5, 65, -5, Blocks.Stone, 0);
        Assert.Equal(SpawnBlock.Blocked, SpawnRules.Check(w, new BlockPos(-5, 64, -5), 11));
        w.SetBlock(-5, 65, -5, 0, 0);
        w.SetBlock(-5, 64, -5, Blocks.Water, 0);
        Assert.Equal(SpawnBlock.Blocked, SpawnRules.Check(w, new BlockPos(-5, 64, -5), 11));
        w.SetBlock(-5, 64, -5, 0, 0);

        // Glass over the cell isn't a normal cube: a zombie hits it, a spider (0.9 high) fits when the sides are free.
        w.SetBlock(8, 65, 8, Blocks.Glass, 0);
        Assert.True(w.CanHostileSpawn(new BlockPos(8, 64, 8)), "spider under glass");
        foreach (var o in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) w.SetBlock(8 + o.Item1, 64, 8 + o.Item2, Blocks.Fence, 0);
        Assert.Equal(SpawnBlock.NoRoom, SpawnRules.Check(w, new BlockPos(8, 64, 8), 11));

        // A torch on the floor: lit within Manhattan 6 at night.
        w.SetBlock(-8, 64, 8, Blocks.Torch, 5);
        Assert.False(w.CanHostileSpawn(new BlockPos(-8, 64, 2)));
        Assert.True(w.CanHostileSpawn(new BlockPos(-8, 64, 1)));

        // World spawn within 24 blocks: never (mc-dev distance² ≥ 576 from the spawn point).
        w.Spawn = new BlockPos(0, 64, 0);
        Assert.Equal(SpawnBlock.NearSpawn, SpawnRules.Check(w, open, 11));
        Assert.False(w.CanHostileSpawn(open));
    }

    [Fact]
    public void Slab_and_stairs_cells_read_their_brightest_neighbour()
    {
        var w = Empty();
        Fill(w, -8, 63, -8, 8, 63, 8, Blocks.Stone);
        w.SetBlock(0, 64, 0, Blocks.Slab, 0);
        w.SetBlock(1, 64, 0, Blocks.Torch, 5);
        // The slab cell is opaque (255): its own block light is 0, getLightLevel takes the torch's 14 beside it.
        Assert.Equal(0, Block(w, 0, 64, 0));
        Assert.Equal(14, w.Light.LightLevel(0, 64, 0, 11));
    }

    [Fact]
    public void Light_updates_are_incremental_and_cheap()
    {
        // A 24×24 island at y63 in the middle of 5×5 loaded columns (the void around it lit by the open sky, like live).
        var w = new WorldState();
        const int n = 80;
        w.ApplyChunkRegion(-40, 0, -40, n, WorldState.Height, n, new byte[n * WorldState.Height * n], null, null, null);
        var sw = Stopwatch.StartNew();
        Fill(w, -12, 63, -12, 11, 63, 11, Blocks.Dirt);
        double perFloor = sw.Elapsed.TotalMilliseconds / 576;
        long floorVisits = w.Light.Visits / 576;
        output.WriteLine($"floor block over the void: {perFloor:0.00} ms, {floorVisits} visits each");
        long u0 = w.Light.Updates, v0 = w.Light.Visits;
        sw.Restart();
        for (int i = 0; i < 40; i++) w.SetBlock(-10 + i % 8 * 3, 64, -10 + i / 8 * 4, Blocks.Torch, 5);
        for (int i = 0; i < 40; i++) w.SetBlock(-10 + i % 8 * 3, 64, -10 + i / 8 * 4, 0, 0);
        sw.Stop();
        long visits = w.Light.Visits - v0, updates = w.Light.Updates - u0;
        output.WriteLine($"80 torch changes: {updates} updates, {visits / 80} visits and {sw.Elapsed.TotalMilliseconds / 80:0.00} ms each");
        Assert.Equal(160, updates); // World.p only: a torch (opacity 0) never moves the height map
        Assert.True(visits / 80 < 20_000, $"≈{visits / 80} visits per torch change");
        Assert.True(sw.Elapsed.TotalMilliseconds / 80 < 25, $"{sw.Elapsed.TotalMilliseconds / 80:0.0} ms per torch change");
        Assert.True(floorVisits < 20_000, $"{floorVisits} visits per floor block");
        Assert.True(perFloor < 25, $"{perFloor:0.0} ms per floor block");
        for (int x = -11; x <= 10; x++) Assert.Equal(0, Block(w, x, 64, 0)); // every torch's light gone again
        // A block placed in the open: the column under it goes dark, lit again from the open sides.
        w.SetBlock(25, 70, 25, Blocks.Cobblestone, 0);
        Assert.Equal(14, Sky(w, 25, 69, 25));
        Assert.Equal(15, Sky(w, 25, 71, 25));
    }
}
