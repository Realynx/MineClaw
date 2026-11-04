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
        // A job's own message rides along with "done" (a chop's "partial: …" reaches the director).
        string[] want = ["maid_chan j1 done", "maid_chan j2 done", "maid_chan j3 done", "maid_chan j4 failed too hard", "maid_chan j5 done"];
        Assert.Equal(want.Length, port.Reports.Count);
        Assert.All(want.Zip(port.Reports), x => Assert.StartsWith(x.First, x.Second));
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
        Assert.StartsWith("j1 done", reports[0]); // the pastime's own message rides along ("socialise")
        Assert.StartsWith("j2 done", reports[1]);
        Assert.StartsWith("j3 done", reports[2]);
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

    /// <summary>
    /// Building fast: a build job is a batch of cells. The script fetches once (a busy chest is waited for and tried again), goes to the
    /// stand, counts a cell that is already built as done, tries a "too far" cell once more after stepping closer, reports each cell,
    /// and ends with "batch: n/m placed".
    /// </summary>
    [Fact]
    public async Task A_build_batch_fetches_once_lays_its_cells_from_the_stand_and_reports_each_one()
    {
        var port = new FakeDirector(
            J("j1", "place", j =>
            {
                j["at"] = "1,64,1"; j["block"] = "planks"; j["want"] = "planks"; j["stand"] = "0,64,3";
                j["cells"] = new JsonArray("1,64,1", "2,64,1", "3,64,1", "4,64,1");
                j["fetch"] = new JsonArray(new JsonObject { ["from"] = "5,64,5", ["item"] = "planks", ["n"] = 3 });
            }),
            J("j2", "end"));
        using var rig = Rig.Js();
        rig.Host.Story = port;
        rig.World.SetBlock(2, 64, 1, 5, 0); // already built
        int takes = 0, far = 0;
        rig.On["withdraw"] = (_, _) =>
        {
            if (takes++ == 0) return Task.FromResult(ActResult.Fail("busy: maid_chan_102 is using chest 5,64,5; waited 12s"));
            rig.World.Inventory.SetSlot(0, 36, new MineClaw.Data.ItemStack(5, 3, 0));
            return Task.FromResult(ActResult.Success("took 3 planks"));
        };
        rig.On["place"] = (args, _) =>
        {
            var at = args.GetProperty("at").GetString()!;
            if (at == "4,64,1" && far++ == 0)
                return Task.FromResult(ActResult.Fail("can't place at 4,64,1: its nearest support face is 5.2 blocks from my eyes at 0.5,64,3.5 (reach 4.5) and I couldn't get closer"));
            var p = at.Split(',').Select(int.Parse).ToArray();
            rig.World.SetBlock(p[0], p[1], p[2], 5, 0);
            return Task.FromResult(ActResult.Success("placed planks at " + at));
        };
        var s = await rig.Run(MineClaw.Hive.Story.StoryColonies.PawnCode(), "story_pawn");
        Assert.True(s.Ok, s.Outcome);
        var calls = rig.Norm();
        Assert.Equal(2, calls.Count(c => c.StartsWith("withdraw ")));                    // busy once, then taken
        Assert.Contains(calls, c => c.StartsWith("goto ") && c.Contains("0,64,3"));       // the stand first
        Assert.DoesNotContain("""place {"at":"2,64,1","block":"planks"}""", calls);    // already built: not placed again
        Assert.Equal(2, calls.Count(c => c == """place {"at":"4,64,1","block":"planks"}""")); // too far: closer, then once more
        Assert.Equal([
            "maid_chan j1 progress fetched",
            "maid_chan j1 progress cell 1,64,1 done",
            "maid_chan j1 progress cell 2,64,1 done",
            "maid_chan j1 progress cell 3,64,1 done",
            "maid_chan j1 progress cell 4,64,1 done",
            "maid_chan j1 done batch: 4/4 placed",
            "maid_chan j2 done"], port.Reports);
    }

    [Fact]
    public async Task The_pawn_script_takes_and_holds_a_jobs_tool_runs_gear_jobs_and_attacks_the_exact_entity()
    {
        var port = new FakeDirector(
            J("j1", "dig", j =>
            {
                j["at"] = "4,64,4"; j["tool"] = "stone_pickaxe"; j["tool_ench"] = "none";
                j["fetch"] = new JsonArray(new JsonObject { ["from"] = "5,64,5", ["item"] = "stone_pickaxe", ["n"] = 1, ["ench"] = "none" });
            }),
            J("j2", "equip", j => { j["unwear"] = true; j["wear"] = new JsonArray("iron_helmet"); j["hold"] = "iron_sword"; j["hold_ench"] = "any"; }),
            J("j3", "attack", j => { j["at"] = "6,64,6"; j["mob"] = "zombie"; j["target"] = 50; }),
            J("j4", "end"));
        using var rig = Rig.Js();
        rig.Host.Story = port;
        rig.On["dig"] = (_, _) => { rig.World.SetBlock(4, 64, 4, 0, 0); return Task.FromResult(ActResult.Success("dug")); };
        var s = await rig.Run(MineClaw.Hive.Story.StoryColonies.PawnCode(), "story_pawn");
        Assert.True(s.Ok, s.Outcome);
        var calls = rig.Norm();
        int take = calls.IndexOf("""equip {"ench":"none","from":"5,64,5","item":"stone_pickaxe","mode":"take"}""");
        int hold = calls.IndexOf("""equip {"ench":"none","from":"","item":"stone_pickaxe","mode":"hold"}""");
        int dig = calls.IndexOf("""dig {"at":"4,64,4"}""");
        Assert.True(take >= 0 && take < hold && hold < dig, string.Join(" | ", calls)); // taken exactly, held, then dug
        int off = calls.IndexOf("""equip {"ench":"","from":"","item":"all","mode":"unwear"}""");
        int on = calls.IndexOf("""equip {"ench":"","from":"","item":"iron_helmet","mode":"wear"}""");
        int sword = calls.IndexOf("""equip {"ench":"any","from":"","item":"iron_sword","mode":"hold"}""");
        Assert.True(off >= 0 && off < on && on < sword, string.Join(" | ", calls));
        Assert.Contains("""attack {"target":"50"}""", calls);
        Assert.Equal(["maid_chan j1 done", "maid_chan j2 done", "maid_chan j3 done", "maid_chan j4 done"], port.Reports);
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
