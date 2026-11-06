using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Gear primitives (Story Mode's pawns, scripts' <c>equip</c>): hold a chosen stack, wear or take off armour, take one exact stack
/// (an enchanted tool) out of a chest, and two per-body dig settings: <see cref="StrictHarvest"/> (an asked-for dig that would lose the
/// drop is refused) and <see cref="PreferHeldTool"/> (a dig keeps the tool the body holds when it harvests the block).
/// </summary>
public sealed partial class MinecraftClient
{
    /// <summary>
    /// An explicit dig (<see cref="DigBlockAsync"/>) that the best tool carried can't harvest (iron ore with a wooden pickaxe, stone by
    /// hand) is refused instead of breaking the block for nothing: "needs a stone pickaxe". Off by default; Story Mode's pawns turn it on.
    /// </summary>
    public bool StrictHarvest { get; set; }

    /// <summary>
    /// A dig keeps the held tool when it harvests the block and beats the bare hand (the colony director chose it: a plain pickaxe for
    /// stone, the Fortune one for diamonds), rather than swapping to the fastest tool carried. Off by default.
    /// </summary>
    public bool PreferHeldTool { get; set; }

    /// <summary>
    /// A walk whose plan is a detour (<see cref="RouteStats.IsDetour"/>: three times the straight line, or a dip far below both ends) is
    /// planned once more digging through natural ground (<see cref="NaturalGround"/>; normal world only), and the dug route is taken
    /// when it is clearly cheaper. Live 11-04 story-1: "it makes the pawn go way out of the way underground". Off by default; Story
    /// Mode's pawns turn it on.
    /// </summary>
    public bool DigThroughDetours { get; set; }

    /// <summary>
    /// A deposit that finds every chest full never puts a new chest down by itself (the overflow chest beside a full one). Live 11-05/06
    /// story-1: pawns' clients placed chests the colony never knew of (223,65,404 on top of a storage chest, 218,64,395, 239,64,397…);
    /// Fox 11-05: "Bots should delete whatever they put down unless it was a directive to build it." Story Mode's pawns turn it on: the
    /// colony says storage is full and builds its own chests. Off by default.
    /// </summary>
    public bool NoOverflowChests { get; set; }

    /// <summary>This body's recent failed searches (<see cref="NoRouteCache"/>): the same one isn't run again for a while.</summary>
    public NoRouteCache NoRoutes { get; } = new();

    /// <summary>Armour slot (5 head … 8 feet) for an item, or -1.</summary>
    public static int ArmorSlotOf(int itemId)
    {
        if (itemId <= 0 || !Items.Exists(itemId)) return -1;
        var n = Items.Get(itemId).Name;
        return n.EndsWith("_helmet", StringComparison.Ordinal) || n.EndsWith("_cap", StringComparison.Ordinal) ? 5
            : n.EndsWith("_chestplate", StringComparison.Ordinal) || n.EndsWith("_tunic", StringComparison.Ordinal) ? 6
            : n.EndsWith("_leggings", StringComparison.Ordinal) || n.EndsWith("_pants", StringComparison.Ordinal) ? 7
            : n.EndsWith("_boots", StringComparison.Ordinal) ? 8 : -1;
    }

    /// <summary>Puts the first carried stack matching <paramref name="match"/> in the hand (into the hotbar first when needed).</summary>
    public Task<ActionResult> HoldAsync(Func<ItemStack, bool> match, string label, CancellationToken ct = default) =>
        RunActionAsync("equip", ct, c => HoldCoreAsync(match, label, c));

    private async Task<ActionResult> HoldCoreAsync(Func<ItemStack, bool> match, string label, CancellationToken c)
    {
        var inv = World.Inventory;
        if (!HeldStack().IsEmpty && match(HeldStack())) return ActionResult.Success($"holding {label}");
        int inHotbar = FirstCarried(36, 44, match);
        if (inHotbar >= 0) { SelectHotbar(inHotbar - 36); return ActionResult.Success($"holding {label}"); }
        int from = FirstCarried(9, 35, match);
        if (from < 0) return ActionResult.Fail($"no {label} carried");
        int hb = ChooseHotbarTarget();
        await SwapIntoHotbarAsync(from, hb, c).ConfigureAwait(false);
        if (inv.GetPlayerSlot(36 + hb) is not { IsEmpty: false } now || !match(now)) return ActionResult.Fail($"couldn't hold {label}");
        SelectHotbar(hb);
        return ActionResult.Success($"holding {label}");
    }

    /// <summary>The first window-0 slot in <paramref name="from"/>..<paramref name="to"/> (inclusive) holding a stack that matches, or -1.</summary>
    private int FirstCarried(int from, int to, Func<ItemStack, bool> match)
    {
        for (int slot = from; slot <= to; slot++)
            if (World.Inventory.GetPlayerSlot(slot) is { IsEmpty: false } s && match(s)) return slot;
        return -1;
    }

