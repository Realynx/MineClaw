using System.Diagnostics;
using MineClaw.Client.Combat;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    /// <summary>One tick of a driver: the input to apply and whether the driver is finished. <paramref name="AllowDrop"/>
    /// marks a planned drop to a lower floor (the edge guard then verifies the landing instead of forcing sneak);
    /// <paramref name="Parkour"/> a committed gap jump (the guard's planned-parkour exemption re-simulates it).</summary>
    private readonly record struct DriverStep(MoveInput Input, bool Done = false, bool AllowDrop = false, ParkourCommit? Parkour = null);

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
            else if (wait < -interval * 10) next = sw.Elapsed; // badly behind (debugger, GC): don't burst-catch-up
        }
    }

    private void TickOnce(Session s)
    {
        var player = World.Player;
        var phys = player.Physics;
        bool teleported = false;
        while (s.Teleports.TryDequeue(out var t))
        {
            // A server-forced move (/tp, a plugin, a far correction): not the answer to a login/respawn (AwaitingTeleport) and
            // farther than any rubber-band. Raised so the brain hears "server moved me from A to B" (live 10-27: "I somehow
            // ended up back at spawn").
            var before = player.Position;
            bool forced = s.PositionKnown && !s.AwaitingTeleport && !player.IsDead;
            var after = new Vec3d(t.X, t.Y, t.Z);
            if (forced && before.DistanceTo(after) > ServerMoveMinDistance) Raise(() => ServerMoved?.Invoke(before, after));
            player.ServerTeleport(new Vec3d(t.X, t.Y, t.Z), t.Stance, t.Yaw, t.Pitch, t.OnGround);
            Send(s, t.EchoForServer(t.OnGround));
            if (s.PositionKnown && !s.AwaitingTeleport) s.Corrections++;
            s.TeleportsReceived++;
            s.AwaitingTeleport = false;
            _lastX = t.X; _lastY = t.Y; _lastStance = t.Stance; _lastZ = t.Z;
            _lastYaw = t.Yaw; _lastPitch = t.Pitch;
            _lookYaw = t.Yaw; _lookPitch = t.Pitch;
            _ticksSincePos = 0;
            teleported = true;
            OnServerTeleportForSafety();
            if (!s.PositionKnown)
            {
                s.PositionKnown = true;
                s.SpawnedTcs.TrySetResult();
            }
            if (s.TeleportsReceived > 1) Log($"server teleport to {t.X:0.##},{t.Y:0.##},{t.Z:0.##}");
        }

        if (!s.PositionKnown || s.AwaitingTeleport)
        {
            EndTick();
            return;
        }

        if (player.IsDead)
        {
            if (!teleported) Send(s, new PlayerOnGroundPacket { OnGround = phys.OnGround });
            EndTick();
            return;
        }

        // ---- riding a boat/minecart: the server drives the body ----
        if (HoldWhileRiding(s, phys, teleported))
        {
            EndTick();
            return;
        }

        // ---- knockback: vanilla applies 0x1C to the local player (replacing motion) ----
        ApplyServerVelocity(phys);

        // ---- after a teleport/respawn: no movement until the ground below is loaded and verified ----
        if (HoldForSafety(phys))
        {
            phys.Frozen = true;
            player.SyncFromPhysics();
            if (!teleported) SendMovement(s, phys);
            EndTick();
            return;
        }

        // ---- input from the active driver, or idle ----
        MoveInput input;
        bool allowDrop = false;
        ParkourCommit? parkour = null;
        var d = _driver;
        // A fight owns the body: the driver is paused (and aborted). A hand-off hold pauses it without aborting.
        if (d is not null && !(_combatInstance?.Engaged ?? false) && !IsHeldStill)
        {
            try
            {
                var step = d.Step(phys);
                input = step.Input;
                allowDrop = step.AllowDrop;
                parkour = step.Done ? null : step.Parkour;
                if (step.Done)
                {
                    Interlocked.CompareExchange(ref _driver, null, d);
                    _lookYaw = input.Yaw;
                    _lookPitch = input.Pitch;
                    d.Tcs.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _driver, null, d);
                d.Tcs.TrySetException(ex);
                input = IdleInput(phys);
            }
        }
        else input = IdleInput(phys);
        parkour = ContinueFlight(phys, ref input, parkour);

        // ---- combat: reflexes / fight / spar replace the input while engaged ----
        var combat = CombatBeforePhysics(phys, input);
        input = combat.Input;

        // ---- safety: below every action, every tick, before the position goes out ----
        if (!combat.Overrides) input = RetreatOverride(phys, HealRetreatOverride(phys, input, idle: d is null));
        var guard = SafetyFilter(phys, input, allowDrop && !combat.Overrides, combat.Overrides ? null : parkour);
        input = guard.Input;
        Combat.OnGuard(guard);

        PlayerPhysics.Tick(World, phys, input);
        if (d is not null || combat.Overrides) { _lookYaw = phys.Yaw; _lookPitch = phys.Pitch; } // idle keeps the last look
        player.SyncFromPhysics();

        CheckDangers(phys);
        SurvivalTick(phys);
        if (!teleported) SendMovement(s, phys);
        CombatAfterMovement(s, combat);
        AfterTickSafety(s, phys, guard);
        EndTick();
    }

    private void EndTick()
    {
        Interlocked.Increment(ref _tickCount);
        _changed.Pulse();
    }

    private MoveInput IdleInput(PhysicsState phys)
    {
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
        bool rotated = phys.Yaw != _lastYaw || phys.Pitch != _lastPitch;
        _ticksSincePos++;

        if (moved && rotated)
            Send(s, new PlayerPositionLookPacket { X = pos.X, Y = pos.Y, Stance = stance, Z = pos.Z, Yaw = phys.Yaw, Pitch = phys.Pitch, OnGround = phys.OnGround });
        else if (moved)
            Send(s, new PlayerPositionPacket { X = pos.X, Y = pos.Y, Stance = stance, Z = pos.Z, OnGround = phys.OnGround });
        else if (rotated)
            Send(s, new PlayerLookPacket { Yaw = phys.Yaw, Pitch = phys.Pitch, OnGround = phys.OnGround });
        else
            Send(s, new PlayerOnGroundPacket { OnGround = phys.OnGround });

        if (moved)
        {
            _lastX = pos.X; _lastY = pos.Y; _lastZ = pos.Z; _lastStance = stance;
            _ticksSincePos = 0;
        }
        if (rotated)
        {
            _lastYaw = phys.Yaw;
            _lastPitch = phys.Pitch;
        }
    }

    private void CheckDangers(PhysicsState phys)
    {
        // Falling with nothing below.
        if (phys.OnGround || phys.InWater || phys.OnLadder) _voidAlerted = false;
        // A committed gap jump over the void whose re-simulated landing still holds is not a fall.
        else if ((_lastGuardActions & GuardActions.ParkourExempt) != 0) { }
        else if (!_voidAlerted && !_voidEpisode && phys.Velocity.Y < -0.3 && World.IsPlayerOverVoid(phys.Position))
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
