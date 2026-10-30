using System.Collections.Concurrent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.GameTools;
using MineClaw.Scripting.Crew;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Fox 10-30: "a hive should be able to assign scripts to all or some or just one of its bodies." The brain's
/// <c>script{name, body:[…]|"all"|"67"}</c> on a crew script is ONE bodiless coordinator over exactly those bodies (no copy per body,
/// no body of the brain's held); bodies outside the subset keep their jobs; a body another live crew holds is reported, not taken;
/// the crew's one result wakes the brain.
/// </summary>
public partial class MultiBodyBrainTests
{
    private const string CrewJob = """
        // purpose: each body does a short job; one result line
        function worker() { wait(0.3); crew.inc('n'); return 'worked' }
        function main() { return `crew: ${everyBody('worker').msg}` }
        """;

    private const string LongJob = """
        // purpose: each body works a long time
        function worker() { for (let i = 0; i < 600; i++) wait(0.1); return 'long' }
        function main() { return everyBody('worker').msg }
        """;

    private static async Task<(Rig Rig, BrainLlm Llm, ScriptBridge Scripts, ConcurrentQueue<GameEvent> Posted)> CrewRig(params string[] clones)
    {
        var llm = new BrainLlm();
        var rig = await StartAsync(DualGen, llm, null, clones);
        var scripts = ScriptTools.Of(rig.Rt)!;
        scripts.Crew = new BodyCrewPort(rig.Reg, rig.Rt.Tools, new InMemoryCrewBoard()); // AppHost: main's bodies are its crew lanes
        scripts.Library.SaveVersion("crew_job", CrewJob, author: "main/claude-opus-5-5");
        scripts.Library.SaveVersion("crew_long", LongJob, author: "main/claude-opus-5-5");
        var posted = new ConcurrentQueue<GameEvent>();
        rig.Rt.Events.Posted += posted.Enqueue;
        return (rig, llm, scripts, posted);
    }

