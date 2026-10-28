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
        var turned = new Blueprint { Name = bp.Name, W = bp.W, H = bp.H, D = bp.D, Facing = bp.Facing.Turn(q), Light = bp.Light, Source = bp.Source };
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
    public static int Mirror(int id, int meta, char axis)
    {
        Dir M(Dir d) => axis == 'x' ? d switch { Dir.North => Dir.South, Dir.South => Dir.North, _ => d } : d switch { Dir.East => Dir.West, Dir.West => Dir.East, _ => d };
        switch (BuildBlocks.KindOf(id))
        {
            case BuildKind.Stairs: return BuildBlocks.StairsMeta(M(BuildBlocks.StairsAscend(meta)));
            case BuildKind.Door: return BuildBlocks.DoorFacingMeta(M(BuildBlocks.DoorPlacerFacing(meta))) | (meta & 8) | ((meta & 4) ^ ((meta & 8) == 0 ? 4 : 0));
        }
        if (id == Blocks.Ladder && meta is >= 2 and <= 5) return BuildBlocks.LadderMeta(M(((Dir[])[Dir.South, Dir.North, Dir.East, Dir.West])[meta - 2]));
        if (id is Blocks.Chest or Blocks.Furnace or Blocks.Dispenser && meta is >= 2 and <= 5)
            return BuildBlocks.FrontMeta(M(((Dir[])[Dir.North, Dir.South, Dir.West, Dir.East])[meta - 2]));
        if (id == Blocks.FenceGate) return BuildBlocks.GateMeta(M(((Dir[])[Dir.South, Dir.West, Dir.North, Dir.East])[meta & 3])) | (meta & 4);
        if (id == Blocks.Torch) return axis == 'x' ? meta switch { 3 => 4, 4 => 3, _ => meta } : meta switch { 1 => 2, 2 => 1, _ => meta };
        return meta;
    }
}
