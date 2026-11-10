using System.Diagnostics;
using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>Click-time orientation and hand, tied to one takeover. Movement keys remain live.</summary>
public sealed class HumanControlAim
{
    internal HumanControlAim(object owner, MoveInput input, int? hotbar) => (Owner, Input, Hotbar) = (owner, input, hotbar);
    internal object Owner { get; }
    internal MoveInput Input { get; }
    internal int? Hotbar { get; }
}

/// <summary>The block a takeover is digging. Never changed, only replaced whole: the action writes it and the tick thread reads it.
/// <paramref name="Progress"/> runs from 0 to 1. <paramref name="FinishSent"/>: the finish packet is out and only the game server's
/// answer is awaited, so nothing is left to cancel.</summary>
public sealed record HumanControlDig(BlockPos Pos, BlockFace Face, double Progress, int Id, int Meta, bool FinishSent = false);

/// <summary>Blocks one takeover has broken: a count that only grows, and the newest block as it was.</summary>
public sealed record HumanControlBroke(long Count, BlockPos Pos, int Id, int Meta);

/// <summary>What one tick under human control left behind, so that everything a controller is told belongs to the same tick.</summary>
/// <param name="Tick">The client's own <see cref="MinecraftClient.TickCount"/> as this tick left it. Ticks that do not move the
/// body by the player's keys (riding) leave no sample but still count.</param>
/// <param name="AckSeq">Sequence number of the input this tick used; negative when the keys are the pawn's own (no input yet, or
/// reset by a server teleport).</param>
/// <param name="AckWaitMs">How long that input had waited when the tick began.</param>
/// <param name="Velocity">Blocks per tick, after the tick.</param>
/// <param name="Stopped">The tick did not follow the controller's keys: its input had timed out, or the controller stopped the pawn.</param>
/// <param name="Teleports">Server teleports received on this connection.</param>
/// <param name="Impulses">Server velocities (knockback) applied so far.</param>
/// <param name="Aim">The block this tick's look hits within <see cref="MinecraftClient.HumanReach"/>, as a click would find it.</param>
public sealed record HumanControlSample(long Tick, long AckSeq, double AckWaitMs, Vec3d Position, Vec3d Velocity, float Yaw, float Pitch,
    bool OnGround, bool UsingItem, bool Stopped, int Teleports, long Impulses, int SpeedAmplifier, int SlownessAmplifier,
    int JumpBoostAmplifier, HumanControlDig? Dig, RaycastHit? Aim, HumanControlBroke? Broke);

public sealed partial class MinecraftClient
{
    public const int HumanInputTimeoutMs = 350;
    /// <summary>How a human action can end, as the message of its result. Whoever relays a takeover maps these to codes for the
    /// browser, so each text is written once.</summary>
    public const string HumanDugMessage = "dug block",
        HumanDigStoppedMessage = "Dig stopped because the aim, held item or control input changed.",
        HumanActionCancelledMessage = "action cancelled";
    /// <summary>A held attack's repeat while a mob is under the crosshair: nothing was sent.</summary>
    public const string HumanAttackHeldMessage = "attack held";
    /// <summary>A right click with a block in hand that the game server answered by filling the placement cell. Without that answer
    /// (refused: the body or a mob stands there, or no answer in time) the click still ends as "used block".</summary>
    public const string HumanPlacedMessage = "placed block";
    /// <summary>How long a block placement waits for the game server to fill the cell before it reports the click unconfirmed.</summary>
    internal static readonly TimeSpan HumanPlaceConfirm = TimeSpan.FromMilliseconds(300);

