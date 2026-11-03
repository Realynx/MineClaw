using System.Text.Json;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Skills;

/// <summary>
/// One farm field: the crops and farmland connected to the nearest one (gaps of one block, e.g. a water row, still join), so
/// every bot finds the same field wherever it stands. Cut into strips across its long axis: the unit a crew splits by.
/// </summary>
internal sealed class FarmField
{
    public required List<BlockPos> Crops { get; init; }
    public required List<BlockPos> Soil { get; init; }
    public int MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
    private bool AlongX => MaxX - MinX >= MaxZ - MinZ;

    /// <summary>A strip of the field with its lease key (the full field height ±1).</summary>
    public sealed record Strip(int Index, ClaimKey Key)
    {
        public Vec3d Centre => new((Key.Min.X + Key.Max.X) / 2.0 + 0.5, Key.Min.Y + 1, (Key.Min.Z + Key.Max.Z) / 2.0 + 0.5);
    }

    public const int StripWidth = 3;
    private const int MaxCells = 2048;

    public static FarmField Find(WorldState w, Vec3d feet, double radius)
    {
        static bool Farm(int id) => CropKinds.IsCrop(id) || id == Blocks.Farmland;
        var seed = w.FindBlocks(feet, radius, (id, _) => Farm(id), 1);
        var seen = new HashSet<BlockPos>();
        if (seed.Count > 0)
        {
            var queue = new Queue<BlockPos>();
            queue.Enqueue(seed[0].Pos);
            seen.Add(seed[0].Pos);
            while (queue.Count > 0 && seen.Count < MaxCells)
            {
                var p = queue.Dequeue();
                for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var n = p.Offset(dx, dy, dz);
                    if ((dx | dy | dz) == 0 || seen.Contains(n) || !Farm(w.GetBlock(n.X, n.Y, n.Z))) continue;
                    seen.Add(n);
                    queue.Enqueue(n);
                }
            }
        }
        var crops = seen.Where(p => CropKinds.IsCrop(w.GetBlock(p.X, p.Y, p.Z))).ToList();
        var soil = seen.Where(p => w.GetBlock(p.X, p.Y, p.Z) == Blocks.Farmland).ToList();
        var f = new FarmField { Crops = crops, Soil = soil };
        if (seen.Count > 0)
        {
            f.MinX = seen.Min(p => p.X); f.MaxX = seen.Max(p => p.X);
            f.MinY = seen.Min(p => p.Y); f.MaxY = seen.Max(p => p.Y);
            f.MinZ = seen.Min(p => p.Z); f.MaxZ = seen.Max(p => p.Z);
        }
        return f;
    }

    /// <summary>Strips <see cref="StripWidth"/> rows wide across the long axis (claim-based crew split).</summary>
    public List<Strip> Strips()
    {
        int lo = AlongX ? MinX : MinZ, hi = AlongX ? MaxX : MaxZ;
        var list = new List<Strip>();
        for (int a = lo, i = 0; a <= hi; a += StripWidth, i++) list.Add(Band(i, a, Math.Min(hi, a + StripWidth - 1)));
        return list;
    }

    /// <summary><paramref name="n"/> near-equal bands along the long axis (a fixed i/n share each; some empty if n &gt; length).</summary>
    public List<Strip> Bands(int n)
    {
        int lo = AlongX ? MinX : MinZ, len = (AlongX ? MaxX : MaxZ) - lo + 1;
        var list = new List<Strip>();
        for (int i = 0; i < n; i++)
        {
            int a = lo + i * len / n, b = lo + (i + 1) * len / n - 1;
            if (b >= a) list.Add(Band(i, a, b));
            else list.Add(new Strip(i, ClaimKey.Area(new BlockPos(int.MaxValue, 0, int.MaxValue), new BlockPos(int.MaxValue, 0, int.MaxValue))));
        }
        return list;
    }

    private Strip Band(int i, int a, int b) => new(i, AlongX
        ? ClaimKey.Area(new BlockPos(a, MinY - 1, MinZ), new BlockPos(b, MaxY + 1, MaxZ))
        : ClaimKey.Area(new BlockPos(MinX, MinY - 1, a), new BlockPos(MaxX, MaxY + 1, b)));

    /// <summary>"2/3" (or "2 of 3") → (2, 3); null when absent or malformed.</summary>
    public static (int I, int N)? ParsePart(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^\s*(\d+)\s*(?:/|of)\s*(\d+)\s*$");
        if (!m.Success) return null;
        int i = int.Parse(m.Groups[1].Value), n = int.Parse(m.Groups[2].Value);
        return n is >= 1 and <= 16 && i >= 1 && i <= n ? (i, n) : null;
    }
}

