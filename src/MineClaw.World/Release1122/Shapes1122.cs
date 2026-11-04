namespace MineClaw.World.Release1122;

/// <summary>
/// 1.12.2 collision boxes per block state (multi-version P3). State-only shapes come from the minecraft-data table
/// (<c>Shapes1122.Data.cs</c>, generated from <c>blockCollisionShapes.json</c>; Shapes1122Tests cross-checks it with the
/// decompiled source's AABB constants). Shapes that depend on neighbours (<c>getActualState</c>) are ported from the source:
/// <list type="bullet">
/// <item>fences (<c>BlockFence.addCollisionBoxToList</c>: a 1.5-high pillar plus an arm per connection, <c>canConnectTo</c>),</item>
/// <item>walls (<c>BlockWall.CLIP_AABB_BY_INDEX</c>, 1.5 high, the thin 0.3125..0.6875 straight run),</item>
/// <item>panes and iron bars (<c>BlockPane</c>: post + arms, <c>attachesTo</c>),</item>
/// <item>stairs (<c>BlockStairs.getStairsShape</c>: straight, inner and outer corners; <c>getCollisionBoxList</c>),</item>
/// <item>doors (<c>BlockDoor.getActualState</c>: the upper half takes facing/open from the lower, the lower takes the hinge
/// from the upper),</item>
/// <item>chests (<c>BlockChest.getBoundingBox</c>: a double chest reaches its neighbour's edge),</item>
/// <item>chorus plants (<c>BlockChorusPlant</c>: centre + arms).</item>
/// </list>
/// </summary>
public static partial class Shapes1122
{
    // 1.12.2 block ids (minecraft-data pc/1.12.2 blocks.json).
    public const int Air = 0, Water = 8, StillWater = 9, Lava = 10, StillLava = 11, Leaves = 18, Glass = 20, Bed = 26;
    public const int StickyPiston = 29, Web = 30, Piston = 33, PistonHead = 34, Chest = 54, WoodDoor = 64, Ladder = 65;
    public const int IronDoor = 71, Ice = 79, Fence = 85, SoulSand = 88, Pumpkin = 86, Glowstone = 89, LitPumpkin = 91;
    public const int StainedGlass = 95, Trapdoor = 96, IronBars = 101, GlassPane = 102, Melon = 103, Vine = 106, FenceGate = 107;
    public const int NetherBrickFence = 113, Cauldron = 118, EndStone = 121, Beacon = 138, CobbleWall = 139, TrappedChest = 146;
    public const int StainedGlassPane = 160, Leaves2 = 161, Slime = 165, Barrier = 166, IronTrapdoor = 167, SeaLantern = 169;
    public const int HayBale = 170, PackedIce = 174, ChorusPlant = 199, ChorusFlower = 200, FrostedIce = 212;
    public const int ShulkerFirst = 219, ShulkerLast = 234;

    public static bool IsStairs(int id) => Stairs.Contains(id);

    private static readonly IdSet Stairs = IdSet.Of([53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203]);
    public static bool IsWoodFence(int id) => id is Fence or (>= 188 and <= 192);
    public static bool IsFence(int id) => IsWoodFence(id) || id == NetherBrickFence;
    public static bool IsFenceGate(int id) => id is FenceGate or (>= 183 and <= 187);
    public static bool IsPane(int id) => id is IronBars or GlassPane or StainedGlassPane;
    public static bool IsDoor(int id) => id is WoodDoor or IronDoor or (>= 193 and <= 197);
    public static bool IsChest(int id) => id is Chest or TrappedChest;
    public static bool IsTrapdoor(int id) => id is Trapdoor or IronTrapdoor;
    public static bool IsWall(int id) => id == CobbleWall;
    public static bool IsLiquid(int id) => id is >= Water and <= StillLava;
    public static bool IsWater(int id) => id is Water or StillWater;
    public static bool IsLava(int id) => id is Lava or StillLava;

