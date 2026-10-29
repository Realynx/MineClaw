using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-27/28: hostiles the bot couldn't get to held the body as "busy: fighting ..." with 0 hits, over and over: a creeper
/// across a void gap restarted a 20 s fight every 200 ms and cancelled every walk north (420), a spider by kiwi failed main's
/// gen job, skeletons on ledges at 4-11 m failed every bot's jobs at night. A reflex fight engages only what the bot can get
/// to, gives up after ZeroHitGiveUpTicks with 0 hits, and a mob it gave up on re-triggers only when it becomes reachable.
/// </summary>
public class UnreachableThreatTests
{
    private static CombatOptions Opts() => new() { TelemetryDirectory = null, Crits = false, Block = false };

    /// <summary>The rig platform with a void channel x = 4..6 (all z): the bot's side x ≤ 3, the far side x ≥ 7.</summary>
    private static CombatRig Gap(int fromX = 4, int toX = 6)
    {
        var r = new CombatRig(Opts(), half: 12);
        for (int x = fromX; x <= toX; x++)
        for (int z = -12; z <= 12; z++)
            r.W.SetBlock(x, 63, z, Blocks.Air, 0);
        return r;
    }

    private static int Starts(CombatRig r) => r.Events.Count(e => e.Kind == CombatEventKind.FightStarted);

    [Fact]
    public void Creeper_across_a_gap_never_starts_a_fight_or_cancels_the_walk()
    {
        // 22:09-22:10: "fight started: creeper 6m away (creeper close)" ×5, each "couldn't reach creeper safely, 0 hits".
        var r = Gap();
        // The creeper stands on the far side of a 3-wide void channel, 6.7 m away: the bot can get no closer than 4 m.
        r.Mob(9, MobTypes.Creeper, 7.2, 0.5, new Dictionary<int, object> { [16] = (sbyte)-1 });
        bool aborted = false;
        for (int i = 0; i < 600; i++) aborted |= r.Step(physics: true).AbortAction;
        Assert.Equal(0, Starts(r));
        Assert.False(aborted);
        Assert.False(r.E.Engaged);
    }

    [Fact]
    public void Creeper_trapped_in_a_pocket_close_by_is_given_up_once_not_every_8_seconds()
    {
        // 10-28 17:20: "fight started: creeper 3m away (creeper close)" → "couldn't reach creeper (0 hits in 8s), left it
        // alone" → again 200 ms later, for minutes: main frozen west of the tower.
        var r = new CombatRig(Opts(), half: 12);
        for (int x = 2; x <= 4; x++)
        for (int z = -1; z <= 1; z++)
        for (int y = 64; y <= 66; y++)
            if (x != 3 || z != 0 || y == 66) r.W.SetBlock(x, y, z, Blocks.Stone, 0);
        r.Mob(9, MobTypes.Creeper, 3.5, 0.5, new Dictionary<int, object> { [16] = (sbyte)-1 });
        int engaged = 0;
        for (int i = 0; i < 1200; i++) { r.Step(physics: true); if (r.E.Engaged) engaged++; }
        Assert.InRange(Starts(r), 0, 1);
        Assert.True(engaged < 300, $"engaged {engaged}/1200 ticks");
    }

    [Fact]
    public void Creeper_within_its_ignite_range_still_gets_the_reflex()
    {
        var r = Gap(fromX: 3, toX: 3);
        r.Mob(9, MobTypes.Creeper, 3.5, 2.5, new Dictionary<int, object> { [16] = (sbyte)-1 });
        r.W.SetBlock(3, 63, 2, Blocks.Stone, 0);
        r.W.Entities.Teleport(9, new Vec3d(2.6, 64, 2.6), 0, 0);
        for (int i = 0; i < 10; i++) r.Step();
        Assert.True(r.E.Engaged);
    }

