using MineClaw.World.Release1122;
using MineClaw.World.Versioning;

namespace MineClaw.World.Tests;

/// <summary>
/// Golden trajectories of the 1.12.2 player tick (multi-version P3). No 1.12.2 recordings exist offline, so every expectation is
/// computed here independently from the decompiled source's formulas (<c>EntityLivingBase.travel</c>/<c>jump</c>,
/// <c>Entity.moveRelative</c>/<c>move</c>, <c>EntityPlayer.onLivingUpdate</c>) with the same float literals, or is a
/// published 1.12 figure (walk 4.317 m/s, sprint 5.612, sneak 1.295, ladder up 2.35 / down 3.0, swim 1.96).
/// </summary>
public class Physics1122Tests
{
    private const float East = -90f; // yaw walking toward +X
    // 1.12.2 ids
    private const int Stone = 1, Bed = 26, Slab = 44, Ladder = 65, SnowLayer = 78, Ice = 79, Fence = 85, SoulSand = 88,
        Trapdoor = 96, Vine = 106, Wall = 139, Slime = 165, HayBale = 170, Carpet = 171, Water = 9;

    internal static WorldState World()
    {
        var w = new WorldState(TestBlocks.Get, 256, physics: Release1122PhysicsRules.Instance);
        const int size = 64;
        w.ApplyChunkRegion(-32, 0, -32, size, 256, size, new byte[size * 256 * size], null, null, null);
        return w;
    }

    private static WorldState Floor(int id = Stone)
    {
        var w = World();
        w.Fill(-20, 63, -20, 20, 63, 20, id);
        return w;
    }

    private static PhysicsState Standing(double x, double y, double z, float yaw = 0)
    {
        var s = new PhysicsState();
        s.Teleport(new Vec3d(x, y, z), yaw, 0);
        s.OnGround = true;
        return s;
    }

    private static void Run(IBlockAccess w, PhysicsState s, MoveInput input, int ticks)
    {
        for (int i = 0; i < ticks; i++) PlayerPhysics.Tick(w, s, input);
    }

    /// <summary>Terminal displacement per tick on flat ground: v ← (v + a)·f, moved v + a, so d* = a / (1 − f).</summary>
    private static double GroundTerminal(float inputScale, float landSpeed, float slip = 0.6f)
    {
        float o = slip * 0.91f;
        float r = 0.16277136f / (o * o * o);
        float a = inputScale * 0.98f * (landSpeed * r);
        return a / (1 - (double)o);
    }

    [Fact]
    public void Profile_Seam_Dispatches_To_1122()
    {
        var w = World();
        Assert.Same(Release1122PhysicsRules.Instance, ((IBlockAccess)w).Physics);
        Assert.Same(Beta181PhysicsRules.Instance, ((IBlockAccess)TestWorld.Create()).Physics);
        Assert.Same(Release1122PhysicsRules.Instance, new BlockOverlay(w).Physics);
        Assert.Equal(256, new BlockOverlay(w).WorldHeight);
        Assert.Equal(0.6, Release1122PhysicsRules.Instance.StepHeight, 6);
        Assert.Equal(1.65, Release1122PhysicsRules.Instance.SneakHeight, 6);
    }

    [Theory]
    [InlineData(false, false, 0.215859)] // walk 4.317 m/s
    [InlineData(true, false, 0.280617)]  // sprint 5.612 m/s
    [InlineData(false, true, 0.064758)]  // sneak 1.295 m/s
    public void Ground_Speeds_Match_Source_And_Published(bool sprint, bool sneak, double published)
    {
        var w = Floor();
        var s = Standing(-15.5, 64, 0.5, East);
        var input = new MoveInput(Forward: 1, Sprint: sprint, Sneak: sneak, Yaw: East);
        Run(w, s, input, 60);
        double x0 = s.Position.X;
        PlayerPhysics.Tick(w, s, input);
        double d = s.Position.X - x0;
        double expected = GroundTerminal(sneak ? (float)(1 * 0.3) : 1f, Physics1122.LandSpeed(sprint));
        Assert.Equal(expected, d, 9);
        Assert.Equal(published, d, 5);
        Assert.Equal(sprint, s.Sprinting);
        Assert.Equal(64, s.Position.Y, 9);
    }

