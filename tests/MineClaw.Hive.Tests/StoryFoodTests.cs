using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Eating from real stock (live 11-04: "eat failed: no rotten flesh in the chest at 224,64,398: it is empty" ×784 in 10 minutes):
/// a failed food fetch corrects the record and backs off; good food first, rotten flesh only when starving; no food: one alert, a wait.
/// </summary>
public sealed class StoryFoodTests
{
    private static readonly P3 Chest = new(0, 64, 9);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();

    private (StoryColony C, FakeStoryHost H) Rig(int food, params Stack[] stock)
    {
        var (c, h, _) = StoryRig.Make(1, time: _clock);
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = 0;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0) with { Food = food };
        Assert.Null(c.StorageOp("set", [Chest], null, null, null).Error);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        return (c, h);
    }

    private static string? FetchItem(JsonObject j) => j["fetch"] is JsonArray a && a.Count > 0 ? a[0]!["item"]!.GetValue<string>() : null;

    private static void Finish(StoryColony c, JsonObject j) => c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);

    [Fact]
    public void An_empty_chest_corrects_the_record_and_the_pawn_does_not_fetch_again_within_its_back_off()
    {
        var (c, _) = Rig(4, new Stack(297, 0, 4)); // bread on record; the chest is empty
        var j = c.Next();
        Assert.Equal(c.Host.World!.Data.Item(297).Name, FetchItem(j));
        Assert.True(c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", $"no bread in the chest at {Chest}: it is empty", null));
        Assert.Empty(c.StorageAt(Chest)!.Contents); // corrected
        c.AddContents(c.StorageAt(Chest)!, new Stack(297, 0, 4)); // even if the record shows some again at once…
        for (int i = 0; i < 5; i++)
        {
            var again = c.Next();
            Assert.Null(FetchItem(again)); // …not within the back-off (15 s), and not that chest (2 min)
            Finish(c, again);
        }
        _clock.Now += TimeSpan.FromSeconds(16);
        Assert.Null(FetchItem(c.Next())); // the chest that failed it stays skipped for 2 minutes
        Finish(c, c.Next());
        _clock.Now += TimeSpan.FromMinutes(2);
        Assert.NotNull(FetchItem(c.Next()));
    }

    [Fact]
    public void Rotten_flesh_is_skipped_for_bread_and_eaten_only_when_starving_with_nothing_else()
    {
        var (c, _) = Rig(5, new Stack(367, 0, 10), new Stack(297, 0, 2));
        Assert.Equal(c.Host.World!.Data.Item(297).Name, FetchItem(c.Next()));

        var (c2, _) = Rig(3, new Stack(367, 0, 10)); // starving, rotten flesh only
        var j = c2.Next();
        Assert.Equal(c2.Host.World!.Data.Item(367).Name, FetchItem(j));
        Assert.Equal("hold", j["kind"]!.GetValue<string>()); // fetched and left to the body's starving reflex: never an eat that refuses it

        var (c3, h3) = Rig(5); // rotten flesh carried, nothing stored: not "eat" in a loop
        h3.Lives[c3.P()] = h3.Lives[c3.P()] with { Inventory = [new Stack(367, 0, 3)] };
        Assert.NotEqual("eat", c3.Next()["kind"]!.GetValue<string>());
    }

    [Fact]
    public void With_no_food_anywhere_the_pawn_waits_and_the_colony_hears_it_once()
    {
        var (c, _) = Rig(4, new Stack(3, 0, 10));
        for (int i = 0; i < 6; i++)
        {
            var j = c.Next();
            Assert.NotEqual("eat", j["kind"]!.GetValue<string>());
            Assert.Null(FetchItem(j));
            Finish(c, j);
        }
        Assert.Single(c.Alerts, a => a.Text.Contains("no food in storage"));
    }
}
