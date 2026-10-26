using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// Collision and selection (raycast) boxes for b1.8.1 blocks. Blocks whose shape depends on metadata or that are
/// not plain boxes (slabs, stairs, ladders, doors, trapdoors, fence gates, snow layers, cactus, cake, beds...) are
/// special-cased by id; everything else uses <see cref="BlockInfo.Solid"/> + <see cref="BlockInfo.CollisionHeight"/>.
/// </summary>
public static class BlockShapes
{
    // b1.8.1 ids used for shape/behaviour special cases.
    public const int Water = 8, StillWater = 9, Lava = 10, StillLava = 11;
    public const int Bed = 26, Web = 30, Slab = 44, Fire = 51, WoodStairs = 53, Chest = 54, RedstoneWire = 55;
    public const int Farmland = 60, WoodDoor = 64, Ladder = 65, Rail = 66, CobbleStairs = 67, IronDoor = 71;
    public const int SnowLayer = 78, Ice = 79, Cactus = 81, Fence = 85, SoulSand = 88, Cake = 92;
    public const int RepeaterOff = 93, RepeaterOn = 94, Trapdoor = 96, IronBars = 101, GlassPane = 102;
    public const int Vine = 106, FenceGate = 107, BrickStairs = 108, StoneBrickStairs = 109;

    public static bool IsWater(int id) => id is Water or StillWater;
    public static bool IsLava(int id) => id is Lava or StillLava;
    public static bool IsLiquid(int id) => id is >= Water and <= StillLava;
    public static bool IsStairs(int id) => id is WoodStairs or CobbleStairs or BrickStairs or StoneBrickStairs;
    public static bool IsClimbable(IBlockAccess w, int id) => id is Ladder or Vine || (id != 0 && w.Info(id).Climbable);

    /// <summary>Block slipperiness (Block.slipperiness): 0.6 default, 0.98 for ice.</summary>
    public static double Slipperiness(int id) => id == Ice ? 0.98 : 0.6;

    /// <summary>Appends the world-space collision boxes of block (x,y,z) to <paramref name="output"/>.</summary>
    public static void AddCollisionBoxes(IBlockAccess w, int x, int y, int z, List<AABB> output)
    {
        int id = w.GetBlock(x, y, z);
        if (id == 0) return;
        int meta = w.GetMeta(x, y, z);
        AddCollisionBoxes(w, id, meta, x, y, z, output);
    }

    public static void AddCollisionBoxes(IBlockAccess w, int id, int meta, int x, int y, int z, List<AABB> output)
    {
        void Add(double x0, double y0, double z0, double x1, double y1, double z1) =>
            output.Add(new AABB(x + x0, y + y0, z + z0, x + x1, y + y1, z + z1));

        switch (id)
        {
            case 0:
            case Water: case StillWater: case Lava: case StillLava:
            case Web: case Fire: case RedstoneWire: case Rail: case 27: case 28:
            case Vine:
            case 6: case 31: case 32: case 37: case 38: case 39: case 40: case 50: case 59:
            case 63: case 68: case 69: case 70: case 72: case 75: case 76: case 77: case 83:
            case 90: case 104: case 105:
                return;
            case Slab:
                Add(0, 0, 0, 1, 0.5, 1); return;
            case Bed:
                Add(0, 0, 0, 1, 0.5625, 1); return;
            case SoulSand:
                Add(0, 0, 0, 1, 0.875, 1); return;
            case Fence:
                Add(0, 0, 0, 1, 1.5, 1); return;
            case FenceGate:
                if ((meta & 4) == 0) Add(0, 0, 0, 1, 1.5, 1);
                return;
            case Cactus:
            {
                const double f = 0.0625;
                Add(f, 0, f, 1 - f, 1 - f, 1 - f); return;
            }
            case Cake:
            {
                const double f = 0.0625;
                double f1 = (1 + meta * 2) / 16.0;
                Add(f1, 0, f, 1 - f, 0.5 - f, 1 - f); return;
            }
            case SnowLayer:
                if ((meta & 7) >= 3) Add(0, 0, 0, 1, 0.5, 1);
                return;
            case RepeaterOff: case RepeaterOn:
                Add(0, 0, 0, 1, 0.125, 1); return;
            case Ladder:
                AddLadderBox(meta, Add); return;
            case WoodDoor: case IronDoor:
                AddDoorBox(meta, Add); return;
            case Trapdoor:
            {
                const double f = 0.1875;
                if ((meta & 4) == 0) { Add(0, 0, 0, 1, f, 1); return; }
                switch (meta & 3)
                {
                    case 0: Add(0, 0, 1 - f, 1, 1, 1); break;
                    case 1: Add(0, 0, 0, 1, 1, f); break;
                    case 2: Add(1 - f, 0, 0, 1, 1, 1); break;
                    default: Add(0, 0, 0, f, 1, 1); break;
                }
                return;
            }
            case WoodStairs: case CobbleStairs: case BrickStairs: case StoneBrickStairs:
                switch (meta & 3)
                {
                    case 0: Add(0, 0, 0, 0.5, 0.5, 1); Add(0.5, 0, 0, 1, 1, 1); break;
                    case 1: Add(0, 0, 0, 0.5, 1, 1); Add(0.5, 0, 0, 1, 0.5, 1); break;
                    case 2: Add(0, 0, 0, 1, 0.5, 0.5); Add(0, 0, 0.5, 1, 1, 1); break;
                    default: Add(0, 0, 0, 1, 1, 0.5); Add(0, 0, 0.5, 1, 0.5, 1); break;
                }
                return;
        }

        var info = w.Info(id);
        if (!info.Solid || info.Liquid) return;
        double h = info.CollisionHeight > 0 ? info.CollisionHeight : 1.0;
        Add(0, 0, 0, 1, h, 1);
    }