    [Fact]
    public void Sprint_Speed_Lags_One_Tick()
    {
        // EntityPlayer.onLivingUpdate sets the land speed after travel: the first sprinting tick still uses 0.1.
        var w = Floor();
        var s = Standing(0.5, 64, 0.5, East);
        PlayerPhysics.Tick(w, s, new MoveInput(Forward: 1, Sprint: true, Yaw: East));
        Assert.True(s.Sprinting);
        float o = 0.6f * 0.91f;
        float a = 0.98f * (0.1f * (0.16277136f / (o * o * o)));
        Assert.Equal(a, s.Position.X - 0.5, 9);
        Assert.Equal(Physics1122.LandSpeed(true), s.LandSpeed);
    }

    [Fact]
    public void Jump_Arc_Matches_Source()
    {
        var w = Floor();
        var s = Standing(0.5, 64, 0.5);
        s.Velocity = new Vec3d(0, -0.0784000015258789, 0);
        double vy = 0.41999998688697815, y = 64, peak = 64;
        var expected = new List<double>();
        for (int t = 0; t < 30 && (t == 0 || y > 64); t++)
        {
            y = Math.Max(64, y + vy);
            expected.Add(y);
            peak = Math.Max(peak, y);
            vy = (vy - 0.08) * 0.9800000190734863;
        }
        var got = new List<double>();
        PlayerPhysics.Tick(w, s, new MoveInput(Jump: true));
        got.Add(s.Position.Y);
        while (!s.OnGround && got.Count < 30) { PlayerPhysics.Tick(w, s, new MoveInput()); got.Add(s.Position.Y); }
        Assert.Equal(expected.Count, got.Count);
        for (int i = 0; i < got.Count; i++) Assert.Equal(expected[i], got[i], 9);
        Assert.Equal(1.2522, peak - 64, 4);
        Assert.Equal(12, got.Count);
    }

    [Fact]
    public void Held_Jump_Under_A_Low_Ceiling_Waits_Ten_Ticks()
    {
        // 2-high room: the head hits after one tick, but jumpTicks = 10 blocks the next jump (EntityLivingBase.onLivingUpdate).
        var w = Floor();
        w.Fill(-3, 66, -3, 3, 66, 3, Stone);
        var s = Standing(0.5, 64, 0.5);
        var takeoffs = new List<int>();
        for (int t = 0; t < 40; t++)
        {
            bool ground = s.OnGround;
            PlayerPhysics.Tick(w, s, new MoveInput(Jump: true));
            if (ground && s.Position.Y > 64) takeoffs.Add(t);
        }
        Assert.True(takeoffs.Count >= 3);
        for (int i = 1; i < takeoffs.Count; i++) Assert.Equal(10, takeoffs[i] - takeoffs[i - 1]);
    }

    [Fact]
    public void Sprint_Jump_Boost_Uses_The_Sine_Table()
    {
        var w = Floor();
        var s = Standing(-10.5, 64, 0.5, East);
        var run = new MoveInput(Forward: 1, Sprint: true, Yaw: East);
        Run(w, s, run, 30);
        double vx = s.Velocity.X, x0 = s.Position.X;
        PlayerPhysics.Tick(w, s, run with { Jump = true });
        Assert.Equal(0.41999998688697815, s.Position.Y - 64, 9);
        // jump(): motionX −= sin(yaw · 0.017453292F) · 0.2F; still on the ground for this travel: + the sprint land acceleration.
        float f = East * 0.017453292f;
        float o = 0.6f * 0.91f;
        float a = 0.98f * (Physics1122.LandSpeed(true) * (0.16277136f / (o * o * o)));
        Assert.Equal(vx - Physics1122.Sin(f) * 0.2f + a, s.Position.X - x0, 9);
        Assert.False(s.OnGround);
    }

