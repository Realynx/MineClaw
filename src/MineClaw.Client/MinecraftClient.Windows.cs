using System.Collections.Concurrent;
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

    /// <summary>
    /// One click of any 1.12.2 click type (<see cref="ClickMode"/>: swap with hotbar key <paramref name="button"/>, throw, drag
    /// stages, double-click pickup-all), predicted by the 1.12.2 inventory model and confirmed by the server's transaction.
    /// b1.8.1 knows only pickup and shift clicks.
    /// </summary>
    public Task<ActionResult> ClickSlotAsync(int slot, ClickMode mode, int button, CancellationToken ct = default) =>
        RunActionAsync("click", ct, async c =>
        {
            bool ok = await ClickCoreAsync(slot, mode, button, c).ConfigureAwait(false);
            return ok ? ActionResult.Success($"{mode.ToString().ToLowerInvariant()} click on slot {slot}")
                : ActionResult.Fail($"{mode.ToString().ToLowerInvariant()} click on slot {slot} rejected (resynced)");
        });

    /// <summary>
    /// Swaps the held hotbar stack with the off hand (1.12.2 Player Digging status 6, the F key; the server answers with the
    /// two slots). Fails on versions without an off hand.
    /// </summary>
    public Task<ActionResult> SwapHandsAsync(CancellationToken ct = default) =>
        RunActionAsync("swap hands", ct, async c =>
        {
            if (!World.Inventory.Model.HasOffhand) return ActionResult.Fail($"{Options.Profile.Version.Id} has no off hand");
            await EnsureNoWindowAsync(c).ConfigureAwait(false);
            Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusSwapHands, Face = 0 });
            World.Inventory.SwapHands(World.Player.HeldSlot);
            return ActionResult.Success($"off hand now holds {Stack(World.Inventory.Offhand)}");
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
            return await ProbeCoreAsync(c).ConfigureAwait(false) switch
            {
                ProbeOutcome.Confirmed => ActionResult.Success("inventory confirmed by the server"),
                ProbeOutcome.Resynced => ActionResult.Success("inventory resynced from the server"),
                ProbeOutcome.NoContents => ActionResult.Fail("server rejected the probe but sent no window contents"),
                _ => ActionResult.Fail("no reply to the inventory resync"),
            };
        });

    /// <summary>Moves up to <paramref name="count"/> of an item between the open container and the inventory
    /// (<paramref name="toContainer"/> = inventory → container).</summary>
    public Task<ActionResult> TransferAsync(int itemId, int count, bool toContainer, int? damage = null, CancellationToken ct = default) =>
        RunActionAsync("transfer", ct, async c =>
        {
            var w = World.Inventory.OpenWindow;
            if (w is null) return ActionResult.Fail("no container open");
            Func<ItemStack, bool> match = s => s.Id == itemId && (damage is null || s.Damage == damage);
            int n = await TransferCoreAsync(w, match, count, toContainer, c).ConfigureAwait(false);
            string dir = toContainer ? "into" : "out of";
            return n > 0 ? ActionResult.Success($"moved {n} {ItemName(itemId, damage ?? 0)} {dir} the {w.Type.ToString().ToLowerInvariant()}", n)
                         : ActionResult.Fail(toContainer ? $"moved no {ItemName(itemId, damage ?? 0)} (none in inventory or container full)"
                             : EmptyTakeReason(w, match, itemId, damage, _lastContainerPos));
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
            // A stack that didn't fit only means no room for THAT item (no empty slot, its partial stacks full): other items may
            // still top up their own partial stacks, so go on with them (live 10-27: one leftover ended the whole deposit).
            var noRoom = new HashSet<(int, int)>();
            for (int slot = w.MainStart; slot < w.SlotCount; slot++)
            {
                var s = w.Get(slot);
                if (s.IsEmpty || !filter(s) || ReservedItems.Contains(s.Id) || noRoom.Contains((s.Id, s.Damage))) continue;
                await ClickCoreAsync(slot, false, true, c).ConfigureAwait(false);
                if (!w.Get(slot).IsEmpty)
                {
                    full = true;
                    noRoom.Add((s.Id, s.Damage));
                }
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
            Func<ItemStack, bool> match = s => s.Id == itemId && (damage is null || s.Damage == damage);
            int n = await TransferCoreAsync(w, match, count, false, c).ConfigureAwait(false);
            // Nothing moved: say which it was, from the window as it stands now (a click race resyncs it from the server first).
            string? why = n > 0 ? null : EmptyTakeReason(w, match, itemId, damage, container);
            if (container is not null) await EnsureNoWindowAsync(c).ConfigureAwait(false);
            return n > 0
                ? ActionResult.Success($"took {n} {ItemName(itemId, damage ?? 0)} (have {World.Inventory.CountItem(itemId)})", n)
                : ActionResult.Fail(why!);
        });

    /// <summary>
    /// Why a take moved nothing, naming what the container actually held (live 10-27 21:06: "none in container or inventory full"
    /// left the brain unable to tell an emptied chest from a bug): "no bread in the chest at 14,64,-5 (it had: stone axe x1,
    /// oak log x40)" / "... is empty" / "inventory full (bread x5 still in the chest)".
    /// </summary>
    private string EmptyTakeReason(Window w, Func<ItemStack, bool> match, int itemId, int? damage, BlockPos? at)
    {
        string name = ItemName(itemId, damage ?? 0);
        string where = $"the {w.Type.ToString().ToLowerInvariant()}" + (at is { } p ? $" at {Fmt(p)}" : "");
        var held = w.ContainerContents().Select(t => t.Item).Where(i => !i.IsEmpty).ToList();
        int there = held.Where(match).Sum(i => i.Count);
        if (there > 0) return $"no {name} taken: inventory full ({name} x{there} still in {where})";
        if (held.Count == 0) return $"no {name} in {where}: it is empty";
        var parts = held.GroupBy(i => (i.Id, Items.Get(i.Id).MaxDamage > 0 ? 0 : (int)i.Damage)).OrderByDescending(g => g.Sum(i => i.Count))
            .Select(g => $"{ItemName(g.Key.Id, g.Key.Item2)} x{g.Sum(i => i.Count)}").ToList();
        string list = string.Join(", ", parts.Take(8)) + (parts.Count > 8 ? $" +{parts.Count - 8} more" : "");
        return $"no {name} in {where} (it had: {list})";
    }

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
            head += $" progress {Percent.Of(w.SmeltProgress)}{(w.IsBurning ? " burning" : "")}; in={Stack(w.Get(0))} fuel={Stack(w.Get(1))} out={Stack(w.Get(2))}";
        return Truncate(head + ": " + (groups.Count == 0 ? "empty" : string.Join(", ", parts)), maxChars);
    }

    // ------------------------------------------------------------------ cores

    private async Task<ActionResult> OpenCoreAsync(BlockPos pos, CancellationToken ct)
    {
        int id = World.GetBlock(pos);
        // 1.12.2's trapped chest is a chest window too (was: "trapped chest ... is not a container", so no deposit ever reached one).
        if (id is not (Blocks.Chest or ChestPairs.TrappedChest or Blocks.Furnace or Blocks.LitFurnace or Blocks.Workbench or Blocks.Dispenser)
            && !StationBlocks.Contains(id))
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

    /// <summary>The container last opened by an action (the inventory ledger names it: "deposited (chest 14,64,-5)").</summary>
    public BlockPos? LastContainerPos => _lastContainerPos;

    /// <summary>The click awaiting its 0x6A (null = none). Read by the packet thread to spot slot updates that race it.</summary>
    private volatile ClickInFlight? _clickInFlight;
    private readonly ConcurrentDictionary<(int Window, short Action), bool> _txRaced = new();
    /// <summary>How often the local window model was replaced by the server's (a rejected click, a raced slot update).
    /// Multi-click macros (crafting) compare it to restart a step from the fresh state.</summary>
    private int _windowResyncs;
    private long _lastSlotPacketMs;

    /// <summary>One click between its prediction and its 0x6A. <see cref="Changed"/> null = not predicted yet (any slot
    /// packet then counts as a race).</summary>
    private sealed class ClickInFlight(int window, short action, HashSet<int>? changed, bool cursorChanged)
    {
        public int Window { get; } = window;
        public short Action { get; } = action;
        public HashSet<int>? Changed { get; } = changed;
        public bool CursorChanged { get; } = cursorChanged;
        public volatile bool Pending = true;
        public volatile bool Raced;
    }

    /// <summary>
    /// Packet thread, before a 0x67/0x68 is applied. One that touches what the click in flight changed was generated before
    /// the server applied that click (it is ahead of the click's 0x6A in TCP order: e.g. the tick that echoes the slots a
    /// closed chest window changed, or the stale copies after a rejected click), so it overwrites our prediction with
    /// pre-click contents. The server then accepts the click and never re-sends the slot (b1.8.1 sends no 0x67 for an
    /// accepted click's changes): the model must be resynced or it keeps phantom/missing stacks.
    /// </summary>
    private void NoteSlotPacket(int windowId, int slot, bool fullWindow, ItemStack? item = null)
    {
        Interlocked.Exchange(ref _lastSlotPacketMs, Environment.TickCount64);
        if (_clickInFlight is not { Pending: true } f) return;
        // A slot update that agrees with the prediction can't hurt it, whenever it was generated. 1.12.2's server sends the
        // crafting output (slotChangedCraftingGrid → SPacketSetSlot slot 0) during the click, ahead of its confirm.
        if (!fullWindow && item is { } got && f.Changed is not null && SlotNow(windowId, slot) is { } now && now.Equals(got)) return;
        if (f.Changed is null || fullWindow) { if (f.Changed is null || windowId == f.Window || windowId == 0) f.Raced = true; return; }
        if (windowId == -1 && slot == -1) { if (f.CursorChanged) f.Raced = true; return; }
        int s = slot;
        if (windowId != f.Window)
        {
            // A window-0 slot while a container is open: map the player slot into the clicked window.
            if (windowId != 0 || World.Inventory.GetWindow(f.Window) is not { } w) return;
            s = w.FromPlayerSlot(slot);
        }
        if (s >= 0 && f.Changed.Contains(s)) f.Raced = true;
    }

    private ItemStack? SlotNow(int windowId, int slot)
    {
        if (windowId == -1 && slot == -1) return World.Inventory.Cursor;
        return World.Inventory.GetWindow(windowId) is { } w && slot >= 0 && slot < w.SlotCount ? w.Get(slot) : null;
    }

    /// <summary>Packet thread: the 0x6A for a click arrived (recorded before its result becomes visible to the click).</summary>
    private void NoteTransaction(int windowId, short action)
    {
        if (_clickInFlight is { Pending: true } f && f.Window == windowId && f.Action == action)
        {
            f.Pending = false;
            _txRaced[(windowId, action)] = f.Raced;
        }
    }

    /// <summary>
    /// Sends one click and waits for its 0x6A. False when the local model was replaced by the server's: the server rejected
    /// the click (we then wait for its 0x68 and for the next tick's slot echoes before going on), or a slot update raced the
    /// click (we then force a resync). Callers doing several clicks restart from the fresh state.
    /// </summary>
    private Task<bool> ClickCoreAsync(int slot, bool right, bool shift, CancellationToken ct) =>
        ClickCoreAsync(slot, shift ? ClickMode.QuickMove : ClickMode.Pickup, right ? 1 : 0, ct);

    private async Task<bool> ClickCoreAsync(int slot, ClickMode mode, int button, CancellationToken ct)
    {
        bool right = button == 1 && (mode is ClickMode.Pickup or ClickMode.QuickMove), shift = mode == ClickMode.QuickMove;
        if (!World.Inventory.Model.ModernClicks && mode is not (ClickMode.Pickup or ClickMode.QuickMove))
            throw new ActionAbortedException($"{mode} clicks are not supported on {Options.Profile.Version.Id}");
        int version = Volatile.Read(ref _windowItemsVersion);
        var marker = new ClickInFlight(World.Inventory.ActiveWindow.Id, -1, null, true);
        _clickInFlight = marker; // a slot packet between here and the real marker counts as a race (conservative)
        var r = World.Inventory.ApplyClick(slot, mode, button);
        var f = new ClickInFlight(r.WindowId, r.ActionNumber, [.. r.ChangedSlots], r.CursorChanged) { Raced = marker.Raced };
        _clickInFlight = f;
        if (marker.Raced) f.Raced = true;
        var key = (r.WindowId, r.ActionNumber);
        try
        {
            Send(new WindowClickPacket
            {
                WindowId = (sbyte)r.WindowId, Slot = (short)slot, RightClick = right, ActionNumber = r.ActionNumber, Shift = shift,
                ClickedItem = r.ExpectedItem, Mode = (byte)mode, Button = (sbyte)button,
            });
            if (!await WaitUntilAsync(() => _txResults.ContainsKey(key), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
                throw new ActionAbortedException($"no reply to window click on slot {slot}");
        }
        finally
        {
            f.Pending = false;
            if (ReferenceEquals(_clickInFlight, f)) _clickInFlight = null;
        }
        _txResults.TryRemove(key, out bool accepted);
        _txRaced.TryRemove(key, out bool raced);
        if (accepted && !raced) return true;
        Interlocked.Increment(ref _windowResyncs);
        if (accepted)
        {
            Log($"a slot update raced click {r.ActionNumber} in window {r.WindowId}; resyncing");
            await ProbeCoreAsync(ct).ConfigureAwait(false);
            return false;
        }
        // Rejected: the server applied the click its own way and sends 0x68 + the cursor. Its per-slot copies stay stale
        // until its next tick, which echoes them as 0x67s: let those land before the next click so they can't race it.
        await WaitUntilAsync(() => Volatile.Read(ref _windowItemsVersion) > version, TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
        await SettleSlotsAsync(ct).ConfigureAwait(false);
        return false;
    }

    private enum ProbeOutcome { NoReply, Confirmed, Resynced, NoContents }

    /// <summary>
    /// Asks the server for the active window's real contents: a shift-click on a slot that is empty here (a no-op for the
    /// server whatever the cursor holds) carrying an "expected item" that can't match. b1.8.1 NetServerHandler answers a
    /// mismatch with 0x6A accepted=false plus the full window (0x68) and the cursor (0x67), which replace the local model.
    /// If the server's slot isn't empty after all, shift-clicking it only moves it into the inventory, never out of reach.
    /// </summary>
    private async Task<ProbeOutcome> ProbeCoreAsync(CancellationToken ct)
    {
        var w = World.Inventory.ActiveWindow;
        int slot = ProbeSlot(w);
        if (slot < 0) return ProbeOutcome.NoReply;
        int version = Volatile.Read(ref _windowItemsVersion);
        short action = World.Inventory.NextActionNumber();
        Send(new WindowClickPacket
        {
            WindowId = (sbyte)w.Id, Slot = (short)slot, RightClick = false, ActionNumber = action, Shift = true,
            ClickedItem = new ItemStack(1, 64, 0x7FFF), // never what the server's slotClick returns: forces the resync
        });
        var key = (w.Id, action);
        if (!await WaitUntilAsync(() => _txResults.ContainsKey(key), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            return ProbeOutcome.NoReply;
        _txResults.TryRemove(key, out bool accepted);
        if (accepted) return ProbeOutcome.Confirmed;
        if (!await WaitUntilAsync(() => Volatile.Read(ref _windowItemsVersion) > version, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            return ProbeOutcome.NoContents;
        await SettleSlotsAsync(ct).ConfigureAwait(false);
        return ProbeOutcome.Resynced;
    }

    /// <summary>A slot that is empty in the local model and harmless to shift-click on the server: the crafting output with
    /// an empty grid, an empty grid cell / furnace output / container slot / armor slot, else an empty inventory slot.
    /// -1 = none.</summary>
    public static int ProbeSlot(Window w)
    {
        bool Empty(int i) => w.Get(i).IsEmpty;
        switch (w.Type)
        {
            case WindowType.Player:
                if (Enumerable.Range(0, 5).All(Empty)) return 0;
                foreach (int i in Enumerable.Range(5, 4)) if (Empty(i)) return i;
                break;
            case WindowType.Workbench:
                if (Enumerable.Range(0, 10).All(Empty)) return 0;
                foreach (int i in Enumerable.Range(1, 9)) if (Empty(i)) return i;
                break;
            case WindowType.Furnace:
                if (Empty(2)) return 2;
                break;
            default:
                for (int i = 0; i < w.ContainerSlots; i++) if (Empty(i)) return i;
                break;
        }
        for (int i = w.MainStart; i < w.SlotCount; i++) if (Empty(i)) return i;
        return -1;
    }

    /// <summary>Waits until no slot packet arrived for ~3 server ticks (at most 600 ms).</summary>
    private async Task SettleSlotsAsync(CancellationToken ct)
    {
        long start = Environment.TickCount64;
        Interlocked.Exchange(ref _lastSlotPacketMs, start);
        while (Environment.TickCount64 - Interlocked.Read(ref _lastSlotPacketMs) < 150 && Environment.TickCount64 - start < 600)
            await Task.Delay(25, ct).ConfigureAwait(false);
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
        return !KeptFromDeposits.Contains(s.Id);
    }

    /// <summary>Items the default deposit keeps besides tools, armour and food: saplings, buckets, torches, seeds.</summary>
    private static readonly HashSet<int> KeptFromDeposits =
    [
        Blocks.Sapling, Items.Bucket, Items.WaterBucket, Items.LavaBucket, Items.MilkBucket, Blocks.Torch, Items.Seeds, Items.PumpkinSeeds,
        Items.MelonSeeds,
    ];

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

    private string DiffSummary(Dictionary<(int, int), int> before, Dictionary<(int, int), int> after, out int total)
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

    private string Stack(ItemStack s) => s.IsEmpty ? "-" : $"{ItemName(s.Id, s.Damage)} x{s.Count}";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 3)] + "...";
}
