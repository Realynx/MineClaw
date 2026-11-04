using System.Text.Json;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// The geometry of the standard field (pure, no world access). <c>Corner</c> is the lowest-x, lowest-z cell of the rectangle at
/// the FARMLAND layer (the y of the ground block that becomes farmland or water; crops grow at y+1). <c>u</c> runs along the
/// length (the trench axis), <c>v</c> across it. The fence ring is the rectangle grown by one cell, fence posts at y+1.
/// </summary>
internal readonly record struct FieldGeom(BlockPos Corner, int Width, int Length, bool AlongX)
{
    public const int MinWidth = 3, MaxWidth = 33, MinLength = 3, MaxLength = 64;

    public int Y => Corner.Y;

    public BlockPos At(int u, int v) => AlongX ? new BlockPos(Corner.X + u, Y, Corner.Z + v) : new BlockPos(Corner.X + v, Y, Corner.Z + u);

    /// <summary>Trench columns (v): one in the middle up to width 9; wider fields get one every ≤ 9 columns, so every farm column is
    /// within 4 of water (BlockFarmland.hasWater scans ±4 horizontally and y..y+1): 17 wide = columns 4 and 12.</summary>
    public int[] TrenchColumns()
    {
        int n = (Width + 8) / 9;
        if (n <= 1) return [Width / 2];
        var cols = new int[n];
        for (int i = 0; i < n; i++) cols[i] = 4 + (int)Math.Round(i * (Width - 9) / (double)(n - 1));
        return cols;
    }

    /// <summary>Cells of one trench column that get a bucket of water: every 2nd one (the gaps between fill as sources by the
    /// 2-source rule) and always the last, so the end is a source and never a flowing tail.</summary>
    public IEnumerable<int> PourRows()
    {
        for (int u = 0; u < Length; u += 2) yield return u;
        if (Length % 2 == 0) yield return Length - 1;
    }

    /// <summary>The fence ring in walking order: (u, v) from (-1,-1) round the rectangle.</summary>
    public IEnumerable<(int U, int V)> Ring()
    {
        for (int v = -1; v <= Width; v++) yield return (-1, v);
        for (int u = 0; u < Length; u++) yield return (u, Width);
        for (int v = Width; v >= -1; v--) yield return (Length, v);
        for (int u = Length - 1; u >= 0; u--) yield return (u, -1);
    }

    /// <summary>The gate: the ring cell on the short side in line with the middle farm column.</summary>
    public (int U, int V) Gate()
    {
        var t = TrenchColumns();
        int v = Width / 2;
        if (t.Contains(v)) v--;
        return (-1, v);
    }

    public bool InRect(int x, int z)
    {
        int u = AlongX ? x - Corner.X : z - Corner.Z, v = AlongX ? z - Corner.Z : x - Corner.X;
        return u >= 0 && u < Length && v >= 0 && v < Width;
    }

    public bool InRing(int x, int z)
    {
        int u = AlongX ? x - Corner.X : z - Corner.Z, v = AlongX ? z - Corner.Z : x - Corner.X;
        return u >= -1 && u <= Length && v >= -1 && v <= Width;
    }

    public string Describe => $"{Width}x{Length} {(AlongX ? "x" : "z")}-axis at {SkillJson.Fmt(Corner)}";
}

