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
        Assert.Contains("""chop_tree {"at":"3,64,0 only"}""", calls);
        int take = calls.FindIndex(c => c.StartsWith("withdraw "));
        int place = calls.FindIndex(c => c.StartsWith("place "));
        Assert.True(take >= 0 && take < place, string.Join(" | ", calls)); // fetch first
        Assert.Contains("""withdraw {"at":"5,64,5","count":4,"item":"planks"}""", calls);
        Assert.Contains("""place {"at":"1,64,1","block":"planks"}""", calls);
        Assert.Contains("""deposit {"at":"2,64,2","items":["log"]}""", calls);
        Assert.Equal(["maid_chan j1 done", "maid_chan j2 done", "maid_chan j3 done", "maid_chan j4 failed too hard", "maid_chan j5 done"], port.Reports);
    }

    /// <summary>A director that takes a job back (real work came up for an idle pawn).</summary>
    private sealed class TakesBack(params JsonObject[] jobs) : IStoryPort
    {
        private readonly Queue<JsonObject> _jobs = new(jobs);
        public ConcurrentQueue<string> Reports { get; } = new();
        public volatile string? TakenBack;
        public JsonObject? Next(string body) => _jobs.Count > 0 ? _jobs.Dequeue() : null;
        public bool Report(string body, string job, string status, string? note, double? pct)
        {
            Reports.Enqueue($"{job} {status}{(note != null ? " " + note : "")}");
            return job != TakenBack;
        }
        public bool Holds(string body, string job) => job != TakenBack;
    }

    [Fact]
    public async Task An_idle_leg_is_cut_mid_walk_the_moment_the_director_takes_it_back()
    {
        var port = new TakesBack(
            J("j1", "idle", j => { j["at"] = "9,64,9"; j["dist"] = 0; j["s"] = 8; j["wander"] = true; }),
            J("j2", "end"));
        using var rig = Rig.Js();
        rig.Host.Story = port;
        rig.On["goto"] = Rig.Blocking(new TaskCompletionSource(), () => _ = Task.Delay(300).ContinueWith(_ => port.TakenBack = "j1")); // a long walk
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var s = await rig.Run(MineClaw.Hive.Story.StoryColonies.PawnCode(), "story_pawn");
        Assert.True(s.Ok, s.Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "cut at once, not after the walk: " + watch.Elapsed);
        Assert.Contains("""goto {"dist":0,"to":"9,64,9"}""", rig.Norm());
        Assert.StartsWith("j1 failed cancelled", port.Reports.First()); // a stale report the director ignores: no failure
        Assert.Equal("j2 done", port.Reports.Last());
    }

    [Fact]
    public async Task A_pastime_walks_to_its_cell_and_looks_at_each_point_and_till_holds_the_hoe_first()
    {
        var port = new TakesBack(
            J("j1", "idle", j =>
            {
                j["at"] = "3,65,3"; j["dist"] = 0; j["s"] = 2; j["wander"] = true; j["act"] = "socialise";
                j["look"] = new JsonArray("5.5,66.6,3.5", "3.5,95,11.5");
            }),
            J("j2", "till", j => { j["at"] = "1,64,1"; j["hoe"] = "wood_hoe"; }),
            J("j3", "end"));
        using var rig = Rig.Js();
        rig.Host.Story = port;
        var s = await rig.Run(MineClaw.Hive.Story.StoryColonies.PawnCode(), "story_pawn");
        Assert.True(s.Ok, s.Outcome);
        var calls = rig.Norm();
        int go = calls.FindIndex(c => c.StartsWith("goto "));
        int f1 = calls.IndexOf("""face {"at":"5.5,66.6,3.5"}"""), f2 = calls.IndexOf("""face {"at":"3.5,95,11.5"}""");
        Assert.True(go >= 0 && go < f1 && f1 < f2, string.Join(" | ", calls)); // walk, then look at each point in turn
        int hold = calls.FindIndex(c => c.StartsWith("skill:hold_item")), use = calls.FindIndex(c => c.StartsWith("use_block "));
        Assert.True(hold >= 0 && hold < use, string.Join(" | ", calls)); // the hoe in hand, then on the block
        var reports = port.Reports.ToList();
        Assert.Equal("j1 done socialise", reports[0]);
        Assert.StartsWith("j2 done", reports[1]);
        Assert.Equal("j3 done", reports[2]);
    }

    [Fact]
    public async Task Story_during_runs_the_function_while_the_job_holds()
    {
        var port = new TakesBack { TakenBack = "gone" };
        using var rig = Rig.Js();
        rig.Host.Story = port;
        var s = await rig.Run("const a = story.during('mine', () => 'ran'); const b = story.during('gone', () => 'ran'); return a + ' ' + b.cancelled");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("ran true", s.Outcome);
    }

    [Fact]
    public async Task A_dig_that_said_ok_but_left_the_block_goes_once_more_and_a_pawn_on_its_target_steps_off_first()
    {
        var port = new FakeDirector(
            J("j1", "dig", j => j["at"] = "1,64,1"),                         // the dig says ok, the stone stays: one more go
            J("j2", "dig", j => { j["at"] = "2,64,2"; j["stand"] = "3,65,2"; }), // the dig takes: one dig; it stood on it: steps off first
            J("j3", "end"));
        using var rig = Rig.Js();
        rig.Host.Story = port;
        rig.On["dig"] = (args, _) =>
        {
            if (args.GetProperty("at").GetString() == "2,64,2") rig.World.SetBlock(2, 64, 2, 0, 0);
            return Task.FromResult(ActResult.Success("dug"));
        };
        var s = await rig.Run(MineClaw.Hive.Story.StoryColonies.PawnCode(), "story_pawn");
        Assert.True(s.Ok, s.Outcome);
        var calls = rig.Norm();
        Assert.Equal(2, calls.Count(c => c == """dig {"at":"1,64,1"}"""));
        Assert.Equal(1, calls.Count(c => c == """dig {"at":"2,64,2"}"""));
        int step = calls.FindIndex(c => c.StartsWith("goto ") && c.Contains("3,65,2"));
        Assert.True(step >= 0 && step < calls.IndexOf("""dig {"at":"2,64,2"}"""), string.Join(" | ", calls));
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
