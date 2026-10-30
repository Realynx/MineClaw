using MineClaw.Agent.Tools;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Hierarchy.Bus;
using Xunit.Abstractions;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Fox 10-30: "spawn up its bodies with a script already made, so its bodies just go straight to work." Main's side: a brain (or
/// kind=hive) with bodies and a script is ONE hive whose task carries the crew script (not a script-only clone: it keeps its brain and
/// hosts its bodies); the script is prepared on main (a DeepSeek hive only runs saved code); a hive's crew bodies come over the bus
/// tagged with its crew and no job of their own; a hive clone's own tool spawns bodies straight onto a saved crew script.
/// </summary>
public class HiveCrewSpawnHierarchyTests(ITestOutputHelper output)
{
    private static HierarchyKit Kit(Func<string?, string?, (string?, string?)>? prep = null)
    {
        var k = new HierarchyKit();
        k.H.ProfileCheck = _ => null;
        k.H.CrewPrep = prep ?? ((s, c) => (s ?? "hive_inline", null));
        return k;
    }

    [Theory]
    [InlineData("opus", null)]
    [InlineData("deepseek", null)]
    [InlineData(null, "hive")]
    public void A_brain_with_bodies_and_a_script_is_one_hive_whose_bodies_start_on_it(string? brain, string? kind)
    {
        var k = Kit();
        var r = k.H.Spawn(new SpawnRequest(Kind: kind, Brain: brain, Script: "deforest", Args: """{"r":48}""", Bodies: 4));
        output.WriteLine(r);
        Assert.StartsWith("ok", r);
        Assert.Contains($"ONE {(brain == "deepseek" ? "DeepSeek" : "Opus")} hive whose brain will drive 4 subhive bodies", r);
        Assert.Contains("they go straight to work on deforest as they arrive (no brain turn first; its brain wakes on deforest's result)", r);
        var (acc, task, opts) = Assert.Single(k.Bodies.Spawns);
        Assert.Equal(("hive", 4, "deforest", """{"r":48}"""), (opts.Kind, opts.Bodies, opts.Crew, opts.Args));
        Assert.Null(opts.Script); // the hive keeps its brain: no script-only clone
        Assert.Equal("crew deforest", task);
        Assert.Equal(CloneKind.Hive, k.H.Tree.Get(acc)!.Kind);
        if (brain == "deepseek") Assert.Equal(k.Cfg.DeepSeekProfile, opts.Profile);
    }

    [Fact]
    public void The_crew_script_is_prepared_on_main_and_without_a_brain_or_bodies_nothing_changes()
    {
        // Inline code from the Opus brain is linted and saved on main first; a refusal spawns nothing.
        var seen = new List<(string?, string?)>();
        var k = Kit((s, c) => { seen.Add((s, c)); return c != null && c.Contains("eval") ? (null, "lint errors (fix and resubmit): eval") : (s ?? "hive_abc123", null); });
        Assert.StartsWith("error: lint errors", k.H.Spawn(new SpawnRequest(Brain: "deepseek", Code: "eval('x')", Bodies: 2)));
        Assert.Empty(k.Bodies.Spawns);
        Assert.Equal(0, k.H.Tree.Count);
        Assert.StartsWith("ok", k.H.Spawn(new SpawnRequest(Brain: "deepseek", Code: "function w(){}\nfunction main(){ return everyBody('w').msg }", Bodies: 2)));
        Assert.Equal("hive_abc123", k.Bodies.Spawns.Single().Opts.Crew); // the hive runs the saved script by name
        Assert.Equal(2, seen.Count);

        // No prep wired: an honest no.
        var off = new HierarchyKit();
        off.H.ProfileCheck = _ => null;
        Assert.StartsWith("no: a hive can't start its bodies on a script here", off.H.Spawn(new SpawnRequest(Brain: "opus", Script: "x", Bodies: 2)));

        // A brain + a script without bodies stays the script spawn it was; script + bodies with no brain stays a brainless hive script.
        var k2 = Kit();
        Assert.StartsWith("ok", k2.H.Spawn(new SpawnRequest(Kind: "hive", Brain: "opus", Script: "x")));
        Assert.Equal(("x", null), (k2.Bodies.Spawns.Single().Opts.Script, k2.Bodies.Spawns.Single().Opts.Crew));
        Assert.StartsWith("no: hive scripts need subhive bodies here", k2.H.Spawn(new SpawnRequest(Script: "x", Bodies: 2)));
    }