/// <summary>
/// <c>lay_field</c>: the standard wheat field in one call, so nobody improvises trenches on a hill. One 1-deep water trench down
/// the middle column (wide fields get a trench every ≤ 9 columns), a water bucket poured into every 2nd trench cell (the gaps
/// fill as sources: a flowing cell beside 2+ sources over a solid floor becomes one), every other column tilled (all within 4 of
/// water, same level), a fence round the rectangle with one gate and torches on the fence line. Nothing is poured before the
/// trench is closed (floor, ends, sides), lava within the flow refuses the pour, and any water outside the trench afterwards is
/// reported (lava or other water inside the field is refused up front). Resumable: sources are never re-poured, farmland never re-hoed, existing fence/torches kept.
/// </summary>
[Skill]
public sealed class LayFieldSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "lay_field", Category = SkillCategory.Farming,
        Summary = "standard field: trench, tilled rows, fence, gate",
        Args = "corner,width?,length?,axis?,level?",
        Schema = SkillJson.Schema("corner:pos,width:int?,length:int?,axis:str?,level:bool?,fence:bool?,max_diff:int?"),
        Estimate = TimeSpan.FromMinutes(6),
        Pre = new() { Items = [[Items.Bucket, Items.WaterBucket], FarmOps.Hoes], Needs = "hoe,buckets", Dimension = Dim.Overworld, Primitives = Primitive.UseOnBlock },
        Keywords = "field big wheat standard farm layout lay build make trench irrigation water channel till rows fence gate torch " +
                   "crops plot acre farmland 17 40 by wide long flatten level",
        Aliases = ["standard_field", "build_field", "make_field", "wheat_field"],
    };

    /// <summary>Most blocks a column may be dug down or filled up when <c>level</c> is set.</summary>
    internal const int DefaultMaxDiff = 4;
    internal const int InfiniteWaterSearch = 48;

    /// <summary>The parsed request: the field's geometry and how to treat it.</summary>
    private sealed record FieldJob(FieldGeom G, string Note, bool Level, bool Fence, int MaxDiff);

    /// <summary>The field's trench cells (every trench column along the whole length), as a list and a set.</summary>
    private sealed record Trench(List<BlockPos> Cells, HashSet<BlockPos> Set);

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var (job, invalid) = Parse(ctx, args);
        if (job is null) return Fail(invalid!);
        var g = job.G;

        // ---- 1. survey: refuses up front, changes nothing ----
        var survey = Survey(ctx, g, job.Level, job.MaxDiff);
        if (survey.Problems.Count > 0) return Fail(Refusal(job, survey));

        // ---- 2. water and materials, still before touching anything ----
        var cells = g.TrenchColumns().SelectMany(v => Enumerable.Range(0, g.Length).Select(u => g.At(u, v))).ToList();
        var trench = new Trench(cells, cells.ToHashSet());
        var external = FindInfiniteWater(ctx, g, trench.Set);
        if (WaterShort(ctx, g, trench, external) is { } dry) return Fail(dry);
        if (job.Fence && await MaterialsShortAsync(ctx, g, ct).ConfigureAwait(false) is { } lacking) return Fail(lacking);

        var done = new Tally();
        var failure = await BuildAsync(ctx, job, survey, trench, external, done, ct).ConfigureAwait(false);
        return failure is null ? Ok(Progress(g, done, null) + job.Note + "; plant_seeds next") : Fail(Progress(g, done, failure));
    }

    /// <summary>The field request, or why it can't be laid (no corner, out of the buildable range, not loaded).</summary>
    private static (FieldJob? Job, string? Error) Parse(SkillContext ctx, JsonElement args)
    {
        if ((args.Pos("corner") ?? args.Pos()) is not { } corner)
            return (null, "corner required: the lowest-x,lowest-z cell of the field at ground level (the block that becomes farmland), e.g. corner=100,64,200");
        int width = Clamp(args.Int("width"), 17, FieldGeom.MinWidth, FieldGeom.MaxWidth), length = Clamp(args.Int("length"), 40, FieldGeom.MinLength, FieldGeom.MaxLength);
        string note = "";
        if (width % 2 == 0) { width++; note = $" (width made odd: {width})"; }
        bool alongX = !string.Equals(args.Str("axis")?.Trim(), "z", StringComparison.OrdinalIgnoreCase);
        var g = new FieldGeom(corner, width, length, alongX);
        var w = ctx.World;
        int y = g.Y;
        if (y < 3 || y >= w.WorldHeight - 6) return (null, $"y {y} is outside the buildable range");
        if (!CornersLoaded(w, g)) return (null, $"{g.Describe} isn't loaded; walk closer first");
        return (new FieldJob(g, note, args.Flag("level"), args.Flag("fence", true), Clamp(args.Int("max_diff"), DefaultMaxDiff, 1, 8)), null);
    }

    private static string Refusal(FieldJob job, SurveyResult survey) =>
        $"can't lay {job.G.Describe}{job.Note}: " + string.Join("; ", survey.Problems.Take(3)) + (survey.Problems.Count > 3 ? $"; +{survey.Problems.Count - 3} more" : "")
        + (!job.Level && survey.Off > 0 ? "; level:true flattens it" : "");

    /// <summary>Not enough water to fill the trench: 2 buckets, or 1 plus an infinite source (a pond, or the trench's own).</summary>
    private static string? WaterShort(SkillContext ctx, FieldGeom g, Trench trench, List<BlockPos> external)
    {
        var w = ctx.World;
        int full = ctx.Count(Items.WaterBucket), buckets = full + ctx.Count(Items.Bucket);
        bool needWater = g.TrenchColumns().Any(v => g.PourRows().Any(u => !FluidRules.IsSource(w, g.At(u, v), false)));
        if (!needWater || CanWater(w, full, buckets, external, trench.Cells)) return null;
        return $"{g.Describe}: needs 2 water buckets, or a bucket plus an infinite water source within {InfiniteWaterSearch} (infinite_water makes one); have {full} water, {buckets - full} empty";
    }

    private static bool CanWater(WorldState w, int full, int buckets, List<BlockPos> external, List<BlockPos> trench) =>
        full >= 2 || buckets >= 1 && (external.Count > 0 || trench.Any(c => FluidRules.IsSource(w, c, false) && FluidRules.Refills(w, c)));

    /// <summary>Fence posts, the gate and the torches still to place, obtained when short; what's still missing after.</summary>
    private static async Task<string?> MaterialsShortAsync(SkillContext ctx, FieldGeom g, CancellationToken ct)
    {
        var lacking = new List<string>();
        foreach (var (id, name, want) in FenceNeeds(ctx, g))
        {
            await ObtainAsync(ctx, id, want, ct).ConfigureAwait(false);
            if (ctx.Count(id) < want) lacking.Add($"{want - ctx.Count(id)} more {name}");
        }
        return lacking.Count > 0 ? $"{g.Describe}: short of {string.Join(", ", lacking)} (fence:false lays the field without them)" : null;
    }

    private static List<(int Id, string Name, int Want)> FenceNeeds(SkillContext ctx, FieldGeom g)
    {
        var need = new List<(int Id, string Name, int Want)>();
        var fenceCells = FenceCells(ctx, g);
        int posts = fenceCells.Count(c => c.Id == Blocks.Fence), gates = fenceCells.Count(c => c.Id == Blocks.FenceGate);
        if (posts > 0) need.Add((Blocks.Fence, "fence", posts));
        if (gates > 0) need.Add((Blocks.FenceGate, "fence gate", gates));
        int torches = TorchSpots(g, fenceCells).Count(t => ctx.Block(t) != Blocks.Torch);
        if (torches > 0) need.Add((Blocks.Torch, "torch", torches));
        return need;
    }

    private static async Task ObtainAsync(SkillContext ctx, int id, int want, CancellationToken ct)
    {
        if (ctx.Count(id) >= want || ctx.Registry.Obtain is not { } obtain) return;
        try { await obtain(ctx, id, want, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { ctx.Log?.Invoke($"lay_field obtain threw: {ex.Message}"); }
    }

    /// <summary>Steps 3-7, each stopping the job with its failure: level, close and dig the trench, water, till, fence.</summary>
    private static async Task<string?> BuildAsync(SkillContext ctx, FieldJob job, SurveyResult survey, Trench trench, List<BlockPos> external,
        Tally done, CancellationToken ct)
    {
        var g = job.G;
        int hoe = ctx.FirstOwned(FarmOps.Hoes)!.Value;
        // ---- 3. level (only cells off the target y: dig down or fill up; a cell at y is never touched) ----
        if (await LevelAsync(ctx, g, survey, done, ct).ConfigureAwait(false) is { } lv) return lv;
        // ---- 4. close the trench (floor, ring ground under the ends), dig it ----
        if (await CloseTrenchAsync(ctx, trench.Cells, done, ct).ConfigureAwait(false) is { } closed) return closed;
        // ---- 5. water ----
        if (await PourAsync(ctx, g, trench.Cells, external, done, ct).ConfigureAwait(false) is { } water) return water;
        var strays = StrayWater(ctx, g, trench.Set);
        if (strays.Count > 0) return $"stray water outside the trench at {string.Join(" ", strays.Take(3).Select(SkillJson.Fmt))} ({strays.Count} cells): scoop it before tilling";
        // ---- 6. till ----
        if (await TillAsync(ctx, g, hoe, done, ct).ConfigureAwait(false) is { } till) return till;
        // ---- 7. fence, gate, torches ----
        if (job.Fence && await FenceAsync(ctx, g, done, ct).ConfigureAwait(false) is { } fr) return fr;
        await FarmOps.CollectAsync(ctx, 8, ct).ConfigureAwait(false);
        var left = StrayWater(ctx, g, trench.Set);
        return left.Count > 0 ? $"stray water outside the trench at {SkillJson.Fmt(left[0])}" : null;
    }

    /// <summary>Within reach of <paramref name="cell"/>, and never standing in its column (a dig or placement there would be refused).</summary>
    private static async Task<ActionResult> ApproachAsync(SkillContext ctx, BlockPos cell, CancellationToken ct)
    {
        var me = ctx.Me;
        if (me.X == cell.X && me.Z == cell.Z)
            return await ctx.Game.GotoAsync(new GoalWithinReach(cell, ctx.Game.Reach - 0.8), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
        return await ctx.ReachAsync(cell, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ survey

    private sealed class Tally
    {
        public int Dug, Filled, Sources, Tilled, Fences, Torches, TrenchDug, Floors;
        public bool Gate;
    }

    private static string Progress(FieldGeom g, Tally t, string? failure)
    {
        var parts = new List<string>();
        if (t.Dug + t.Filled > 0) parts.Add($"levelled {t.Dug} dug/{t.Filled} filled");
        if (t.TrenchDug > 0) parts.Add($"trench {t.TrenchDug} dug");
        if (t.Sources > 0) parts.Add($"{t.Sources} water poured");
        if (t.Tilled > 0) parts.Add($"{t.Tilled} tilled");
        if (t.Fences > 0) parts.Add($"{t.Fences} fence{(t.Gate ? "+gate" : "")}");
        if (t.Torches > 0) parts.Add($"{t.Torches} torches");
        string did = parts.Count > 0 ? string.Join(", ", parts) : "nothing new";
        return failure is null ? $"field {g.Describe}: {did} (trenches at {string.Join(",", g.TrenchColumns())})"
                               : $"field {g.Describe} stopped: {failure} (done so far: {did}); run lay_field again to continue";
    }

    private static bool CornersLoaded(WorldState w, FieldGeom g) =>
        w.IsLoaded(g.At(-1, -1).X, g.At(-1, -1).Z) && w.IsLoaded(g.At(g.Length, g.Width).X, g.At(g.Length, g.Width).Z)
        && w.IsLoaded(g.At(-1, g.Width).X, g.At(-1, g.Width).Z) && w.IsLoaded(g.At(g.Length, -1).X, g.At(g.Length, -1).Z);

    internal static bool Plantish(int id) => id is 6 or 31 or 32 or 37 or 38 or 39 or 40 or 78 or 106 or 175;
    private static bool Crop(int id) => CropKinds.IsCrop(id) || id is 104 or 105;
    private static bool Ignorable(int id) => id == 0 || Plantish(id) || Crop(id) || id is Blocks.Fence or Blocks.FenceGate or Blocks.Torch;
    private static bool Tree(int id) => id is Blocks.Log or Blocks.Leaves or 161 or 162;

    private sealed class SurveyResult
    {
        public List<string> Problems { get; } = [];
        public int Off;
        public List<BlockPos> Digs { get; } = [];
        public List<BlockPos> Fills { get; } = [];
    }

    /// <summary>Surface of a column: the y of its highest solid block within the dig range (plants, crops, fence and torches don't
    /// count), or why it can't be judged. A trench cell may hold its own water.</summary>
    private static (int? Top, string? Blocker) Surface(SkillContext ctx, int x, int z, int y, int range, bool trench)
    {
        var w = ctx.World;
        for (int yy = Math.Min(y + range + 3, w.WorldHeight - 2); yy >= Math.Max(1, y - range - 1); yy--)
        {
            int id = w.GetBlock(x, yy, z);
            if (Ignorable(id)) continue;
            var at = new BlockPos(x, yy, z);
            if (BlockShapes.IsLiquid(id))
            {
                if (trench && yy == y) continue;
                return (null, $"{SkillWorld.Name(id)} at {SkillJson.Fmt(at)}");
            }
            if (Tree(id)) return (null, $"a tree at {SkillJson.Fmt(at)} (chop it first)");
            if (!BlockShapes.HasCollision(w, x, yy, z)) return (null, $"{SkillWorld.Name(id)} at {SkillJson.Fmt(at)}");
            return (yy, null);
        }
        return (null, $"no ground near y{y} at {x},{z}");
    }

    private static SurveyResult Survey(SkillContext ctx, FieldGeom g, bool level, int maxDiff) => new FieldSurvey(ctx, g, level, maxDiff).Run();

    /// <summary>One survey of the field and its ring: off-level columns (to dig or fill), what blocks it, foreign liquids.</summary>
    private sealed class FieldSurvey(SkillContext ctx, FieldGeom g, bool level, int maxDiff)
    {
        private readonly SurveyResult _r = new();
        private readonly HashSet<int> _trenchCols = g.TrenchColumns().ToHashSet();
        private readonly List<(int Dev, BlockPos Pos)> _offs = new();
        private readonly List<BlockPos> _digs = new();
        private readonly List<BlockPos> _fills = new();
        private int Y => g.Y;

        public SurveyResult Run()
        {
            for (int u = -1; u <= g.Length; u++)
            for (int v = -1; v <= g.Width; v++)
                Column(u, v);
            if (!level) NameOffCells();
            Obstacles();
            _r.Digs.AddRange(_digs.OrderByDescending(p => p.Y));
            _r.Fills.AddRange(_fills.OrderBy(p => p.Y));
            ForeignLiquids();
            return _r;
        }

        private bool IsRing(int u, int v) => u < 0 || u >= g.Length || v < 0 || v >= g.Width;

        private void Column(int u, int v)
        {
            var c = g.At(u, v);
            bool ring = IsRing(u, v), isTrench = !ring && _trenchCols.Contains(v);
            var (top, blocker) = Surface(ctx, c.X, c.Z, Y, maxDiff, isTrench);
            if (top is null) { _r.Problems.Add(blocker!); return; }
            int dev = top.Value - Y;
            // A trench cell dug on an earlier run: water or air at y over a floor at y-1 is the finished state.
            if (isTrench && dev == -1 && DugOut(c)) dev = 0;
            if (dev == 0) return;
            _offs.Add((dev, c));
            if (Repairs(ring, dev)) Repair(c, dev, top.Value);
        }

        private bool DugOut(BlockPos c) => ctx.Block(c) == 0 || BlockShapes.IsLiquid(ctx.Block(c)) || Ignorable(ctx.Block(c));

        /// <summary>Level fixes every column; without it the ring's low cells are still filled (fence support, trench end cap).</summary>
        private bool Repairs(bool ring, int dev) => level || ring && dev < 0;

        private void Repair(BlockPos c, int dev, int top)
        {
            if (Math.Abs(dev) > maxDiff) { _r.Problems.Add($"{SkillJson.Fmt(c)} is {Math.Abs(dev)} {(dev > 0 ? "above" : "below")} y{Y} (more than {maxDiff})"); return; }
            if (dev > 0) DigsAbove(c, top);
            else
                for (int yy = top + 1; yy <= Y; yy++) _fills.Add(new BlockPos(c.X, yy, c.Z));
        }

        private void DigsAbove(BlockPos c, int top)
        {
            for (int yy = Y + 1; yy <= top; yy++)
            {
                var b = new BlockPos(c.X, yy, c.Z);
                int id = ctx.Block(b);
                if (Ignorable(id)) continue;
                if (ctx.Protected(b)) _r.Problems.Add(ctx.ProtectedText(b, SkillJson.Fmt(b)));
                else if (Blocks.Get(id).Interactable || Blocks.Get(id).Hardness < 0) _r.Problems.Add($"{SkillWorld.Name(id)} at {SkillJson.Fmt(b)} can't be dug");
                else _digs.Add(b);
            }
        }

        /// <summary>Farm rows must be flat at y (±0): name the worst cells.</summary>
        private void NameOffCells()
        {
            var rect = _offs.Where(o => g.InRect(o.Pos.X, o.Pos.Z)).ToList();
            _r.Off = rect.Count;
            if (rect.Count == 0) return;
            var worst = rect.OrderByDescending(o => Math.Abs(o.Dev)).Take(3).Select(o => $"{SkillJson.Fmt(o.Pos)} {Math.Abs(o.Dev)} {(o.Dev > 0 ? "above" : "below")}");
            _r.Problems.Insert(0, $"{rect.Count} of {g.Width * g.Length} cells aren't at y{Y} (worst {string.Join(", ", worst)})");
        }

        /// <summary>Anything in the way above the farm rows that is neither air, a plant nor a crop is an obstacle (a torch, a sign...).</summary>
        private void Obstacles()
        {
            var w = ctx.World;
            for (int u = 0; u < g.Length; u++)
            for (int v = 0; v < g.Width; v++)
            {
                var up = g.At(u, v).Up();
                int id = ctx.Block(up);
                if (!Ignorable(id) && !_digs.Contains(up) && !BlockShapes.IsLiquid(id) && !BlockShapes.HasCollision(w, up.X, up.Y, up.Z))
                    _r.Problems.Add($"{SkillWorld.Name(id)} at {SkillJson.Fmt(up)}");
            }
        }

        /// <summary>Foreign water or lava next to the field would meet the flow (or hold the soil): refuse by name.</summary>
        private void ForeignLiquids()
        {
            var w = ctx.World;
            foreach (var f in w.FindBlocks(g.At(g.Length / 2, g.Width / 2).Center, Math.Max(g.Length, g.Width) / 2.0 + 4, (id, _) => BlockShapes.IsLiquid(id), 64))
                if (g.InRing(f.Pos.X, f.Pos.Z) && f.Pos.Y >= Y - 1 && f.Pos.Y <= Y + 1 && !OwnTrench(f.Pos))
                    _r.Problems.Add($"{SkillWorld.Name(f.Id)} at {SkillJson.Fmt(f.Pos)} is inside the field");
        }

        private bool OwnTrench(BlockPos p) => p.Y == Y && g.InRect(p.X, p.Z) && _trenchCols.Contains(AlongV(g, p));
    }

    private static int AlongV(FieldGeom g, BlockPos p) => g.AlongX ? p.Z - g.Corner.Z : p.X - g.Corner.X;

    // ------------------------------------------------------------------ levelling

    /// <summary>Digs the columns above y down to it and fills the ones below up to it; cells already at y are never touched.</summary>
    private static async Task<string?> LevelAsync(SkillContext ctx, FieldGeom g, SurveyResult s, Tally t, CancellationToken ct)
    {
        if (await DigDownAsync(ctx, s, t, ct).ConfigureAwait(false) is { } dug) return dug;
        if (s.Digs.Count > 0) await FarmOps.CollectAsync(ctx, 10, ct).ConfigureAwait(false);
        if (s.Fills.Count == 0) return null;
        await ObtainAsync(ctx, Blocks.Dirt, s.Fills.Count, ct).ConfigureAwait(false);
        return await FillUpAsync(ctx, g, s, t, ct).ConfigureAwait(false);
    }

    private static async Task<string?> DigDownAsync(SkillContext ctx, SurveyResult s, Tally t, CancellationToken ct)
    {
        foreach (var b in s.Digs)
        {
            ct.ThrowIfCancellationRequested();
            if (Ignorable(ctx.Block(b)) && ctx.Block(b) == 0) continue;
            await ApproachAsync(ctx, b, ct).ConfigureAwait(false);
            var d = await ctx.Game.DigAsync(b, ct).ConfigureAwait(false);
            if (!d.Ok) return $"couldn't dig {SkillJson.Fmt(b)}: {d.Message}";
            t.Dug++;
        }
        return null;
    }

    private static async Task<string?> FillUpAsync(SkillContext ctx, FieldGeom g, SurveyResult s, Tally t, CancellationToken ct)
    {
        foreach (var b in s.Fills)
        {
            ct.ThrowIfCancellationRequested();
            if (!SkillWorld.Replaceable(ctx.World, b)) continue;
            int block = FillBlock(ctx, g, b);
            if (block < 0) return $"out of dirt at {SkillJson.Fmt(b)} ({s.Fills.Count - t.Filled} cells still to fill)";
            await ApproachAsync(ctx, b, ct).ConfigureAwait(false);
            var p = await ctx.Game.PlaceAsync(b, block, ct).ConfigureAwait(false);
            if (!p.Ok) return $"couldn't fill {SkillJson.Fmt(b)}: {p.Message}";
            t.Filled++;
        }
        return null;
    }

    /// <summary>The farm layer itself must be tillable (dirt); the support below it can be any building block. -1: none.</summary>
    private static int FillBlock(SkillContext ctx, FieldGeom g, BlockPos b) =>
        b.Y == g.Y || ctx.Has(Blocks.Dirt) ? (ctx.Has(Blocks.Dirt) ? Blocks.Dirt : -1) : SkillWorld.BuildingBlock(ctx) ?? -1;

    // ------------------------------------------------------------------ trench

    private static async Task<string?> CloseTrenchAsync(SkillContext ctx, List<BlockPos> trench, Tally t, CancellationToken ct)
    {
        if (await DigTrenchAsync(ctx, trench, t, ct).ConfigureAwait(false) is { } dug) return dug;
        if (t.TrenchDug > 0) await FarmOps.CollectAsync(ctx, 8, ct).ConfigureAwait(false);
        if (await CloseFloorAsync(ctx, trench, t, ct).ConfigureAwait(false) is { } floor) return floor;
        return Leak(ctx, trench);
    }

    /// <summary>Digs the trench (a cell holding water or air is already dug).</summary>
    private static async Task<string?> DigTrenchAsync(SkillContext ctx, List<BlockPos> trench, Tally t, CancellationToken ct)
    {
        foreach (var c in trench)
        {
            ct.ThrowIfCancellationRequested();
            int id = ctx.Block(c);
            if (id == 0 || BlockShapes.IsLiquid(id)) continue;
            if (ctx.Protected(c)) return ctx.ProtectedText(c, SkillJson.Fmt(c));
            await ApproachAsync(ctx, c, ct).ConfigureAwait(false);
            var d = await ctx.Game.DigAsync(c, ct).ConfigureAwait(false);
            if (!d.Ok) return $"couldn't dig the trench at {SkillJson.Fmt(c)}: {d.Message}";
            t.TrenchDug++;
        }
        return null;
    }

    /// <summary>The floor must hold water: a solid block (or water) under every trench cell; fill a hole in it.</summary>
    private static async Task<string?> CloseFloorAsync(SkillContext ctx, List<BlockPos> trench, Tally t, CancellationToken ct)
    {
        var w = ctx.World;
        foreach (var c in trench)
        {
            var floor = c.Down();
            if (SkillWorld.Solid(w, floor) || BlockShapes.IsWater(ctx.Block(floor)) || BlockShapes.HasCollision(w, floor.X, floor.Y, floor.Z)) continue;
            int block = ctx.Has(Blocks.Dirt) ? Blocks.Dirt : SkillWorld.BuildingBlock(ctx) ?? -1;
            if (block < 0) return $"no block to close the trench floor at {SkillJson.Fmt(floor)}";
            await ApproachAsync(ctx, floor, ct).ConfigureAwait(false);
            var p = await ctx.Game.PlaceAsync(floor, block, ct).ConfigureAwait(false);
            if (!p.Ok) return $"couldn't close the trench floor at {SkillJson.Fmt(floor)}: {p.Message}";
            t.Floors++;
        }
        return null;
    }

    /// <summary>The two ends and both sides must be solid at the water layer before a drop goes in.</summary>
    private static string? Leak(SkillContext ctx, List<BlockPos> trench)
    {
        var w = ctx.World;
        foreach (var c in trench)
            foreach (var o in SkillWorld.Horizontal)
            {
                var n = c + o;
                if (trench.Contains(n)) continue;
                if (!BlockShapes.HasCollision(w, n.X, n.Y, n.Z) && !SkillWorld.Solid(w, n)) return $"the trench would leak at {SkillJson.Fmt(n)} ({ctx.BlockName(n)})";
            }
        return null;
    }

    // ------------------------------------------------------------------ water

    private static List<BlockPos> FindInfiniteWater(SkillContext ctx, FieldGeom g, HashSet<BlockPos> own) =>
        ctx.World.FindBlocks(ctx.Feet, InfiniteWaterSearch, (id, m) => Blocks.IsWaterSource(id, m), 512)
            .Select(f => f.Pos).Where(p => !g.InRing(p.X, p.Z) && FluidRules.Refills(ctx.World, p) && !ctx.Protected(p)).ToList();

    /// <summary>Pours a bucket into every 2nd trench cell (and the last); refills from the nearest infinite source (the trench's
    /// own gap cells once two drops are in, else a pond).</summary>
    private static async Task<string?> PourAsync(SkillContext ctx, FieldGeom g, List<BlockPos> trench, List<BlockPos> external, Tally t, CancellationToken ct)
    {
        var w = ctx.World;
        var own = trench.ToHashSet();
        foreach (int v in g.TrenchColumns())
        foreach (int u in g.PourRows())
        {
            ct.ThrowIfCancellationRequested();
            var cell = g.At(u, v);
            if (FluidRules.IsSource(w, cell, false)) continue; // never re-pour a source
            if (!ctx.Has(Items.WaterBucket))
            {
                var fill = await RefillAsync(ctx, own, ct).ConfigureAwait(false);
                if (fill is not null) return fill;
            }
            var r = await BucketOps.EmptyAsync(ctx, cell, false, ct).ConfigureAwait(false);
            if (!r.Ok) return $"pour at {SkillJson.Fmt(cell)}: {r.Text}";
            // A cell that already held flowing water reads as "water appeared" at once: only a source proves the bucket emptied.
            if (!await ctx.Game.WaitUntilAsync(() => FluidRules.IsSource(w, cell, false), TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
                return $"pour at {SkillJson.Fmt(cell)} left no water source";
            t.Sources++;
        }
        // The gaps become sources by themselves; give the server a moment, then count.
        await ctx.Game.WaitUntilAsync(() => trench.All(c => FluidRules.IsSource(w, c, false)), TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        var missing = trench.Where(c => !FluidRules.IsSource(w, c, false)).ToList();
        if (missing.Count > 0) return $"{missing.Count} trench cells never became water sources (first {SkillJson.Fmt(missing[0])})";
        return null;
    }

    private static async Task<string?> RefillAsync(SkillContext ctx, HashSet<BlockPos> own, CancellationToken ct)
    {
        if (!ctx.Has(Items.Bucket)) return "no empty bucket to refill";
        List<BlockPos> Candidates() => ctx.World.FindBlocks(ctx.Feet, 80, (id, m) => Blocks.IsWaterSource(id, m), 512)
            .Where(f => FluidRules.Refills(ctx.World, f.Pos) && !ctx.Protected(f.Pos)).OrderBy(f => f.DistanceSquared).Select(f => f.Pos).Take(4).ToList();
        var cands = Candidates();
        if (cands.Count == 0)
        {
            await ctx.Game.WaitUntilAsync(() => Candidates().Count > 0, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            cands = Candidates();
        }
        if (cands.Count == 0) return "no infinite water to refill the bucket from";
        string last = "";
        foreach (var src in cands)
        {
            var f = await BucketOps.FillAsync(ctx, src, ct).ConfigureAwait(false);
            if (f.Ok) return null;
            last = f.Text;
        }
        return $"couldn't refill the bucket: {last}";
    }

    /// <summary>Water within the field (ring + 2) that isn't a trench cell and wasn't there when the run started.</summary>
    private static List<BlockPos> StrayWater(SkillContext ctx, FieldGeom g, HashSet<BlockPos> trench)
    {
        var found = new List<BlockPos>();
        var centre = g.At(g.Length / 2, g.Width / 2).Center;
        double rad = Math.Max(g.Length, g.Width) / 2.0 + 4;
        foreach (var f in ctx.World.FindBlocks(centre, rad, (id, _) => BlockShapes.IsWater(id), 4096))
        {
            if (trench.Contains(f.Pos) || f.Pos.Y < g.Y - 1 || f.Pos.Y > g.Y + 2) continue;
            if (f.Pos.X < Math.Min(g.At(-3, -3).X, g.At(g.Length + 2, g.Width + 2).X) || f.Pos.X > Math.Max(g.At(-3, -3).X, g.At(g.Length + 2, g.Width + 2).X)) continue;
            if (f.Pos.Z < Math.Min(g.At(-3, -3).Z, g.At(g.Length + 2, g.Width + 2).Z) || f.Pos.Z > Math.Max(g.At(-3, -3).Z, g.At(g.Length + 2, g.Width + 2).Z)) continue;
            found.Add(f.Pos);
        }
        return found;
    }

    // ------------------------------------------------------------------ tilling

    private static async Task<string?> TillAsync(SkillContext ctx, FieldGeom g, int hoe, Tally t, CancellationToken ct)
    {
        int row = 0;
        foreach (int v in RowsFarSideFirst(ctx, g))
        {
            var us = Enumerable.Range(0, g.Length);
            foreach (int u in row++ % 2 == 0 ? us : us.Reverse())
            {
                ct.ThrowIfCancellationRequested();
                if (await TillCellAsync(ctx, g.At(u, v), hoe, t, ct).ConfigureAwait(false) is { } failed) return failed;
            }
        }
        return null;
    }

    /// <summary>Rows from the far side toward the near side (tilled serpentine along u): the bot stands on untilled ground behind it.</summary>
    private static List<int> RowsFarSideFirst(SkillContext ctx, FieldGeom g)
    {
        var trenchCols = g.TrenchColumns().ToHashSet();
        int botV = AlongV(g, ctx.Me);
        var vs = Enumerable.Range(0, g.Width).Where(v => !trenchCols.Contains(v)).ToList();
        if (botV > g.Width / 2) vs.Reverse();
        vs.Reverse(); // far side first
        return vs;
    }

    private static async Task<string?> TillCellAsync(SkillContext ctx, BlockPos c, int hoe, Tally t, CancellationToken ct)
    {
        int id = ctx.Block(c);
        if (id == Blocks.Farmland) return null; // never re-hoe farmland
        if (id is not (Blocks.Grass or Blocks.Dirt)) return $"{SkillJson.Fmt(c)} is {SkillWorld.Name(id)}, not tillable ground";
        var up = c.Up();
        if (Plantish(ctx.Block(up)) && !(await ctx.Game.DigAsync(up, ct).ConfigureAwait(false)).Ok)
            return $"couldn't clear {SkillWorld.Name(ctx.Block(up))} at {SkillJson.Fmt(up)}";
        var reach = await ApproachAsync(ctx, c, ct).ConfigureAwait(false);
        if (!reach.Ok) return $"can't reach {SkillJson.Fmt(c)}: {reach.Message}";
        var r = await ctx.Game.UseOnBlockAsync(c, BlockFace.Top, hoe, ct).ConfigureAwait(false);
        if (!r.Ok) return $"hoe at {SkillJson.Fmt(c)}: {r.Message}";
        if (!await ctx.WaitBlockAsync(c, (b, _) => b == Blocks.Farmland, ct).ConfigureAwait(false)) return $"{SkillJson.Fmt(c)} didn't turn to farmland";
        t.Tilled++;
        return null;
    }

    // ------------------------------------------------------------------ fence

    /// <summary>The fence cells (y+1 over the ring) still missing, with the block each needs (Fence, or the gate at the gate).
    /// A ring cell whose ground is already a wall (solid at y+1) needs none.</summary>
    private static List<(BlockPos Pos, int Id)> FenceCells(SkillContext ctx, FieldGeom g)
    {
        var list = new List<(BlockPos, int)>();
        var gate = g.Gate();
        foreach (var (u, v) in g.Ring())
        {
            var p = g.At(u, v).Up();
            int have = ctx.Block(p);
            bool isGate = (u, v) == gate;
            if (have == (isGate ? Blocks.FenceGate : Blocks.Fence)) continue;
            if (have != 0 && !Plantish(have) && BlockShapes.HasCollision(ctx.World, p.X, p.Y, p.Z) && have is not (Blocks.Fence or Blocks.FenceGate)) continue; // a wall of terrain
            list.Add((p, isGate ? Blocks.FenceGate : Blocks.Fence));
        }
        return list;
    }

    /// <summary>Every ~6th fence post along the ring (not the gate) carries a torch on top.</summary>
    private static IEnumerable<BlockPos> TorchSpots(FieldGeom g, List<(BlockPos Pos, int Id)> missingFence)
    {
        var gate = g.Gate();
        int i = 0;
        foreach (var (u, v) in g.Ring())
        {
            if (i++ % 6 != 3 || (u, v) == gate) continue;
            yield return g.At(u, v).Up(2);
        }
    }

    private static async Task<string?> FenceAsync(SkillContext ctx, FieldGeom g, Tally t, CancellationToken ct)
    {
        foreach (var (p, id) in FenceCells(ctx, g))
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Has(id)) return $"out of {(id == Blocks.Fence ? "fence" : "fence gates")}";
            await ApproachAsync(ctx, p, ct).ConfigureAwait(false);
            var r = await ctx.Game.PlaceAsync(p, id, ct).ConfigureAwait(false);
            if (!r.Ok) return $"{(id == Blocks.Fence ? "fence" : "gate")} at {SkillJson.Fmt(p)}: {r.Message}";
            if (id == Blocks.Fence) t.Fences++; else { t.Fences++; t.Gate = true; }
        }
        var torchSpots = TorchSpots(g, []).Where(s => ctx.Block(s) != Blocks.Torch && ctx.Block(s.Down()) == Blocks.Fence).ToList();
        foreach (var s in torchSpots)
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Has(Blocks.Torch)) return "out of torches";
            await ApproachAsync(ctx, s, ct).ConfigureAwait(false);
            var r = await ctx.Game.PlaceAsync(s, Blocks.Torch, BlockFace.Top, ct).ConfigureAwait(false);
            if (r.Ok) t.Torches++;
        }
        return null;
    }
}
