using System.Globalization;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>One palette character: a block state, optionally with a facing (applied through <see cref="BuildBlocks.MetaFor"/>).</summary>
public sealed record PaletteEntry(char Symbol, int Id, int Meta, Dir? Facing, string Source)
{
    /// <summary>Meta in the canonical (unrotated) frame.</summary>
    public int CanonicalMeta => Facing is { } d && BuildBlocks.MetaFor(Id, d) is { } m ? m : Meta;
}

/// <summary>A drawing command applied after (or between) layers. Coordinates are local: 0,0,0 = west-north-bottom corner.</summary>
public sealed record BlueprintOp(string Kind, char Symbol, (int X, int Y, int Z) A, (int X, int Y, int Z) B, Dir? Facing, string Arg, int Line);

/// <summary>Severity of a finding: an error blocks the version from becoming a candidate.</summary>
public enum IssueLevel { Error, Warning, Info }

public sealed record BlueprintIssue(IssueLevel Level, string Code, string Text, int Line = 0)
{
    public override string ToString() => (Line > 0 ? $"L{Line} " : "") + Text;
}

/// <summary>
/// A parsed blueprint (the DSL, <see cref="BlueprintParser"/>): palette, size W×H×D (x, y, z), an optional world origin (min
/// corner), a facing (the front: canonical drawings have their front on the SOUTH side, i.e. the bottom row of each layer;
/// <c>facing east</c> turns the whole build so that front faces east), layers bottom-up as rows (north row first) and primitives.
/// </summary>
public sealed class Blueprint
{
    public string Name { get; set; } = "build";
    public int W { get; set; }
    public int H { get; set; }
    public int D { get; set; }
    public BlockPos? Origin { get; set; }
    public Dir Facing { get; set; } = Dir.South;
    public bool Light { get; set; }
    public Dictionary<char, PaletteEntry> Palette { get; } = new();
    /// <summary>Layer index → rows (north first), each W chars.</summary>
    public SortedDictionary<int, (List<string> Rows, int Line)> Layers { get; } = new();
    public List<BlueprintOp> Ops { get; } = new();
    /// <summary>Order in which layers and ops were written (later wins): ("layer", y) or ("op", index).</summary>
    public List<(string What, int Index)> Order { get; } = new();
    public string Source { get; set; } = "";

    /// <summary>
    /// The side of the drawing that is the front: south (canonical) unless the doors are already drawn on the <see cref="Facing"/>
    /// side, then that side and the drawing isn't turned (set by <see cref="BlueprintNormalizer"/>, also run at parse time).
    /// </summary>
    public Dir DrawnFront { get; set; } = Dir.South;

    /// <summary>
    /// Clockwise quarter turns from the drawn front to <see cref="Facing"/>: 0 for a south-front drawing facing south, 2 (a z-flip
    /// and an x-flip: local z=0 ends up on the world's south side) for one facing north, 0 when the front is drawn where it faces.
    /// </summary>
    public int Quarters => (((int)Facing - (int)DrawnFront) % 4 + 4) % 4;

    /// <summary>World footprint after rotation (x extent, z extent).</summary>
    public (int Wx, int Dz) Footprint => Quarters % 2 == 0 ? (W, D) : (D, W);

    public string SizeText => string.Create(CultureInfo.InvariantCulture, $"{Footprint.Wx}×{Footprint.Dz}×{H}");
}

/// <summary>A block of the normalized plan, in world coordinates, with its meta already rotated.</summary>
public sealed record PlannedBlock(BlockPos Pos, int Id, int Meta)
{
    public BuildKind Kind => BuildBlocks.KindOf(Id);
    public bool UpperDoor => Kind == BuildKind.Door && (Meta & 8) != 0;
    public override string ToString() => $"{BuildBlocks.Name(Id, Meta)}@{Pos.X},{Pos.Y},{Pos.Z}";
}

/// <summary>
/// The voxels of a blueprint at a site: every block to place (doors as two halves), the cells that must end up air (interior,
/// doorways), and the bounding box. Produced by <see cref="BlueprintNormalizer"/>.
/// </summary>
public sealed class VoxelPlan
{
    public required Blueprint Blueprint { get; init; }
    public required BlockPos Origin { get; init; }
    public Dictionary<BlockPos, PlannedBlock> Blocks { get; } = new();
    public HashSet<BlockPos> Air { get; } = new();
    public BlockPos Min => Origin;
    public BlockPos Max => new(Origin.X + Blueprint.Footprint.Wx - 1, Origin.Y + Blueprint.H - 1, Origin.Z + Blueprint.Footprint.Dz - 1);
    public bool Inside(BlockPos p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;
    public (int X, int Y, int Z) Center => ((Min.X + Max.X) / 2, Min.Y, (Min.Z + Max.Z) / 2);

    /// <summary>World → the blueprint's own (unrotated) coordinates, so findings point at what the author wrote.</summary>
    public (int X, int Y, int Z) ToLocal(BlockPos p)
    {
        int wx = p.X - Origin.X, wz = p.Z - Origin.Z, W = Blueprint.W, D = Blueprint.D;
        var (x, z) = Blueprint.Quarters switch
        {
            1 => (wz, D - 1 - wx),
            2 => (W - 1 - wx, D - 1 - wz),
            3 => (W - 1 - wz, wx),
            _ => (wx, wz),
        };
        return (x, p.Y - Origin.Y, z);
    }

    public string Local(BlockPos p)
    {
        var (x, y, z) = ToLocal(p);
        return $"{x},{y},{z}";
    }

    /// <summary>The block that will stand at <paramref name="p"/> when built: planned, forced air, or what is there now.</summary>
    public int Final(IBlockAccess w, BlockPos p) => Blocks.TryGetValue(p, out var b) ? b.Id : Air.Contains(p) ? 0 : w.GetBlock(p.X, p.Y, p.Z);

    public int FinalMeta(IBlockAccess w, BlockPos p) => Blocks.TryGetValue(p, out var b) ? b.Meta : Air.Contains(p) ? 0 : w.GetMeta(p.X, p.Y, p.Z);

    /// <summary>
    /// <paramref name="n"/> disjoint parts for <paramref name="n"/> bodies: vertical strips along the longer side (built at the same
    /// time) or, with <paramref name="byLayer"/>, bottom-up layer bands (built in order). Each carries its <c>ClaimKey.Area</c>.
    /// </summary>
    public List<BuildPart> Partition(int n, bool byLayer = false, int dim = 0) =>
        byLayer ? ConstructionPlanner.SplitLayers(this, n, dim) : ConstructionPlanner.Split(this, n, dim);

    /// <summary>Layers (local y) that place blocks.</summary>
    public int LayerCount => Blueprint.H;

    /// <summary>Stable content hash (render/critique cache key, change detection).</summary>
    public ulong Hash()
    {
        ulong h = 14695981039346656037UL;
        unchecked
        {
            foreach (var b in Blocks.Values.OrderBy(b => b.Pos.Y).ThenBy(b => b.Pos.Z).ThenBy(b => b.Pos.X))
                foreach (int v in new[] { b.Pos.X, b.Pos.Y, b.Pos.Z, b.Id, b.Meta }) h = (h ^ (uint)v) * 1099511628211UL;
            foreach (var a in Air.OrderBy(p => p.Y).ThenBy(p => p.Z).ThenBy(p => p.X))
                foreach (int v in new[] { a.X, a.Y, a.Z, -1 }) h = (h ^ (uint)v) * 1099511628211UL;
        }
        return h;
    }
}
