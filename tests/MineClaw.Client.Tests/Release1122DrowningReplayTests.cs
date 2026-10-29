using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Replay of live 2026-10-29 01:56-01:59:08 (1.12.2, the first normal-world session). Fox: "mine some trees with 2 agents".
/// Main's delegate chopped three spruce trees by a ravine (154,68,278 / 148,66,281 / 147,63,286; "item … next to the void" at the
/// ravine's rim), a creeper fight below the rim stalled at 135.5,63,285.4, and 22 s later the body stood on the ravine lake's
/// gravel bed at 125,52,294 ("water 2738" around), head under, nothing pressing jump. 2 damage a second from 01:58:56 to the death
/// at 01:59:08 ("maid_chan drowned", 15 spruce logs lost); the only reflex line was "low health 8/20 …: work goes on".
/// The fixture: grass at y63, a lake 11 deep (water y53..63, gravel at y52) between the bot and a spruce trunk, the fake server
/// running the vanilla breath rule (air 300, −1 a tick with the eye in water, 2 damage at −20, our own metadata index 1).
/// </summary>
public class Release1122DrowningReplayTests
{
    private const int LakeX0 = 4, LakeX1 = 20, LakeZ0 = 2, LakeZ1 = 14;

    private static Task<(MinecraftClient C, FakeServer S)> Start(bool release1122 = true, (double, double, double)? spawn = null, bool lid = false,
        bool pocket = false) =>
        Harness.StartAsync(srv =>
        {
            for (int x = -16; x <= 31; x++)
            for (int z = -16; z <= 31; z++)
            {
                bool lake = x >= LakeX0 && x <= LakeX1 && z >= LakeZ0 && z <= LakeZ1;
                if (!lake)
                {
                    // Solid banks down to the bed (water beside air would flow out: the flow vector pushed the body sideways).
                    for (int y = 52; y <= 61; y++) srv.Blocks[(x, y, z)] = Blocks.Stone;
                    srv.Blocks[(x, 62, z)] = Blocks.Dirt;
                    srv.Blocks[(x, 63, z)] = Blocks.Grass;
                    continue;
                }
                srv.Blocks[(x, 52, z)] = Blocks.Gravel;
                for (int y = 53; y <= 63; y++) srv.Blocks[(x, y, z)] = Blocks.Water;
            }
            for (int y = 64; y <= 69; y++) { srv.Blocks[(24, y, 8)] = Blocks.Log; srv.Metas[(24, y, 8)] = 1; } // spruce, across the lake
            // A sealed 1×2 water pocket inside the bank at -10,53,-10 (no air anywhere near: the reflex can't surface).
            if (pocket) { srv.Blocks[(-10, 53, -10)] = Blocks.Water; srv.Blocks[(-10, 54, -10)] = Blocks.Water; }
            // A stone lid over the lake but its west end (x 4..5): under it the head is under water all the way.
            if (lid)
                for (int x = 6; x <= LakeX1; x++)
                for (int z = LakeZ0; z <= LakeZ1; z++)
                    srv.Blocks[(x, 63, z)] = Blocks.Stone;
            srv.SpawnAt = spawn ?? (1.5, 64, 8.5);
        }, release1122: release1122);