    /// <summary><c>Block.slipperiness</c>: 0.6, ice / packed ice / frosted ice 0.98 (<c>BlockIce</c>, <c>BlockPackedIce</c>; frosted
    /// ice extends BlockIce), slime 0.8 (<c>BlockSlime</c>).</summary>
    public static float Slipperiness(int id) => id switch
    {
        Ice or PackedIce or FrostedIce => 0.98f,
        Slime => 0.8f,
        _ => 0.6f,
    };

    /// <summary>Appends the world-space collision boxes of block state (id, meta) at x,y,z (no intersection filter).</summary>
    public static void AddCollisionBoxes(IBlockAccess w, int id, int meta, int x, int y, int z, List<AABB> output)
    {
        if (id <= 0 || id > 255) return;
        var box = new BoxSink(output, x, y, z);
        if (IsFence(id)) AddFenceBoxes(w, id, x, y, z, box);
        else if (IsWall(id)) AddWallBoxes(w, x, y, z, box);
        else if (IsPane(id)) AddPaneBoxes(w, x, y, z, box);
        else if (IsStairs(id)) AddStairBoxes(w, meta, x, y, z, box);
        else if (IsDoor(id)) AddDoorBoxes(w, id, meta, x, y, z, box);
        else if (IsChest(id)) AddChestBoxes(w, id, x, y, z, box);
        else if (id == ChorusPlant) AddChorusBoxes(w, x, y, z, box);
        else AddTableBoxes(id, meta, box);
    }

    /// <summary><c>BlockFence.addCollisionBoxToList</c>: the 1.5-high pillar plus an arm per connection.</summary>
    private static void AddFenceBoxes(IBlockAccess w, int id, int x, int y, int z, BoxSink box)
    {
        box.Add(0.375, 0, 0.375, 0.625, 1.5, 0.625);                                  // PILLAR_AABB
        if (FenceConnects(w, id, x, y, z - 1, Face.South)) box.Add(0.375, 0, 0, 0.625, 1.5, 0.375);   // NORTH_AABB
        if (FenceConnects(w, id, x + 1, y, z, Face.West)) box.Add(0.625, 0, 0.375, 1, 1.5, 0.625);   // EAST_AABB
        if (FenceConnects(w, id, x, y, z + 1, Face.North)) box.Add(0.375, 0, 0.625, 0.625, 1.5, 1);   // SOUTH_AABB
        if (FenceConnects(w, id, x - 1, y, z, Face.East)) box.Add(0, 0, 0.375, 0.375, 1.5, 0.625);   // WEST_AABB
    }

    private static void AddWallBoxes(IBlockAccess w, int x, int y, int z, BoxSink box)
    {
        int i = 0;                                                                    // getAABBIndex: S 1, W 2, N 4, E 8
        if (WallConnects(w, x, y, z + 1, Face.North)) i |= 1;
        if (WallConnects(w, x - 1, y, z, Face.East)) i |= 2;
        if (WallConnects(w, x, y, z - 1, Face.South)) i |= 4;
        if (WallConnects(w, x + 1, y, z, Face.West)) i |= 8;
        var b = WallBoxes[i];
        box.Add(b[0], 0, b[1], b[2], 1.5, b[3]);                                      // CLIP_AABB_BY_INDEX: setMaxY(1.5)
    }

    private static void AddPaneBoxes(IBlockAccess w, int x, int y, int z, BoxSink box)
    {
        box.Add(0.4375, 0, 0.4375, 0.5625, 1, 0.5625);                                // AABB_BY_INDEX[0]
        if (PaneConnects(w, x, y, z - 1, Face.South)) box.Add(0.4375, 0, 0, 0.5625, 1, 0.5625);
        if (PaneConnects(w, x, y, z + 1, Face.North)) box.Add(0.4375, 0, 0.4375, 0.5625, 1, 1);
        if (PaneConnects(w, x + 1, y, z, Face.West)) box.Add(0.4375, 0, 0.4375, 1, 1, 0.5625);
        if (PaneConnects(w, x - 1, y, z, Face.East)) box.Add(0, 0, 0.4375, 0.5625, 1, 0.5625);
    }

