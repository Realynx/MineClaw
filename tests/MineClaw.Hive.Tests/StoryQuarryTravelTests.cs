using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryQuarryTravelTests
{
    [Fact]
    public void A_miner_enters_by_the_stairs_then_exits_by_them_only_when_its_bag_fills()
    {
        var (c, h, w) = StoryRig.Make();
        c.DoneGrace = TimeSpan.Zero;
        var pawn = c.P();
        foreach (var type in WorkTypes.All) c.Pawns[pawn].Priorities[type] = type == WorkTypes.Mine ? 1 : 0;
        c.SetHaulOptions(null, null, null, pillarStack: 0);
        c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 29, 63, 19));
        var zone = c.Zones.Single(z => z.Kind == "mine");
        zone.QuarryTop = 63;
        var ring = StoryQuarry.Ring(zone.Cells)!;
        for (int x = 10; x <= 29; x++) for (int z = 10; z <= 19; z++) for (int y = 49; y <= 63; y++) w.Set(x, y, z, 0);
        for (int k = 0; k <= 14; k++) { var s = StoryQuarry.Step(ring, 63, k); w.Set(s.X, s.Y, s.Z, 1); }
        var chest = new P3(30, 64, 19);
        c.StorageOp("set", [chest], null, null, null);
        h.Lives[pawn] = FakeStoryHost.Live(30, 64, 19);
        var job = c.Next();
        Assert.Equal("dig", job["kind"]!.GetValue<string>());
        var route = job["quarry_approach"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal("10,64,10", route[0]);
        Assert.Equal("25,49,10", route[^1]);
        void Finish(JsonObject j)
        {
            var at = j["at"]!.GetValue<string>().Split(',').Select(int.Parse).ToArray();
            w.Set(at[0], at[1], at[2], 0);
            c.Report(pawn, j["id"]!.GetValue<string>(), "done", null, null);
        }
        Finish(job);
        h.Lives[pawn] = FakeStoryHost.Live(28, 49, 18, inv: new Stack(4, 0, 8));
        job = c.Next();
        Assert.Equal("dig", job["kind"]!.GetValue<string>());
        Assert.Null(job["quarry_exit"]);
        Assert.Null(job["quarry_approach"]); // already at the work floor, no trip to the entrance for each block
        Finish(job);
        h.Lives[pawn] = h.Lives[pawn] with { FreeSlots = 2 };
        job = c.Next();
        Assert.Equal("go", job["kind"]!.GetValue<string>());
        Assert.NotNull(job["stash"]);
        route = job["quarry_exit"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal("25,49,10", route[0]);
        Assert.Equal("10,64,10", route[^1]);
    }
}
