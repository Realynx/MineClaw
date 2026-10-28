using System.Globalization;
using System.Text;
using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>What to capture: two corners (inclusive box), or a seed (the player's feet / a pointed block) to flood-fill from.</summary>
public sealed record CaptureRequest
{
    public required string Name { get; init; }
    /// <summary>Live blocks where loaded, remembered elsewhere (<see cref="WorldState.PlanningView"/>); unloaded = unknown.</summary>
    public required IBlockAccess World { get; init; }
    public (BlockPos A, BlockPos B)? Corners { get; init; }
    public BlockPos? Seed { get; init; }
    /// <summary>The floor the build stands on (the y of the block under the player's feet); null = found from the seed.</summary>
    public int? FloorY { get; init; }
    /// <summary>Whether column (x, z) is live (else remembered = stale); null = every known column is live.</summary>
    public Func<int, int, bool>? Live { get; init; }
    /// <summary>Cells never part of a build even when made of building blocks (a cobble generator's walls).</summary>
    public Func<BlockPos, bool>? Exclude { get; init; }
    /// <summary>The flood-fill / box cap per axis.</summary>
    public int MaxSide { get; init; } = StructureCapture.MaxSide;
    /// <summary>Refuse when more than this share of the box is unknown (unloaded chunks).</summary>
    public double MaxUnknown { get; init; } = 0.05;
}

/// <summary>A captured structure: its box, the captured block states, the generated DSL and the round-trip verification.</summary>
public sealed class CaptureResult
{
    public string Name { get; init; } = "";
    public string? Refusal { get; set; }
    public bool Ok => Refusal == null;
    public BlockPos Min { get; set; }
    public BlockPos Max { get; set; }
    public int W => Max.X - Min.X + 1;
    public int H => Max.Y - Min.Y + 1;
    public int D => Max.Z - Min.Z + 1;
    /// <summary>Every captured cell → the state the DSL reproduces (doors: both halves; substitutes applied).</summary>
    public Dictionary<BlockPos, (int Id, int Meta)> Cells { get; } = new();
    /// <summary>Interior cells that are air in the world and drawn '.' (must be air when rebuilt).</summary>
    public HashSet<BlockPos> Air { get; } = new();
    public string Dsl { get; set; } = "";
    /// <summary>The DSL parsed back at <see cref="Min"/> (null when refused before generating).</summary>
    public VoxelPlan? Plan { get; set; }
    /// <summary>Captured cells the parsed DSL reproduces exactly (id and meta).</summary>
    public int Matched { get; set; }
    public List<BlockPos> Mismatched { get; } = new();
    /// <summary>"lit furnace as furnace" → count: captured, but as a buildable stand-in.</summary>
    public SortedDictionary<string, int> Substituted { get; } = new(StringComparer.Ordinal);
    /// <summary>"bed" → count: part of the build but not captured (not buildable in a blueprint).</summary>
    public SortedDictionary<string, int> Skipped { get; } = new(StringComparer.Ordinal);
    /// <summary>Notes for the report ("chest contents not captured").</summary>
    public List<string> Notes { get; } = new();
    public int Unknown { get; set; }
    public int Stale { get; set; }
    public int Volume => W * H * D;
    /// <summary>Blocks to place (doors count once).</summary>
    public int Blocks => Cells.Count(c => !(BuildBlocks.KindOf(c.Value.Id) == BuildKind.Door && (c.Value.Meta & 8) != 0));
    public bool Exact => Plan != null && Mismatched.Count == 0 && Matched == Cells.Count;

    public string Bounds => string.Create(CultureInfo.InvariantCulture, $"x {Min.X}..{Max.X}, y {Min.Y}..{Max.Y}, z {Min.Z}..{Max.Z}");

    /// <summary>"not captured: 1 bed; 2 double stone slab as stone; chest contents not captured; 4 stale cells (remembered chunks)".</summary>
    public string Report()
    {
        var bits = new List<string>();
        if (Skipped.Count > 0) bits.Add("not captured: " + string.Join(", ", Skipped.Select(kv => $"{kv.Value} {kv.Key}")));
        if (Substituted.Count > 0) bits.Add(string.Join(", ", Substituted.Select(kv => $"{kv.Value} {kv.Key}")));
        bits.AddRange(Notes);
        if (Stale > 0) bits.Add($"{Stale} cell{(Stale == 1 ? "" : "s")} from remembered chunks (stale)");
        if (Unknown > 0) bits.Add($"{Unknown} unknown cell{(Unknown == 1 ? "" : "s")} (unloaded)");
        return string.Join("; ", bits);
    }
}

/// <summary>
/// Memorize an existing build (ARCHITECTURE.md "Blueprints", capture): the region (two corners, or a flood fill from the player
/// over connected non-natural blocks above the floor they stand on, plus that floor under the build), the block states read from
/// the world (doors, stairs, slabs, torches, ladders with their metas), a Blueprint DSL that reproduces them exactly (palette of
/// raw metas, <c>facing south</c> = drawn as it stands, <c>origin</c> = the box's min corner), and the voxel-by-voxel check of the
/// DSL parsed back against the world. Read-only: nothing is dug or placed.
/// </summary>
public static class StructureCapture
{
    public const int MaxSide = 32;

    /// <summary>Palette symbols in order of preference after the block's own initial. No 'L'/'l' (a row "LIT"/"light" would
    /// read as the <c>light</c> statement), no '.', '_', '#', '=', ';', spaces, quotes or backticks.</summary>
    private const string Symbols = "ABCDEFGHIJKMNOPQRSTUVWXYZ0123456789abcdefghijkmnopqrstuvwxyz!$%&*+-/<>?@^~|()[]{}";

    public enum CellClass { Air, Natural, Build, Unsupported }

    /// <summary>Natural terrain, plants and fluids: never part of a build (a living tree's logs are checked separately).</summary>
    public static bool IsTerrain(int id) => Protection.IsNaturalFill(id) || id is Data.Blocks.Bedrock or Data.Blocks.GoldOre or Data.Blocks.IronOre
        or Data.Blocks.CoalOre or Data.Blocks.LapisOre or Data.Blocks.DiamondOre or Data.Blocks.RedstoneOre or Data.Blocks.LitRedstoneOre
        or Data.Blocks.Ice or Data.Blocks.Portal or Data.Blocks.Fire or Data.Blocks.MobSpawner or Data.Blocks.Web or Data.Blocks.PistonMoving
        or Data.Blocks.PistonHead or Data.Blocks.MonsterEgg or Data.Blocks.BrownMushroomBlock or Data.Blocks.RedMushroomBlock;

    /// <summary>The scan: classify every cell of the region, generate the DSL, parse it back and verify.</summary>
    public static CaptureResult Capture(CaptureRequest req)
    {
        var res = new CaptureResult { Name = CleanName(req.Name) };
        var w = req.World;
        var trees = new Dictionary<BlockPos, bool>();
        var region = new HashSet<BlockPos>();
        var frontier = new HashSet<BlockPos>(); // flood cells that ran into unloaded chunks
        if (req.Corners is { } c)
        {
            var (lo, hi) = (Lo(c.A, c.B), Hi(c.A, c.B));
            if (Too(lo, hi, req.MaxSide) is { } big) { res.Min = lo; res.Max = hi; res.Refusal = big; return res; }
            for (int y = lo.Y; y <= hi.Y; y++)
                for (int z = lo.Z; z <= hi.Z; z++)
                    for (int x = lo.X; x <= hi.X; x++)
                    {
                        var p = new BlockPos(x, y, z);
                        if (w.IsLoaded(x, z) && Classify(w, p, req.Exclude, trees).Class is CellClass.Build or CellClass.Unsupported) region.Add(p);
                    }
            res.Min = lo;
            res.Max = hi;
        }
        else if (req.Seed is { } seed)
        {
            if (Flood(req, seed, trees, region, frontier) is { } why) { res.Refusal = why; return res; }
            if (region.Count == 0)
            {
                res.Refusal = $"I see no build at {seed.X},{seed.Y},{seed.Z}: only natural blocks around it (stand inside it, point at it, or give two corners)";
                return res;
            }
            res.Min = new BlockPos(region.Min(p => p.X), region.Min(p => p.Y), region.Min(p => p.Z));
            res.Max = new BlockPos(region.Max(p => p.X), region.Max(p => p.Y), region.Max(p => p.Z));
        }
        else
        {
            res.Refusal = "capture needs two corners, a pointed block or where you stand";
            return res;
        }

        // Unknown (unloaded) and stale (remembered) cells of the box, and where the flood ran into unloaded chunks (the build
        // may go on there).
        for (int z = res.Min.Z; z <= res.Max.Z; z++)
            for (int x = res.Min.X; x <= res.Max.X; x++)
            {
                if (!w.IsLoaded(x, z)) res.Unknown += res.H;
                else if (req.Live?.Invoke(x, z) == false) res.Stale += res.H;
            }
        int beyond = frontier.Count(p => p.X < res.Min.X || p.X > res.Max.X || p.Y < res.Min.Y || p.Y > res.Max.Y || p.Z < res.Min.Z || p.Z > res.Max.Z);
        res.Unknown += beyond;
        if (res.Unknown > req.MaxUnknown * (res.Volume + beyond))
        {
            res.Refusal = string.Create(CultureInfo.InvariantCulture,
                $"{res.Unknown} of the {res.Volume + beyond} cells ({100.0 * res.Unknown / (res.Volume + beyond):0}%) at {res.Bounds} aren't loaded: come closer so I can see all of it");
            return res;
        }

        // The states, with stand-ins for what a blueprint can't hold.
        foreach (var p in region.OrderBy(p => p.Y))
        {
            var (cls, id, meta, note) = Classify(w, p, req.Exclude, trees);
            if (cls != CellClass.Build) { if (cls == CellClass.Unsupported) Count(res.Skipped, note ?? Data.Blocks.Get(id).Name.Replace('_', ' ')); continue; }
            if (note != null) Count(res.Substituted, note);
            if (BuildBlocks.KindOf(id) == BuildKind.Door)
            {
                if ((meta & 8) != 0) continue; // the lower half writes both
                var up = p.Up();
                if (up.Y > res.Max.Y || w.GetBlock(up.X, up.Y, up.Z) != id || (w.GetMeta(up.X, up.Y, up.Z) & 8) == 0) { Count(res.Skipped, "half door"); continue; }
                res.Cells[p] = (id, meta & 7);
                res.Cells[up] = (id, (meta & 7) | 8);
                continue;
            }
            res.Cells[p] = (id, meta);
        }
        // An upper door half whose lower half isn't captured (outside the box, or not a door).
        foreach (var p in region)
            if (BuildBlocks.KindOf(w.GetBlock(p.X, p.Y, p.Z)) == BuildKind.Door && (w.GetMeta(p.X, p.Y, p.Z) & 8) != 0 && !res.Cells.ContainsKey(p))
                Count(res.Skipped, "half door");
        if (req.Corners == null) Doorsteps(res, w, req.Exclude, trees, req.MaxSide);
        if (res.Cells.Values.Any(s => s.Id == Data.Blocks.Chest)) res.Notes.Add("chest contents not captured");
        if (res.Cells.Count == 0)
        {
            res.Refusal = $"nothing buildable at {res.Bounds}" + (res.Skipped.Count > 0 ? $" ({res.Report()})" : "");
            return res;
        }
        if (res.Blocks > BlueprintNormalizer.MaxBlocks)
        {
            res.Refusal = $"{res.Blocks} blocks at {res.Bounds}: a design holds at most {BlueprintNormalizer.MaxBlocks} (give two corners around a part of it)";
            return res;
        }

        // Interior air: between the lowest and highest captured block of its column (rooms, doorways under lintels).
        var cols = new Dictionary<(int X, int Z), (int Lo, int Hi)>();
        foreach (var p in res.Cells.Keys)
            cols[(p.X, p.Z)] = cols.TryGetValue((p.X, p.Z), out var r) ? (Math.Min(r.Lo, p.Y), Math.Max(r.Hi, p.Y)) : (p.Y, p.Y);
        foreach (var ((x, z), (lo, hi)) in cols)
            for (int y = lo + 1; y < hi; y++)
                if (!res.Cells.ContainsKey(new BlockPos(x, y, z)) && w.IsLoaded(x, z) && w.GetBlock(x, y, z) == 0) res.Air.Add(new BlockPos(x, y, z));

        res.Dsl = Dsl(res);
        Verify(res, w);
        return res;
    }

    /// <summary>
    /// A door standing on the captured floor opens onto the ground outside at the floor's level. A copy is built ON the new
    /// site's ground (layer 0 one above it), so without that ground cell the door would sit a block above the outside: the
    /// cell in front of each outer door becomes part of the build (natural ground as dirt, a path block as itself).
    /// </summary>
    private static void Doorsteps(CaptureResult res, IBlockAccess w, Func<BlockPos, bool>? exclude, Dictionary<BlockPos, bool> trees, int maxSide)
    {
        var roofed = new Dictionary<(int X, int Z), int>();
        foreach (var p in res.Cells.Keys) roofed[(p.X, p.Z)] = Math.Max(roofed.GetValueOrDefault((p.X, p.Z), int.MinValue), p.Y);
        int added = 0;
        foreach (var (p, (id, meta)) in res.Cells.ToList())
        {
            if (BuildBlocks.KindOf(id) != BuildKind.Door || (meta & 8) != 0 || p.Y - 1 < res.Min.Y) continue;
            var (dx, dz) = BuildBlocks.DoorPlacerFacing(meta).Offset();
            foreach (int sign in new[] { 1, -1 })
            {
                var front = p.Offset(dx * sign, 0, dz * sign);
                var step = front.Down();
                // Outside: open, nothing of the build over it (inside cells are under the roof).
                if (res.Cells.ContainsKey(front) || res.Cells.ContainsKey(step) || roofed.GetValueOrDefault((front.X, front.Z), int.MinValue) > front.Y
                    || !w.IsLoaded(step.X, step.Z) || w.GetBlock(front.X, front.Y, front.Z) is var f && f != 0 && Data.Blocks.Get(f).Solid) continue;
                var (cls, sid, smeta, _) = Classify(w, step, exclude, trees);
                int ground = w.GetBlock(step.X, step.Y, step.Z);
                (int Id, int Meta)? state = cls == CellClass.Build ? (sid, smeta)
                    : cls == CellClass.Natural && Data.Blocks.Get(ground).Solid && !Data.Blocks.IsWater(ground) && !Data.Blocks.IsLava(ground) ? (Data.Blocks.Dirt, 0) : null;
                if (state is not { } s) continue;
                var lo = new BlockPos(Math.Min(res.Min.X, step.X), res.Min.Y, Math.Min(res.Min.Z, step.Z));
                var hi = new BlockPos(Math.Max(res.Max.X, step.X), res.Max.Y, Math.Max(res.Max.Z, step.Z));
                if (hi.X - lo.X + 1 > maxSide || hi.Z - lo.Z + 1 > maxSide) continue;
                (res.Min, res.Max) = (lo, hi);
                res.Cells[step] = s;
                added++;
            }
        }
        if (added > 0) res.Notes.Add($"added {added} doorstep{(added == 1 ? "" : "s")} (the ground in front of the door) so the copy can be walked into");
    }

    /// <summary>
    /// The flood fill (26-connected, so stair roofs and diagonal steps hold together) over building blocks above the floor,
    /// from the building blocks within 2 of the seed; then the floor under that footprint ("plus the floor it stands on"). With
    /// nothing above the floor, the floor itself (8-connected in its layer) is the build. Refuses past <see cref="CaptureRequest.MaxSide"/>.
    /// </summary>
    private static string? Flood(CaptureRequest req, BlockPos seed, Dictionary<BlockPos, bool> trees, HashSet<BlockPos> region, HashSet<BlockPos> unknown)
    {
        var w = req.World;
        int max = req.MaxSide, height = w.WorldHeight;
        bool Building(BlockPos p) => w.IsLoaded(p.X, p.Z) && (uint)p.Y < height
                                     && Classify(w, p, req.Exclude, trees).Class is CellClass.Build or CellClass.Unsupported;
        int floorY = req.FloorY ?? FloorOf(w, seed, Building);
        var q = new Queue<BlockPos>();
        foreach (var p in Start(seed, floorY, Building))
            if (region.Add(p)) q.Enqueue(p);
        int x0 = seed.X, x1 = seed.X, y0 = seed.Y, y1 = seed.Y, z0 = seed.Z, z1 = seed.Z;
        bool first = true;
        void Grow(BlockPos p)
        {
            if (first) { (x0, x1, y0, y1, z0, z1) = (p.X, p.X, p.Y, p.Y, p.Z, p.Z); first = false; return; }
            (x0, x1, y0, y1, z0, z1) = (Math.Min(x0, p.X), Math.Max(x1, p.X), Math.Min(y0, p.Y), Math.Max(y1, p.Y), Math.Min(z0, p.Z), Math.Max(z1, p.Z));
        }
        foreach (var p in region) Grow(p);
        string? Over() => x1 - x0 + 1 > max || y1 - y0 + 1 > max || z1 - z0 + 1 > max
            ? string.Create(CultureInfo.InvariantCulture,
                $"the build keeps going past {max}×{max}×{max} (reached x {x0}..{x1}, y {y0}..{y1}, z {z0}..{z1}: it joins something bigger); give me two corners around it")
            : null;
        while (q.Count > 0)
        {
            var p = q.Dequeue();
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        var n = p.Offset(dx, dy, dz);
                        if (n.Y <= floorY || region.Contains(n)) continue;
                        if (!w.IsLoaded(n.X, n.Z)) { unknown.Add(n); continue; }
                        if (!Building(n)) continue;
                        region.Add(n);
                        Grow(n);
                        if (Over() is { } big) return big;
                        q.Enqueue(n);
                    }
        }
        // The floor it stands on: building blocks of the floor layer under the footprint (one-thick floors and platforms; the
        // stone of a thick island underneath isn't a floor).
        bool FloorCell(BlockPos p) => Building(p) && !(w.GetBlock(p.X, p.Y, p.Z) == Data.Blocks.Stone && w.GetBlock(p.X, p.Y - 1, p.Z) is var below
                                                     && below != 0 && (below == Data.Blocks.Stone || IsTerrain(below)));
        if (region.Count > 0)
        {
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new BlockPos(x, floorY, z);
                    if (FloorCell(p)) region.Add(p);
                }
            return null;
        }
        // Only a floor (a platform, a path): its layer, 8-connected.
        var start = new BlockPos(seed.X, floorY, seed.Z);
        if (!FloorCell(start)) return null;
        region.Add(start);
        q.Enqueue(start);
        first = true;
        Grow(start);
        while (q.Count > 0)
        {
            var p = q.Dequeue();
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var n = p.Offset(dx, 0, dz);
                    if (region.Contains(n)) continue;
                    if (!w.IsLoaded(n.X, n.Z)) { unknown.Add(n); continue; }
                    if (!FloorCell(n)) continue;
                    region.Add(n);
                    Grow(n);
                    if (Over() is { } big) return big;
                    q.Enqueue(n);
                }
        }
        return null;
    }

    /// <summary>
    /// Where the flood starts: the seed itself when it is a building block above the floor, else the nearest building blocks
    /// seen through the open cells around it (a breadth-first walk over air/plants above the floor, ≤ 12 away): from inside a
    /// room that is its walls, ceiling and door; from outside, the nearest wall. The 26-connected flood spreads from there.
    /// </summary>
    private static List<BlockPos> Start(BlockPos seed, int floorY, Func<BlockPos, bool> building)
    {
        if (seed.Y > floorY && building(seed)) return [seed];
        const int Radius = 12;
        var seen = new HashSet<BlockPos> { seed };
        var frontier = new List<BlockPos> { seed };
        var found = new List<BlockPos>();
        for (int depth = 0; depth < Radius * 3 && frontier.Count > 0 && found.Count == 0; depth++)
        {
            var next = new List<BlockPos>();
            foreach (var p in frontier)
                foreach (var f in BlockFaceExtensions.All)
                {
                    var n = p.Offset(f);
                    if (n.Y <= floorY || Math.Abs(n.X - seed.X) > Radius || Math.Abs(n.Y - seed.Y) > Radius || Math.Abs(n.Z - seed.Z) > Radius || !seen.Add(n)) continue;
                    if (building(n)) found.Add(n);
                    else next.Add(n);
                }
            frontier = next;
        }
        return found;
    }

    /// <summary>The floor under a seed: air → the first solid below it; a block → the cell under the run of building blocks
    /// below it (a wall on grass: the grass; a planks floor on grass: the grass, so the floor is part of the build).</summary>
    private static int FloorOf(IBlockAccess w, BlockPos seed, Func<BlockPos, bool> building)
    {
        if (w.GetBlock(seed.X, seed.Y, seed.Z) == 0)
        {
            for (int y = seed.Y - 1; y >= Math.Max(0, seed.Y - 6); y--)
                if (w.GetBlock(seed.X, y, seed.Z) != 0) return y;
            return seed.Y - 1;
        }
        int b = seed.Y;
        while (b > 0 && building(new BlockPos(seed.X, b - 1, seed.Z))) b--;
        return b - 1;
    }

    /// <summary>One cell: air, terrain (incl. a living tree's logs), a buildable state (with a stand-in note), or unsupported.</summary>
    public static (CellClass Class, int Id, int Meta, string? Note) Classify(IBlockAccess w, BlockPos p, Func<BlockPos, bool>? exclude,
        Dictionary<BlockPos, bool>? trees = null)
    {
        int id = w.GetBlock(p.X, p.Y, p.Z), meta = w.GetMeta(p.X, p.Y, p.Z);
        if (id == 0) return (CellClass.Air, 0, 0, null);
        if (IsTerrain(id) || exclude?.Invoke(p) == true) return (CellClass.Natural, id, meta, null);
        if (id == Data.Blocks.Log && LivingTree(w, p, trees)) return (CellClass.Natural, id, meta, null);
        switch (id)
        {
            case Data.Blocks.LitFurnace: return (CellClass.Build, Data.Blocks.Furnace, Front(meta), "lit furnace as furnace");
            case Data.Blocks.RedstoneTorch or Data.Blocks.RedstoneTorchOff:
                return (CellClass.Build, Data.Blocks.Torch, meta is >= 1 and <= 5 ? meta : 5, "redstone torch as torch");
            case Data.Blocks.DoubleSlab:
                {
                    var (sub, name) = (meta & 7) switch
                    {
                        1 => (Data.Blocks.Sandstone, "sandstone"), 2 => (Data.Blocks.Planks, "planks"), 3 => (Data.Blocks.Cobblestone, "cobblestone"),
                        4 => (Data.Blocks.Bricks, "bricks"), 5 => (Data.Blocks.StoneBricks, "stone bricks"), _ => (Data.Blocks.Stone, "stone"),
                    };
                    return (CellClass.Build, sub, 0, $"double slab as {name}");
                }
            case Data.Blocks.LockedChest: return (CellClass.Build, Data.Blocks.Chest, 0, "locked chest as chest");
        }
        if (!BuildBlocks.Buildable(id)) return (CellClass.Unsupported, id, meta, Data.Blocks.Get(id).Name.Replace('_', ' '));
        int m = id switch
        {
            Data.Blocks.Slab => meta & 7,
            Data.Blocks.Furnace or Data.Blocks.Dispenser => Front(meta),
            _ => meta,
        };
        if (BuildBlocks.KindOf(id) == BuildKind.Door) return (CellClass.Build, id, meta & 15, null);
        if (BuildBlocks.MetaProblem(id, m) != null)
        {
            if (BuildBlocks.MetaProblem(id, 0) == null) return (CellClass.Build, id, 0, $"{Data.Blocks.Get(id).Name.Replace('_', ' ')} meta {meta} as 0");
            return (CellClass.Unsupported, id, meta, Data.Blocks.Get(id).Name.Replace('_', ' '));
        }
        return (CellClass.Build, id, m, null);
    }

    private static int Front(int meta) => meta is >= 2 and <= 5 ? meta : 3;

    /// <summary>A log that belongs to a living tree (logs with leaves, grown from soil): natural, never a build.</summary>
    private static bool LivingTree(IBlockAccess w, BlockPos p, Dictionary<BlockPos, bool>? cache)
    {
        if (cache != null && cache.TryGetValue(p, out var known)) return known;
        bool living = false;
        HashSet<BlockPos>? logs = null;
        try
        {
            if (TreeScan.Analyze(w, p) is { } t)
            {
                living = TreeStewardship.IsLiving(t);
                logs = t.Logs.ToHashSet();
            }
        }
        catch (Exception) { /* best effort: an unreadable log is a build block */ }
        if (cache != null)
        {
            cache[p] = living;
            if (logs != null) foreach (var l in logs) cache.TryAdd(l, living);
        }
        return living;
    }

    // ------------------------------------------------------------------ DSL

    /// <summary>The DSL that reproduces <see cref="CaptureResult.Cells"/> exactly at <see cref="CaptureResult.Min"/>.</summary>
    internal static string Dsl(CaptureResult r)
    {
        // One symbol per state (an upper door half draws its lower's symbol: the normalizer derives the half).
        var states = r.Cells.Where(c => !(BuildBlocks.KindOf(c.Value.Id) == BuildKind.Door && (c.Value.Meta & 8) != 0))
            .GroupBy(c => c.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Id).ThenBy(g => g.Key.Meta).Select(g => g.Key).ToList();
        var sym = new Dictionary<(int Id, int Meta), char>();
        var palette = new List<string>();
        var used = new HashSet<char>();
        foreach (var s in states)
        {
            var name = PaletteName(s.Id, s.Meta);
            if (name == null) continue;
            char initial = char.ToUpperInvariant(BuildBlocks.Name(s.Id, s.Meta).FirstOrDefault(char.IsLetter));
            char c = Symbols.Contains(initial) && !used.Contains(initial) ? initial : Symbols.FirstOrDefault(ch => !used.Contains(ch));
            if (c == '\0') break; // > 80 states: the rest are reported as mismatches
            used.Add(c);
            sym[s] = c;
            palette.Add($"{c}={name}");
        }
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# captured from the world at {r.Bounds}: {r.Blocks} blocks, drawn as it stands\n");
        sb.Append(CultureInfo.InvariantCulture, $"blueprint {r.Name}\n");
        sb.Append(CultureInfo.InvariantCulture, $"size {r.W}x{r.H}x{r.D}\n");
        sb.Append(CultureInfo.InvariantCulture, $"origin {r.Min.X},{r.Min.Y},{r.Min.Z}\n");
        sb.Append("facing south\n");
        for (int i = 0; i < palette.Count; i += 8) sb.Append("palette ").Append(string.Join(' ', palette.Skip(i).Take(8))).Append('\n');
        var layers = new List<string[]>();
        for (int y = r.Min.Y; y <= r.Max.Y; y++)
        {
            var rows = new string[r.D];
            for (int z = r.Min.Z; z <= r.Max.Z; z++)
            {
                var row = new char[r.W];
                for (int x = r.Min.X; x <= r.Max.X; x++)
                {
                    var p = new BlockPos(x, y, z);
                    char ch = '_';
                    if (r.Cells.TryGetValue(p, out var s))
                    {
                        if (BuildBlocks.KindOf(s.Id) == BuildKind.Door && (s.Meta & 8) != 0 && r.Cells.TryGetValue(p.Down(), out var lo)) s = lo;
                        ch = sym.TryGetValue(s, out var c) ? c : '_';
                    }
                    else if (r.Air.Contains(p)) ch = '.';
                    row[x - r.Min.X] = ch;
                }
                rows[z - r.Min.Z] = new string(row);
            }
            layers.Add(rows);
        }
        // Consecutive identical layers share one block ("layer 1-3").
        for (int a = 0; a < layers.Count;)
        {
            int b = a;
            while (b + 1 < layers.Count && layers[b + 1].SequenceEqual(layers[a])) b++;
            if (layers[a].All(row => row.All(ch => ch == '_'))) { a = b + 1; continue; } // nothing to say: kept as is
            sb.Append(a == b ? $"layer {a}\n" : $"layer {a}-{b}\n");
            foreach (var row in layers[a]) sb.Append(row).Append('\n');
            a = b + 1;
        }
        return sb.ToString();
    }

    /// <summary>"planks", "cobblestone_stairs:2", "wooden_door:5" — a name the parser resolves back to exactly (id, meta).</summary>
    internal static string? PaletteName(int id, int meta)
    {
        var name = Data.Blocks.Get(id).Name;
        var arg = meta != 0 ? meta.ToString(CultureInfo.InvariantCulture) : null;
        var (e, _) = BlueprintParser.Resolve('X', name, arg, name);
        int want = BuildBlocks.KindOf(id) == BuildKind.Door ? meta & 7 : meta;
        if (e != null && e.Id == id && e.CanonicalMeta == want) return arg == null ? name : $"{name}:{arg}";
        // A name the item table reads as another damage (e.g. a variant): spell the meta out.
        if (arg == null)
        {
            (e, _) = BlueprintParser.Resolve('X', name, "0", name);
            if (e != null && e.Id == id && e.CanonicalMeta == want) return $"{name}:0";
        }
        return null;
    }

    /// <summary>Parse the DSL back and compare it to the captured states voxel by voxel (and the drawn air to the world).</summary>
    internal static void Verify(CaptureResult r, IBlockAccess w)
    {
        var (bp, issues) = BlueprintParser.Parse(r.Dsl);
        if (issues.FirstOrDefault(i => i.Level == IssueLevel.Error) is { } perr) { r.Refusal = "the captured DSL doesn't parse: " + perr; return; }
        var (plan, more) = BlueprintNormalizer.Normalize(bp, r.Min, w.WorldHeight);
        if (plan == null || more.FirstOrDefault(i => i.Level == IssueLevel.Error) is { } nerr)
        {
            r.Refusal = "the captured DSL doesn't normalize: " + (more.FirstOrDefault(i => i.Level == IssueLevel.Error)?.ToString() ?? "no plan");
            return;
        }
        r.Plan = plan;
        foreach (var (p, s) in r.Cells)
        {
            if (plan.Blocks.TryGetValue(p, out var b) && b.Id == s.Id && b.Meta == s.Meta) r.Matched++;
            else r.Mismatched.Add(p);
        }
        foreach (var p in plan.Blocks.Keys) if (!r.Cells.ContainsKey(p)) r.Mismatched.Add(p);
        foreach (var p in plan.Air) if (w.GetBlock(p.X, p.Y, p.Z) != 0) r.Mismatched.Add(p);
        if (r.Mismatched.Count > 0)
            r.Refusal = $"the captured design doesn't match the world at {r.Mismatched.Count} cell(s), e.g. "
                        + string.Join(" ", r.Mismatched.Take(4).Select(p => $"{p.X},{p.Y},{p.Z}"));
    }

    // ------------------------------------------------------------------ helpers

    private static string CleanName(string name)
    {
        var n = (name ?? "").Replace('#', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim().Trim('"', '\'').Trim();
        if (n.Length == 0) n = "captured build";
        return n.Length <= 48 ? n : n[..48].TrimEnd();
    }

    private static string? Too(BlockPos lo, BlockPos hi, int max) =>
        hi.X - lo.X + 1 > max || hi.Y - lo.Y + 1 > max || hi.Z - lo.Z + 1 > max
            ? string.Create(CultureInfo.InvariantCulture, $"{hi.X - lo.X + 1}×{hi.Z - lo.Z + 1}×{hi.Y - lo.Y + 1} is bigger than I can memorize ({max}×{max}×{max}): pick corners around a part")
            : null;

    private static void Count(SortedDictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;
    private static BlockPos Lo(BlockPos a, BlockPos b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    private static BlockPos Hi(BlockPos a, BlockPos b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
}