    /// <summary><c>BlockDoor.getBoundingBox</c>: closed → the facing's box; open → swung to the hinge side (a right hinge turns it
    /// counter-clockwise, a left one clockwise).</summary>
    private static void AddDoorBoxes(IBlockAccess w, int id, int meta, int x, int y, int z, BoxSink box)
    {
        var (facing, open, rightHinge) = DoorState(w, id, meta, x, y, z);
        const double t = 0.1875;
        switch (!open ? facing : rightHinge ? Ccw(facing) : Cw(facing))
        {
            case Face.South: box.Add(0, 0, 0, 1, 1, t); break;           // SOUTH_AABB
            case Face.North: box.Add(0, 0, 1 - t, 1, 1, 1); break;       // NORTH_AABB
            case Face.West: box.Add(1 - t, 0, 0, 1, 1, 1); break;        // WEST_AABB
            default: box.Add(0, 0, 0, t, 1, 1); break;                   // EAST_AABB
        }
    }

    /// <summary><c>BlockChest.getBoundingBox</c>: a double chest reaches its neighbour's edge.</summary>
    private static void AddChestBoxes(IBlockAccess w, int id, int x, int y, int z, BoxSink box)
    {
        if (w.GetBlock(x, y, z - 1) == id) box.Add(0.0625, 0, 0, 0.9375, 0.875, 0.9375);
        else if (w.GetBlock(x, y, z + 1) == id) box.Add(0.0625, 0, 0.0625, 0.9375, 0.875, 1);
        else if (w.GetBlock(x - 1, y, z) == id) box.Add(0, 0, 0.0625, 0.9375, 0.875, 0.9375);
        else if (w.GetBlock(x + 1, y, z) == id) box.Add(0.0625, 0, 0.0625, 1, 0.875, 0.9375);
        else box.Add(0.0625, 0, 0.0625, 0.9375, 0.875, 0.9375);
    }

    /// <summary><c>BlockChorusPlant</c>: the centre plus an arm toward each plant or flower (and end stone below).</summary>
    private static void AddChorusBoxes(IBlockAccess w, int x, int y, int z, BoxSink box)
    {
        static bool Plant(int b) => b is ChorusPlant or ChorusFlower;
        box.Add(0.1875, 0.1875, 0.1875, 0.8125, 0.8125, 0.8125);
        if (Plant(w.GetBlock(x - 1, y, z))) box.Add(0, 0.1875, 0.1875, 0.1875, 0.8125, 0.8125);
        if (Plant(w.GetBlock(x + 1, y, z))) box.Add(0.8125, 0.1875, 0.1875, 1, 0.8125, 0.8125);
        if (Plant(w.GetBlock(x, y + 1, z))) box.Add(0.1875, 0.8125, 0.1875, 0.8125, 1, 0.8125);
        int below = w.GetBlock(x, y - 1, z);
        if (Plant(below) || below == EndStone) box.Add(0.1875, 0, 0.1875, 0.8125, 0.1875, 0.8125);
        if (Plant(w.GetBlock(x, y, z - 1))) box.Add(0.1875, 0.1875, 0, 0.8125, 0.8125, 0.1875);
        if (Plant(w.GetBlock(x, y, z + 1))) box.Add(0.1875, 0.1875, 0.8125, 0.8125, 0.8125, 1);
    }

    /// <summary>The state-only shape (minecraft-data's table).</summary>
    private static void AddTableBoxes(int id, int meta, BoxSink box) => box.AddAll(ShapeBoxes[StateShape[id << 4 | (meta & 15)]]);

    /// <summary>The state-only table entry (no neighbour rules): for the cross-check tests.</summary>
    public static double[] TableBoxes(int id, int meta) => ShapeBoxes[StateShape[id << 4 | (meta & 15)]];

