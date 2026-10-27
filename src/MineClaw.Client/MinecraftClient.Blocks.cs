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
        RunActionAsync("dig", ct, c => DigCoreAsync(new BlockPos(x, y, z), c, floorGuard: true));

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
    /// <param name="floorGuard">A free-standing dig/mine (the model or a script picked the block): never the floor of a walkway
    /// near the void (see <see cref="WalkwayFloorVeto"/>). Macros that dig their own blocks (pillars, trees, paths) skip it.</param>
    private async Task<ActionResult> DigCoreAsync(BlockPos pos, CancellationToken ct, bool allowPathing = true, bool ignoreProtection = false,
        bool generator = false, bool noShears = false, bool floorGuard = false)
    {
        int id = World.GetBlock(pos);
        string name = BlockName(id, World.GetMeta(pos));
        if (!World.IsLoaded(pos.X, pos.Z)) return ActionResult.Fail($"{Fmt(pos)} is not loaded");
        if (id == 0 || BlockShapes.IsLiquid(id)) return ActionResult.Success($"{Fmt(pos)} already clear");
        if (!ignoreProtection && IsBlockProtected(pos, id))
            return ActionResult.Fail(id == Blocks.Log && !UserProtected(pos, id)
                ? $"won't dig the log at {Fmt(pos)}: that tree is one of the island's last {MinTrees} (chop_tree fells and replants it once I hold a sapling)"
                : $"{name} at {Fmt(pos)} is protected");
        if (Blocks.Get(id).Hardness < 0) return ActionResult.Fail($"{name} is unbreakable");
        if (EditBlocked(pos)) return ActionResult.Fail(BlockedReason(pos));
        if (DigSafetyVeto(pos, name, generator, positioned: !allowPathing) is { } veto) return ActionResult.Fail(veto);
        if (floorGuard && !generator && WalkwayFloorVeto(pos, name) is { } floor) return ActionResult.Fail(floor);

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
        {
            NoteEditReverted(pos, "dig"); // first edit of the macro: aborts it (spawn protection, claims)
            return ActionResult.Fail($"{name} at {Fmt(pos)} didn't break" + SpawnProtectionHint(pos));
        }
        NoteEditOk(pos);
        return ActionResult.Success($"dug {name} at {Fmt(pos)} ({ticks} ticks, {(held > 0 ? ItemName(held) : "hand")})");
    }

    /// <param name="face">Click this face of the neighbour at pos − normal(face) (wall-mounted blocks); null = any visible support.</param>
    private async Task<ActionResult> PlaceCoreAsync(BlockPos pos, int itemId, CancellationToken ct, bool allowPathing = true, int? damage = null,
        BlockFace? face = null)
    {
        if (itemId <= 0) return ActionResult.Fail("no block to place");
        if (World.Inventory.CountItem(itemId, damage) == 0) return ActionResult.Fail($"no {ItemName(itemId, damage ?? 0)} in inventory");
        int existing = World.GetBlock(pos);
        if (!Aiming.IsReplaceable(World, existing)) return ActionResult.Fail($"{Fmt(pos)} is occupied by {BlockName(existing, World.GetMeta(pos))}");
        if (!World.IsLoaded(pos.X, pos.Z)) return ActionResult.Fail($"{Fmt(pos)} is not loaded");
        if (EditBlocked(pos)) return ActionResult.Fail(BlockedReason(pos));

        await EnsureNoWindowAsync(ct).ConfigureAwait(false);
        // Placing (and bridging) always sneaks: vanilla edge clamping while the arm is busy.
        bool sneakBefore = _idleSneak;
        _idleSneak = true;
        try
        {
            return await PlaceSneakingAsync(pos, itemId, ct, allowPathing, damage, face).ConfigureAwait(false);
        }
        finally
        {
            _idleSneak = sneakBefore;
        }
    }

    private async Task<ActionResult> PlaceSneakingAsync(BlockPos pos, int itemId, CancellationToken ct, bool allowPathing, int? damage = null,
        BlockFace? face = null)
    {
        PlaceAim? FindAim() => face is { } f
            ? FacePlaceAim(pos, f)
            : Aiming.FindPlaceAim(World, World.Player.EyePosition, pos, Options.Reach, AABB.ForPlayer(World.Player.Position));
        if (face is { } wf)
        {
            var n = wf.Normal();
            var against = new BlockPos(pos.X - n.X, pos.Y - n.Y, pos.Z - n.Z);
            int aid = World.GetBlock(against);
            if (aid == 0 || BlockShapes.IsLiquid(aid) || !BlockShapes.HasCollision(World, against.X, against.Y, against.Z))
                return ActionResult.Fail($"nothing solid at {Fmt(against)} to place against ({wf.ToString().ToLowerInvariant()} face)");
            if (Blocks.Get(aid).Interactable) return ActionResult.Fail($"{BlockName(aid, 0)} at {Fmt(against)} would open instead of taking the block");
        }
        var aim = FindAim();
        if (aim is null)
        {
            if (!allowPathing) return ActionResult.Fail($"no visible support face for {Fmt(pos)}");
            await GotoCoreAsync(new GoalWithinReach(pos, Options.Reach - 0.6), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) }, ct)
                .ConfigureAwait(false);
            aim = FindAim();
            // An island edge cell whose only support is the outer face of the floor block beside it: that face is only in
            // view when leaning out over the edge, never from a cell centre.
            if (aim is null && face is null && await LeanForPlaceAsync(pos, ct).ConfigureAwait(false)) aim = FindAim();
            if (aim is null)
                return ActionResult.Fail(face is { } f2
                    ? $"can't place at {Fmt(pos)} on the {f2.ToString().ToLowerInvariant()} face: not visible/in reach (or I'm in the way)"
                    : $"can't place at {Fmt(pos)}: no solid neighbour face in reach (or I'm in the way)");
        }

        if (await SelectItemAsync(itemId, damage, ct).ConfigureAwait(false) < 0) return ActionResult.Fail($"couldn't hold {ItemName(itemId)}");
        var a = aim.Value;
        await LookCoreAsync(a.Yaw, a.Pitch, ct).ConfigureAwait(false);
        // Vanilla World.mayPlace refuses a block whose box holds an entity: a pillar jump that fell back while the slot
        // switch/look ticked would otherwise be sent (and on a lenient server embed us in it).
        if (AABB.ForPlayer(World.Player.Position).Intersects(AABB.Block(pos.X, pos.Y, pos.Z)) || FallingIntoCell(pos))
            return ActionResult.Fail(InTheWay + Fmt(pos));
        Send(new PlayerBlockPlacementPacket
        {
            X = a.Against.X, Y = a.Against.Y, Z = a.Against.Z, Direction = (byte)a.Face, HeldItem = HeldStack(),
        });
        Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
        if (!await WaitUntilAsync(() => !Aiming.IsReplaceable(World, World.GetBlock(pos)), TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false))
        {
            NoteEditReverted(pos, "placement");
            return ActionResult.Fail($"{ItemName(itemId)} didn't appear at {Fmt(pos)}" + SpawnProtectionHint(pos));
        }
        NoteEditOk(pos);
        if (itemId == Blocks.Sapling) NoteSaplingPlanted();
        return ActionResult.Success($"placed {ItemName(itemId)} at {Fmt(pos)}");
    }

    /// <summary>
    /// Vanilla edge bridging for one placement: walks onto a solid block beside <paramref name="pos"/> (its outer face is
    /// the support) and sneaks out toward the cell until that face is in view. Sneaking plus the EdgeGuard keep the body on
    /// the block (never over the void). True when a support face became visible.
    /// </summary>
    private async Task<bool> LeanForPlaceAsync(BlockPos pos, CancellationToken ct)
    {
        if (BlockShapes.HasCollision(World, pos.X, pos.Y + 1, pos.Z) || BlockShapes.HasCollision(World, pos.X, pos.Y + 2, pos.Z)) return false;
        var me = World.Player.Position;
        var stances = new[] { new BlockPos(1, 0, 0), new BlockPos(-1, 0, 0), new BlockPos(0, 0, 1), new BlockPos(0, 0, -1) }
            .Select(o => new BlockPos(pos.X + o.X, pos.Y, pos.Z + o.Z))
            .Where(s => BlockShapes.HasCollision(World, s.X, s.Y, s.Z) && !BlockShapes.IsLiquid(World.GetBlock(s)) && !Blocks.Get(World.GetBlock(s)).Interactable
                        && !BlockShapes.HasCollision(World, s.X, s.Y + 1, s.Z) && !BlockShapes.HasCollision(World, s.X, s.Y + 2, s.Z))
            .Select(s => new BlockPos(s.X, s.Y + 1, s.Z))
            .OrderBy(s => Math.Pow(s.X + 0.5 - me.X, 2) + Math.Pow(s.Z + 0.5 - me.Z, 2));
        foreach (var stance in stances)
        {
            if (!new GoalBlock(stance).IsReached(Pathfinder.StartCellFor(World, World.Player.Position) ?? World.Player.Position.ToBlockPos()))
            {
                var g = await GotoCoreAsync(new GoalBlock(stance), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(20), AllowPartial = false }, ct)
                    .ConfigureAwait(false);
                if (!g.Ok) continue;
            }
            var toward = new Vec3d(pos.X + 0.5, stance.Y, pos.Z + 0.5);
            bool seen = false;
            int ticks = 0;
            await DriveAsync(phys =>
            {
                ticks++;
                seen = phys.OnGround && Aiming.FindPlaceAim(World, phys.EyePosition, pos, Options.Reach, phys.BoundingBox) is not null;
                float yaw = PlayerPhysics.YawPitchTo(phys.Position, toward).Yaw;
                return seen || ticks > 40
                    ? new DriverStep(new MoveInput(Sneak: true, Yaw: yaw, Pitch: 75), Done: true)
                    : new DriverStep(new MoveInput(Forward: 1, Sneak: true, Yaw: yaw, Pitch: 75));
            }, ct).ConfigureAwait(false);
            if (seen) return true;
        }
        return false;
    }

    /// <summary>Path into reach of an interactable block and right-click it.</summary>
    /// <summary>Last block right-clicked by <see cref="ClickBlockAsync"/> (activate / open) and when (ms tick count), so
    /// the next 0x64 can be attributed to it (<see cref="ContainerOpened"/>).</summary>
    private volatile Activation? _lastActivated;
    private sealed record Activation(BlockPos Pos, long At);

    private async Task<ActionResult> ClickBlockAsync(BlockPos pos, CancellationToken ct, bool allowPathing = true)
    {
        int id = World.GetBlock(pos);
        if (id == 0 || !Blocks.Get(id).Interactable)
            return ActionResult.Fail($"{BlockName(id, World.GetMeta(pos))} at {Fmt(pos)} is not something to use");
        var aim = Aiming.FindDigAim(World, World.Player.EyePosition, pos, Options.Reach);
        if (aim is null)
        {
            if (!allowPathing) return ActionResult.Fail($"can't see/reach {BlockName(id, 0)} at {Fmt(pos)} from here");
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

    /// <summary>Owner protection plus tree stewardship (<see cref="TreeGuarded"/>: the island's last trees' logs).</summary>
    private bool IsBlockProtected(BlockPos pos, int id) => (IsProtected?.Invoke(pos, id) ?? false) || (id == Blocks.Log && TreeGuarded(pos));

    private bool NextToWater(BlockPos p)
    {
        foreach (var f in BlockFaceExtensions.All)
            if (Blocks.IsWater(World.GetBlock(p.Offset(f)))) return true;
        return false;
    }

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
            // Lava that flows into a cell touching water hardens straight back into cobble/stone: that is how every
            // cobble generator works, so treat such a cell like a generator cell and only refuse if lava could reach me.
            generator |= NextToWater(pos);
            if (!generator) return $"won't dig {name} at {Fmt(pos)}: lava next to it would flow out";
            // A player taught this stand spot for this cell (demo): trust it over the lava-distance rule.
            if (positioned && LavaPathToMe(pos) && !OnTrustedStand(pos)) return $"won't dig {name} at {Fmt(pos)} from here: released lava could reach me";
        }
        return null;
    }

    /// <summary>
    /// Live 10-27 (B21): a script told to mine the generator's cell dug "the nearest cobblestone" instead: the island floor
    /// next to the gen hole, leaving the bot on the rim ("you're mining the flooor"). A block at or below the level I stand on,
    /// with a walkable cell on top and the void within 3 of it, is a walkway's floor: never dug by a free-standing dig/mine.
    /// A generator cell (lava could flow in, water next to it) is the exception; trees are never floors.
    /// </summary>
    private string? WalkwayFloorVeto(BlockPos p, string name)
    {
        if (p.Y >= FeetCell().Y) return null; // at/above my feet: a wall or a stack, not the floor I walk on
        int id = World.GetBlock(p);
        if (id is Blocks.Log or Blocks.Leaves) return null;
        if (LavaCouldFlowInto(p) && NextToWater(p)) return null; // a cobble generator's cell
        if (!Safety.IsStandable(World, p.X, p.Y + 1, p.Z)) return null;
        int edge = Safety.EdgeDistance(World, p.X, p.Y + 1, p.Z, WalkwayVoidDistance);
        if (edge > WalkwayVoidDistance) return null;
        return $"won't dig {name} at {Fmt(p)}: it's the floor of a walkway with the void {edge} away (dig only the taught cell)";
    }

    /// <summary>Void this close to a walkway's floor block makes it undiggable for free-standing digs (<see cref="WalkwayFloorVeto"/>).</summary>
    public const int WalkwayVoidDistance = 3;

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

    /// <summary>
    /// Blocks the bot may spend on pillaring/bridging, cheapest first: full, non-falling, non-precious cubes. Never dirt
    /// or grass (on a skyblock island every dirt block is a future tree or farm), sand/gravel (they fall), ores, wool,
    /// glass or anything else worth keeping. Logs are allowed (and can be crafted into 4 planks each when short).
    /// </summary>
    public static readonly IReadOnlyList<int> ScaffoldBlocks =
        [Blocks.Cobblestone, Blocks.Netherrack, Blocks.Stone, Blocks.Planks, Blocks.Log, Blocks.Sandstone, Blocks.StoneBricks];

    /// <summary>Scaffold block to use for bridging/pillaring: the first of <see cref="ScaffoldBlocks"/> held (never reserved items).</summary>
    private int? ScaffoldItem()
    {
        foreach (int id in ScaffoldBlocks)
            if (!ReservedItems.Contains(id) && World.Inventory.CountItem(id) > 0) return id;
        return null;
    }

    /// <summary>All scaffold blocks held (the placer moves on to the next kind when one runs out).</summary>
    private int ScaffoldCount() => ScaffoldBlocks.Where(id => !ReservedItems.Contains(id)).Sum(id => World.Inventory.CountItem(id));

    /// <summary>Scaffold kinds held now, for "used 5 planks, 2 log" after a build-mode goto.</summary>
    private Dictionary<int, int> ScaffoldSnapshot() =>
        ScaffoldBlocks.ToDictionary(id => id, id => World.Inventory.CountItem(id));

    private static string ScaffoldUsed(Dictionary<int, int> before, Dictionary<int, int> after)
    {
        var used = before.Where(kv => kv.Value > after.GetValueOrDefault(kv.Key)).Select(kv => $"{kv.Value - after.GetValueOrDefault(kv.Key)} {BlockName(kv.Key, 0)}")
            .ToList();
        return used.Count == 0 ? "" : $"; built with {string.Join(", ", used)}";
    }

    private const string InTheWay = "I'm in the way of ";

    /// <summary>
    /// Pillar placement under my own feet while airborne: the packet takes a few ticks to reach the server, so a placement
    /// sent while already falling back lands the block inside my body (a lenient server accepts it; vanilla's mayPlace
    /// refuses it). Only send it rising, or with enough height left above the cell that the fall can't reach it first.
    /// </summary>
    private bool FallingIntoCell(BlockPos pos)
    {
        var p = World.Player.Position;
        if (World.Player.OnGround || (int)Math.Floor(p.X) != pos.X || (int)Math.Floor(p.Z) != pos.Z || p.Y < pos.Y + 1 - 1e-6 || p.Y > pos.Y + 2) return false;
        double vy = World.Player.Physics.Velocity.Y;
        return vy <= 0.05 && p.Y < pos.Y + 1.15;
    }

    private static string BlockName(int id, int meta) => Blocks.Get(id).Name;
    private static string ItemName(int id, int damage = 0) => Items.DisplayName(id, damage).ToLowerInvariant();
}
