namespace MineClaw.World;

/// <summary>
/// The b1.8.1 collision and selection boxes (mc-dev): each block id has a shape kind (a table), the fixed shapes are box tables, and
/// the shapes that read the metadata or the neighbours have one small builder each.
/// </summary>
public static partial class BlockShapes
{
    // ------------------------------------------------------------------ collision (Block.a(World, x, y, z, AABB, list))

    private enum BetaCollision : byte { Material, None, Fixed, Gate, Cake, Snow, Pane, PistonHead, Ladder, Door, Trapdoor, Stairs }

    private static readonly BetaCollision[] BetaCollisionKinds = BuildBetaCollisionKinds();
    private static readonly double[]?[] BetaFixedCollision = BuildBetaFixedCollision();

    private static BetaCollision[] BuildBetaCollisionKinds()
    {
        var k = new BetaCollision[256];
        // No collision: air, liquids, web, fire, redstone, rails, vines, plants, torches, signs, levers, plates, buttons, cane, portal, stems.
        foreach (int id in new[] { 0, Water, StillWater, Lava, StillLava, Web, Fire, RedstoneWire, Rail, 27, 28, Vine, 6, 31, 32, 37, 38, 39,
                     40, 50, 59, 63, 68, 69, 70, 72, 75, 76, 77, 83, 90, 104, 105 })
            k[id] = BetaCollision.None;
        foreach (int id in new[] { Slab, Bed, SoulSand, Fence, Cactus, RepeaterOff, RepeaterOn, Piston, StickyPiston })
            k[id] = BetaCollision.Fixed;
        k[FenceGate] = BetaCollision.Gate;
        k[Cake] = BetaCollision.Cake;
        k[SnowLayer] = BetaCollision.Snow;
        k[IronBars] = k[GlassPane] = BetaCollision.Pane;
        k[PistonHead] = BetaCollision.PistonHead;
        k[Ladder] = BetaCollision.Ladder;
        k[WoodDoor] = k[IronDoor] = BetaCollision.Door;
        k[Trapdoor] = BetaCollision.Trapdoor;
        k[WoodStairs] = k[CobbleStairs] = k[BrickStairs] = k[StoneBrickStairs] = BetaCollision.Stairs;
        return k;
    }

    private static double[]?[] BuildBetaFixedCollision()
    {
        const double f = 0.0625;
        var t = new double[]?[256];
        t[Slab] = [0, 0, 0, 1, 0.5, 1];
        t[Bed] = [0, 0, 0, 1, 0.5625, 1];
        t[SoulSand] = [0, 0, 0, 1, 0.875, 1];
        t[Fence] = [0, 0, 0, 1, 1.5, 1];
        t[Cactus] = [f, 0, f, 1 - f, 1 - f, 1 - f];
        t[RepeaterOff] = t[RepeaterOn] = [0, 0, 0, 1, 0.125, 1];
        t[Piston] = t[StickyPiston] = [0, 0, 0, 1, 1, 1];      // BlockPiston.a(World..AABB): full cube even when extended
        return t;
    }

    private static BetaCollision BetaCollisionKind(int id) =>
        (uint)id < (uint)BetaCollisionKinds.Length ? BetaCollisionKinds[id] : BetaCollision.Material;

    /// <summary>The b1.8.1 collision boxes (mc-dev), whatever the world's version.</summary>
    internal static void AddCollisionBoxesBeta(IBlockAccess w, int id, int meta, int x, int y, int z, List<AABB> output)
    {
        var box = new BoxSink(output, x, y, z);
        var kind = BetaCollisionKind(id);
        switch (kind)
        {
            case BetaCollision.None: return;
            case BetaCollision.Fixed: box.AddAll(BetaFixedCollision[id]!); return;
            case BetaCollision.Material: AddMaterialBox(w, id, box); return;
            default: AddShapedCollisionBeta(kind, w, id, meta, x, y, z, box); return;
        }
    }

    /// <summary>Every other block: a solid one is a box of its collision height (a full cube by default).</summary>
    private static void AddMaterialBox(IBlockAccess w, int id, BoxSink box)
    {
        var info = w.Info(id);
        if (!info.Solid || info.Liquid) return;
        double h = info.CollisionHeight > 0 ? info.CollisionHeight : 1.0;
        box.Add(0, 0, 0, 1, h, 1);
    }

