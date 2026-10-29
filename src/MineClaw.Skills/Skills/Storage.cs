using System.Text.Json;
using MineClaw.Data;
using MineClaw.Skills.Inventory;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>Empties the inventory into nearby chests (category chest first). Used by hive clones before logout.</summary>
[Skill]
public sealed class DepositEverythingSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "deposit_everything", Category = SkillCategory.Storage, Summary = "put whole inventory in nearby chests", Args = "keep_tools?,chest?",
        Schema = SkillJson.Schema("keep_tools:bool?,chest:str?"), Estimate = TimeSpan.FromSeconds(30),
        Keywords = "deposit everything empty inventory dump all stash store logout before leaving unload",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        bool keepTools = args.Flag("keep_tools");
        bool Keep(ItemStack s) => s.IsEmpty || ctx.Game.ReservedItems.Contains(s.Id) || (keepTools && (Items.Get(s.Id).MaxDamage > 0 || Items.Food(s.Id) != null));
        var items = ctx.World.Inventory.PlayerSlots().Skip(9).Where(s => !Keep(s)).GroupBy(s => (s.Id, s.Damage)).ToList();
        if (items.Count == 0) return Ok("nothing to deposit");
        var chests = args.Str("chest") is { } name && ctx.Directory.Resolve(ctx, name) is { } one ? [one]
            : ctx.Find((id, _) => StorageOps.IsChest(id), ctx.Options.SearchRadius, 16).Select(f => f.Pos).Where(p => ctx.Claims.IsFree(MineClaw.World.Claims.ClaimKey.Chest(p, ctx.World.Dimension))).ToList();
        if (chests.Count == 0) return Fail("no free chest nearby");
        var moves = new List<MoveSpec>();
        foreach (var g in items)
        {
            var k = StorageCategory.Of(g.Key.Id, g.Key.Damage);
            var home = chests.FirstOrDefault(c => ctx.Directory.CategoryOf(ctx, c) == k, chests.OrderBy(c => c.DistanceSquaredTo(ctx.Me)).First());
            moves.Add(new MoveSpec(g.Key.Id, Items.Get(g.Key.Id).MaxDamage > 0 ? null : g.Key.Damage, null,
                new Endpoint(null, Region.Player, null, "inv"), new Endpoint(home, Region.Container, null, $"chest@{P(home)}")));
        }
        var run = new InventoryRun(ctx);
        var error = await new MovePlanner(ctx, run).RunAsync(moves, ct);
        // Whatever didn't fit: try the other chests.
        foreach (var c in chests)
        {
            var left = ctx.World.Inventory.PlayerSlots().Skip(9).Where(s => !Keep(s)).ToList();
            if (left.Count == 0) break;
            if (ctx.Protected(c) || !ctx.Claims.IsFree(MineClaw.World.Claims.ClaimKey.Chest(c, ctx.World.Dimension))) continue;
            await run.CloseAsync(ct);
            await ctx.Game.DepositAllAsync(c, s => !Keep(s), ct);
        }
        await run.CloseAsync(ct);
        int remaining = ctx.World.Inventory.PlayerSlots().Skip(9).Count(s => !Keep(s));
        run.Moves = moves.Count;
        return remaining == 0 ? Ok($"inventory deposited: {run.Summary()}") : Fail($"{remaining} stacks left (chests full?); {run.Summary(error)}");
    }
}

/// <summary>Restocks a tools/food/blocks loadout from nearby chests (surveyed once).</summary>
[Skill]
public sealed class RestockLoadoutSkill : Skill
{
    private static readonly (string Name, int[] Ids, int Want)[] Default =
    [
        ("pickaxe", [Items.DiamondPickaxe, Items.IronPickaxe, Items.StonePickaxe, Items.WoodenPickaxe], 1),
        ("axe", [Items.DiamondAxe, Items.IronAxe, Items.StoneAxe, Items.WoodenAxe], 1),
        ("sword", [Items.DiamondSword, Items.IronSword, Items.StoneSword, Items.WoodenSword], 1),
        ("food", [Items.CookedPorkchop, Items.Steak, Items.Bread, Items.CookedChicken, Items.CookedFish], 16),
        ("torches", [Blocks.Torch], 16),
        ("blocks", [Blocks.Cobblestone, Blocks.Dirt], 64),
    ];