    /// <param name="Seq">The controller's sequence number of this input; negative = the pawn's own keys.</param>
    /// <param name="Stamp">When it arrived (<see cref="Stopwatch.GetTimestamp"/>): a tick measures the input's wait from here.</param>
    /// <param name="Stopped">The controller released the keys itself (<see cref="StopHumanInput"/>).</param>
    private sealed record HumanKeys(MoveInput Input, int? Hotbar, long At, long Seq, long Stamp, bool Stopped = false)
    {
        public static HumanKeys Own(float yaw, float pitch) => new(MoveInput.Idle(yaw, pitch), null, Environment.TickCount64, -1, Stopwatch.GetTimestamp());
    }
    private sealed class HumanControl(float yaw, float pitch)
    {
        public readonly CancellationTokenSource Cancel = new();
        public HumanKeys Keys = HumanKeys.Own(yaw, pitch);
        public volatile bool UsingItem;
        public bool Started;
        public HumanControlDig? Dig;
        public HumanControlBroke? Broke;
        public HumanControlSample? Sample;
    }
    private readonly object _humanSync = new();
    private readonly AsyncLocal<HumanControl?> _humanActionContext = new();
    private HumanControl? _human;
    public bool HumanControlled => Volatile.Read(ref _human) is not null;

    public HumanControlAim? CaptureHumanAim()
    {
        if (Volatile.Read(ref _human) is not { } control) return null;
        var keys = Volatile.Read(ref control.Keys);
        return new HumanControlAim(control, keys.Input, keys.Hotbar ?? World.Player.HeldSlot);
    }

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

    /// <param name="seq">The controller's sequence number of this input: the tick that uses it reports it back
    /// (<see cref="HumanControlSample.AckSeq"/>).</param>
    public void SetHumanInput(MoveInput input, int? hotbar = null, long seq = -1)
    {
        if (!float.IsFinite(input.Forward) || !float.IsFinite(input.Strafe) || !float.IsFinite(input.Yaw) || !float.IsFinite(input.Pitch))
            throw new ArgumentException("Control input must be finite.");
        if (hotbar is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(hotbar));
        lock (_humanSync)
        {
            if (_human is not { } control) throw new InvalidOperationException("The pawn is not under human control.");
            var keys = input with { Forward = Math.Clamp(input.Forward, -1, 1), Strafe = Math.Clamp(input.Strafe, -1, 1),
                Yaw = input.Yaw % 360, Pitch = Math.Clamp(input.Pitch, -90, 90), UsingItem = false, Hungry = false };
            Volatile.Write(ref control.Keys, new HumanKeys(keys, hotbar, Environment.TickCount64, seq, Stopwatch.GetTimestamp()));
        }
    }

    /// <summary>The controller halts the pawn itself (its player went quiet, or its connection was replaced): every key up, the look
    /// and the last input's sequence kept. Ticks report <see cref="HumanControlSample.Stopped"/> until a fresh input arrives.</summary>
    public void StopHumanInput()
    {
        lock (_humanSync)
        {
            if (_human is not { } control) throw new InvalidOperationException("The pawn is not under human control.");
            var keys = control.Keys;
            Volatile.Write(ref control.Keys, new HumanKeys(MoveInput.Idle(keys.Input.Yaw, keys.Input.Pitch), null, Environment.TickCount64,
                keys.Seq, Stopwatch.GetTimestamp(), Stopped: true));
        }
    }

    private void ResetHumanInputAfterTeleport(float yaw, float pitch)
    {
        lock (_humanSync)
        {
            if (_human is not { } control) return;
            Volatile.Write(ref control.Keys, HumanKeys.Own(yaw, pitch));
            StopHumanInteraction(control);
        }
    }

    /// <summary>How far a human click reaches for a block.</summary>
    public double HumanReach => Math.Min(4.5, Options.Reach);

    /// <summary>The armour points the HUD shows for the worn pieces (player slots 5..8): b1.8.1 scales its points by what is left of
    /// each piece's durability (<see cref="CombatRules.ArmorValue"/>); 1.12.2 adds up each material's points.</summary>
    public int HumanArmor()
    {
        var inventory = World.Inventory;
        ItemStack head = inventory.GetPlayerSlot(5), chest = inventory.GetPlayerSlot(6), legs = inventory.GetPlayerSlot(7), feet = inventory.GetPlayerSlot(8);
        if (Options.Profile.Version.Id == "b1.8.1") return CombatRules.ArmorValue([head, chest, legs, feet]);
        return Release1122CombatRules.ArmorOf(head.Id).Points + Release1122CombatRules.ArmorOf(chest.Id).Points
            + Release1122CombatRules.ArmorOf(legs.Id).Points + Release1122CombatRules.ArmorOf(feet.Id).Points;
    }

