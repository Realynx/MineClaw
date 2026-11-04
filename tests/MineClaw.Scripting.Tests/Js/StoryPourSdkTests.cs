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
        int fill = calls.FindIndex(c => c.StartsWith("skill:fill_bucket")), back = calls.FindIndex(fill, c => c.StartsWith("goto ") && c.Contains("0,64,3")),
            pour = calls.FindIndex(c => c.StartsWith("skill:empty_bucket"));
        Assert.True(fill >= 0 && fill < back && back < pour, string.Join(" | ", calls)); // fill, back to the stand, pour
        Assert.Equal([
            "j1 progress cell 1,64,1 done",
            "j1 progress cell 2,64,1 done",
            "j1 progress cell 3,64,1 failed: 3,64,1 shows flowing_water, not a water source",
            "j1 done batch: 2/3 poured",
            "j2 done"], port.Reports);
    }

    [Fact]
    public async Task No_way_to_fill_a_bucket_fails_the_job_with_the_reason()
    {
        var port = new Director(Pour("1,64,1"), new JsonObject { ["id"] = "j2", ["kind"] = "end" });
        using var rig = Rig.Js();
        rig.Host.Story = port;
        rig.World.Inventory.SetSlot(0, 36, new MineClaw.Data.ItemStack(325, 1, 0));
        rig.On["skill:fill_bucket"] = (_, _) => Task.FromResult(ActResult.Fail("no reachable visible source in the pond at 9,63,9"));
        var s = await rig.Run(MineClaw.Hive.Story.StoryColonies.PawnCode(), "story_pawn");
        Assert.True(s.Ok, s.Outcome);
        Assert.DoesNotContain(rig.Norm(), c => c.StartsWith("skill:empty_bucket"));
        Assert.StartsWith("j1 failed no reachable visible source", port.Reports.First());
    }
}
