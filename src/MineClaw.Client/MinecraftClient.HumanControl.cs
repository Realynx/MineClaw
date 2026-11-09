using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    public const int HumanInputTimeoutMs = 350;
    private sealed record HumanKeys(MoveInput Input, int? Hotbar, long At);
    private sealed class HumanControl(float yaw, float pitch)
    {
        public readonly CancellationTokenSource Cancel = new();
        public HumanKeys Keys = new(MoveInput.Idle(yaw, pitch), null, Environment.TickCount64);
        public volatile bool UsingItem;
        public bool Started;
        public RaycastHit? Dig;
    }
    private readonly object _humanSync = new();
    private readonly AsyncLocal<HumanControl?> _humanActionContext = new();
    private HumanControl? _human;
    public bool HumanControlled => Volatile.Read(ref _human) is not null;

    /// <summary>Owns the body immediately. AI actions wind down under their existing action lock before a human interaction starts.</summary>
    public void BeginHumanControl()
    {
        lock (_humanSync)
        {
            if (_human is not null) return;
            if (!IsPlaying) throw new InvalidOperationException("The pawn is not in game.");
            var p = World.Player.Snapshot;
            Volatile.Write(ref _human, new HumanControl(p.Yaw, p.Pitch));
            AbortCurrentAction("human took control");
            Interlocked.Exchange(ref _driver, null)?.Tcs.TrySetCanceled();
            CancelGesture();
            _gNow = null;
            _gOnWire = false;
            _combatInstance?.RequestStop("human took control");
            if (_combatBlocking) { ReleaseHumanUse(); _combatBlocking = false; }
            _idleSneak = _crouchHold = false;
        }
    }

    /// <summary>Stops keys and interaction packets before yielding ownership. Old action cleanup cannot touch a new takeover.</summary>
    public void EndHumanControl()
    {
        lock (_humanSync)
        {
            var control = _human;
            if (control is null) return;
            control.Cancel.Cancel();
            StopHumanInteraction(control);
            var p = World.Player.Snapshot;
            _lookYaw = p.Yaw; _lookPitch = p.Pitch;
            _combatInstance?.RequestStop("human released control");
            Volatile.Write(ref _human, null);
        }
    }

    public void SetHumanInput(MoveInput input, int? hotbar = null)
    {
        if (!float.IsFinite(input.Forward) || !float.IsFinite(input.Strafe) || !float.IsFinite(input.Yaw) || !float.IsFinite(input.Pitch))
            throw new ArgumentException("Control input must be finite.");
        if (hotbar is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(hotbar));
        lock (_humanSync)
        {
            if (_human is not { } control) throw new InvalidOperationException("The pawn is not under human control.");
            var keys = input with { Forward = Math.Clamp(input.Forward, -1, 1), Strafe = Math.Clamp(input.Strafe, -1, 1),
                Yaw = input.Yaw % 360, Pitch = Math.Clamp(input.Pitch, -90, 90), UsingItem = false, Hungry = false };
            Volatile.Write(ref control.Keys, new HumanKeys(keys, hotbar, Environment.TickCount64));
        }
    }

    private void ResetHumanInputAfterTeleport(float yaw, float pitch)
    {
        lock (_humanSync)
        {
            if (_human is not { } control) return;
            Volatile.Write(ref control.Keys, new HumanKeys(MoveInput.Idle(yaw, pitch), null, Environment.TickCount64));
            StopHumanInteraction(control);
        }
    }

    internal static MoveInput HumanInputAt(MoveInput input, long at, long now) => now - at >= HumanInputTimeoutMs
        ? MoveInput.Idle(input.Yaw, input.Pitch) : input;

    /// <summary>Vanilla movement, collisions, effects and gravity; no autonomous aiming, retreat, building or gear changes.</summary>
    private void HumanTick(Session session, PhysicsState phys, HumanControl control, bool teleported)
    {
        if (!control.Started)
        {
            control.Started = true;
            _flight = null;
            _waterHold = null;
            _retreatTarget = _healRetreat = null;
            _surfacing = null;
            _voidHoldAt = null;
            _safetyFreeze = false;
            _guardInstance?.Reset();
        }
        _gNow = null;
        var keys = Volatile.Read(ref control.Keys);
        var input = HumanInputAt(keys.Input, keys.At, Environment.TickCount64);
        if (Environment.TickCount64 - keys.At >= HumanInputTimeoutMs) StopHumanInteraction(control);
        else if (keys.Hotbar is { } slot) SelectHotbarFromTick(session, slot);
        phys.Frozen = false;
        var player = World.Player;
        phys.SpeedAmplifier = player.EffectAmplifier(PotionEffects.Speed);
        phys.SlownessAmplifier = player.EffectAmplifier(PotionEffects.Slowness);
        phys.JumpBoostAmplifier = player.EffectAmplifier(PotionEffects.JumpBoost);
        var fall = phys.FallDistance;
        Options.Profile.Physics.Tick(World, phys, input with { Hungry = !CanSprintNow(), UsingItem = control.UsingItem });
        _lookYaw = phys.Yaw; _lookPitch = phys.Pitch;
        player.SyncFromPhysics();
        BreathTick(phys, fall);
        if (!teleported) SendMovement(session, phys);
        EndTick();
    }

    public async Task<ActionResult> HumanActionAsync(string kind, BlockPos? target = null, int? entityId = null,
        int? face = null, int? windowId = null, int? slot = null, int button = 0, bool shift = false, bool wholeStack = false,
        CancellationToken ct = default, CancellationToken holdRelease = default)
    {
        var control = Volatile.Read(ref _human);
        if (control is null) return ActionResult.Fail("The pawn is not under human control.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, control.Cancel.Token, SessionToken);
        bool locked = false;
        try
        {
            await _actionLock.WaitAsync(cancel.Token).ConfigureAwait(false);
            locked = true;
            if (!ReferenceEquals(control, Volatile.Read(ref _human)) || !IsPlaying) return ActionResult.Fail("Control ended.");
            CurrentAction = "human:" + kind;
            _humanActionContext.Value = control;
            using var data = MineClaw.Data.Versioning.GameData.Override(Options.Profile.Data);
            cancel.Token.ThrowIfCancellationRequested();
            if (kind == "inventory_click")
            {
                var w = World.Inventory.ActiveWindow;
                if (windowId != w.Id || slot is null || (slot != -999 && (slot < 0 || slot >= w.SlotCount)) || button is < 0 or > 1)
                    return ActionResult.Fail("The inventory changed or the slot is invalid.");
                return await ClickCoreAsync(slot.Value, button == 1, shift, cancel.Token).ConfigureAwait(false)
                    ? ActionResult.Success("clicked slot") : ActionResult.Fail("Inventory resynced; try again.");
            }
            if (kind == "close_inventory")
            {
                var w = World.Inventory.ActiveWindow;
                if (windowId is { } expected && expected != w.Id) return ActionResult.Fail("The inventory changed.");
                // Preserve cursor/grid items when possible; never invent items or silently delete a held stack.
                await EnsureNoWindowAsync(cancel.Token).ConfigureAwait(false);
                return ActionResult.Success("closed inventory");
            }
            if (World.Inventory.OpenWindow is not null) return ActionResult.Fail("Close the inventory first.");
            var keys = Volatile.Read(ref control.Keys);
            if (Environment.TickCount64 - keys.At >= HumanInputTimeoutMs) return ActionResult.Fail("Control input timed out.");
            PrepareHumanClick(control, keys);
            if (kind == "drop")
            {
                if (HeldStack().IsEmpty) return ActionResult.Success("empty hand");
                int count = wholeStack && Options.Profile.Version.Id == "b1.8.1" ? HeldStack().Count : 1;
                byte status = wholeStack && Options.Profile.Version.Id != "b1.8.1" ? PlayerDiggingPacket.StatusDropStack : PlayerDiggingPacket.StatusDropItem;
                for (int i = 0; i < count; i++) { cancel.Token.ThrowIfCancellationRequested(); Send(new PlayerDiggingPacket { Status = status }); }
                return ActionResult.Success("dropped item");
            }
            var hit = HumanBlockHit(control);
            var entity = HumanEntityHit(control, hit?.Distance ?? Options.Reach);
            if (entityId is { } expectedEntity && entity?.Id != expectedEntity) return ActionResult.Fail("The entity is not under the crosshair within reach.");
            if (target is { } expectedTarget && hit?.Pos != expectedTarget) return ActionResult.Fail("The block is not under the crosshair within reach.");
            if (face is { } expectedFace && (expectedFace is < 0 or > 5 || (int?)hit?.Face != expectedFace)) return ActionResult.Fail("The block face changed.");
            if (kind == "attack" && entity is not null)
            {
                SendMeleeHit(_session!, entity.Id);
                return ActionResult.Success("attacked " + entity.DisplayName);
            }
            if (kind is "attack" or "dig")
            {
                if (hit is null) { Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 }); return ActionResult.Success("swung hand"); }
                using var held = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, holdRelease);
                return await HumanDigAsync(control, hit.Value, held.Token).ConfigureAwait(false);
            }
            if (kind is "use" or "place") return await HumanUseAsync(control, entity, hit, cancel.Token, holdRelease).ConfigureAwait(false);
            return ActionResult.Fail("Unknown human action.");
        }
        catch (OperationCanceledException) { return ActionResult.Fail("action cancelled"); }
        catch (ActionAbortedException e) { return ActionResult.Fail(e.Message); }
        finally
        {
            if (locked)
            {
                lock (_humanSync) if (ReferenceEquals(control, _human)) StopHumanInteraction(control);
                _humanActionContext.Value = null;
                CurrentAction = null;
                _actionLock.Release();
            }
        }
    }

    private RaycastHit? HumanBlockHit(HumanControl control)
    {
        var input = Volatile.Read(ref control.Keys).Input;
        return Raycast.Cast(World, World.Player.EyePosition, input.Yaw, input.Pitch, Math.Min(4.5, Options.Reach));
    }

    private void PrepareHumanClick(HumanControl control, HumanKeys keys)
    {
        lock (_humanSync)
        {
            if (!ReferenceEquals(control, _human)) throw new OperationCanceledException("Human control ended.");
            if (keys.Hotbar is { } hotbar) SelectHotbar(hotbar);
            // Send both look and sneak before the click, even if the next physics tick has not run yet. Sneaking on a
            // chest places against its face; an un-sneaked click opens it. The exact hit cursor determines stair/slab half.
            Send(new PlayerLookPacket { Yaw = keys.Input.Yaw, Pitch = keys.Input.Pitch, OnGround = World.Player.OnGround });
            lock (_sneakWireSync)
            {
                if (_lastSneak == keys.Input.Sneak) return;
                Send(new EntityActionPacket { EntityId = World.Player.EntityId, Action = (sbyte)(keys.Input.Sneak ? 1 : 2) });
                _lastSneak = keys.Input.Sneak;
            }
        }
    }

    private Entity? HumanEntityHit(HumanControl control, double blockDistance)
    {
        var input = Volatile.Read(ref control.Keys).Input;
        return HumanPickEntity(World, World.Player.EyePosition, input.Yaw, input.Pitch, Math.Min(3, blockDistance));
    }

    internal static Entity? HumanPickEntity(WorldState world, Vec3d eye, float yaw, float pitch, double range)
    {
        var direction = PlayerPhysics.LookVector(yaw, pitch);
        Entity? best = null;
        double distance = Math.Min(range, Raycast.Cast(world, eye, direction, range)?.Distance ?? range);
        foreach (var entity in world.Entities.Nearby(eye, range + 4))
        {
            if (entity.Id == world.Player.EntityId || entity.Kind is EntityKind.Item or EntityKind.XpOrb or EntityKind.Weather || entity.Health <= 0) continue;
            if (CombatRules.Hitbox(entity).Grow(0.1, 0.1, 0.1).RayIntersect(eye, direction, distance) is not { } hit || hit.T >= distance) continue;
            best = entity; distance = hit.T;
        }
        return best;
    }

    private async Task<ActionResult> HumanDigAsync(HumanControl control, RaycastHit hit, CancellationToken ct)
    {
        var pos = hit.Pos;
        int id = hit.BlockId;
        if (Options.Profile.Data.Block(id).Hardness < 0) return ActionResult.Fail("That block is unbreakable.");
        int ticks = DigTicksHere(pos, id);
        if (ticks == int.MaxValue) return ActionResult.Fail("That block cannot be dug.");
        var held = HeldStack();
        control.Dig = hit;
        Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
        Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusStarted, X = pos.X, Y = pos.Y, Z = pos.Z, Face = (byte)hit.Face });
        double progress = 0;
        for (int count = 0; progress < 1; count++)
        {
            if (ticks > 0) await WaitTicksAsync(1, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (Broke(pos, id)) { control.Dig = null; return ActionResult.Success("dug block"); }
            var keys = Volatile.Read(ref control.Keys);
            if (!ReferenceEquals(control, Volatile.Read(ref _human)) || Environment.TickCount64 - keys.At >= HumanInputTimeoutMs
                || control.Dig is null || HumanBlockHit(control)?.Pos != pos || HeldStack().Id != held.Id || HeldStack().Damage != held.Damage)
                return ActionResult.Fail("Dig stopped because the aim, held item or control input changed.");
            int nowTicks = DigTicksHere(pos, id);
            if (nowTicks == int.MaxValue) return ActionResult.Fail("That block cannot be dug.");
            progress += 1d / Math.Max(1, nowTicks);
            if (count % 4 == 0) Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
        }
        ct.ThrowIfCancellationRequested();
        Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusFinished, X = pos.X, Y = pos.Y, Z = pos.Z, Face = (byte)hit.Face });
        control.Dig = null;
        return await WaitUntilAsync(() => Broke(pos, id), TimeSpan.FromSeconds(2), ct).ConfigureAwait(false)
            ? ActionResult.Success("dug block") : ActionResult.Fail("The server did not break that block.");
    }

    private async Task<ActionResult> HumanUseAsync(HumanControl control, Entity? entity, RaycastHit? hit, CancellationToken ct, CancellationToken holdRelease)
    {
        if (entity is not null)
        {
            Send(new UseEntityPacket { UserEntityId = World.Player.EntityId, TargetEntityId = entity.Id, LeftClick = false });
            return ActionResult.Success("used entity");
        }
        if (hit is { } block)
        {
            var pos = block.Pos;
            _lastActivated = new Activation(pos, Environment.TickCount64, World.Dimension);
            var before = Volatile.Read(ref _windowsOpened);
            var itemsBefore = Volatile.Read(ref _windowItemsVersion);
            Send(new PlayerBlockPlacementPacket { X = pos.X, Y = pos.Y, Z = pos.Z, Direction = (byte)block.Face, HeldItem = HeldStack(),
                Cursor = ((float)(block.Point.X - pos.X), (float)(block.Point.Y - pos.Y), (float)(block.Point.Z - pos.Z)) });
            Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
            if (OpensWindow(block.BlockId) && !Volatile.Read(ref control.Keys).Input.Sneak)
            {
                if (await WaitUntilAsync(() => Volatile.Read(ref _windowsOpened) > before, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
                {
                    await WaitUntilAsync(() => Volatile.Read(ref _windowItemsVersion) > itemsBefore, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    _lastContainerPos = pos;
                    return ActionResult.Success("opened inventory");
                }
                return ActionResult.Fail("The server did not open that block.");
            }
            // Vanilla falls through to item-use for a non-interactable target (food, bow, bucket etc.).
            if (Options.Profile.Data.Block(block.BlockId).Interactable && !Volatile.Read(ref control.Keys).Input.Sneak) return ActionResult.Success("used block");
            if (!UsesRightClickAction(HeldStack().Id)) return ActionResult.Success("used block");
        }
        var held = HeldStack();
        if (held.IsEmpty) return ActionResult.Success("used hand");
        // Food, bows, potions and defensive items keep use held until released. Throwables and placeable items are one click.
        bool continuous = held.Id is 261 or 373 or 442 || Options.Profile.Data.Food(held.Id, held.Damage) is not null
            || (Options.Profile.Version.Id == "b1.8.1" && Items.Get(held.Id).Name.EndsWith("_sword", StringComparison.Ordinal));
        control.UsingItem = continuous;
        Send(PlayerBlockPlacementPacket.UseItem(held));
        if (!continuous) return ActionResult.Success("used held item");
        using var released = CancellationTokenSource.CreateLinkedTokenSource(ct, holdRelease);
        while (control.UsingItem && !released.IsCancellationRequested && ReferenceEquals(control, Volatile.Read(ref _human))
            && Environment.TickCount64 - Volatile.Read(ref control.Keys).At < HumanInputTimeoutMs)
            await WaitTicksAsync(1, released.Token).ConfigureAwait(false);
        return ActionResult.Success("released held item");
    }

    private void StopHumanInteraction(HumanControl control)
    {
        if (control.UsingItem) { control.UsingItem = false; ReleaseHumanUse(); }
        if (control.Dig is not { } dig) return;
        control.Dig = null;
        var packet = new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusCancelled, X = dig.Pos.X, Y = dig.Pos.Y, Z = dig.Pos.Z, Face = (byte)dig.Face };
        if (_session is { Ended: false } s && s.Conn.CanSend(packet)) Send(s, packet);
    }

    private void ReleaseHumanUse()
    {
        if (_session is { Ended: false } s) Send(s, new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusReleaseUseItem, Face = 255 });
    }
}
