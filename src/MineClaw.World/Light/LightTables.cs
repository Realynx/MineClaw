namespace MineClaw.World;

/// <summary>
/// b1.8.1 light properties per block id, exactly as mc-dev's Block static tables (Bukkit mc-dev 218a2a71, Block.java):
/// <list type="bullet">
/// <item><see cref="Opacity"/> = <c>Block.q[]</c> (lightOpacity): 255 when the class's <c>a()</c> (isOpaqueCube) is true at
/// construction time, else 0; then <c>f(n)</c> overrides: water 3, lava 255, leaves 1 (BlockLeaves.a() is !fancy = true on the
/// server, so 255, then f(1)), web 1, ice 3, and f(255) in the BlockStep, BlockStairs and BlockSoil constructors (both slab
/// ids: BlockStep.b is still false while Block's constructor runs, so the double slab's a() reads false there too).</item>
/// <item><see cref="Emission"/> = <c>Block.s[]</c> (lightEmission) = (int)(15 * f) of <c>a(float)</c>: lava/fire/glowstone/
/// jack-o-lantern/locked chest 15, torch 14 (0.9375), lit furnace 13 (0.875), portal 11 (0.75), glowing redstone ore and a
/// powered repeater 9 (0.625), redstone torch 7 (0.5), brown mushroom 1 (0.125).</item>
/// <item><see cref="IsNormalCube"/> = World.e(x,y,z) = <c>block.material.j() &amp;&amp; block.b()</c>: what a mob may stand on
/// and what blocks its spawn cell (SpawnerCreature). Material.j() is false for air, liquids, plants, "orientable"
/// (MaterialLogic), fire, portal, web, snow layer, and the o()-marked leaves, glass, TNT, ice and cactus; b()
/// (renderAsNormalBlock) is false for every class that overrides it (slab 44 but not the double slab 43, stairs, farmland,
/// fences, doors, panes, pistons, rails, plants, torches, signs, plates, buttons, levers, ladders, beds, cake, cactus,
/// repeaters, trapdoors, vines, web, snow layer, fire, portal). Chests, spawners, furnaces and glowstone stay normal cubes.</item>
/// </list>
/// </summary>
public static class LightTables
{
    private static readonly byte[] Opacities = BuildOpacity();
    private static readonly byte[] Emissions = BuildEmission();
    private static readonly bool[] Normal = BuildNormal();

    /// <summary>mc-dev Block.q[id] (0..255); unknown ids are full opaque blocks like Block's default.</summary>
    public static int Opacity(int id) => (uint)id < 256 ? Opacities[id] : 255;

    /// <summary>mc-dev Block.s[id] (0..15).</summary>
    public static int Emission(int id) => (uint)id < 256 ? Emissions[id] : 0;

    /// <summary>mc-dev World.e(): material.j() &amp;&amp; block.b() (a full "normal" cube a mob can spawn on).</summary>
    public static bool IsNormalCube(int id) => (uint)id < 256 && Normal[id];

    private static byte[] BuildOpacity()
    {
        var q = new byte[256];
        for (int id = 1; id < 256; id++) q[id] = BlockShapes.IsOpaqueCube(id) ? (byte)255 : (byte)0;
        // Ids that don't exist in b1.8.1 have no Block: q stays 0 like mc-dev's untouched array slot.
        for (int id = 110; id < 256; id++) q[id] = 0;
        q[0] = 0;
        q[8] = q[9] = 3;            // water: f(3)
        q[10] = q[11] = 255;        // lava: f(255)
        q[18] = 1;                  // leaves: f(1)
        q[30] = 1;                  // web: f(1)
        q[79] = 3;                  // ice: f(3)
        q[43] = q[44] = 255;        // BlockStep constructor: f(255)
        q[53] = q[67] = q[108] = q[109] = 255; // BlockStairs constructor: f(255)
        q[60] = 255;                // BlockSoil constructor: f(255)
        // Construction-time a() differs from the runtime BlockShapes.IsOpaqueCube for the double slab only (covered above).
        return q;
    }

    private static byte[] BuildEmission()
    {
        var s = new byte[256];
        s[10] = s[11] = 15;  // lava a(1.0F)
        s[39] = 1;           // brown mushroom a(0.125F)
        s[50] = 14;          // torch a(0.9375F)
        s[51] = 15;          // fire
        s[62] = 13;          // burning furnace a(0.875F)
        s[74] = 9;           // glowing redstone ore a(0.625F)
        s[76] = 7;           // redstone torch on a(0.5F)
        s[89] = 15;          // glowstone
        s[90] = 11;          // portal a(0.75F)
        s[91] = 15;          // jack-o-lantern
        s[94] = 9;           // repeater on a(0.625F)
        s[95] = 15;          // locked chest
        return s;
    }

    private static bool[] BuildNormal()
    {
        var n = new bool[256];
        for (int id = 1; id <= 109; id++) n[id] = true;
        // Material.j() false: liquids, plants, orientables, fire, portal, web, snow layer + o(): leaves, glass, TNT, ice, cactus.
        // b() false: the classes that override renderAsNormalBlock.
        int[] not =
        [
            6, 8, 9, 10, 11, 18, 20, 26, 27, 28, 29, 30, 31, 32, 33, 34, 36, 37, 38, 39, 40, 44, 46, 50, 51, 53, 55, 59, 60,
            63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 75, 76, 77, 78, 79, 81, 83, 85, 90, 92, 93, 94, 96, 101, 102, 104, 105,
            106, 107, 108, 109,
        ];
        foreach (var id in not) n[id] = false;
        return n;
    }
}
