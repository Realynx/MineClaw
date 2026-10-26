using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.World.Tests;

/// <summary>Block table for tests, independent of the (in-progress) Data registry.</summary>
public static class TestBlocks
{
    public const int Air = 0, Stone = 1, Grass = 2, Dirt = 3, Cobble = 4, Planks = 5, Bedrock = 7,
        Water = 8, StillWater = 9, Lava = 10, StillLava = 11, Slab = 44, Torch = 50, Fire = 51, Chest = 54,
        Ladder = 65, Ice = 79, Cactus = 81, Fence = 85, Vine = 106;

    private static BlockInfo B(int id, string name, float hardness = 1.5f, bool solid = true, double h = 1.0,
        bool liquid = false, bool climb = false, bool transparent = false, bool interact = false, bool hazard = false) =>
        new(id, name, name, hardness, solid, solid ? h : 0, liquid, climb, transparent, interact, ToolType.None, -1, hazard);

    private static readonly Dictionary<int, BlockInfo> Table = new[]
    {
        B(Air, "air", 0, solid: false, transparent: true),
        B(Stone, "stone"), B(Grass, "grass", 0.6f), B(Dirt, "dirt", 0.5f), B(Cobble, "cobblestone", 2f),
        B(Planks, "planks", 2f), B(Bedrock, "bedrock", -1f),
        B(Water, "water", 100, solid: false, liquid: true, transparent: true),
        B(StillWater, "still_water", 100, solid: false, liquid: true, transparent: true),
        B(Lava, "lava", 100, solid: false, liquid: true, hazard: true),
        B(StillLava, "still_lava", 100, solid: false, liquid: true, hazard: true),
        B(Slab, "slab", 2f, h: 0.5), B(Torch, "torch", 0, solid: false, transparent: true),
        B(Fire, "fire", 0, solid: false, hazard: true), B(Chest, "chest", 2.5f, interact: true),
        B(Ladder, "ladder", 0.4f, solid: false, climb: true, transparent: true),
        B(Ice, "ice", 0.5f), B(Cactus, "cactus", 0.4f, h: 0.9375, hazard: true), B(Fence, "fence", 2f, h: 1.5),
        B(Vine, "vine", 0.2f, solid: false, climb: true, transparent: true),
    }.ToDictionary(b => b.Id);

    public static BlockInfo Get(int id) => Table.TryGetValue(id, out var b)
        ? b
        : new BlockInfo(id, $"unknown_{id}", "?", 1f, true, 1.0, false, false, false, false, ToolType.None, -1, false);
}

public static class TestWorld
{
    /// <summary>A world with chunks -2..1 (blocks -32..31) loaded as air.</summary>
    public static WorldState Create(int minChunk = -2, int maxChunk = 1)
    {
        var w = new WorldState(TestBlocks.Get);
        int size = (maxChunk - minChunk + 1) * 16;
        var empty = new byte[size * 128 * size];
        w.ApplyChunkRegion(minChunk * 16, 0, minChunk * 16, size, 128, size, empty, null, null, null);
        return w;
    }

    public static void Fill(this WorldState w, int x0, int y0, int z0, int x1, int y1, int z1, int id, int meta = 0)
    {
        for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
        for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
        for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
            w.SetBlock(x, y, z, id, meta);
    }

    public static void Set(this WorldState w, int x, int y, int z, int id, int meta = 0) => w.SetBlock(x, y, z, id, meta);

    /// <summary>Physics state standing on the ground at the given feet position.</summary>
    public static PhysicsState Standing(double x, double y, double z)
    {
        var s = new PhysicsState();
        s.Teleport(new Vec3d(x, y, z), 0, 0);
        s.OnGround = true;
        return s;
    }

    public static void Run(WorldState w, PhysicsState s, MoveInput input, int ticks)
    {
        for (int i = 0; i < ticks; i++) PlayerPhysics.Tick(w, s, input);
    }
}