    /// <summary>The newest tick under human control, for as long as it is the body's latest tick. Null when the body is not under
    /// human control, has not ticked under it yet, or has ticked since without following the player's keys (riding: the server
    /// drives it). A caller then reports the live position as stopped instead of repeating an old sample.</summary>
    public HumanControlSample? HumanSample() =>
        Volatile.Read(ref _human) is { } control && Volatile.Read(ref control.Sample) is { } sample && sample.Tick >= TickCount ? sample : null;

    /// <summary>Waits for the sample of a tick later than <paramref name="afterTick"/>. Null when none came within
    /// <paramref name="max"/> (riding, control ended).</summary>
    public async Task<HumanControlSample?> WaitHumanSampleAsync(long afterTick, TimeSpan max, CancellationToken ct = default)
    {
        HumanControlSample? sample = null;
        bool Newer() => (sample = HumanSample()) is { } s && s.Tick > afterTick;
        return await _changed.WaitUntilAsync(Newer, max, ct).ConfigureAwait(false) ? sample : null;
    }

    internal static MoveInput HumanInputAt(MoveInput input, long at, long now) => now - at >= HumanInputTimeoutMs
        ? MoveInput.Idle(input.Yaw, input.Pitch) : input;

    /// <summary>Vanilla movement, collisions, effects and gravity; no autonomous aiming, retreat, building or gear changes.</summary>
    private void HumanTick(Session session, PhysicsState phys, HumanControl control, bool teleported)
    {
        long tickStamp = Stopwatch.GetTimestamp();
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
        long now = Environment.TickCount64;
        bool timedOut = now - keys.At >= HumanInputTimeoutMs;
        var input = HumanInputAt(keys.Input, keys.At, now);
        if (timedOut) StopHumanInteraction(control);
        else if (keys.Hotbar is { } slot) SelectHotbarFromTick(session, slot);
        phys.Frozen = false;
        var player = World.Player;
        phys.SpeedAmplifier = player.EffectAmplifier(PotionEffects.Speed);
        phys.SlownessAmplifier = player.EffectAmplifier(PotionEffects.Slowness);
        phys.JumpBoostAmplifier = player.EffectAmplifier(PotionEffects.JumpBoost);
        var fall = phys.FallDistance;
        bool usingItem = control.UsingItem;
        Options.Profile.Physics.Tick(World, phys, input with { Hungry = !CanSprintNow(), UsingItem = usingItem });
        _lookYaw = phys.Yaw; _lookPitch = phys.Pitch;
        player.SyncFromPhysics();
        BreathTick(phys, fall);
        if (!teleported) SendMovement(session, phys);
        // Published before EndTick wakes whoever waits for it, and numbered as the counter stands once EndTick has counted this tick.
        Volatile.Write(ref control.Sample, new HumanControlSample(TickCount + 1, keys.Seq,
            Math.Max(0, Stopwatch.GetElapsedTime(keys.Stamp, tickStamp).TotalMilliseconds), phys.Position, phys.Velocity, phys.Yaw, phys.Pitch,
            phys.OnGround, usingItem, timedOut || keys.Stopped, session.TeleportsReceived, KnockbacksReceived, phys.SpeedAmplifier,
            phys.SlownessAmplifier, phys.JumpBoostAmplifier, Volatile.Read(ref control.Dig), HumanBlockHit(input), Volatile.Read(ref control.Broke)));
        EndTick();
    }

