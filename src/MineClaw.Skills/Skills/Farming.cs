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
        Name = "harvest_wheat", Category = SkillCategory.Farming, Summary = "harvest ripe wheat (meta 7) + replant seeds", Args = "radius?,replant?",
        Schema = SkillJson.Schema("radius:int?,replant:bool?"), Estimate = TimeSpan.FromSeconds(40), Pre = new() { Dimension = Dim.Overworld },
        Keywords = "wheat crops farm harvest ripe bread seeds replant reap",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int radius = Clamp(args.Int("radius"), 12, 2, 24);
        bool replant = args.Flag("replant", true);
        var ripe = ctx.Find((id, m) => id == Blocks.Crops && m >= 7, radius);
        int growing = ctx.Find((id, m) => id == Blocks.Crops && m < 7, radius).Count;
        if (ripe.Count == 0) return Fail($"no ripe wheat within {radius} ({growing} still growing)");
        int cut = 0, planted = 0;
        foreach (var c in ripe)
        {
            ct.ThrowIfCancellationRequested();
            if (ctx.Block(c.Pos) != Blocks.Crops || ctx.Meta(c.Pos) < 7) continue;
            var d = await ctx.Game.DigAsync(c.Pos, ct);
            if (!d.Ok) continue;
            cut++;
            if (replant && ctx.Block(c.Pos.Down()) == Blocks.Farmland)
            {
                await FarmOps.CollectAsync(ctx, 3, ct);
                if (await FarmOps.PlantAsync(ctx, c.Pos.Down(), Items.Seeds, ct)) planted++;
            }
        }
        await FarmOps.CollectAsync(ctx, radius, ct);
        return cut > 0 ? Ok($"harvested {cut} wheat, replanted {planted} (wheat {ctx.Count(Items.Wheat)}, seeds {ctx.Count(Items.Seeds)}; {growing} growing)")
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
        if (ctx.Block(s) is not (Blocks.Dirt or Blocks.Grass)) return Fail($"{P(s)} is {ctx.BlockName(s)}, needs dirt/grass");
        if (!SkillWorld.Air(ctx.World, s.Up())) return Fail($"{P(s.Up())} is occupied");
        return await FarmOps.PlantAsync(ctx, s, Blocks.Sapling, ct) ? Ok($"planted sapling at {P(s.Up())}") : Fail($"sapling didn't take at {P(s.Up())}");
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
