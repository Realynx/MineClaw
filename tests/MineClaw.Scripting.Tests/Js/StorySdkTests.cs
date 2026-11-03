using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MineClaw.Scripting.Story;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>The <c>story</c> SDK primitive and the shipped <c>story_pawn</c> script against a fake director.</summary>
public sealed class StorySdkTests
{
    private sealed class FakeDirector(params JsonObject[] jobs) : IStoryPort
    {
        private readonly Queue<JsonObject> _jobs = new(jobs);
        public ConcurrentQueue<string> Reports { get; } = new();
        public JsonObject? Next(string body) => _jobs.Count > 0 ? _jobs.Dequeue() : null;
        public bool Report(string body, string job, string status, string? note, double? pct)
        {
            Reports.Enqueue($"{body} {job} {status}{(note != null ? " " + note : "")}");
            return true;
        }
    }

    private static JsonObject J(string id, string kind, Action<JsonObject>? more = null)
    {
        var o = new JsonObject { ["id"] = id, ["type"] = "chop", ["kind"] = kind, ["label"] = kind };
        more?.Invoke(o);
        return o;
    }

    [Fact]
    public async Task The_pawn_script_runs_each_job_with_the_usual_primitives_and_reports()
    {
        var port = new FakeDirector(
            J("j1", "chop", j => j["at"] = "3,64,0"),
            J("j2", "place", j =>
            {
                j["at"] = "1,64,1"; j["block"] = "planks";
                j["fetch"] = new JsonArray(new JsonObject { ["from"] = "5,64,5", ["item"] = "planks", ["n"] = 4 });
            }),
            J("j3", "go", j =>
            {
                j["at"] = "2,64,2";
                j["stash"] = new JsonArray(new JsonObject { ["to"] = "2,64,2", ["items"] = new JsonArray("log") });
            }),
            J("j4", "dig", j => j["at"] = "9,64,9"),
            J("j5", "end"));
        using var rig = Rig.Js();
        rig.Host.Story = port;
        rig.On["dig"] = (_, _) => Task.FromResult(ActResult.Fail("too hard"));
        var code = MineClaw.Hive.Story.StoryColonies.PawnCode();
        Assert.True(code.Length < new ScriptOptions().MaxCodeChars, $"story_pawn is {code.Length} chars");
        var s = await rig.Run(code, "story_pawn");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("released", s.Outcome);
        var calls = rig.Norm();
        Assert.Contains("""chop_tree {"at":"3,64,0"}""", calls);
        int take = calls.FindIndex(c => c.StartsWith("withdraw "));
        int place = calls.FindIndex(c => c.StartsWith("place "));
        Assert.True(take >= 0 && take < place, string.Join(" | ", calls)); // fetch first
        Assert.Contains("""withdraw {"at":"5,64,5","count":4,"item":"planks"}""", calls);
        Assert.Contains("""place {"at":"1,64,1","block":"planks"}""", calls);
        Assert.Contains("""deposit {"at":"2,64,2","items":["log"]}""", calls);
        Assert.Equal(["maid_chan j1 done", "maid_chan j2 done", "maid_chan j3 done", "maid_chan j4 failed too hard", "maid_chan j5 done"], port.Reports);
    }

    [Fact]
    public async Task Story_calls_without_a_director_fail_the_script_plainly()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("return story.next()");
        Assert.False(s.Ok);
        Assert.Contains("isn't a Story Mode pawn", s.Outcome);
    }
}
