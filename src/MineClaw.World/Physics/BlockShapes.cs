using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// Collision and selection (raycast) boxes for b1.8.1 blocks. Blocks whose shape depends on metadata or that are
/// not plain boxes (slabs, stairs, ladders, doors, trapdoors, fence gates, snow layers, cactus, cake, beds...) are
/// special-cased by id; everything else uses <see cref="BlockInfo.Solid"/> + <see cref="BlockInfo.CollisionHeight"/>.
/// </summary>
public static partial class BlockShapes
{
    // b1.8.1 ids used for shape/behaviour special cases.
    public const int Water = 8, StillWater = 9, Lava = 10, StillLava = 11;
    public const int Bed = 26, Web = 30, Slab = 44, Fire = 51, WoodStairs = 53, Chest = 54, RedstoneWire = 55;
    public const int Farmland = 60, WoodDoor = 64, Ladder = 65, Rail = 66, CobbleStairs = 67, IronDoor = 71;
    public const int SnowLayer = 78, Ice = 79, Cactus = 81, Fence = 85, SoulSand = 88, Cake = 92;
    public const int RepeaterOff = 93, RepeaterOn = 94, Trapdoor = 96, IronBars = 101, GlassPane = 102;
    public const int Vine = 106, FenceGate = 107, BrickStairs = 108, StoneBrickStairs = 109;
    public const int Glass = 20, StickyPiston = 29, Piston = 33, PistonHead = 34, PistonMoving = 36, Portal = 90;

    public static bool IsWater(int id) => id is Water or StillWater;
    public static bool IsLava(int id) => id is Lava or StillLava;
    public static bool IsLiquid(int id) => id is >= Water and <= StillLava;
    public static bool IsStairs(int id) => id is WoodStairs or CobbleStairs or BrickStairs or StoneBrickStairs;
    /// <summary>EntityLiving.p() (isOnLadder) in b1.8 tests <c>== Block.LADDER.id</c> only: vines are NOT climbable
    /// (that came in 1.2). mc-dev EntityLiving.java:620-625.</summary>
    public static bool IsClimbable(IBlockAccess w, int id) => id == Ladder;

    /// <summary>Block.a() (isOpaqueCube) per mc-dev: false for every block class that overrides it to false (plants,
    /// liquids, glass, ice, slabs, stairs, chests, pistons, fences, doors, panes, ...). Leaves are opaque on the server
    /// (BlockLeaves.a() = !fancy, fancy = false). Glass panes / iron bars connect to opaque cubes.</summary>
    public static bool IsOpaqueCube(int id) => !NotOpaqueCube.Contains(id);

    private static readonly IdSet NotOpaqueCube = IdSet.Of(
        [0, 6, Glass, Bed, 27, 28, StickyPiston, Web, 31, 32, Piston, PistonHead, PistonMoving, Slab, 50, Fire, 52, WoodStairs, Chest,
         RedstoneWire, 59, Farmland, 63, WoodDoor, Ladder, Rail, CobbleStairs, 68, 69, 70, IronDoor, 72, 75, 76, 77, SnowLayer, Ice, Cactus,
         83, Fence, Portal, Cake, RepeaterOff, RepeaterOn, Trapdoor, IronBars, GlassPane, 104, 105, Vine, FenceGate, BrickStairs,
         StoneBrickStairs],
        (8, 11), (37, 40));

    /// <summary>Material.isSolid() (MCP blocksMovement) of the block's material: false for air, liquids, plants,
    /// "orientable" (torch, lever, button, ladder, rails, redstone, repeater), snow layer, fire, portal and web; true for
    /// everything else, signs, pressure plates and doors included. mc-dev Material.java, MaterialLogic / MaterialTransparent
    /// / MaterialLiquid / MaterialPortal / MaterialWeb. The water flow vector tests this, not the collision box.</summary>
    public static bool MaterialBlocksMovement(int id) => !NonBlockingMaterial.Contains(id);

    private static readonly IdSet NonBlockingMaterial = IdSet.Of(
        [0, 6, 27, 28, Web, 31, 32, 50, Fire, RedstoneWire, 59, Ladder, Rail, 69, 75, 76, 77, SnowLayer, 83, Portal, RepeaterOff, RepeaterOn,
         104, 105, Vine],
        (8, 11), (37, 40));

    /// <summary>Material.isBuildable() (MCP isSolid): like <see cref="MaterialBlocksMovement"/> except that web is buildable.</summary>
    public static bool MaterialIsBuildable(int id) => id == Web || MaterialBlocksMovement(id);

