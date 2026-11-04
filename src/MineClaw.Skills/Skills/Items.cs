using System.Text.Json;
using MineClaw.Data;
using MineClaw.Skills.Inventory;
using MineClaw.World;

namespace MineClaw.Skills;

internal static class ItemOps
{
    /// <summary>The damage to match: none for tools and armor (any wear) or an unspecified variant.</summary>
    public static int? MatchDamage(int id, int damage) => damage < 0 || Items.Get(id).MaxDamage > 0 ? null : damage;

    /// <summary>Drops exactly <paramref name="count"/> of an item where I'm looking (window clicks: pick up, put back
    /// the rest with singles or the whole remainder, click outside). Returns how many were dropped.</summary>
    public static async Task<int> DropAsync(SkillContext ctx, int id, int? damage, int count, CancellationToken ct)
    {
        var run = new InventoryRun(ctx);
        await run.CloseAsync(ct);
        int dropped = 0;
        for (int guard = 0; guard < 40 && dropped < count; guard++)
        {
            var m = run.Snapshot();
            var src = m.SlotsOf(Region.Player).Where(s => WindowModel.Same(m[s], id, damage)).OrderBy(s => m[s].Count).Cast<int?>().FirstOrDefault();
            if (src is not { } s) break;
            int n = m[s].Count, want = count - dropped;
            const int outside = InventoryManager.OutsideSlot;
            var plan = new List<Click> { new(s) };
            if (n <= want) plan.Add(new Click(outside));
            else if (want <= n - want)
            {
                plan.AddRange(Enumerable.Repeat(new Click(outside, Right: true), want)); // drop singles, put the rest back
                plan.Add(new Click(s));
            }
            else
            {
                plan.AddRange(Enumerable.Repeat(new Click(s, Right: true), n - want)); // put singles back, drop the rest
                plan.Add(new Click(outside));
            }
            int before = ctx.Count(id, damage);
            if (!await run.ExecAsync(plan, ct)) await run.ClearCursorAsync(ct);
            dropped += Math.Max(0, before - ctx.Count(id, damage));
        }
        await run.ClearCursorAsync(ct);
        return dropped;
    }

    /// <summary>
    /// Drops like <see cref="DropAsync"/>, then asks the server for the real inventory: the count returned is how many
    /// actually left (server-confirmed when the client supports a resync). A stale local stack can no longer make a toss
    /// look done (or hide one), which once left 12 "ghost" slabs in the model after they had been handed over.
    /// </summary>
    public static async Task<(int Dropped, bool Confirmed)> DropVerifiedAsync(SkillContext ctx, int id, int? damage, int count, CancellationToken ct)
    {
        int before = ctx.Count(id, damage);
        int n = await DropAsync(ctx, id, damage, count, ct);
        var sync = await ctx.Game.ResyncInventoryAsync(ct);
        if (!sync.Ok) return (n, false);
        return (Math.Max(0, before - ctx.Count(id, damage)), true);
    }
}

[Skill]
public sealed class GiveItemsSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "give_items", Category = SkillCategory.Items, Summary = "walk to a player and toss them N items", Args = "player,item,count?",
        Schema = SkillJson.Schema("player:str,item:str,count:int?"), Estimate = TimeSpan.FromSeconds(20),
        Keywords = "give hand pass toss share items player gift send deliver",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var name = args.Str("player") ?? "";
        if (ctx.World.Entities.FindPlayer(name) is not { } pl) return Fail($"can't see {name}");
        if (SkillJson.Item(args.Str("item")) is not { } it) return Fail($"unknown item '{args.Str("item")}'");
        if (ctx.Game.ReservedItems.Contains(it.Id)) return Fail($"{Item(it.Id)} is reserved");
        int? dmg = ItemOps.MatchDamage(it.Id, it.Damage);
        int have = ctx.Count(it.Id, dmg);
        if (have == 0) return Fail($"no {Item(it.Id, Math.Max(0, it.Damage))}");
        int count = Clamp(args.Int("count"), 1, 1, have);
        var g = await ctx.Game.GotoAsync(new GoalNearPosition(pl.Position, 2.5), null, ct);
        if (!g.Ok) return Fail($"couldn't reach {name}: {g.Message}");
        var now = ctx.World.Entities.FindPlayer(name)?.Position ?? pl.Position;
        await ctx.Game.LookAtAsync(now + new Vec3d(0, 1.2, 0), ct);
        var (n, confirmed) = await ItemOps.DropVerifiedAsync(ctx, it.Id, dmg, count, ct);
        string sure = confirmed ? " (server-confirmed)" : "";
        return n >= count ? Ok($"tossed {n} {Item(it.Id, Math.Max(0, it.Damage))} to {name}{sure}") : Fail($"only dropped {n}/{count}{sure}");
    }
}