    /// <summary><c>World.getCollisionBoxes(entity, aabb)</c>: the boxes of loaded blocks intersecting <paramref name="area"/>,
    /// scanning one block beyond it on every side (floor(min) − 1 .. ceil(max) + 1) except the four vertical edge columns, and the
    /// top layer only inside them. Entity boxes (boats, shulkers) and the world border are not modelled.</summary>
    public static List<AABB> GetCollidingBoxes(IBlockAccess w, AABB area, List<AABB> output)
    {
        output.Clear();
        int i = (int)Math.Floor(area.MinX) - 1, j = (int)Math.Ceiling(area.MaxX) + 1;
        int k = (int)Math.Floor(area.MinY) - 1, l = (int)Math.Ceiling(area.MaxY) + 1;
        int m = (int)Math.Floor(area.MinZ) - 1, n = (int)Math.Ceiling(area.MaxZ) + 1;
        var tmp = Tmp ??= new List<AABB>(8);
        for (int o = i; o < j; o++)
        for (int p = m; p < n; p++)
        {
            bool edgeX = o == i || o == j - 1, edgeZ = p == m || p == n - 1;
            if (edgeX && edgeZ) continue;
            if (!w.IsLoaded(o, p)) continue;
            for (int q = k; q < l; q++)
            {
                if ((edgeX || edgeZ) && q == l - 1) continue;
                int id = w.GetBlock(o, q, p);
                if (id == 0) continue;
                tmp.Clear();
                AddCollisionBoxes(w, id, w.GetMeta(o, q, p), o, q, p, tmp);
                foreach (var b in tmp)
                    if (b.Intersects(area)) output.Add(b);
            }
        }
        return output;
    }

    [ThreadStatic] private static List<AABB>? Tmp;

    // ---- connections (getActualState) ----

    /// <summary>Horizontal faces, named as <c>EnumFacing</c>.</summary>
    public enum Face { North, South, West, East }

    /// <summary><c>BlockFence.canConnectTo</c>: the neighbour's face is SOLID (and the block isn't one of the attach exceptions),
    /// or a MIDDLE_POLE of the same material (wood fences with wood fences, nether brick with nether brick) or of a fence gate.</summary>
    private static bool FenceConnects(IBlockAccess w, int self, int x, int y, int z, Face face)
    {
        int id = w.GetBlock(x, y, z);
        if (IsFence(id)) return IsWoodFence(id) == IsWoodFence(self);                 // MIDDLE_POLE, material WOOD vs ROCK
        if (IsFenceGate(id)) return GatePole(w.GetMeta(x, y, z), face);
        return !FenceException(id) && SideSolid(w, id, x, y, z, face);
    }

    /// <summary><c>BlockWall.canConnectTo</c>: MIDDLE_POLE_THICK (walls), a fence gate's MIDDLE_POLE, or a SOLID face.</summary>
    private static bool WallConnects(IBlockAccess w, int x, int y, int z, Face face)
    {
        int id = w.GetBlock(x, y, z);
        if (IsWall(id)) return true;
        if (IsFenceGate(id)) return GatePole(w.GetMeta(x, y, z), face);
        return !FenceException(id) && SideSolid(w, id, x, y, z, face);
    }

    /// <summary><c>BlockPane.attachesTo</c>: MIDDLE_POLE_THIN (panes, bars) or a SOLID face outside the pane's exception list
    /// (which, unlike the fence's, lets glass and trapdoors through).</summary>
    private static bool PaneConnects(IBlockAccess w, int x, int y, int z, Face face)
    {
        int id = w.GetBlock(x, y, z);
        if (IsPane(id)) return true;
        return !PaneAttachException.Contains(id) && SideSolid(w, id, x, y, z, face);
    }

    /// <summary><c>BlockFenceGate.getBlockFaceShape</c>: a side face is a MIDDLE_POLE when the gate's facing axis equals the
    /// face's rotateY axis (a gate facing south spans X, so its west and east faces are poles). Facing = byHorizontalIndex(meta &amp; 3).</summary>
    private static bool GatePole(int meta, Face face)
    {
        bool gateAxisZ = (meta & 3) is 0 or 2;                                         // S 0, W 1, N 2, E 3
        bool rotatedAxisZ = face is Face.East or Face.West;                            // rotateY: N→E, E→S, S→W, W→N
        return gateAxisZ == rotatedAxisZ;
    }

    /// <summary><c>BlockFence.isExcepBlockForAttachWithPiston</c> = <c>Block.isExceptBlockForAttachWithPiston</c> + barrier, melon,
    /// pumpkins (the wall's list is the same).</summary>
    private static bool FenceException(int id) => FenceAttachException.Contains(id);