    [Theory]
    [InlineData(Slab, 0, true)]       // 0.5: step
    [InlineData(Bed, 0, true)]        // 0.5625: steps in 1.12.2 (step 0.6), not in b1.8.1 (0.5)
    [InlineData(SnowLayer, 4, true)]  // 5 layers → collision 4/8 = 0.5
    [InlineData(SoulSand, 0, false)]  // 0.875: needs a jump
    [InlineData(Stone, 0, false)]
    public void Step_Height_Is_0_6(int id, int meta, bool steps)
    {
        var w = Floor();
        w.Fill(3, 64, -3, 3, 64, 3, id, meta);
        var s = Standing(0.5, 64, 0.5, East);
        Run(w, s, new MoveInput(Forward: 1, Yaw: East), 40);
        Assert.Equal(steps, s.Position.X > 3.2);
    }

    [Theory]
    [InlineData(Fence, 0)]
    [InlineData(Wall, 0)]
    public void Fences_And_Walls_Are_Not_Jumpable(int id, int meta)
    {
        var w = Floor();
        w.Fill(3, 64, -5, 3, 64, 5, id, meta);
        var s = Standing(0.5, 64, 0.5, East);
        Run(w, s, new MoveInput(Forward: 1, Jump: true, Sprint: true, Yaw: East), 80);
        Assert.True(s.Position.X < 3.1);   // pressed against the post / thin wall run (x 3.375 / 3.3125), never over it
    }

    [Fact]
    public void Sneak_Edge_Holds_Over_A_Drop_Deeper_Than_The_Step()
    {
        // Entity.move probes the box offset by −stepHeight (0.6): a 0.75 drop (snow layer meta 2 = 0.25) is an edge.
        var w = World();
        w.Fill(-10, 63, -3, 0, 63, 3, Stone);
        w.Fill(1, 62, -3, 6, 62, 3, Stone);
        w.Fill(1, 63, -3, 6, 63, 3, SnowLayer, 2);
        var s = Standing(-1.5, 64, 0.5, East);
        Run(w, s, new MoveInput(Forward: 1, Sneak: true, Yaw: East), 80);
        Assert.True(s.Position.X < 1.3 && s.Position.X > 0.6);
        Assert.Equal(64, s.Position.Y, 9);
        Assert.Equal(1.65f, (float)s.Height);
        Assert.Equal(1.54f, (float)s.EyeHeight);
    }

    [Fact]
    public void Sneak_Walks_Down_A_Half_Step()
    {
        var w = World();
        w.Fill(-10, 63, -3, 0, 63, 3, Stone);
        w.Fill(1, 63, -3, 6, 63, 3, Slab);
        var s = Standing(-1.5, 64, 0.5, East);
        Run(w, s, new MoveInput(Forward: 1, Sneak: true, Yaw: East), 80);
        Assert.True(s.Position.X > 2);
        Assert.Equal(63.5, s.Position.Y, 6);
    }

    [Fact]
    public void Sneaking_Box_Fits_A_1_75_Gap_And_Stays_Low_Inside()
    {
        // Carpet floor (0.0625) under a top trapdoor (0.8125..1): 1.75 of room. updateSize shrinks the box to 1.65 only when it fits
        // and grows it back only when 1.8 fits.
        var w = World();
        w.Fill(-10, 63, -3, 10, 63, 3, Stone);
        w.Fill(2, 64, -3, 8, 64, 3, Carpet);
        w.Fill(2, 65, -3, 8, 65, 3, Trapdoor, 8);
        var standing = Standing(-0.5, 64, 0.5, East);
        Run(w, standing, new MoveInput(Forward: 1, Yaw: East), 60);
        Assert.True(standing.Position.X < 2);
        var sneak = Standing(-0.5, 64, 0.5, East);
        Run(w, sneak, new MoveInput(Forward: 1, Sneak: true, Yaw: East), 120);
        Assert.True(sneak.Position.X > 3);
        Run(w, sneak, new MoveInput(Yaw: East), 5);
        Assert.False(sneak.Sneaking);
        Assert.Equal(1.65f, (float)sneak.Height);
    }