    [Fact]
    public async Task A_hives_crew_bodies_come_over_the_bus_tagged_with_its_crew_and_no_job_of_their_own()
    {
        var k = Kit();
        var bus = new HiveBus(HierarchyKit.Main) { MainHandler = k.H.HandleBusAsync };
        k.H.Bus = bus;
        k.H.RemoteSubhives = true;
        Assert.StartsWith("ok", k.H.Spawn(new SpawnRequest(Brain: "opus", Script: "deforest", Bodies: 2)));
        const string Hive = "maid_chan_101";
        var hosted = new List<HostReq>();
        var token = bus.Register(Hive, (e, _) =>
        {
            if (e.Type == BusTypes.Host && e.As<HostReq>() is { } h) lock (hosted) hosted.Add(h);
            return Task.FromResult(BusReply.Of("ok: spawning"));
        });
        var reply = await bus.RouteAsync(BusEnvelope.Make(BusTypes.Spawn, Hive, "main",
            new SpawnReq(Kind: "subhive", Task: "crew deforest", Bodies: 2, Reset: true, Crew: "deforest@maid_chan_101#1-1"), token), Hive);
        Assert.StartsWith("ok: 2 subhive bodies (102, 103) for 101's brain", reply.As<string>());
        await Wait.Until(() => { lock (hosted) return hosted.Count == 2; }, 10, "2 hosted");
        Assert.All(hosted, h => Assert.Equal(("deforest@maid_chan_101#1-1", null, null), (h.Crew, h.Task, h.Script)));
        Assert.All(hosted, h => Assert.Equal(Hive, k.H.Tree.Get(h.Account)!.Owner));
    }

    [Fact]
    public async Task A_hive_clones_own_tool_spawns_bodies_straight_onto_a_saved_crew_script()
    {
        var host = new Clone.CloneHost("maid_chan_101", "no-pipe", new HiveConfig());
        var tools = new ToolRegistry();
        CloneHiveTool.Register(tools, host);
        var calls = new List<(string Script, string? Args, int N, string? Task)>();
        string Run(object input) => tools.ExecuteAsync("hive", System.Text.Json.JsonSerializer.SerializeToElement(input), default).GetAwaiter().GetResult().Text;

        // No bodies in this hive: an honest no, never a bus request that would spawn per-body jobs.
        Assert.StartsWith("no: this hive hosts no bodies of its own", Run(new { op = "spawn", bodies = 2, script = "deforest" }));
        host.CrewSpawn = (s, a, n, t) => { calls.Add((s, a, n, t)); return Task.FromResult($"ok: {n} subhive bodies (102, 103) for 101's brain; they go straight to work on {s}"); };
        var r = Run(new { op = "spawn", bodies = 2, script = "deforest", args = new { r = 32 } });
        Assert.StartsWith("ok: 2 subhive bodies", r);
        Assert.Equal(("deforest", """{"r":32}""", 2, (string?)null), calls.Single());
        // A brain named is a new hive (main's path, over the bus), not bodies of this one.
        Assert.Contains("hive bus", Run(new { op = "spawn", bodies = 2, script = "deforest", brain = "deepseek" }));
        Assert.Single(calls);

        // The clone's tool stays smaller than main's (one shared brain cap) and teaches the new shape in its own text.
        Assert.Contains("spawn bodies=N (+script=X: they start on X)", CloneHiveTool.Description);
        static int Tok(MineClaw.Agent.Llm.LlmToolDefinition d) => (int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6;
        int clone = Tok(tools.Definitions(MineClaw.Agent.Llm.ModelTier.Main).Single());
        output.WriteLine($"clone hive tool ≈{clone} tok (main's cap {MineClaw.Agent.Tools.ToolBudgets.HiveTool})");
        Assert.True(clone < MineClaw.Agent.Tools.ToolBudgets.HiveTool, $"clone hive tool ≈{clone}");
        await Task.CompletedTask;
    }
}
