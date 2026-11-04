using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.Blueprints;

public enum StepKind { Clear, Place, Scaffold, RemoveScaffold }

/// <summary>
/// One construction step. <see cref="Look"/> = the way the body must face while placing (stairs, doors, chests, gates take
/// their meta from the placer's yaw per mc-dev); <see cref="Face"/> = the face to click (doors: the top of the block below;
/// ladders: the wall).
/// </summary>
/// <para><see cref="Hidden"/>: the face may be clicked unseen (b1.8.1 checks distance only); <see cref="Yaw"/>: the look yaw to
/// send (facing metas). Both come from the build planner (Bot/Building), which also chose where to stand.</para>
public sealed record BuildStep(StepKind Kind, BlockPos Pos, int Id, int Meta, Dir? Look = null, BlockFace? Face = null, bool Hidden = false, float? Yaw = null)
{
    public (int Item, int Damage) Item => BuildBlocks.ItemFor(Id, Meta);
    public override string ToString() => $"{Kind} {BuildBlocks.Name(Id, Meta)} {Pos.X},{Pos.Y},{Pos.Z}";
}

/// <summary>A body's share of a build: the cells it places and the area it claims (disjoint from every other part).</summary>
public sealed record BuildPart(int Index, int Count, HashSet<BlockPos> Cells, ClaimKey Claim, int AxisLo, int AxisHi, char Axis)
{
    /// <summary>Scaffolding for this part stays inside its strip (the claim), so bodies never work in each other's cells.</summary>
    public bool Allows(BlockPos p) => Coord(p) >= AxisLo && Coord(p) <= AxisHi;
    public int Coord(BlockPos p) => Axis switch { 'x' => p.X, 'z' => p.Z, _ => p.Y };
    public string Label => Count <= 1 ? "whole build" : $"part {Index + 1}/{Count} ({(Axis == 'y' ? "layers" : "strip")} {AxisLo}..{AxisHi})";
    public static BuildPart Whole(VoxelPlan plan, int dim = 0) => new(0, 1, plan.Blocks.Keys.Concat(plan.Air).ToHashSet(),
        ClaimKey.Area(plan.Min.Offset(-3, -1, -3), plan.Max.Offset(3, 1, 3), dim), int.MinValue, int.MaxValue, 'x');
}

/// <summary>The ordered steps for one part, plus cells it couldn't plan (no support reachable within the scaffold limit). The
/// simple layer order; the executor runs the full slicer (<see cref="Building.BuildPlanner"/>) instead.</summary>
public sealed class ConstructionOrder
{
    public List<BuildStep> Steps { get; } = new();
    public List<BlockPos> Unreachable { get; } = new();
    public int Places => Steps.Count(s => s.Kind == StepKind.Place);
    public int Scaffolds => Steps.Count(s => s.Kind == StepKind.Scaffold);
}

/// <summary>
/// Construction order (zero tokens): obstructions cleared top-down first; then layer by layer, bottom-up: structural blocks,
/// then thin/attached blocks and doors, each next to something already standing (support-first, nearest next); a cell with no
/// support gets a temporary scaffold path from the nearest support (self-placed blocks, removable under the protection policy);
/// a cell no standing spot can reach gets a scaffold walkway on the floor first (never a stand over the void); scaffolding comes
/// down at the end, top first.
/// </summary>
public static class ConstructionPlanner
{
    public const int Scaffold = Blocks.Cobblestone; // the executor swaps in whatever clutch block it carries

    public static ConstructionOrder Plan(VoxelPlan plan, IBlockAccess world, BuildPart? part = null, int maxScaffold = 6, double reach = 4.5) =>
        new ConstructionOrderBuilder(plan, world, part ?? BuildPart.Whole(plan), maxScaffold, reach).Build();