    /// <summary>Wears the first carried armour piece matching <paramref name="match"/> (what was in that slot goes into the bag).</summary>
    public Task<ActionResult> WearAsync(Func<ItemStack, bool> match, string label, CancellationToken ct = default) =>
        RunActionAsync("equip", ct, c => WearCoreAsync(match, label, c));

    private async Task<ActionResult> WearCoreAsync(Func<ItemStack, bool> match, string label, CancellationToken c)
    {
        await EnsureNoWindowAsync(c).ConfigureAwait(false);
        var inv = World.Inventory;
        int from = FirstCarried(9, 44, s => match(s) && ArmorSlotOf(s.Id) >= 5);
        if (from < 0)
            return FirstCarried(5, 8, match) >= 0 ? ActionResult.Success($"already wearing {label}") : ActionResult.Fail($"no {label} carried");
        var piece = inv.GetPlayerSlot(from);
        int armor = ArmorSlotOf(piece.Id);
        await ClickCoreAsync(from, false, false, c).ConfigureAwait(false);   // pick it up
        await ClickCoreAsync(armor, false, false, c).ConfigureAwait(false);  // put it on (the old piece comes onto the cursor)
        if (!inv.Cursor.IsEmpty) await ClickCoreAsync(from, false, false, c).ConfigureAwait(false); // the old piece where the new one was
        if (!inv.Cursor.IsEmpty) await StashCursorAsync(c).ConfigureAwait(false);
        var on = inv.GetPlayerSlot(armor);
        return !on.IsEmpty && on.Id == piece.Id ? ActionResult.Success($"wearing {label}") : ActionResult.Fail($"couldn't put on {label}");
    }

    /// <summary>Takes off the worn armour (all of it, or the pieces matching <paramref name="match"/>) into free bag slots.</summary>
    public Task<ActionResult> UnwearAsync(Func<ItemStack, bool>? match = null, CancellationToken ct = default) =>
        RunActionAsync("equip", ct, c => UnwearCoreAsync(match, c));

    private async Task<ActionResult> UnwearCoreAsync(Func<ItemStack, bool>? match, CancellationToken c)
    {
        await EnsureNoWindowAsync(c).ConfigureAwait(false);
        var inv = World.Inventory;
        int n = 0;
        for (int armor = 5; armor <= 8; armor++)
        {
            var s = inv.GetPlayerSlot(armor);
            if (s.IsEmpty || match != null && !match(s)) continue;
            int free = FirstEmptyBagSlot();
            if (free < 0) return n > 0 ? ActionResult.Success($"took off {n} piece(s); no room for the rest", n) : ActionResult.Fail("no free slot to take armour off into");
            await ClickCoreAsync(armor, false, false, c).ConfigureAwait(false);
            await ClickCoreAsync(free, false, false, c).ConfigureAwait(false);
            if (!inv.Cursor.IsEmpty) await StashCursorAsync(c).ConfigureAwait(false);
            if (inv.GetPlayerSlot(armor).IsEmpty) n++;
        }
        return ActionResult.Success(n == 0 ? "wearing nothing to take off" : $"took off {n} piece(s)", n);
    }

    private int FirstEmptyBagSlot()
    {
        for (int slot = 9; slot <= 44; slot++) if (World.Inventory.GetPlayerSlot(slot).IsEmpty) return slot;
        return -1;
    }

    /// <summary>Takes up to <paramref name="count"/> of the stacks matching <paramref name="match"/> (one exact tool: an enchanted
    /// pickaxe) out of the chest at <paramref name="container"/>.</summary>
    public Task<ActionResult> WithdrawMatchingAsync(BlockPos container, Func<ItemStack, bool> match, int count, string label, CancellationToken ct = default) =>
        RunActionAsync("withdraw", ct, async c =>
        {
            var o = await OpenCoreAsync(container, c).ConfigureAwait(false);
            if (!o.Ok) return o;
            var w = World.Inventory.OpenWindow;
            if (w is null) return ActionResult.Fail("no container open");
            int n = await TransferCoreAsync(w, match, count, false, c).ConfigureAwait(false);
            await EnsureNoWindowAsync(c).ConfigureAwait(false);
            return n > 0 ? ActionResult.Success($"took {n} {label}", n) : ActionResult.Fail($"no {label} in the chest at {Fmt(container)}");
        });

    /// <summary>The drop-keeping tool a strict dig wants, said the way a person would: "a stone pickaxe" (null = none needed).</summary>
    internal static string? HarvestNeed(int blockId)
    {
        if (Tools.CanHarvest(blockId, -1)) return null;
        var b = Blocks.Get(blockId);
        if (b.BestTool is ToolType.Shears) return "shears";
        if (b.BestTool is ToolType.Sword) return "a sword or shears";
        if (b.BestTool is ToolType.None) return "the right tool";
        string tier = Math.Max(0, b.MinHarvestLevel) switch { 0 => "wooden", 1 => "stone", 2 => "iron", _ => "diamond" };
        return $"a{(tier == "iron" ? "n" : "")} {tier} {b.BestTool.ToString().ToLowerInvariant()}";
    }
}