internal static class FarmOps
{
    public static readonly int[] Hoes = [Items.WoodenHoe, Items.StoneHoe, Items.IronHoe, Items.GoldenHoe, Items.DiamondHoe];

    public static async Task CollectAsync(SkillContext ctx, double radius, CancellationToken ct)
    {
        try { await ctx.Game.CollectDropsAsync(radius, ct); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// The field around <paramref name="crops"/> (their bounding box + 2, same layer ±1): every farmland cell with nothing on it
    /// gets a seed (two passes: a placement the server rejected is retried once), then the field is counted from the world.
    /// Text: "; field: 24/24 farmland planted" or "; field: 21/24 planted, 3 empty: out of seeds".
    /// </summary>
    public static async Task<(int Planted, string Text)> FillFieldAsync(SkillContext ctx, IReadOnlyList<BlockPos> crops, int radius, CancellationToken ct,
        Func<BlockPos, bool>? within = null)
    {
        if (crops.Count == 0) return (0, "");
        int minX = crops.Min(p => p.X) - 2, maxX = crops.Max(p => p.X) + 2, minZ = crops.Min(p => p.Z) - 2, maxZ = crops.Max(p => p.Z) + 2;
        int minY = crops.Min(p => p.Y) - 2, maxY = crops.Max(p => p.Y);
        // Farmland is planted INTO, never dug: the protection filter of ctx.Find (config "farmland" is soft-protected as a block)
        // must not hide it (live: the field check found no farmland at all, "3 plots may still be empty").
        List<BlockPos> Farmland() => ctx.World.FindBlocks(ctx.Feet, radius + 2, (id, _) => id == Blocks.Farmland, int.MaxValue)
            .Select(f => f.Pos).Where(p => p.X >= minX && p.X <= maxX && p.Z >= minZ && p.Z <= maxZ && p.Y >= minY && p.Y <= maxY
                                           && within?.Invoke(p) != false).ToList();
        bool Empty(BlockPos soil) => SkillWorld.Air(ctx.World, soil.Up());
        int planted = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            var empty = Farmland().Where(Empty).ToList();
            if (empty.Count == 0 || !ctx.Has(Items.Seeds)) break;
            foreach (var soil in empty)
            {
                ct.ThrowIfCancellationRequested();
                if (!ctx.Has(Items.Seeds)) break;
                if (await PlantAsync(ctx, soil, Items.Seeds, ct) && await ctx.WaitBlockAsync(soil.Up(), (id, _) => id == Blocks.Crops, ct)) planted++;
            }
        }
        var all = Farmland();
        int left = all.Count(Empty);
        string text = all.Count == 0 ? ""
            : left == 0 ? $"; field: {all.Count}/{all.Count} farmland planted"
            : $"; field: {all.Count - left}/{all.Count} planted, {left} empty" + (!ctx.Has(Items.Seeds) ? ": out of seeds (need " + left + " more)" : ": couldn't plant them");
        return (planted, text);
    }

    /// <summary>Plants <paramref name="item"/> in the empty cell above <paramref name="soil"/>; verified by the block appearing.</summary>
    public static async Task<bool> PlantAsync(SkillContext ctx, BlockPos soil, int item, CancellationToken ct)
    {
        var cell = soil.Up();
        if (!SkillWorld.Air(ctx.World, cell) || !ctx.Has(item)) return false;
        var r = await ctx.Game.PlaceAsync(cell, item, ct);
        return r.Ok;
    }
}

[Skill]
public sealed class HarvestWheatSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "harvest_wheat", Category = SkillCategory.Farming,
        Summary = "harvest ripe wheat, hoe, replant; crews split it (claims)",
        Args = "radius?,replant?,part?",
        Schema = SkillJson.Schema("radius:int?,replant:bool?,part:str?"), Estimate = TimeSpan.FromSeconds(40), Pre = new() { Dimension = Dim.Overworld },
        Keywords = "wheat crops farm harvest ripe bread seeds replant reap hoe till carrots potatoes beetroot",
    };

    /// <summary>Lease on one strip of the field while this bot works it (renewed per strip; released when the skill ends).</summary>
    internal static readonly TimeSpan StripTtl = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Fox's farming procedure (live 10-27): per cell, always harvest → hoe → plant. The hoe goes on every time, even on what
    /// looks like farmland: a trampled or dried-out cell is dirt again (mc-dev BlockSoil: dry farmland with nothing planted
    /// reverts; an entity walking on it reverts it 1 in 4), and a hoe on farmland is a harmless no-op (mc-dev ItemHoe only
    /// tills grass/dirt with air above). No hoe: one is acquired (chest, hive, craft 2 sticks + 2 planks/cobble); still none =
    /// plant on whatever is farmland already.
    /// </summary>
    /// <remarks>
    /// One field, several bots (live 10-28, "the 3 of you harvest the grown wheat": main's worker refused the whole field as
    /// "protected, owned by others", the clones each ran the whole field anyway). The field (<see cref="FarmField"/>: its
    /// connected crops + farmland, the same for every bot whatever its position) is cut into strips across its long axis; this
    /// bot works only the strips it holds a hive lease on (<c>ClaimKey.Area</c>, taken nearest first, the way gen cells
    /// at=auto are), so the crew's parts are disjoint and nobody waits for anyone's permission: another bot's strip is just
    /// "their part". <c>part=i/n</c> takes a fixed share instead (n equal bands, the i-th: a planner-made split, like
    /// <c>VoxelPlan.Partition</c>). Ripeness is the crop's metadata (7 = ripe); growing crops are never cut.
    /// </remarks>
    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int radius = Clamp(args.Int("radius"), 12, 2, 24);
        bool replant = args.Flag("replant", true);
        var field = FarmField.Find(ctx.World, ctx.Feet, radius);
        int growing = field.Crops.Count(c => !CropKinds.Ripe(ctx.Block(c), ctx.Meta(c)));
        // Ripe = stage 7 and not held back by the policy (a player's never-break mark; a crop another bot holds is its part).
        var ripeAll = field.Crops.Where(c => CropKinds.Ripe(ctx.Block(c), ctx.Meta(c))).ToList();
        if (ripeAll.Count == 0) return Fail($"no ripe wheat within {radius} ({growing} still growing)");
        var part = FarmField.ParsePart(args.Str("part"));
        var strips = part is { } pt ? field.Bands(pt.N).Skip(pt.I - 1).Take(1).ToList() : field.Strips();
        var refused = new List<ClaimKey>();
        int theirs = part != null ? ripeAll.Count(c => !strips.Any(s => s.Key.Contains(c))) : 0;
        var st = new Tally();
        st.ExistingDrops.UnionWith(ctx.World.Entities.All().Where(e => e.Kind == EntityKind.Item).Select(e => e.Id));
        bool any = false;
        // One strip at a time, nearest first: lease it, harvest + replant it, release it (its crops are growing again, so the
        // next bot passes it by), then the next. The crew interleaves strip by strip; no bot sits on the whole field.
        foreach (var strip in strips.OrderBy(s => s.Centre.DistanceSquaredTo(ctx.Feet)).ThenBy(s => s.Index))
        {
            ct.ThrowIfCancellationRequested();
            var inStrip = field.Crops.Where(c => strip.Key.Contains(c) && CropKinds.Ripe(ctx.Block(c), ctx.Meta(c))).ToList();
            if (inStrip.Count == 0) continue;
            var lease = await ctx.Claims.TryClaimAsync(strip.Key, StripTtl, "harvest_wheat", ct);
            if (!lease.Ok) { theirs += inStrip.Count; refused.Add(strip.Key); continue; } // another bot's part: theirs, not "protected"
            // Another bot may have harvested it while the lease was on its way: what is still ripe now.
            inStrip = inStrip.Where(c => CropKinds.Ripe(ctx.Block(c), ctx.Meta(c))).ToList();
            if (inStrip.Count > 0) any = true;
            try { if (inStrip.Count > 0) await HarvestCellsAsync(ctx, inStrip, replant, st, ct); }
            finally
            {
                try { await ctx.Claims.ReleaseAsync(strip.Key, CancellationToken.None); } catch (Exception) { /* the lease times out */ }
            }
        }
        // Replanting covers my share: my band (part=i/n), else the field minus the strips other bots held.
        Func<BlockPos, bool> fillable = part != null
            ? p => strips.Any(s => s.Key.Contains(p.Up()) || s.Key.Contains(p))
            : p => !refused.Any(k => k.Contains(p.Up()) || k.Contains(p));
        string crew = theirs > 0 ? $"; {theirs} ripe in parts other hive bots are working (theirs)" : "";
        // Every ripe strip went to other bots (or they harvested it first): a done share, not a failure.
        if (!any) return Ok("nothing left in my part" + (crew.Length > 0 ? crew : ": the crew harvested it"));
        var result = await FinishAsync(ctx, st, growing, replant, radius, fillable, ct);
        return result.Ok ? Ok(result.Text + crew) : Fail(result.Text + crew);
    }

    private sealed class Tally
    {
        public int Cut, Planted, Tilled;
        public bool OtherCrops;
        public int? Hoe;
        public bool HoeChecked;
        public string? HoeNote;
        public readonly List<BlockPos> Harvested = new();
        public readonly HashSet<int> ExistingDrops = new(), Drops = new(), DropItems = new();
        public TimeSpan PickupTimeLeft = TimeSpan.FromSeconds(4);
    }

    private static void ObserveDrops(SkillContext ctx, Tally st)
    {
        foreach (var drop in ctx.World.Entities.All().Where(e => e.Kind == EntityKind.Item
                     && st.DropItems.Contains(e.Item.Id) && !st.ExistingDrops.Contains(e.Id)
                     && st.Harvested.Any(p => p.Center.DistanceSquaredTo(e.Position) <= 16)))
            st.Drops.Add(drop.Id);
    }

    // Pickup is opportunistic, not a precondition for harvesting the next crop. One drifting stack must not consume the
    // whole job: at most two seconds per attempt and four seconds across the field, always from dry ground/the rim.
    private static async Task CollectHarvestDropsAsync(SkillContext ctx, Tally st, int radius, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObserveDrops(ctx, st);
        if (st.PickupTimeLeft <= TimeSpan.Zero) return;
        using var pickup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pickup.CancelAfter(st.PickupTimeLeft < TimeSpan.FromSeconds(2) ? st.PickupTimeLeft : TimeSpan.FromSeconds(2));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { await ctx.Game.CollectDropsAsync(radius, pickup.Token, dryOnly: true); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { ctx.Log?.Invoke($"harvest_wheat: skipped pickup: {ex.Message}"); }
        finally
        {
            st.PickupTimeLeft -= clock.Elapsed;
            ObserveDrops(ctx, st);
        }
    }

    /// <summary>harvest → hoe → plant on each ripe cell (nearest first).</summary>
    private async Task HarvestCellsAsync(SkillContext ctx, List<BlockPos> cells, bool replant, Tally st, CancellationToken ct)
    {
        var ripe = cells.Select(p => new FoundBlock(p, Blocks.Crops, 7, p.Center.DistanceSquaredTo(ctx.Feet))).OrderBy(f => f.DistanceSquared).ToList();
        bool canHoe = replant && (ctx.Game.Supported & Primitive.UseOnBlock) != 0;
        if (!st.HoeChecked)
        {
            st.HoeChecked = true;
            if (canHoe && ctx.FirstOwned(FarmOps.Hoes) is null && ctx.Registry.AcquireMissing is { } acquire)
            {
                try { st.HoeNote = await acquire(ctx, Spec, FarmOps.Hoes, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { ctx.Log?.Invoke($"harvest_wheat: hoe acquisition threw: {ex.Message}"); }
            }
            st.Hoe = canHoe ? ctx.FirstOwned(FarmOps.Hoes) : null;
        }
        int? hoe = st.Hoe;
        foreach (var c in ripe)
        {
            ct.ThrowIfCancellationRequested();
            int crop = ctx.Block(c.Pos);
            if (!CropKinds.Ripe(crop, ctx.Meta(c.Pos)) || ctx.Protected(c.Pos)) continue;
            var d = await ctx.Game.DigAsync(c.Pos, ct);                                        // 1. harvest
            if (!d.Ok) continue;
            st.Cut++;
            if (crop != Blocks.Crops) st.OtherCrops = true;
            st.Harvested.Add(c.Pos);
            st.DropItems.Add(CropKinds.Seed(crop));
            if (crop == Blocks.Crops) st.DropItems.Add(Items.Wheat);
            if (!replant) continue;
            var soil = c.Pos.Down();
            await CollectHarvestDropsAsync(ctx, st, 3, ct);
            if (hoe is { } h && ctx.Block(soil) is Blocks.Farmland or Blocks.Dirt or Blocks.Grass && SkillWorld.Air(ctx.World, c.Pos))
            {
                bool wasSoil = ctx.Block(soil) != Blocks.Farmland;
                var u = await ctx.Game.UseOnBlockAsync(soil, BlockFace.Top, h, ct);           // 2. hoe (always)
                if (u.Ok && wasSoil && await ctx.WaitBlockAsync(soil, (id, _) => id == Blocks.Farmland, ct)) st.Tilled++;
            }
            int seed = CropKinds.Seed(crop);
            if (!ctx.Has(seed)) seed = Items.Seeds;
            if (ctx.Block(soil) == Blocks.Farmland && await FarmOps.PlantAsync(ctx, soil, seed, ct)) st.Planted++; // 3. plant (the same crop)
        }
    }

    private async Task<SkillResult> FinishAsync(SkillContext ctx, Tally st, int growing, bool replant, int radius, Func<BlockPos, bool> fillable,
        CancellationToken ct)
    {
        await CollectHarvestDropsAsync(ctx, st, radius, ct);
        // 4. Verify the whole field (live 10-28, 420: "done, maybe 3 empty plots left"; Robin: "420 didn't finish replanting"):
        // every farmland cell of it gets a crop, from the seeds just picked up; the count reported is the real one.
        string field = "";
        if (replant && st.Cut > 0)
        {
            var check = await FarmOps.FillFieldAsync(ctx, st.Harvested, radius, ct, fillable);
            st.Planted += check.Planted;
            field = check.Text;
        }
        string re = $"replanted {st.Planted}" + (st.Tilled > 0 ? $" (re-tilled {st.Tilled})" : "")
                    + (replant && st.HoeChecked && st.Hoe is null ? " (no hoe: planted only on farmland)" : "");
        ObserveDrops(ctx, st);
        int left = st.Drops.Count(id => ctx.World.Entities.Get(id) is not null);
        return st.Cut > 0 ? Ok($"harvested {st.Cut} {(st.OtherCrops ? "crops" : "wheat")}, {re} (wheat {ctx.Count(Items.Wheat)}, seeds {ctx.Count(Items.Seeds)}; {growing} growing)"
                               + field + $"; {left} drop(s) left behind" + (st.HoeNote != null ? "; " + st.HoeNote : ""))
                          : Fail("couldn't harvest any wheat");
    }
}

[Skill]
public sealed class PlantSeedsSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "plant_seeds", Category = SkillCategory.Farming, Summary = "plant seeds on empty farmland", Args = "radius?,item?",
        Schema = SkillJson.Schema("radius:int?,item:str?"), Estimate = TimeSpan.FromSeconds(20),
        Pre = new() { Items = [[Items.Seeds, Items.MelonSeeds, Items.PumpkinSeeds]], Needs = "seeds", Dimension = Dim.Overworld },
        Keywords = "plant seeds sow farmland farm crops melon pumpkin",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int item = SkillJson.Item(args.Str("item")) is { } i ? i.Id : ctx.FirstOwned(Items.Seeds, Items.MelonSeeds, Items.PumpkinSeeds) ?? Items.Seeds;
        // Farmland is planted into, not dug: not filtered by the dig protection (config "farmland" is soft-protected as a block).
        var soil = ctx.World.FindBlocks(ctx.Feet, Clamp(args.Int("radius"), 10, 2, 24), (id, _) => id == Blocks.Farmland, int.MaxValue)
            .Where(f => SkillWorld.Air(ctx.World, f.Pos.Up())).Take(64).ToList();
        if (soil.Count == 0) return Fail("no empty farmland nearby (till_soil makes some)");
        int n = 0;
        foreach (var f in soil)
        {
            if (!ctx.Has(item)) break;
            if (await FarmOps.PlantAsync(ctx, f.Pos, item, ct)) n++;
        }
        return n > 0 ? Ok($"planted {n} {Item(item)}") : Fail($"couldn't plant on {soil.Count} farmland");
    }
}