    private static void AddLadderBox(int meta, Action<double, double, double, double, double, double> add)
    {
        const double f = 0.125;
        switch (meta)
        {
            case 2: add(0, 0, 1 - f, 1, 1, 1); break;
            case 3: add(0, 0, 0, 1, 1, f); break;
            case 4: add(1 - f, 0, 0, 1, 1, 1); break;
            case 5: add(0, 0, 0, f, 1, 1); break;
            default: add(0, 0, 0, 1, 1, f); break;
        }
    }

    private static void AddDoorBox(int meta, Action<double, double, double, double, double, double> add)
    {
        const double f = 0.1875;
        int state = (meta & 4) == 0 ? (meta - 1) & 3 : meta & 3;
        switch (state)
        {
            case 0: add(0, 0, 0, 1, 1, f); break;
            case 1: add(1 - f, 0, 0, 1, 1, 1); break;
            case 2: add(0, 0, 1 - f, 1, 1, 1); break;
            default: add(0, 0, 0, f, 1, 1); break;
        }
    }

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
                AddCollisionBoxes(w, id, w.GetMeta(x, y, z), x, y, z, tmp);
                foreach (var b in tmp)
                    if (b.Intersects(area)) output.Add(b);
            }
        }
        return output;
    }

    /// <summary>Raycast target boxes. Air, liquids and fire are not targetable.</summary>
    public static void AddSelectionBoxes(IBlockAccess w, int x, int y, int z, List<AABB> output)
    {
        int id = w.GetBlock(x, y, z);
        if (id == 0 || IsLiquid(id) || id == Fire) return;
        int meta = w.GetMeta(x, y, z);
        void Add(double x0, double y0, double z0, double x1, double y1, double z1) =>
            output.Add(new AABB(x + x0, y + y0, z + z0, x + x1, y + y1, z + z1));
        switch (id)
        {
            case Fence: case FenceGate: Add(0, 0, 0, 1, 1, 1); return;
            case 6: case 31: case 32: case 37: case 38: case 39: case 40: case 59: case 83: case 104: case 105:
                Add(0.1, 0, 0.1, 0.9, 0.8, 0.9); return;
            case 50: case 75: case 76:
                if (meta == 5 || meta == 0) Add(0.4, 0, 0.4, 0.6, 0.6, 0.6);
                else Add(0.35, 0.2, 0.35, 0.65, 0.8, 0.65);
                return;
            case RedstoneWire: Add(0, 0, 0, 1, 0.0625, 1); return;
            case Rail: case 27: case 28: Add(0, 0, 0, 1, 0.125, 1); return;
            case 70: case 72: Add(0.0625, 0, 0.0625, 0.9375, 0.0625, 0.9375); return;
            case SnowLayer: Add(0, 0, 0, 1, (2 * (1 + (meta & 7))) / 16.0, 1); return;
            case Vine: case 63: case 68: case 69: case 77: case Web: case 90:
                Add(0, 0, 0, 1, 1, 1); return;
        }
        int before = output.Count;
        AddCollisionBoxes(w, id, meta, x, y, z, output);
        if (output.Count == before) Add(0, 0, 0, 1, 1, 1);
    }

    private static readonly ThreadLocal<List<AABB>> ListCache = new(() => new List<AABB>(4));
}