    /// <summary><c>Block.isExceptBlockForAttachWithPiston</c>: shulker boxes, leaves, trapdoors, beacon, cauldron, glass, glowstone,
    /// ice, sea lantern, stained glass, pistons.</summary>
    internal static readonly IdSet AttachException = IdSet.Of(
        [Leaves, Leaves2, Trapdoor, IronTrapdoor, Beacon, Cauldron, Glass, Glowstone, Ice, SeaLantern, StainedGlass, Piston, StickyPiston,
         PistonHead],
        (ShulkerFirst, ShulkerLast));

    private static readonly IdSet FenceAttachException = IdSet.Of(
        [Leaves, Leaves2, Trapdoor, IronTrapdoor, Beacon, Cauldron, Glass, Glowstone, Ice, SeaLantern, StainedGlass, Piston, StickyPiston,
         PistonHead, Barrier, Melon, Pumpkin, LitPumpkin],
        (ShulkerFirst, ShulkerLast));

    /// <summary><c>BlockPane.attachesTo</c>'s exceptions: unlike the fence's, glass and trapdoors are not among them.</summary>
    private static readonly IdSet PaneAttachException = IdSet.Of(
        [Leaves, Leaves2, Beacon, Cauldron, Glowstone, Ice, SeaLantern, Piston, StickyPiston, PistonHead, Melon, Pumpkin, LitPumpkin, Barrier],
        (ShulkerFirst, ShulkerLast));

    /// <summary><c>getBlockFaceShape(face) == SOLID</c> for a horizontal face: <c>Block</c>'s default is SOLID; the classes that
    /// override it return UNDEFINED (or a pole) on their sides, except double slabs (SOLID), cauldron sides (SOLID, but an attach
    /// exception), piston bases (exceptions) and stairs (the back faces of straight and inner shapes).</summary>
    public static bool SideSolid(IBlockAccess w, int id, int x, int y, int z, Face face)
    {
        if (IsStairs(id))
        {
            int meta = w.GetMeta(x, y, z);
            var facing = StairFacing(meta);
            var shape = StairShape(w, meta, x, y, z);
            return shape switch
            {
                StairsShape.Straight => facing == face,
                StairsShape.InnerRight => facing == face || facing == Ccw(face),
                StairsShape.InnerLeft => facing == face || facing == Cw(face),
                _ => false,
            };
        }
        if (id == Cauldron) return true;
        return !SideUndefined(id);
    }

    /// <summary>Blocks whose <c>getBlockFaceShape</c> is never SOLID on a horizontal side (air, liquids, plants, rails, redstone,
    /// torches, signs, banners, doors, trapdoors, ladders, vines, beds, chests, cake, cactus, carpets, snow layers, farmland, grass
    /// path, slabs (non-double), fences, gates, walls, panes, …; the overriding classes listed in the source).</summary>
    private static bool SideUndefined(int id) => SideNeverSolid.Contains(id);

    private static readonly IdSet SideNeverSolid = IdSet.Of(
        [0, 6, 26, 27, 28, 30, 31, 32, 34, 36, 44, 50, 51, 54, 55, 59, 60, 63, 64, 65, 66, 68, 69, 70, 71, 72, 75, 76, 77, 78, 81, 83, 85,
         90, 92, 93, 94, 96, 101, 102, 104, 105, 106, 107, 111, 113, 115, 116, 117, 119, 120, 122, 126, 127, 130, 131, 132, 139, 140, 141,
         142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 154, 157, 160, 167, 171, 175, 176, 177, 178, 182, 205, 207, 208, 209, 217],
        (8, 11), (37, 40), (183, 200));