[Skill]
public sealed class TillSoilSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "till_soil", Category = SkillCategory.Farming, Summary = "hoe dirt/grass near water into farmland, plant", Args = "pos?,count?",
        Schema = SkillJson.Schema("pos:pos?,count:int?"), Estimate = TimeSpan.FromSeconds(30),
        Pre = new() { Items = [FarmOps.Hoes], Needs = "hoe", Dimension = Dim.Overworld, Primitives = Primitive.UseOnBlock },
        Keywords = "till hoe farmland dirt grass farm prepare soil plow",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int want = Clamp(args.Int("count"), 4, 1, 32);
        int hoe = ctx.FirstOwned(FarmOps.Hoes)!.Value;
        // pos names the patch center, which may be water or existing farmland. Hoe only dirt/grass with air above near
        // water, nearest to pos first; existing farmland counts as done and is planted if empty.
        var given = args.Pos();
        var near = ctx.World.FindBlocks(given?.Center ?? ctx.Feet, given is null ? 8 : PatchRadius,
                (id, _) => id is Blocks.Dirt or Blocks.Grass or Blocks.Farmland, int.MaxValue)
            .Where(f => SkillWorld.Air(ctx.World, f.Pos.Up()) && NearWater(ctx.World, f.Pos)).ToList();
        var spots = near.Where(f => f.Id != Blocks.Farmland && !ctx.Game.IsProtected(f.Pos, f.Id))
            .Concat(near.Where(f => f.Id == Blocks.Farmland)).Take(want).ToList();
        if (spots.Count == 0) return Fail("no dirt/grass within 4 of water with air above");
        int tilled = 0, planted = 0, already = 0;
        foreach (var s in spots)
        {
            if (ctx.Block(s.Pos) == Blocks.Farmland)
            {
                already++;
                if (ctx.Has(Items.Seeds) && await FarmOps.PlantAsync(ctx, s.Pos, Items.Seeds, ct)) planted++;
                continue;
            }
            var reach = await ctx.ReachAsync(s.Pos, ct);
            if (!reach.Ok) continue;
            var r = await ctx.Game.UseOnBlockAsync(s.Pos, BlockFace.Top, hoe, ct);
            if (!r.Ok) return tilled > 0 ? Ok($"tilled {tilled}, then {r.Message}") : SkillResult.From(r);
            if (!await ctx.WaitBlockAsync(s.Pos, (id, _) => id == Blocks.Farmland, ct)) continue;
            tilled++;
            if (await FarmOps.PlantAsync(ctx, s.Pos, Items.Seeds, ct)) planted++;
        }
        string done = already > 0 ? $" ({already} already farmland)" : "";
        return tilled > 0 || already > 0 ? Ok($"tilled {tilled}, planted {planted}{done}") : Fail("nothing tilled (protected or no block change)");
    }

    /// <summary>Around a given pos: the hydration range (farmland within 4 of water stays wet).</summary>
    private const double PatchRadius = 4.5;

    /// <summary>b1.8.1 farmland stays wet (and doesn't revert) within 4 blocks of water.</summary>
    private static bool NearWater(IBlockAccess w, BlockPos p)
    {
        for (int dx = -4; dx <= 4; dx++)
        for (int dz = -4; dz <= 4; dz++)
        for (int dy = 0; dy <= 1; dy++)
            if (BlockShapes.IsWater(w.GetBlock(p.X + dx, p.Y + dy, p.Z + dz))) return true;
        return false;
    }
}

