using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;
using MineClaw.World.Versioning;

namespace MineClaw.Client.Tests;

/// <summary>
/// Story Mode's Fast / Faster (<see cref="SpeedBoost"/>, MinecraftClient.Speed.cs): the extra move per tick, its collisions and its
/// safety limits, the server's checks it must pass (the sim enforces vanilla's "moved too quickly" / "moved wrongly"), and the
/// step-down after rollbacks.
/// </summary>
public class SpeedBoostTests
{
    public static TheoryData<bool> Versions => new() { false, true };

    private sealed class Grid(bool release1122) : IBlockAccess
    {
        public readonly Dictionary<(int, int, int), int> Cells = new();
        public int GetBlock(int x, int y, int z) => Cells.GetValueOrDefault((x, y, z));
        public int GetMeta(int x, int y, int z) => 0;
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => Blocks.Get(id);
        public int WorldHeight => release1122 ? 256 : 128;
        public IPhysicsRules Physics => release1122 ? Release1122PhysicsRules.Instance : Beta181PhysicsRules.Instance;

        public Grid Floor(int x1, int x2, int z1, int z2, int y = 63, int id = Blocks.Stone)
        {
            for (int x = x1; x <= x2; x++)
            for (int z = z1; z <= z2; z++)
                Cells[(x, y, z)] = id;
            return this;
        }
    }

