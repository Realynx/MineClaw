using System.Text.Json;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

// The 1.12.2 work stations (b1.8.1 has none): brewing stand, enchanting table, anvil, and filling glass bottles. Story Mode's bills
// run them through the story SDK (Sdk/js/75-story.js story.work); the client primitives are MinecraftClient.Stations.cs.

/// <summary>Opens a container, reads it and closes it (the bodies' memory of it is then fresh: Story Mode's storage record).</summary>
[Skill]
public sealed class PeekContainerSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "peek_container", Category = SkillCategory.Storage, Summary = "look into a container and close it", Args = "pos",
        Schema = SkillJson.Schema("pos:pos"), Estimate = TimeSpan.FromSeconds(8),
        Keywords = "peek look inside container contents check ledger record",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos() is not { } pos) return Fail("pos required");
        var open = await ctx.Game.OpenContainerAsync(pos, ct);
        if (!open.Ok) return SkillResult.From(open);
        try { return Ok(open.Message); }
        finally { await ctx.Game.CloseWindowAsync(ct); }
    }
}

/// <summary>Fills glass bottles at a water source block or a filled cauldron (the nearest when no pos).</summary>
[Skill]
public sealed class FillBottlesSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "fill_bottles", Category = SkillCategory.Items, Summary = "fill glass bottles (source block or cauldron)", Args = "pos?,count?",
        Schema = SkillJson.Schema("pos:pos?,count:int?"), Estimate = TimeSpan.FromSeconds(20),
        Keywords = "glass bottle fill bottles cauldron brewing potion base",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var at = args.Pos() ?? ctx.Find((id, meta) => id == Brewing.CauldronBlock && meta > 0 || id == Brewing.WaterSource && meta == 0, ctx.Options.SearchRadius, 1)
            .Select(f => (BlockPos?)f.Pos).FirstOrDefault();
        if (at is not { } pos) return Fail("no water source or filled cauldron nearby");
        return SkillResult.From(await ctx.Game.FillBottlesAsync(pos, Clamp(args.Int("count"), 3, 1, 64), ct));
    }
}

/// <summary>Brews a potion at a brewing stand from the furthest stage of its chain the inventory holds (water bottles at the least).</summary>
[Skill]
public sealed class BrewSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "brew", Category = SkillCategory.Items, Summary = "brew up to 3 potions at a brewing stand", Args = "potion,form?,count?,pos?",
        Schema = SkillJson.Schema("potion:str,form:str?,count:int?,pos:pos?"), Estimate = TimeSpan.FromSeconds(90),
        Keywords = "brew potion brewing stand awkward nether wart splash lingering swiftness healing strength blaze powder",
    };

    /// <summary>"potion" | "splash" | "lingering" (or the item names) → the potion item id.</summary>
    public static int Form(string? form) => (form ?? "potion").ToLowerInvariant() switch
    {
        "splash" or "splash_potion" or "438" => Brewing.SplashPotion,
        "lingering" or "lingering_potion" or "441" => Brewing.LingeringPotion,
        _ => Brewing.Potion,
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var type = (args.Str("potion") ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
        int form = Form(args.Str("form"));
        if (Brewing.Chain(form, type) is not { Count: > 0 } chain) return Fail($"no brewing chain makes '{type}' ({string.Join(", ", Brewing.Brewable.Take(8))}...)");
        var stand = args.Pos() ?? ctx.Find(Brewing.StandBlock, ctx.Options.SearchRadius, 1).Select(f => (BlockPos?)f.Pos).FirstOrDefault();
        if (stand is not { } at) return Fail("no brewing stand nearby");
        // Start from the furthest stage the inventory already holds (awkward potions skip the wart).
        var inv = ctx.World.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty && Brewing.IsPotionItem(s.Id)).ToList();
        int start = 0;
        for (int i = chain.Count - 1; i >= 0; i--)
            if (inv.Any(s => s.Id == chain[i].FromForm && (s.Tag?.Potion ?? "empty") == chain[i].FromType)) { start = i; break; }
        var steps = chain.Skip(start).ToList();
        return SkillResult.From(await ctx.Game.BrewAsync(at, steps[0].FromType, steps[0].FromForm, steps, Clamp(args.Int("count"), 3, 1, 3), ct));
    }
}

/// <summary>Enchants one plain item at an enchanting table with lapis and XP levels.</summary>
[Skill]
public sealed class EnchantSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "enchant_item", Category = SkillCategory.Items, Summary = "enchant an item at an enchanting table", Args = "item,option?,prefer?,pos?",
        Schema = SkillJson.Schema("item:str,option:int?,prefer:str?,prefer_only:bool?,pos:pos?"), Estimate = TimeSpan.FromSeconds(20),
        Keywords = "enchant enchanting table lapis lazuli xp levels fortune silk touch efficiency unbreaking sharpness protection",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (SkillJson.Item(args.Str("item")) is not { } it) return Fail($"unknown item '{args.Str("item")}'");
        int? prefer = null;
        if (args.Str("prefer") is { Length: > 0 } p)
        {
            if (Enchanting.ByName(p) is not { } e) return Fail($"unknown enchantment '{p}'");
            prefer = e.Id;
        }
        var table = args.Pos() ?? ctx.Find(Enchanting.TableBlock, ctx.Options.SearchRadius, 1).Select(f => (BlockPos?)f.Pos).FirstOrDefault();
        if (table is not { } at) return Fail("no enchanting table nearby");
        return SkillResult.From(await ctx.Game.EnchantAsync(at, it.Id, Clamp(args.Int("option"), 3, 1, 3), prefer, args.Flag("prefer_only"), ct));
    }
}

/// <summary>One anvil job: repair with the material, combine two of an item, or apply an enchanted book.</summary>
[Skill]
public sealed class AnvilSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "use_anvil", Category = SkillCategory.Items, Summary = "anvil: repair/combine an item or apply a book", Args = "item,mode?,pos?",
        Schema = SkillJson.Schema("item:str,mode:str?,pos:pos?"), Estimate = TimeSpan.FromSeconds(15),
        Keywords = "anvil repair mend combine enchanted book damaged tool xp levels",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (SkillJson.Item(args.Str("item")) is not { } it) return Fail($"unknown item '{args.Str("item")}'");
        var mode = (args.Str("mode") ?? "repair").ToLowerInvariant();
        if (mode is not ("repair" or "combine" or "book")) return Fail("mode is repair|combine|book");
        var anvil = args.Pos() ?? ctx.Find(Anvil.Block, ctx.Options.SearchRadius, 1).Select(f => (BlockPos?)f.Pos).FirstOrDefault();
        if (anvil is not { } at) return Fail("no anvil nearby");
        return SkillResult.From(await ctx.Game.AnvilAsync(at, mode, it.Id, ct));
    }
}