    /// <summary>Block slipperiness (Block.slipperiness): 0.6 default, 0.98 for ice.</summary>
    public static double Slipperiness(int id) => id == Ice ? 0.98 : 0.6;

    /// <summary>Appends the world-space collision boxes of block (x,y,z) to <paramref name="output"/> (the world's version:
    /// <see cref="IBlockAccess.Physics"/>).</summary>
    public static void AddCollisionBoxes(IBlockAccess w, int x, int y, int z, List<AABB> output)
    {
        int id = w.GetBlock(x, y, z);
        if (id == 0) return;
        int meta = w.GetMeta(x, y, z);
        AddCollisionBoxes(w, id, meta, x, y, z, output);
    }

    /// <summary>The collision boxes of block state (id, meta) at x,y,z under the world's version rules.</summary>
    public static void AddCollisionBoxes(IBlockAccess w, int id, int meta, int x, int y, int z, List<AABB> output)
    {
        var rules = w.Physics;
        if (rules is Versioning.Beta181PhysicsRules) AddCollisionBoxesBeta(w, id, meta, x, y, z, output);
        else rules.AddCollisionBoxes(w, id, meta, x, y, z, output);
    }

    /// <summary>Whether the cell at x,y,z is climbable under the world's version rules (b1.8.1 ladders; 1.12.2 also vines and
    /// an open trapdoor over a ladder).</summary>
    public static bool IsClimbableAt(IBlockAccess w, int x, int y, int z) => w.Physics.IsClimbable(w, x, y, z);

    /// <summary>True when the block has any collision box.</summary>
    public static bool HasCollision(IBlockAccess w, int x, int y, int z)
    {
        int id = w.GetBlock(x, y, z);
        if (id == 0) return false;
        var list = ListCache.Value!;
        list.Clear();
        AddCollisionBoxes(w, id, w.GetMeta(x, y, z), x, y, z, list);
        return list.Count > 0;
    }

    /// <summary>Top of the block's collision boxes relative to y (0 when no collision; 1.5 for fences).</summary>
    public static double CollisionTop(IBlockAccess w, int x, int y, int z)
    {
        int id = w.GetBlock(x, y, z);
        if (id == 0) return 0;
        var list = ListCache.Value!;
        list.Clear();
        AddCollisionBoxes(w, id, w.GetMeta(x, y, z), x, y, z, list);
        double top = 0;
        foreach (var b in list) top = Math.Max(top, b.MaxY - y);
        return top;
    }

    /// <summary>Whether the block's collision is a full 1x1x1 cube.</summary>
    public static bool IsFullCube(IBlockAccess w, int x, int y, int z)
    {
        int id = w.GetBlock(x, y, z);
        if (id == 0) return false;
        var list = ListCache.Value!;
        list.Clear();
        AddCollisionBoxes(w, id, w.GetMeta(x, y, z), x, y, z, list);
        return list.Count == 1 && list[0] == AABB.Block(x, y, z);
    }

    /// <summary>All collision boxes of blocks intersecting <paramref name="area"/> (World.getCollidingBoundingBoxes):
    /// scans y from minY-1 so fences reach into the area.</summary>
    public static List<AABB> GetCollidingBoxes(IBlockAccess w, AABB area, List<AABB>? output = null)
    {
        output ??= new List<AABB>();
        var rules = w.Physics;
        if (rules is not Versioning.Beta181PhysicsRules) return rules.GetCollidingBoxes(w, area, output);
        return GetCollidingBoxesBeta(w, area, output);
    }

    internal static List<AABB> GetCollidingBoxesBeta(IBlockAccess w, AABB area, List<AABB> output)
    {
        output.Clear();
        int x0 = (int)Math.Floor(area.MinX), x1 = (int)Math.Floor(area.MaxX + 1);
        int y0 = (int)Math.Floor(area.MinY) - 1, y1 = (int)Math.Floor(area.MaxY + 1);
        int z0 = (int)Math.Floor(area.MinZ), z1 = (int)Math.Floor(area.MaxZ + 1);
        var tmp = ListCache.Value!;
        for (int x = x0; x < x1; x++)
        for (int z = z0; z < z1; z++)
        {
            for (int y = y0; y < y1; y++)
            {
                int id = w.GetBlock(x, y, z);
                if (id == 0) continue;
                tmp.Clear();
                AddCollisionBoxesBeta(w, id, w.GetMeta(x, y, z), x, y, z, tmp);
                foreach (var b in tmp)
                    if (b.Intersects(area)) output.Add(b);
            }
        }
        return output;
    }

    private static readonly ThreadLocal<List<AABB>> ListCache = new(() => new List<AABB>(4));
}