    private static readonly double[][] WallBoxes =                                    // BlockWall.AABB_BY_INDEX: minX, minZ, maxX, maxZ
    [
        [0.25, 0.25, 0.75, 0.75], [0.25, 0.25, 0.75, 1], [0, 0.25, 0.75, 0.75], [0, 0.25, 0.75, 1],
        [0.25, 0, 0.75, 0.75], [0.3125, 0, 0.6875, 1], [0, 0, 0.75, 0.75], [0, 0, 0.75, 1],
        [0.25, 0.25, 1, 0.75], [0.25, 0.25, 1, 1], [0, 0.3125, 1, 0.6875], [0, 0.25, 1, 1],
        [0.25, 0, 1, 0.75], [0.25, 0, 1, 1], [0, 0, 1, 0.75], [0, 0, 1, 1],
    ];

    // ---- stairs ----

    public enum StairsShape { Straight, InnerLeft, InnerRight, OuterLeft, OuterRight }

    /// <summary><c>BlockStairs.getStateFromMeta</c>: facing = byIndex(5 − (meta &amp; 3)): 0 east, 1 west, 2 south, 3 north.</summary>
    public static Face StairFacing(int meta) => (meta & 3) switch { 0 => Face.East, 1 => Face.West, 2 => Face.South, _ => Face.North };

    private static bool StairTop(int meta) => (meta & 4) != 0;

    public static Face Cw(Face f) => f switch { Face.North => Face.East, Face.East => Face.South, Face.South => Face.West, _ => Face.North };
    public static Face Ccw(Face f) => f switch { Face.North => Face.West, Face.West => Face.South, Face.South => Face.East, _ => Face.North };
    private static Face Opposite(Face f) => f switch { Face.North => Face.South, Face.South => Face.North, Face.West => Face.East, _ => Face.West };
    private static bool AxisZ(Face f) => f is Face.North or Face.South;
    private static (int Dx, int Dz) Offset(Face f) => f switch { Face.North => (0, -1), Face.South => (0, 1), Face.West => (-1, 0), _ => (1, 0) };

    /// <summary><c>BlockStairs.getStairsShape</c>.</summary>
    public static StairsShape StairShape(IBlockAccess w, int meta, int x, int y, int z)
    {
        var facing = StairFacing(meta);
        bool top = StairTop(meta);
        var (fx, fz) = Offset(facing);
        int front = w.GetBlock(x + fx, y, z + fz);
        if (IsStairs(front))
        {
            int fm = w.GetMeta(x + fx, y, z + fz);
            if (StairTop(fm) == top)
            {
                var f1 = StairFacing(fm);
                if (AxisZ(f1) != AxisZ(facing) && DifferentStairs(w, meta, x, y, z, Opposite(f1)))
                    return f1 == Ccw(facing) ? StairsShape.OuterLeft : StairsShape.OuterRight;
            }
        }
        var (bx, bz) = Offset(Opposite(facing));
        int back = w.GetBlock(x + bx, y, z + bz);
        if (IsStairs(back))
        {
            int bm = w.GetMeta(x + bx, y, z + bz);
            if (StairTop(bm) == top)
            {
                var f2 = StairFacing(bm);
                if (AxisZ(f2) != AxisZ(facing) && DifferentStairs(w, meta, x, y, z, f2))
                    return f2 == Ccw(facing) ? StairsShape.InnerLeft : StairsShape.InnerRight;
            }
        }
        return StairsShape.Straight;
    }

    private static bool DifferentStairs(IBlockAccess w, int meta, int x, int y, int z, Face dir)
    {
        var (dx, dz) = Offset(dir);
        int id = w.GetBlock(x + dx, y, z + dz);
        if (!IsStairs(id)) return true;
        int m = w.GetMeta(x + dx, y, z + dz);
        return StairFacing(m) != StairFacing(meta) || StairTop(m) != StairTop(meta);
    }

