using System.Text.Json;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// The hidden <c>equip {item, mode, from?, ench?}</c> (no tier sees its definition, so no prompt bytes; scripts call it, Story Mode's
/// pawn script above all): hold an item (the freshest matching stack, the enchanted one when asked), wear or take off armour, take one
/// exact stack out of a chest. <c>ench</c>: an enchantment name ("fortune", "silk_touch"), "any", or "none" (a plain one).
/// </summary>
public static class EquipTool
{
    public const string ToolName = "equip";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Hold, wear, take off, take out of or store into a chest an item (mode hold|wear|unwear|take|store; ench: fortune|silk_touch|any|none).",
            ToolSchema.Create().Str("item").Str("mode", "hold|wear|unwear|take", required: false).Str("from", "chest x,y,z (take)", required: false)
                .Str("ench", "fortune|silk_touch|any|none", required: false).Int("keep", "store: how many to keep (the freshest)", required: false).Build(),
            (i, ct) => ExecuteAsync(c, i, ct), longRunning: true, tiers: ToolTiers.None);
    }

    private static async Task<ToolResult> ExecuteAsync(IGameClient c, JsonElement i, CancellationToken ct)
    {
        var item = i.ReqStr("item").Trim();
        var mode = (i.Str("mode") ?? "hold").Trim().ToLowerInvariant();
        var ench = i.Str("ench")?.Trim();
        if (mode == "unwear") return await UnwearAsync(c, item, ct).ConfigureAwait(false);
        if (Items.ByNameWithDamage(item) is not { } it) return ToolResult.Error($"unknown item '{item}'");
        var want = new Wanted(it.Item.Id, it.Damage, it.Item.MaxDamage, ench);
        string label = Items.Get(it.Item.Id).DisplayName.ToLowerInvariant() + (string.IsNullOrEmpty(ench) || ench == "none" ? "" : $" ({ench})");
        return mode switch
        {
            "take" => await TakeAsync(c, i, want, label, ct).ConfigureAwait(false),
            "store" => await StoreAsync(c, i, want, ct).ConfigureAwait(false),
            "wear" => GameToolSet.R(await c.WearAsync(Best(c, want.Matches) is { } w ? s => s.Equals(w) : want.Matches, label, ct).ConfigureAwait(false)),
            "hold" => await HoldAsync(c, want, label, ct).ConfigureAwait(false),
            _ => ToolResult.Error("mode must be hold|wear|unwear|take|store"),
        };
    }

    /// <summary>The stacks an equip call means: that item (any variant for -1 or a tool), with the enchantment wish.</summary>
    private sealed record Wanted(int Id, int Damage, int MaxDamage, string? Ench)
    {
        private bool Kind(ItemStack s) => !s.IsEmpty && s.Id == Id && (Damage < 0 || MaxDamage > 0 || s.Damage == Damage);
        public bool Matches(ItemStack s) => Kind(s) && Fits(s, Ench);
    }

    /// <summary>Takes off all armour ("all", "armor", nothing named) or the named piece.</summary>
    private static async Task<ToolResult> UnwearAsync(IGameClient c, string item, CancellationToken ct)
    {
        if (item is "all" or "" or "armor" or "armour") return GameToolSet.R(await c.UnwearAsync(null, ct).ConfigureAwait(false));
        if (Items.ByNameWithDamage(item) is not { } uw) return ToolResult.Error($"unknown item '{item}'");
        return GameToolSet.R(await c.UnwearAsync(s => s.Id == uw.Item.Id, ct).ConfigureAwait(false));
    }

    private static async Task<ToolResult> TakeAsync(IGameClient c, JsonElement i, Wanted want, string label, CancellationToken ct)
    {
        if (i.Str("from") is not { } f || Locations.ParseCoords(f) is not { } at) return ToolResult.Error("take needs from: the chest's x,y,z");
        return GameToolSet.R(await c.WithdrawMatchingAsync(at, want.Matches, 1, label, ct).ConfigureAwait(false));
    }

    /// <summary>Into the chest at from, all but the `keep` freshest matching stacks (a pawn's extra tools go back to the colony).</summary>
    private static async Task<ToolResult> StoreAsync(IGameClient c, JsonElement i, Wanted want, CancellationToken ct)
    {
        if (i.Str("from") is not { } sf || Locations.ParseCoords(sf) is not { } chest) return ToolResult.Error("store needs from: the chest's x,y,z");
        var kept = c.World.Inventory.PlayerSlots().Skip(9).Where(want.Matches).OrderBy(st => st.Damage).Take(Math.Max(0, i.Int("keep") ?? 0)).ToList();
        bool Store(ItemStack st)
        {
            if (!want.Matches(st)) return false;
            int k = kept.IndexOf(st);
            if (k < 0) return true;
            kept.RemoveAt(k);
            return false;
        }
        var r = await c.DepositAllAsync(chest, Store, ct).ConfigureAwait(false);
        return !r.Ok && r.Message.StartsWith("nothing to deposit", StringComparison.Ordinal) ? ToolResult.Success("nothing to store") : GameToolSet.R(r);
    }

    /// <summary>The freshest matching stack (a tool about to break stays in the bag while a better one is there).</summary>
    private static async Task<ToolResult> HoldAsync(IGameClient c, Wanted want, string label, CancellationToken ct)
    {
        if (Best(c, want.Matches) is not { } pick) return ToolResult.Error($"no {label} carried");
        return GameToolSet.R(await c.HoldAsync(s => s.Equals(pick), label, ct).ConfigureAwait(false));
    }

    /// <summary>Enchantment wish: null/"" anything, "none" a plain stack, else <see cref="Enchantments.Satisfies"/>.</summary>
    internal static bool Fits(ItemStack s, string? ench) =>
        string.IsNullOrEmpty(ench) ? true : ench.Equals("none", StringComparison.OrdinalIgnoreCase) ? s.Tag is not { Enchantments.Count: > 0 } : Enchantments.Satisfies(s, ench);

    /// <summary>Of the carried stacks (bag and hotbar) that match, the one with the most durability left.</summary>
    private static ItemStack? Best(IGameClient c, Func<ItemStack, bool> match)
    {
        var slots = c.World.Inventory.PlayerSlots();
        ItemStack? best = null;
        for (int i = 9; i < slots.Length; i++)
        {
            var s = slots[i];
            if (!match(s)) continue;
            if (best is not { } b || s.Damage < b.Damage) best = s;
        }
        return best;
    }
}