    private static void AddShapedCollisionBeta(BetaCollision kind, IBlockAccess w, int id, int meta, int x, int y, int z, BoxSink box)
    {
        switch (kind)
        {
            case BetaCollision.Gate: AddGateBox(meta, box); return;
            case BetaCollision.Cake: AddCakeBox(meta, box); return;
            case BetaCollision.Snow: AddSnowBox(meta, box); return;
            case BetaCollision.Pane: AddPaneBoxes(w, id, x, y, z, box); return;
            case BetaCollision.PistonHead: AddPistonHeadBoxes(meta, box); return;
            case BetaCollision.Ladder: AddLadderBox(meta, box); return;
            case BetaCollision.Door: AddDoorBox(meta, box); return;
            case BetaCollision.Trapdoor: AddTrapdoorBox(meta, box); return;
            default: AddStairBoxesBeta(meta, box); return;
        }
    }

    private static void AddGateBox(int meta, BoxSink box)
    {
        if ((meta & 4) == 0) box.Add(0, 0, 0, 1, 1.5, 1);
    }

    private static void AddCakeBox(int meta, BoxSink box)
    {
        const double f = 0.0625;
        double f1 = (1 + meta * 2) / 16.0;
        box.Add(f1, 0, f, 1 - f, 0.5 - f, 1 - f);
    }

    private static void AddSnowBox(int meta, BoxSink box)
    {
        if ((meta & 7) >= 3) box.Add(0, 0, 0, 1, 0.5, 1);
    }

    /// <summary>BlockThin.a(World, x, y, z, AABB, list): b1.8 glass panes / iron bars are a 1/8-thick bar along X and/or
    /// Z depending on which neighbours they connect to (opaque cubes, the same block, glass). mc-dev BlockThin.java:22-53.</summary>
    private static void AddPaneBoxes(IBlockAccess w, int id, int x, int y, int z, BoxSink box)
    {
        var (n, s, wst, e) = PaneLinks(w, id, x, y, z);
        bool any = n || s || wst || e;
        switch (PaneSpanOf(wst, e, any))
        {
            case PaneSpan.Low: box.Add(0, 0, 0.4375, 0.5, 1, 0.5625); break;
            case PaneSpan.High: box.Add(0.5, 0, 0.4375, 1, 1, 0.5625); break;
            case PaneSpan.Both: box.Add(0, 0, 0.4375, 1, 1, 0.5625); break;
        }
        switch (PaneSpanOf(n, s, any))
        {
            case PaneSpan.Low: box.Add(0.4375, 0, 0, 0.5625, 1, 0.5); break;
            case PaneSpan.High: box.Add(0.4375, 0, 0.5, 0.5625, 1, 1); break;
            case PaneSpan.Both: box.Add(0.4375, 0, 0, 0.5625, 1, 1); break;
        }
    }

    /// <summary>BlockThin.a(IBlockAccess): the selection box is the bounding box of the arms. mc-dev BlockThin.java:55-88.</summary>
    private static void AddPaneSelection(IBlockAccess w, int id, int x, int y, int z, BoxSink box)
    {
        var (n, s, wst, e) = PaneLinks(w, id, x, y, z);
        bool any = n || s || wst || e;
        var (x0, x1) = PaneBounds(PaneSpanOf(wst, e, any));
        var (z0, z1) = PaneBounds(PaneSpanOf(n, s, any));
        box.Add(x0, 0, z0, x1, 1, z1);
    }

    /// <summary>Which part of a pane's bar along one axis exists: the half toward the low or the high side when only that side
    /// connects, the whole bar when both do or nothing does, none when only the other axis connects.</summary>
    private enum PaneSpan { None, Low, High, Both }

    private static PaneSpan PaneSpanOf(bool low, bool high, bool any)
    {
        if ((low && high) || !any) return PaneSpan.Both;
        if (low) return PaneSpan.Low;
        return high ? PaneSpan.High : PaneSpan.None;
    }

    private static (double Min, double Max) PaneBounds(PaneSpan span) => span switch
    {
        PaneSpan.Low => (0, 0.5625),
        PaneSpan.High => (0.4375, 1),
        PaneSpan.Both => (0, 1),
        _ => (0.4375, 0.5625),
    };

    /// <summary>BlockThin.c(id): a pane connects to opaque cubes, to its own block and to glass. (north, south, west, east)</summary>
    private static (bool N, bool S, bool W, bool E) PaneLinks(IBlockAccess w, int id, int x, int y, int z) =>
        (PaneLinksTo(w.GetBlock(x, y, z - 1), id), PaneLinksTo(w.GetBlock(x, y, z + 1), id), PaneLinksTo(w.GetBlock(x - 1, y, z), id),
            PaneLinksTo(w.GetBlock(x + 1, y, z), id));

