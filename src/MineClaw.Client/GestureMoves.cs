namespace MineClaw.Client;

/// <summary>
/// One client tick of a body-language gesture. <see cref="Yaw"/>/<see cref="Pitch"/> are offsets from the body's real look
/// (or, with <see cref="Absolute"/>, the look itself); <see cref="Sneak"/> holds sneak this tick, <see cref="Swing"/> sends
/// one 0x12 arm swing, <see cref="Jump"/> presses jump. A frame with no look is "head at rest".
/// </summary>
public readonly record struct GestureFrame(float Yaw = 0, float Pitch = 0, bool Sneak = false, bool Swing = false, bool Jump = false,
    bool Absolute = false)
{
    public bool HasLook => Absolute || Yaw != 0 || Pitch != 0;
}

/// <summary>A named tick-by-tick gesture. Body gestures (sneak, jump) need an idle body; head/arm ones may overlay a walk.</summary>
public sealed record GesturePlan(string Name, IReadOnlyList<GestureFrame> Frames)
{
    /// <summary>Sneak or jump: only on an idle body (no action, no driver).</summary>
    public bool UsesBody => Frames.Any(f => f.Sneak || f.Jump);
    public bool UsesLook => Frames.Any(f => f.HasLook);
    public int Ticks => Frames.Count;
}

/// <summary>
/// The gesture library, in vanilla b1.8.1 packets only. What other players see (mc-dev 218a2a71 EntityTrackerEntry.track):
/// a player's look reaches them every 2 ticks and only when yaw or pitch moved ≥ 8/256 of a turn (≈ 11.25°) since the
/// last update, so every swing of the head here is ≥ 20°. 0x12 animation 1 → <c>player.v()</c> (arm swing, broadcast);
/// 0x13 action 1/2 → <c>setSneak(true/false)</c> (metadata flag, broadcast), so each sneak state is held ≥ 3 ticks.
/// </summary>
public static class GestureMoves
{
    /// <summary>Pitch down/up <paramref name="cycles"/> times (≈ 0.3 s each). Looking far down already, the nod goes up.</summary>
    public static GesturePlan Nod(int cycles = 2, float amplitude = 25, float basePitch = 0)
    {
        float sign = basePitch + amplitude > 85 ? -1 : 1;
        var f = new List<GestureFrame>();
        for (int c = 0; c < Math.Clamp(cycles, 1, 4); c++)
            foreach (var k in new[] { 0.4f, 0.8f, 1f, 0.8f, 0.4f, 0f })
                f.Add(new GestureFrame(Pitch: sign * amplitude * k));
        return new GesturePlan("nod", f);
    }

    /// <summary>Yaw right/left <paramref name="cycles"/> times (≈ 0.4 s each).</summary>
    public static GesturePlan Shake(int cycles = 2, float amplitude = 30)
    {
        var f = new List<GestureFrame>();
        for (int c = 0; c < Math.Clamp(cycles, 1, 4); c++)
            foreach (var k in new[] { 0.5f, 1f, 0.5f, 0f, -0.5f, -1f, -0.5f, 0f })
                f.Add(new GestureFrame(Yaw: amplitude * k));
        return new GesturePlan("shake", f);
    }

    /// <summary><paramref name="swings"/> arm swings 6 ticks apart (each b1.8 swing animates ~6-8 ticks).</summary>
    public static GesturePlan Wave(int swings = 3)
    {
        int n = Math.Clamp(swings, 1, 5);
        var f = new List<GestureFrame>();
        for (int i = 0; i < n * 6; i++) f.Add(new GestureFrame(Swing: i % 6 == 0));
        return new GesturePlan("wave", f);
    }

    /// <summary>A bow: head dips 40° with a sneak held at the bottom, then back up. Without the body: the dip alone.</summary>
    public static GesturePlan Bow(bool sneak = true, float depth = 40)
    {
        float[] pitch = [0.25f, 0.5f, 0.75f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 0.75f, 0.5f, 0.25f, 0f];
        var f = new List<GestureFrame>();
        for (int i = 0; i < pitch.Length; i++)
            f.Add(new GestureFrame(Pitch: depth * pitch[i], Sneak: sneak && i >= 2 && i <= 10));
        return new GesturePlan(sneak ? "bow" : "bow(head)", f);
    }

    /// <summary>The classic "hi"/excited crouch-bob: <paramref name="pulses"/> sneak pulses (4 ticks down, 3 up).</summary>
    public static GesturePlan CrouchBob(int pulses = 3)
    {
        var f = new List<GestureFrame>();
        for (int p = 0; p < Math.Clamp(pulses, 1, 5); p++)
        {
            for (int i = 0; i < 4; i++) f.Add(new GestureFrame(Sneak: true));
            for (int i = 0; i < 3; i++) f.Add(new GestureFrame());
        }
        return new GesturePlan("bob", f);
    }

    /// <summary>Head turned to an absolute look for <paramref name="ticks"/> (default 1.2 s), then back.</summary>
    public static GesturePlan LookAt(float yaw, float pitch, int ticks = 24)
    {
        var f = new List<GestureFrame>();
        for (int i = 0; i < Math.Clamp(ticks, 1, 60); i++) f.Add(new GestureFrame(Yaw: yaw, Pitch: Math.Clamp(pitch, -90f, 90f), Absolute: true));
        return new GesturePlan("look", f);
    }

    /// <summary>A small hop in place (one jump, then the landing).</summary>
    public static GesturePlan Hop()
    {
        var f = new List<GestureFrame> { new(Jump: true) };
        for (int i = 0; i < 13; i++) f.Add(new GestureFrame());
        return new GesturePlan("hop", f);
    }

    /// <summary>Plays two gestures at once (e.g. wave + look-at): flags OR-ed; an absolute look wins, offsets add.</summary>
    public static GesturePlan Merge(GesturePlan a, GesturePlan b)
    {
        int n = Math.Max(a.Ticks, b.Ticks);
        var f = new List<GestureFrame>(n);
        for (int i = 0; i < n; i++)
        {
            var x = i < a.Ticks ? a.Frames[i] : default;
            var y = i < b.Ticks ? b.Frames[i] : default;
            var look = new GestureFrame(Yaw: x.Yaw + y.Yaw, Pitch: x.Pitch + y.Pitch);
            if (x.Absolute) look = x with { Yaw = x.Yaw + (y.Absolute ? 0 : y.Yaw), Pitch = x.Pitch + (y.Absolute ? 0 : y.Pitch) };
            else if (y.Absolute) look = y with { Yaw = y.Yaw + x.Yaw, Pitch = y.Pitch + x.Pitch };
            f.Add(look with { Sneak = x.Sneak || y.Sneak, Swing = x.Swing || y.Swing, Jump = x.Jump || y.Jump });
        }
        return new GesturePlan(a.Name + "+" + b.Name, f);
    }
}
