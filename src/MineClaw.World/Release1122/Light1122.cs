namespace MineClaw.World.Release1122;

/// <summary>
/// 1.12.2 light and hostile-spawn block facts, ported from the decompiled client (multi-version P6). The b1.8.1 tables
/// (<see cref="LightTables"/>) stay right for the old ids except stained glass (95, b1.8.1's locked chest); the rest are the
/// 1.12.2 registrations.
/// <list type="bullet">
/// <item><b>Opacity</b> (<c>Block.lightOpacity</c>): the constructor's <c>fullBlock ? 255 : 0</c> with <c>fullBlock =
/// isOpaqueCube()</c>, then <c>setLightOpacity</c>: leaves 1, frosted ice 3, carpet/anvil/enchanting table 0, slabs, stairs,
/// farmland and grass paths 255. Every block class that overrides <c>isOpaqueCube</c> to false (plants, fences, gates, walls,
/// doors, panes, chests, hoppers, slime, beacons, shulker boxes, banners, end rods, chorus…) is 0.</item>
/// <item><b>Emission</b> (<c>(int)(15 · setLightLevel)</c>): brewing stand, end portal frame and dragon egg 1, magma 3, ender
/// chest 7, powered comparator 9, end rod 14, lit redstone lamp, beacon, sea lantern, end portal and gateway 15.</item>
/// <item><b>Spawn floor</b> (<c>WorldEntitySpawner.canCreatureTypeSpawnAtLocation</c>): the block below is
/// <c>isTopSolid</c> (<c>Block.isTopSolid</c> = opaque material and <c>isFullCube</c>; a top or double slab, upside-down stairs,
/// a full snow layer, a hopper, a retracted piston), and not bedrock or a barrier. Magma (<c>BlockMagma.canEntitySpawn</c>)
/// only takes fire-immune mobs, so it is no floor for the overworld's zombies, skeletons, creepers and spiders. Translucent
/// materials are never top solid: glass, glowstone, sea lanterns and beacons (Material.GLASS), ice, leaves, TNT.</item>
/// <item><b>Spawn cell</b> (<c>isValidEmptySpawnBlock</c>, the cell and the one above): not a normal cube
/// (<c>isBlockNormalCube</c>: a full cube of a movement-blocking material, leaves and ice included), not a power source
/// (plates, buttons, levers, redstone torches/wire/blocks, repeaters, comparators, daylight sensors, observers, tripwire hooks,
/// trapped chests, detector rails), not a liquid, not a rail.</item>
/// <item><b>Biomes</b> (<c>Biome.getSpawnableList(MONSTER)</c>): the mushroom island (14) and its shore (15) and the void (127)
/// list no monsters.</item>
/// </list>
/// </summary>
public static class Light1122
{
    private static readonly byte[] Opacities = BuildOpacity();
    private static readonly byte[] Emissions = BuildEmission();
    private static readonly bool[] NotFullCube = BuildNotFull();
    private static readonly bool[] Translucent = BuildTranslucent();
    private static readonly bool[] NoBlockMaterial = BuildNoBlock();

    public static int Opacity(int id) => (uint)id < 256 ? Opacities[id] : 255;

    public static int Emission(int id) => (uint)id < 256 ? Emissions[id] : 0;

    /// <summary><c>IBlockState.isTopSolid()</c> of (id, meta), minus bedrock, barriers and magma (see the class remarks).</summary>
    public static bool IsSpawnFloor(int id, int meta)
    {
        if ((uint)id >= 256) return false;
        return SpawnFloorRules[id] switch
        {
            FloorRule.Never => false,                               // air, bedrock, barrier; magma (fire-immune mobs only)
            FloorRule.TopSlab => (meta & 8) != 0,                   // single slabs: the top half
            FloorRule.UpsideDownStairs => (meta & 4) != 0,          // stairs: upside down
            FloorRule.FullSnow => (meta & 7) == 7,                  // snow: 8 layers
            FloorRule.Always => true,                               // hopper
            FloorRule.Piston => (meta & 8) == 0 || (meta & 7) == 0, // piston: retracted, or facing down
            FloorRule.PistonHeadUp => (meta & 7) == 1,              // piston head facing up
            _ => IsTopSolidCube(id),
        };
    }

    private enum FloorRule : byte { Cube, Never, TopSlab, UpsideDownStairs, FullSnow, Always, Piston, PistonHeadUp }

    private static readonly FloorRule[] SpawnFloorRules = BuildFloorRules();

    private static FloorRule[] BuildFloorRules()
    {
        var r = new FloorRule[256];
        foreach (int id in new[] { 0, 7, 166, 213 }) r[id] = FloorRule.Never;
        foreach (int id in new[] { 44, 126, 182, 205 }) r[id] = FloorRule.TopSlab;
        foreach (int id in new[] { 53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203 }) r[id] = FloorRule.UpsideDownStairs;
        r[78] = FloorRule.FullSnow;
        r[154] = FloorRule.Always;
        r[29] = r[33] = FloorRule.Piston;
        r[34] = FloorRule.PistonHeadUp;
        return r;
    }