    /// <summary><c>BlockStairs.getCollisionBoxList</c>: the half slab, the quarter (straight and inner shapes; note the source's
    /// swapped names: a top stair gets the lower quarter), and the eighth (every non-straight shape).</summary>
    private static void AddStairBoxes(IBlockAccess w, int meta, int x, int y, int z, BoxSink box)
    {
        bool top = StairTop(meta);
        var facing = StairFacing(meta);
        var shape = StairShape(w, meta, x, y, z);
        if (top) box.Add(0, 0.5, 0, 1, 1, 1); else box.Add(0, 0, 0, 1, 0.5, 1);
        double y0 = top ? 0 : 0.5, y1 = top ? 0.5 : 1;
        if (shape is StairsShape.Straight or StairsShape.InnerLeft or StairsShape.InnerRight)
        {
            switch (facing)
            {
                case Face.North: box.Add(0, y0, 0, 1, y1, 0.5); break;
                case Face.South: box.Add(0, y0, 0.5, 1, y1, 1); break;
                case Face.West: box.Add(0, y0, 0, 0.5, y1, 1); break;
                default: box.Add(0.5, y0, 0, 1, y1, 1); break;
            }
        }
        if (shape != StairsShape.Straight)
        {
            Face f1 = shape switch
            {
                StairsShape.OuterRight => Cw(facing),
                StairsShape.InnerRight => Opposite(facing),
                StairsShape.InnerLeft => Ccw(facing),
                _ => facing,                                                          // OUTER_LEFT
            };
            switch (f1)                                                               // N → NW, S → SE, W → SW, E → NE
            {
                case Face.North: box.Add(0, y0, 0, 0.5, y1, 0.5); break;
                case Face.South: box.Add(0.5, y0, 0.5, 1, y1, 1); break;
                case Face.West: box.Add(0, y0, 0.5, 0.5, y1, 1); break;
                default: box.Add(0.5, y0, 0, 1, y1, 0.5); break;
            }
        }
    }

    // ---- doors ----

    /// <summary><c>BlockDoor.getStateFromMeta</c> + <c>getActualState</c>: lower half (meta &lt; 8) facing =
    /// byHorizontalIndex(meta &amp; 3).rotateYCCW() (0 east, 1 south, 2 west, 3 north), open = bit 4; upper half (bit 8) hinge = bit 1.
    /// Each half reads the missing properties from the other; a lone half keeps the defaults (north, closed, left hinge).</summary>
    public static (Face Facing, bool Open, bool RightHinge) DoorState(IBlockAccess w, int id, int meta, int x, int y, int z)
    {
        static Face LowerFacing(int m) => (m & 3) switch { 0 => Face.East, 1 => Face.South, 2 => Face.West, _ => Face.North };
        if ((meta & 8) == 0)
        {
            bool right = false;
            if (w.GetBlock(x, y + 1, z) == id)
            {
                int up = w.GetMeta(x, y + 1, z);
                right = (up & 8) != 0 && (up & 1) != 0;                                // an "upper" without bit 8 is a lower: left hinge
            }
            return (LowerFacing(meta), (meta & 4) != 0, right);
        }
        bool hinge = (meta & 1) != 0;
        if (w.GetBlock(x, y - 1, z) == id)
        {
            int lo = w.GetMeta(x, y - 1, z);
            if ((lo & 8) == 0) return (LowerFacing(lo), (lo & 4) != 0, hinge);
            return (Face.North, false, hinge);                                         // the "lower" is an upper: its facing/open defaults
        }
        return (Face.North, false, hinge);
    }

    // ---- climbing ----

    /// <summary><c>EntityLivingBase.isOnLadder</c>: a ladder or vine in the feet cell, or an open trapdoor above a ladder with the
    /// same facing (<c>canGoThroughtTrapDoorOnLadder</c>; trapdoor facing = meta &amp; 3: 0 north, 1 south, 2 west, 3 east; ladder
    /// facing = meta 2 north, 3 south, 4 west, 5 east).</summary>
    public static bool IsClimbable(IBlockAccess w, int x, int y, int z)
    {
        int id = w.GetBlock(x, y, z);
        if (id is Ladder or Vine) return true;
        if (!IsTrapdoor(id)) return false;
        int meta = w.GetMeta(x, y, z);
        if ((meta & 4) == 0) return false;
        if (w.GetBlock(x, y - 1, z) != Ladder) return false;
        int ladder = w.GetMeta(x, y - 1, z) % 6;                                       // EnumFacing.byIndex(meta), Y axis → north
        if (ladder < 2) ladder = 2;
        int trap = (meta & 3) switch { 0 => 2, 1 => 3, 2 => 4, _ => 5 };
        return ladder == trap;
    }
}