/// <summary>Sugar cane and cactus: break the second block of each column so the bottom keeps growing.</summary>
public abstract class ColumnHarvestSkill : Skill
{
    protected abstract int BlockId { get; }
    protected abstract int ItemId { get; }

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int radius = Clamp(args.Int("radius"), 12, 2, 24);
        var bottoms = ctx.Find((id, _) => id == BlockId, radius, 256)
            .Where(f => ctx.Block(f.Pos.Down()) != BlockId && ctx.Block(f.Pos.Up()) == BlockId)
            .Select(f => f.Pos).ToList();
        if (bottoms.Count == 0) return Fail($"no {SkillWorld.Name(BlockId)} taller than 1 within {radius}");
        int before = ctx.Count(ItemId), cols = 0;
        foreach (var b in bottoms)
        {
            ct.ThrowIfCancellationRequested();
            var cut = b.Up();
            if (ctx.Block(cut) != BlockId || ctx.Protected(cut)) continue;
            var d = await ctx.Game.DigAsync(cut, ct); // everything above pops off too
            if (d.Ok) cols++;
        }
        await FarmOps.CollectAsync(ctx, radius, ct);
        int got = ctx.Count(ItemId) - before;
        return cols > 0 ? Ok($"cut {cols} {SkillWorld.Name(BlockId)} column(s), bottoms left; +{got} ({ctx.Count(ItemId)} total)")
                        : Fail($"couldn't cut any {SkillWorld.Name(BlockId)}");
    }
}

