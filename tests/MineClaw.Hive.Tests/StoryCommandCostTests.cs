using MineClaw.Agent.Rag;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// What a colony command costs under the lock, by counts rather than times: cancelling marks by id or by block among thousands,
/// and the settings that used to rewrite the whole colony inside the request (Pause, Move home, options, beds) and now leave it
/// to the next tick.
/// </summary>
public sealed class StoryCommandCostTests
{
    /// <summary>A clock that moves only when told: a save "at most every 5 s" never comes due by itself.</summary>
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The revision the store holds for the colony: it moves only when the colony is written.</summary>
    private static long SavedRev(AgentDb db, string id) => db.ScalarLong("SELECT rev FROM story_colonies WHERE id = $i", ("$i", id));

    /// <summary>One stone layer of a 64 x 64 square as mine blocks: 4096 cells, one add's limit.</summary>
    private static List<P3> Layer(int y) =>
        Enumerable.Range(0, 64 * 64).Select(i => new P3(i % 64, y, i / 64)).ToList();

    [Fact]
    public void Cancelling_by_id_removes_exactly_the_named_marks_among_8192()
    {
        var (c, _, _) = StoryRig.Make();
        Assert.Equal(4096, c.Designate("add", "mine", null, Layer(60), null).Result!["count"]!.GetValue<int>());
        Assert.Equal(4096, c.Designate("add", "mine", null, Layer(59), null).Result!["count"]!.GetValue<int>());
        Assert.Equal(8192, c.Designations.Count);
        long before = c.Rev;

        // Every other mark by id, each named twice, plus ids nobody has.
        var named = c.Designations.Where((_, i) => i % 2 == 0).Select(d => d.Id).ToList();
        var ids = named.Concat(named).Concat(["d-none", "d-missing"]).ToList();
        var (res, err) = c.Designate("cancel", null, null, null, null, ids);
        Assert.Null(err);
        Assert.Equal(4096, res!["count"]!.GetValue<int>());
        Assert.Equal(named.Order(StringComparer.Ordinal), res["removed"]!.AsArray().Select(x => x!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal(4096, c.Designations.Count);
        Assert.DoesNotContain(c.Designations, d => named.Contains(d.Id));

        // The delta names each removed mark.
        var removed = c.State(before)["removed"]!["designations"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.True(named.All(removed.Contains));

        // By block: a whole layer, and a block twice; only that layer goes.
        var layer = Layer(59);
        (res, err) = c.Designate("cancel", "mine", null, layer.Concat(layer.Take(10)).ToList(), null);
        Assert.Null(err);
        int left = c.Designations.Count;
        Assert.All(c.Designations, d => Assert.Equal(60, d.At!.Value.Y));
        Assert.Equal(4096 - left, res!["count"]!.GetValue<int>());
        Assert.Equal(2048, left);
    }

    [Fact]
    public void A_retry_by_id_reopens_exactly_the_named_marks()
    {
        var (c, _, _) = StoryRig.Make();
        c.Designate("add", "mine", null, Layer(60), null);
        foreach (var d in c.Designations) d.Status = "failed";
        var named = c.Designations.Take(100).Select(d => d.Id).ToList();
        var (res, err) = c.Designate("retry", null, null, null, null, named.Concat(named).Append("d-none").ToList());
        Assert.Null(err);
        Assert.Equal(100, res!["count"]!.GetValue<int>());
        Assert.Equal(100, c.Designations.Count(d => d.Status == "open"));
        Assert.All(c.Designations.Where(d => named.Contains(d.Id)), d => Assert.Equal("open", d.Status));
    }

    [Fact]
    public void An_add_answers_its_targets_for_the_colony_tool()
    {
        var (c, _, _) = StoryRig.Make();
        var res = c.Designate("add", "mine", null, [new P3(1, 60, 1), new P3(2, 60, 1)], null).Result!;
        Assert.Equal("[[1,60,1],[2,60,1]]", res["targets"]!.ToJsonString()); // colony_control reads this echo: unchanged
    }

    [Fact]
    public void Pause_and_option_changes_write_the_colony_at_the_next_tick()
    {
        using var db = AgentDb.Open(null);
        var clock = new Clock(DateTimeOffset.Parse("2026-11-09T12:00:00Z"));
        var w = new FakeStoryWorld();
        var h = new FakeStoryHost(w);
        var c = new StoryColony(new ColonyMeta { Id = "story-1", Name = "Cost", Home = new P3(0, 64, 0) }, h, clock, new StoryStore(db));
        var (accounts, _) = h.Spawn("story-1", 1, null);
        c.Joined(accounts);
        w.Set(3, 64, 3, 26, 0); // a bed: foot, head
        w.Set(3, 64, 4, 26, 8);
        c.Save(force: true);
        Assert.Equal(c.Rev, SavedRev(db, c.Id));

        var changes = new (string What, Action Run)[]
        {
            ("pause", () => c.Pause(true)),
            ("move home", () => c.SetHome(new P3(4, 64, 4))),
            ("bench options", () => c.SetOptions(true, null)),
            ("haul options", () => c.SetHaulOptions(true, null, null)),
            ("water options", () => c.SetWaterOptions(true)),
            ("other options", () => { Assert.Null(c.SetIdle("stay")); c.OptionsNow(); }),
            ("bed", () => Assert.Null(c.BedsOp("assign", new P3(3, 64, 3), c.P()).Error)),
        };
        foreach (var (what, run) in changes)
        {
            long saved = SavedRev(db, c.Id);
            run();
            // Before: the whole colony rewritten inside the request, under the lock.
            Assert.True(SavedRev(db, c.Id) == saved, $"{what}: the colony was written inside the request");
            Assert.True(c.Rev > saved, $"{what}: nothing changed");
            // At the next tick, although the clock has not moved past the 5 s between ordinary saves.
            c.Tick();
            Assert.True(SavedRev(db, c.Id) == c.Rev, $"{what}: saved {SavedRev(db, c.Id)}, the colony is at {c.Rev}");
        }

        // What was written is what a restart reads.
        var again = new StoryColonies(new FakeStoryHost(w), new StoryStore(db));
        Assert.Equal(1, again.Load());
        var r = again.Get("story-1")!;
        Assert.True(r.Meta.Paused);
        Assert.Equal(new P3(4, 64, 4), r.Meta.Home);
        Assert.True(r.Meta.AllowLava);
        Assert.True(r.Meta.RescueTeleport);
        Assert.Equal(c.P(), r.Meta.Beds.Values.Single());
    }
}
