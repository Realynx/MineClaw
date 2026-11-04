using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Brainstem gestures (nod, shake, wave, bow, crouch-bob, look-at, hop) played tick by tick without the action lock.
/// <list type="bullet">
/// <item>The look of a gesture is an overlay on the <b>sent</b> 0x0C/0x0D only: physics keeps the driver's yaw, so the
///   movement heading (<see cref="PlayerPhysics"/> moves along <c>input.Yaw</c>) never changes while a walk is overlaid,
///   and when the gesture ends the next movement packet carries the real look again (the head is "restored").</item>
/// <item>Sneak/jump frames go through the idle input (so the EdgeGuard still filters a hop) and need an idle body: no
///   action, no driver, no held crouch. When the gesture ends or is cancelled the idle input stops sneaking, so the next
///   tick's 0x13 is "stop sneaking": a sneak pulse can't be left on.</item>
/// <item>Cancelled at once by a safety reflex/fight, a parkour flight or planned drop, an action starting (body
///   gestures), leaving the ground under a driver, and by any dig/place/use packet: the real look goes out first, so the
///   server never orients a placed block (furnace, stairs, piston) by a nod.</item>
/// </list>
/// </summary>
public sealed partial class MinecraftClient
{
    private sealed class GestureRun(GesturePlan plan)
    {
        public GesturePlan Plan { get; } = plan;
        public long StartTick = -1;
    }

    private volatile GestureRun? _gesture;
    private GestureFrame? _gNow;          // this tick's frame (tick thread only)
    private volatile bool _gOnWire;       // the last movement packet carried a gesture look

    /// <summary>The gesture playing now (name), or null.</summary>
    public string? CurrentGesture => _gesture?.Plan.Name;

    /// <summary>Gestures played / cancelled before their end (telemetry, tests).</summary>
    public int GesturesPlayed, GesturesCancelled;

    /// <summary>
    /// Starts <paramref name="plan"/> on the next tick (replacing a running gesture). False when not playing, a safety
    /// reflex owns the body, or the plan sneaks/jumps while the body is busy or held crouched.
    /// </summary>
    public bool PlayGesture(GesturePlan plan)
    {
        if (plan.Frames.Count == 0 || !IsPlaying || SafetyEngaged || World.Player.IsDead) return false;
        if (plan.UsesBody && BodyHeldForGesture()) return false;
        var prev = Interlocked.Exchange(ref _gesture, new GestureRun(plan));
        if (prev != null) Interlocked.Increment(ref GesturesCancelled);
        Interlocked.Increment(ref GesturesPlayed);
        return true;
    }

    private bool BodyBusyForGesture() => _driver is not null || CurrentAction is not null || _crouchHold;

    /// <summary>An action, a drive, a held crouch or an action's sneak owns the body.</summary>
    private bool BodyHeldForGesture() => CurrentAction is not null || _driver is not null || _crouchHold || _idleSneak;

    /// <summary>Stops the gesture now; the next tick sends the real look and, if it was sneaking, "stop sneaking".</summary>
    public void CancelGesture()
    {
        if (Interlocked.Exchange(ref _gesture, null) != null) Interlocked.Increment(ref GesturesCancelled);
    }

    /// <summary>Why the gesture ends this tick (done, a safety reflex, the body busy, airborne on a drive), or null.</summary>
    private string? GestureStop(GestureRun g, long i, PhysicsState phys)
    {
        if (i >= g.Plan.Frames.Count) return "done";
        if (SafetyEngaged || World.Player.IsDead) return "safety";
        if (g.Plan.UsesBody && BodyBusyForGesture()) return "body busy";
        return _driver is not null && !phys.OnGround && !phys.InWater ? "airborne" : null;
    }

    /// <summary>Tick thread, before the input: picks this tick's frame (or ends/cancels the gesture) and sends its swing.</summary>
    private void GestureTick(Session s, PhysicsState phys)
    {
        _gNow = null;
        var g = _gesture;
        if (g is null) return;
        if (g.StartTick < 0) g.StartTick = TickCount;
        long i = TickCount - g.StartTick;
        string? stop = GestureStop(g, i, phys);
        if (stop != null)
        {
            if (Interlocked.CompareExchange(ref _gesture, null, g) == g && stop != "done") Interlocked.Increment(ref GesturesCancelled);
            return;
        }
        var f = g.Plan.Frames[(int)i];
        if (f.Swing) Send(s, new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
        _gNow = f;
    }

    /// <summary>After the safety filter: a fight, a parkour flight or a planned drop owns the head too.</summary>
    private void GestureAfterGuard(bool overridden)
    {
        if (_gNow is null || !overridden) return;
        _gNow = null;
        CancelGesture();
    }

    /// <summary>Idle input with this tick's gesture sneak/jump (body gestures only run with no driver).</summary>
    private MoveInput GestureIdle(MoveInput idle) =>
        _gNow is { } f && (f.Sneak || f.Jump) ? idle with { Sneak = idle.Sneak || f.Sneak, Jump = f.Jump } : idle;

    /// <summary>The look the movement packet carries: the real one, or this tick's gesture overlay.</summary>
    private (float Yaw, float Pitch) SentLook(PhysicsState phys)
    {
        if (_gNow is not { HasLook: true } f) { _gOnWire = false; return (phys.Yaw, phys.Pitch); }
        _gOnWire = true;
        if (!f.Absolute) return (phys.Yaw + f.Yaw, Math.Clamp(phys.Pitch + f.Pitch, -90f, 90f));
        // Absolute: the nearest equivalent of the target yaw, so the head never spins the long way round.
        float d = (f.Yaw - phys.Yaw) % 360f;
        if (d > 180) d -= 360;
        else if (d < -180) d += 360;
        return (phys.Yaw + d, Math.Clamp(f.Pitch, -90f, 90f));
    }

    /// <summary>Sends this look to the server now (ahead of a click that depends on it) and keeps it as the last one sent.</summary>
    private void SendLookNow(float yaw, float pitch)
    {
        if (_session is not { } s) return;
        var phys = World.Player.Physics;
        try { s.Conn.Send(new PlayerLookPacket { Yaw = yaw, Pitch = pitch, OnGround = phys.OnGround }); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { return; }
        _lastYaw = yaw;
        _lastPitch = pitch;
    }

    /// <summary>
    /// A dig/place/use is about to go out while the server holds a gesture look: cancel the gesture and send the real look
    /// first (b1.8.1 orients furnaces/stairs/pistons by the player's yaw/pitch at placement).
    /// </summary>
    private void GestureBeforeUse(Session s, IPacket p)
    {
        if (!_gOnWire || p is not (PlayerBlockPlacementPacket or PlayerDiggingPacket or UseEntityPacket)) return;
        CancelGesture();
        _gOnWire = false;
        var phys = World.Player.Physics;
        float yaw = _driver is null ? _lookYaw : phys.Yaw, pitch = _driver is null ? _lookPitch : phys.Pitch;
        try { s.Conn.Send(new PlayerLookPacket { Yaw = yaw, Pitch = pitch, OnGround = phys.OnGround }); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { return; }
        _lastYaw = yaw;
        _lastPitch = pitch;
    }
}
