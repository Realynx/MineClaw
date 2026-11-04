using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MineClaw.Scripting.Story;

namespace MineClaw.Scripting.Tests.Js;

/// <summary><c>story.pour</c> (docs/web/story-mode.md "Water") through the shipped <c>story_pawn</c>: fill at the job's source, pour from
/// the stand, check the world shows a source, report each cell.</summary>
public sealed class StoryPourSdkTests
{
    private sealed class Director(params JsonObject[] jobs) : IStoryPort
    {
        private readonly Queue<JsonObject> _jobs = new(jobs);
        public ConcurrentQueue<string> Reports { get; } = new();
        public JsonObject? Next(string body) => _jobs.Count > 0 ? _jobs.Dequeue() : null;
        public bool Report(string body, string job, string status, string? note, double? pct)
        {
            Reports.Enqueue($"{job} {status}{(note != null ? " " + note : "")}");
            return true;
        }
    }

    private static JsonObject Pour(params string[] cells) => new()
    {
        ["id"] = "j1", ["type"] = "build", ["kind"] = "pour", ["label"] = "pouring water", ["liquid"] = "water", ["block"] = "bucket",
        ["want"] = "water", ["at"] = cells[0], ["cells"] = new JsonArray(cells.Select(c => (JsonNode)c!).ToArray()), ["source"] = "9,63,9",
        ["buckets"] = 1, ["stand"] = "0,64,3",
    };

    [Fact]
    public async Task A_pour_job_fills_at_its_source_pours_each_cell_and_reports_what_the_world_shows()
    {
        var port = new Director(Pour("1,64,1", "2,64,1", "3,64,1"), new JsonObject { ["id"] = "j2", ["kind"] = "end" });
        using var rig = Rig.Js();
        rig.Host.Story = port;
        rig.World.Inventory.SetSlot(0, 36, new MineClaw.Data.ItemStack(325, 1, 0)); // an empty bucket
        rig.World.SetBlock(2, 64, 1, 9, 0); // a source already (it filled by itself)
        rig.On["skill:fill_bucket"] = (_, _) =>
        {
            rig.World.Inventory.SetSlot(0, 36, new MineClaw.Data.ItemStack(326, 1, 0));
            return Task.FromResult(ActResult.Success("filled water bucket at 9,63,9"));
        };
        rig.On["skill:empty_bucket"] = (args, _) =>
        {
            var at = args.GetProperty("pos").GetString()!;
            var p = at.Split(',').Select(int.Parse).ToArray();
            rig.World.SetBlock(p[0], p[1], p[2], at == "3,64,1" ? 8 : 9, at == "3,64,1" ? 2 : 0); // the last one runs off: flowing
            rig.World.Inventory.SetSlot(0, 36, new MineClaw.Data.ItemStack(325, 1, 0));
            return Task.FromResult(ActResult.Success("poured water at " + at));
        };
        var s = await rig.Run(MineClaw.Hive.Story.StoryColonies.PawnCode(), "story_pawn");
        Assert.True(s.Ok, s.Outcome);
        var calls = rig.Norm();
        Assert.True(calls.Count(c => c.StartsWith("skill:fill_bucket ")) == 2, string.Join(" | ", calls) + " // " + string.Join(" | ", port.Reports));
        Assert.Contains("""skill:fill_bucket {"liquid":"water","pos":"9,63,9"}""", calls);
        Assert.DoesNotContain(calls, c => c.StartsWith("skill:empty_bucket") && c.Contains("2,64,1")); // already a source: not poured
        int fill = calls.FindIndex(c => c.StartsWith("skill:fill_bucket")), pour = calls.FindIndex(c => c.StartsWith("skill:empty_bucket"));
        Assert.True(fill >= 0 && fill < pour, string.Join(" | ", calls)); // filled, then poured (empty_bucket finds its own dry stand)
        Assert.Equal(2, calls.Count(c => c.StartsWith("skill:empty_bucket"))); // one pour a cell: a pour that worked isn't repeated
        Assert.Equal([
            "j1 progress cell 1,64,1 done",
            "j1 progress cell 2,64,1 done",
            "j1 progress cell 3,64,1 failed: 3,64,1 shows flowing_water, not a water source",
            "j1 done batch: 2/3 poured",
            "j2 done"], port.Reports);
    }

    [Fact]
    public async Task No_way_to_fill_a_bucket_ends_the_batch_and_names_the_source_for_the_director()
    {
        var port = new Director(Pour("1,64,1"), new JsonObject { ["id"] = "j2", ["kind"] = "end" });
        using var rig = Rig.Js();
        rig.Host.Story = port;
        rig.World.Inventory.SetSlot(0, 36, new MineClaw.Data.ItemStack(325, 1, 0));
        rig.On["skill:fill_bucket"] = (_, _) => Task.FromResult(ActResult.Fail("no reachable visible source in the pond at 9,63,9"));
        var s = await rig.Run(MineClaw.Hive.Story.StoryColonies.PawnCode(), "story_pawn");
        Assert.True(s.Ok, s.Outcome);
        Assert.DoesNotContain(rig.Norm(), c => c.StartsWith("skill:empty_bucket"));
        Assert.Equal(1, rig.Norm().Count(c => c.StartsWith("skill:fill_bucket"))); // no retry loop at an unreachable pond
        Assert.Equal("j1 progress fill failed at 9,63,9: no reachable visible source in the pond at 9,63,9", port.Reports.First());
        Assert.StartsWith("j1 done batch: 0/1 poured; no bucket filled", port.Reports.Skip(1).First()); // the cell goes back, no failure
    }

    [Fact]
    public async Task A_cell_that_wont_take_the_pour_is_tried_twice_reported_failed_and_the_batch_moves_on()
    {
        var port = new Director(Pour("1,64,1", "3,64,1"), new JsonObject { ["id"] = "j2", ["kind"] = "end" });
        using var rig = Rig.Js();
        rig.Host.Story = port;
        rig.World.Inventory.SetSlot(0, 36, new MineClaw.Data.ItemStack(326, 1, 0));
        rig.On["skill:empty_bucket"] = (args, _) =>
        {
            var at = args.GetProperty("pos").GetString()!;
            if (at == "1,64,1") return Task.FromResult(ActResult.Fail("can't get near 1,64,1: no dry spot with a face beside it in view (no path)"));
            rig.World.SetBlock(3, 64, 1, 9, 0);
            rig.World.Inventory.SetSlot(0, 36, new MineClaw.Data.ItemStack(325, 1, 0));
            return Task.FromResult(ActResult.Success("poured water at " + at));
        };
        rig.On["skill:fill_bucket"] = (_, _) =>
        {
            rig.World.Inventory.SetSlot(0, 36, new MineClaw.Data.ItemStack(326, 1, 0));
            return Task.FromResult(ActResult.Success("filled"));
        };
        var s = await rig.Run(MineClaw.Hive.Story.StoryColonies.PawnCode(), "story_pawn");
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal(2, rig.Norm().Count(c => c.StartsWith("skill:empty_bucket") && c.Contains("1,64,1")));
        Assert.Equal([
            "j1 progress cell 1,64,1 failed: can't get near 1,64,1: no dry spot with a face beside it in view (no path)",
            "j1 progress cell 3,64,1 done",
            "j1 done batch: 1/2 poured",
            "j2 done"], port.Reports);
    }
}