[Skill]
public sealed class HarvestCaneSkill : ColumnHarvestSkill
{
    protected override int BlockId => Blocks.SugarCane;
    protected override int ItemId => Items.SugarCane;
    public override SkillSpec Spec { get; } = new()
    {
        Name = "harvest_cane", Category = SkillCategory.Farming, Summary = "cut sugar cane, leave bottom blocks", Args = "radius?",
        Schema = SkillJson.Schema("radius:int?"), Estimate = TimeSpan.FromSeconds(30), Pre = new() { Dimension = Dim.Overworld },
        Keywords = "sugar cane reeds paper harvest collect gather sugar",
    };
}

[Skill]
public sealed class HarvestCactusSkill : ColumnHarvestSkill
{
    protected override int BlockId => Blocks.Cactus;
    protected override int ItemId => Blocks.Cactus;
    public override SkillSpec Spec { get; } = new()
    {
        Name = "harvest_cactus", Category = SkillCategory.Farming, Summary = "cut cactus from reach, leave bottoms, collect", Args = "radius?",
        Schema = SkillJson.Schema("radius:int?"), Estimate = TimeSpan.FromSeconds(30), Pre = new() { Dimension = Dim.Overworld },
        Keywords = "cactus green dye harvest collect spiky",
    };
}

[Skill]
public sealed class HarvestMelonsSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "harvest_melons", Category = SkillCategory.Farming, Summary = "break grown melons/pumpkins, keep stems", Args = "radius?",
        Schema = SkillJson.Schema("radius:int?"), Estimate = TimeSpan.FromSeconds(30), Pre = new() { Dimension = Dim.Overworld },
        Keywords = "melon pumpkin slices harvest stem fruit",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int radius = Clamp(args.Int("radius"), 12, 2, 24);
        var fruit = ctx.Find((id, _) => id is Blocks.Melon or Blocks.Pumpkin, radius)
            .Where(f => SkillWorld.Horizontal.Any(o => ctx.Block(f.Pos + o) is Blocks.MelonStem or Blocks.PumpkinStem)).ToList();
        if (fruit.Count == 0) return Fail("no grown melon/pumpkin next to a stem");
        int n = 0;
        foreach (var f in fruit)
            if ((await ctx.Game.DigAsync(f.Pos, ct)).Ok) n++;
        await FarmOps.CollectAsync(ctx, radius, ct);
        return n > 0 ? Ok($"harvested {n} (melon slices {ctx.Count(Items.MelonSlice)}, pumpkins {ctx.Count(Blocks.Pumpkin)}); stems kept")
                     : Fail("couldn't break any");
    }
}