    private static bool PaneLinksTo(int other, int id) => IsOpaqueCube(other) || other == id || other == Glass;

    /// <summary>BlockPistonExtension.a(World..AABB): head plate + arm by facing (meta &amp; 7). mc-dev BlockPistonExtension.java:61-108.</summary>
    private static void AddPistonHeadBoxes(int meta, BoxSink box)
    {
        switch (meta & 7)
        {
            case 0: box.Add(0, 0, 0, 1, 0.25, 1); box.Add(0.375, 0.25, 0.375, 0.625, 1, 0.625); break;
            case 1: box.Add(0, 0.75, 0, 1, 1, 1); box.Add(0.375, 0, 0.375, 0.625, 0.75, 0.625); break;
            case 2: box.Add(0, 0, 0, 1, 1, 0.25); box.Add(0.25, 0.375, 0.25, 0.75, 0.625, 1); break;
            case 3: box.Add(0, 0, 0.75, 1, 1, 1); box.Add(0.25, 0.375, 0, 0.75, 0.625, 0.75); break;
            case 4: box.Add(0, 0, 0, 0.25, 1, 1); box.Add(0.375, 0.25, 0.25, 0.625, 0.75, 1); break;
            case 5: box.Add(0.75, 0, 0, 1, 1, 1); box.Add(0, 0.375, 0.25, 0.75, 0.625, 0.75); break;
        }
    }

    private static void AddLadderBox(int meta, BoxSink box)
    {
        const double f = 0.125;
        switch (meta)
        {
            case 2: box.Add(0, 0, 1 - f, 1, 1, 1); break;
            case 3: box.Add(0, 0, 0, 1, 1, f); break;
            case 4: box.Add(1 - f, 0, 0, 1, 1, 1); break;
            case 5: box.Add(0, 0, 0, f, 1, 1); break;
            default: box.Add(0, 0, 0, 1, 1, f); break;
        }
    }

    private static void AddDoorBox(int meta, BoxSink box)
    {
        const double f = 0.1875;
        int state = (meta & 4) == 0 ? (meta - 1) & 3 : meta & 3;
        switch (state)
        {
            case 0: box.Add(0, 0, 0, 1, 1, f); break;
            case 1: box.Add(1 - f, 0, 0, 1, 1, 1); break;
            case 2: box.Add(0, 0, 1 - f, 1, 1, 1); break;
            default: box.Add(0, 0, 0, f, 1, 1); break;
        }
    }

    private static void AddTrapdoorBox(int meta, BoxSink box)
    {
        const double f = 0.1875;
        if ((meta & 4) == 0) { box.Add(0, 0, 0, 1, f, 1); return; }
        switch (meta & 3)
        {
            case 0: box.Add(0, 0, 1 - f, 1, 1, 1); break;
            case 1: box.Add(0, 0, 0, 1, 1, f); break;
            case 2: box.Add(1 - f, 0, 0, 1, 1, 1); break;
            default: box.Add(0, 0, 0, f, 1, 1); break;
        }
    }

    private static void AddStairBoxesBeta(int meta, BoxSink box)
    {
        switch (meta & 3)
        {
            case 0: box.Add(0, 0, 0, 0.5, 0.5, 1); box.Add(0.5, 0, 0, 1, 1, 1); break;
            case 1: box.Add(0, 0, 0, 0.5, 1, 1); box.Add(0.5, 0, 0, 1, 0.5, 1); break;
            case 2: box.Add(0, 0, 0, 1, 0.5, 0.5); box.Add(0, 0, 0.5, 1, 1, 1); break;
            default: box.Add(0, 0, 0, 1, 1, 0.5); box.Add(0, 0, 0.5, 1, 0.5, 1); break;
        }
    }

    // ------------------------------------------------------------------ selection (Block.a(IBlockAccess, x, y, z))

    /// <summary>Selection shape kinds: <see cref="Collision"/> (the collision boxes, or a full cube without any) and the table's
    /// fixed boxes, then the attached blocks (by facing), then the rest that read the metadata or the neighbours.</summary>
    private enum BetaSelection : byte { Collision, Fixed, Torch, Lever, Button, WallSign, Rail, Plate, Cake, Stem, Snow, Piston, PistonHead, Vine, Portal, Pane }