    [Fact]
    public async Task The_chop_job_across_the_lake_keeps_its_head_above_water_and_finishes()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start();
        await using var _ = c;
        using var __ = s;
        s.StartBreathSim();
        var r = await c.ChopTreeAsync(new BlockPos(24, 64, 8));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(0, s.DrownHits);
        Assert.Equal(20, s.LowestHealth);
        Assert.True(s.Dug.Count(d => d.Id == Blocks.Log) >= 6, $"dug {s.Dug.Count(d => d.Id == Blocks.Log)} logs: {r.Message}, at {c.World.Player.Position}");
    }

    [Fact]
    public async Task A_walk_along_the_lake_bed_is_stopped_by_the_reflex_which_surfaces_and_the_job_resumes_with_full_air()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        // The live failure: a walk that takes the body down to the bed and along it with the head under for longer than the air
        // lasts (the old planner had no breath limit or cost: MaxHeadUnderSteps 99, HeadUnderStepCost 0 replay it). Under a lid
        // (the open lake's surface would be swum on), so the reflex must find the nearest breathable cell: back at the open end.
        var (c, s) = await Start(lid: true);
        await using var _ = c;
        using var __ = s;
        var dangers = new System.Collections.Concurrent.ConcurrentQueue<DangerEvent>();
        c.Danger += d => dangers.Enqueue(d);
        s.StartBreathSim();
        var job = c.GotoAsync(new GoalBlock(new BlockPos(19, 53, 8)), GotoOptions.Default with { MaxHeadUnderSteps = 99, HeadUnderStepCost = 0, AllowPartial = false, Parkour = ParkourMode.Off, PlanTimeout = TimeSpan.FromSeconds(10) });

        // Air is tracked (the client's estimate, corrected by the server's metadata).
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Air < 250, 8000),
            $"air {c.World.Player.Air}, at {c.World.Player.Position}, job {(job.IsCompleted ? job.Result.Message : "running")}");
        Assert.Contains("air ", c.DescribeStatus());

        // The reflex: "drowning risk … surfacing", then "surfaced at …: air was N/10 bubbles"; never a drowning hit.
        Assert.True(await Harness.WaitAsync(() => dangers.Any(d => d.Kind == DangerKind.Recovered && d.Message.StartsWith("surfaced at")), 15000),
            $"no surfacing: air {c.World.Player.Air}, pos {c.World.Player.Position}, dangers: {string.Join(" | ", dangers.Select(d => d.Message))}");
        var risk = Assert.Single(dangers, d => d.Kind == DangerKind.Drowning);
        Assert.True(risk.Critical);
        Assert.Contains("bubbles", risk.Message);
        var first = await job;
        Assert.False(first.Ok); // the reflex stopped the bed walk
        Assert.Equal(0, s.DrownHits);
        Assert.True(c.World.Player.Position.Y >= 62, $"at {c.World.Player.Position}");

        // The job resumes with full air (the worker's retry, default planner): over the lid, to the far bank.
        var r = await c.GotoAsync(new GoalBlock(new BlockPos(22, 64, 8)));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(0, s.DrownHits);
        Assert.Equal(20, s.LowestHealth);
    }

    [Fact]
    public async Task Idle_on_the_lake_bed_the_body_swims_up_at_once_like_a_player_holding_jump()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start(spawn: (12.5, 53, 8.5));
        await using var _ = c;
        using var __ = s;
        s.StartBreathSim();
        Assert.True(await Harness.WaitAsync(() => !c.World.Player.HeadUnderWater && c.World.Player.Position.Y > 62, 8000),
            $"still under at {c.World.Player.Position}, air {c.World.Player.Air}");
        Assert.Equal(0, s.DrownHits);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Air == 300, 2000), $"air {c.World.Player.Air}");
    }

    [Fact]
    public void The_planner_never_holds_its_breath_longer_than_seven_steps_and_prefers_the_surface()
    {
        // A flooded corridor under a stone ceiling: 12 cells long, the only dry way round is long. The plan goes round.
        var w = new WorldState(physics: MineClaw.World.Versioning.Release1122PhysicsRules.Instance, height: 256);
        w.ApplyChunkRegion(-16, 0, -16, 64, 128, 64, new byte[64 * 128 * 64], null, null, null);
        for (int x = -10; x <= 30; x++)
        for (int z = -10; z <= 30; z++)
            w.SetBlock(x, 63, z, Blocks.Stone, 0);
        // The pool: x 2..13 at z 0, water y 64..65 with a stone lid at 66; walls at z ±1 for x 2..13.
        for (int x = 2; x <= 13; x++)
        {
            w.SetBlock(x, 64, 0, Blocks.Water, 0);
            w.SetBlock(x, 65, 0, Blocks.Water, 0);
            w.SetBlock(x, 66, 0, Blocks.Stone, 0);
            for (int y = 64; y <= 66; y++) { w.SetBlock(x, y, 1, Blocks.Stone, 0); w.SetBlock(x, y, -1, Blocks.Stone, 0); }
        }
        var direct = Pathfinder.FindPath(w, new BlockPos(1, 64, 0), new GoalBlock(new BlockPos(14, 64, 0)));
        Assert.True(direct.Success, direct.ToString());
        int run = 0, worst = 0;
        foreach (var st in direct.Steps)
        {
            run = Breath.HeadUnderAt(w, st.X, st.Floor, st.Z) ? run + 1 : 0;
            worst = Math.Max(worst, run);
        }
        Assert.True(worst <= 7, $"{worst} head-under steps in a row: {string.Join(" ", direct.Steps)}");
        Assert.DoesNotContain(direct.Steps, st => st.X is > 3 and < 12 && st.Z == 0); // went round, not through
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Air_comes_from_our_own_metadata_on_either_version(bool release1122)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start(release1122, spawn: (-9.5, 53, -9.5), pocket: true);
        await using var _ = c;
        using var __ = s;
        // The server says 120 (index 1: b1.8.1 short, 1.12.2 VarInt).
        var entry = release1122 ? MineClaw.Protocol.MetadataEntry.FromInt(EntityMeta.Air, 120) : MineClaw.Protocol.MetadataEntry.FromShort(EntityMeta.Air, 120);
        s.Send(new MineClaw.Protocol.Packets.EntityMetadataPacket { EntityId = FakeServer.EntityId, Metadata = new MineClaw.Protocol.EntityMetadata([entry]) });
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Air <= 120, 3000), $"air {c.World.Player.Air}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Damage_names_its_cause_drowning_under_water_and_a_mob_on_land_on_either_version(bool release1122)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start(release1122, spawn: (0.5, 64, 0.5));
        await using var _ = c;
        using var __ = s;
        var dangers = new System.Collections.Concurrent.ConcurrentQueue<DangerEvent>();
        c.Danger += d => dangers.Enqueue(d);

        // On land with a zombie in reach: a mob.
        s.SpawnMob(MobTypes.Zombie, 1.5, 64, 1.5);
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Nearby(c.World.Player.Position, 3).Count > 0));
        c.Combat.DefendSelf = false;
        s.SetVitals(17, 20);
        Assert.True(await Harness.WaitAsync(() => dangers.Any(d => d.Kind == DangerKind.Damage)));
        Assert.Contains("zombie attacking", dangers.First(d => d.Kind == DangerKind.Damage).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_damage_with_the_head_under_and_no_air_is_drowning_on_either_version(bool release1122)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start(release1122, spawn: (-9.5, 53, -9.5), pocket: true);
        await using var _ = c;
        using var __ = s;
        var entry = release1122 ? MineClaw.Protocol.MetadataEntry.FromInt(EntityMeta.Air, 0) : MineClaw.Protocol.MetadataEntry.FromShort(EntityMeta.Air, 0);
        s.Send(new MineClaw.Protocol.Packets.EntityMetadataPacket { EntityId = FakeServer.EntityId, Metadata = new MineClaw.Protocol.EntityMetadata([entry]) });
        Assert.True(await Harness.WaitAsync(() => c.World.Player.HeadUnderWater && c.World.Player.Air <= 0, 3000), $"air {c.World.Player.Air}");
        Assert.StartsWith("drowning", c.DamageCause(2));
    }

    [Fact]
    public void Breath_follows_the_vanilla_air_rule_and_the_eye_surface()
    {
        // 300 → −20 in 320 ticks, a 2-damage hit, 0, then every 20 ticks; out of water it is full at once.
        int air = Breath.MaxAir, hits = 0, firstHit = -1;
        for (int t = 1; t <= 360; t++)
        {
            var (a, hit) = Breath.Tick(air, eyeInWater: true);
            air = a;
            if (hit) { hits++; if (firstHit < 0) firstHit = t; }
        }
        Assert.Equal(320, firstHit);
        Assert.Equal(3, hits); // 320, 340, 360
        Assert.Equal(Breath.MaxAir, Breath.Tick(0, eyeInWater: false).Air);
        Assert.Equal(150, Breath.Tick(150, eyeInWater: true, waterBreathing: true).Air);
        Assert.Equal(10, Breath.Bubbles(300));
        Assert.Equal(4, Breath.Bubbles(110));
        Assert.Equal(0, Breath.Bubbles(0));
        Assert.StartsWith("air 4/10 bubbles", Breath.Text(110));

        // The eye vs the water surface: a source block is full (surface 8/9 up the block + 1/9 = the top), level 7 nearly empty.
        var w = new WorldState();
        w.ApplyChunkRegion(0, 0, 0, 16, 128, 16, new byte[16 * 128 * 16], null, null, null);
        w.SetBlock(1, 65, 1, Blocks.Water, 0);
        Assert.True(Breath.EyeInWater(w, new Vec3d(1.5, 65.9, 1.5)));
        w.SetBlock(1, 65, 1, Blocks.FlowingWater, 7); // getLiquidHeightPercent(7) = 8/9: the surface is 8/9 − 1/9 below the top (65.22)
        Assert.False(Breath.EyeInWater(w, new Vec3d(1.5, 65.5, 1.5)));
        Assert.True(Breath.EyeInWater(w, new Vec3d(1.5, 65.1, 1.5)));

        // The nearest breathable cell under a lid: back to the hole, along the route.
        for (int x = 0; x <= 10; x++)
        {
            w.SetBlock(x, 64, 5, Blocks.Water, 0);
            w.SetBlock(x, 65, 5, Blocks.Water, 0);
            w.SetBlock(x, 66, 5, x == 0 ? 0 : Blocks.Stone, 0);
            w.SetBlock(x, 63, 5, Blocks.Stone, 0);
            w.SetBlock(x, 64, 4, Blocks.Stone, 0); w.SetBlock(x, 65, 4, Blocks.Stone, 0);
            w.SetBlock(x, 64, 6, Blocks.Stone, 0); w.SetBlock(x, 65, 6, Blocks.Stone, 0);
        }
        w.SetBlock(11, 64, 5, Blocks.Stone, 0); w.SetBlock(11, 65, 5, Blocks.Stone, 0); // the far end is closed
        var found = Breath.NearestBreathable(w, new BlockPos(9, 64, 5));
        Assert.NotNull(found);
        Assert.Equal(0, found!.Value.Cell.X);
        Assert.True(found.Value.Steps >= 9);
        Assert.True(Breath.AirNeeded(found.Value.Steps) > Breath.AirNeeded(1));
    }
}