[Skill]
public sealed class BonemealSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "apply_bonemeal", Category = SkillCategory.Farming, Summary = "bonemeal crops/saplings to grow now", Args = "pos?,count?",
        Schema = SkillJson.Schema("pos:pos?,count:int?"), Estimate = TimeSpan.FromSeconds(15),
        Pre = new() { Needs = "bone meal", Dimension = Dim.Overworld, Primitives = Primitive.UseOnBlock },
        Keywords = "bonemeal bone meal fertilize grow sapling crops instant",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (!ctx.Has(Items.Dye, Items.BoneMeal)) return Fail("no bone meal");
        int want = Clamp(args.Int("count"), 3, 1, 16);
        // ctx.Find hides crops protected against digging. Bone meal grows them without breaking anything.
        static bool Growing(int id, int m) => (CropKinds.IsCrop(id) && !CropKinds.Ripe(id, m)) || id == Blocks.Sapling;
        var targets = args.Pos() is { } p ? [p]
            : ctx.World.FindBlocks(ctx.Feet, 8, Growing, int.MaxValue).Select(f => f.Pos).Take(want).ToList();
        if (targets.Count == 0) return Fail("no growing crops or saplings nearby");
        int n = 0, uses = 0;
        foreach (var t in targets)
        {
            // b1.8.1 grows a crop or sapling fully at once; 1.12.2 (ItemDye.applyBonemeal) adds 2-5 crop stages, a sapling stage
            // 45% of the time: keep using it on the same one until it is ripe / a tree, like a player clicking again.
            bool grew = false;
            for (int i = 0; i < MaxUsesPerTarget && Growing(ctx.Block(t), ctx.Meta(t)); i++)
            {
                if (!ctx.Has(Items.Dye, Items.BoneMeal)) break;
                var reach = await ctx.ReachAsync(t, ct);
                if (!reach.Ok) break;
                int id0 = ctx.Block(t), m0 = ctx.Meta(t), meal0 = ctx.Count(Items.Dye);
                var r = await ctx.Game.UseOnBlockAsync(t, BlockFace.Top, Items.Dye, ct);
                if (!r.Ok) return n > 0 ? Ok($"bonemealed {n} ({uses} bone meal), then {r.Message}") : SkillResult.From(r);
                uses++;
                if (await ctx.WaitBlockAsync(t, (id, m) => id != id0 || m != m0, ct)) grew = true;
                else if (ctx.Count(Items.Dye) >= meal0) break; // not taken and nothing grew: it won't take bone meal
            }
            if (grew) n++;
        }
        return n > 0 ? Ok($"bonemealed {n} ({uses} bone meal)") : Fail("nothing grew");
    }

    /// <summary>1.12.2: 2-5 stages a use, so a seedling (0/7) is ripe after at most 4; a sapling 45% a use (two stages).</summary>
    private const int MaxUsesPerTarget = 6;
}

