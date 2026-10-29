using System.Text.Json;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>Furnace batches on the client's smelt/collect macros: picks fuel (charcoal/coal, then planks/logs not being
/// smelted), splits big batches across nearby furnaces, collects.</summary>
[Skill]
public sealed class SmeltBatchSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "smelt_batch", Category = SkillCategory.Smelting, Summary = "smelt N items (cobble->stone, log->charcoal...)", Args = "item,count?,wait?",
        Schema = SkillJson.Schema("item:str,count:int?,wait:bool?"), Estimate = TimeSpan.FromSeconds(90),
        Keywords = "smelt furnace cook stone cobblestone charcoal logs glass sand iron ore bake batch",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var name = args.Str("item") ?? "cobblestone";
        if (name.Contains("charcoal", StringComparison.OrdinalIgnoreCase)) name = "log";
        if (name.Equals("stone", StringComparison.OrdinalIgnoreCase)) name = "cobblestone";
        if (SkillJson.Item(name) is not { } it || !Smelting.IsSmeltable(it.Id)) return Fail($"'{name}' doesn't smelt");
        int have = ctx.Count(it.Id);
        if (have == 0) return Fail($"no {Item(it.Id)} to smelt");
        int n = Clamp(args.Int("count"), have, 1, have);
        int? fuel = Fuel(ctx, it.Id);
        if (fuel is null) return Fail("no fuel (coal, charcoal, planks, logs)");
        var furnaces = ctx.Find((id, _) => id is Blocks.Furnace or Blocks.LitFurnace, ctx.Options.SearchRadius, 4);
        if (furnaces.Count == 0) return Fail("no furnace nearby (craft one: 8 cobblestone)");
        bool wait = args.Flag("wait");
        // ≤ 64 per furnace; spread larger batches.
        int left = n, started = 0;
        var notes = new List<string>();
        foreach (var f in furnaces)
        {
            if (left <= 0) break;
            int part = Math.Min(64, left);
            var r = await ctx.Game.SmeltAsync(it.Id, part, fuel, wait && furnaces.Count == 1, f.Pos, ct);
            if (!r.Ok) { notes.Add(r.Message); continue; }
            left -= part;
            started += part;
            fuel = Fuel(ctx, it.Id) ?? fuel;
        }
        if (started == 0) return Fail($"couldn't start smelting: {string.Join("; ", notes)}");
        var output = Smelting.ResultFor(it.Id);
        return Ok($"smelting {started} {Item(it.Id)} in {Math.Min(furnaces.Count, (started + 63) / 64)} furnace(s)" +
                  (output is null ? "" : $" -> {Item(output.OutputId, output.OutputDamage)}") + (wait ? "" : "; collect_furnace later"));
    }

    /// <summary>Best fuel that isn't the thing being smelted: charcoal/coal first, then the rest by burn time from the version's
    /// data (<see cref="Smelting.FuelTicks"/>: planks/logs incl. 1.12.2's log2, sticks); a coal block (1.12.2) only when there is
    /// no coal (it burns 80 items' worth).</summary>
    public static int? Fuel(SkillContext ctx, int smelting)
    {
        if (ctx.Has(Items.Coal)) return Items.Coal;
        int? best = null;
        foreach (int id in new[] { Blocks.Planks, Blocks.Log, MineClaw.Client.Trees.Wood.Log2, Items.Stick, CoalBlock })
            if (id != smelting && Smelting.IsFuel(id) && ctx.Has(id) && !ctx.Game.ReservedItems.Contains(id)
                && (best is null || id != CoalBlock && Smelting.FuelTicks(id) > Smelting.FuelTicks(best.Value)))
                best = id;
        return best;
    }

    /// <summary>Block of coal (1.12.2).</summary>
    private const int CoalBlock = 173;
}

[Skill]
public sealed class CollectFurnaceSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "collect_furnace", Category = SkillCategory.Smelting, Summary = "take finished output from nearby furnaces", Args = "pos?",
        Schema = SkillJson.Schema("pos:pos?"), Estimate = TimeSpan.FromSeconds(15),
        Keywords = "collect furnace output take smelted results empty",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var list = args.Pos() is { } p ? [p] : ctx.Find((id, _) => id is Blocks.Furnace or Blocks.LitFurnace, ctx.Options.SearchRadius, 4).Select(f => f.Pos).ToList();
        if (list.Count == 0) return Fail("no furnace nearby");
        var got = new List<string>();
        foreach (var f in list)
        {
            var r = await ctx.Game.CollectFurnaceAsync(f, ct);
            if (r.Ok) got.Add(r.Message);
        }
        return got.Count > 0 ? Ok(string.Join("; ", got)) : Fail("nothing ready in the furnaces");
    }
}

[Skill]
public sealed class RefuelFurnaceSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "refuel_furnace", Category = SkillCategory.Smelting, Summary = "top up furnace fuel slot", Args = "pos?,fuel?,count?",
        Schema = SkillJson.Schema("pos:pos?,fuel:str?,count:int?"), Estimate = TimeSpan.FromSeconds(10),
        Keywords = "fuel furnace coal charcoal refuel top up burn",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var f = args.Pos() ?? ctx.Find((id, _) => id is Blocks.Furnace or Blocks.LitFurnace, ctx.Options.SearchRadius, 1).Select(x => x.Pos).FirstOrDefault();
        if (ctx.Block(f) is not (Blocks.Furnace or Blocks.LitFurnace)) return Fail("no furnace nearby");
        int fuel = SkillJson.Item(args.Str("fuel")) is { } it ? it.Id : SmeltBatchSkill.Fuel(ctx, -1) ?? -1;
        if (fuel < 0 || !ctx.Has(fuel)) return Fail("no fuel");
        int count = Clamp(args.Int("count"), 8, 1, 64);
        var open = await ctx.Game.OpenContainerAsync(f, ct);
        if (!open.Ok) return SkillResult.From(open);
        try
        {
            // Furnace window: 0 input, 1 fuel, 2 output, 3.. player inventory. Pick up the fuel stack, right-click into slot 1.
            var win = ctx.World.Inventory.OpenWindow;
            if (win is null) return Fail("furnace window didn't open");
            int src = -1;
            for (int i = 3; i < win.SlotCount; i++) if (win[i] is { IsEmpty: false } s && s.Id == fuel) { src = i; break; }
            if (src < 0) return Fail("fuel not in inventory view");
            await ctx.Game.ClickSlotAsync(src, false, false, ct);
            int moved = 0;
            for (; moved < count && !ctx.World.Inventory.Cursor.IsEmpty; moved++) await ctx.Game.ClickSlotAsync(1, true, false, ct);
            if (!ctx.World.Inventory.Cursor.IsEmpty) await ctx.Game.ClickSlotAsync(src, false, false, ct);
            return Ok($"added {moved} {Item(fuel)} to furnace at {P(f)}");
        }
        finally { await ctx.Game.CloseWindowAsync(ct); }
    }
}
