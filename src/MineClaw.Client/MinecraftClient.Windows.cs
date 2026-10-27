using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    private int _windowsOpened;

    // ------------------------------------------------------------------ public window API

    /// <summary>Paths to and right-clicks a chest/furnace/workbench/dispenser and waits for the window (0x64).</summary>
    public Task<ActionResult> OpenContainerAsync(int x, int y, int z, CancellationToken ct = default) =>
        RunActionAsync("open", ct, c => OpenCoreAsync(new BlockPos(x, y, z), c));

    /// <summary>One window click with World click prediction, 0x66 and the server's 0x6A verdict.</summary>
    public Task<ActionResult> ClickSlotAsync(int slot, bool rightClick = false, bool shift = false, CancellationToken ct = default) =>
        RunActionAsync("click", ct, async c =>
        {
            bool ok = await ClickCoreAsync(slot, rightClick, shift, c).ConfigureAwait(false);
            return ok ? ActionResult.Success($"clicked slot {slot}") : ActionResult.Fail($"click on slot {slot} rejected (resynced)");
        });

    /// <summary>Closes the open window after putting the cursor and crafting-grid items back into the inventory
    /// (beta drops them on close).</summary>
    public Task<ActionResult> CloseWindowAsync(CancellationToken ct = default) =>
        RunActionAsync("close", ct, async c =>
        {
            var w = World.Inventory.OpenWindow;
            await EnsureNoWindowAsync(c).ConfigureAwait(false);
            return ActionResult.Success(w is null ? "no window open" : $"closed {w.Type.ToString().ToLowerInvariant()}");
        });

    /// <summary>
    /// Fresh inventory from the server, not the client's (possibly predicted) copy: closes any window (cursor and grid back
    /// into the inventory first), then sends a no-op click on the empty crafting-output slot whose "expected item" can't
    /// match. b1.8.1 NetServerHandler answers a mismatch with 0x6A accepted=false plus the full window contents (0x68) and
    /// the cursor (0x67), which replace the local model. True once that resync arrived (or the server agreed).
    /// </summary>
    public Task<ActionResult> ResyncInventoryAsync(CancellationToken ct = default) =>
        RunActionAsync("resync", ct, async c =>
        {
            await EnsureNoWindowAsync(c).ConfigureAwait(false);
            int version = Volatile.Read(ref _windowItemsVersion);
            short action = World.Inventory.NextActionNumber();
            Send(new WindowClickPacket
            {
                WindowId = 0, Slot = 0, RightClick = false, ActionNumber = action, Shift = false,
                ClickedItem = new ItemStack(1, 64, 0x7FFF), // never the server's (empty) result: forces the rejection + resync
            });
            var key = (0, action);
            if (!await WaitUntilAsync(() => _txResults.ContainsKey(key), TimeSpan.FromSeconds(3), c).ConfigureAwait(false))
                return ActionResult.Fail("no reply to the inventory resync");
            _txResults.TryRemove(key, out bool accepted);
            if (accepted) return ActionResult.Success("inventory confirmed by the server");
            if (!await WaitUntilAsync(() => Volatile.Read(ref _windowItemsVersion) > version, TimeSpan.FromSeconds(2), c).ConfigureAwait(false))
                return ActionResult.Fail("server rejected the probe but sent no window contents");
            return ActionResult.Success("inventory resynced from the server");
        });

    /// <summary>Moves up to <paramref name="count"/> of an item between the open container and the inventory
    /// (<paramref name="toContainer"/> = inventory → container).</summary>
    public Task<ActionResult> TransferAsync(int itemId, int count, bool toContainer, int? damage = null, CancellationToken ct = default) =>
        RunActionAsync("transfer", ct, async c =>
        {
            var w = World.Inventory.OpenWindow;
            if (w is null) return ActionResult.Fail("no container open");
            int n = await TransferCoreAsync(w, s => s.Id == itemId && (damage is null || s.Damage == damage), count, toContainer, c).ConfigureAwait(false);
            string dir = toContainer ? "into" : "out of";
            return n > 0 ? ActionResult.Success($"moved {n} {ItemName(itemId, damage ?? 0)} {dir} the {w.Type.ToString().ToLowerInvariant()}", n)
                         : ActionResult.Fail($"moved no {ItemName(itemId, damage ?? 0)} ({(toContainer ? "none in inventory or container full" : "none in container or inventory full")})");
        });

    /// <summary>Deposits every inventory stack matching <paramref name="filter"/> (default: everything except tools,
    /// armor, food and <see cref="ReservedItems"/>) into a chest. With <paramref name="container"/> the chest is
    /// opened first and closed afterwards; otherwise the open window is used.</summary>
    public Task<ActionResult> DepositAllAsync(BlockPos? container = null, Func<ItemStack, bool>? filter = null, CancellationToken ct = default) =>
        RunActionAsync("deposit", ct, async c =>
        {
            if (container is { } p)
            {
                var o = await OpenCoreAsync(p, c).ConfigureAwait(false);
                if (!o.Ok) return o;
            }
            var w = World.Inventory.OpenWindow;
            if (w is null || w.Type is not (WindowType.Chest or WindowType.Dispenser)) return ActionResult.Fail("no chest open");
            filter ??= DefaultDepositFilter;
            var before = InventoryCounts();
            bool full = false;
            for (int slot = w.MainStart; slot < w.SlotCount; slot++)
            {
                var s = w.Get(slot);
                if (s.IsEmpty || !filter(s) || ReservedItems.Contains(s.Id)) continue;
                await ClickCoreAsync(slot, false, true, c).ConfigureAwait(false);
                if (!w.Get(slot).IsEmpty) { full = true; break; }
            }
            var summary = DiffSummary(before, InventoryCounts(), out int moved);
            if (container is not null) await EnsureNoWindowAsync(c).ConfigureAwait(false);
            if (moved == 0) return ActionResult.Fail(full ? "chest is full" : "nothing to deposit");
            return ActionResult.Success($"deposited {summary}{(full ? " (chest full)" : "")}", moved);
        });

    /// <summary>Takes up to <paramref name="count"/> of an item out of a chest (opened first when <paramref name="container"/> is given).</summary>
    public Task<ActionResult> WithdrawAsync(int itemId, int count, BlockPos? container = null, int? damage = null, CancellationToken ct = default) =>
        RunActionAsync("withdraw", ct, async c =>
        {
            if (container is { } p)
            {
                var o = await OpenCoreAsync(p, c).ConfigureAwait(false);
                if (!o.Ok) return o;
            }
            var w = World.Inventory.OpenWindow;
            if (w is null) return ActionResult.Fail("no container open");
            int n = await TransferCoreAsync(w, s => s.Id == itemId && (damage is null || s.Damage == damage), count, false, c).ConfigureAwait(false);
            if (container is not null) await EnsureNoWindowAsync(c).ConfigureAwait(false);
            return n > 0
                ? ActionResult.Success($"took {n} {ItemName(itemId, damage ?? 0)} (have {World.Inventory.CountItem(itemId)})", n)
                : ActionResult.Fail($"no {ItemName(itemId, damage ?? 0)} taken (none in container or inventory full)");
        });

    /// <summary>Compact listing of the open container's contents.</summary>
    public string DescribeOpenWindow(int maxChars = 600)
    {
        var w = World.Inventory.OpenWindow;
        if (w is null) return "no window open";
        var groups = new Dictionary<(int, int), int>();
        foreach (var (_, item) in w.ContainerContents())
            if (!item.IsEmpty) groups[(item.Id, item.Damage)] = groups.GetValueOrDefault((item.Id, item.Damage)) + item.Count;
        int free = w.ContainerContents().Count(t => t.Item.IsEmpty);
        var parts = groups.OrderByDescending(kv => kv.Value).Select(kv => $"{ItemName(kv.Key.Item1, kv.Key.Item2)} x{kv.Value}");
        string head = $"{w.Type.ToString().ToLowerInvariant()} ({w.ContainerSlots} slots, {free} free)";
        if (w.Type == WindowType.Furnace)
            head += $" progress {w.SmeltProgress:P0}{(w.IsBurning ? " burning" : "")}; in={Stack(w.Get(0))} fuel={Stack(w.Get(1))} out={Stack(w.Get(2))}";
        return Truncate(head + ": " + (groups.Count == 0 ? "empty" : string.Join(", ", parts)), maxChars);
    }

    // ------------------------------------------------------------------ cores

    private async Task<ActionResult> OpenCoreAsync(BlockPos pos, CancellationToken ct)
    {
        int id = World.GetBlock(pos);
        if (id is not (Blocks.Chest or Blocks.Furnace or Blocks.LitFurnace or Blocks.Workbench or Blocks.Dispenser))
            return ActionResult.Fail($"{BlockName(id, 0)} at {Fmt(pos)} is not a container");
        var open = World.Inventory.OpenWindow;
        if (open is not null) await EnsureNoWindowAsync(ct).ConfigureAwait(false);
        else await CleanPlayerGridAsync(ct).ConfigureAwait(false);
        int before = Volatile.Read(ref _windowsOpened);
        int itemsBefore = Volatile.Read(ref _windowItemsVersion);
        var r = await ClickBlockAsync(pos, ct).ConfigureAwait(false);
        if (!r.Ok) return r;
        if (!await WaitUntilAsync(() => Volatile.Read(ref _windowsOpened) > before, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            return ActionResult.Fail($"{BlockName(id, 0)} at {Fmt(pos)} didn't open (blocked above, or protected?)");
        await WaitUntilAsync(() => Volatile.Read(ref _windowItemsVersion) > itemsBefore, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        _lastContainerPos = pos;
        return ActionResult.Success("opened " + DescribeOpenWindow());
    }

    private BlockPos? _lastContainerPos;

    /// <summary>Sends one click and waits for its 0x6A. False if the server rejected it (window resynced).</summary>
    private async Task<bool> ClickCoreAsync(int slot, bool right, bool shift, CancellationToken ct)
    {
        int version = Volatile.Read(ref _windowItemsVersion);
        var r = World.Inventory.ApplyClick(slot, right, shift);
        Send(new WindowClickPacket
        {
            WindowId = (sbyte)r.WindowId, Slot = (short)slot, RightClick = right, ActionNumber = r.ActionNumber, Shift = shift,
            ClickedItem = r.ExpectedItem,
        });
        var key = (r.WindowId, r.ActionNumber);
        if (!await WaitUntilAsync(() => _txResults.ContainsKey(key), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            throw new ActionAbortedException($"no reply to window click on slot {slot}");
        _txResults.TryRemove(key, out bool accepted);
        if (accepted) return true;
        await WaitUntilAsync(() => Volatile.Read(ref _windowItemsVersion) > version, TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>Puts the cursor stack into the inventory part of the active window (matching stacks first, then empty).</summary>
    private async Task<bool> StashCursorAsync(CancellationToken ct)
    {
        for (int guard = 0; guard < 8 && !World.Inventory.Cursor.IsEmpty; guard++)
        {
            var w = World.Inventory.ActiveWindow;
            var cur = World.Inventory.Cursor;
            int target = -1;
            for (int slot = w.HotbarStart; slot < w.SlotCount && target < 0; slot++) target = Fits(w, slot, cur, partialOnly: true) ? slot : -1;
            for (int slot = w.MainStart; slot < w.HotbarStart && target < 0; slot++) target = Fits(w, slot, cur, partialOnly: true) ? slot : -1;
            for (int slot = w.HotbarStart; slot < w.SlotCount && target < 0; slot++) target = w.Get(slot).IsEmpty ? slot : -1;
            for (int slot = w.MainStart; slot < w.HotbarStart && target < 0; slot++) target = w.Get(slot).IsEmpty ? slot : -1;
            if (target < 0) return false;
            await ClickCoreAsync(target, false, false, ct).ConfigureAwait(false);
        }
        return World.Inventory.Cursor.IsEmpty;
    }

    private bool Fits(Window w, int slot, ItemStack item, bool partialOnly)
    {
        var s = w.Get(slot);
        if (s.IsEmpty) return !partialOnly;
        var rules = World.Inventory.Rules;
        return rules.SameItem(s, item) && rules.IsStackable(s) && s.Count < rules.MaxStack(s.Id);
    }

    /// <summary>Returns the cursor and crafting-grid items to the inventory, then closes any open container.</summary>
    private async Task EnsureNoWindowAsync(CancellationToken ct)
    {
        var w = World.Inventory.OpenWindow;
        if (w is null)
        {
            await CleanPlayerGridAsync(ct).ConfigureAwait(false);
            return;
        }
        if (!World.Inventory.Cursor.IsEmpty) await StashCursorAsync(ct).ConfigureAwait(false);
        if (w.Type == WindowType.Workbench)
            for (int slot = 1; slot <= 9; slot++)
                if (!w.Get(slot).IsEmpty) await ClickCoreAsync(slot, false, true, ct).ConfigureAwait(false);
        Send(new CloseWindowPacket { WindowId = (sbyte)w.Id });
        World.Inventory.Close(w.Id);
        await CleanPlayerGridAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Empties the 2×2 grid and cursor while only the player inventory is "open".</summary>
    private async Task CleanPlayerGridAsync(CancellationToken ct)
    {
        if (World.Inventory.OpenWindow is not null) return;
        if (!World.Inventory.Cursor.IsEmpty) await StashCursorAsync(ct).ConfigureAwait(false);
        var pw = World.Inventory.PlayerWindow;
        for (int slot = 1; slot <= 4; slot++)
            if (!pw.Get(slot).IsEmpty) await ClickCoreAsync(slot, false, true, ct).ConfigureAwait(false);
    }

    /// <summary>Moves up to <paramref name="count"/> matching items between container and inventory. Returns the number moved.</summary>
    private async Task<int> TransferCoreAsync(Window w, Func<ItemStack, bool> match, int count, bool toContainer, CancellationToken ct)
    {
        int containerEnd = w.Type == WindowType.Workbench ? 10 : w.ContainerSlots;
        int srcStart = toContainer ? w.MainStart : 0, srcEnd = toContainer ? w.SlotCount : containerEnd;
        var dests = toContainer
            ? Enumerable.Range(0, containerEnd).Where(i => w.KindOf(i) is SlotKind.Container or SlotKind.FurnaceInput).ToList()
            : Enumerable.Range(w.HotbarStart, 9).Concat(Enumerable.Range(w.MainStart, 27)).ToList();
        bool canShift = w.Type is WindowType.Chest or WindowType.Dispenser || !toContainer;
        int moved = 0;
        for (int guard = 0; guard < 64 && moved < count; guard++)
        {
            int src = -1;
            for (int i = srcStart; i < srcEnd; i++)
            {
                var s = w.Get(i);
                if (!s.IsEmpty && match(s)) { src = i; break; }
            }
            if (src < 0) break;
            var stack = w.Get(src);
            int need = count - moved;
            int n;
            if (canShift && stack.Count <= need)
            {
                await ClickCoreAsync(src, false, true, ct).ConfigureAwait(false);
                var after = w.Get(src);
                n = stack.Count - (after.IsEmpty || after.Id != stack.Id ? 0 : after.Count);
            }
            else n = await MoveManualAsync(w, src, dests, need, ct).ConfigureAwait(false);
            if (n <= 0) break;
            moved += n;
        }
        if (!World.Inventory.Cursor.IsEmpty) await StashCursorAsync(ct).ConfigureAwait(false);
        return moved;
    }

    /// <summary>Picks up <paramref name="src"/>, drops exactly up to <paramref name="count"/> items into the destination
    /// slots (left-click when the whole cursor fits, right-click one at a time otherwise), puts the rest back.</summary>
    private async Task<int> MoveManualAsync(Window w, int src, IReadOnlyList<int> dests, int count, CancellationToken ct)
    {
        var rules = World.Inventory.Rules;
        await ClickCoreAsync(src, false, false, ct).ConfigureAwait(false);
        int placed = 0;
        foreach (int d in dests)
        {
            var cur = World.Inventory.Cursor;
            if (cur.IsEmpty || placed >= count) break;
            if (d == src) continue;
            var ds = w.Get(d);
            if (!ds.IsEmpty && !(rules.SameItem(ds, cur) && rules.IsStackable(ds))) continue;
            int room = rules.MaxStack(cur.Id) - (ds.IsEmpty ? 0 : ds.Count);
            int k = Math.Min(Math.Min(room, count - placed), cur.Count);
            if (k <= 0) continue;
            if (k == cur.Count)
            {
                await ClickCoreAsync(d, false, false, ct).ConfigureAwait(false);
            }
            else
            {
                for (int i = 0; i < k; i++) await ClickCoreAsync(d, true, false, ct).ConfigureAwait(false);
            }
            placed += cur.Count - World.Inventory.Cursor.Count;
        }
        if (!World.Inventory.Cursor.IsEmpty)
        {
            await ClickCoreAsync(src, false, false, ct).ConfigureAwait(false);
            if (!World.Inventory.Cursor.IsEmpty) await StashCursorAsync(ct).ConfigureAwait(false);
        }
        return placed;
    }

    /// <summary>Default deposit filter: keep tools, weapons, armor, food, saplings, buckets, seeds.</summary>
    public static bool DefaultDepositFilter(ItemStack s)
    {
        if (s.IsEmpty) return false;
        var info = Items.Get(s.Id);
        if (info.MaxDamage > 0 || info.Tool != ToolType.None) return false;
        if (Items.Food(s.Id) is not null) return false;
        return s.Id is not (Blocks.Sapling or Items.Bucket or Items.WaterBucket or Items.LavaBucket or Items.MilkBucket
            or Blocks.Torch or Items.Seeds or Items.PumpkinSeeds or Items.MelonSeeds);
    }

    private Dictionary<(int, int), int> InventoryCounts()
    {
        var d = new Dictionary<(int, int), int>();
        var slots = World.Inventory.PlayerSlots();
        for (int i = 9; i < 45; i++)
        {
            var s = slots[i];
            if (!s.IsEmpty) d[(s.Id, s.Damage)] = d.GetValueOrDefault((s.Id, s.Damage)) + s.Count;
        }
        return d;
    }

    private static string DiffSummary(Dictionary<(int, int), int> before, Dictionary<(int, int), int> after, out int total)
    {
        var parts = new List<string>();
        total = 0;
        foreach (var (k, v) in before)
        {
            int diff = v - after.GetValueOrDefault(k);
            if (diff <= 0) continue;
            total += diff;
            parts.Add($"{diff} {ItemName(k.Item1, k.Item2)}");
        }
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts.Take(8)) + (parts.Count > 8 ? $" +{parts.Count - 8} more" : "");
    }

    private static string Stack(ItemStack s) => s.IsEmpty ? "-" : $"{ItemName(s.Id, s.Damage)} x{s.Count}";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 3)] + "...";
}