    /// <param name="repeat">A held button's re-send of its click: it digs on, but never hits a mob a second time (one attack per click,
    /// on both versions).</param>
    public async Task<ActionResult> HumanActionAsync(string kind, BlockPos? target = null, int? entityId = null,
        int? face = null, int? windowId = null, int? slot = null, int button = 0, bool shift = false, bool wholeStack = false,
        CancellationToken ct = default, CancellationToken holdRelease = default, HumanControlAim? aim = null, bool repeat = false)
    {
        var control = Volatile.Read(ref _human);
        if (control is null) return ActionResult.Fail("The pawn is not under human control.");
        var initial = Volatile.Read(ref control.Keys);
        aim ??= new HumanControlAim(control, initial.Input, initial.Hotbar ?? World.Player.HeldSlot);
        if (!ReferenceEquals(aim.Owner, control)) return ActionResult.Fail("Control ended.");
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
            using var held = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, holdRelease);
            Task<ActionResult> interaction;
            // Only the initial click is atomic with the tick: a newer hotbar update cannot change the item between
            // selecting the captured hand and sending the placement packet. Async dig/window waits are outside.
            lock (_humanSync) interaction = StartHumanClick(control, aim, kind, target, entityId, face, wholeStack, cancel.Token, held.Token, holdRelease, repeat);
            return await interaction.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return ActionResult.Fail(HumanActionCancelledMessage); }
        catch (ActionAbortedException e) { return ActionResult.Fail(e.Message); }
        finally
        {
            if (locked)
            {
                lock (_humanSync) if (ReferenceEquals(control, _human)) StopHumanInteraction(control, actionEnded: true);
                _humanActionContext.Value = null;
                CurrentAction = null;
                _actionLock.Release();
            }
        }
    }

    private Task<ActionResult> StartHumanClick(HumanControl control, HumanControlAim aim, string kind,
        BlockPos? target, int? entityId, int? face, bool wholeStack, CancellationToken ct, CancellationToken held, CancellationToken holdRelease,
        bool repeat)
    {
        static Task<ActionResult> Done(ActionResult result) => Task.FromResult(result);
        if (World.Inventory.OpenWindow is not null) return Done(ActionResult.Fail("Close the inventory first."));
        if (Environment.TickCount64 - Volatile.Read(ref control.Keys).At >= HumanInputTimeoutMs) return Done(ActionResult.Fail("Control input timed out."));
        ct.ThrowIfCancellationRequested();
        PrepareHumanClick(control, aim);
        if (kind == "drop")
        {
            if (HeldStack().IsEmpty) return Done(ActionResult.Success("empty hand"));
            int count = wholeStack && Options.Profile.Version.Id == "b1.8.1" ? HeldStack().Count : 1;
            byte status = wholeStack && Options.Profile.Version.Id != "b1.8.1" ? PlayerDiggingPacket.StatusDropStack : PlayerDiggingPacket.StatusDropItem;
            for (int i = 0; i < count; i++) { ct.ThrowIfCancellationRequested(); Send(new PlayerDiggingPacket { Status = status }); }
            return Done(ActionResult.Success("dropped item"));
        }
        var hit = HumanBlockHit(aim.Input);
        var entity = HumanEntityHit(aim.Input, hit?.Distance ?? Options.Reach);
        if (entityId is { } expectedEntity && entity?.Id != expectedEntity) return Done(ActionResult.Fail("The entity is not under the crosshair within reach."));
        if (target is { } expectedTarget && hit?.Pos != expectedTarget) return Done(ActionResult.Fail("The block is not under the crosshair within reach."));
        if (face is { } expectedFace && (expectedFace is < 0 or > 5 || (int?)hit?.Face != expectedFace)) return Done(ActionResult.Fail("The block face changed."));
        if (kind == "attack" && entity is not null)
        {
            // The mob stands in front of any block, so a held button neither hits it again nor digs behind it.
            if (repeat) return Done(ActionResult.Success(HumanAttackHeldMessage));
            SendMeleeHit(_session!, entity.Id);
            return Done(ActionResult.Success("attacked " + entity.DisplayName));
        }
        if (kind is "attack" or "dig")
        {
            if (hit is null) { Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 }); return Done(ActionResult.Success("swung hand")); }
            return HumanDigAsync(control, hit.Value, held, ct);
        }
        if (kind is "use" or "place") return HumanUseAsync(control, entity, hit, aim.Input.Sneak, ct, holdRelease);
        return Done(ActionResult.Fail("Unknown human action."));
    }

    private RaycastHit? HumanBlockHit(HumanControl control) => HumanBlockHit(Volatile.Read(ref control.Keys).Input);
    private RaycastHit? HumanBlockHit(MoveInput input) => Raycast.Cast(World, HumanEye(input), input.Yaw, input.Pitch, HumanReach);

    /// <summary>Where a click's ray starts: the eye a vanilla client of this version aims from. 1.12.2 lowers it by 0.08 while
    /// sneaking (<c>EntityPlayer.getEyeHeight</c>), which is also where the browser draws its crosshair from; b1.8.1 never does.</summary>
    private Vec3d HumanEye(MoveInput input)
    {
        var eye = World.Player.EyePosition;
        return input.Sneak && Options.Profile.Version.Id != "b1.8.1" ? eye with { Y = eye.Y - Aiming.SneakEyeDrop } : eye;
    }

    private void PrepareHumanClick(HumanControl control, HumanControlAim keys)
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

    private Entity? HumanEntityHit(MoveInput input, double blockDistance) =>
        HumanPickEntity(World, HumanEye(input), input.Yaw, input.Pitch, Math.Min(3, blockDistance));

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

    /// <param name="ct">Ends the digging: the action's lifetime, or the button being released.</param>
    /// <param name="answer">The action's lifetime alone. Once the finish packet is out, letting go of the button no longer stops the
    /// wait for the game server's answer, or a block that breaks a moment later would never be counted.</param>
    private async Task<ActionResult> HumanDigAsync(HumanControl control, RaycastHit hit, CancellationToken ct, CancellationToken answer)
    {
        var pos = hit.Pos;
        int id = hit.BlockId;
        if (Options.Profile.Data.Block(id).Hardness < 0) return ActionResult.Fail("That block is unbreakable.");
        int ticks = DigTicksHere(pos, id);
        if (ticks == int.MaxValue) return ActionResult.Fail("That block cannot be dug.");
        var held = HeldStack();
        var dig = new HumanControlDig(pos, hit.Face, 0, id, World.GetMeta(pos));
        Volatile.Write(ref control.Dig, dig);
        Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
        Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusStarted, X = pos.X, Y = pos.Y, Z = pos.Z, Face = (byte)hit.Face });
        double progress = 0;
        for (int count = 0; progress < 1; count++)
        {
            if (ticks > 0) await WaitTicksAsync(1, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (Broke(pos, id)) return HumanDug(control, dig);
            var keys = Volatile.Read(ref control.Keys);
            if (!ReferenceEquals(control, Volatile.Read(ref _human)) || Environment.TickCount64 - keys.At >= HumanInputTimeoutMs
                || HumanBlockHit(control)?.Pos != pos || HeldStack().Id != held.Id || HeldStack().Damage != held.Damage)
                return ActionResult.Fail(HumanDigStoppedMessage);
            int nowTicks = DigTicksHere(pos, id);
            if (nowTicks == int.MaxValue) return ActionResult.Fail("That block cannot be dug.");
            progress += 1d / Math.Max(1, nowTicks);
            // The swap fails when the tick thread stopped this dig meanwhile (the input timed out, the server teleported the pawn).
            var next = dig with { Progress = Math.Min(1, progress) };
            if (!ReferenceEquals(Interlocked.CompareExchange(ref control.Dig, next, dig), dig)) return ActionResult.Fail(HumanDigStoppedMessage);
            dig = next;
            if (count % 4 == 0) Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
        }
        ct.ThrowIfCancellationRequested();
        // Under the tick's lock, so a tick that stops this dig (and sends the cancel) cannot slip in before the finish packet.
        lock (_humanSync)
        {
            if (!ReferenceEquals(Volatile.Read(ref control.Dig), dig)) return ActionResult.Fail(HumanDigStoppedMessage);
            Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusFinished, X = pos.X, Y = pos.Y, Z = pos.Z, Face = (byte)hit.Face });
            // Nothing is left to cancel. A dig that took time stays reported, complete, until this action ends, so its cracks do not
            // vanish before the block does. One that breaks at once (a torch, a flower) never had progress to show.
            Volatile.Write(ref control.Dig, ticks > 0 ? dig with { Progress = 1, FinishSent = true } : null);
        }
        return await WaitUntilAsync(() => Broke(pos, id), TimeSpan.FromSeconds(2), answer).ConfigureAwait(false)
            ? HumanDug(control, dig) : ActionResult.Fail("The server did not break that block.");
    }

    /// <summary>The block broke: one more on the takeover's counter. Counted before the dig record goes, so no sample shows a dig
    /// that ended without its break.</summary>
    private static ActionResult HumanDug(HumanControl control, HumanControlDig dig)
    {
        Volatile.Write(ref control.Broke, new HumanControlBroke((Volatile.Read(ref control.Broke)?.Count ?? 0) + 1, dig.Pos, dig.Id, dig.Meta));
        Interlocked.CompareExchange(ref control.Dig, null, dig);
        return ActionResult.Success(HumanDugMessage);
    }

    private async Task<ActionResult> HumanUseAsync(HumanControl control, Entity? entity, RaycastHit? hit, bool sneak, CancellationToken ct, CancellationToken holdRelease)
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
            // Where a held block would go: into the clicked cell when it is replaceable (tall grass, snow, a vine), else beside it.
            var cell = Aiming.IsReplaceable(World, block.BlockId) || block.BlockId is 32 or 106 || block.Face == BlockFace.None ? pos : pos.Offset(block.Face);
            int cellBefore = World.GetBlock(cell), cellMetaBefore = World.GetMeta(cell), heldBefore = HeldStack().Id;
            Send(new PlayerBlockPlacementPacket { X = pos.X, Y = pos.Y, Z = pos.Z, Direction = (byte)block.Face, HeldItem = HeldStack(),
                Cursor = ((float)(block.Point.X - pos.X), (float)(block.Point.Y - pos.Y), (float)(block.Point.Z - pos.Z)) });
            Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
            if (OpensWindow(block.BlockId) && !sneak)
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
            if (Options.Profile.Data.Block(block.BlockId).Interactable && !sneak) return ActionResult.Success("used block");
            if (!UsesRightClickAction(HeldStack().Id))
            {
                // Sending the packet is not placing the block: the game server refuses a cell the body (or a mob) still stands in.
                // Only its answer lets the browser stand on the block and sound it.
                if (heldBefore is > 0 and < 256 && await WaitUntilAsync(() =>
                        (World.GetBlock(cell) != cellBefore || World.GetMeta(cell) != cellMetaBefore) && !Aiming.IsReplaceable(World, World.GetBlock(cell)),
                        HumanPlaceConfirm, ct).ConfigureAwait(false))
                    return ActionResult.Success(HumanPlacedMessage);
                return ActionResult.Success("used block");
            }
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

    /// <param name="actionEnded">The interaction's own action is over. Until then a dig that only waits for the game server's answer
    /// stays as it is: it has nothing to cancel and is still reported.</param>
    private void StopHumanInteraction(HumanControl control, bool actionEnded = false)
    {
        if (control.UsingItem) { control.UsingItem = false; ReleaseHumanUse(); }
        if (Volatile.Read(ref control.Dig) is not { } reported || (reported.FinishSent && !actionEnded)) return;
        if (Interlocked.Exchange(ref control.Dig, null) is not { FinishSent: false } dig) return;
        var packet = new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusCancelled, X = dig.Pos.X, Y = dig.Pos.Y, Z = dig.Pos.Z, Face = (byte)dig.Face };
        if (_session is { Ended: false } s && s.Conn.CanSend(packet)) Send(s, packet);
    }

    private void ReleaseHumanUse()
    {
        if (_session is { Ended: false } s) Send(s, new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusReleaseUseItem, Face = 255 });
    }
}
