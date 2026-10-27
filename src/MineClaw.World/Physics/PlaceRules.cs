using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// Why a block can't go into a cell, in words a player understands, and the vanilla server's own rules for it. b1.8.1
/// (mc-dev NetServerHandler.a(Packet15Place)) only checks that the clicked block's centre is within 8 blocks of the player
/// and then runs ItemBlock → World.mayPlace → Block.canPlace: there is no line-of-sight test, so a support face the bot
/// can't see is still a valid click (<see cref="BlindAim"/>). Plants have soil rules (BlockFlower.canPlace: grass, dirt or
/// farmland directly under; reeds: grass/dirt/sand with water beside it; cactus: sand under and nothing solid around).
/// </summary>
public static class PlaceRules
{
    /// <summary>Block a placed item becomes (sugar cane item 338 → reed block 83), else the id itself.</summary>
    public static int BlockOf(int itemId) => itemId == 338 ? Blocks.SugarCane : itemId;

    /// <summary>
    /// Null when the block may stand in <paramref name="target"/> as far as its own rules go (vanilla canPlace), else the exact
    /// reason, e.g. "15,65,1 has no dirt/grass under it for a sapling (15,64,1 is air)".
    /// </summary>
    public static string? SoilProblem(IBlockAccess w, BlockPos target, int itemId)
    {
        int block = BlockOf(itemId);
        var below = target.Down();
        int under = w.GetBlock(below.X, below.Y, below.Z);
        string underName = Name(w, below);
        switch (block)
        {
            case Blocks.Sapling or Blocks.Dandelion or Blocks.Rose:
                if (under is Blocks.Grass or Blocks.Dirt or Blocks.Farmland) return null;
                return $"{F(target)} has no dirt/grass under it for a {PlantName(block)} ({F(below)} is {underName})";
            case Blocks.BrownMushroom or Blocks.RedMushroom:
                if (under != 0 && w.Info(under).Solid && BlockShapes.HasCollision(w, below.X, below.Y, below.Z) && !BlockShapes.IsLiquid(under)) return null;
                return $"{F(target)} has nothing solid under it for a mushroom ({F(below)} is {underName})";
            case Blocks.SugarCane:
                if (under == Blocks.SugarCane) return null;
                if (under is not (Blocks.Grass or Blocks.Dirt or Blocks.Sand))
                    return $"{F(target)} has no dirt/grass/sand under it for sugar cane ({F(below)} is {underName})";
                foreach (var o in Horizontal)
                    if (BlockShapes.IsWater(w.GetBlock(below.X + o.X, below.Y, below.Z + o.Z))) return null;
                return $"{F(target)}: sugar cane needs water right next to the {underName} at {F(below)}";
            case Blocks.Cactus:
                foreach (var o in Horizontal)
                    if (BlockShapes.HasCollision(w, target.X + o.X, target.Y, target.Z + o.Z))
                        return $"{F(target)}: cactus can't touch a block on its side ({Name(w, new BlockPos(target.X + o.X, target.Y, target.Z + o.Z))} at {F(new BlockPos(target.X + o.X, target.Y, target.Z + o.Z))})";
                if (under is Blocks.Sand or Blocks.Cactus) return null;
                return $"{F(target)} has no sand under it for a cactus ({F(below)} is {underName})";
        }
        return null;
    }

    /// <summary>A neighbour the server lets us click to put a block into the cell beside it (solid, not a container/door).</summary>
    public static bool IsSupport(IBlockAccess w, BlockPos p)
    {
        int id = w.GetBlock(p.X, p.Y, p.Z);
        return id != 0 && !BlockShapes.IsLiquid(id) && !w.Info(id).Interactable && BlockShapes.HasCollision(w, p.X, p.Y, p.Z);
    }

