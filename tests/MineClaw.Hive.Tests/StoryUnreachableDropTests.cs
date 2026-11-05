using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Drops the pawns can't collect are kept by place, not by entity id (StoryColony.DropSpots.cs; docs/web/story-mode.md "Hauling").
/// Live 11-04 story-1 (release 20261104-181056): "collect … failed: no reachable drops nearby" ×63 in 6 minutes all over the farm, each
/// collect about 0.2 s. A drop that came back with a new id at the same spot was a new target every time, a batch's other drops never
/// rested, and drops in water were offered although nobody could stand by them.
/// </summary>
public sealed class StoryUnreachableDropTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Haulers(int pawns)
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-04T12:00:00Z"));
        var (c, h, w) = StoryRig.Make(pawns, time: clock);
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Haul ? 1 : 0;
        c.StorageOp("set", [new P3(2, 64, 0)], null, null, null);
        return (c, h, w, clock);
    }

    private static StoryEntity Seeds(int id, double x, double y, double z) => new(id, "item", "wheat_seeds", x, y, z, ItemId: 295, Count: 1);

    [Fact]
    public void A_spot_three_pawns_missed_rests_for_everyone_even_when_its_drop_comes_back_with_a_new_id()
    {
        var (c, _, w, clock) = Haulers(3);
        w.Ents.Add(Seeds(1, 6.5, 64, 6.5));
        for (int i = 0; i < 3; i++)
        {
            var j = c.Next(i);
            Assert.Equal("collect", Kind(j));
            c.Report(c.P(i), Id(j), "failed", "no reachable drops nearby", null);
        }
        // A new seed drops there (another harvest, a merged stack): a new entity id at the same spot.
        w.Ents.Clear();
        w.Ents.Add(Seeds(2, 6.6, 64, 7.4));
        clock.Now += TimeSpan.FromMinutes(1); // every pawn's own rest of the old drop is over
        var idle = Enumerable.Range(0, 3).Select(i => c.Next(i)).ToList();
        Assert.All(idle, j => Assert.NotEqual("collect", Kind(j)));
        c.Tick();
        Assert.Equal("waiting: drops at 6,64,6 unreachable", c.IdleReasonOf(c.P(0)));
        for (int i = 0; i < 3; i++) c.Report(c.P(i), Id(idle[i]), "done", null, null);

        // After the rest it is tried once more, and one more miss rests it again (longer).
        clock.Now += StoryColony.DropSpotRest(1);
        var again = c.Next(0);
        Assert.Equal("collect", Kind(again));
        c.Report(c.P(0), Id(again), "failed", "no reachable drops nearby", null);
        clock.Now += TimeSpan.FromMinutes(6);
        for (int i = 0; i < 3; i++) Assert.NotEqual("collect", Kind(c.Next(i)));
    }

    [Fact]
    public void A_batch_the_pawn_wrote_off_rests_every_spot_in_it_at_once()
    {
        var (c, _, w, clock) = Haulers(2);
        w.Ents.Add(Seeds(1, 6.5, 64, 6.5));
        w.Ents.Add(Seeds(3, 9.5, 64, 6.5));
        var j = c.Next(0);
        Assert.Equal("collect", Kind(j));
        Assert.Contains("(+1 more)", j["label"]!.GetValue<string>());
        c.Report(c.P(0), Id(j), "failed", "no reachable drops nearby; 2 drop(s) left in lava/water (hazard, not chased)", null);
        w.Ents.Clear();
        w.Ents.Add(Seeds(4, 6.5, 64, 6.5));
        w.Ents.Add(Seeds(5, 9.5, 64, 6.5));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.NotEqual("collect", Kind(c.Next(0)));
        Assert.NotEqual("collect", Kind(c.Next(1)));
    }

    [Fact]
    public void A_drop_in_deep_water_is_never_offered()
    {
        var (c, _, w, _) = Haulers(1);
        for (int x = 5; x <= 7; x++)
            for (int z = 5; z <= 7; z++)
                for (int y = 60; y <= 63; y++) w.Set(x, y, z, 9);
        w.Ents.Add(Seeds(1, 6.5, 63.0, 6.5)); // on the pond's surface: nowhere to stand with the head out of the water
        Assert.NotEqual("collect", Kind(c.Next()));
        c.Tick();
        Assert.Equal("waiting: drops at 6,63,6 unreachable", c.IdleReasonOf(c.P()));

        w.Ents.Add(Seeds(2, 4.5, 64, 6.5)); // on the bank beside it: fine
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        var j = c.Next();
        Assert.Equal("collect", Kind(j));
        Assert.Equal("4,64,6", j["at"]!.GetValue<string>());
    }

    [Fact]
    public void A_haul_mark_on_a_resting_spot_says_why()
    {
        var (c, _, w, clock) = Haulers(1);
        w.Ents.Add(Seeds(1, 6.5, 64, 6.5));
        c.Designate("add", "haul", Area.Of(0, 60, 0, 15, 80, 15), null, null);
        var j = c.Next();
        Assert.Equal("collect", Kind(j));
        c.Report(c.P(), Id(j), "failed", "no reachable drops nearby; 1 drop(s) left near the void (1 item(s) lost)", null);
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.NotEqual("collect", Kind(c.Next()));
        c.Tick();
        Assert.StartsWith("no reachable target: haul", c.IdleReasonOf(c.P()));
        Assert.EndsWith("drops there unreachable (missed there, or nowhere to stand by them)", c.IdleReasonOf(c.P()));
    }
}