    /// <summary>The order of a layer's blocks: structure, thin blocks, doors, then what hangs on walls.</summary>
    internal static int Class(PlannedBlock b) => b.Kind switch
    {
        BuildKind.Solid or BuildKind.Gravity or BuildKind.Slab or BuildKind.Stairs or BuildKind.Station => 0,
        BuildKind.Thin => 1,
        BuildKind.Door => 2,
        _ => 3, // torches, ladders: after the walls they hang on
    };

    internal static BlockPos LadderWall(PlannedBlock b) => b.Meta switch
    {
        2 => b.Pos.Offset(0, 0, 1), 3 => b.Pos.Offset(0, 0, -1), 4 => b.Pos.Offset(1, 0, 0), _ => b.Pos.Offset(-1, 0, 0),
    };

    /// <summary>The placement for a planned block: the direction to face (yaw-set metas) and the face to click.</summary>
    public static BuildStep StepFor(PlannedBlock b) =>
        new(StepKind.Place, b.Pos, b.Id, b.Meta & (b.Kind == BuildKind.Door ? 7 : 15), PlacerLook(b), ClickFace(b));

    /// <summary>The way the placer faces: a gate's way through, a chest/furnace/dispenser front toward the placer, stairs the way
    /// up (walking up = the way the placer faced), a door the way it opens; else any.</summary>
    private static Dir? PlacerLook(PlannedBlock b)
    {
        if (b.Id == Blocks.FenceGate) return GateLook(b.Meta);
        if (b.Id is Blocks.Chest or Blocks.Furnace or Blocks.Dispenser && b.Meta is >= 2 and <= 5) return FrontLook(b.Meta);
        return KindLook(b);
    }

    private static Dir GateLook(int meta) => (meta & 3) switch { 0 => Dir.South, 1 => Dir.West, 2 => Dir.North, _ => Dir.East };

    /// <summary>The front faces the placer.</summary>
    private static Dir FrontLook(int meta) => meta switch { 2 => Dir.South, 3 => Dir.North, 4 => Dir.East, _ => Dir.West };

    private static Dir? KindLook(PlannedBlock b) => b.Kind switch
    {
        BuildKind.Stairs => BuildBlocks.StairsAscend(b.Meta),
        BuildKind.Door => BuildBlocks.DoorPlacerFacing(b.Meta),
        _ => null,
    };

    /// <summary>The face to click: a ladder's wall (its face toward the ladder), a door the top of the block below; else any.</summary>
    private static BlockFace? ClickFace(PlannedBlock b)
    {
        if (b.Id == Blocks.Ladder)
            return b.Meta switch { 2 => BlockFace.North, 3 => BlockFace.South, 4 => BlockFace.West, _ => BlockFace.East }; // the wall's face toward the ladder
        return b.Kind == BuildKind.Door ? BlockFace.Top : null;
    }

    // ------------------------------------------------------------------ split across bodies

    /// <summary>
    /// Splits the plan into <paramref name="bodies"/> disjoint strips along its longer horizontal axis, balanced by block count
    /// (doors stay whole: a column never splits). Each strip's claim area covers its full height; the outer strips extend 3
    /// blocks past the footprint for scaffolding. Areas never overlap, so bodies build at the same time without conflicts.
    /// </summary>
    public static List<BuildPart> Split(VoxelPlan plan, int bodies, int dim = 0)
    {
        bodies = Math.Max(1, bodies);
        bool alongX = plan.Max.X - plan.Min.X >= plan.Max.Z - plan.Min.Z;
        int lo = alongX ? plan.Min.X : plan.Min.Z, hi = alongX ? plan.Max.X : plan.Max.Z;
        bodies = Math.Min(bodies, hi - lo + 1);
        var perSlice = new int[hi - lo + 1];
        foreach (var b in plan.Blocks.Keys) perSlice[(alongX ? b.X : b.Z) - lo]++;
        var bounds = StripBounds(perSlice, lo, hi, bodies);
        var parts = new List<BuildPart>();
        for (int i = 0; i < bounds.Count; i++) parts.Add(Strip(plan, bounds, i, alongX, dim));
        return parts;
    }

