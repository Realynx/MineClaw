using System.Text;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the live build loops (<see cref="ConstructionExecutor"/> and the crew's lane runner in <see cref="CrewExecutor"/>) by the
/// exact sequence of actions they issue to a body (goto, place, dig, pillar, water, ensure, …, each with its result), the progress
/// lines and the report, for representative builds on both versions (b1.8.1, and 1.12.2 game data with halves following the
/// cursor). A refactor of the executors must leave <see cref="ExecutorGoldenData.Expected"/> unchanged. Regenerate (only for an
/// intended change) by setting WP02_GOLDEN_OUT to a file path and running <see cref="Executor_traces_match_the_golden_dump"/>.
/// </summary>
public class ExecutorGoldenTests
{
    /// <summary>Records every call the executor makes on a body (and its answer), delegating to the inner body.</summary>
    internal sealed class TraceBody(IBuildBody inner, StringBuilder sink, bool halves) : IBuildBody
    {
        private static string P(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
        private static string R(ActionResult r) => r.Ok ? "ok" : "fail: " + r.Message;
        private string Rec(string line) { lock (sink) sink.Append("  ").Append(inner.Name).Append(' ').AppendLine(line); return line; }
        private async Task<ActionResult> Call(string what, Task<ActionResult> t)
        {
            var r = await t.ConfigureAwait(false);
            Rec(what + " -> " + R(r));
            return r;
        }

        public string Name => inner.Name;
        public WorldState World => inner.World;
        public bool HalvesFollowCursor => halves;
        public Task<ActionResult> GotoAsync(BlockPos stand, CancellationToken ct) => Call($"goto {P(stand)}", inner.GotoAsync(stand, ct));
        public Task<ActionResult> PlaceAsync(BuildStep step, int itemId, int damage, CancellationToken ct) =>
            Call($"place {step.Kind} {P(step.Pos)} {step.Id}:{step.Meta} item {itemId}:{damage} face {step.Face} hidden {step.Hidden} look {step.Look} yaw {step.Yaw}",
                inner.PlaceAsync(step, itemId, damage, ct));
        public Task<ActionResult> DigAsync(BlockPos pos, CancellationToken ct) => Call($"dig {P(pos)}", inner.DigAsync(pos, ct));
        public int Count(int itemId, int damage) => inner.Count(itemId, damage);
        public async Task<string?> EnsureAsync(int itemId, int damage, int count, CancellationToken ct)
        {
            var r = await inner.EnsureAsync(itemId, damage, count, ct).ConfigureAwait(false);
            Rec($"ensure {itemId}:{damage} x{count} -> {r ?? "ok"}");
            return r;
        }
        public async Task<string?> ClutchAsync(CancellationToken ct)
        {
            var r = await inner.ClutchAsync(ct).ConfigureAwait(false);
            Rec($"clutch -> {r ?? "ok"}");
            return r;
        }
        public bool IsProtected(BlockPos p, int id) => inner.IsProtected(p, id);
        public Task<ActionResult> DigHereAsync(BlockPos pos, CancellationToken ct) => Call($"dighere {P(pos)}", inner.DigHereAsync(pos, ct));
        public Task<ActionResult> PourWaterAsync(BlockPos source, BlockFace face, CancellationToken ct) => Call($"pour {P(source)} {face}", inner.PourWaterAsync(source, face, ct));
        public Task<ActionResult> SwimToDepthAsync(SwimColumn column, BlockPos entry, double feetY, CancellationToken ct) =>
            Call($"swim {P(entry)} feet {feetY}", inner.SwimToDepthAsync(column, entry, feetY, ct));
        public Task<ActionResult> SwimOutAsync(SwimColumn column, BlockPos exit, CancellationToken ct) => Call($"swimout {P(exit)}", inner.SwimOutAsync(column, exit, ct));
        public Task<ActionResult> PickupWaterAsync(BlockPos source, CancellationToken ct) => Call($"pickup {P(source)}", inner.PickupWaterAsync(source, ct));
        public Task<ActionResult> PillarAsync(BlockPos feet, int itemId, CancellationToken ct) => Call($"pillar {P(feet)} item {itemId}", inner.PillarAsync(feet, itemId, ct));
        public bool Available => inner.Available;
        public double? BackInSeconds => inner.BackInSeconds;
        public IClaimBoard? Claims => inner.Claims;
        public IDisposable? Enter() => inner.Enter();
        public Task<string?> ReturnAsync(int itemId, int damage, CancellationToken ct) => inner.ReturnAsync(itemId, damage, ct);
    }

    private static VoxelPlan PlanOf(string dsl, BlockPos origin)
    {
        var (plan, issues) = BlueprintNormalizer.FromText(dsl.ReplaceLineEndings("\n"), origin);
        Assert.NotNull(plan);
        Assert.False(issues.Any(i => i.Level == IssueLevel.Error), dsl + ": " + string.Join("; ", issues));
        return plan!;
    }

    private const string Bridge = "size 6x1x1\npalette C=cobblestone\nlayer 0\nCCCCCC";
    private const string Stairs = "size 1x4x4\npalette S=cobblestone_stairs:north C=cobblestone\nlayer 0\n.\n.\n.\nS\nlayer 1\n.\n.\nS\nC\nlayer 2\n.\nS\nC\nC\nlayer 3\nS\nC\nC\nC";
    private const string Slabs = "size 4x2x1\npalette H=slab:cobble C=cobblestone\nlayer 0\nCHCH\nlayer 1\nH.H.";

    private delegate Task<string> Scenario(StringBuilder sink, bool halves);

    private static readonly (string Name, Scenario Run)[] Scenarios =
    [
        ("hut", (s, h) => Single(s, h, BlueprintTests.Island(), BlueprintTests.Hut.Replace("palette C=cobblestone", "palette S=cobblestone_stairs C=cobblestone") + "\nstairs S 1,0,1 north",
            new BlockPos(-2, 64, -3), inv: b => b.Inv[(Blocks.CobblestoneStairs, 0)] = 4)),
        ("platform scaffold", (s, h) => Single(s, h, BlueprintTests.Island(4), "size 3x3x3\npalette C=cobblestone\nlayer 2\nCCC\nCCC\nCCC", new BlockPos(-1, 64, -1))),
        ("staircase", (s, h) => Single(s, h, BlueprintTests.Island(), Stairs, new BlockPos(0, 64, -2), inv: b => b.Inv[(Blocks.CobblestoneStairs, 0)] = 8)),
        ("slabs", (s, h) => Single(s, h, BlueprintTests.Island(), Slabs, new BlockPos(0, 64, 0), inv: b => b.Inv[(Blocks.Slab, 3)] = 8)),
        ("overhang", (s, h) => Single(s, h, BlueprintTests.Island(), "size 4x1x1\npalette C=cobblestone\nlayer 0\nCCCC", new BlockPos(7, 63, 0))),
        ("hollow tower", (s, h) => Single(s, h, BlueprintTests.Island(4), ClimbTests.HollowTower, new BlockPos(-1, 64, -1))),
        ("water column", (s, h) => Single(s, h, BlueprintTests.Island(3), "size 3x1x3\npalette C=cobblestone\nlayer 0\nCCC\nCCC\nCCC", new BlockPos(1, 57, -1),
            inv: b => { b.Inv.Clear(); b.Inv[(Blocks.Cobblestone, 0)] = 256; b.Inv[(Items.WaterBucket, 0)] = 1; })),
        ("infeasible", (s, h) => Single(s, h, BlueprintTests.Island(3), "size 3x1x3\npalette C=cobblestone\nlayer 0\nCCC\nCCC\nCCC", new BlockPos(1, 50, -1))),
        ("shortfall", (s, h) => Single(s, h, BlueprintTests.Island(), BlueprintTests.Hut, new BlockPos(-2, 64, -3), inv: b => b.Inv[(Blocks.Planks, 0)] = 5)),
        ("no scaffold blocks", (s, h) => Single(s, h, BlueprintTests.Island(4), "size 3x3x3\npalette G=glass\nlayer 2\nGGG\nGGG\nGGG", new BlockPos(-1, 64, -1),
            inv: b => { b.Inv.Clear(); b.Inv[(Blocks.Glass, 0)] = 64; })),
        ("foreign block", (s, h) => Single(s, h, BlueprintTests.Island(), BlueprintTests.Hut, new BlockPos(-2, 64, -3),
            world: w => w.SetBlock(-2, 65, -3, Blocks.Dirt, 0))),
        ("protected obstruction", (s, h) => Single(s, h, BlueprintTests.Island(), BlueprintTests.Hut, new BlockPos(-2, 64, -3),
            world: w => { w.SetBlock(0, 65, -1, Blocks.Dirt, 0); w.SetBlock(0, 66, -1, Blocks.Stone, 0); },
            inv: b => b.Protect = (p, id) => id == Blocks.Stone)),
        ("layer 1", (s, h) => Single(s, h, BlueprintTests.Island(), BlueprintTests.Hut, new BlockPos(-2, 64, -3), layer: 1)),
        ("jammed", (s, h) => Single(s, h, BlueprintTests.Island(), Bridge, new BlockPos(0, 64, 0), inv: b => b.Jammed = true)),
        ("gate approve", (s, h) => Gated(s, h, n => new GateReply(GateVerdict.Approve, "PoofImaFox", "keep going"))),
        ("gate correct", (s, h) => Gated(s, h, n => n == 1
            ? BuildGate.Parse("wrong way", "PoofImaFox", new BlockPos(0, 64, 0), 'x')
            : new GateReply(GateVerdict.Approve, "PoofImaFox", "keep going"))),
        ("gate hold", (s, h) => Gated(s, h, n => new GateReply(GateVerdict.Hold, "PoofImaFox", "stop"))),
        ("gate undo", (s, h) => Gated(s, h, n => new GateReply(GateVerdict.Undo, "PoofImaFox", "undo"))),
        ("gate timeout", (s, h) => Gated(s, h, n => null)),
        ("until gate", (s, h) => Gated(s, h, n => new GateReply(GateVerdict.Approve, "PoofImaFox", "keep going"), untilGate: true)),
        ("rule hold", (s, h) => Single(s, h, BlueprintTests.Island(4), ClimbTests.HollowTower, new BlockPos(-1, 64, -1), holdAfter: 40)),
        ("resume", Resume),
        ("crew 1 body", (s, h) => Crew(s, h, 1)),
        ("crew lanes", Lanes),
    ];

    private static async Task<string> Single(StringBuilder sink, bool halves, WorldState w, string dsl, BlockPos at, Action<SimBuildBody>? inv = null,
        Action<WorldState>? world = null, int? layer = null, int holdAfter = -1)
    {
        var plan = PlanOf(dsl, at);
        world?.Invoke(w);
        var sim = new SimBuildBody(w);
        inv?.Invoke(sim);
        var body = new TraceBody(sim, sink, halves);
        int places = 0;
        var ex = new ConstructionExecutor(new BlueprintOptions());
        ex.Progress = l => { lock (sink) sink.Append("  > ").AppendLine(l); };
        if (holdAfter >= 0) ex.Hold = _ => ++places > holdAfter ? "the tower only in daytime" : null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var r = await ex.RunAsync(1, plan, BuildPart.Whole(plan), body, cts.Token, layer);
        return Report(r);
    }

    private static string Report(BuildReport r) =>
        $"report {r.Text("x")} | total {r.Total} failed {r.Failed} unreachable {r.Unreachable.Count} gate {r.AtGate}/{r.GatePassed} complete {r.Complete}" +
        $" | plan {r.Plan?.State} {r.Plan?.Gate.State} {r.Plan?.Note} placed {r.Plan?.Placed.Count} scaffold {r.Plan?.ScaffoldUp.Count} exit {r.Plan?.Exit.Count}";

    private static async Task<string> Gated(StringBuilder sink, bool halves, Func<int, GateReply?> answer, bool untilGate = false)
    {
        var w = BlueprintTests.Island();
        var plan = PlanOf(Bridge, new BlockPos(0, 64, 0));
        var gates = new BuildGates();
        int asked = 0;
        using var db = AgentDb.Open(null);
        var store = new BlueprintStore(db);
        // Long enough for an answer to arrive; short when none comes.
        var ex = new ConstructionExecutor(new BlueprintOptions { GateMinutes = answer(1) == null ? 0 : 1, GateBlocks = 3 }, store)
        {
            Gates = gates,
            Ask = q =>
            {
                lock (sink) sink.Append("  ? ").AppendLine(q);
                if (answer(++asked) is { } reply)
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < 400 && !gates.IsWaiting(5); i++) await Task.Delay(5);
                        gates.Answer(5, reply);
                    });
                return Task.CompletedTask;
            },
        };
        ex.Progress = l => { lock (sink) sink.Append("  > ").AppendLine(l); };
        var body = new TraceBody(new SimBuildBody(w), sink, halves);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var r = await ex.RunAsync(5, plan, BuildPart.Whole(plan), body, cts.Token, untilGate: untilGate);
        var rec = store.GetBuild(5, 0);
        return Report(r) + $" | record {rec?.State} {rec?.Placed}/{rec?.Total} {rec?.Note}";
    }

    private static async Task<string> Resume(StringBuilder sink, bool halves)
    {
        var w = BlueprintTests.Island();
        var plan = PlanOf(BlueprintTests.Hut, new BlockPos(-2, 64, -3));
        using var db = AgentDb.Open(null);
        var sim = new SimBuildBody(w) { PlaceLimit = 20 };
        using (var stop = new CancellationTokenSource())
        {
            sim.Stop = stop;
            var ex1 = new ConstructionExecutor(new BlueprintOptions { CheckpointEvery = 4 }, new BlueprintStore(db));
            ex1.Progress = l => { lock (sink) sink.Append("  > ").AppendLine(l); };
            var r1 = await ex1.RunAsync(7, plan, BuildPart.Whole(plan), new TraceBody(sim, sink, halves), stop.Token);
            sink.AppendLine("first " + Report(r1));
        }
        sim.PlaceLimit = int.MaxValue;
        sim.Stop = null;
        var ex2 = new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db));
        ex2.Progress = l => { lock (sink) sink.Append("  > ").AppendLine(l); };
        var r2 = await ex2.RunAsync(7, plan, BuildPart.Whole(plan), new TraceBody(sim, sink, halves), CancellationToken.None);
        return Report(r2);
    }

    private static string CrewText(CrewReport r) =>
        $"crew {r.Text("build")} | complete {r.Complete} rounds {r.Rounds.Count} rebalances {r.Rebalances} returned {r.Returned}" +
        (r.Finale != null ? " | finale " + Report(r.Finale) : "") + (r.Gate != null ? " | gate " + Report(r.Gate) : "") +
        string.Concat(r.Lanes.Select(l => $" | lane {l.Lane} {l.Text} left {l.Left} why {l.Why} skips {l.Skips}"));

    private static async Task<string> Crew(StringBuilder sink, bool halves, int n)
    {
        var (_, sims) = CrewBuildTests.Crew(n);
        var bodies = sims.Select(b => (IBuildBody)new TraceBody(b, sink, halves)).ToList();
        var plan = PlanOf(CrewBuildTests.Hall, CrewBuildTests.HallAt);
        using var db = AgentDb.Open(null);
        var crew = CrewBuildTests.Fast(new ConstructionExecutor(new BlueprintOptions { GateBlocks = 3 }, new BlueprintStore(db))
        {
            Gates = new BuildGates(),
        });
        crew.Progress = l => { lock (sink) sink.Append("  > ").AppendLine(l); };
        var r = await crew.RunAsync(1, plan, bodies, CrewBuildTests.Soon());
        return CrewText(r);
    }

    /// <summary>A two-lane crew plan; each lane run on its own, one after the other (the hive clone's side), so the trace is deterministic.</summary>
    private static async Task<string> Lanes(StringBuilder sink, bool halves)
    {
        var (_, sims) = CrewBuildTests.Crew(2);
        var plan = PlanOf("size 10x2x1\npalette C=cobblestone\nlayer 0-1\nCCCCCCCCCC", new BlockPos(-5, 64, 0));
        using var db = AgentDb.Open(null);
        var store = new BlueprintStore(db);
        var single = new ConstructionExecutor(new BlueprintOptions(), store);
        var crew = CrewBuildTests.Fast(single);
        crew.Progress = l => { lock (sink) sink.Append("  > ").AppendLine(l); };
        var w = sims[0].World;
        var bp = CrewSlicer.Slice(plan, w.PlanningView, sims.Select(b => new CrewMember(b.Name, b.Feet0)).ToList(), null, new BuildPlannerOptions { GateBlocks = 0 });
        bp.BlueprintId = 3;
        bp.Version = plan.Hash();
        bp.State = "building";
        single.Plans!.Save(bp);
        var sb = new StringBuilder();
        for (int k = 0; k < bp.Lanes.Count; k++)
        {
            var body = new TraceBody(sims.Single(b => b.Name == bp.Lanes[k].Body), sink, halves);
            var lr = await crew.RunLaneAsync(3, plan, k, body, CrewBuildTests.Soon());
            sb.Append($"lane {lr.Lane} {lr.Text} left {lr.Left} why {lr.Why} skips {lr.Skips}; ");
        }
        sb.Append(" | missing lane " + (await crew.RunLaneAsync(3, plan, 9, new TraceBody(sims[0], sink, halves), CancellationToken.None)).Why);
        return sb.ToString();
    }

    internal static async Task<string> Dump()
    {
        var all = new StringBuilder();
        foreach (var modern in new[] { false, true })
            foreach (var (name, run) in Scenarios)
            {
                var sink = new StringBuilder();
                string result;
                using (modern ? GameData.Override(Release1122GameData.Instance) : null)
                    result = await run(sink, modern);
                all.Append("== ").Append(modern ? "1.12.2 " : "b1.8.1 ").AppendLine(name);
                all.Append(sink);
                all.AppendLine(result);
            }
        return all.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public async Task Executor_traces_match_the_golden_dump()
    {
        var actual = await Dump();
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) await File.WriteAllTextAsync(path, actual);
        // The raw literal drops the dump's last newline (and a CRLF checkout turns its newlines into "\r\n").
        var expected = ExecutorGoldenData.Expected.Replace("\r\n", "\n").TrimEnd('\n');
        actual = actual.TrimEnd('\n');
        if (expected != actual)
        {
            var a = actual.Split('\n');
            var e = expected.Split('\n');
            int i = 0;
            while (i < Math.Min(a.Length, e.Length) && a[i] == e[i]) i++;
            Assert.Fail($"executor trace differs at line {i + 1}:\n expected: {(i < e.Length ? e[i] : "<end>")}\n actual:   {(i < a.Length ? a[i] : "<end>")}");
        }
    }
}