    // Built in this order: the kinds read the fixed table.
    private static readonly double[]?[] BetaFixedSelection = BuildBetaFixedSelection();
    private static readonly BetaSelection[] BetaSelectionKinds = BuildBetaSelectionKinds();

    private static BetaSelection[] BuildBetaSelectionKinds()
    {
        var k = new BetaSelection[256];
        for (int id = 0; id < 256; id++)
            if (BetaFixedSelection[id] is not null) k[id] = BetaSelection.Fixed;
        k[50] = k[75] = k[76] = BetaSelection.Torch;
        k[69] = BetaSelection.Lever;
        k[77] = BetaSelection.Button;
        k[68] = BetaSelection.WallSign;
        k[Rail] = k[27] = k[28] = BetaSelection.Rail;
        k[70] = k[72] = BetaSelection.Plate;
        k[Cake] = BetaSelection.Cake;
        k[104] = k[105] = BetaSelection.Stem;
        k[SnowLayer] = BetaSelection.Snow;
        k[Piston] = k[StickyPiston] = BetaSelection.Piston;
        k[PistonHead] = BetaSelection.PistonHead;
        k[Vine] = BetaSelection.Vine;
        k[Portal] = BetaSelection.Portal;
        k[IronBars] = k[GlassPane] = BetaSelection.Pane;
        return k;
    }

    private static double[]?[] BuildBetaFixedSelection()
    {
        var t = new double[]?[256];
        double[] full = [0, 0, 0, 1, 1, 1];
        // BlockStairs.a(IBlockAccess): full; soul sand and cactus: only their collision is inset.
        foreach (int id in new[] { Fence, FenceGate, Web, WoodStairs, CobbleStairs, BrickStairs, StoneBrickStairs, SoulSand, Cactus }) t[id] = full;
        t[Farmland] = [0, 0, 0, 1, 0.9375, 1];                                    // BlockSoil.java:11
        t[37] = t[38] = [0.3, 0, 0.3, 0.7, 0.6, 0.7];                             // BlockFlower f=0.2
        t[39] = t[40] = [0.3, 0, 0.3, 0.7, 0.4, 0.7];                             // BlockMushroom
        t[6] = t[31] = t[32] = [0.1, 0, 0.1, 0.9, 0.8, 0.9];                      // f=0.4
        t[59] = [0, 0, 0, 1, 0.25, 1];                                            // BlockCrops
        t[83] = [0.125, 0, 0.125, 0.875, 1, 0.875];                               // BlockReed
        t[63] = [0.25, 0, 0.25, 0.75, 1, 0.75];                                   // BlockSign post, f=0.25
        t[RedstoneWire] = [0, 0, 0, 1, 0.0625, 1];
        return t;
    }

    private static BetaSelection BetaSelectionKind(int id) =>
        (uint)id < (uint)BetaSelectionKinds.Length ? BetaSelectionKinds[id] : BetaSelection.Collision;

    /// <summary>Raycast target boxes: the block bounds after Block.a(IBlockAccess, x, y, z) (setBlockBoundsBasedOnState)
    /// in mc-dev. They differ from the collision boxes for stairs (full), farmland (0.9375; collision is full), soul
    /// sand and cactus (full), cake (0.5 high), pistons, plants, torches, levers, buttons, signs, rails, plates, vines
    /// and portals. Air, liquids and fire are not targetable.</summary>
    public static void AddSelectionBoxes(IBlockAccess w, int x, int y, int z, List<AABB> output)
    {
        int id = w.GetBlock(x, y, z);
        if (id == 0 || IsLiquid(id) || id == Fire) return;
        int meta = w.GetMeta(x, y, z);
        var box = new BoxSink(output, x, y, z);
        var kind = BetaSelectionKind(id);
        if (kind == BetaSelection.Fixed) box.AddAll(BetaFixedSelection[id]!);
        else if (kind == BetaSelection.Collision) AddCollisionSelection(w, id, meta, x, y, z, output);
        else if (kind < BetaSelection.Cake) AddAttachedSelection(kind, meta, box);
        else AddShapedSelection(kind, w, id, meta, x, y, z, box);
    }

    /// <summary>The collision boxes (the world's version), or a full cube when there are none.</summary>
    private static void AddCollisionSelection(IBlockAccess w, int id, int meta, int x, int y, int z, List<AABB> output)
    {
        int before = output.Count;
        AddCollisionBoxes(w, id, meta, x, y, z, output);
        if (output.Count == before) new BoxSink(output, x, y, z).Add(0, 0, 0, 1, 1, 1);
    }

