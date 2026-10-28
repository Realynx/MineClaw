using System.Text.Json;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

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
    public static async Task<(int Planted, string Text)> FillFieldAsync(SkillContext ctx, IReadOnlyList<BlockPos> crops, int radius, CancellationToken ct)
    {
        if (crops.Count == 0) return (0, "");
        int minX = crops.Min(p => p.X) - 2, maxX = crops.Max(p => p.X) + 2, minZ = crops.Min(p => p.Z) - 2, maxZ = crops.Max(p => p.Z) + 2;
        int minY = crops.Min(p => p.Y) - 2, maxY = crops.Max(p => p.Y);
        List<BlockPos> Farmland() => ctx.Find((id, _) => id == Blocks.Farmland, radius + 2)
            .Select(f => f.Pos).Where(p => p.X >= minX && p.X <= maxX && p.Z >= minZ && p.Z <= maxZ && p.Y >= minY && p.Y <= maxY).ToList();
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
        Name = "harvest_wheat", Category = SkillCategory.Farming, Summary = "harvest ripe wheat (meta 7), hoe, replant seeds", Args = "radius?,replant?",
        Schema = SkillJson.Schema("radius:int?,replant:bool?"), Estimate = TimeSpan.FromSeconds(40), Pre = new() { Dimension = Dim.Overworld },
        Keywords = "wheat crops farm harvest ripe bread seeds replant reap hoe till",
    };

    /// <summary>
    /// Fox's farming procedure (live 10-27): per cell, always harvest → hoe → plant. The hoe goes on every time, even on what
    /// looks like farmland: a trampled or dried-out cell is dirt again (mc-dev BlockSoil: dry farmland with nothing planted
    /// reverts; an entity walking on it reverts it 1 in 4), and a hoe on farmland is a harmless no-op (mc-dev ItemHoe only
    /// tills grass/dirt with air above). No hoe: one is acquired (chest, hive, craft 2 sticks + 2 planks/cobble); still none =
    /// plant on whatever is farmland already.
    /// </summary>
    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int radius = Clamp(args.Int("radius"), 12, 2, 24);
        bool replant = args.Flag("replant", true);
        var ripe = ctx.Find((id, m) => id == Blocks.Crops && m >= 7, radius);
        int growing = ctx.Find((id, m) => id == Blocks.Crops && m < 7, radius).Count;
        if (ripe.Count == 0) return Fail($"no ripe wheat within {radius} ({growing} still growing)");
        bool canHoe = replant && (ctx.Game.Supported & Primitive.UseOnBlock) != 0;
        string? hoeNote = null;
        if (canHoe && ctx.FirstOwned(FarmOps.Hoes) is null && ctx.Registry.AcquireMissing is { } acquire)
        {
            try { hoeNote = await acquire(ctx, Spec, FarmOps.Hoes, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { ctx.Log?.Invoke($"harvest_wheat: hoe acquisition threw: {ex.Message}"); }
        }
        int? hoe = canHoe ? ctx.FirstOwned(FarmOps.Hoes) : null;
        int cut = 0, planted = 0, tilled = 0;
        foreach (var c in ripe)
        {
            ct.ThrowIfCancellationRequested();
            if (ctx.Block(c.Pos) != Blocks.Crops || ctx.Meta(c.Pos) < 7) continue;
            var d = await ctx.Game.DigAsync(c.Pos, ct);                                        // 1. harvest
            if (!d.Ok) continue;
            cut++;
            if (!replant) continue;
            var soil = c.Pos.Down();
            await FarmOps.CollectAsync(ctx, 3, ct);
            if (hoe is { } h && ctx.Block(soil) is Blocks.Farmland or Blocks.Dirt or Blocks.Grass && SkillWorld.Air(ctx.World, c.Pos))
            {
                bool wasSoil = ctx.Block(soil) != Blocks.Farmland;
                var u = await ctx.Game.UseOnBlockAsync(soil, BlockFace.Top, h, ct);           // 2. hoe (always)
                if (u.Ok && wasSoil && await ctx.WaitBlockAsync(soil, (id, _) => id == Blocks.Farmland, ct)) tilled++;
            }
            if (ctx.Block(soil) == Blocks.Farmland && await FarmOps.PlantAsync(ctx, soil, Items.Seeds, ct)) planted++; // 3. plant
        }
        await FarmOps.CollectAsync(ctx, radius, ct);
        // 4. Verify the whole field (live 10-28, 420: "done, maybe 3 empty plots left"; Robin: "420 didn't finish replanting"):
        // every farmland cell of it gets a crop, from the seeds just picked up; the count reported is the real one.
        string field = "";
        if (replant && cut > 0)
        {
            var check = await FarmOps.FillFieldAsync(ctx, ripe.Select(r => r.Pos).ToList(), radius, ct);
            planted += check.Planted;
            field = check.Text;
        }
        string re = $"replanted {planted}" + (tilled > 0 ? $" (re-tilled {tilled})" : "") + (replant && hoe is null ? " (no hoe: planted only on farmland)" : "");
        return cut > 0 ? Ok($"harvested {cut} wheat, {re} (wheat {ctx.Count(Items.Wheat)}, seeds {ctx.Count(Items.Seeds)}; {growing} growing)"
                            + field + (hoeNote != null ? "; " + hoeNote : ""))
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
        var soil = ctx.Find((id, _) => id == Blocks.Farmland, Clamp(args.Int("radius"), 10, 2, 24))
            .Where(f => SkillWorld.Air(ctx.World, f.Pos.Up())).ToList();
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
        var spots = (args.Pos() is { } p ? [new FoundBlock(p, ctx.Block(p), 0, 0)] : ctx.Find((id, _) => id is Blocks.Dirt or Blocks.Grass, 8))
            .Where(f => SkillWorld.Air(ctx.World, f.Pos.Up()) && NearWater(ctx.World, f.Pos)).Take(want).ToList();
        if (spots.Count == 0) return Fail("no dirt/grass within 4 of water with air above");
        int tilled = 0, planted = 0;
        foreach (var s in spots)
        {
            var reach = await ctx.ReachAsync(s.Pos, ct);
            if (!reach.Ok) continue;
            var r = await ctx.Game.UseOnBlockAsync(s.Pos, BlockFace.Top, hoe, ct);
            if (!r.Ok) return tilled > 0 ? Ok($"tilled {tilled}, then {r.Message}") : SkillResult.From(r);
            if (!await ctx.WaitBlockAsync(s.Pos, (id, _) => id == Blocks.Farmland, ct)) continue;
            tilled++;
            if (await FarmOps.PlantAsync(ctx, s.Pos, Items.Seeds, ct)) planted++;
        }
        return tilled > 0 ? Ok($"tilled {tilled}, planted {planted}") : Fail("nothing tilled (protected or no block change)");
    }

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
        var targets = args.Pos() is { } p ? [p]
            : ctx.Find((id, m) => (id == Blocks.Crops && m < 7) || id == Blocks.Sapling, 8).Select(f => f.Pos).Take(want).ToList();
        if (targets.Count == 0) return Fail("no growing crops or saplings nearby");
        int n = 0;
        foreach (var t in targets)
        {
            if (!ctx.Has(Items.Dye, Items.BoneMeal)) break;
            var reach = await ctx.ReachAsync(t, ct);
            if (!reach.Ok) continue;
            int id0 = ctx.Block(t), m0 = ctx.Meta(t);
            var r = await ctx.Game.UseOnBlockAsync(t, BlockFace.Top, Items.Dye, ct);
            if (!r.Ok) return n > 0 ? Ok($"bonemealed {n}, then {r.Message}") : SkillResult.From(r);
            if (await ctx.WaitBlockAsync(t, (id, m) => id != id0 || m != m0, ct)) n++;
        }
        return n > 0 ? Ok($"bonemealed {n}") : Fail("nothing grew");
    }
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
        if (there == Blocks.Log) return Ok($"already a tree at {P(s.Up())}");
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
                        && !Near(ctx.World, p, Blocks.Sapling, 2) && !Near(ctx.World, p, Blocks.Log, 2))
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
