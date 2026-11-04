using MineClaw.World;
using MineClaw.World.Versioning;

namespace MineClaw.Client;

/// <summary>A body's speed boost was stepped down after the server kept rejecting it (<see cref="MinecraftClient.SpeedSteppedDown"/>).</summary>
/// <param name="From">The multiplier it ran at.</param>
/// <param name="To">The multiplier it runs at now (1 = normal).</param>
/// <param name="Rollbacks">The server's corrections that led to it.</param>
public sealed record SpeedStepDown(double From, double To, int Rollbacks);

/// <summary>What a body's speed boost does now (<see cref="MinecraftClient.SpeedNow"/>).</summary>
/// <param name="Requested">The multiplier asked for (1 normal, 2 fast, 3 faster).</param>
/// <param name="Effective">What it runs at after any step-down (<see cref="MinecraftClient.SpeedCap"/>).</param>
/// <param name="Client">The client's own share: <paramref name="Effective"/> over the server's Speed effect (1.12.2).</param>
/// <param name="ServerFactor">The server's Speed effect on the movement attribute (1 = none; Speed II 1.4).</param>
/// <param name="Rollbacks">The server's corrections while boosting, since the body logged in.</param>
public sealed record SpeedState(double Requested, double Effective, double Client, double ServerFactor, int Rollbacks);

/// <summary>
/// Story Mode's Fast / Faster for one body (docs/web/story-mode.md "Speed"): the client moves this body further each tick than
/// vanilla (<see cref="SpeedBoost"/>: the version's own collision, never in the air, extra position packets so the server's replay
/// matches). Off (1) for every body by default: only the Story director sets it, and only on its colony's pawns.
/// <para><b>Rollback safety</b>: a server correction (a teleport back that isn't a login, respawn or /tp) within
/// <see cref="BoostRecentTicks"/> of a boosted tick counts as a rejection; <see cref="StepDownRollbacks"/> of them within
/// <see cref="StepDownWindowTicks"/> step the body down one level (3 → 2 → 1) and raise <see cref="SpeedSteppedDown"/>. The boost
/// also rests <see cref="AfterTeleportTicks"/> after any teleport. A new <see cref="SpeedMultiplier"/> clears the step-down.</para>
/// </summary>
public sealed partial class MinecraftClient
{
    public const int BoostRecentTicks = 40, StepDownRollbacks = 2, StepDownWindowTicks = 600, AfterTeleportTicks = 10;

    private double _speedTarget = 1, _speedCap = SpeedBoost.MaxMultiplier;
    private long _lastBoostTick = long.MinValue / 2, _lastTeleportTick = long.MinValue / 2;
    private readonly Queue<long> _boostRejections = new();
    private int _speedRollbacks;
    private readonly object _speedLock = new();

    /// <summary>The movement multiplier asked for (1 = normal, 2 = Fast, 3 = Faster; clamped to 1..3). A change clears a step-down.</summary>
    public double SpeedMultiplier
    {
        get => Volatile.Read(ref _speedTarget);
        set
        {
            double v = double.IsFinite(value) ? Math.Clamp(value, 1, SpeedBoost.MaxMultiplier) : 1;
            lock (_speedLock)
            {
                if (v == _speedTarget) return;
                Volatile.Write(ref _speedTarget, v);
                Volatile.Write(ref _speedCap, SpeedBoost.MaxMultiplier);
                _boostRejections.Clear();
            }
            Log($"speed: ×{v:0.##}");
        }
    }

    /// <summary>The ceiling the server's rejections left (3 until one steps it down).</summary>
    public double SpeedCap => Volatile.Read(ref _speedCap);

    /// <summary>The server's corrections while boosting since the client was created.</summary>
    public int SpeedRollbacks => Volatile.Read(ref _speedRollbacks);

    /// <summary>The boost was stepped down after repeated server corrections (raised on the event thread).</summary>
    public event Action<SpeedStepDown>? SpeedSteppedDown;

    /// <summary>The server's own speed-up of this body: a Speed effect on 1.12.2 (<c>+20 %</c> a level on the movement attribute,
    /// which the 1.12.2 physics tick already applies); b1.8.1 has no effects.</summary>
    public double ServerSpeedFactor
    {
        get
        {
            if (Options.Profile.Physics is not Release1122PhysicsRules) return 1;
            int amp = World.Player.EffectAmplifier(PotionEffects.Speed);
            return amp >= 0 ? 1 + MineClaw.World.Release1122.Physics1122.SpeedPotionModifier * (amp + 1) : 1;
        }
    }

    public SpeedState SpeedNow
    {
        get
        {
            double req = SpeedMultiplier, eff = Math.Min(req, SpeedCap), server = ServerSpeedFactor;
            return new SpeedState(req, eff, Math.Max(1, eff / server), server, SpeedRollbacks);
        }
    }

    /// <summary>The multiplier the client applies this tick (the effective one over the server's share; never below 1).</summary>
    private double ClientSpeedFactor()
    {
        double eff = Math.Min(SpeedMultiplier, SpeedCap);
        if (eff <= 1.0001) return 1;
        return Math.Max(1, eff / ServerSpeedFactor);
    }

    /// <summary>
    /// After the physics tick: the extra move of a boosted body (<see cref="SpeedBoost.Plan"/>), its first segments sent at once
    /// (the tick's own position packet carries the last). Only while a driver moves the body with room to spare
    /// (<paramref name="room"/>, from the path follower), never in a fight, a hold, a planned drop or gap jump, a void step, or just
    /// after a teleport.
    /// </summary>
    private void SpeedAfterPhysics(Session s, PhysicsState phys, Vec3d before, bool groundBefore, double room, bool exempt)
    {
        if (room <= 0 || exempt) return;
        double factor = ClientSpeedFactor();
        if (factor <= 1.0001) return;
        if (TickCount - _lastTeleportTick < AfterTeleportTicks || IsHeldStill || ActiveVoidStep is not null || (_combatInstance?.Engaged ?? false)) return;
        if (SpeedBoost.Plan(World, phys, before, groundBefore, factor, room) is not { } plan) return;
        SendMovement(s, phys); // the normal tick's position: the server replays it on its own
        SpeedBoost.Apply(World, phys, plan, _ => SendMovement(s, phys));
        _lastBoostTick = TickCount;
    }

    /// <summary>A server correction (not a login, respawn or forced move): counts against the boost when it was boosting.</summary>
    private void NoteSpeedCorrection()
    {
        _lastTeleportTick = TickCount;
        if (TickCount - _lastBoostTick > BoostRecentTicks) return;
        SpeedStepDown? down = null;
        lock (_speedLock)
        {
            Interlocked.Increment(ref _speedRollbacks);
            long now = TickCount;
            _boostRejections.Enqueue(now);
            while (_boostRejections.Count > 0 && now - _boostRejections.Peek() > StepDownWindowTicks) _boostRejections.Dequeue();
            if (_boostRejections.Count < StepDownRollbacks) return;
            double from = Math.Min(_speedTarget, _speedCap);
            if (from <= 1.0001) return;
            double to = from > 2.0001 ? 2 : 1;
            Volatile.Write(ref _speedCap, to);
            down = new SpeedStepDown(from, to, _boostRejections.Count);
            _boostRejections.Clear();
        }
        Log($"speed: the server rejected ×{down.From:0.##} ({down.Rollbacks} corrections): ×{down.To:0.##} now");
        Raise(() => SpeedSteppedDown?.Invoke(down));
    }
}
