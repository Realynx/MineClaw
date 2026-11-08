using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryGuardZoneTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NetherView(FakeStoryWorld world) : IStoryWorld
    {
        public MineClaw.Data.Versioning.IGameData Data => world.Data;
        public long TimeOfDay => world.TimeOfDay;
        public int Height => world.Height;
        public int Dimension => -1;
        public (int Id, int Meta) Block(int x, int y, int z) => world.Block(x, y, z);
        public IReadOnlyList<StoryEntity> Entities() => world.Entities();
        public bool Loaded(int x, int z) => world.Loaded(x, z);
    }

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(int pawns = 1, TimeProvider? time = null)
    {
        var (c, h, w) = StoryRig.Make(pawns, time: time);
        c.Meta.Home = new P3(100, 64, 100); // keep the initial join watchdog from moving this distant fixture home
        foreach (var p in c.Pawns.Values)
        {
            foreach (var type in WorkTypes.All) p.Priorities[type] = type == WorkTypes.Guard ? 1 : 0;
            h.Lives[p.Name] = FakeStoryHost.Live(100, 64, 100);
        }
        Assert.Null(c.ZoneOp("add", "guard", Area.Of(100, 64, 100, 110, 64, 110)).Error);
        return (c, h, w);
    }

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();
    private static StoryEntity Mob(bool hostile = false, int id = 77) => new(id, "mob", hostile ? "zombie" : "cow", 106.5, 64, 100.5, Hostile: hostile);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_available_guard_can_attack_the_same_exact_mob_without_exclusive_leases(bool hostile)
    {
        var (c, h, w) = Rig(3);
        h.Lives[c.P(1)] = FakeStoryHost.Live(105, 64, 100);
        h.Lives[c.P(2)] = FakeStoryHost.Live(80, 64, 100);
        w.Ents.Add(Mob(hostile));
        for (int i = 0; i < 3; i++)
        {
            var job = c.Next(i);
            Assert.Equal("attack", Kind(job));
            Assert.Equal(77, job["target"]!.GetValue<int>());
            Assert.True(c.CurrentOf(c.P(i))!.GuardZone);
        }
        Assert.Empty(c.Leases());
        Assert.Empty(h.Permits);
    }

    [Fact]
    public void Guard_is_an_independent_overlay_and_uses_the_painted_cells_including_erased_holes()
    {
        var (c, _, w) = Rig();
        c.ZoneOp("add", "mine", Area.Of(100, 63, 100, 110, 63, 110));
        c.ZoneOp("remove", "guard", Area.Of(106, 0, 100, 106, 0, 100));
        Assert.Contains(c.Zones, z => z.Kind == "mine" && z.Contains(106, 100));
        Assert.Contains(c.Zones, z => z.Kind == "guard" && z.Contains(105, 100));
        w.Ents.Add(Mob());
        Assert.Null(c.GuardJob(c.Pawns[c.P()], c.Host.Live(c.P())));
        c.ZoneOp("add", "guard", Area.Of(106, 64, 100, 106, 64, 100));
        Assert.Equal("attack", Kind(c.Next()));
        Assert.Contains(c.State()["zones"]!.AsArray(), z => z!["kind"]!.GetValue<string>() == "guard");
    }

    [Theory]
    [InlineData("player")]
    [InlineData("item")]
    [InlineData("object")]
    public void Players_pawns_and_non_mob_entities_are_never_automatic_guard_targets(string kind)
    {
        var (c, h, w) = Rig();
        w.Ents.Add(Mob() with { Kind = kind, Name = c.P() });
        Assert.Null(c.GuardJob(c.Pawns[c.P()], h.Live(c.P())));
        Assert.Empty(h.Permits);
    }

    [Fact]
    public void Guards_approach_on_shared_sight_but_attack_only_after_their_own_sight_is_clear()
    {
        var (c, h, w) = Rig(2);
        h.Lives[c.P(1)] = FakeStoryHost.Live(106, 64, 104);
        w.Set(103, 64, 100, 1); w.Set(103, 65, 100, 1);
        w.Ents.Add(Mob());
        var approach = c.Next();
        Assert.Equal("go", Kind(approach));
        Assert.Null(approach["target"]);
        Assert.Equal(0, approach["dist"]!.GetValue<int>());
        Assert.Equal("attack", Kind(c.Next(1)));
        w.Set(103, 64, 100, 0); w.Set(103, 65, 100, 0);
        c.Tick();
        Assert.Contains(h.Interrupted, s => s.Contains("guard target now in sight"));
        Assert.Equal("attack", Kind(c.Next()));
        w.Set(103, 64, 100, 1); w.Set(103, 65, 100, 1);
        c.Tick();
        Assert.Equal("go", Kind(c.Next())); // the other guard still sees it
        h.Lives[c.P(1)] = PawnLive.Offline;
        c.Tick();
        Assert.Null(c.CurrentOf(c.P()));
        Assert.Null(c.GuardJob(c.Pawns[c.P()], h.Live(c.P())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Leaving_the_painted_cells_or_erasing_the_zone_stops_every_guard(bool erase)
    {
        var (c, h, w) = Rig(2);
        w.Ents.Add(Mob());
        var a = c.Next(); var b = c.Next(1);
        if (erase) c.ZoneOp("remove", null, null, c.Zones.Single().Id);
        else { w.Ents[0] = Mob() with { X = 111.5 }; c.Tick(); }
        Assert.Null(c.CurrentOf(c.P()));
        Assert.Null(c.CurrentOf(c.P(1)));
        Assert.False(c.Report(c.P(), Id(a), "done", null, null));
        Assert.False(c.Report(c.P(1), Id(b), "done", null, null));
        Assert.Equal(2, h.Interrupted.Count(s => s.Contains("guard target left the guard zone")));
    }

    [Fact]
    public void Stale_hidden_and_forbidden_targets_do_not_trigger_guards()
    {
        var clock = new Clock();
        var (c, h, w) = Rig(time: clock);
        w.Ents.Add(Mob() with { SeenAt = clock.Now - TimeSpan.FromMinutes(1) });
        Assert.Null(c.GuardJob(c.Pawns[c.P()], h.Live(c.P())));
        w.Ents[0] = Mob();
        w.Set(103, 64, 100, 1); w.Set(103, 65, 100, 1);
        Assert.Null(c.GuardJob(c.Pawns[c.P()], h.Live(c.P())));
        w.Set(103, 64, 100, 0); w.Set(103, 65, 100, 0);
        c.ZoneOp("add", "forbidden", Area.Of(106, 0, 100, 106, 127, 100));
        Assert.Null(c.GuardJob(c.Pawns[c.P()], h.Live(c.P())));
    }

    [Fact]
    public void A_failed_guard_rests_for_itself_without_reserving_the_mob_from_other_guards()
    {
        var clock = new Clock();
        var (c, _, w) = Rig(2, clock);
        w.Ents.Add(Mob());
        var job = c.Next();
        c.Report(c.P(), Id(job), "failed", "no path", null);
        Assert.Null(c.GuardJob(c.Pawns[c.P()], c.Host.Live(c.P())));
        Assert.Equal("attack", Kind(c.Next(1)));
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.NotNull(c.GuardJob(c.Pawns[c.P()], c.Host.Live(c.P())));
    }

    [Theory]
    [InlineData(1, 3, false, true)]
    [InlineData(4, 1, false, false)]
    [InlineData(2, 2, false, false)]
    [InlineData(2, 2, true, true)]
    public void Guard_zones_preempt_lower_priority_work_and_dedicated_guards_but_respect_other_priorities(int guard, int mine, bool role, bool preempt)
    {
        var (c, h, w) = Rig();
        var p = c.Pawns[c.P()];
        p.Priorities[WorkTypes.Guard] = guard;
        p.Priorities[WorkTypes.Mine] = mine;
        if (role) p.Role = "guard";
        w.Set(102, 64, 103, 1);
        c.Designate("add", "mine", null, [new P3(102, 64, 103)], "all");
        var digging = c.Next();
        Assert.Equal("dig", Kind(digging));
        w.Ents.Add(Mob());
        c.Tick();
        Assert.Equal(preempt, h.Interrupted.Any(s => s.Contains("mob in guard zone")));
        if (preempt) Assert.Equal("attack", Kind(c.Next()));
        else Assert.Equal(Id(digging), c.CurrentOf(p.Name)!.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Disabled_or_drafted_guards_do_not_take_guard_zone_work(bool drafted)
    {
        var (c, h, w) = Rig();
        c.Pawns[c.P()].Drafted = drafted;
        if (!drafted) c.Pawns[c.P()].Priorities[WorkTypes.Guard] = 0;
        w.Ents.Add(Mob());
        Assert.Null(c.GuardJob(c.Pawns[c.P()], h.Live(c.P())));
    }

    [Theory]
    [InlineData("order")]
    [InlineData("need")]
    [InlineData("schedule")]
    public void Direct_orders_urgent_needs_and_off_duty_schedules_are_not_interrupted(string kind)
    {
        var (c, h, w) = Rig();
        var p = c.Pawns[c.P()];
        p.Role = "guard";
        if (kind == "order") p.Queue.Add(new Order { Id = "owner", Kind = "move", At = new P3(114, 64, 100) });
        if (kind == "need") h.Lives[p.Name] = FakeStoryHost.Live(100, 64, 100, food: 6, inv: new Stack(297, 0, 8));
        if (kind == "schedule") p.Schedule = Enumerable.Repeat(ScheduleBlocks.Free, 24).ToArray();
        var original = c.Next();
        Assert.False(c.CurrentOf(p.Name)!.GuardZone);
        w.Ents.Add(Mob());
        c.Tick();
        Assert.DoesNotContain(h.Interrupted, s => s.Contains("mob in guard zone"));
        Assert.Equal(Id(original), c.CurrentOf(p.Name)!.Id);
    }

    [Fact]
    public void Guard_columns_apply_at_any_height_without_needing_a_home_area()
    {
        var (c, h, w) = Rig();
        c.Meta.Home = null;
        h.Lives[c.P()] = FakeStoryHost.Live(100, 84, 100);
        w.Ents.Add(Mob() with { Y = 84 });
        Assert.Equal("attack", c.GuardJob(c.Pawns[c.P()], h.Live(c.P()))!.Kind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_guard_in_another_dimension_neither_responds_nor_supplies_shared_sight(bool ownView, bool hostile)
    {
        var (c, h, w) = Rig(2);
        h.Lives[c.P(1)] = h.Live(c.P(1)) with { Dim = -1 };
        if (ownView) h.Views[c.P(1)] = new NetherView(new FakeStoryWorld()); // clear ray in the wrong dimension is not sight
        w.Ents.Add(Mob(hostile));
        Assert.Null(c.GuardJob(c.Pawns[c.P(1)], h.Live(c.P(1)))); // no attack or approach, including the legacy home-guard fallback
        w.Set(103, 64, 100, 1); w.Set(103, 65, 100, 1);
        Assert.Null(c.GuardJob(c.Pawns[c.P()], h.Live(c.P()))); // no real observer in the target's dimension
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Crossing_dimensions_ends_an_active_guard_attack_or_approach(bool approaching)
    {
        var (c, h, w) = Rig(2);
        h.Lives[c.P(1)] = FakeStoryHost.Live(106, 64, 104);
        if (approaching) { w.Set(103, 64, 100, 1); w.Set(103, 65, 100, 1); }
        w.Ents.Add(Mob());
        Assert.Equal(approaching ? "go" : "attack", Kind(c.Next()));
        h.Lives[c.P()] = h.Live(c.P()) with { Dim = -1 };
        h.Views[c.P()] = new NetherView(new FakeStoryWorld());
        c.Tick();
        Assert.Null(c.CurrentOf(c.P()));
        Assert.Contains(h.Interrupted, s => s.Contains("guard target is in another dimension"));
    }
}
