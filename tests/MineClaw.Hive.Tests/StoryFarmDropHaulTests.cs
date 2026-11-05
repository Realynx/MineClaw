using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 11-04 (releases 20261104-2005 and -201512): "collect … failed: no reachable drops nearby (none in sight within 3 with room for it)"
/// ×1050 in 75 minutes over the farm, every target a different spot, 83% of them a cell over the farmland (a drop still in the air, a second
/// after the harvest): the colony sent a hauler to each fresh harvest drop, and the farmer working beside it had picked it up before the
/// hauler got there. The colony now leaves a fresh drop to a pawn working right there, a collect whose drops someone else took is logged
/// as no failure (nothing rests), and a pawn whose bag has no room for a drop isn't sent for it.
/// </summary>
public sealed class StoryFarmDropHaulTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();

    /// <summary>Pawn 0 hauls only, pawn 1 farms (a player entity in the farm); a 9×9 field at x,z 2..10, surface 63, 1.12.2.</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Farm()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-04T20:00:00Z"));
        var (c, h, w) = StoryRig.Make(2, MineClaw.Data.Versioning.Release1122GameData.Instance, clock);
        foreach (var t in WorkTypes.All)
        {
            c.Pawns[c.P(0)].Priorities[t] = t == WorkTypes.Haul ? 1 : 0;
            c.Pawns[c.P(1)].Priorities[t] = 0; // the farmer: busy with its own work, never handed a job here
        }
        c.StorageOp("set", [new P3(2, 64, 0)], null, null, null);
        for (int x = 2; x <= 10; x++)
            for (int z = 2; z <= 10; z++) w.Set(x, 63, z, 60);
        return (c, h, w, clock);
    }

    private static StoryEntity Seeds(int id, double x, double y, double z) => new(id, "item", "wheat_seeds", x, y, z, ItemId: 295, Count: 1);
    private static StoryEntity Wheat(int id, double x, double y, double z) => new(id, "item", "wheat", x, y, z, ItemId: 296, Count: 1);

    [Fact]
    public void A_fresh_harvest_drop_beside_a_working_farmer_is_left_to_it_then_offered()
    {
        var (c, _, w, clock) = Farm();
        w.Ents.Add(new StoryEntity(500, "player", c.P(1), 7.5, 63.9375, 6.5)); // the farmer, a block from its harvest
        w.Ents.Add(Seeds(1, 6.5, 64.4, 6.5));                                     // still in the air
        w.Ents.Add(Wheat(2, 6.4, 64.6, 6.6));
        Assert.NotEqual("collect", Kind(Ask(c)));

        // The farmer took them: nothing to haul, nothing failed.
        w.Ents.RemoveAll(e => e.Kind == "item");
        clock.Now += StoryColony.PawnDropGrace;
        Assert.NotEqual("collect", Kind(Ask(c)));

        // One it left behind (it walked on): offered once it is no longer fresh.
        w.Ents.Add(Seeds(3, 9.5, 63.9375, 9.5));
        w.Ents[0] = w.Ents[0] with { X = 9.5, Z = 8.5 };
        Assert.NotEqual("collect", Kind(Ask(c)));
        clock.Now += StoryColony.PawnDropGrace;
        var j = Ask(c);
        Assert.Equal("collect", Kind(j));
        Assert.Equal("9,63,9", j["at"]!.GetValue<string>());
    }

    [Fact]
    public void A_drop_with_no_pawn_beside_it_is_offered_at_once()
    {
        var (c, _, w, _) = Farm();
        w.Ents.Add(new StoryEntity(500, "player", c.P(1), 2.5, 63.9375, 9.5)); // working far across the field
        w.Ents.Add(Seeds(1, 8.5, 63.9375, 3.5));
        Assert.Equal("collect", Kind(Ask(c)));
    }

    [Fact]
    public void A_batch_whose_drops_were_all_taken_on_the_way_is_no_failure_and_rests_nothing()
    {
        var (c, _, w, clock) = Farm();
        w.Ents.Add(Seeds(1, 6.5, 63.9375, 6.5));
        w.Ents.Add(Wheat(2, 8.5, 63.9375, 6.5));
        w.Ents.Add(Seeds(3, 6.5, 63.9375, 9.5));
        var j = Ask(c);
        Assert.Equal("collect", Kind(j));
        Assert.Contains("(+2 more)", j["label"]!.GetValue<string>());
        // The client's radius reaches every drop of the batch from the first one's cell (go(at, 1), then collect(r)).
        Assert.True(j["r"]!.GetValue<int>() >= 3 + 2, j.ToJsonString()); // the farthest lies 3 from the first

        // Everyone else's feet got there first: the colony sees none of them any more.
        w.Ents.Clear();
        c.Report(c.P(0), j["id"]!.GetValue<string>(), "failed", "no reachable drops nearby (none in sight within 5 with room for it)", null);

        // New drops at the same spots straight away: offered at once, to the same pawn (no rest for it, none for the spots).
        w.Ents.Add(Seeds(4, 6.5, 63.9375, 6.5));
        clock.Now += TimeSpan.FromSeconds(1);
        var again = Ask(c);
        Assert.Equal("collect", Kind(again));
        Assert.Equal("6,63,6", again["at"]!.GetValue<string>());
    }

    [Fact]
    public void A_pawn_with_no_room_for_a_drop_is_not_sent_and_a_full_bag_never_rests_the_spot()
    {
        var (c, h, w, _) = Farm();
        var full = Enumerable.Range(0, 36).Select(i => new MineClaw.Hive.Story.Stack(1, 0, 64)).ToList(); // 36 stacks of stone
        h.Lives[c.P(0)] = new PawnLive(true, 20, 20, 0.5, 64, 0.5, 0, null, full, true, 0);
        w.Ents.Add(Seeds(1, 6.5, 63.9375, 6.5));
        Assert.NotEqual("collect", Kind(Ask(c)));

        // Another hauler with room goes; its client says it had no room after all (the colony's totals were behind): the pawn's
        // trouble, never a miss at the spot. Three such reports and the spot still doesn't rest: the next hauler is sent at once.
        foreach (var t in WorkTypes.All) c.Pawns[c.P(1)].Priorities[t] = t == WorkTypes.Haul ? 1 : 0;
        const string NoRoom = "no reachable drops nearby (none in sight within 3 with room for it: 1 in sight with no room in my bag)";
        Assert.True(StoryColony.NoRoomInBag(NoRoom));
        for (int i = 0; i < StoryColony.DropSpotMisses; i++)
        {
            w.Ents.RemoveAll(e => e.Kind == "item");
            w.Ents.Add(Seeds(10 + i, 6.5, 63.9375, 6.5)); // a new drop there each time (its id is new: only the spot could rest it)
            var j = c.Next(1);
            Assert.True(Kind(j) == "collect", j.ToJsonString());
            c.Report(c.P(1), j["id"]!.GetValue<string>(), "failed", NoRoom, null);
        }
        w.Ents.RemoveAll(e => e.Kind == "item");
        w.Ents.Add(Seeds(20, 6.5, 63.9375, 6.5));
        Assert.Equal("collect", Kind(c.Next(1)));
    }

    /// <summary>The pawn's next job, its current one (an idle stroll, a wait) reported done first.</summary>
    private static JsonObject Ask(StoryColony c)
    {
        if (c.CurrentOf(c.P(0)) is { } cur) c.Report(c.P(0), cur.Id, "done", null, null);
        return c.Next(0);
    }

    [Theory]
    [InlineData("no reachable drops nearby", true, false)]
    [InlineData("no reachable drops nearby (none in sight within 3 with room for it)", true, false)]
    [InlineData("no reachable drops nearby (none in sight within 3 with room for it: 2 in sight with no room in my bag)", true, true)]
    [InlineData("no reachable drops nearby (1 seen: 0 with no way to a pickup spot, 1 not picked up)", true, false)]
    [InlineData("no reachable drops nearby; 1 drop(s) left near the void (1 item(s) lost)", true, false)]
    [InlineData("picked up 3 item(s)", false, false)]
    public void Collect_failure_texts_are_read_by_their_stable_parts(string why, bool noneReached, bool noRoom)
    {
        var j = new ActiveJob { Kind = "collect", Source = "drop" };
        Assert.Equal(noneReached, StoryColony.NoDropReached(j, why));
        Assert.Equal(noRoom, StoryColony.NoRoomInBag(why));
        Assert.Equal(noneReached && why.Contains(';'), StoryColony.NoDropReached(j, why) && !StoryColony.DropMissed(j, why));
    }
}
