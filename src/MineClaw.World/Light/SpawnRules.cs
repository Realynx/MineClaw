using MineClaw.Data;

namespace MineClaw.World;

/// <summary>Why a cell can't host a hostile spawn (or <see cref="Spawnable"/>).</summary>
public enum SpawnBlock : byte
{
    Spawnable,
    NotLoaded,
    /// <summary>The block under it isn't a normal cube (slab, glass, leaves, TNT, ice, stairs, farmland, air...).</summary>
    NoFloor,
    /// <summary>The cell or the one above is a normal cube, or the cell holds a liquid.</summary>
    Blocked,
    /// <summary>A zombie (0.6×1.8) and a spider (1.4×0.9) both hit a collision box or a liquid there.</summary>
    NoRoom,
    /// <summary>Light above 7 (mc-dev: <c>light ≤ random.nextInt(8)</c>).</summary>
    Lit,
    /// <summary>Within 24 blocks of the world spawn point (SpawnerCreature: distance² ≥ 576).</summary>
    NearSpawn,
    /// <summary>The biome lists no monsters (1.12.2 mushroom island/shore, the void biome).</summary>
    Biome,
}

/// <summary>
/// The hostile spawn rule for one cell; the block and biome tests come from the version (<see cref="Versioning.IPhysicsRules"/>:
/// b1.8.1 below; 1.12.2 <c>Light1122</c>, the same light and distance rules). The b1.8.1 rule (mc-dev 218a2a71), the mob's feet at (x + 0.5, y, z + 0.5):
/// <list type="number">
/// <item>SpawnerCreature.a(MONSTER): <c>World.e(x, y−1, z) &amp;&amp; !World.e(x, y, z) &amp;&amp; !liquid(x, y, z) &amp;&amp;
/// !World.e(x, y+1, z)</c> (<see cref="LightTables.IsNormalCube"/>: never on slabs, glass, leaves, ice, TNT, stairs, farmland).</item>
/// <item>SpawnerCreature: (x+0.5−spawnX)² + (y−spawnY)² + (z+0.5−spawnZ)² ≥ 576, and no player within 24 (positional, not
/// a cell property: not checked here).</item>
/// <item>EntityMonster.d(): <c>sky light &gt; random(32)</c> fails (never certain: sky ≤ 15), then
/// <c>getLightLevel(x, y, z) ≤ random(8)</c> with World.k (10 during a thunderstorm): possible iff the light is ≤ 7.
/// EntityCreature.d(): <c>0.5 − brightness ≥ 0</c> (true for every light ≤ 7).</item>
/// <item>EntityLiving.d(): no collision box and no liquid inside the mob's box; the zombie/skeleton/creeper box
/// (0.6×1.8) or the spider's (1.4×0.9) must fit.</item>
/// </list>
/// </summary>
public static class SpawnRules
{
    /// <param name="maxLight">Brightest light that still counts as spawnable: 7 is the rule; a higher value asks "darker than
    /// <paramref name="maxLight"/> + 1" (a lighting margin).</param>
    public static SpawnBlock Check(WorldState w, BlockPos p, int skylightSubtracted, bool spawnRadius = true, int maxLight = 7)
    {
        int x = p.X, y = p.Y, z = p.Z;
        if ((uint)y >= w.WorldHeight || y < 1 || !w.IsLoaded(x, z)) return SpawnBlock.NotLoaded;
        if (BlockRules(w, x, y, z) is { } refused) return refused;
        if (w.Light.LightLevel(x, y, z, skylightSubtracted) > maxLight) return SpawnBlock.Lit;
        if (spawnRadius && NearSpawn(w, x, y, z)) return SpawnBlock.NearSpawn;
        if (!Fits(w, x + 0.5, y, z + 0.5, 0.6, 1.8) && !Fits(w, x + 0.5, y, z + 0.5, 1.4, 0.9)) return SpawnBlock.NoRoom;
        return SpawnBlock.Spawnable;
    }

    /// <summary>The version's block rules (IPhysicsRules: b1.8.1 normal cubes; 1.12.2 isTopSolid / isValidEmptySpawnBlock,
    /// biomes): the floor, the feet and head cells, the biome; null when they all allow a spawn.</summary>
    private static SpawnBlock? BlockRules(WorldState w, int x, int y, int z)
    {
        var rules = w.Physics;
        if (!rules.IsSpawnFloor(w.GetBlock(x, y - 1, z), w.GetMeta(x, y - 1, z))) return SpawnBlock.NoFloor;
        if (!rules.IsSpawnCellClear(w.GetBlock(x, y, z), w.GetMeta(x, y, z))
            || !rules.IsSpawnHeadClear(w.GetBlock(x, y + 1, z), w.GetMeta(x, y + 1, z))) return SpawnBlock.Blocked;
        if (!rules.BiomeSpawnsMonsters(w.GetBiome(x, z))) return SpawnBlock.Biome;
        return null;
    }

    /// <summary>Within 24 blocks of the world spawn (when there is one).</summary>
    private static bool NearSpawn(WorldState w, int x, int y, int z)
    {
        if (!w.HasSpawn) return false;
        float dx = x + 0.5F - w.Spawn.X, dy = (float)y - w.Spawn.Y, dz = z + 0.5F - w.Spawn.Z;
        return dx * dx + dy * dy + dz * dz < 576.0F;
    }

    /// <summary>EntityLiving.d()'s block part: no collision box intersects the box and no liquid touches it (World.c(AABB)).</summary>
    public static bool Fits(IBlockAccess w, double cx, double y, double cz, double width, double height)
    {
        double r = width / 2;
        var box = new AABB(cx - r, y, cz - r, cx + r, y + height, cz + r);
        if (BlockShapes.GetCollidingBoxes(w, box).Count > 0) return false;
        int x0 = (int)Math.Floor(box.MinX), x1 = (int)Math.Floor(box.MaxX + 1);
        int y0 = (int)Math.Floor(box.MinY), y1 = (int)Math.Floor(box.MaxY + 1);
        int z0 = (int)Math.Floor(box.MinZ), z1 = (int)Math.Floor(box.MaxZ + 1);
        // mc-dev World.c: a negative min corner widens the scan by one (kept).
        if (box.MinX < 0) x0--;
        if (box.MinY < 0) y0--;
        if (box.MinZ < 0) z0--;
        for (int bx = x0; bx < x1; bx++)
        for (int by = y0; by < y1; by++)
        for (int bz = z0; bz < z1; bz++)
            if (BlockShapes.IsLiquid(w.GetBlock(bx, by, bz))) return false;
        return true;
    }

    /// <summary>"no floor (slab)", "lit (9)"... for reports.</summary>
    public static string Describe(SpawnBlock b) => b switch
    {
        SpawnBlock.Spawnable => "spawnable",
        SpawnBlock.NotLoaded => "not loaded",
        SpawnBlock.NoFloor => "no spawn floor",
        SpawnBlock.Blocked => "solid",
        SpawnBlock.NoRoom => "no room",
        SpawnBlock.Lit => "lit",
        SpawnBlock.NearSpawn => "within 24 of spawn",
        SpawnBlock.Biome => "no monsters in this biome",
        _ => b.ToString(),
    };
}
