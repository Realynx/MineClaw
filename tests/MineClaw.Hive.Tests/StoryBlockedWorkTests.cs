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
        Only(c, WorkTypes.Build, WorkTypes.Mine); // the torch is building work, the digs mining
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

    /// <summary>A build held by water in its empty space, a forbidden zone, a stuck spot or a pawn's temporary block said only "the
    /// level below is still being built" or "its open cells are taken or backing off". The note now names the hold.</summary>
    [Theory]
    [InlineData("water", "build b1: water at 4,64,12 can't be cleared: the 4 cells under it wait")]
    [InlineData("forbidden", "build b1: its open cells are in a forbidden zone (8,64,8)")]
    [InlineData("stuck", "build b1: its open cells are by a spot where a pawn got stuck (8,64,8)")]
    [InlineData("temp", "build b1: a pawn's temporary block stands in its cell at 5,64,12 (taken back first)")]
    public void The_note_says_which_hold_keeps_a_build_from_going_on(string hold, string note)
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-10T10:00:00Z"));
        var (c, h, w) = StoryRig.Make(pawns: 2, time: clock);
        Only(c, WorkTypes.Build);
        var cobble = new Stack(4, 0, 8);
        h.Lives[c.P(0)] = hold == "water" ? FakeStoryHost.Live(0, 64, 3) : FakeStoryHost.Live(0, 64, 3, inv: cobble);
        h.Lives[c.P(1)] = FakeStoryHost.Live(30, 64, 30);
        var block = new JsonObject { ["kind"] = "block", ["blocks"] = Cells((8, 64, 8)), ["material"] = "cobblestone" };
        switch (hold)
        {
            case "water": // the design's empty space holds still water over stone: the digs under it wait; its block has no stock
                w.Set(4, 64, 12, 9);
                block["blocks"] = Cells((5, 64, 12));
                block["replace_area"] = new JsonArray(4, 62, 12, 5, 64, 12);
                break;
            case "stuck":
                h.Lives[c.P(1)] = FakeStoryHost.Live(8, 64, 7);
                c.Stuck(c.P(1), "boxed in");
                break;
            case "temp": // another pawn stands on its pillar in the design's cell
                var at = new P3(5, 64, 12);
                w.Set(at.X, at.Y, at.Z, 3);
                h.Lives[c.P(1)] = FakeStoryHost.Live(5.5, 65, 12.5);
                h.Temps[c.P(1)] = [(at, 3)];
                c.Temporary(c.P(1), at.ToString(), true, "reach pillar");
                block["blocks"] = Cells((5, 64, 12));
                block["replace_area"] = new JsonArray(5, 64, 12, 5, 64, 12);
                break;
        }
        Assert.Null(c.BuildsOp("add", new JsonArray(block), null).Error);
        if (hold == "forbidden") Assert.Null(c.ZoneOp("add", "forbidden", Area.Of(7, 60, 7, 9, 70, 9)).Error);
        Assert.NotEqual("place", Kind(c.Next(0)));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.Equal("waiting: " + note, c.IdleReasonOf(c.P(0)));
    }

    /// <summary>A placement nobody has in view reads as air: nothing to place against, no scaffold spot, so no job and nobody sent
    /// there, for good. One pawn now goes to its nearest open cell (the existing go job), and only one.</summary>
    [Fact]
    public void A_build_out_of_everyones_view_sends_one_pawn_to_its_site()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-10T10:00:00Z"));
        var (c, h, w) = StoryRig.Make(pawns: 2, time: clock);
        Only(c, WorkTypes.Build);
        for (int i = 0; i < 2; i++) h.Lives[c.P(i)] = FakeStoryHost.Live(0, 64, 3, inv: new Stack(4, 0, 8));
        w.Unloaded.Add((40, 40));
        w.Unloaded.Add((41, 40));
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["blocks"] = Cells((41, 64, 40), (40, 64, 40)), ["material"] = "cobblestone",
        }), null).Error);
        var go = c.Next(0);
        Assert.Equal(("go", "40,64,40", 8), (Kind(go), go["at"]!.GetValue<string>(), go["dist"]!.GetValue<int>()));
        Assert.NotEqual("go", Kind(c.Next(1))); // the site's lease: one pawn goes
        // Once a body is there the site is in view and the build goes on as ever.
        Assert.True(c.Report(c.P(0), go["id"]!.GetValue<string>(), "done", null, null));
        w.Unloaded.Clear();
        h.Lives[c.P(0)] = FakeStoryHost.Live(38, 64, 40, inv: new Stack(4, 0, 8));
        Assert.Equal("place", Kind(c.Next(0)));
    }

    /// <summary>The site visit went out before any stock check: a far build whose material was out sent a pawn there and back every
    /// two minutes (it arrived, found nothing to place, left, and the site unloaded) until the material came. Nobody walks now, and
    /// the build says what it needs as it would in view.</summary>
    [Fact]
    public void A_build_out_of_view_with_nothing_to_place_sends_nobody()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-10T10:00:00Z"));
        var (c, h, w) = StoryRig.Make(pawns: 2, time: clock);
        Only(c, WorkTypes.Build);
        for (int i = 0; i < 2; i++) h.Lives[c.P(i)] = FakeStoryHost.Live(0, 64, 3); // no cobblestone carried or stored
        w.Unloaded.Add((40, 40));
        w.Unloaded.Add((41, 40));
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["blocks"] = Cells((41, 64, 40), (40, 64, 40)), ["material"] = "cobblestone",
        }), null).Error);
        JsonObject Pick(int i)
        {
            var j = c.Next(i)!;
            if (j["id"] is not null) c.Report(c.P(i), j["id"]!.GetValue<string>(), "done", null, null);
            return j;
        }
        for (int k = 0; k < 3; k++)
        {
            Assert.NotEqual("go", Kind(Pick(0)));
            Assert.NotEqual("go", Kind(Pick(1)));
            clock.Now += TimeSpan.FromMinutes(3); // past the site's rest
        }
        var idle = c.Next(0)!;
        Assert.NotEqual("go", Kind(idle));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        var why = c.IdleReasonOf(c.P(0));
        Assert.NotNull(why);
        Assert.Contains("need 2 cobblestone", why);
        if (idle["id"] is not null) c.Report(c.P(0), idle["id"]!.GetValue<string>(), "done", null, null);
        // Cobblestone carried: the pawn goes to the site after all.
        h.Lives[c.P(0)] = FakeStoryHost.Live(0, 64, 3, inv: new Stack(4, 0, 8));
        clock.Now += TimeSpan.FromSeconds(5);
        Assert.Equal("go", Kind(c.Next(0)));
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