    public override SkillSpec Spec { get; } = new()
    {
        Name = "restock_loadout", Category = SkillCategory.Storage, Summary = "refill tools/food/torches/blocks from chests", Args = "items?",
        Schema = SkillJson.Schema("items:str?"), Estimate = TimeSpan.FromSeconds(40),
        Keywords = "restock refill loadout gear up supplies tools food torches resupply kit",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var needs = Default.Where(n => Have(ctx, n) < n.Want).ToList();
        if (args.Str("items") is { } only) needs = needs.Where(n => only.Contains(n.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (needs.Count == 0) return Ok("loadout already full");
        var run = new InventoryRun(ctx);
        var got = new List<string>();
        foreach (var c in ctx.Find((id, _) => StorageOps.IsChest(id), ctx.Options.SearchRadius, 16).Select(f => f.Pos))
        {
            if (needs.Count == 0) break;
            if (await run.OpenAsync(c, ct) is { } err) { run.Notes.Add(err); continue; }
            var stock = run.ContainerContents();
            foreach (var n in needs.ToList())
            {
                int have = Have(ctx, n);
                // Food: any safe food the version's data knows (1.12.2 baked potatoes, carrots, mutton...), the cooked
                // staples first.
                var ids = n.Name == "food"
                    ? n.Ids.Concat(stock.Where(s => !s.IsEmpty && IsSafeFood(s)).Select(s => (int)s.Id)).Distinct().ToArray()
                    : n.Ids;
                foreach (var id in ids)
                {
                    if (have >= n.Want) break;
                    int there = stock.Where(s => !s.IsEmpty && s.Id == id).Sum(s => s.Count);
                    if (there == 0) continue;
                    int k = await run.TransferAsync(id, null, Math.Min(there, n.Want - have), w => w.SlotsOf(Region.Container).ToArray(),
                        w => w.SlotsOf(Region.Player).ToArray(), ct);
                    have += k;
                    if (k > 0) { got.Add($"{k} {Item(id)}"); run.Moves++; }
                }
                if (have >= n.Want) needs.Remove(n);
            }
        }
        await run.CloseAsync(ct);
        string missing = needs.Count > 0 ? "; still short: " + string.Join(", ", needs.Select(n => n.Name)) : "";
        return got.Count > 0 ? Ok($"took {string.Join(", ", got)}{missing}; {run.Summary()}") : Fail($"nothing useful in nearby chests{missing}");
    }

    private static bool IsSafeFood(ItemStack s) =>
        s.Id != Items.GoldenApple && Items.Food(s.Id, s.Damage) is not null && !MineClaw.Client.Survival.HungerRules.IsRisky(s.Id, s.Damage);

    private static int Have(SkillContext ctx, (string Name, int[] Ids, int Want) n) =>
        n.Name == "food"
            ? ctx.World.Inventory.PlayerSlots().Where(s => !s.IsEmpty && !ctx.Game.ReservedItems.Contains(s.Id) && IsSafeFood(s)).Sum(s => s.Count)
            : n.Ids.Sum(id => ctx.Count(id));
}

[Skill]
public sealed class LabelChestSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "label_chest", Category = SkillCategory.Storage, Summary = "place a sign on a chest and write a label", Args = "chest,text",
        Schema = SkillJson.Schema("chest:str,text:str"), Estimate = TimeSpan.FromSeconds(10),
        Pre = new() { Items = [[Items.Sign]], Needs = "sign", Primitives = Primitive.WriteSign },
        Keywords = "label sign chest name tag write mark storage",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Str("chest") is not { } name || ctx.Directory.Resolve(ctx, name) is not { } chest) return Fail("chest required");
        var text = (args.Str("text") ?? "").Trim();
        if (text.Length == 0) return Fail("text required");
        // b1.8.1 signs can't go on the chest itself (clicking it opens it): hang a wall sign in a free cell above/beside
        // the chest on another solid block, else stand a sign post on solid ground beside it.
        if (SignSpot(ctx, chest) is not { } spot) return Fail("no free spot next to the chest with a wall or floor for a sign");
        var (cell, face) = spot;
        var p = await ctx.Game.PlaceAsync(cell, Items.Sign, face, ct);
        if (!p.Ok) return SkillResult.From(p);
        var lines = Wrap(text);
        var w = await ctx.Game.WriteSignAsync(cell, lines, ct);
        if (!w.Ok) return SkillResult.From(w);
        if (StorageCategory.Parse(text) is { } cat) ctx.Directory.Assign(chest, cat);
        return Ok($"labelled chest {P(chest)} \"{string.Join(" ", lines).Trim()}\"");
    }

    /// <summary>A free cell above/beside <paramref name="chest"/> and the 0x0F face that hangs (or stands) a sign there.</summary>
    internal static (BlockPos Cell, BlockFace Face)? SignSpot(SkillContext ctx, BlockPos chest)
    {
        bool IsChest(BlockPos b) => b == chest || StorageOps.IsChest(ctx.Block(b));
        foreach (var c in new[] { chest.Up() }.Concat(SkillWorld.Horizontal.Select(o => chest + o)))
        {
            if (!SkillWorld.Air(ctx.World, c) || ctx.Protected(c)) continue;
            if (ctx.Game.WallFace(c, IsChest) is { } wall) return (c, wall);
            var below = c.Down();
            if (!IsChest(below) && MineClaw.Client.FaceAim.IsWallSupport(ctx.World, below)) return (c, BlockFace.Top);
        }
        return null;
    }

    /// <summary>Four lines of ≤ 15 chars (b1.8.1 sign limit).</summary>
    public static string[] Wrap(string text)
    {
        var lines = new List<string> { "" };
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.Length > 15 ? word[..15] : word;
            if (lines[^1].Length + w.Length + (lines[^1].Length > 0 ? 1 : 0) > 15) { if (lines.Count == 4) break; lines.Add(""); }
            lines[^1] += (lines[^1].Length > 0 ? " " : "") + w;
        }
        while (lines.Count < 4) lines.Add("");
        return lines.ToArray();
    }
}

/// <summary>Container facts shared by the storage skills.</summary>
public static class StorageOps
{
    /// <summary>Trapped chest (1.12.2): a chest that also powers redstone while open.</summary>
    public const int TrappedChest = 146;

    /// <summary>A chest or a trapped chest (absent from b1.8.1 worlds).</summary>
    public static bool IsChest(int id) => id is Blocks.Chest or TrappedChest;
}