    [Fact]
    public void Ladder_Climb_And_Descent_Speeds()
    {
        var w = Floor();
        w.Fill(1, 64, 0, 1, 80, 0, Stone);
        w.Fill(0, 64, 0, 0, 80, 0, Ladder, 4); // facing west, on the east wall of its cell
        var s = Standing(0.5, 64, 0.5, East);
        Run(w, s, new MoveInput(Forward: 1, Yaw: East), 20);
        double y0 = s.Position.Y;
        PlayerPhysics.Tick(w, s, new MoveInput(Forward: 1, Yaw: East));
        Assert.Equal((0.2 - 0.08) * 0.9800000190734863, s.Position.Y - y0, 9); // 2.35 m/s
        // Let go: falls at the −0.15 clamp (3 m/s).
        Run(w, s, new MoveInput(Yaw: East), 10);
        double y1 = s.Position.Y;
        PlayerPhysics.Tick(w, s, new MoveInput(Yaw: East));
        Assert.Equal(-0.15, s.Position.Y - y1, 9);
        Assert.Equal(0.15, s.FallDistance, 6); // the clamp zeroes it, the move adds this tick's drop
        // Sneak holds in place.
        Run(w, s, new MoveInput(Sneak: true, Yaw: East), 3);
        double y2 = s.Position.Y;
        Run(w, s, new MoveInput(Sneak: true, Yaw: East), 10);
        Assert.Equal(y2, s.Position.Y, 9);
    }

    [Fact]
    public void Vines_And_A_Trapdoor_Over_A_Ladder_Are_Climbable()
    {
        var w = Floor();
        w.Fill(1, 64, 0, 1, 80, 0, Stone);
        w.Fill(0, 64, 0, 0, 70, 0, Vine, 8);
        var s = Standing(0.5, 64, 0.5, East);
        Run(w, s, new MoveInput(Forward: 1, Yaw: East), 30);
        Assert.True(s.Position.Y > 66);
        Assert.True(Shapes1122.IsClimbable(w, 0, 66, 0));

        w.Set(5, 64, 5, Ladder, 2);      // facing north
        w.Set(5, 65, 5, Trapdoor, 4);    // open, facing north (meta & 3 = 0)
        Assert.True(Shapes1122.IsClimbable(w, 5, 65, 5));
        w.Set(5, 65, 5, Trapdoor, 4 | 1); // facing south: no
        Assert.False(Shapes1122.IsClimbable(w, 5, 65, 5));
        Assert.False(Beta181PhysicsRules.Instance.IsClimbable(TestWorld.Create(), 0, 0, 0));
    }

    [Fact]
    public void Swimming_Speeds()
    {
        var w = Floor();
        w.Fill(-20, 64, -5, 20, 90, 5, Water);
        var s = Standing(-15.5, 64, 0.5, East);
        s.OnGround = false;
        // Hold jump: v ← (v + 0.04)·0.8 − 0.02 → terminal rise 0.1 b/t.
        var up = new MoveInput(Jump: true, Yaw: East);
        Run(w, s, up, 60);
        double y0 = s.Position.Y;
        PlayerPhysics.Tick(w, s, up);
        Assert.True(s.InWater);
        Assert.Equal(0.1, s.Position.Y - y0, 5);
        // Forward while holding jump: a = 0.02 · 0.98 per tick, ×0.8F → 0.098 b/t horizontally (1.96 m/s).
        var swim = new MoveInput(Forward: 1, Jump: true, Yaw: East);
        Run(w, s, swim, 45);
        double x0 = s.Position.X;
        PlayerPhysics.Tick(w, s, swim);
        Assert.Equal(0.02f * 0.98f / (1 - 0.800000011920929), s.Position.X - x0, 5);
    }

