using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Survival;

/// <summary>One placement of the box: <see cref="Target"/> by clicking <see cref="Face"/> of the block at Target − normal(Face).</summary>
public readonly record struct ShelterPlace(BlockPos Target, BlockFace Face)
{
    public BlockPos Against => Target - Face.Normal();
}

/// <summary>
/// The no-path danger reflex's box (pure; live 10-28 16:20: seven zombies on the drop tower's platform, no way down, "retreating
/// inward" went nowhere and the body died): when retreat has no path home and health is low or the mobs outnumber it, the body
/// encloses its own 1×2 column from where it stands. Four sides at feet and head, the cell overhead if open; a side with
/// nothing to click gets a support under it first (the side face of my own floor block), the roof a cap beside it (on top of
/// a head-level wall). It never digs, never moves, never places into its own column, so it can't fall: the floor stays.
/// Blocks: cobble first, planks, stone, then other plain full blocks, dirt last; never saplings, falling or interactable blocks.
/// </summary>
public static class Shelter
{
    private static readonly BlockFace[] Sides = [BlockFace.North, BlockFace.East, BlockFace.South, BlockFace.West];

    /// <summary>The column is closed: all four sides at feet and head, and overhead.</summary>
    public static bool Enclosed(IBlockAccess w, BlockPos feet) => Open(w, feet).Count == 0;

    /// <summary>The cells of the box still open (feet sides, head sides, overhead).</summary>
    public static List<BlockPos> Open(IBlockAccess w, BlockPos feet)
    {
        var open = new List<BlockPos>();
        foreach (var f in Sides) if (!Solid(w, feet + f.Normal())) open.Add(feet + f.Normal());
        foreach (var f in Sides) if (!Solid(w, feet.Up() + f.Normal())) open.Add(feet.Up() + f.Normal());
        if (!Solid(w, feet.Up().Up())) open.Add(feet.Up().Up());
        return open;
    }

    /// <summary>The placements, in an order where each one clicks a block already there (or placed before it).</summary>
    public static List<ShelterPlace> Plan(IBlockAccess w, BlockPos feet)
    {
        var placed = new HashSet<BlockPos>();
        var plan = new List<ShelterPlace>();
        bool S(BlockPos p) => placed.Contains(p) || Solid(w, p);
        var body = new HashSet<BlockPos> { feet, feet.Up() };
        bool Try(BlockPos target)
        {
            if (S(target) || body.Contains(target) || !Replaceable(w, target)) return S(target);
            foreach (var face in BlockFaceExtensions.All)
            {
                var against = target - face.Normal();
                if (body.Contains(against) || !S(against) || !Clickable(w, against, placed)) continue;
                plan.Add(new ShelterPlace(target, face));
                placed.Add(target);
                return true;
            }
            return false;
        }

        // A cap beside the roof cell, on top of a head-level wall, when the roof has nothing to click yet.
        void Roof(List<BlockPos> headSides)
        {
            var roof = feet.Up().Up();
            if (Try(roof)) return;
            foreach (var c in headSides.Where(S))
            {
                var cap = c.Up();
                if (!Replaceable(w, cap) && !S(cap)) continue;
                if (Try(cap) && Try(roof)) break;
            }
        }

        var feetSides = Sides.Select(f => feet + f.Normal()).ToList();
        var headSides = Sides.Select(f => feet.Up() + f.Normal()).ToList();
        foreach (var c in feetSides)
            if (!Try(c) && Replaceable(w, c.Down()) && Solid(w, feet.Down()))
            {
                // Over an edge: a support under the side cell first, against the side of my own floor block.
                var support = c.Down();
                plan.Add(new ShelterPlace(support, FaceFrom(feet.Down(), support)));
                placed.Add(support);
                Try(c);
            }
        foreach (var c in headSides) Try(c);
        Roof(headSides);
        return plan;
    }

    /// <summary>Preference of a block for the box (lower is better), or −1 when it must not be used.</summary>
    public static int BlockRank(int id)
    {
        if (id <= 0 || id >= 256) return -1;
        if (NeverInBox.Contains(id)) return -1;
        if (UnfitForBox(Blocks.Get(id))) return -1;
        return BoxRanks.TryGetValue(id, out int rank) ? rank : 6;
    }

    /// <summary>Blocks that fall, blow up, grow or let mobs see through: never a box wall.</summary>
    private static readonly HashSet<int> NeverInBox = [Blocks.Sapling, Blocks.Sand, Blocks.Gravel, Blocks.Tnt, Blocks.Leaves];

    /// <summary>The box's favourites (lower is better); any other fit block ranks 6.</summary>
    private static readonly Dictionary<int, int> BoxRanks = new()
    {
        [Blocks.Cobblestone] = 0, [Blocks.Planks] = 1, [Blocks.Stone] = 2, [Blocks.Netherrack] = 3, [Blocks.Log] = 5,
        [Blocks.Dirt] = 9, [Blocks.Grass] = 9, // dirt last
    };

    /// <summary>Not a full, solid, inert, opaque block.</summary>
    private static bool UnfitForBox(BlockInfo b) =>
        !b.Solid || b.CollisionHeight < 1 || b.Interactable || b.Hazard || b.Liquid || b.Transparent;

    private static BlockFace FaceFrom(BlockPos against, BlockPos target)
    {
        var d = target - against;
        foreach (var f in BlockFaceExtensions.All) if (f.Normal() == d) return f;
        return BlockFace.Top;
    }

    private static bool Solid(IBlockAccess w, BlockPos p) => BlockShapes.HasCollision(w, p.X, p.Y, p.Z) && !BlockShapes.IsLiquid(w.GetBlock(p.X, p.Y, p.Z));

    private static bool Replaceable(IBlockAccess w, BlockPos p) => Aiming.IsReplaceable(w, w.GetBlock(p.X, p.Y, p.Z));

    /// <summary>A block a click puts the new block against (not a chest/door/crafting table that would open instead).</summary>
    private static bool Clickable(IBlockAccess w, BlockPos p, HashSet<BlockPos> placed) =>
        placed.Contains(p) || !Blocks.Get(w.GetBlock(p.X, p.Y, p.Z)).Interactable;
}