    /// <summary>Torches, levers, buttons, wall signs, rails and plates: a box by facing or state.</summary>
    private static void AddAttachedSelection(BetaSelection kind, int meta, BoxSink box)
    {
        switch (kind)
        {
            case BetaSelection.Torch: AddTorchSelection(meta, box); break;
            case BetaSelection.Lever: AddLeverSelection(meta, box); break;
            case BetaSelection.Button: AddButtonSelection(meta, box); break;
            case BetaSelection.WallSign: AddWallSignSelection(meta, box); break;
            case BetaSelection.Rail: box.Add(0, 0, 0, 1, RailHeight(meta), 1); break;
            default: box.Add(0.0625, 0, 0.0625, 0.9375, PlateHeight(meta), 0.9375); break;
        }
    }

    /// <summary>Ascending rails are 0.625 high, flat ones 0.125.</summary>
    private static double RailHeight(int meta) => meta is >= 2 and <= 5 ? 0.625 : 0.125;

    /// <summary>A pressed plate is 1/32 high, a released one 1/16.</summary>
    private static double PlateHeight(int meta) => meta == 1 ? 0.03125 : 0.0625;

    private static void AddShapedSelection(BetaSelection kind, IBlockAccess w, int id, int meta, int x, int y, int z, BoxSink box)
    {
        switch (kind)
        {
            case BetaSelection.Cake: box.Add((1 + meta * 2) / 16.0, 0, 0.0625, 0.9375, 0.5, 0.9375); break;     // BlockCake.java:12-19
            case BetaSelection.Stem: box.Add(0.375, 0, 0.375, 0.625, (meta * 2 + 2) / 16.0, 0.625); break;     // BlockStem
            case BetaSelection.Snow: box.Add(0, 0, 0, 1, (2 * (1 + (meta & 7))) / 16.0, 1); break;
            case BetaSelection.Piston: AddPistonSelection(meta, box); break;
            case BetaSelection.PistonHead: AddPistonHeadSelection(meta, box); break;
            case BetaSelection.Vine: AddVineSelection(w, meta, x, y, z, box); break;
            case BetaSelection.Portal: AddPortalSelection(w, x, y, z, box); break;
            default: AddPaneSelection(w, id, x, y, z, box); break;
        }
    }

    /// <summary>BlockTorch.a(World, .., Vec3D, Vec3D).</summary>
    private static void AddTorchSelection(int meta, BoxSink box)
    {
        switch (meta & 7)
        {
            case 1: box.Add(0, 0.2, 0.35, 0.3, 0.8, 0.65); break;
            case 2: box.Add(0.7, 0.2, 0.35, 1, 0.8, 0.65); break;
            case 3: box.Add(0.35, 0.2, 0, 0.65, 0.8, 0.3); break;
            case 4: box.Add(0.35, 0.2, 0.7, 0.65, 0.8, 1); break;
            default: box.Add(0.4, 0, 0.4, 0.6, 0.6, 0.6); break;
        }
    }

    /// <summary>BlockLever.a(IBlockAccess).</summary>
    private static void AddLeverSelection(int meta, BoxSink box)
    {
        switch (meta & 7)
        {
            case 1: box.Add(0, 0.2, 0.3125, 0.375, 0.8, 0.6875); break;
            case 2: box.Add(0.625, 0.2, 0.3125, 1, 0.8, 0.6875); break;
            case 3: box.Add(0.3125, 0.2, 0, 0.6875, 0.8, 0.375); break;
            case 4: box.Add(0.3125, 0.2, 0.625, 0.6875, 0.8, 1); break;
            default: box.Add(0.25, 0, 0.25, 0.75, 0.6, 0.75); break;
        }
    }

    /// <summary>BlockButton.a(IBlockAccess); pressed = 1/16 deep.</summary>
    private static void AddButtonSelection(int meta, BoxSink box)
    {
        double d = (meta & 8) != 0 ? 0.0625 : 0.125;
        switch (meta & 7)
        {
            case 1: box.Add(0, 0.375, 0.3125, d, 0.625, 0.6875); break;
            case 2: box.Add(1 - d, 0.375, 0.3125, 1, 0.625, 0.6875); break;
            case 3: box.Add(0.3125, 0.375, 0, 0.6875, 0.625, d); break;
            case 4: box.Add(0.3125, 0.375, 1 - d, 0.6875, 0.625, 1); break;
            default: box.Add(0, 0, 0, 1, 1, 1); break;
        }
    }