    /// <summary>Walks east (yaw −90) for <paramref name="ticks"/> ticks at <paramref name="factor"/>: positions after each tick and the
    /// plans applied.</summary>
    private static (List<Vec3d> Path, List<BoostPlan> Plans, PhysicsState S) Walk(Grid w, double factor, int ticks, bool sprint = true,
        double room = double.PositiveInfinity, Vec3d? start = null, bool sneak = false, bool jump = false)
    {
        var s = new PhysicsState { Position = start ?? new Vec3d(0.5, 64, 8.5), OnGround = true };
        var path = new List<Vec3d>();
        var plans = new List<BoostPlan>();
        for (int t = 0; t < ticks; t++)
        {
            var before = s.Position;
            bool ground = s.OnGround;
            w.Physics.Tick(w, s, new MoveInput(Forward: 1, Sprint: sprint, Sneak: sneak, Jump: jump, Yaw: -90));
            if (SpeedBoost.Plan(w, s, before, ground, factor, room) is { } plan)
            {
                SpeedBoost.Apply(w, s, plan);
                plans.Add(plan);
            }
            path.Add(s.Position);
        }
        return (path, plans, s);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Fast_and_faster_cover_two_and_three_times_the_ground(bool release1122)
    {
        var w = new Grid(release1122).Floor(-5, 200, 7, 10);
        double normal = Walk(w, 1, 60).S.Position.X;
        var fast = Walk(w, 2, 60);
        var faster = Walk(w, 3, 60);
        Assert.InRange((fast.S.Position.X - 0.5) / (normal - 0.5), 1.85, 2.05);
        Assert.InRange((faster.S.Position.X - 0.5) / (normal - 0.5), 2.75, 3.05);
        foreach (var run in new[] { fast, faster })
        {
            Assert.All(run.Path, p => Assert.Equal(64, p.Y, 6)); // on the ground all the way: no float, no sink
            Assert.True(run.S.OnGround);
            // Per packet: the normal tick's own move (≤ 0.29 sprinting) and segments of at most MaxPacketStep.
            Assert.All(run.Plans, pl => Assert.True(pl.Horizontal / pl.Segments <= SpeedBoost.MaxPacketStep + 1e-9));
            for (int i = 1; i < run.Path.Count; i++)
                Assert.True(run.Path[i].X - run.Path[i - 1].X <= SpeedBoost.MaxTickHorizontal);
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void A_wall_stops_the_extra_move_without_clipping(bool release1122)
    {
        var w = new Grid(release1122).Floor(-5, 30, 7, 10);
        for (int z = 7; z <= 10; z++) { w.Cells[(12, 64, z)] = Blocks.Stone; w.Cells[(12, 65, z)] = Blocks.Stone; }
        var run = Walk(w, 3, 60);
        Assert.All(run.Path, p => Assert.True(p.X <= 12 - 0.3 + 1e-9, $"clipped into the wall at {p.X}"));
        Assert.Equal(11.7, run.S.Position.X, 3);
        Assert.Equal(0, run.S.Velocity.X, 6);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void A_half_step_is_climbed_by_the_extra_move_like_the_server_does(bool release1122)
    {
        var w = new Grid(release1122).Floor(-5, 80, 7, 10);
        w.Floor(10, 80, 7, 10, y: 64, id: Blocks.Slab); // a slab step at x = 10
        var run = Walk(w, 2, 60);
        Assert.True(run.S.Position.X > 20, $"stopped at {run.S.Position.X}");
        Assert.True(Math.Abs(run.S.Position.Y - 64.5) < 1e-3, $"at {run.S.Position}");
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void It_slows_down_before_an_edge_and_never_leaves_the_body_over_the_drop(bool release1122)
    {
        var w = new Grid(release1122).Floor(-5, 10, 7, 10); // the floor ends at x = 11
        var s = new PhysicsState { Position = new Vec3d(9.6, 64, 8.5), OnGround = true, Velocity = new Vec3d(0.2, -0.0784, 0) };
        var before = s.Position;
        w.Physics.Tick(w, s, new MoveInput(Forward: 1, Yaw: -90));
        var plan = SpeedBoost.Plan(w, s, before, true, 3, double.PositiveInfinity);
        Assert.True(plan is null || plan.Value.Factor <= SpeedBoost.EdgeFactor, $"factor {plan?.Factor}");

        // Far from the edge it runs at full speed.
        s = new PhysicsState { Position = new Vec3d(2.5, 64, 8.5), OnGround = true, Velocity = new Vec3d(0.2, -0.0784, 0) };
        before = s.Position;
        w.Physics.Tick(w, s, new MoveInput(Forward: 1, Yaw: -90));
        var far = SpeedBoost.Plan(w, s, before, true, 3, double.PositiveInfinity);
        Assert.True(far is { Factor: 3 }, $"far from the edge: {far} at {s.Position} ground {s.OnGround}");

        // Walking at the edge every tick: each boosted move ends with ground within a step below.
        var run = Walk(w, 3, 40);
        Assert.Contains(run.Plans, pl => pl.Factor == SpeedBoost.EdgeFactor); // it did slow down on the way
        Assert.All(run.Path.Where(p => p.Y >= 63.99), p => Assert.True(p.X < 11.3 + 0.35, $"boosted past the edge to {p.X}"));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Water_ahead_slows_it_and_land_moves_never_end_in_a_liquid(bool release1122)
    {
        var w = new Grid(release1122).Floor(-5, 30, 7, 10);
        for (int z = 7; z <= 10; z++) w.Cells[(12, 64, z)] = BlockShapes.StillWater; // a puddle on the floor
        var s = new PhysicsState { Position = new Vec3d(10.9, 64, 8.5), OnGround = true, Velocity = new Vec3d(0.2, -0.0784, 0) };
        var before = s.Position;
        w.Physics.Tick(w, s, new MoveInput(Forward: 1, Yaw: -90));
        var plan = SpeedBoost.Plan(w, s, before, true, 3, double.PositiveInfinity);
        Assert.True(plan is null || plan.Value.Factor <= SpeedBoost.EdgeFactor);
        var run = Walk(w, 3, 30, start: new Vec3d(2.5, 64, 8.5));
        Assert.All(run.Plans, pl => Assert.True(pl.Mode != "ground" || pl.Factor <= 3));
        Assert.DoesNotContain(run.Plans, pl => pl.Mode == "swim" && pl.Factor > SpeedBoost.SwimCap);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Swimming_is_capped_and_air_sneak_and_lava_get_nothing(bool release1122)
    {
        var w = new Grid(release1122).Floor(-5, 30, 7, 10, y: 60);
        for (int x = -5; x <= 30; x++)
        for (int y = 61; y <= 64; y++)
        for (int z = 7; z <= 10; z++)
            w.Cells[(x, y, z)] = BlockShapes.StillWater;
        var swim = Walk(w, 3, 40, sprint: false, start: new Vec3d(0.5, 62, 8.5));
        Assert.NotEmpty(swim.Plans);
        Assert.All(swim.Plans, pl => { Assert.Equal("swim", pl.Mode); Assert.True(pl.Factor <= SpeedBoost.SwimCap); });

        var land = new Grid(release1122).Floor(-5, 30, 7, 10);
        Assert.Empty(Walk(land, 3, 30, sneak: true).Plans);                     // sneaking: vanilla's edge clamp is the point
        var hop = Walk(land, 3, 30, jump: true);                                 // jumping: never boosted in the air
        Assert.All(hop.Plans, pl => Assert.Equal("ground", pl.Mode));
        land.Cells[(4, 64, 6)] = BlockShapes.StillLava;                               // lava within a block of the way: nothing
        var s = new PhysicsState { Position = new Vec3d(3.5, 64, 7.5), OnGround = true, Velocity = new Vec3d(0.2, -0.0784, 0) };
        var before = s.Position;
        land.Physics.Tick(land, s, new MoveInput(Forward: 1, Yaw: -90));
        Assert.Null(SpeedBoost.Plan(land, s, before, true, 3, double.PositiveInfinity));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Ladders_climb_faster_but_capped_and_stay_on_the_ladder(bool release1122)
    {
        var w = new Grid(release1122).Floor(-5, 10, 7, 10);
        for (int y = 64; y <= 74; y++) { w.Cells[(3, y, 8)] = Blocks.Stone; w.Cells[(2, y, 8)] = Blocks.Ladder; }
        var s = new PhysicsState { Position = new Vec3d(2.5, 64, 8.5), OnGround = true };
        var plans = new List<BoostPlan>();
        double top = 0;
        for (int t = 0; t < 40; t++)
        {
            var before = s.Position;
            bool g = s.OnGround;
            w.Physics.Tick(w, s, new MoveInput(Forward: 1, Yaw: -90));
            if (SpeedBoost.Plan(w, s, before, g, 3, double.PositiveInfinity) is { } pl) { SpeedBoost.Apply(w, s, pl); plans.Add(pl); }
            top = Math.Max(top, s.Position.Y);
        }
        Assert.NotEmpty(plans);
        Assert.All(plans, pl => { Assert.Equal("ladder", pl.Mode); Assert.True(pl.Factor <= SpeedBoost.LadderCap); Assert.True(pl.Ey <= 0.2 * 0.5 + 1e-9); });
        Assert.True(top > 70, $"climbed only to {top}");
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Room_from_the_driver_limits_the_whole_tick(bool release1122)
    {
        var w = new Grid(release1122).Floor(-5, 200, 7, 10);
        var run = Walk(w, 3, 40, room: 0.4);
        for (int i = 1; i < run.Path.Count; i++)
            Assert.True(run.Path[i].X - run.Path[i - 1].X <= 0.4 + 1e-9 || run.Path[i].X - run.Path[i - 1].X <= 0.2806 + 1e-9);
        Assert.Empty(Walk(w, 3, 20, room: 0).Plans);
    }

    // ---------------------------------------------------------------- end to end on the fake server

    private static void Corridor(FakeServer srv)
    {
        for (int x = -15; x <= 17; x++)
        for (int z = 7; z <= 9; z++)
            srv.Blocks[(x, 63, z)] = Blocks.Stone;
        srv.SpawnAt = (-14.5, 64, 8.5);
        srv.MovementChecks = true;
        srv.ServerTickMs = 5; // the harness ticks the client every 5 ms: one server tick per client tick, as live
    }

    /// <summary>Walks the 30-block corridor at a multiplier: (client ticks from the first step to the far end, the server). The test
    /// harness ticks every 5 ms, so path planning would count for several ticks: the walk is timed from the first move.</summary>
    private static async Task<(long Ticks, FakeServer S, MinecraftClient C)> Run(bool release1122, double speed)
    {
        var (c, s) = await Harness.StartAsync(Corridor, release1122: release1122);
        c.SpeedMultiplier = speed;
        long started = -1, arrived = -1;
        bool stop = false;
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                long t = c.TickCount;
                double x = c.World.Player.Position.X;
                if (started < 0 && x > -14.45) started = t;
                if (arrived < 0 && x >= 15.5) arrived = t;
                Thread.Yield();
            }
        }) { IsBackground = true };
        sampler.Start();
        var r = await c.GotoAsync(16, 64, 8, options: new GotoOptions { AllowSprint = true });
        Volatile.Write(ref stop, true);
        sampler.Join();
        Assert.True(r.Ok, r.Message);
        Assert.True(started >= 0 && arrived > started, $"started {started}, arrived {arrived}");
        Assert.True(await Harness.WaitAsync(() => s.Accepted.LastOrDefault().X > 15.5));
        return (arrived - started, s, c);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task A_pawn_walks_thirty_blocks_two_to_three_times_faster_with_no_rollbacks(bool release1122)
    {
        var ticks = new Dictionary<double, long>();
        foreach (var speed in new[] { 1.0, 2.0, 3.0 })
        {
            var (t, s, c) = await Run(release1122, speed);
            await using var _ = c;
            using var __ = s;
            ticks[speed] = t;
            Assert.True(s.MovedTooQuickly == 0 && s.MovedWrongly == 0 && s.Rollbacks == 0,
                $"×{speed}: {s.MovedTooQuickly} too quickly, {s.MovedWrongly} wrongly, {s.Rollbacks} rollbacks: {s.LastRejection}");
            Assert.Equal(0, s.IllegalStance);
            Assert.False(s.Kicked);
            Assert.True(s.LargestStep <= SpeedBoost.MaxPacketStep + 0.35, $"a packet carried {s.LargestStep:0.00}");
            Assert.True(c.SpeedRollbacks == 0, $"×{speed}: client counted {c.SpeedRollbacks} rollbacks, {c.PositionCorrections} corrections; server {s.Rollbacks} rollbacks");
            var end = s.Accepted.Last();
            Assert.InRange(end.X, 15.5, 17); // the server agrees where it ended
        }
        double fast = (double)ticks[1.0] / ticks[2.0], faster = (double)ticks[1.0] / ticks[3.0];
        Console.WriteLine($"{(release1122 ? "1.12.2" : "b1.8.1")}: normal {ticks[1.0]} ticks, fast {ticks[2.0]} ({fast:0.00}×), faster {ticks[3.0]} ({faster:0.00}×)");
        Assert.True(fast >= 1.8, $"fast only {fast:0.00}× ({ticks[1.0]} vs {ticks[2.0]} ticks)");
        Assert.True(faster >= 2.5, $"faster only {faster:0.00}× ({ticks[1.0]} vs {ticks[3.0]} ticks)");
        Assert.True(faster > fast);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Repeated_rollbacks_step_the_boost_down_once_and_never_kick(bool release1122)
    {
        // A stricter server (a plugin): any packet stepping over 0.4 blocks is teleported back.
        (double X, double Y, double Z)? last = null;
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Corridor(srv);
            srv.MovementChecks = false;
            srv.Hook = p =>
            {
                (double X, double Y, double Z)? at = p switch
                {
                    PlayerPositionPacket pp => (pp.X, pp.Y, pp.Z),
                    PlayerPositionLookPacket pl => (pl.X, pl.Y, pl.Z),
                    _ => null,
                };
                if (at is not { } a) return false;
                if (last is { } l && Math.Sqrt((a.X - l.X) * (a.X - l.X) + (a.Z - l.Z) * (a.Z - l.Z)) > 0.4)
                {
                    srv.Send(new PlayerPositionLookPacket { X = l.X, Y = l.Y, Stance = l.Y + 1.62, Z = l.Z, Yaw = -90, Pitch = 0, OnGround = true });
                    return true;
                }
                last = a;
                return false;
            };
        }, release1122: release1122);
        await using var _ = c;
        using var __ = s;
        var downs = new List<SpeedStepDown>();
        c.SpeedSteppedDown += d => { lock (downs) downs.Add(d); };
        c.SpeedMultiplier = 3;
        var r = await c.GotoAsync(16, 64, 8, options: new GotoOptions { AllowSprint = true });
        Assert.True(r.Ok, r.Message);
        Assert.True(await Harness.WaitAsync(() => { lock (downs) return downs.Count > 0; }));
        Assert.True(c.SpeedCap < 3, $"cap {c.SpeedCap}");
        Assert.True(c.SpeedRollbacks >= MinecraftClient.StepDownRollbacks);
        lock (downs) Assert.Equal(3, downs[0].From);
        Assert.Equal(3, c.SpeedMultiplier); // the request stands; the cap is what changed
        Assert.True(c.IsPlaying);
        // A new request clears the step-down.
        c.SpeedMultiplier = 2;
        Assert.Equal(3, c.SpeedCap);
    }

    [Fact]
    public async Task A_server_speed_effect_takes_its_share_and_the_client_adds_the_rest_on_1122()
    {
        var (t0, s0, c0) = await Run(true, 2);
        await using (c0) using (s0) { }
        var (c, s) = await Harness.StartAsync(Corridor, release1122: true);
        await using var _ = c;
        using var __ = s;
        c.SpeedMultiplier = 2;
        s.Send(new EntityEffectPacket { EntityId = FakeServer.EntityId, EffectId = PotionEffects.Speed, Amplifier = 1, Duration = 600 });
        Assert.True(await Harness.WaitAsync(() => c.SpeedNow.ServerFactor > 1));
        var now = c.SpeedNow;
        Assert.Equal(1.4, now.ServerFactor, 6);          // Speed II: +40 % on the movement attribute, applied by the physics tick
        Assert.Equal(2 / 1.4, now.Client, 6);            // the client adds only the rest
        Assert.Equal(2, now.Effective);
        // Still about as fast as the client-only Fast, still no rejections.
        long started = -1, arrived = -1;
        bool stop = false;
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                long t = c.TickCount;
                double x = c.World.Player.Position.X;
                if (started < 0 && x > -14.45) started = t;
                if (arrived < 0 && x >= 15.5) arrived = t;
                Thread.Yield();
            }
        }) { IsBackground = true };
        sampler.Start();
        var r = await c.GotoAsync(16, 64, 8, options: new GotoOptions { AllowSprint = true });
        Volatile.Write(ref stop, true);
        sampler.Join();
        Assert.True(r.Ok, r.Message);
        Assert.True(s.MovedTooQuickly == 0 && s.MovedWrongly == 0 && s.Rollbacks == 0, s.LastRejection);
        Assert.InRange((double)(arrived - started) / t0, 0.8, 1.25);
        // b1.8.1 has no effects: the server's factor stays 1 whatever the client tracks.
        var (cb, sb) = await Harness.StartAsync(Corridor);
        await using var ___ = cb;
        using var ____ = sb;
        Assert.Equal(1, cb.ServerSpeedFactor);
    }

    [Fact]
    public async Task Bodies_are_normal_speed_unless_asked()
    {
        var (c, s) = await Harness.StartAsync(Corridor);
        await using var _ = c;
        using var __ = s;
        Assert.Equal(1, c.SpeedMultiplier);
        Assert.Equal(new SpeedState(1, 1, 1, 1, 0), c.SpeedNow);

        var r = await c.GotoAsync(0, 64, 8, options: new GotoOptions { AllowSprint = true });
        Assert.True(r.Ok, r.Message);
        Assert.True(s.LargestStep < 0.3, $"a normal body stepped {s.LargestStep:0.00}"); // vanilla sprint ≈ 0.28 a tick
        c.SpeedMultiplier = 7;
        Assert.Equal(3, c.SpeedMultiplier); // clamped to Faster
        c.SpeedMultiplier = double.NaN;
        Assert.Equal(1, c.SpeedMultiplier);

    }
}