[Skill]
public sealed class DropItemsSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "drop_items", Category = SkillCategory.Items, Summary = "drop N of an item here", Args = "item,count?",
        Schema = SkillJson.Schema("item:str,count:int?"), Estimate = TimeSpan.FromSeconds(5),
        Keywords = "drop throw discard toss trash junk",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (SkillJson.Item(args.Str("item")) is not { } it) return Fail($"unknown item '{args.Str("item")}'");
        if (ctx.Game.ReservedItems.Contains(it.Id)) return Fail($"{Item(it.Id)} is reserved");
        int? dmg = ItemOps.MatchDamage(it.Id, it.Damage);
        int have = ctx.Count(it.Id, dmg);
        if (have == 0) return Fail($"no {Item(it.Id, Math.Max(0, it.Damage))}");
        var (n, _) = await ItemOps.DropVerifiedAsync(ctx, it.Id, dmg, Clamp(args.Int("count"), have, 1, have), ct);
        return n > 0 ? Ok($"dropped {n} {Item(it.Id, Math.Max(0, it.Damage))}") : Fail("nothing dropped");
    }
}

[Skill]
public sealed class EquipArmorSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "equip_armor", Category = SkillCategory.Items, Summary = "wear the best armor in inventory", Args = "",
        Estimate = TimeSpan.FromSeconds(5), Keywords = "armor armour wear equip helmet chestplate leggings boots dress",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var run = new InventoryRun(ctx);
        await run.CloseAsync(ct);
        var worn = new List<string>();
        // Player window armor slots: 5 helmet, 6 chest, 7 legs, 8 boots. Armor ids cycle helmet..boots per material.
        for (int piece = 0; piece < 4; piece++)
        {
            int slot = 5 + piece;
            var m = run.Snapshot();
            var best = m.SlotsOf(Region.Player).Where(s => !m[s].IsEmpty && m[s].Id is >= Items.LeatherHelmet and <= Items.GoldenBoots
                                                           && (m[s].Id - Items.LeatherHelmet) % 4 == piece)
                .OrderByDescending(s => Tier(m[s].Id)).Cast<int?>().FirstOrDefault();
            if (best is not { } b) continue;
            if (!m[slot].IsEmpty && Tier(m[slot].Id) >= Tier(m[b].Id)) continue;
            // b1.8.1 shift-click in the inventory only moves main<->hotbar: armor goes on by pick-up + place.
            var plan = m[slot].IsEmpty ? new List<Click> { new(b), new(slot) } : [new(b), new(slot), new(b)];
            if (await run.ExecAsync(plan, ct) && !ctx.World.Inventory.PlayerWindow.Get(slot).IsEmpty)
                worn.Add(Item(ctx.World.Inventory.PlayerWindow.Get(slot).Id));
        }
        await run.CloseAsync(ct);
        return worn.Count > 0 ? Ok("wearing " + string.Join(", ", worn)) : Ok("no better armor to put on");
    }

    // Leather < gold < chain < iron < diamond (b1.8.1 armor values).
    private static int Tier(int id) => ((id - Items.LeatherHelmet) / 4) switch { 0 => 0, 4 => 1, 1 => 2, 2 => 3, 3 => 4, _ => 0 };
}

[Skill]
public sealed class HoldItemSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "hold_item", Category = SkillCategory.Items, Summary = "put an item in my hand (current hotbar slot)", Args = "item",
        Schema = SkillJson.Schema("item:str"), Estimate = TimeSpan.FromSeconds(3),
        Keywords = "hold wield hand select equip swap held item tool weapon",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (SkillJson.Item(args.Str("item")) is not { } it) return Fail($"unknown item '{args.Str("item")}'");
        int? dmg = ItemOps.MatchDamage(it.Id, it.Damage);
        var held = ctx.World.Inventory.HeldItem(ctx.World.Player.HeldSlot);
        if (WindowModel.Same(held, it.Id, dmg)) return Ok($"already holding {Item(it.Id, Math.Max(0, it.Damage))}");
        var run = new InventoryRun(ctx);
        await run.CloseAsync(ct);
        var m = run.Snapshot();
        int hand = 36 + ctx.World.Player.HeldSlot;
        var src = m.SlotsOf(Region.Player).Where(s => s != hand && WindowModel.Same(m[s], it.Id, dmg)).Cast<int?>().FirstOrDefault();
        if (src is not { } s) return Fail($"no {Item(it.Id, Math.Max(0, it.Damage))}");
        // Swap through the cursor: pick up, click the hand slot (swap), put the old hand item where the new one was.
        var plan = new List<Click> { new(s), new(hand) };
        if (!m[hand].IsEmpty) plan.Add(new Click(s));
        if (!await run.ExecAsync(plan, ct)) await run.ClearCursorAsync(ct);
        await run.ClearCursorAsync(ct);
        held = ctx.World.Inventory.HeldItem(ctx.World.Player.HeldSlot);
        return WindowModel.Same(held, it.Id, dmg) ? Ok($"holding {Item(it.Id, Math.Max(0, it.Damage))}") : Fail("swap didn't take");
    }
}

[Skill]
public sealed class EatFoodSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "eat_food", Category = SkillCategory.Survival, Summary = "eat the best food (auto picks, not reserved)", Args = "",
        Estimate = TimeSpan.FromSeconds(3), Keywords = "eat food hungry hunger meal snack",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        SkillResult.From(await ctx.Game.EatAsync(ct));
}
