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
    public static bool IsOpaqueCube(int id) => id switch
    {
        0 or 6 or (>= 8 and <= 11) or Glass or Bed or 27 or 28 or StickyPiston or Web or 31 or 32 or Piston or PistonHead
            or PistonMoving or (>= 37 and <= 40) or Slab or 50 or Fire or 52 or WoodStairs or Chest or RedstoneWire or 59
            or Farmland or 63 or WoodDoor or Ladder or Rail or CobbleStairs or 68 or 69 or 70 or IronDoor or 72 or 75 or 76
            or 77 or SnowLayer or Ice or Cactus or 83 or Fence or Portal or Cake or RepeaterOff or RepeaterOn or Trapdoor
            or IronBars or GlassPane or 104 or 105 or Vine or FenceGate or BrickStairs or StoneBrickStairs => false,
        _ => true,
    };

    /// <summary>Material.isSolid() (MCP blocksMovement) of the block's material: false for air, liquids, plants,
    /// "orientable" (torch, lever, button, ladder, rails, redstone, repeater), snow layer, fire, portal and web; true for
    /// everything else, signs, pressure plates and doors included. mc-dev Material.java, MaterialLogic / MaterialTransparent
    /// / MaterialLiquid / MaterialPortal / MaterialWeb. The water flow vector tests this, not the collision box.</summary>
    public static bool MaterialBlocksMovement(int id) => id switch
    {
        0 or 6 or (>= 8 and <= 11) or 27 or 28 or Web or 31 or 32 or (>= 37 and <= 40) or 50 or Fire or RedstoneWire
            or 59 or Ladder or Rail or 69 or 75 or 76 or 77 or SnowLayer or 83 or Portal or RepeaterOff or RepeaterOn
            or 104 or 105 or Vine => false,
        _ => true,
    };

    /// <summary>Material.isBuildable() (MCP isSolid): like <see cref="MaterialBlocksMovement"/> except that web is buildable.</summary>
    public static bool MaterialIsBuildable(int id) => id == Web || MaterialBlocksMovement(id);

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
            case IronBars: case GlassPane:
                AddPaneBoxes(w, id, x, y, z, Add); return;
            case PistonHead:
                AddPistonHeadBoxes(meta, Add); return;
            case Piston: case StickyPiston:
                Add(0, 0, 0, 1, 1, 1); return; // BlockPiston.a(World..AABB): full cube even when extended
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

    /// <summary>BlockThin.a(World, x, y, z, AABB, list): b1.8 glass panes / iron bars are a 1/8-thick bar along X and/or
    /// Z depending on which neighbours they connect to (opaque cubes, the same block, glass). mc-dev BlockThin.java:22-53.</summary>
    private static void AddPaneBoxes(IBlockAccess w, int id, int x, int y, int z, Action<double, double, double, double, double, double> add)
    {
        var (n, s, wst, e) = PaneLinks(w, id, x, y, z);
        bool any = n || s || wst || e;
        if ((!wst || !e) && any)
        {
            if (wst && !e) add(0, 0, 0.4375, 0.5, 1, 0.5625);
            else if (!wst && e) add(0.5, 0, 0.4375, 1, 1, 0.5625);
        }
        else add(0, 0, 0.4375, 1, 1, 0.5625);
        if ((!n || !s) && any)
        {
            if (n && !s) add(0.4375, 0, 0, 0.5625, 1, 0.5);
            else if (!n && s) add(0.4375, 0, 0.5, 0.5625, 1, 1);
        }
        else add(0.4375, 0, 0, 0.5625, 1, 1);
    }

    /// <summary>BlockThin.a(IBlockAccess): the selection box is the bounding box of the arms. mc-dev BlockThin.java:55-88.</summary>
    private static void AddPaneSelection(IBlockAccess w, int id, int x, int y, int z, Action<double, double, double, double, double, double> add)
    {
        var (n, s, wst, e) = PaneLinks(w, id, x, y, z);
        bool any = n || s || wst || e;
        double x0 = 0.4375, x1 = 0.5625, z0 = 0.4375, z1 = 0.5625;
        if ((!wst || !e) && any) { if (wst && !e) x0 = 0; else if (!wst && e) x1 = 1; }
        else { x0 = 0; x1 = 1; }
        if ((!n || !s) && any) { if (n && !s) z0 = 0; else if (!n && s) z1 = 1; }
        else { z0 = 0; z1 = 1; }
        add(x0, 0, z0, x1, 1, z1);
    }

    /// <summary>BlockThin.c(id): a pane connects to opaque cubes, to its own block and to glass. (north, south, west, east)</summary>
    private static (bool N, bool S, bool W, bool E) PaneLinks(IBlockAccess w, int id, int x, int y, int z)
    {
        bool C(int other) => IsOpaqueCube(other) || other == id || other == Glass;
        return (C(w.GetBlock(x, y, z - 1)), C(w.GetBlock(x, y, z + 1)), C(w.GetBlock(x - 1, y, z)), C(w.GetBlock(x + 1, y, z)));
    }

    /// <summary>BlockPistonExtension.a(World..AABB): head plate + arm by facing (meta &amp; 7). mc-dev BlockPistonExtension.java:61-108.</summary>
    private static void AddPistonHeadBoxes(int meta, Action<double, double, double, double, double, double> add)
    {
        switch (meta & 7)
        {
            case 0: add(0, 0, 0, 1, 0.25, 1); add(0.375, 0.25, 0.375, 0.625, 1, 0.625); break;
            case 1: add(0, 0.75, 0, 1, 1, 1); add(0.375, 0, 0.375, 0.625, 0.75, 0.625); break;
            case 2: add(0, 0, 0, 1, 1, 0.25); add(0.25, 0.375, 0.25, 0.75, 0.625, 1); break;
            case 3: add(0, 0, 0.75, 1, 1, 1); add(0.25, 0.375, 0, 0.75, 0.625, 0.75); break;
            case 4: add(0, 0, 0, 0.25, 1, 1); add(0.375, 0.25, 0.25, 0.625, 0.75, 1); break;
            case 5: add(0.75, 0, 0, 1, 1, 1); add(0, 0.375, 0.25, 0.75, 0.625, 0.75); break;
        }
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

    /// <summary>Raycast target boxes: the block bounds after Block.a(IBlockAccess, x, y, z) (setBlockBoundsBasedOnState)
    /// in mc-dev. They differ from the collision boxes for stairs (full), farmland (0.9375; collision is full), soul
    /// sand and cactus (full), cake (0.5 high), pistons, plants, torches, levers, buttons, signs, rails, plates, vines
    /// and portals. Air, liquids and fire are not targetable.</summary>
    public static void AddSelectionBoxes(IBlockAccess w, int x, int y, int z, List<AABB> output)
    {
        int id = w.GetBlock(x, y, z);
        if (id == 0 || IsLiquid(id) || id == Fire) return;
        int meta = w.GetMeta(x, y, z);
        void Add(double x0, double y0, double z0, double x1, double y1, double z1) =>
            output.Add(new AABB(x + x0, y + y0, z + z0, x + x1, y + y1, z + z1));
        switch (id)
        {
            case Fence: case FenceGate: case Web:
            case WoodStairs: case CobbleStairs: case BrickStairs: case StoneBrickStairs: // BlockStairs.a(IBlockAccess): full
            case SoulSand: case Cactus:                                                  // only their collision is inset
                Add(0, 0, 0, 1, 1, 1); return;
            case Farmland: Add(0, 0, 0, 1, 0.9375, 1); return;                                         // BlockSoil.java:11
            case Cake: Add((1 + meta * 2) / 16.0, 0, 0.0625, 0.9375, 0.5, 0.9375); return;               // BlockCake.java:12-19
            case 37: case 38: Add(0.3, 0, 0.3, 0.7, 0.6, 0.7); return;                                  // BlockFlower f=0.2
            case 39: case 40: Add(0.3, 0, 0.3, 0.7, 0.4, 0.7); return;                                  // BlockMushroom
            case 6: case 31: case 32: Add(0.1, 0, 0.1, 0.9, 0.8, 0.9); return;                          // f=0.4
            case 59: Add(0, 0, 0, 1, 0.25, 1); return;                                                  // BlockCrops
            case 83: Add(0.125, 0, 0.125, 0.875, 1, 0.875); return;                                     // BlockReed
            case 104: case 105: Add(0.375, 0, 0.375, 0.625, (meta * 2 + 2) / 16.0, 0.625); return;      // BlockStem
            case 50: case 75: case 76:                                         // BlockTorch.a(World, .., Vec3D, Vec3D)
                switch (meta & 7)
                {
                    case 1: Add(0, 0.2, 0.35, 0.3, 0.8, 0.65); break;
                    case 2: Add(0.7, 0.2, 0.35, 1, 0.8, 0.65); break;
                    case 3: Add(0.35, 0.2, 0, 0.65, 0.8, 0.3); break;
                    case 4: Add(0.35, 0.2, 0.7, 0.65, 0.8, 1); break;
                    default: Add(0.4, 0, 0.4, 0.6, 0.6, 0.6); break;
                }
                return;
            case 69:                                                           // BlockLever.a(IBlockAccess)
                switch (meta & 7)
                {
                    case 1: Add(0, 0.2, 0.3125, 0.375, 0.8, 0.6875); break;
                    case 2: Add(0.625, 0.2, 0.3125, 1, 0.8, 0.6875); break;
                    case 3: Add(0.3125, 0.2, 0, 0.6875, 0.8, 0.375); break;
                    case 4: Add(0.3125, 0.2, 0.625, 0.6875, 0.8, 1); break;
                    default: Add(0.25, 0, 0.25, 0.75, 0.6, 0.75); break;
                }
                return;
            case 77:                                                           // BlockButton.a(IBlockAccess); pressed = 1/16 deep
            {
                double d = (meta & 8) != 0 ? 0.0625 : 0.125;
                switch (meta & 7)
                {
                    case 1: Add(0, 0.375, 0.3125, d, 0.625, 0.6875); break;
                    case 2: Add(1 - d, 0.375, 0.3125, 1, 0.625, 0.6875); break;
                    case 3: Add(0.3125, 0.375, 0, 0.6875, 0.625, d); break;
                    case 4: Add(0.3125, 0.375, 1 - d, 0.6875, 0.625, 1); break;
                    default: Add(0, 0, 0, 1, 1, 1); break;
                }
                return;
            }
            case 63: Add(0.25, 0, 0.25, 0.75, 1, 0.75); return;                // BlockSign post, f=0.25
            case 68:                                                           // BlockSign wall
                switch (meta)
                {
                    case 2: Add(0, 0.28125, 0.875, 1, 0.78125, 1); break;
                    case 3: Add(0, 0.28125, 0, 1, 0.78125, 0.125); break;
                    case 4: Add(0.875, 0.28125, 0, 1, 0.78125, 1); break;
                    case 5: Add(0, 0.28125, 0, 0.125, 0.78125, 1); break;
                    default: Add(0, 0, 0, 1, 1, 1); break;
                }
                return;
            case RedstoneWire: Add(0, 0, 0, 1, 0.0625, 1); return;
            case Rail: case 27: case 28: Add(0, 0, 0, 1, meta is >= 2 and <= 5 ? 0.625 : 0.125, 1); return; // ascending 0.625
            case 70: case 72: Add(0.0625, 0, 0.0625, 0.9375, meta == 1 ? 0.03125 : 0.0625, 0.9375); return;
            case SnowLayer: Add(0, 0, 0, 1, (2 * (1 + (meta & 7))) / 16.0, 1); return;
            case Vine: AddVineSelection(w, meta, x, y, z, Add); return;
            case Portal:                                                       // BlockPortal.a(IBlockAccess)
                if (w.GetBlock(x - 1, y, z) != Portal && w.GetBlock(x + 1, y, z) != Portal) Add(0.375, 0, 0, 0.625, 1, 1);
                else Add(0, 0, 0.375, 1, 1, 0.625);
                return;
            case Piston: case StickyPiston:                                    // BlockPiston.a(IBlockAccess): shorter when extended
                if ((meta & 8) == 0) { Add(0, 0, 0, 1, 1, 1); return; }
                switch (meta & 7)
                {
                    case 0: Add(0, 0.25, 0, 1, 1, 1); break;
                    case 1: Add(0, 0, 0, 1, 0.75, 1); break;
                    case 2: Add(0, 0, 0.25, 1, 1, 1); break;
                    case 3: Add(0, 0, 0, 1, 1, 0.75); break;
                    case 4: Add(0.25, 0, 0, 1, 1, 1); break;
                    case 5: Add(0, 0, 0, 0.75, 1, 1); break;
                    default: Add(0, 0, 0, 1, 1, 1); break;
                }
                return;
            case PistonHead:                                                   // BlockPistonExtension.a(IBlockAccess): plate only
                switch (meta & 7)
                {
                    case 0: Add(0, 0, 0, 1, 0.25, 1); break;
                    case 1: Add(0, 0.75, 0, 1, 1, 1); break;
                    case 2: Add(0, 0, 0, 1, 1, 0.25); break;
                    case 3: Add(0, 0, 0.75, 1, 1, 1); break;
                    case 4: Add(0, 0, 0, 0.25, 1, 1); break;
                    default: Add(0.75, 0, 0, 1, 1, 1); break;
                }
                return;
            case IronBars: case GlassPane: AddPaneSelection(w, id, x, y, z, Add); return;
        }
        int before = output.Count;
        AddCollisionBoxes(w, id, meta, x, y, z, output);
        if (output.Count == before) Add(0, 0, 0, 1, 1, 1);
    }

    /// <summary>BlockVine.a(IBlockAccess): a 1/16 plate on each attached side (meta 1 south, 2 west, 4 north, 8 east), or
    /// under the block above when unattached. mc-dev BlockVine.java:20-80.</summary>
    private static void AddVineSelection(IBlockAccess w, int meta, int x, int y, int z, Action<double, double, double, double, double, double> add)
    {
        double x0 = 1, y0 = 1, z0 = 1, x1 = 0, y1 = 0, z1 = 0;
        bool attached = meta > 0;
        if ((meta & 2) != 0) { x1 = Math.Max(x1, 0.0625); x0 = 0; y0 = 0; y1 = 1; z0 = 0; z1 = 1; }
        if ((meta & 8) != 0) { x0 = Math.Min(x0, 0.9375); x1 = 1; y0 = 0; y1 = 1; z0 = 0; z1 = 1; }
        if ((meta & 4) != 0) { z1 = Math.Max(z1, 0.0625); z0 = 0; x0 = 0; x1 = 1; y0 = 0; y1 = 1; }
        if ((meta & 1) != 0) { z0 = Math.Min(z0, 0.9375); z1 = 1; x0 = 0; x1 = 1; y0 = 0; y1 = 1; }
        if (!attached && IsOpaqueCube(w.GetBlock(x, y + 1, z))) { y0 = Math.Min(y0, 0.9375); y1 = 1; x0 = 0; x1 = 1; z0 = 0; z1 = 1; }
        if (x1 <= x0 || y1 <= y0 || z1 <= z0) { add(0, 0, 0, 1, 1, 1); return; }
        add(x0, y0, z0, x1, y1, z1);
    }

    private static readonly ThreadLocal<List<AABB>> ListCache = new(() => new List<AABB>(4));
}
