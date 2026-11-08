using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 11-04 (bot 20261104-032440): "idle-with-work 228: build or bill waiting 227", nearly four pawns idle all the time with no
/// telling what held them. The idle audit now says what (a build's missing material and its raw source, a suspended bill), the
/// colony's <c>blocked_work</c> groups it for the browser, a bill whose count is met is no work, and a material that is out holds
/// only its own cells (docs/web/story-mode.md "Idle with work").
/// </summary>
public sealed class StoryBlockedWorkTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly P3 Chest = new(0, 64, 9), Table = new(3, 64, 0), Furnace = new(6, 64, 0);

    private static JsonArray Cells(params (int X, int Y, int Z)[] ps) => new(ps.Select(p => (JsonNode)new JsonArray(p.X, p.Y, p.Z)).ToArray());

    private static void Only(StoryColony c, params string[] types)
    {
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static string Kind(JsonObject? j) => j?["kind"]?.GetValue<string>() ?? "";

    /// <summary>Asks and finishes jobs until the pawn idles (a look into a chest, a delivery…), then returns its idle job.</summary>
    private static JsonObject IdleAfterJobs(StoryColony c, int i, Clock clock)
    {
        for (int k = 0; k < 8; k++)
        {
            var j = c.Next(i)!;
            if (Kind(j) is "idle" or "hold" or "wander" || j["type"]?.GetValue<string>() == "idle") return j;
            c.Report(c.P(i), j["id"]!.GetValue<string>(), "done", null, null);
            clock.Now += TimeSpan.FromSeconds(1);
        }
        throw new Xunit.Sdk.XunitException("the pawn never idled");
    }

    [Fact]
    public void A_mine_short_of_torches_digs_on_and_says_what_the_torch_waits_for()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-04T10:00:00Z"));
        var (c, h, w) = StoryRig.Make(pawns: 1, time: clock);
        Only(c, WorkTypes.Build);
        h.Lives[c.P(0)] = FakeStoryHost.Live(0, 64, 3); // no torches anywhere
        // A torch at the entry (the highest cell: first in a mine's order) and a shaft below it.
        var r = c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "mine", ["blocks"] = Cells((5, 63, 5), (5, 62, 5)), ["place"] = new JsonArray(new JsonArray(4, 64, 5, 50, 5)), ["name"] = "shaft",
        }), null);
        Assert.Null(r.Error);
        // Before: the torch came first, was out, and the whole mine stood still. Now its digs go on.
        var dug = new List<string>();
        for (int k = 0; k < 4; k++)
        {
            var j = c.Next(0)!;
            if (Kind(j) != "dig") break;
            var at = j["at"]!.GetValue<string>();
            dug.Add(at);
            var p = at.Split(',').Select(int.Parse).ToArray();
            w.Set(p[0], p[1], p[2], 0);
            c.Report(c.P(0), j["id"]!.GetValue<string>(), "done", null, null);
            clock.Now += TimeSpan.FromSeconds(1);
        }
        Assert.Equal(new[] { "5,63,5", "5,62,5" }, dug);
        // Only the torch is left: the pawn idles, and says why.
        Assert.NotEqual("place", Kind(c.Next(0)));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        var why = c.IdleReasonOf(c.P(0));
        Assert.NotNull(why);
        Assert.StartsWith("waiting: build ", why);
        Assert.Contains("torch", why, StringComparison.OrdinalIgnoreCase);
        var blocked = c.State()["blocked_work"]!.AsArray();
        Assert.Single(blocked);
        Assert.Equal(why, blocked[0]!["reason"]!.GetValue<string>());
        Assert.Equal(c.P(0), blocked[0]!["pawns"]![0]!.GetValue<string>());
    }

    [Fact]
    public void A_bill_whose_count_is_met_is_no_work_and_a_suspended_one_says_why()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-04T10:00:00Z"));
        var (c, h, w) = StoryRig.Make(pawns: 1, time: clock);
        Only(c, WorkTypes.Craft, WorkTypes.Cook);
        w.Set(Table.X, Table.Y, Table.Z, 58);
        w.Set(Furnace.X, Furnace.Y, Furnace.Z, 61);
        c.StorageOp("set", [Chest], null, null, null);
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        c.AddContents(c.StorageAt(Chest)!, new Stack(280, 0, 20)); // sticks
        c.AddContents(c.StorageAt(Chest)!, new Stack(263, 0, 16)); // coal
        Assert.Null(c.WorkbenchOp("designate", Table).Error);
        Assert.Null(c.WorkbenchOp("designate", Furnace).Error);
        // Sticks until 10, with 20 stored: nothing to do. Before: "build or bill waiting" all the same.
        Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = Table.Json(), ["bill"] = new JsonObject { ["recipe"] = "craft:stick", ["mode"] = "until", ["target"] = 10 } }).Error);
        IdleAfterJobs(c, 0, clock);
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.Null(c.IdleReasonOf(c.P(0)));
        Assert.Empty(c.State()["blocked_work"]!.AsArray());
        // Iron ingots with no iron ore anywhere: the bill is suspended, and the idle pawn says so.
        Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = Furnace.Json(), ["bill"] = new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 5 } }).Error);
        IdleAfterJobs(c, 0, clock);
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        var why = c.IdleReasonOf(c.P(0));
        Assert.NotNull(why);
        Assert.StartsWith("waiting: bill ", why);
        Assert.Contains("suspended", why);
        Assert.Contains("iron", why, StringComparison.OrdinalIgnoreCase);
    }
}
