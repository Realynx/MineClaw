using System.Diagnostics;
using MineClaw.Client.Combat;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    /// <summary>One tick of a driver: the input to apply and whether the driver is finished. <paramref name="AllowDrop"/>
    /// marks a planned drop to a lower floor (the edge guard then verifies the landing instead of forcing sneak);
    /// <paramref name="Parkour"/> a committed gap jump (the guard's planned-parkour exemption re-simulates it).
    /// <paramref name="BoostRoom"/>: how far a boosted body may travel this tick (Story Mode's Fast / Faster, <see cref="SpeedBoost"/>);
    /// 0 = no boost (every driver but the path follower).</summary>
    private readonly record struct DriverStep(MoveInput Input, bool Done = false, bool AllowDrop = false, ParkourCommit? Parkour = null, double BoostRoom = 0);

    /// <summary>Per-tick movement logic installed by an action; runs on the tick thread.</summary>
    private sealed class Driver(Func<PhysicsState, DriverStep> step)
    {
        public Func<PhysicsState, DriverStep> Step { get; } = step;
        public TaskCompletionSource Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ChangeSignal _changed = new();
    private volatile Driver? _driver;
    private long _tickCount;

    // Look / idle state (written by actions, read by the tick thread).
    private float _lookYaw, _lookPitch;
    private volatile bool _idleSneak;

    // Movement packet state (tick thread only).
    private double _lastX, _lastY, _lastStance, _lastZ;
    private float _lastYaw, _lastPitch;
    private int _ticksSincePos;
    private bool _lastSprint, _lastSneak;

    // Danger latches (tick thread only).
    private bool _voidAlerted, _lavaAlerted;
    private readonly HashSet<int> _hostileAlerted = new();

    private long _ticksBehind, _maxTickLateMs;

    /// <summary>Ticks that started more than two intervals late (GC pauses, CPU starvation), since the client was created.</summary>
    public long TicksBehind => Interlocked.Read(ref _ticksBehind);

    /// <summary>The worst tick lateness seen (ms); <see cref="ResetMaxTickLate"/> starts a new window.</summary>
    public long MaxTickLateMs => Interlocked.Read(ref _maxTickLateMs);

    /// <summary>Reads and clears the worst tick lateness (a per-minute "tick behind" log line).</summary>
    public long ResetMaxTickLate() => Interlocked.Exchange(ref _maxTickLateMs, 0);

    private void TickLoop(Session s)
    {
        var sw = Stopwatch.StartNew();
        var interval = Options.TickInterval;
        var next = TimeSpan.Zero;
        while (!s.Cts.IsCancellationRequested && !s.Ended)
        {
            next += interval;
            try
            {
                TickOnce(s);
            }
            catch (Exception ex)
            {
                Log($"tick error: {ex}");
            }
            var wait = next - sw.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            else
            {
                // One process, many bodies: a gen2 pause or a CPU burst stalls every body's tick at once; count it per body.
                if (wait < -interval * 2)
                {
                    Interlocked.Increment(ref _ticksBehind);
                    long late = (long)(-wait).TotalMilliseconds;
                    if (late > Interlocked.Read(ref _maxTickLateMs)) Interlocked.Exchange(ref _maxTickLateMs, late);
                }
                if (wait < -interval * 10) next = sw.Elapsed; // badly behind (debugger, GC): don't burst-catch-up
            }
        }
    }

    private void TickOnce(Session s)
    {
        var player = World.Player;
        var phys = player.Physics;
        bool teleported = ApplyServerTeleports(s);
        if (TickHeldBack(s, phys, ref teleported))
        {
            EndTick();
            return;
        }

        // ---- input from the active driver, or idle ----
        GestureTick(s, phys); // brainstem gesture frame: look overlay (sent only), idle sneak/jump, arm swing
        var d = _driver;
        var input = DriverInput(phys, d, out bool allowDrop, out double boostRoom, out var parkour);
        parkour = ContinueFlight(phys, ref input, parkour);

        // ---- combat: reflexes / fight / spar replace the input while engaged ----
        var combat = CombatBeforePhysics(phys, input);

        // ---- safety: below every action, every tick, before the position goes out ----
        var guard = SafetyAfterCombat(phys, combat, d, allowDrop, parkour);
        input = guard.Input;

        // b1.8.1: PlayerPhysics.Tick; 1.12.2: Physics1122 (multi-version P3), which also stops sprinting at food ≤ 6 (b1.8.1 ignores Hungry).
        phys.SpeedAmplifier = player.EffectAmplifier(PotionEffects.Speed);         // 1.12.2 reads them (b1.8.1's tick doesn't)
        phys.SlownessAmplifier = player.EffectAmplifier(PotionEffects.Slowness);
        phys.JumpBoostAmplifier = player.EffectAmplifier(PotionEffects.JumpBoost);
        double fallBefore = phys.FallDistance;
        var posBefore = phys.Position;
        bool groundBefore = phys.OnGround;
        Options.Profile.Physics.Tick(World, phys, input with { Hungry = !CanSprintNow() });
        if (d is not null || combat.Overrides) { _lookYaw = phys.Yaw; _lookPitch = phys.Pitch; } // idle keeps the last look
        // Story Mode's Fast / Faster (MinecraftClient.Speed.cs): the extra move after the normal one, its own position packets.
        if (!teleported && _speedTarget > 1) SpeedAfterPhysics(s, phys, posBefore, groundBefore, boostRoom, SpeedHeldBack(combat, allowDrop, parkour, guard));
        player.SyncFromPhysics();

        BreathTick(phys, fallBefore);
        CheckDangers(phys);
        SurvivalTick(phys);
        if (!teleported) SendMovement(s, phys);
        CombatAfterMovement(s, combat);
        AfterTickSafety(s, phys, guard);
        EndTick();
    }

    /// <summary>Applies the server's queued teleports; true when there was one (its echo is this tick's movement packet).</summary>
    private bool ApplyServerTeleports(Session s)
    {
        bool teleported = false;
        while (s.Teleports.TryDequeue(out var t))
        {
            ApplyServerTeleport(s, t);
            teleported = true;
        }
        return teleported;
    }

    private void ApplyServerTeleport(Session s, PlayerPositionLookPacket t)
    {
        var player = World.Player;
        // A server-forced move (/tp, a plugin, a far correction): not the answer to a login/respawn (AwaitingTeleport) and
        // farther than any rubber-band. Raised so the brain hears "server moved me from A to B" (live 10-27: "I somehow
        // ended up back at spawn").
        var before = player.Position;
        bool forced = s.PositionKnown && !s.AwaitingTeleport && !player.IsDead;
        var after = new Vec3d(t.X, t.Y, t.Z);
        if (forced && before.DistanceTo(after) > ServerMoveMinDistance) Raise(() => ServerMoved?.Invoke(before, after));
        player.ServerTeleport(new Vec3d(t.X, t.Y, t.Z), t.Stance, t.Yaw, t.Pitch, t.OnGround);
        // Over the void (or chunks not here yet) the confirmation waits until the ground is verified: unconfirmed, b1.8.1
        // never runs its movement checks, so a bot holding still in the void can't be kicked for floating (see DeferEcho).
        if (DeferEcho(after)) _pendingEcho = t.EchoForServer(t.OnGround);
        else { _pendingEcho = null; Send(s, t.EchoForServer(t.OnGround)); }
        NoteTeleportCorrection(s, before, after);
        s.TeleportsReceived++;
        s.AwaitingTeleport = false;
        _lastX = t.X; _lastY = t.Y; _lastStance = t.Stance; _lastZ = t.Z;
        _lastYaw = t.Yaw; _lastPitch = t.Pitch;
        _lookYaw = t.Yaw; _lookPitch = t.Pitch;
        _ticksSincePos = 0;
        _lastTeleportTick = TickCount;
        OnServerTeleportForSafety();
        if (!s.PositionKnown)
        {
            s.PositionKnown = true;
            s.SpawnedTcs.TrySetResult();
        }
        if (s.TeleportsReceived > 1) Log($"server teleport to {t.X:0.##},{t.Y:0.##},{t.Z:0.##}");
    }

    /// <summary>A teleport that isn't the answer to a login/respawn is a correction; a short one a rollback, maybe of a boosted move.</summary>
    private void NoteTeleportCorrection(Session s, Vec3d before, Vec3d after)
    {
        if (!s.PositionKnown || s.AwaitingTeleport) return;
        s.Corrections++;
        if (before.DistanceTo(after) < ServerMoveMinDistance) NoteSpeedCorrection();
    }

    /// <summary>The ticks the body doesn't move on its own: before the spawn position, dead, riding (the server drives it), and after
    /// a teleport/respawn until the ground below is loaded and verified. True = this tick ends here. A deferred teleport echo whose
    /// ground is now verified goes out here (then <paramref name="teleported"/>: the echo is this tick's movement packet).</summary>
    private bool TickHeldBack(Session s, PhysicsState phys, ref bool teleported)
    {
        var player = World.Player;
        if (!s.PositionKnown || s.AwaitingTeleport) return true;
        if (player.IsDead)
        {
            if (!teleported) Send(s, new PlayerOnGroundPacket { OnGround = phys.OnGround });
            return true;
        }
        // ---- riding a boat/minecart: the server drives the body ----
        if (HoldWhileRiding(s, phys, teleported)) return true;
        // ---- knockback: vanilla applies 0x1C to the local player (replacing motion) ----
        ApplyServerVelocity(phys);
        // ---- after a teleport/respawn: no movement until the ground below is loaded and verified ----
        if (HoldForSafety(phys))
        {
            phys.Frozen = true;
            player.SyncFromPhysics();
            if (!teleported && !SilentOverVoid(phys.Position)) SendMovement(s, phys);
            return true;
        }
        // Ground verified under a teleport we hadn't confirmed yet: confirm it now (same spot, the body never moved).
        if (_pendingEcho is { } echo)
        {
            _pendingEcho = null;
            Send(s, echo);
            teleported = true;
        }
        return false;
    }

    /// <summary>The active driver's step, or the idle input. A fight owns the body: the driver is paused (and aborted). A hand-off
    /// hold pauses it without aborting.</summary>
    private MoveInput DriverInput(PhysicsState phys, Driver? d, out bool allowDrop, out double boostRoom, out ParkourCommit? parkour)
    {
        allowDrop = false;
        boostRoom = 0;
        parkour = null;
        if (d is null || (_combatInstance?.Engaged ?? false) || IsHeldStill) return GestureIdle(IdleInput(phys));
        try
        {
            var step = d.Step(phys);
            var input = step.Input;
            allowDrop = step.AllowDrop;
            boostRoom = step.Done ? 0 : step.BoostRoom;
            parkour = step.Done ? null : step.Parkour;
            if (step.Done)
            {
                Interlocked.CompareExchange(ref _driver, null, d);
                _lookYaw = input.Yaw;
                _lookPitch = input.Pitch;
                d.Tcs.TrySetResult();
            }
            return input;
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _driver, null, d);
            d.Tcs.TrySetException(ex);
            return IdleInput(phys);
        }
    }

    /// <summary>The safety layers over the tick's input: the retreats (not while a fight owns the body), breath, then the edge guard /
    /// void step (which yields only to into_void).</summary>
    private GuardDecision SafetyAfterCombat(PhysicsState phys, CombatStep combat, Driver? d, bool allowDrop, ParkourCommit? parkour)
    {
        var input = combat.Input;
        if (!combat.Overrides) input = RetreatOverride(phys, HealRetreatOverride(phys, input, idle: d is null));
        input = BreathOverride(phys, input, driven: d is not null, fighting: combat.Overrides);
        var guard = GuardOrVoidStep(phys, input, allowDrop && !combat.Overrides, combat.Overrides ? null : parkour);
        GestureAfterGuard(combat.Overrides || parkour is not null || allowDrop);
        Combat.OnGuard(guard);
        return guard;
    }

    /// <summary>The speed boost holds back while a fight, a drop, a parkour jump or the edge guard's sneak owns the move.</summary>
    private static bool SpeedHeldBack(CombatStep combat, bool allowDrop, ParkourCommit? parkour, GuardDecision guard) =>
        combat.Overrides || allowDrop || parkour is not null || guard.Input.Sneak;

    private void EndTick()
    {
        _gNow = null;
        Interlocked.Increment(ref _tickCount);
        _changed.Pulse();
    }

    private MoveInput IdleInput(PhysicsState phys)
    {
        // Riding a planned water column (blueprint building down): hold the depth while placing/digging from it.
        if (WaterHoldInput(phys) is { } hover) return hover;
        bool sneak = _idleSneak || _crouchHold;
        if (!sneak && Options.SneakAtEdgesWhenIdle && phys.OnGround && !phys.Frozen)
        {
            var p = phys.Position;
            sneak = Safety.VoidNeighbourCount(World, (int)Math.Floor(p.X), (int)Math.Floor(p.Y + 0.001), (int)Math.Floor(p.Z)) > 0;
        }
        return new MoveInput(Yaw: _lookYaw, Pitch: _lookPitch, Sneak: sneak);
    }

    /// <summary>Vanilla EntityClientPlayerMP.sendMotionUpdates: 0x13 on sprint/sneak change, then exactly one of
    /// 0x0D (moved + rotated), 0x0B (moved), 0x0C (rotated) or 0x0A (neither). Position is forced every 20 ticks.</summary>
    private void SendMovement(Session s, PhysicsState phys)
    {
        int eid = World.Player.EntityId;
        if (phys.Sprinting != _lastSprint)
        {
            Send(s, new EntityActionPacket { EntityId = eid, Action = (sbyte)(phys.Sprinting ? 4 : 5) });
            _lastSprint = phys.Sprinting;
        }
        if (phys.Sneaking != _lastSneak)
        {
            Send(s, new EntityActionPacket { EntityId = eid, Action = (sbyte)(phys.Sneaking ? 1 : 2) });
            _lastSneak = phys.Sneaking;
        }
        if (phys.Frozen)
        {
            Send(s, new PlayerOnGroundPacket { OnGround = phys.OnGround });
            return;
        }

        var pos = phys.Position;
        double stance = phys.Stance;
        double dx = pos.X - _lastX, dy = pos.Y - _lastY, dz = pos.Z - _lastZ, ds = stance - _lastStance;
        bool moved = dx * dx + dy * dy + dz * dz + ds * ds > 0.0009 || _ticksSincePos >= 20;
        var (yaw, pitch) = SentLook(phys); // a gesture's head overlay rides here only; physics keeps the real look
        bool rotated = yaw != _lastYaw || pitch != _lastPitch;
        _ticksSincePos++;

        if (moved && rotated)
            Send(s, new PlayerPositionLookPacket { X = pos.X, Y = pos.Y, Stance = stance, Z = pos.Z, Yaw = yaw, Pitch = pitch, OnGround = phys.OnGround });
        else if (moved)
            Send(s, new PlayerPositionPacket { X = pos.X, Y = pos.Y, Stance = stance, Z = pos.Z, OnGround = phys.OnGround });
        else if (rotated)
            Send(s, new PlayerLookPacket { Yaw = yaw, Pitch = pitch, OnGround = phys.OnGround });
        else
            Send(s, new PlayerOnGroundPacket { OnGround = phys.OnGround });

        if (moved)
        {
            _lastX = pos.X; _lastY = pos.Y; _lastZ = pos.Z; _lastStance = stance;
            _ticksSincePos = 0;
        }
        if (rotated)
        {
            _lastYaw = yaw;
            _lastPitch = pitch;
        }
    }

    private void CheckDangers(PhysicsState phys)
    {
        // Falling with nothing below.
        if (phys.OnGround || phys.InWater || phys.OnLadder) _voidAlerted = false;
        // A committed gap jump over the void whose re-simulated landing still holds is not a fall.
        else if ((_lastGuardActions & GuardActions.ParkourExempt) != 0) { }
        else if (!_voidAlerted && !_voidEpisode && phys.Velocity.Y < -0.3 && World.IsPlayerOverVoid(phys.Position) && !InWaterHold(phys))
        {
            _voidAlerted = true;
            var msg = $"falling into the void at {Fmt(phys.Position)}";
            Log(msg);
            RaiseDanger(DangerKind.FallingIntoVoid, msg, abort: true, critical: true);
        }

        if (phys.InLava)
        {
            if (!_lavaAlerted)
            {
                _lavaAlerted = true;
                RaiseDanger(DangerKind.Lava, "in lava!", abort: true);
            }
        }
        else _lavaAlerted = false;

        if (TickCount % 20 == 0 && Options.HostileAlertRadius > 0)
        {
            var near = World.Entities.Nearby(phys.Position, Options.HostileAlertRadius, e => e.IsHostileMob);
            _hostileAlerted.IntersectWith(near.Select(e => e.Id));
            foreach (var e in near)
                if (_hostileAlerted.Add(e.Id) && !(Combat.DefendSelf && CombatRules.IsThreat(e) && e.TypeId != MobTypes.Enderman))
                    RaiseDanger(DangerKind.HostileNearby,
                        $"{e.DisplayName} {phys.Position.DistanceTo(e.Position):0} blocks away at {Fmt(e.Position)}", abort: false);
        }
    }

    private void RaiseDanger(DangerKind kind, string message, bool abort, bool critical = false)
    {
        // A deliberate void step (into_void): the fall, the void damage and the death are the plan, not dangers.
        if (ActiveVoidStep is not null && kind != DangerKind.Recovered)
        {
            Debug($"into_void: not a danger ({kind}): {message}");
            return;
        }
        var ev = new DangerEvent(kind, message) { Critical = critical };
        Raise(() => Danger?.Invoke(ev));
        _combatInstance?.OnDanger(ev);
        if (abort && Options.AbortActionsOnDanger) AbortCurrentAction(message);
    }

    // ------------------------------------------------------------------ helpers for actions

    /// <summary>Installs a per-tick driver and waits until it reports done (or cancellation/abort).</summary>
    private async Task DriveAsync(Func<PhysicsState, DriverStep> step, CancellationToken ct)
    {
        var d = new Driver(step);
        var prev = Interlocked.Exchange(ref _driver, d);
        prev?.Tcs.TrySetCanceled();
        using var reg = ct.Register(() =>
        {
            Interlocked.CompareExchange(ref _driver, null, d);
            d.Tcs.TrySetCanceled(ct);
        });
        await d.Tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Waits <paramref name="ticks"/> client ticks.</summary>
    private Task WaitTicksAsync(int ticks, CancellationToken ct) => WaitUntilTickAsync(TickCount + Math.Max(1, ticks), ct);

    /// <summary>Waits until the tick counter reaches <paramref name="target"/>.</summary>
    private async Task WaitUntilTickAsync(long target, CancellationToken ct)
    {
        long ticks = Math.Max(1, target - TickCount);
        if (!await _changed.WaitUntilAsync(() => TickCount >= target || SessionToken.IsCancellationRequested,
                TimeSpan.FromMilliseconds(Options.TickInterval.TotalMilliseconds * ticks * 4 + 2000), ct).ConfigureAwait(false))
            throw new ActionAbortedException("tick loop stalled");
        if (SessionToken.IsCancellationRequested) throw new ActionAbortedException("disconnected");
    }

    /// <summary>Waits for a condition (re-checked on every packet and tick). False on timeout.</summary>
    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        bool ok = await _changed.WaitUntilAsync(() => condition() || SessionToken.IsCancellationRequested, timeout, ct)
            .ConfigureAwait(false);
        if (SessionToken.IsCancellationRequested) throw new ActionAbortedException("disconnected");
        return ok;
    }

    /// <summary>Turns the head (yaw/pitch) and waits until the look packet went out.</summary>
    private async Task LookCoreAsync(float yaw, float pitch, CancellationToken ct)
    {
        _lookYaw = yaw;
        _lookPitch = Math.Clamp(pitch, -90f, 90f);
        await WaitTicksAsync(1, ct).ConfigureAwait(false);
    }

    private static string Fmt(Vec3d p) => $"{p.X:0.#},{p.Y:0.#},{p.Z:0.#}";
    private static string Fmt(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
}