    /// <summary>
    /// "7,62,9 has nothing solid on any side to place against (below 7,61,9 air, above 7,63,9 air, ...)" when no neighbour can
    /// be clicked at all, else null.
    /// </summary>
    public static string? NoSupportProblem(IBlockAccess w, BlockPos target)
    {
        var sides = new List<string>();
        foreach (var face in BlockFaceExtensions.All)
        {
            var n = face.Normal();
            var against = new BlockPos(target.X - n.X, target.Y - n.Y, target.Z - n.Z);
            if (IsSupport(w, against)) return null;
            sides.Add(Name(w, against));
        }
        var kinds = sides.GroupBy(s => s).OrderByDescending(g => g.Count()).Select(g => g.Count() == 6 ? $"all {g.Key}" : $"{g.Count()} {g.Key}");
        return $"{F(target)} has nothing solid on any side to place against ({string.Join(", ", kinds)}); place a block next to it first";
    }

    /// <summary>
    /// A click on a support face the bot can't see (vanilla b1.8.1 never raytraces 0x0F; it only checks distance): the
    /// nearest solid neighbour whose shared face centre is within <paramref name="reach"/> of the eye. Null = none in reach.
    /// The caller still checks the player box (vanilla refuses a block inside an entity).
    /// </summary>
    public static PlaceAim? BlindAim(IBlockAccess w, Vec3d eye, BlockPos target, double reach)
    {
        PlaceAim? best = null;
        foreach (var face in BlockFaceExtensions.All)
        {
            var n = face.Normal();
            var against = new BlockPos(target.X - n.X, target.Y - n.Y, target.Z - n.Z);
            if (!IsSupport(w, against)) continue;
            // Centre of the face shared by `against` and the target.
            var p = new Vec3d(target.X + 0.5 - n.X * 0.5, target.Y + 0.5 - n.Y * 0.5, target.Z + 0.5 - n.Z * 0.5);
            double d = eye.DistanceTo(p);
            if (d > reach || (best is { } b && d >= b.Distance)) continue;
            var (yaw, pitch) = PlayerPhysics.YawPitchTo(eye, p);
            best = new PlaceAim(target, against, face, p, yaw, pitch, d);
        }
        return best;
    }

    /// <summary>Distance from the eye to the nearest support face centre (null = no support at all).</summary>
    public static double? NearestSupportDistance(IBlockAccess w, Vec3d eye, BlockPos target) =>
        BlindAim(w, eye, target, double.MaxValue)?.Distance;

    /// <summary>
    /// Nearest free soil for <paramref name="itemId"/> (a cell whose rules pass and that is empty), within <paramref name="radius"/>
    /// of <paramref name="near"/> on the same and adjacent layers; null = none. For a "try 13,65,0 instead" hint.
    /// </summary>
    public static BlockPos? NearestSoil(IBlockAccess w, BlockPos near, int itemId, int radius = 4)
    {
        BlockPos? best = null;
        int bestD = int.MaxValue;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        for (int dz = -radius; dz <= radius; dz++)
        {
            var c = new BlockPos(near.X + dx, near.Y + dy, near.Z + dz);
            if (c == near || !Aiming.IsReplaceable(w, w.GetBlock(c.X, c.Y, c.Z)) || BlockShapes.IsLiquid(w.GetBlock(c.X, c.Y, c.Z))) continue;
            if (SoilProblem(w, c, itemId) != null) continue;
            int d = dx * dx + dy * dy * 4 + dz * dz;
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    private static readonly BlockPos[] Horizontal = [new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)];

    private static string PlantName(int block) => block switch
    {
        Blocks.Sapling => "sapling",
        Blocks.Dandelion => "dandelion",
        Blocks.Rose => "rose",
        _ => "plant",
    };

    private static string Name(IBlockAccess w, BlockPos p)
    {
        if (!w.IsLoaded(p.X, p.Z)) return "unloaded";
        int id = w.GetBlock(p.X, p.Y, p.Z);
        if (id == 0) return "air";
        if (BlockShapes.IsWater(id)) return "water";
        if (BlockShapes.IsLava(id)) return "lava";
        return w.Info(id).Name;
    }

    private static string F(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
}
