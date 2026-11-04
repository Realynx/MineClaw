using MineClaw.Bot.Perception;
using MineClaw.Protocol;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Synthetic motion traces through each version's entity tracker rules (Fox 10-29: gesture detection, "nodding my head yes or
/// no"): b1.8.1 sends a player's look only once it moved ≥ 8/256 of a turn (11.25°) and a move only at ≥ 1/4 block (mc-dev
/// EntityTrackerEntry, every 2 ticks); 1.12.2 at ≥ 1/256 turn and any move. Every gesture must fire once on both wires; the
/// near misses (looking around, walking while turning, a single crouch, mining, a single nod, a slow look up and down, a hit
/// series, a turn around, sprint-jumping across) must never fire.
/// </summary>
public class GestureDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    internal readonly record struct WireSample(double T, Vec3d Pos, float Yaw, float Pitch, bool Sneak);

    internal readonly record struct Pose(Vec3d Pos, double Yaw, double Pitch, bool Sneak = false);

    /// <summary>What the bot's tracker holds after each packet: the server samples the pose each tick and sends per its rules.</summary>
    internal static List<WireSample> Wire(Func<double, Pose> f, double seconds, bool b181)
    {
        var o = new List<WireSample>();
        int? ly = null, lp = null;
        Vec3d? lpos = null;
        bool? ls = null;
        for (int tick = 0; tick * 0.05 <= seconds + 1e-9; tick++)
        {
            double t = tick * 0.05;
            var m = f(t);
            bool sneakChanged = ls != null && ls != m.Sneak;
            bool emit = sneakChanged || ls == null;
            if (tick % 2 == 0)
            {
                int yb = (int)Math.Floor(m.Yaw * 256 / 360), pb = (int)Math.Floor(m.Pitch * 256 / 360);
                int th = b181 ? 8 : 1;
                if (ly == null || Math.Abs(yb - ly.Value) >= th || Math.Abs(pb - lp!.Value) >= th) { ly = yb; lp = pb; emit = true; }
                double posTh = b181 ? 0.25 : 0.003;
                if (lpos is not { } lq || Math.Abs(m.Pos.X - lq.X) >= posTh || Math.Abs(m.Pos.Y - lq.Y) >= posTh || Math.Abs(m.Pos.Z - lq.Z) >= posTh)
                {
                    lpos = b181 ? new Vec3d(Math.Round(m.Pos.X * 32) / 32, Math.Round(m.Pos.Y * 32) / 32, Math.Round(m.Pos.Z * 32) / 32) : m.Pos;
                    emit = true;
                }
            }
            ls = m.Sneak;
            if (emit && ly != null)
                o.Add(new WireSample(t, lpos!.Value, FixedPoint.FromAngle(unchecked((byte)ly.Value)), FixedPoint.FromAngle(unchecked((byte)lp!.Value)), m.Sneak));
        }
        return o;
    }

    private static readonly Vec3d Here = new(3.5, 64, 3.5);

    internal static List<SeenGesture> Feed(GestureDetector d, IEnumerable<WireSample> wire, string player = "PoofimaFox",
        Func<WireSample, string?>? aim = null, double offset = 0, Func<WireSample, bool?>? ground = null)
    {
        var fired = new List<SeenGesture>();
        foreach (var s in wire)
            fired.AddRange(d.Observe(player, T0.AddSeconds(offset + s.T), s.Pos, s.Yaw, s.Pitch, s.Sneak, ground?.Invoke(s), aim?.Invoke(s)));
        return fired;
    }

    /// <summary>Down-up nods from level: pitch = base + amp·(1 − cos)/2 per period, then still.</summary>
    internal static Func<double, Pose> Nod(double amp = 30, double period = 0.5, int cycles = 2, double basePitch = 0, double yaw = 90, double start = 0.3) =>
        t => new Pose(Here, yaw, t < start || t > start + period * cycles ? basePitch
            : basePitch + amp * (1 - Math.Cos(2 * Math.PI * (t - start) / period)) / 2);

    internal static Func<double, Pose> Shake(double amp = 40, double period = 0.5, int cycles = 2, double pitch = 5, double yaw = 90, double start = 0.3) =>
        t => new Pose(Here, t < start || t > start + period * cycles ? yaw : yaw + amp / 2 * Math.Sin(2 * Math.PI * (t - start) / period), pitch);

    // ------------------------------------------------------------------ gestures fire, once

    [Theory]
    [InlineData(true, 30)]
    [InlineData(false, 30)]
    [InlineData(true, 15)]   // a small nod still reads ≈ 14° through b1.8.1's 11.25° look deadband
    [InlineData(false, 15)]
    [InlineData(true, 55)]
    public void Nod_two_cycles_fires_one_nod(bool b181, double amp)
    {
        var fired = Feed(new GestureDetector(), Wire(Nod(amp), 2, b181));
        var g = Assert.Single(fired);
        Assert.Equal("nod", g.Kind);
        Assert.InRange(g.Strength, 0.4, 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Shake_two_cycles_fires_one_shake(bool b181)
    {
        var g = Assert.Single(Feed(new GestureDetector(), Wire(Shake(), 2, b181)));
        Assert.Equal("shake", g.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_long_nod_is_one_gesture_instance(bool b181)
    {
        var fired = Feed(new GestureDetector(), Wire(Nod(cycles: 6), 4, b181));
        Assert.Equal("nod", Assert.Single(fired).Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Shake_across_the_yaw_wrap_reads_one_shake(bool b181)
    {
        // Facing south (yaw ≈ 0/360): the byte angle wraps 255 → 0 on every swing.
        var g = Assert.Single(Feed(new GestureDetector(), Wire(Shake(yaw: 360), 2, b181)));
        Assert.Equal("shake", g.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nod_from_looking_up_reads_through_the_pitch_wrap(bool b181)
    {
        // Pitch −40 arrives as 331.875° on the wire.
        Assert.Equal("nod", Assert.Single(Feed(new GestureDetector(), Wire(Nod(basePitch: -40), 2, b181))).Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_nod_after_standing_still_fires(bool b181)
    {
        // Live shape: a player stands still (no packets) for a second, then nods: the window opens on an old rest sample.
        var d = new GestureDetector();
        Feed(d, [new WireSample(0, Here, 90, 0, false)]);
        Assert.Equal("nod", Assert.Single(Feed(d, Wire(Nod(start: 0.1), 1.6, b181), offset: 1.0)).Kind);
    }

    [Fact]
    public void A_nod_aimed_at_me_says_so()
    {
        // The look ray hits my head at the nod's rest pitch (0): those samples carry aim "me".
        var g = Assert.Single(Feed(new GestureDetector(), Wire(Nod(), 2, b181: true), aim: s => GestureDetector.Pitch(s.Pitch) < 8 ? "me" : null));
        Assert.Equal("me", g.At);
        var none = Assert.Single(Feed(new GestureDetector(), Wire(Nod(), 2, b181: true)));
        Assert.Null(none.At);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Crouch_bob_fires_and_a_single_crouch_does_not(bool b181)
    {
        Func<double, Pose> bob = t => new Pose(Here, 90, 0, t is > 0.2 and < 0.4 or > 0.6 and < 0.8);
        Assert.Equal("bob", Assert.Single(Feed(new GestureDetector(), Wire(bob, 1.5, b181))).Kind);
        Func<double, Pose> once = t => new Pose(Here, 90, 0, t is > 0.2 and < 1.4);
        Assert.Empty(Feed(new GestureDetector(), Wire(once, 2, b181)));
    }

    [Fact]
    public void Three_air_swings_are_a_wave_but_hits_and_digging_are_not()
    {
        var d = new GestureDetector();
        Feed(d, Wire(t => new Pose(Here, 90, 0), 0.2, true));
        foreach (var s in new[] { 0.3, 0.6, 0.9 }) Assert.Empty(d.Swing("PoofimaFox", T0.AddSeconds(s), clean: true));
        Assert.Empty(d.Poll(T0.AddSeconds(1.0)));                 // still waiting for a hurt status
        Assert.Equal("wave", Assert.Single(d.Poll(T0.AddSeconds(1.3))).Kind);
        Assert.Empty(d.Poll(T0.AddSeconds(1.6)));                 // one wave

        var hit = new GestureDetector();
        Feed(hit, Wire(t => new Pose(Here, 90, 0), 0.2, true));
        foreach (var s in new[] { 0.3, 0.6, 0.9 })
        {
            hit.Swing("PoofimaFox", T0.AddSeconds(s), clean: true);
            hit.Struck("PoofimaFox", T0.AddSeconds(s + 0.05));    // a zombie took the hit
        }
        Assert.Empty(hit.Poll(T0.AddSeconds(1.4)));

        var dig = new GestureDetector();
        Feed(dig, Wire(t => new Pose(Here, 90, 50), 0.2, true));
        for (double s = 0.3; s < 2; s += 0.25) dig.Swing("PoofimaFox", T0.AddSeconds(s), clean: false);
        Assert.Empty(dig.Poll(T0.AddSeconds(2.5)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Jump_spam_in_place_fires_and_sprint_jumping_across_does_not(bool b181)
    {
        static double Hop(double t) { double ph = t % 0.65; return ph < 0.6 ? 1.25 * Math.Sin(Math.PI * ph / 0.6) : 0; }
        Func<WireSample, bool?> ground = s => s.Pos.Y - 64 < 0.05 ? true : s.Pos.Y - Math.Floor(s.Pos.Y) >= 0.1 ? false : null;
        Func<double, Pose> spam = t => new Pose(Here + new Vec3d(0, t < 0.2 ? 0 : Hop(t - 0.2), 0), 90, 0);
        Assert.Equal("jump", Assert.Single(Feed(new GestureDetector(), Wire(spam, 2.6, b181), ground: ground)).Kind);
        Func<double, Pose> across = t => new Pose(Here + new Vec3d(5.6 * t, t < 0.2 ? 0 : Hop(t - 0.2), 0), 270, 0);
        Assert.Empty(Feed(new GestureDetector(), Wire(across, 2.6, b181), ground: ground));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_full_spin_fires_and_turning_around_does_not(bool b181)
    {
        Func<double, Pose> spin = t => new Pose(Here, 30 + 360 * Math.Clamp((t - 0.2) / 0.9, 0, 1), 0);
        Assert.Equal("spin", Assert.Single(Feed(new GestureDetector(), Wire(spin, 1.5, b181))).Kind);
        Func<double, Pose> turn = t => new Pose(Here, 30 + 180 * Math.Clamp((t - 0.2) / 0.5, 0, 1), 0);
        Assert.Empty(Feed(new GestureDetector(), Wire(turn, 1.5, b181)));
    }

    [Fact]
    public void A_stare_at_me_fires_once_per_gaze_and_never_from_far()
    {
        var d = new GestureDetector();
        Feed(d, Wire(t => new Pose(Here, 90, 0), 0.1, true));
        var me = new LookHit("me", "maid_chan", null, 4);
        var got = new List<SeenGesture>();
        for (double t = 0.25; t <= 3; t += 0.25) got.AddRange(d.Poll(T0.AddSeconds(t), (_, _) => me));
        var g = Assert.Single(got);
        Assert.Equal(("stare", "me"), (g.Kind, g.At));

        var far = new GestureDetector();
        Feed(far, Wire(t => new Pose(Here, 90, 0), 0.1, true));
        var away = new LookHit("me", "maid_chan", null, 12);
        for (double t = 0.25; t <= 3; t += 0.25) Assert.Empty(far.Poll(T0.AddSeconds(t), (_, _) => away));
    }

    [Fact]
    public void A_steady_look_at_a_far_block_then_an_air_swing_is_a_point_with_its_target()
    {
        var d = new GestureDetector();
        Feed(d, Wire(t => new Pose(Here, 90, 10), 0.1, true));
        var chest = new LookHit("block", "chest", new BlockPos(12, 64, 3), 9);
        for (double t = 0.25; t <= 1.8; t += 0.25) Assert.Empty(d.Poll(T0.AddSeconds(t), (_, _) => chest));
        var p = Assert.Single(d.Swing("PoofimaFox", T0.AddSeconds(2.0), clean: true));
        Assert.Equal(("point", "chest", "block"), (p.Kind, p.TargetName, p.TargetKind));
        Assert.Equal(new BlockPos(12, 64, 3), p.Target);
        Assert.Empty(d.Poll(T0.AddSeconds(2.6)));                 // the point's swing is no wave

        // A near block is a punch, not a point; a glance (shorter than the hold) is nothing.
        var near = new GestureDetector();
        Feed(near, Wire(t => new Pose(Here, 90, 40), 0.1, true));
        var cell = new LookHit("block", "cobblestone", new BlockPos(4, 63, 3), 2);
        for (double t = 0.25; t <= 1.8; t += 0.25) near.Poll(T0.AddSeconds(t), (_, _) => cell);
        Assert.Empty(near.Swing("PoofimaFox", T0.AddSeconds(2.0), clean: true));
        var glance = new GestureDetector();
        Feed(glance, Wire(t => new Pose(Here, 90, 10), 0.1, true));
        glance.Poll(T0.AddSeconds(0.25), (_, _) => chest);
        glance.Poll(T0.AddSeconds(0.75), (_, _) => chest);
        Assert.Empty(glance.Swing("PoofimaFox", T0.AddSeconds(0.9), clean: true));
    }

    [Fact]
    public void A_nod_after_a_steady_far_look_points_too()
    {
        var d = new GestureDetector();
        var chest = new LookHit("block", "chest", new BlockPos(12, 64, 3), 9);
        Feed(d, Wire(t => new Pose(Here, 90, 0), 0.1, true));
        for (double t = 0.25; t <= 1.8; t += 0.25) d.Poll(T0.AddSeconds(t), (_, _) => chest);
        var fired = Feed(d, Wire(Nod(start: 0.1), 1.5, true), offset: 1.8);
        Assert.Equal(["nod", "point"], fired.Select(g => g.Kind).ToArray());
    }

    // ------------------------------------------------------------------ near misses never fire

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 7)]
    [InlineData(false, 7)]
    [InlineData(false, 42)]
    public void Looking_around_never_fires(bool b181, int seed)
    {
        // A player scanning the island: yaw and pitch both wander, glances snap between spots.
        var rng = new Random(seed);
        var keys = Enumerable.Range(0, 14).Select(i => (T: i * 0.45, Yaw: 90 + rng.Next(-70, 70), Pitch: rng.Next(-35, 50))).ToList();
        Func<double, Pose> scan = t =>
        {
            int i = Math.Min(keys.Count - 2, (int)(t / 0.45));
            double u = Math.Clamp((t - keys[i].T) / 0.3, 0, 1); // a quick turn, then a hold
            return new Pose(Here, keys[i].Yaw + (keys[i + 1].Yaw - keys[i].Yaw) * u, keys[i].Pitch + (keys[i + 1].Pitch - keys[i].Pitch) * u);
        };
        var fired = Feed(new GestureDetector(), Wire(scan, 6, b181));
        Assert.DoesNotContain(fired, g => g.Kind is "nod" or "shake");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Walking_while_turning_is_no_shake(bool b181)
    {
        Func<double, Pose> walk = t => new Pose(Here + new Vec3d(4.3 * t, 0, 0), 270 + 22 * Math.Sin(2 * Math.PI * t / 0.5), 5);
        Assert.Empty(Feed(new GestureDetector(), Wire(walk, 3, b181)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mining_two_gen_cells_is_no_nod_and_no_wave(bool b181)
    {
        // Looks between the two cells (pitch 40 / 60, small yaw step) while swinging at them: digging swings.
        var d = new GestureDetector();
        Func<double, Pose> mine = t => new Pose(Here, 90 + ((int)(t / 0.3) % 2) * 8, (int)(t / 0.3) % 2 == 0 ? 40 : 60);
        var fired = new List<SeenGesture>();
        foreach (var s in Wire(mine, 3, b181))
        {
            fired.AddRange(d.Observe("PoofimaFox", T0.AddSeconds(s.T), s.Pos, s.Yaw, s.Pitch, s.Sneak));
            if ((int)(s.T * 20) % 5 == 0) fired.AddRange(d.Swing("PoofimaFox", T0.AddSeconds(s.T), clean: false));
        }
        fired.AddRange(d.Poll(T0.AddSeconds(3.5)));
        Assert.Empty(fired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_single_nod_or_a_slow_look_down_and_up_is_nothing(bool b181)
    {
        Assert.Empty(Feed(new GestureDetector(), Wire(Nod(cycles: 1), 2, b181)));
        Assert.Empty(Feed(new GestureDetector(), Wire(Nod(period: 1.6, cycles: 2, amp: 50), 4, b181)));
    }

    [Fact]
    public void A_nod_while_swinging_at_blocks_is_work()
    {
        var d = new GestureDetector();
        var fired = new List<SeenGesture>();
        bool swung = false;
        foreach (var s in Wire(Nod(), 2, true))
        {
            if (!swung && s.T >= 0.6) { d.Swing("PoofimaFox", T0.AddSeconds(0.6), clean: false); swung = true; } // placing a block mid-motion
            fired.AddRange(d.Observe("PoofimaFox", T0.AddSeconds(s.T), s.Pos, s.Yaw, s.Pitch, s.Sneak));
        }
        Assert.Empty(fired);
    }

    [Fact]
    public void Zigzag_pivots_ignore_jitter_below_the_hysteresis()
    {
        var v = new List<double> { 0, 3, -2, 4, 20, 18, 22, 5, 2, 25, 0 };
        Assert.Equal([0, 6, 8, 9, 10], GestureDetector.Pivots(v, 10));
    }

    [Fact]
    public void Zigzag_pivots_match_the_reference_walk_on_random_series()
    {
        var rnd = new Random(7);
        for (int n = 0; n < 2000; n++)
        {
            var v = new List<double>();
            int len = rnd.Next(0, 40);
            double x = 0;
            for (int i = 0; i < len; i++) { x += rnd.Next(-20, 21) * (rnd.Next(4) == 0 ? 0.5 : 1); v.Add(x); }
            double h = rnd.Next(1, 25);
            Assert.Equal(ReferencePivots(v, h), GestureDetector.Pivots(v, h));
        }
    }

    /// <summary>The zigzag walk as first written (one branch per direction): the refactored one must match it exactly.</summary>
    private static List<int> ReferencePivots(IReadOnlyList<double> v, double h)
    {
        var piv = new List<int>();
        if (v.Count == 0) return piv;
        piv.Add(0);
        int dir = 0, ext = 0;
        for (int i = 1; i < v.Count; i++)
        {
            if (dir == 0)
            {
                if (v[i] - v[0] >= h) { dir = 1; ext = i; }
                else if (v[0] - v[i] >= h) { dir = -1; ext = i; }
                else if (Math.Abs(v[i] - v[0]) > Math.Abs(v[ext] - v[0])) ext = i;
            }
            else if (dir == 1)
            {
                if (v[i] > v[ext]) ext = i;
                else if (v[ext] - v[i] >= h) { piv.Add(ext); dir = -1; ext = i; }
            }
            else
            {
                if (v[i] < v[ext]) ext = i;
                else if (v[i] - v[ext] >= h) { piv.Add(ext); dir = 1; ext = i; }
            }
        }
        if (dir != 0) piv.Add(ext);
        return piv;
    }
}