    /// <summary>BlockSign, wall.</summary>
    private static void AddWallSignSelection(int meta, BoxSink box)
    {
        switch (meta)
        {
            case 2: box.Add(0, 0.28125, 0.875, 1, 0.78125, 1); break;
            case 3: box.Add(0, 0.28125, 0, 1, 0.78125, 0.125); break;
            case 4: box.Add(0.875, 0.28125, 0, 1, 0.78125, 1); break;
            case 5: box.Add(0, 0.28125, 0, 0.125, 0.78125, 1); break;
            default: box.Add(0, 0, 0, 1, 1, 1); break;
        }
    }

    /// <summary>BlockPortal.a(IBlockAccess): thin along the axis the portal spans.</summary>
    private static void AddPortalSelection(IBlockAccess w, int x, int y, int z, BoxSink box)
    {
        if (w.GetBlock(x - 1, y, z) != Portal && w.GetBlock(x + 1, y, z) != Portal) box.Add(0.375, 0, 0, 0.625, 1, 1);
        else box.Add(0, 0, 0.375, 1, 1, 0.625);
    }

    /// <summary>BlockPiston.a(IBlockAccess): shorter when extended.</summary>
    private static void AddPistonSelection(int meta, BoxSink box)
    {
        if ((meta & 8) == 0) { box.Add(0, 0, 0, 1, 1, 1); return; }
        switch (meta & 7)
        {
            case 0: box.Add(0, 0.25, 0, 1, 1, 1); break;
            case 1: box.Add(0, 0, 0, 1, 0.75, 1); break;
            case 2: box.Add(0, 0, 0.25, 1, 1, 1); break;
            case 3: box.Add(0, 0, 0, 1, 1, 0.75); break;
            case 4: box.Add(0.25, 0, 0, 1, 1, 1); break;
            case 5: box.Add(0, 0, 0, 0.75, 1, 1); break;
            default: box.Add(0, 0, 0, 1, 1, 1); break;
        }
    }

    /// <summary>BlockPistonExtension.a(IBlockAccess): plate only.</summary>
    private static void AddPistonHeadSelection(int meta, BoxSink box)
    {
        switch (meta & 7)
        {
            case 0: box.Add(0, 0, 0, 1, 0.25, 1); break;
            case 1: box.Add(0, 0.75, 0, 1, 1, 1); break;
            case 2: box.Add(0, 0, 0, 1, 1, 0.25); break;
            case 3: box.Add(0, 0, 0.75, 1, 1, 1); break;
            case 4: box.Add(0, 0, 0, 0.25, 1, 1); break;
            default: box.Add(0.75, 0, 0, 1, 1, 1); break;
        }
    }

    /// <summary>BlockVine.a(IBlockAccess): a 1/16 plate on each attached side (meta 1 south, 2 west, 4 north, 8 east), or
    /// under the block above when unattached. mc-dev BlockVine.java:20-80.</summary>
    private static void AddVineSelection(IBlockAccess w, int meta, int x, int y, int z, BoxSink box)
    {
        double x0 = 1, y0 = 1, z0 = 1, x1 = 0, y1 = 0, z1 = 0;
        bool attached = meta > 0;
        if ((meta & 2) != 0) { x1 = Math.Max(x1, 0.0625); x0 = 0; y0 = 0; y1 = 1; z0 = 0; z1 = 1; }
        if ((meta & 8) != 0) { x0 = Math.Min(x0, 0.9375); x1 = 1; y0 = 0; y1 = 1; z0 = 0; z1 = 1; }
        if ((meta & 4) != 0) { z1 = Math.Max(z1, 0.0625); z0 = 0; x0 = 0; x1 = 1; y0 = 0; y1 = 1; }
        if ((meta & 1) != 0) { z0 = Math.Min(z0, 0.9375); z1 = 1; x0 = 0; x1 = 1; y0 = 0; y1 = 1; }
        if (!attached && IsOpaqueCube(w.GetBlock(x, y + 1, z))) { y0 = Math.Min(y0, 0.9375); y1 = 1; x0 = 0; x1 = 1; z0 = 0; z1 = 1; }
        if (x1 <= x0 || y1 <= y0 || z1 <= z0) { box.Add(0, 0, 0, 1, 1, 1); return; }
        box.Add(x0, y0, z0, x1, y1, z1);
    }
}
