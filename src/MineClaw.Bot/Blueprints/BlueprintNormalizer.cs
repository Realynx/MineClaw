using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// Blueprint → voxels: layers and primitives in the order written (later wins) on a canonical W×H×D grid, door halves derived
/// (lower = facing meta, upper = lower | 8, per mc-dev ItemDoor), rotated to the blueprint's facing (metas too) and placed at
/// the origin. Structural validation (world-independent) happens here; site checks are <see cref="DesignChecks"/>.
/// </summary>
public static partial class BlueprintNormalizer
{
    /// <summary>Blocks per design: a base-63 stepped pyramid is 43,680 blocks; designs past <see cref="BlueprintOptions.SectionBlocks"/>
    /// are built band by band, so slicing never takes on more than a band at once.</summary>
    public const int MaxBlocks = 50_000;
    private const char Air = '.', Keep = '_';

    /// <param name="worldHeight">The world's height (<see cref="IBlockAccess.WorldHeight"/>): b1.8.1 128, 1.12.2 256.</param>
    public static (VoxelPlan? Plan, List<BlueprintIssue> Issues) Normalize(Blueprint bp, BlockPos? defaultOrigin = null, int worldHeight = ChunkColumn.SizeY)
    {
        // A versioned design normalizes in its own version (its ids, its metas, its rules).
        using var scope = DesignVersions.Use(bp.Version);
        var issues = new List<BlueprintIssue>();
        if (bp.W < 1 || bp.H < 1 || bp.D < 1) { issues.Add(new(IssueLevel.Error, "size", "no valid size")); return (null, issues); }
        var canvas = new Canvas(bp, issues);
        canvas.Draw();
        canvas.ReportUnusedPalette();
        var states = canvas.States();
        canvas.CheckDoors(states);
        canvas.CheckSlabs(states);
        canvas.CheckMetas(states);

        // The drawn front. Canonically the front is the south row and `facing` turns the drawing (facing north = a 180° turn: local
        // z=0 lands on the world's south side, x flips too). An author who drew every door on the very side `facing` names (live
        // design #8: `facing north`, `door D 3,0,0 north`) drew the build as it stands: turning it would put the door on the back,
        // so such a drawing is kept as drawn.
        bp.DrawnFront = DrawnFront(bp, states, bp.W, bp.D);
        if (bp.DrawnFront != Dir.South)
            issues.Add(new(IssueLevel.Info, "facing", $"door(s) drawn on the {bp.Facing.Name()} side: built as drawn (facing {bp.Facing.Name()} doesn't turn it)"));
        canvas.CheckBlockCount(states);

        var plan = canvas.Place(states, bp.Origin ?? defaultOrigin ?? new BlockPos(0, 64, 0), worldHeight);
        return (issues.Any(i => i.Level == IssueLevel.Error) && states.Count == 0 && plan.Air.Count == 0 ? null : plan, issues);
    }

    /// <summary>
    /// Which side of the drawing is the front: <see cref="Dir.South"/> (canonical: <c>facing</c> turns it) unless every door on the
    /// drawing's edge is drawn on the side <c>facing</c> names (and none on the south edge), then that side (no turn).
    /// </summary>
    internal static Dir DrawnFront(Blueprint bp, Dictionary<(int X, int Y, int Z), (int Id, int Meta)> states, int W, int D)
    {
        if (bp.Facing == Dir.South) return Dir.South;
        bool any = false;
        foreach (var ((x, _, z), (id, meta)) in states)
        {
            if (BuildBlocks.KindOf(id) != BuildKind.Door || (meta & 8) != 0) continue;
            var sides = EdgeSides(x, z, W, D);
            if (sides.Count == 0) continue; // an inner door says nothing about the front
            if (!sides.Contains(bp.Facing) || sides.Contains(Dir.South)) return Dir.South;
            any = true;
        }
        return any ? bp.Facing : Dir.South;
    }

    /// <summary>The drawing's edges a cell lies on (none for an inner cell, two at a corner).</summary>
    private static List<Dir> EdgeSides(int x, int z, int W, int D)
    {
        var sides = new List<Dir>(2);
        if (z == 0) sides.Add(Dir.North);
        if (z == D - 1) sides.Add(Dir.South);
        if (x == 0) sides.Add(Dir.West);
        if (x == W - 1) sides.Add(Dir.East);
        return sides;
    }

    private static (int X, int Y, int Z) Min((int X, int Y, int Z) a, (int X, int Y, int Z) b) => (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    private static (int X, int Y, int Z) Max((int X, int Y, int Z) a, (int X, int Y, int Z) b) => (Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>Parse + normalize in one go (tests, the design loop).</summary>
    public static (VoxelPlan? Plan, List<BlueprintIssue> Issues) FromText(string dsl, BlockPos? defaultOrigin = null, int worldHeight = ChunkColumn.SizeY)
    {
        var (bp, issues) = BlueprintParser.Parse(dsl);
        if (issues.Any(i => i.Level == IssueLevel.Error && i.Code is "size")) return (null, issues);
        var (plan, more) = Normalize(bp, defaultOrigin, worldHeight);
        issues.AddRange(more);
        return (plan, issues);
    }
}