    [Fact]
    public void Guard_fight_with_zero_hits_gives_up_and_does_not_retrigger_until_reachable()
    {
        // 10-28 00:07:52 "fight started: spider 13m away (guarding kiwi)" → main's gen job "busy: fighting spider 13.1m
        // (guarding kiwi): 0 hits" for 20 s, then the same spider again.
        // The spider is in a 2-high stone pen: the bot can walk up to it but never hit it.
        var r = new CombatRig(Opts(), half: 12);
        for (int x = 8; x <= 11; x++)
        for (int z = -1; z <= 2; z++)
            if (x is 8 or 11 || z is -1 or 2) { r.W.SetBlock(x, 64, z, Blocks.Stone, 0); r.W.SetBlock(x, 65, z, Blocks.Stone, 0); }
        r.W.Time = 18000; // night: spiders are hostile
        r.Player(77, "kiwi", 9.5, 4.5);
        var spider = r.Mob(9, MobTypes.Spider, 9.5, 0.5);
        int startTick = -1, endTick = -1;
        for (int i = 0; i < 400 && endTick < 0; i++)
        {
            r.Step(physics: true);
            if (startTick < 0 && r.E.Engaged) startTick = i;
            if (startTick >= 0 && !r.E.Engaged) endTick = i;
        }
        Assert.True(startTick >= 0, "the guard reflex never engaged");
        Assert.True(endTick > 0, "the guard fight never gave up");
        Assert.InRange(endTick - startTick, 100, new CombatOptions().ZeroHitGiveUpTicks + 20);
        Assert.Contains(r.Events, e => e.Kind == CombatEventKind.FightEnded && e.Message.Contains("couldn't reach spider"));
        Assert.Contains(spider.Id, r.E.GaveUpOn);
        Assert.False(r.W.IsPlayerOverVoid(r.P.Position));

        // The spider wanders on its own island: no new fight, no aborted job.
        int before = Starts(r);
        bool aborted = false;
        for (int i = 0; i < 600; i++)
        {
            r.W.Entities.Teleport(spider.Id, new Vec3d(9.5 + (i / 50 % 2) * 0.6, 64, 0.5 + (i / 70 % 2) * 0.6), 0, 0);
            aborted |= r.Step(physics: true).AbortAction;
        }
        Assert.Equal(before, Starts(r));
        Assert.False(aborted);

        // It comes over to the bot (within melee reach): reachable now, the reflex fights it.
        r.W.Entities.Teleport(spider.Id, new Vec3d(r.P.Position.X + 1.6, 64, r.P.Position.Z), 0, 0);
        for (int i = 0; i < 10; i++) r.Step();
        Assert.True(r.E.Engaged);
        Assert.Contains(r.Events, e => e.Message.Contains("spider") && e.Message.Contains("in reach"));
    }

    [Fact]
    public void Skeleton_in_sight_at_range_is_left_alone()
    {
        // 10-28 night, main: "goto failed 2x: busy: fighting skeleton 11.4m (skeleton in sight): 0 hits".
        var r = new CombatRig(Opts(), half: 14);
        r.Mob(9, MobTypes.Skeleton, 11.5, 0.5);
        bool aborted = false;
        for (int i = 0; i < 200; i++) aborted |= r.Step().AbortAction;
        Assert.False(r.E.Engaged);
        Assert.False(aborted);
    }

    [Fact]
    public void Skeleton_close_but_across_a_gap_is_left_alone()
    {
        // 69: "generator dug 0/1: busy: fighting skeleton 3.9m".
        var r = Gap(fromX: 2, toX: 4);
        r.Mob(9, MobTypes.Skeleton, 5.5, 0.5);
        for (int i = 0; i < 200; i++) r.Step();
        Assert.Equal(0, Starts(r));
    }

    [Fact]
    public void Skeleton_close_and_reachable_is_still_fought()
    {
        var r = new CombatRig(Opts());
        r.Mob(9, MobTypes.Skeleton, 4.5, 0.5);
        for (int i = 0; i < 10; i++) r.Step();
        Assert.True(r.E.Engaged);
    }