    /// <summary>One brain turn issuing <paramref name="input"/> on the script tool; its tool result.</summary>
    private static async Task<LlmToolResult> BrainCall(Rig rig, BrainLlm llm, object input)
    {
        int before;
        lock (llm.Brain) before = llm.Brain.Count;
        llm.Tool("script", input).Text("on it");
        await rig.Rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "get to work")]), CancellationToken.None);
        lock (llm.Brain) return llm.Brain[before + 1].Messages[^1].Content.OfType<LlmToolResult>().Single();
    }

    private static string? CrewEnd(ConcurrentQueue<GameEvent> posted, string script) =>
        posted.FirstOrDefault(e => e.Text.StartsWith($"[crew {script} ", StringComparison.Ordinal))?.Text;

    [Fact]
    public async Task The_brain_runs_one_crew_over_some_one_or_all_of_its_bodies_and_the_rest_keep_their_jobs()
    {
        var (rig, llm, scripts, posted) = await CrewRig("maid_chan_67", "maid_chan_69", "maid_chan_101");
        await using var rigScope = rig;
        // 101 is busy with its own job; a crew over 67 and 69 must leave it alone.
        var b101 = (Body)rig.Reg.Find("101")!;
        var hold = new DelegateTool("hold_still", "test", ToolSchema.Empty, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ToolResult.Success("held"); }, longRunning: true);
        _ = b101.Activities.RunAsync([(hold, ToolSchema.Empty)], TimeSpan.Zero, CancellationToken.None);
        Assert.True(await Until(() => b101.Activities.IsBusy));

        // ---- some: ONE coordinator over 67 and 69 (not a copy per body), main's own body free
        var some = await BrainCall(rig, llm, new { op = "run", name = "crew_job", body = new[] { "67", "69" } });
        output.WriteLine(some.Content);
        Assert.False(some.IsError, some.Content);
        Assert.StartsWith("ok: crew crew_job running on 2 bodies (67,69): ONE coordinator", some.Content);
        Assert.Null(scripts.Controller.Current); // main's body isn't the coordinator
        Assert.True(await Until(() => CrewEnd(posted, "crew_job") != null, 15000), "the crew's one result");
        var end = CrewEnd(posted, "crew_job")!;
        output.WriteLine(end);
        Assert.Contains("[crew crew_job done] crew: 2/2 bodies done (67 worked, 69 worked)", end);
        Assert.True(posted.Single(e => e.Text == end).MustWake);
        Assert.True(b101.Activities.IsBusy, "101 kept its own job");
        Assert.Null(b101.Scripts!.Last); // no lane ever reached it
        foreach (var n in new[] { "67", "69" }) Assert.Equal("worker", ((Body)rig.Reg.Find(n)!).Scripts!.Last!.Request.Role);
        // Each lane says whose crew it is (the Controller panel's view).
        var snap = ((Body)rig.Reg.Find("67")!).Scripts!.Last!.DebugSnapshot(false);
        Assert.Equal(("worker", "maid_chan"), (snap.Role, snap.Coordinator));
        Assert.StartsWith("crew_job@maid_chan#", snap.Crew);

        // ---- one: just 69
        posted.Clear();
        var one = await BrainCall(rig, llm, new { op = "run", name = "crew_job", body = "69" });
        Assert.StartsWith("ok: crew crew_job running on 1 body (69)", one.Content);
        Assert.True(await Until(() => CrewEnd(posted, "crew_job") != null, 15000));
        Assert.Contains("1/1 bodies done (69 worked)", CrewEnd(posted, "crew_job"));

        // ---- all: every body, 101 included once it is free
        b101.Activities.Cancel("test: free now");
        Assert.True(await Until(() => !b101.Activities.IsBusy));
        posted.Clear();
        var all = await BrainCall(rig, llm, new { op = "run", name = "crew_job", body = "all" });
        Assert.StartsWith("ok: crew crew_job running on all your bodies, late ones too", all.Content);
        Assert.True(await Until(() => CrewEnd(posted, "crew_job") != null, 15000));
        Assert.Contains("3/3 bodies done", CrewEnd(posted, "crew_job"));

        // A script that isn't a crew script still runs once per body, as before.
        scripts.Library.SaveVersion("solo", "// purpose: one body\nwait(0.2)\nreturn 'solo ' + me.body", author: "main/claude-opus-5-5");
        var solo = await BrainCall(rig, llm, new { op = "run", name = "solo", body = new[] { "67", "69" } });
        Assert.Contains("67: ", solo.Content);
        Assert.Contains("69: ", solo.Content);
    }

    [Fact]
    public async Task A_body_another_live_crew_holds_is_reported_not_taken()
    {
        var (rig, llm, scripts, posted) = await CrewRig("maid_chan_67", "maid_chan_69");
        await using var rigScope = rig;
        var first = await BrainCall(rig, llm, new { op = "run", name = "crew_long", body = new[] { "67" } });
        Assert.False(first.IsError, first.Content);
        var b67 = (Body)rig.Reg.Find("67")!;
        Assert.True(await Until(() => b67.Scripts!.Current is { Request.Role: "worker" }), "67 on crew_long's lane");
        var held = b67.LiveCrew;
        Assert.StartsWith("crew_long@", held);

        // A list naming 67 only: nothing to take.
        var named = await BrainCall(rig, llm, new { op = "run", name = "crew_job", body = "67" });
        output.WriteLine(named.Content);
        Assert.True(named.IsError);
        Assert.Contains($"no body free for this crew: 67 (in crew {held})", named.Content);

        // "all": 69 only, 67 reported; 67's lane is never touched.
        var all = await BrainCall(rig, llm, new { op = "run", name = "crew_job", body = "all" });
        output.WriteLine(all.Content);
        Assert.False(all.IsError, all.Content);
        Assert.Contains($"not taken: 67 (in crew {held})", all.Content);
        Assert.True(await Until(() => CrewEnd(posted, "crew_job") != null, 15000));
        Assert.Contains("1/1 bodies done (69 worked)", CrewEnd(posted, "crew_job"));
        Assert.Equal(held, b67.Scripts!.Current?.Request.CrewId); // still crew_long's lane

        // The brain sees and ends its crews by name.
        var status = await BrainCall(rig, llm, new { op = "status" });
        Assert.Contains("crew crew_long on 67", status.Content);
        var cancel = await BrainCall(rig, llm, new { op = "cancel", name = "crew_long" });
        Assert.StartsWith("crew crew_long cancelled", cancel.Content);
        Assert.True(await Until(() => b67.Scripts!.Current == null, 10000), "its lane stopped with it");
        Assert.Null(b67.LiveCrew);
        Assert.Empty(scripts.CrewRuns);
    }
}
