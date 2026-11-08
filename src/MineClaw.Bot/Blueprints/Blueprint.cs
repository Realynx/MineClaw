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
    /// <summary>
    /// Layers dug into the ground (<c>ground N</c>, 0 = none): layer N is the first one above the site's surface, layers 0..N-1
    /// are below it (a basement, a cellar, a foundation). A site places layer N on the ground block's y + 1, so the design's
    /// '.' cells under that are dug out of the terrain and its blocks there replace it.
    /// </summary>
    public int Ground { get; set; }
    /// <summary>The game version the design is drawn for (<c>version X</c>; <see cref="DesignVersions"/>): its palette resolves in
    /// that version's names and may hold any of its design blocks. Null = the server's (a legacy DSL: buildable blocks only).</summary>
    public string? Version { get; set; }
    /// <summary><c>kind mine</c>: a mine design (docs/web/api.md "Mine designs"): mostly cells to dig (its forced air), anchored to
    /// absolute world y (<see cref="AbsoluteY"/>) instead of a site's surface; else "build".</summary>
    public string Kind { get; set; } = "build";
    public bool Mine => Kind == "mine";
    /// <summary><c>anchor absolute X,Y,Z</c>: the world y of layer 0 (ore heights matter, so a placed mine keeps it); X,Z = where its
    /// min corner sat on the Design Studio's mine plot (its terrain preview). Null = surface-relative (<see cref="Ground"/>).</summary>
    public int? AbsoluteY { get; set; }
    public (int X, int Z)? PlotAt { get; set; }
    /// <summary><c>entry x,y,z</c> (local): a mine's top, where its shaft meets the surface; a placement puts this column on the
    /// clicked one.</summary>
    public (int X, int Y, int Z)? Entry { get; set; }
    /// <summary><c>seed S</c>: the studio terrain a mine was drawn on.</summary>
    public string? Seed { get; set; }
    /// <summary><c>preferred_for V</c>: a version-preferred mine design (the strip mine that suits that version's ore heights).</summary>
    public string? PreferredFor { get; set; }
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

    /// <summary>The origin (min corner) that stands this design centred on <paramref name="ground"/> (the site's ground block):
    /// layer <see cref="Ground"/> one above it, the dug layers below.</summary>
    public BlockPos OriginOn(BlockPos ground)
    {
        // A mine keeps its absolute y; its entry's column (unturned) goes on the ground's.
        if (AbsoluteY is { } ay)
            return Entry is { } e && Quarters == 0 ? new(ground.X - e.X, ay, ground.Z - e.Z) : new(ground.X - Footprint.Wx / 2, ay, ground.Z - Footprint.Dz / 2);
        return new(ground.X - Footprint.Wx / 2, ground.Y + 1 - Ground, ground.Z - Footprint.Dz / 2);
    }
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
    /// <summary>Design cells the survival builder can't place yet (not <see cref="BuildBlocks"/> states: beds, redstone, upside-down
    /// stairs…), taken out of <see cref="Blocks"/> for a build and reported, never placed wrong (<see cref="BlueprintService.PlanOf"/>).</summary>
    public Dictionary<BlockPos, (int Id, int Meta)> Unsupported { get; } = new();
    /// <summary>"1.12.2 → b1.8.1: 120 the same; 20 Spruce Planks → Wooden Planks" when the design was converted for this world.</summary>
    public string? Converted { get; set; }
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

    /// <summary>
    /// Bottom-up layer bands (world y, inclusive) of at most <paramref name="maxBlocks"/> blocks each (a layer bigger than that is
    /// a band by itself): a massive design is built band by band (<see cref="CrewExecutor"/>), each band a plan of its own, so
    /// slicing, persisting and re-planning stay the size of one band. One band when the whole design fits.
    /// </summary>
    public List<(int Lo, int Hi)> Bands(int maxBlocks)
    {
        if (maxBlocks <= 0 || Blocks.Count <= maxBlocks) return [(Min.Y, Max.Y)];
        var per = Blocks.Keys.GroupBy(p => p.Y).ToDictionary(g => g.Key, g => g.Count());
        var bands = new List<(int, int)>();
        int lo = Min.Y, acc = 0;
        for (int y = Min.Y; y <= Max.Y; y++)
        {
            int n = per.GetValueOrDefault(y);
            if (acc > 0 && acc + n > maxBlocks) { bands.Add((lo, y - 1)); lo = y; acc = 0; }
            acc += n;
        }
        bands.Add((lo, Max.Y));
        return bands;
    }

    /// <summary>The part of this design in layers <paramref name="lo"/>..<paramref name="hi"/> (world y): its blocks and its air
    /// cells there, the same blueprint and origin (so the box, the footprint and the front stay the design's).</summary>
    public VoxelPlan Band(int lo, int hi)
    {
        if (lo <= Min.Y && hi >= Max.Y) return this;
        var band = new VoxelPlan { Blueprint = Blueprint, Origin = Origin };
        foreach (var (p, b) in Blocks) if (p.Y >= lo && p.Y <= hi) band.Blocks[p] = b;
        foreach (var p in Air) if (p.Y >= lo && p.Y <= hi) band.Air.Add(p);
        return band;
    }

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