    /// <summary>The strips' slice ranges: each closes once it holds its share of blocks (or only as many slices are left as
    /// strips), never leaving a later strip without a slice.</summary>
    private static List<(int A, int B)> StripBounds(int[] perSlice, int lo, int hi, int bodies)
    {
        int total = perSlice.Sum(), target = (total + bodies - 1) / bodies;
        var bounds = new List<(int A, int B)>();
        int start = lo, acc = 0;
        for (int s = lo; s <= hi; s++)
        {
            acc += perSlice[s - lo];
            int remainingSlices = hi - s, remainingParts = bodies - bounds.Count - 1;
            if (bounds.Count >= bodies - 1 || acc < target && remainingSlices != remainingParts || remainingSlices < remainingParts || remainingParts <= 0) continue;
            bounds.Add((start, s));
            start = s + 1;
            acc = 0;
        }
        bounds.Add((start, hi));
        return bounds;
    }

    /// <summary>Strip <paramref name="i"/>: its cells, and a full-height claim (the outer strips 3 past the footprint).</summary>
    private static BuildPart Strip(VoxelPlan plan, List<(int A, int B)> bounds, int i, bool alongX, int dim)
    {
        var (a, b) = bounds[i];
        int axisLo = i == 0 ? a - 3 : a, axisHi = i == bounds.Count - 1 ? b + 3 : b;
        var cells = plan.Blocks.Keys.Concat(plan.Air).Where(p => (alongX ? p.X : p.Z) >= a && (alongX ? p.X : p.Z) <= b).ToHashSet();
        var min = alongX ? new BlockPos(axisLo, plan.Min.Y - 1, plan.Min.Z - 3) : new BlockPos(plan.Min.X - 3, plan.Min.Y - 1, axisLo);
        var max = alongX ? new BlockPos(axisHi, plan.Max.Y + 1, plan.Max.Z + 3) : new BlockPos(plan.Max.X + 3, plan.Max.Y + 1, axisHi);
        return new BuildPart(i, bounds.Count, cells, ClaimKey.Area(min, max, dim), axisLo, axisHi, alongX ? 'x' : 'z');
    }

    /// <summary>
    /// Splits by layer bands (bottom-up, balanced by block count): part k+1 stands on part k, so these run one after another
    /// (a crew script hands each body the next band); claims are full-footprint boxes over disjoint y ranges.
    /// </summary>
    public static List<BuildPart> SplitLayers(VoxelPlan plan, int parts, int dim = 0)
    {
        int lo = plan.Min.Y, hi = plan.Max.Y;
        parts = Math.Clamp(parts, 1, hi - lo + 1);
        var per = new int[hi - lo + 1];
        foreach (var b in plan.Blocks.Keys) per[b.Y - lo]++;
        int target = (per.Sum() + parts - 1) / parts;
        var bounds = new List<(int A, int B)>();
        int start = lo, acc = 0;
        for (int y = lo; y <= hi; y++)
        {
            acc += per[y - lo];
            int remainingLayers = hi - y, remainingParts = parts - bounds.Count - 1;
            if (remainingParts > 0 && remainingLayers >= remainingParts && (acc >= target || remainingLayers == remainingParts))
            {
                bounds.Add((start, y));
                start = y + 1;
                acc = 0;
            }
        }
        bounds.Add((start, hi));
        return bounds.Select((b, i) =>
        {
            int a = i == 0 ? b.A - 1 : b.A, z = i == bounds.Count - 1 ? b.B + 1 : b.B;
            var cells = plan.Blocks.Keys.Concat(plan.Air).Where(p => p.Y >= b.A && p.Y <= b.B).ToHashSet();
            var claim = ClaimKey.Area(new BlockPos(plan.Min.X - 3, a, plan.Min.Z - 3), new BlockPos(plan.Max.X + 3, z, plan.Max.Z + 3), dim);
            return new BuildPart(i, bounds.Count, cells, claim, a, z, 'y');
        }).ToList();
    }
}