    private static bool IsTopSolidCube(int id) => !NotFullCube[id] && !Translucent[id] && !NoBlockMaterial[id];

    /// <summary><c>WorldEntitySpawner.isValidEmptySpawnBlock</c>: whether a mob's feet or head may be in this cell.</summary>
    public static bool IsSpawnCellClear(int id, int meta)
    {
        if (id == 0) return true;
        if ((uint)id >= 256 || NeverSpawnCell.Contains(id)) return false;          // liquids, rails, power sources
        return NotFullCube[id] || NoBlockMaterial[id];                               // not a normal cube
    }

    /// <summary>Whether the biome lists hostile mobs (−1 = unknown: yes).</summary>
    public static bool BiomeHasMonsters(int biome) => biome is not (14 or 15 or 127);

    /// <summary>Liquids, rails and the blocks that provide power (levers, plates, buttons, trapped chest, redstone block, daylight
    /// sensors, observer, detector rail, repeaters, comparators, torches, wire, tripwire hook).</summary>
    private static readonly IdSet NeverSpawnCell = IdSet.Of(
        [27, 28, 66, 157, 69, 70, 72, 77, 143, 147, 148, 146, 152, 151, 178, 218, 93, 94, 149, 150, 75, 76, 55, 131], (8, 11));

    private static byte[] BuildOpacity()
    {
        var q = new byte[256];
        for (int id = 0; id < 110; id++) q[id] = (byte)LightTables.Opacity(id);
        q[95] = 0; // stained glass (BlockBreakable: not an opaque cube); b1.8.1's 95 was the locked chest
        for (int id = 110; id < 256; id++) q[id] = 255;
        foreach (int id in (int[])[111, 113, 115, 116, 117, 118, 119, 120, 122, 127, 130, 131, 132, 138, 139, 140, 141, 142, 143, 144, 145,
                     146, 147, 148, 149, 150, 151, 154, 157, 160, 165, 166, 167, 171, 175, 176, 177, 178, 198, 199, 200, 207, 209, 217])
            q[id] = 0;
        for (int id = 183; id <= 197; id++) q[id] = 0; // gates, fences, doors
        for (int id = 219; id <= 234; id++) q[id] = 0; // shulker boxes
        q[161] = 1;   // leaves2
        q[212] = 3;   // frosted ice
        q[253] = q[254] = 0; // unused ids
        return q;
    }

    private static byte[] BuildEmission()
    {
        var e = new byte[256];
        for (int id = 0; id < 110; id++) e[id] = (byte)LightTables.Emission(id);
        e[95] = 0;
        e[117] = 1; e[120] = 1; e[122] = 1;
        e[213] = 3;
        e[130] = 7;
        e[150] = 9;
        e[198] = 14;
        e[119] = 15; e[124] = 15; e[138] = 15; e[169] = 15; e[209] = 15;
        return e;
    }

    /// <summary>Classes whose <c>isFullCube</c> is false (plus the single slabs and stairs, decided per state above).</summary>
    private static bool[] BuildNotFull()
    {
        var n = new bool[256];
        foreach (int id in (int[])[0, 6, 8, 9, 10, 11, 20, 26, 27, 28, 29, 30, 31, 32, 33, 34, 36, 37, 38, 39, 40, 44, 50, 51, 53, 54, 55, 59,
                     60, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 75, 76, 77, 78, 81, 83, 85, 90, 92, 93, 94, 95, 96, 101, 102, 104, 105, 106,
                     107, 108, 109, 111, 113, 114, 115, 116, 117, 118, 119, 120, 122, 126, 127, 128, 130, 131, 132, 134, 135, 136, 138, 139,
                     140, 141, 142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 154, 156, 157, 160, 163, 164, 167, 171, 175, 176, 177, 178,
                     180, 182, 198, 199, 200, 203, 205, 207, 208, 209, 217])
            n[id] = true;
        for (int id = 183; id <= 197; id++) n[id] = true;
        for (int id = 219; id <= 234; id++) n[id] = true;
        return n;
    }

    /// <summary>Full cubes of a translucent material (Material.isOpaque false): leaves, glass, glowstone, sea lantern, beacon, ice, TNT.</summary>
    private static bool[] BuildTranslucent()
    {
        var t = new bool[256];
        foreach (int id in (int[])[18, 161, 20, 95, 89, 169, 138, 79, 212, 46]) t[id] = true;
        return t;
    }

    /// <summary>Materials that don't block movement (air, plants, circuits, liquids, fire, portals, web, snow layer, carpet).</summary>
    private static bool[] BuildNoBlock()
    {
        var b = new bool[256];
        foreach (int id in (int[])[0, 6, 8, 9, 10, 11, 27, 28, 30, 31, 32, 37, 38, 39, 40, 50, 51, 55, 59, 66, 69, 75, 76, 77, 78, 83, 90, 93,
                     94, 104, 105, 106, 111, 115, 119, 131, 132, 141, 142, 143, 149, 150, 157, 171, 175, 207, 209, 217])
            b[id] = true;
        return b;
    }
}
