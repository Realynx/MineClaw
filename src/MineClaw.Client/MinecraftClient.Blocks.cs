using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    // ------------------------------------------------------------------ public block actions

    /// <summary>Digs one block: paths into reach if needed, picks the best tool, aims at a visible face, digs for the
    /// computed time and verifies the server broke it.</summary>
    public Task<ActionResult> DigBlockAsync(int x, int y, int z, CancellationToken ct = default) =>
        RunActionAsync("dig", ct, c => DigCoreAsync(new BlockPos(x, y, z), c));

    /// <summary>Places a block of <paramref name="itemId"/> at (x,y,z) against a solid neighbour; verifies it appeared.</summary>
    public Task<ActionResult> PlaceBlockAsync(int x, int y, int z, int itemId, CancellationToken ct = default) =>
        RunActionAsync("place", ct, c => PlaceCoreAsync(new BlockPos(x, y, z), itemId, c));

    /// <summary>Right-clicks an interactable block (chest, furnace, workbench, door, lever, button, bed...).</summary>
    public Task<ActionResult> ActivateBlockAsync(int x, int y, int z, CancellationToken ct = default) =>
        RunActionAsync("activate", ct, async c =>
        {
            var pos = new BlockPos(x, y, z);
            var r = await ClickBlockAsync(pos, c).ConfigureAwait(false);
            if (!r.Ok) return r;
            await WaitTicksAsync(3, c).ConfigureAwait(false);
            return ActionResult.Success($"activated {BlockName(World.GetBlock(pos), World.GetMeta(pos))} at {Fmt(pos)}");
        });

    /// <summary>Uses the held item, or <paramref name="itemId"/> after selecting it (eat, drink, throw...). Food is
    /// held for the vanilla 32 ticks and the result verified via 0x26 / food level.</summary>
    public Task<ActionResult> UseItemAsync(int? itemId = null, CancellationToken ct = default) =>
        RunActionAsync("use", ct, c => UseCoreAsync(itemId, c));

    /// <summary>Eats the best available food that is not in <see cref="ReservedItems"/>.</summary>
    public Task<ActionResult> EatAsync(CancellationToken ct = default) =>
        RunActionAsync("eat", ct, async c =>
        {
            if (World.Player.Food >= 20) return ActionResult.Fail("not hungry (food 20/20)");
            var food = PickFood();
            if (food is null)
                return ActionResult.Fail(SurvivalReflex.HasAnyFood(World.Inventory.PlayerSlots(), ReservedItems)
                    ? $"only food-poisoning risks left (rotten flesh/raw chicken), eaten automatically at food {Options.Survival.StarvingFood}; use_item to eat one anyway"
                    : "no food (or only reserved food)");
            return await UseCoreAsync(food.Value, c).ConfigureAwait(false);
        });

    // ------------------------------------------------------------------ cores

    /// <param name="generator">Digging a cobblestone generator's output: lava next to it is expected, so instead of
    /// refusing, the bot must stand where released lava can't reach it.</param>
    /// <param name="noShears">Leaves for saplings: shears would drop the leaf block instead of the sapling chance.</param>
    private async Task<ActionResult> DigCoreAsync(BlockPos pos, CancellationToken ct, bool allowPathing = true, bool ignoreProtection = false,
        bool generator = false, bool noShears = false)
    {
        int id = World.GetBlock(pos);
        string name = BlockName(id, World.GetMeta(pos));
        if (!World.IsLoaded(pos.X, pos.Z)) return ActionResult.Fail($"{Fmt(pos)} is not loaded");
        if (id == 0 || BlockShapes.IsLiquid(id)) return ActionResult.Success($"{Fmt(pos)} already clear");
        if (!ignoreProtection && IsBlockProtected(pos, id)) return ActionResult.Fail($"{name} at {Fmt(pos)} is protected");
        if (Blocks.Get(id).Hardness < 0) return ActionResult.Fail($"{name} is unbreakable");
        if (DigSafetyVeto(pos, name, generator, positioned: !allowPathing) is { } veto) return ActionResult.Fail(veto);

        await EnsureNoWindowAsync(ct).ConfigureAwait(false);
        var aim = Aiming.FindDigAim(World, World.Player.EyePosition, pos, Options.Reach);
        // Edge blocks are dug from the inward side; generators from a spot released lava can't reach.
        bool reposition = aim is not null && allowPathing
                          && (OnRimDiggingRim(pos) || (generator && (LavaPathToMe(pos) || LavaNearMe())));
        if (aim is null || reposition)
        {
            if (!allowPathing) return ActionResult.Fail($"can't see/reach {name} at {Fmt(pos)}");
            var g = await GotoReachSafelyAsync(pos, generator, ct).ConfigureAwait(false);
            aim = Aiming.FindDigAim(World, World.Player.EyePosition, pos, Options.Reach);
            if (aim is null) return ActionResult.Fail($"can't reach {name} at {Fmt(pos)}" + (g.Ok ? "" : $" ({g.Message})"));
        }
        if (DigSafetyVeto(pos, name, generator, positioned: true) is { } veto2) return ActionResult.Fail(veto2);

        int held = await SelectToolForAsync(id, ct, noShears).ConfigureAwait(false);
        var snap = World.Player.Snapshot;
        bool inWater = BlockShapes.IsWater(World.GetBlock((int)Math.Floor(snap.Position.X), (int)Math.Floor(snap.Position.Y + PlayerPhysics.EyeHeight), (int)Math.Floor(snap.Position.Z)));
        int ticks = Tools.DigTicks(id, held, snap.OnGround, inWater);
        if (ticks == int.MaxValue) return ActionResult.Fail($"{name} can't be dug");

        var a = aim.Value;
        await LookCoreAsync(a.Yaw, a.Pitch, ct).ConfigureAwait(false);
        int eid = World.Player.EntityId;
        byte face = (byte)a.Face;
        Send(new AnimationPacket { EntityId = eid, Animation = 1 });
        Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusStarted, X = pos.X, Y = pos.Y, Z = pos.Z, Face = face });
        if (ticks > 0)
        {
            int total = ticks + 2; // small margin: the server counts its own ticks since the start packet
            long startTick = TickCount;
            int lastSwing = 0;
            while (TickCount < startTick + total)
            {
                await WaitUntilTickAsync(Math.Min(startTick + total, TickCount + 4), ct).ConfigureAwait(false);
                if (World.GetBlock(pos) != id) break;
                int elapsed = (int)(TickCount - startTick);
                if (elapsed - lastSwing >= 4) { lastSwing = elapsed; Send(new AnimationPacket { EntityId = eid, Animation = 1 }); }
            }
            Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusFinished, X = pos.X, Y = pos.Y, Z = pos.Z, Face = face });
        }

        var wait = TimeSpan.FromMilliseconds(1500 + ticks * Options.TickInterval.TotalMilliseconds * 0.4);
        if (!await WaitUntilAsync(() => World.GetBlock(pos) != id, wait, ct).ConfigureAwait(false))
            return ActionResult.Fail($"{name} at {Fmt(pos)} didn't break" + SpawnProtectionHint(pos));
        return ActionResult.Success($"dug {name} at {Fmt(pos)} ({ticks} ticks, {(held > 0 ? ItemName(held) : "hand")})");
    }

    private async Task<ActionResult> PlaceCoreAsync(BlockPos pos, int itemId, CancellationToken ct, bool allowPathing = true, int? damage = null)
    {
        if (itemId <= 0) return ActionResult.Fail("no block to place");
        if (World.Inventory.CountItem(itemId, damage) == 0) return ActionResult.Fail($"no {ItemName(itemId, damage ?? 0)} in inventory");
        int existing = World.GetBlock(pos);
        if (!Aiming.IsReplaceable(World, existing)) return ActionResult.Fail($"{Fmt(pos)} is occupied by {BlockName(existing, World.GetMeta(pos))}");
        if (!World.IsLoaded(pos.X, pos.Z)) return ActionResult.Fail($"{Fmt(pos)} is not loaded");

        await EnsureNoWindowAsync(ct).ConfigureAwait(false);
        // Placing (and bridging) always sneaks: vanilla edge clamping while the arm is busy.
        bool sneakBefore = _idleSneak;
        _idleSneak = true;
        try
        {
            return await PlaceSneakingAsync(pos, itemId, ct, allowPathing, damage).ConfigureAwait(false);
        }
        finally
        {
            _idleSneak = sneakBefore;
        }
    }

    private async Task<ActionResult> PlaceSneakingAsync(BlockPos pos, int itemId, CancellationToken ct, bool allowPathing, int? damage = null)
    {
        var aim = Aiming.FindPlaceAim(World, World.Player.EyePosition, pos, Options.Reach, AABB.ForPlayer(World.Player.Position));
        if (aim is null)
        {
            if (!allowPathing) return ActionResult.Fail($"no visible support face for {Fmt(pos)}");
            await GotoCoreAsync(new GoalWithinReach(pos, Options.Reach - 0.6), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) }, ct)
                .ConfigureAwait(false);
            aim = Aiming.FindPlaceAim(World, World.Player.EyePosition, pos, Options.Reach, AABB.ForPlayer(World.Player.Position));
            if (aim is null) return ActionResult.Fail($"can't place at {Fmt(pos)}: no solid neighbour face in reach (or I'm in the way)");
        }

        if (await SelectItemAsync(itemId, damage, ct).ConfigureAwait(false) < 0) return ActionResult.Fail($"couldn't hold {ItemName(itemId)}");
        var a = aim.Value;
        await LookCoreAsync(a.Yaw, a.Pitch, ct).ConfigureAwait(false);
        Send(new PlayerBlockPlacementPacket
        {
            X = a.Against.X, Y = a.Against.Y, Z = a.Against.Z, Direction = (byte)a.Face, HeldItem = HeldStack(),
        });
        Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
        if (!await WaitUntilAsync(() => !Aiming.IsReplaceable(World, World.GetBlock(pos)), TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false))
            return ActionResult.Fail($"{ItemName(itemId)} didn't appear at {Fmt(pos)}" + SpawnProtectionHint(pos));
        return ActionResult.Success($"placed {ItemName(itemId)} at {Fmt(pos)}");
    }

    /// <summary>Path into reach of an interactable block and right-click it.</summary>
    /// <summary>Last block right-clicked by <see cref="ClickBlockAsync"/> (activate / open) and when (ms tick count), so
    /// the next 0x64 can be attributed to it (<see cref="ContainerOpened"/>).</summary>
    private volatile Activation? _lastActivated;
    private sealed record Activation(BlockPos Pos, long At);

    private async Task<ActionResult> ClickBlockAsync(BlockPos pos, CancellationToken ct)
    {
        int id = World.GetBlock(pos);
        if (id == 0 || !Blocks.Get(id).Interactable)
            return ActionResult.Fail($"{BlockName(id, World.GetMeta(pos))} at {Fmt(pos)} is not something to use");
        var aim = Aiming.FindDigAim(World, World.Player.EyePosition, pos, Options.Reach);
        if (aim is null)
        {
            var g = await GotoCoreAsync(new GoalWithinReach(pos, Options.Reach - 0.6), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) }, ct)
                .ConfigureAwait(false);
            aim = Aiming.FindDigAim(World, World.Player.EyePosition, pos, Options.Reach);
            if (aim is null) return ActionResult.Fail($"can't reach {BlockName(id, 0)} at {Fmt(pos)}" + (g.Ok ? "" : $" ({g.Message})"));
        }
        var a = aim.Value;
        await LookCoreAsync(a.Yaw, a.Pitch, ct).ConfigureAwait(false);
        _lastActivated = new Activation(pos, Environment.TickCount64);
        Send(new PlayerBlockPlacementPacket { X = pos.X, Y = pos.Y, Z = pos.Z, Direction = (byte)a.Face, HeldItem = HeldStack() });
        Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
        return ActionResult.Success("clicked");
    }

    private async Task<ActionResult> UseCoreAsync(int? itemId, CancellationToken ct)
    {
        await EnsureNoWindowAsync(ct).ConfigureAwait(false);
        if (itemId is { } want)
        {
            if (await SelectItemAsync(want, null, ct).ConfigureAwait(false) < 0) return ActionResult.Fail($"no {ItemName(want)}");
        }
        var held = HeldStack();
        if (held.IsEmpty) return ActionResult.Fail("holding nothing");
        bool isFood = Items.Food(held.Id) is not null;
        if (isFood && World.Player.Food >= 20 && held.Id != Items.GoldenApple) return ActionResult.Fail("not hungry (food 20/20)");

        int eatsBefore = Volatile.Read(ref _eatAcceptedCount);
        int foodBefore = World.Player.Food;
        int countBefore = World.Inventory.CountItem(held.Id);
        Send(PlayerBlockPlacementPacket.UseItem(held));
        if (!isFood)
        {
            await WaitTicksAsync(2, ct).ConfigureAwait(false);
            return ActionResult.Success($"used {ItemName(held.Id, held.Damage)}");
        }
        // Eating: the server finishes after 32 ticks as long as the held item doesn't change.
        bool done = await WaitUntilAsync(() => Volatile.Read(ref _eatAcceptedCount) > eatsBefore || World.Player.Food > foodBefore
                                               || World.Inventory.CountItem(held.Id) < countBefore,
            TimeSpan.FromMilliseconds(Options.TickInterval.TotalMilliseconds * 32 + 1500), ct).ConfigureAwait(false);
        if (!done) return ActionResult.Fail($"eating {ItemName(held.Id)} didn't finish");
        await WaitUntilAsync(() => World.Player.Food > foodBefore, TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        return ActionResult.Success($"ate {ItemName(held.Id)} (food {World.Player.Food}/20)");
    }

    // ------------------------------------------------------------------ held item management

    private ItemStack HeldStack() => World.Inventory.HeldItem(World.Player.HeldSlot);

    private void SelectHotbar(int index)
    {
        index = Math.Clamp(index, 0, 8);
        if (World.Player.HeldSlot == index && _serverHeldSlot == index) return;
        World.Player.HeldSlot = index;
        _serverHeldSlot = index;
        Send(new HoldingChangePacket { Slot = (short)index });
    }

    private int _serverHeldSlot = -1;

    /// <summary>Puts an item stack matching (id, damage) in the hand, moving it from the main inventory into the hotbar
    /// if needed. Returns the hotbar index or -1 when not in the inventory.</summary>
    private async Task<int> SelectItemAsync(int itemId, int? damage, CancellationToken ct)
    {
        var inv = World.Inventory;
        for (int i = 0; i < 9; i++)
        {
            var s = inv.GetPlayerSlot(36 + i);
            if (!s.IsEmpty && s.Id == itemId && (damage is null || s.Damage == damage))
            {
                SelectHotbar(i);
                return i;
            }
        }
        var slots = inv.FindSlots(itemId, damage);
        if (slots.Count == 0) return -1;
        int hb = ChooseHotbarTarget();
        await SwapIntoHotbarAsync(slots[0], hb, ct).ConfigureAwait(false);
        var now = inv.GetPlayerSlot(36 + hb);
        if (now.IsEmpty || now.Id != itemId) return -1;
        SelectHotbar(hb);
        return hb;
    }

    /// <summary>Selects the best tool for the block (or a harmless slot for bare-hand blocks). Returns the held item id (-1 = hand).</summary>
    private async Task<int> SelectToolForAsync(int blockId, CancellationToken ct, bool noShears = false)
    {
        var slots = World.Inventory.PlayerSlots();
        var usable = new ItemStack[45];
        for (int i = 0; i < 45; i++) usable[i] = i >= 9 && !(noShears && slots[i].Id == Items.Shears) ? slots[i] : ItemStack.Empty;
        int best = Tools.BestToolIndex(blockId, usable);
        if (best >= 0)
        {
            var tool = usable[best];
            int hb = await SelectItemAsync(tool.Id, tool.Damage, ct).ConfigureAwait(false);
            return hb >= 0 ? HeldStack().Id : -1;
        }
        // Bare hand is best: avoid wearing down a tool/sword in hand.
        var held = HeldStack();
        if (!held.IsEmpty && (Items.Get(held.Id).MaxDamage > 0 || (noShears && held.Id == Items.Shears)))
        {
            for (int i = 0; i < 9; i++)
            {
                var s = slots[36 + i];
                if (s.IsEmpty || Items.Get(s.Id).MaxDamage == 0) { SelectHotbar(i); break; }
            }
        }
        var h = HeldStack();
        return h.IsEmpty ? -1 : h.Id;
    }

    /// <summary>Hotbar slot to receive a new item: empty, else one not holding a tool, else the last slot.</summary>
    private int ChooseHotbarTarget()
    {
        var inv = World.Inventory;
        for (int i = 0; i < 9; i++) if (inv.GetPlayerSlot(36 + i).IsEmpty) return i;
        for (int i = 8; i >= 0; i--)
        {
            var s = inv.GetPlayerSlot(36 + i);
            if (Items.Get(s.Id).MaxDamage == 0 && Items.Food(s.Id) is null) return i;
        }
        return 8;
    }

    /// <summary>Swaps window-0 slot <paramref name="playerSlot"/> (9..35) with hotbar index <paramref name="hotbar"/> via clicks.</summary>
    private async Task SwapIntoHotbarAsync(int playerSlot, int hotbar, CancellationToken ct)
    {
        await EnsureNoWindowAsync(ct).ConfigureAwait(false);
        int hb = 36 + hotbar;
        if (playerSlot == hb) return;
        await ClickCoreAsync(playerSlot, false, false, ct).ConfigureAwait(false);
        await ClickCoreAsync(hb, false, false, ct).ConfigureAwait(false);
        if (!World.Inventory.Cursor.IsEmpty) await ClickCoreAsync(playerSlot, false, false, ct).ConfigureAwait(false);
        if (!World.Inventory.Cursor.IsEmpty) await StashCursorAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Best non-reserved food for the current hunger (see <see cref="SurvivalReflex.PickFood"/>).</summary>
    private int? PickFood()
    {
        var p = World.Player;
        return SurvivalReflex.PickFood(World.Inventory.PlayerSlots(), ReservedItems, p.Health, p.Food, Options.Survival)?.ItemId;
    }

    // ------------------------------------------------------------------ safety helpers

    private bool IsBlockProtected(BlockPos pos, int id) => IsProtected?.Invoke(pos, id) ?? false;

    private bool NextToLava(BlockPos p)
    {
        foreach (var f in BlockFaceExtensions.All)
        {
            var n = p.Offset(f);
            if (BlockShapes.IsLava(World.GetBlock(n))) return true;
        }
        return false;
    }

    /// <summary>The blocks currently holding the player up (top layer under the hitbox).</summary>
    private List<BlockPos> SupportBlocks() => Safety.SupportBlocks(World, World.Player.Position);

    /// <summary>True when removing <paramref name="target"/> would leave nothing but void below the hitbox.</summary>
    private bool WouldDropIntoVoid(BlockPos target) => Safety.RemovalLeavesOverVoid(World, World.Player.Position, target);

    /// <summary>Why digging <paramref name="pos"/> from where the bot stands is unsafe, or null.</summary>
    private string? DigSafetyVeto(BlockPos pos, string name, bool generator, bool positioned)
    {
        if (SupportBlocks().Contains(pos))
            return $"won't dig the {name} I'm standing on at {Fmt(pos)}" + (WouldDropIntoVoid(pos) ? " (it's all that holds me over the void)" : "");
        if (WouldDropIntoVoid(pos)) return $"won't dig {name} at {Fmt(pos)}: it's the last block between me and the void";
        if (LavaCouldFlowInto(pos))
        {
            if (!generator) return $"won't dig {name} at {Fmt(pos)}: lava next to it would flow out";
            if (positioned && LavaPathToMe(pos)) return $"won't dig {name} at {Fmt(pos)} from here: released lava could reach me";
        }
        return null;
    }

    private bool LavaNearMe()
    {
        var p = World.Player.Position;
        return Safety.LavaNear(World, (int)Math.Floor(p.X), (int)Math.Floor(p.Y + 1e-6), (int)Math.Floor(p.Z));
    }

    private string SpawnProtectionHint(BlockPos pos)
    {
        var sp = World.Spawn;
        int d = Math.Max(Math.Abs(pos.X - sp.X), Math.Abs(pos.Z - sp.Z));
        return d <= 16 ? $" (within {d} of spawn: spawn protection needs op?)" : "";
    }

    /// <summary>Scaffold block to use for bridging: cobblestone, then dirt, then planks (never reserved items).</summary>
    private int? ScaffoldItem()
    {
        foreach (int id in new[] { Blocks.Cobblestone, Blocks.Dirt, Blocks.Planks, Blocks.Stone })
            if (!ReservedItems.Contains(id) && World.Inventory.CountItem(id) > 0) return id;
        return null;
    }

    private static string BlockName(int id, int meta) => Blocks.Get(id).Name;
    private static string ItemName(int id, int damage = 0) => Items.DisplayName(id, damage).ToLowerInvariant();
}