[Skill]
public sealed class PlantSaplingSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "plant_sapling", Category = SkillCategory.Farming, Summary = "plant a sapling on dirt/grass with room to grow", Args = "pos?",
        Schema = SkillJson.Schema("pos:pos?"), Estimate = TimeSpan.FromSeconds(10),
        Pre = new() { Items = [[Blocks.Sapling]], Needs = "sapling", Dimension = Dim.Overworld },
        Keywords = "sapling tree plant grow wood forest replant",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var soil = args.Pos() is { } p ? (ctx.Block(p) is Blocks.Dirt or Blocks.Grass ? p : p.Down()) : Spot(ctx);
        if (soil is not { } s) return Fail("no dirt/grass with 2 blocks clearance and 5 of open sky nearby");
        // An idempotent goal (live 10-27: "skill failed 2x the same way: 9,65,3 is occupied" by the sapling planted before):
        // a sapling or a standing tree there already = done.
        int there = ctx.Block(s.Up());
        if (there == Blocks.Sapling) return Ok($"already planted at {P(s.Up())}");
        if (MineClaw.Client.Trees.Wood.IsLog(there)) return Ok($"already a tree at {P(s.Up())}");
        if (ctx.Block(s) is not (Blocks.Dirt or Blocks.Grass)) return Fail($"{P(s)} is {ctx.BlockName(s)}, needs dirt/grass");
        if (!SkillWorld.Air(ctx.World, s.Up())) return Fail($"{P(s.Up())} is occupied by {Blocks.Get(there).Name}");
        // Someone standing on the spot: wait a moment, then skip it (ok, so a replant loop carries on with the other spots).
        if (await WaitClearAsync(ctx, s.Up(), ct) is { } who) return Ok($"skipped {P(s.Up())} ({who} standing there)");
        return await FarmOps.PlantAsync(ctx, s, Blocks.Sapling, ct) ? Ok($"planted sapling at {P(s.Up())}") : Fail($"sapling didn't take at {P(s.Up())}");
    }

    /// <summary>Who (another player/bot, not us) stands in <paramref name="cell"/> after up to ~3 s of waiting; null = clear.</summary>
    internal static async Task<string?> WaitClearAsync(SkillContext ctx, BlockPos cell, CancellationToken ct, int waitMs = 3000)
    {
        string? Occupant()
        {
            foreach (var e in ctx.World.Entities.All())
            {
                if (e.Kind != EntityKind.Player || e.Id == ctx.World.Player.EntityId) continue;
                var box = AABB.ForPlayer(e.Position);
                if (box.MaxX > cell.X && box.MinX < cell.X + 1 && box.MaxZ > cell.Z && box.MinZ < cell.Z + 1
                    && box.MaxY > cell.Y && box.MinY < cell.Y + 1) return e.Name ?? "someone";
            }
            return null;
        }
        for (int waited = 0; Occupant() is { } who; waited += 250)
        {
            if (waited >= waitMs) return who;
            await Task.Delay(250, ct);
        }
        return null;
    }

    private static BlockPos? Spot(SkillContext ctx) =>
        ctx.Find((id, _) => id is Blocks.Dirt or Blocks.Grass, 8, 128).Select(f => f.Pos)
            .Where(p => p != ctx.Me.Down() && Enumerable.Range(1, 5).All(dy => SkillWorld.Air(ctx.World, p.Up(dy)))
                        && !Near(ctx.World, p, Blocks.Sapling, 2) && !Near(ctx.World, p, Blocks.Log, 2) && !Near(ctx.World, p, MineClaw.Client.Trees.Wood.Log2, 2))
            .Cast<BlockPos?>().FirstOrDefault();

    private static bool Near(IBlockAccess w, BlockPos p, int id, int r)
    {
        for (int dx = -r; dx <= r; dx++)
        for (int dz = -r; dz <= r; dz++)
        for (int dy = 0; dy <= 2; dy++)
            if (w.GetBlock(p.X + dx, p.Y + dy, p.Z + dz) == id) return true;
        return false;
    }
}

/// <summary>Crops on farmland, from data: wheat 59 (ripe at meta 7), and on 1.12.2 carrots 141 / potatoes 142 (7) and
/// beetroots 207 (3). Ids that don't exist in a version never occur in its world.</summary>
public static class CropKinds
{
    public const int Carrots = 141, Potatoes = 142, Beetroots = 207;
    public const int CarrotItem = 391, PotatoItem = 392, BeetrootSeeds = 435;

    public static bool IsCrop(int id) => id is Blocks.Crops or Carrots or Potatoes or Beetroots;
    public static int RipeMeta(int id) => id == Beetroots ? 3 : 7;
    public static bool Ripe(int id, int meta) => IsCrop(id) && meta >= RipeMeta(id);
    /// <summary>What replants the crop: seeds, a carrot, a potato, beetroot seeds.</summary>
    public static int Seed(int id) => id switch { Carrots => CarrotItem, Potatoes => PotatoItem, Beetroots => BeetrootSeeds, _ => Items.Seeds };
}