    [Fact]
    public void Unreachable_skeleton_shooting_the_bot_sends_it_into_cover_briefly_then_it_keeps_working()
    {
        var r = Gap(fromX: 3, toX: 8);
        // A wall stub to hide behind on the bot's side.
        r.W.SetBlock(-2, 64, 2, Blocks.Stone, 0);
        r.W.SetBlock(-2, 65, 2, Blocks.Stone, 0);
        var sk = r.Mob(9, MobTypes.Skeleton, 10.5, 0.5);
        for (int i = 0; i < 20; i++) r.Step(physics: true);
        Assert.False(r.E.Engaged);

        r.W.Player.SetHealth(15, 20, 5); // an arrow
        int maxEngaged = 0, run = 0;
        for (int i = 0; i < 200; i++)
        {
            r.Step(physics: true);
            run = r.E.Engaged ? run + 1 : 0;
            maxEngaged = Math.Max(maxEngaged, run);
        }
        Assert.Contains(r.Events, e => e.Kind == CombatEventKind.FightStarted && e.Message.Contains("taking cover"));
        Assert.Contains(r.Events, e => e.Kind == CombatEventKind.FightEnded && (e.Message.Contains("took cover") || e.Message.Contains("no cover")));
        Assert.InRange(maxEngaged, 1, new CombatOptions().CoverMaxTicks + 5);
        Assert.Contains(sk.Id, r.E.GaveUpOn);
        Assert.DoesNotContain(r.Events, e => e.Kind == CombatEventKind.FightStarted && e.Message.Contains("skeleton in sight"));
        Assert.False(r.W.IsPlayerOverVoid(r.P.Position));
    }

    /// <summary>A cave 4 under the rig's floor (y 59 ground, 60-62 air): the 63 stone layer is its roof and the bot's floor.</summary>
    private static Entity CaveMob(CombatRig r, int id, int type, double x, double z, Dictionary<int, object>? meta = null)
    {
        for (int cx = -12; cx <= 12; cx++)
        for (int cz = -12; cz <= 12; cz++)
            r.W.SetBlock(cx, 59, cz, Blocks.Stone, 0);
        return r.W.Entities.SpawnMob(id, type, new Vec3d(x, 60, z), 0, 0, meta);
    }

    [Fact]
    public void Creeper_in_a_cave_under_the_floor_is_no_danger_until_it_comes_up()
    {
        // 10-29 21:53-21:59: "unreachable creeper W of night_tunnel keeps interrupting all bodies", "i think it's down below the edge".
        var r = new CombatRig(Opts(), half: 12);
        var c = CaveMob(r, 9, MobTypes.Creeper, 2.5, 0.5, new Dictionary<int, object> { [16] = (sbyte)-1 });
        bool aborted = false;
        for (int i = 0; i < 600; i++)
        {
            // It wanders about down there, right under the bot.
            r.W.Entities.Teleport(c.Id, new Vec3d(0.5 + (i / 40 % 4) * 0.8, 60, 0.5 + (i / 60 % 3) * 0.8), 0, 0);
            aborted |= r.Step(physics: true).AbortAction;
        }
        Assert.Equal(0, Starts(r));
        Assert.False(aborted);

        // It finds a way up and stands next to the bot: the reflex is back.
        r.W.Entities.Teleport(c.Id, new Vec3d(r.P.Position.X + 2.5, 64, r.P.Position.Z), 0, 0);
        for (int i = 0; i < 20; i++) r.Step();
        Assert.True(r.E.Engaged);
    }