    [Fact]
    public void Soul_Sand_And_Ice_Speeds()
    {
        // Soul sand: doBlockCollisions multiplies motion ×0.4 once per soul sand block in the box (shrunk 0.001) after the move,
        // then the 0.546 friction. Reference: d = v + a; x += d; v = d · 0.4^cells · o.
        var w = Floor(SoulSand);
        var s = Standing(-15.5, 63.875, 0.5, East);
        var walk = new MoveInput(Forward: 1, Yaw: East);
        Run(w, s, new MoveInput(Yaw: East), 3); // settle: a teleport leaves motionY 0, so the first tick isn't on the ground
        Assert.True(s.OnGround);
        float o = 0.6f * 0.91f;
        float a = 0.98f * (0.1f * (0.16277136f / (o * o * o)));
        double x = -15.5, v = 0;
        for (int k = 0; k < 60; k++)
        {
            PlayerPhysics.Tick(w, s, walk);
            double d = v + a;
            x += d;
            int cells = (int)Math.Floor(x + 0.3 - 0.001) - (int)Math.Floor(x - 0.3 + 0.001) + 1;
            v = d * Math.Pow(0.4, cells) * o;
            Assert.True(Math.Abs(x - s.Position.X) < 1e-6, $"tick {k}: ref {x} got {s.Position.X} v {s.Velocity.X} ref v {v}");
        }
        Assert.InRange(s.Position.X + 15.5, 60 * 0.09, 60 * 0.13); // well under walking (0.216 b/t)

        var ice = Floor(Ice);
        var t = Standing(-19.5, 64, 0.5, East);
        Run(ice, t, walk, 200);
        double x1 = t.Position.X;
        PlayerPhysics.Tick(ice, t, walk);
        Assert.Equal(GroundTerminal(1f, 0.1f, 0.98f), t.Position.X - x1, 6);
    }

    [Fact]
    public void Slime_Bounces_And_Cancels_Fall_Damage()
    {
        var w = Floor(Slime);
        var s = Standing(0.5, 74, 0.5);
        s.OnGround = false;
        double vBefore = 0;
        for (int t = 0; t < 40 && !s.OnGround; t++) { vBefore = s.Velocity.Y; PlayerPhysics.Tick(w, s, new MoveInput()); }
        Assert.True(s.OnGround);
        Assert.Equal(0, s.FallDamage);
        Assert.True(s.Velocity.Y > 0.5); // onLanded: motionY = −motionY, then gravity
        PlayerPhysics.Tick(w, s, new MoveInput());
        Assert.True(s.Position.Y > 64.3);
    }

    [Theory]
    [InlineData(Stone, 10, false, 7)]
    [InlineData(HayBale, 10, false, 2)]  // ceil(7 × 0.2)
    [InlineData(Bed, 10, false, 2)]      // distance halved: ceil(5.x − 3)
    [InlineData(Slime, 10, true, 7)]     // sneaking onto slime takes the damage
    public void Fall_Damage(int id, int height, bool sneak, int expected)
    {
        var w = World();
        w.Fill(-5, 63, -5, 5, 63, 5, id);
        double top = id == Bed ? 63.5625 : 64;
        var s = Standing(0.5, top + height, 0.5);
        s.OnGround = false;
        int dmg = 0;
        for (int t = 0; t < 60 && !s.OnGround; t++) { PlayerPhysics.Tick(w, s, new MoveInput(Sneak: sneak)); dmg = Math.Max(dmg, s.FallDamage); }
        Assert.Equal(expected, dmg);
    }

    [Fact]
    public void Web_Slows_To_A_Crawl()
    {
        var w = Floor();
        w.Fill(2, 64, -2, 6, 65, 2, 30);
        var s = Standing(0.5, 64, 0.5, East);
        Run(w, s, new MoveInput(Forward: 1, Yaw: East), 60);
        Assert.True(s.Position.X > 1.5 && s.Position.X < 3.5);
    }
}
