using MineClaw.Bot.Blueprints;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>
/// Applies a gate correction to a voxel plan: clockwise quarter turns about the anchor (the first gate block, so the build
/// keeps starting where it started), a mirror across the main axis, a shift. Directional metas turn/mirror with the cells
/// (stairs, doors incl. the hinge, ladders, chests/furnaces/dispensers, gates, torches), per the mc-dev meta tables in
/// <see cref="BuildBlocks"/>.
/// </summary>
public static class PlanTransform
{
    public static VoxelPlan Apply(VoxelPlan plan, BuildTransform t)
    {
        if (t.IsNone) return plan;
        int q = ((t.Quarters % 4) + 4) % 4;
        var a = t.Anchor;
        BlockPos Map(BlockPos p)
        {
            int dx = p.X - a.X, dz = p.Z - a.Z;
            if (t.Mirror) { if (t.Axis == 'x') dz = -dz; else dx = -dx; }
            for (int i = 0; i < q; i++) (dx, dz) = (-dz, dx); // clockwise seen from above (north up): N→E→S→W
            return new BlockPos(a.X + dx + t.Dx, p.Y + t.Dy, a.Z + dz + t.Dz);
        }
        var bp = plan.Blueprint;
        var turned = new Blueprint { Name = bp.Name, W = bp.W, H = bp.H, D = bp.D, Facing = bp.Facing.Turn(q), DrawnFront = bp.DrawnFront, Light = bp.Light, Ground = bp.Ground, Source = bp.Source };
        var c1 = Map(plan.Min);
        var c2 = Map(plan.Max);
        var origin = new BlockPos(Math.Min(c1.X, c2.X), Math.Min(c1.Y, c2.Y), Math.Min(c1.Z, c2.Z));
        turned.Origin = origin;
        var outPlan = new VoxelPlan { Blueprint = turned, Origin = origin };
        foreach (var b in plan.Blocks.Values)
        {
            int meta = t.Mirror ? Mirror(b.Id, b.Meta, t.Axis) : b.Meta;
            meta = BuildBlocks.Rotate(b.Id, meta, q);
            var p = Map(b.Pos);
            outPlan.Blocks[p] = new PlannedBlock(p, b.Id, meta);
        }
        foreach (var p in plan.Air) outPlan.Air.Add(Map(p));
        return outPlan;
    }

    /// <summary>A directional meta mirrored across the main axis (<paramref name="axis"/> 'x': north↔south; 'z': east↔west).</summary>
    public static int Mirror(int id, int meta, char axis) => BuildBlocks.KindOf(id) switch
    {
        BuildKind.Stairs => BuildBlocks.StairsMeta(Flip(BuildBlocks.StairsAscend(meta), axis)),
        // The hinge flips with the facing (a lower half's hinge bit toggles; an upper half keeps its own).
        BuildKind.Door => BuildBlocks.DoorFacingMeta(Flip(BuildBlocks.DoorPlacerFacing(meta), axis)) | (meta & 8) | ((meta & 4) ^ ((meta & 8) == 0 ? 4 : 0)),
        // Every other design block: the design rules (axis 'x' here flips north↔south, the design rules' 'z').
        _ => MirrorBuilt(id, meta, axis) ?? DesignFacing.Mirror(id, meta, axis == 'x' ? 'z' : 'x'),
    };

    /// <summary>The builder's own directional blocks: ladders, chests/furnaces/dispensers, gates, torches (null = none of them).</summary>
    private static int? MirrorBuilt(int id, int meta, char axis)
    {
        if (id == Blocks.Ladder && meta is >= 2 and <= 5) return BuildBlocks.LadderMeta(Flip(BuildBlocks.LadderWalls[meta - 2], axis));
        if (BuildBlocks.IsFronted(id) && meta is >= 2 and <= 5) return BuildBlocks.FrontMeta(Flip(BuildBlocks.FrontSides[meta - 2], axis));
        if (id == Blocks.FenceGate) return BuildBlocks.GateMeta(Flip(BuildBlocks.GateWays[meta & 3], axis)) | (meta & 4);
        if (id == Blocks.Torch) return MirrorTorch(meta, axis);
        return null;
    }

    /// <summary>A torch's wall (1 west, 2 east, 3 north, 4 south) mirrored.</summary>
    private static int MirrorTorch(int meta, char axis) =>
        axis == 'x' ? meta switch { 3 => 4, 4 => 3, _ => meta } : meta switch { 1 => 2, 2 => 1, _ => meta };

    /// <summary>A direction across the axis ('x': north↔south; else east↔west).</summary>
    private static Dir Flip(Dir d, char axis) => axis == 'x'
        ? d switch { Dir.North => Dir.South, Dir.South => Dir.North, _ => d }
        : d switch { Dir.East => Dir.West, Dir.West => Dir.East, _ => d };
}