    [Fact]
    public void Zombie_in_a_cave_under_the_floor_starts_no_defence()
    {
        var r = new CombatRig(Opts(), half: 12);
        r.W.Time = 18000;
        CaveMob(r, 9, MobTypes.Zombie, 1.5, 0.5);
        bool aborted = false;
        for (int i = 0; i < 200; i++) aborted |= r.Step().AbortAction;
        Assert.Equal(0, Starts(r));
        Assert.False(aborted);
        Assert.Null(r.E.NearestThreat(r.P.Position, 16));
    }

    [Fact]
    public void Can_get_at_sees_through_open_air_and_its_own_level_but_not_through_the_floor()
    {
        var r = new CombatRig(Opts(), half: 12);
        var me = r.P.Position;
        var cave = CaveMob(r, 9, MobTypes.Zombie, 3.5, 0.5);
        Assert.False(CombatGeometry.CanGetAt(r.W, cave, me));
        // A hole in the floor over it: now it sees the bot.
        for (int x = 1; x <= 4; x++) r.W.SetBlock(x, 63, 0, Blocks.Air, 0);
        Assert.True(CombatGeometry.CanGetAt(r.W, cave, me));
        // On the bot's level behind a wall it still counts (it walks round).
        r.W.SetBlock(2, 64, 4, Blocks.Stone, 0);
        r.W.SetBlock(2, 65, 4, Blocks.Stone, 0);
        var behind = r.Mob(10, MobTypes.Zombie, 3.5, 5.5);
        Assert.True(CombatGeometry.CanGetAt(r.W, behind, me));
    }

    [Fact]
    public void Zombie_next_to_the_bot_is_still_fought_at_once()
    {
        var r = new CombatRig(Opts());
        r.Mob(9, MobTypes.Zombie, 2.5, 0.5);
        Assert.True(r.Step().AbortAction);
        Assert.True(r.E.Engaged);
        Assert.True(r.E.ReflexEngaged);
    }

    [Fact]
    public void Walks_keep_out_of_a_creepers_ignite_range_unless_the_goal_is_there()
    {
        var c = new MinecraftClient(new ClientOptions { Username = "maid_chan" });
        c.World.Player.Physics.Teleport(new Vec3d(0.5, 64, 0.5), 0, 0);
        c.World.Player.SyncFromPhysics();
        c.World.Entities.SpawnMob(9, MobTypes.Creeper, new Vec3d(10.5, 64, 0.5), 0, 0, null);
        var avoid = c.CreeperAvoidCells(new GoalBlock(20, 64, 0));
        Assert.NotNull(avoid);
        Assert.Contains(new BlockPos(10, 64, 0), avoid!);
        Assert.Contains(new BlockPos(8, 64, 0), avoid!);
        Assert.DoesNotContain(new BlockPos(5, 64, 0), avoid!);
        Assert.Null(c.CreeperAvoidCells(new GoalBlock(10, 64, 1)));
    }
}

/// <summary>Item 3: an enclosed generator cell says exactly what boxes it in.</summary>
public class EnclosedCellTextTests
{
    [Fact]
    public void Enclosure_names_every_side_and_the_roof()
    {
        var c = new MinecraftClient(new ClientOptions { Username = "maid_chan" });
        var w = c.World;
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -3; x <= 3; x++) for (int z = -3; z <= 3; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        var cell = new BlockPos(0, 64, 0);
        w.SetBlock(0, 64, 0, Blocks.Cobblestone, 0);
        w.SetBlock(0, 64, -1, Blocks.Water, 0);   // N
        w.SetBlock(0, 64, 1, Blocks.Lava, 0);     // S
        w.SetBlock(1, 64, 0, Blocks.Planks, 0);   // E
        w.SetBlock(1, 65, 0, Blocks.Planks, 0);
        w.SetBlock(-1, 63, 0, Blocks.Air, 0);     // W: a hole down to the void
        w.SetBlock(0, 65, 0, Blocks.Planks, 0);   // roof
        Assert.Equal("N water, S lava, E planks wall, W void, above: planks", c.EnclosureText(cell));
    }
}
