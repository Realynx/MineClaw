using System.Globalization;
using MineClaw.Bot.Building;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Blueprints;

/// <summary>What one lane (one body) of a crew build did.</summary>
public sealed class LaneReport
{
    public int Lane { get; init; }
    public string Body { get; init; } = "";
    public int Placed, Already, Cleared, Scaffolded, Failed, Left;
    /// <summary>done | yielded (re-slice) | lost (body gone/dead) | stuck (failing over and over) | cancelled.</summary>
    public string State { get; set; } = "building";
    public string? Why { get; set; }
    /// <summary>Why this lane's left steps are left, grouped (<see cref="BuildSkips.Line"/>); null when none are.</summary>
    public string? Skips { get; set; }
    public List<string> Deviations { get; } = new();

    public string Text => $"{Short(Body)} {State}: placed {Placed}" + (Already > 0 ? $", {Already} already there" : "") + (Cleared > 0 ? $", cleared {Cleared}" : "")
        + (Scaffolded > 0 ? $", scaffold {Scaffolded}" : "") + (Left > 0 ? $", {Left} left" : "") + (Skips != null ? $" — {Skips}" : "") + (Why != null ? $" ({Why})" : "");

    internal static string Short(string body) => Bodies.Body.ShortName(body, false);
}

/// <summary>What a crew build achieved: the gate phase, every lane, the finale (stragglers, scaffold, water) and re-balances.</summary>
public sealed class CrewReport
{
    public BuildReport? Gate { get; set; }
    public List<LaneReport> Lanes { get; } = new();
    public BuildReport? Finale { get; set; }
    public int Rebalances { get; set; }
    public string? Stopped { get; set; }
    public bool AtGate { get; set; }
    public BuildPlan? Plan { get; set; }
    /// <summary>Each round's crew plan (the first slice, then one per re-balance or crew change).</summary>
    public List<BuildPlan> Rounds { get; } = new();
    public bool Complete => Stopped == null && Finale?.Complete == true;

    public string Text(string what)
    {
        var lanes = Lanes.GroupBy(l => l.Body).Select(g => g.Last()).ToList();
        int placed = Lanes.Sum(l => l.Placed) + (Gate?.Placed ?? 0) + (Finale?.Placed ?? 0);
        var text = $"{what}: placed {placed} with {Math.Max(1, lanes.Count)} bod{(lanes.Count == 1 ? "y" : "ies")}";
        if (lanes.Count > 0) text += " (" + string.Join("; ", lanes.Select(l => l.Text)) + ")";
        if (Rebalances > 0) text += $", re-balanced {Rebalances}×";
        if (Finale is { } f && (f.ScaffoldRemoved > 0 || f.Placed > 0)) text += $"; finish: placed {f.Placed}, scaffold removed {f.ScaffoldRemoved}";
        if (Finale?.Missing.Count > 0) text += $", {Finale.Missing.Count} missing";
        // The build's skip reasons: the finale's (every cell left), else the plan's own (it never got that far).
        if ((Finale?.Skips ?? (Plan != null ? BuildSkips.OfPlan(Plan) : null)) is { } skips) text += " — " + skips;
        if (Stopped != null) text += "; stopped: " + Stopped;
        return text;
    }
}

/// <summary>
/// Runs a build with a crew of bodies (ARCHITECTURE.md "Blueprints", crew builds):
/// <list type="number">
/// <item>gate: one body places the first blocks and asks once (the one confirmation gate of the whole build; the others wait);</item>
/// <item>lanes: the slicer splits what remains into one lane per body (<see cref="CrewSlicer"/>); the lanes run in parallel,
///   each waiting only for the other lanes' steps it needs, stand cells reserved so no two bodies use one (and leased on the
///   hive claim board), each body fetching its own share of the bill from storage one at a time; progress is persisted per
///   lane (resume per lane after a restart);</item>
/// <item>a body that dies, leaves or gets stuck: its lane waits for it when that is faster (a respawn), else the remaining steps
///   are sliced again over the others; a body joining or leaving mid-build re-slices too;</item>
/// <item>finale: whoever finishes last runs the one-body executor on the persisted plan: anything left (a missing item, a
///   failed step), scaffolding down top-first, water taken back, the build recorded done.</item>
/// </list>
/// </summary>
public sealed class CrewExecutor(ConstructionExecutor single, TimeProvider? time = null, ILogger? log = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ConstructionExecutor Single => single;
    /// <summary>How often a waiting lane looks again.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Longest wait for another lane's step or a stand before the step is deferred (and, at the end, the finale's).</summary>
    public TimeSpan WaitLimit { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>A lane waiting this long steps out of the way (to where it started) when another lane needs its cell.</summary>
    public TimeSpan ParkAfter { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Failed steps in a row before a lane counts as stuck.</summary>
    public int StuckAfter { get; init; } = 5;
    public int MaxRebalances { get; init; } = 4;
    /// <summary>Re-slicing costs this much (stopping the lanes, slicing, walking to new regions) in the wait-or-re-balance choice.</summary>
    public TimeSpan RebalanceCost { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>A remote lane (hive job) with no progress for this long is given up (its steps go to the finale).</summary>
    public TimeSpan RemoteStall { get; init; } = TimeSpan.FromMinutes(8);
    /// <summary>How often lanes in other processes are read back (their progress rows in the shared db).</summary>
    public TimeSpan RemotePoll { get; init; } = TimeSpan.FromSeconds(2);
    public Action<string>? Progress { get; set; }
    /// <summary>The crew as it is now (polled while lanes run): a body joining or leaving re-slices the remaining steps.</summary>
    public Func<IReadOnlyList<IBuildBody>>? Live { get; set; }

    internal int CheckpointEvery => Math.Max(1, single.Options0.CheckpointEvery);
    internal ILogger? Log => log;
    internal TimeProvider Time => _time;

    // ------------------------------------------------------------------ in-process crew

    /// <param name="live">The crew as it is now (default <see cref="Live"/>): a change re-slices what remains.</param>
    public async Task<CrewReport> RunAsync(long id, VoxelPlan design, IReadOnlyList<IBuildBody> bodies, CancellationToken ct,
        Func<IReadOnlyList<IBuildBody>>? live = null)
    {
        live ??= Live;
        var report = new CrewReport();
        if (bodies.Count == 0) { report.Stopped = "no body to build with"; return report; }
        int dim = bodies[0].World.Dimension;
        try
        {
            if (await GateAsync(id, design, bodies[0], report, ct).ConfigureAwait(false) is false) return report;
            var crew = bodies.Where(b => b.Available).ToList();
            if (crew.Count == 0) crew = bodies.ToList();
            IBuildBody? last = null;
            BuildPlan? current = null;
            for (int round = 0; ; round++)
            {
                ct.ThrowIfCancellationRequested();
                var (bp, voxel) = Prepare(id, design, crew, current, resume: round == 0);
                current = bp;
                report.Plan = bp;
                report.Rounds.Add(bp);
                if (bp.Lanes.Count <= 1 || !bp.Lanes.Any(l => l.Steps.Any(i => !bp.Steps[i].Finished))) break;
                var ctx = new CrewContext(id, voxel, bp, single.Plans, ownsPlan: true);
                var runs = bp.Lanes.Select(l => Start(ctx, l, crew.First(b => b.Name == l.Body), ct)).ToList();
                var (next, lastBody) = await CoordinateAsync(ctx, runs, crew, report, live, ct).ConfigureAwait(false);
                ctx.Stands.Clear();
                if (lastBody != null) last = lastBody;
                single.Plans?.Save(bp);
                if (next == null || round >= MaxRebalances) break;
                report.Rebalances++;
                crew = next;
                Progress?.Invoke($"re-slicing the rest over {string.Join(", ", crew.Select(b => LaneReport.Short(b.Name)))}");
            }
            // Finale: whoever finished last takes the scaffolding down and does what's left.
            var fin = last is { Available: true } ? last : crew.FirstOrDefault(b => b.Available) ?? bodies[0];
            var f = await single.RunAsync(id, design, BuildPart.Whole(design, dim), fin, ct, previous: current).ConfigureAwait(false);
            report.Finale = f;
            report.Plan = f.Plan;
            report.Stopped = f.Stopped;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report.Stopped = "cancelled";
        }
        return report;
    }

    /// <summary>The one gate of the build, by one body; false = not passed (paused at it, undone, or the build ended there).</summary>
    private async Task<bool> GateAsync(long id, VoxelPlan design, IBuildBody gateBody, CrewReport report, CancellationToken ct)
    {
        // A stale plan keeps its gate (BuildPlan.Resume): an approved build never asks again.
        var prev = BuildPlan.Resume(single.Plans?.Get(id, 0), design.Hash());
        if (!single.GateOn || prev?.Gate.State is GateState.Approved or GateState.Off) return true;
        var g = await single.RunAsync(id, design, BuildPart.Whole(design, gateBody.World.Dimension), gateBody, ct, untilGate: true).ConfigureAwait(false);
        report.Gate = g;
        report.Plan = g.Plan;
        if (g.GatePassed) return true;
        if (g.Complete) report.Finale = g;
        else
        {
            report.Stopped = g.Stopped ?? "paused at the confirmation gate";
            report.AtGate = g.AtGate;
        }
        return false;
    }

    /// <summary>
    /// The plan for this round: the persisted lanes again when the same crew comes back to an unfinished slicing (restart: each
    /// lane resumes from its own progress), else a fresh slice of what remains over <paramref name="crew"/>.
    /// </summary>
    private (BuildPlan Plan, VoxelPlan Voxel) Prepare(long id, VoxelPlan design, IReadOnlyList<IBuildBody> crew, BuildPlan? current, bool resume)
    {
        var plans = single.Plans;
        var loaded = current ?? plans?.Get(id, 0);
        var prev = BuildPlan.Resume(loaded, design.Hash()); // stale: no lanes to resume, re-sliced keeping the ledger and gate
        if (prev != null && current == null && ReferenceEquals(prev, loaded)) plans?.MergeLanes(prev);
        var w = crew[0].World;
        int dim = w.Dimension;
        var (voxel, vpart) = ConstructionExecutor.Shape(design, BuildPart.Whole(design, dim), prev?.Transform ?? BuildTransform.None, dim);
        if (resume && prev is { Lanes.Count: > 1 } && prev.SliceId.Length > 0 && prev.State is not ("done" or "undone")
            && SameNames(prev.Lanes.Select(l => l.Body), crew.Select(b => b.Name)))
        {
            Reverify(prev, voxel, w);
            foreach (var l in prev.Lanes) l.State = "planned";
            prev.State = "building";
            plans?.Save(prev);
            Progress?.Invoke($"resuming the crew build: {prev.LaneProgress()}");
            return (prev, voxel);
        }
        var members = crew.Select(b => new CrewMember(b.Name, ConstructionExecutor.StartCell(b.World), b.Count(Items.WaterBucket, 0) > 0)).ToList();
        var o = single.Options(crew[0], vpart, prev) with
        {
            WaterAvailable = members.Any(m => m.Water),
            Buckets = Math.Max(1, crew.Max(b => b.Count(Items.WaterBucket, 0))),
        };
        var bp = CrewSlicer.Slice(voxel, w.PlanningView, members, vpart, o, prev);
        bp.BlueprintId = id;
        bp.Version = design.Hash();
        bp.Dim = dim;
        bp.State = "building";
        plans?.Save(bp);
        if (bp.Lanes.Count > 1) Progress?.Invoke($"split into {bp.Lanes.Count} lanes: {bp.CrewText()}");
        log?.LogInformation("blueprint #{Id}: {Lanes} lane(s) over {Crew}; {Text}", id, bp.Lanes.Count, string.Join(",", crew.Select(b => b.Name)),
            bp.Lanes.Count > 1 ? bp.CrewText() : "one body");
        return (bp, voxel);
    }

    /// <summary>A resumed plan: finished placements that no longer stand go back to pending (someone took one, a creeper).</summary>
    private static void Reverify(BuildPlan bp, VoxelPlan voxel, WorldState w)
    {
        lock (bp)
            foreach (var s in bp.Steps.Where(s => s.Finished && s.Status != StepStatus.Skipped && w.IsLoaded(s.Target.X, s.Target.Z)))
            {
                bool holds = s.Action switch
                {
                    StepAction.Place => voxel.Blocks.TryGetValue(s.Target, out var want) && DesignChecks.SameState(want, w.GetBlock(s.Target), w.GetMeta(s.Target)),
                    StepAction.Scaffold => BuildBlocks.Supports(w.GetBlock(s.Target)) || !bp.ScaffoldUp.Contains(s.Target),
                    _ => true,
                };
                if (!holds) s.Status = StepStatus.Pending;
            }
    }

    private static bool SameNames(IEnumerable<string> a, IEnumerable<string> b) =>
        a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private sealed record Run(PlanLane Lane, IBuildBody Body, Task<LaneReport> Task);

    private Run Start(CrewContext ctx, PlanLane lane, IBuildBody body, CancellationToken ct) =>
        new(lane, body, Task.Run(() => new LaneRunner(this, ctx, lane, body).RunAsync(ct), ct));

    /// <summary>
    /// Watches the lanes: a lost or stuck lane waits for its body (when that's faster) or ends the round for a re-slice without it;
    /// a crew change ends the round for a re-slice with the new crew. Returns (the next round's crew or null when every lane is
    /// done, the body that finished last).
    /// </summary>
    private async Task<(List<IBuildBody>? Next, IBuildBody? Last)> CoordinateAsync(CrewContext ctx, List<Run> runs, List<IBuildBody> crew,
        CrewReport report, Func<IReadOnlyList<IBuildBody>>? live, CancellationToken ct)
    {
        var active = runs.ToList();
        var handled = new HashSet<Task<LaneReport>>();
        IBuildBody? last = null;
        List<IBuildBody>? next = null;
        var names = crew.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
        while (true)
        {
            var open = active.Where(r => !r.Task.IsCompleted).Select(r => (Task)r.Task).ToList();
            if (open.Count == 0 && active.All(r => handled.Contains(r.Task))) break;
            if (open.Count > 0) await Task.WhenAny(open.Append(Task.Delay(Poll * 4, _time, ct))).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (next == null && live?.Invoke() is { } now)
            {
                var avail = now.Where(b => b.Available).ToList();
                if (avail.Count > 0 && !SameNames(avail.Select(b => b.Name), names))
                {
                    Progress?.Invoke($"crew changed ({string.Join(", ", avail.Select(b => LaneReport.Short(b.Name)))}): re-slicing the rest");
                    next = avail;
                    ctx.Yield = true;
                }
            }
            foreach (var r in active.Where(r => r.Task.IsCompleted && !handled.Contains(r.Task)).ToList())
            {
                handled.Add(r.Task);
                var rep = await r.Task.ConfigureAwait(false);
                report.Lanes.Add(rep);
                if (rep.State == "done") { last = r.Body; continue; }
                if (rep.State is not ("lost" or "stuck") || next != null) continue;
                if (rep.State == "lost" && ShouldWait(ctx, r, active))
                {
                    Progress?.Invoke($"{LaneReport.Short(r.Body.Name)} is down: waiting for it (faster than re-balancing)");
                    var back = r.Body.BackInSeconds ?? 0;
                    var until = _time.GetUtcNow() + TimeSpan.FromSeconds(back * 2 + 10);
                    while (!r.Body.Available && _time.GetUtcNow() < until) await Task.Delay(Poll, _time, ct).ConfigureAwait(false);
                    if (r.Body.Available)
                    {
                        active.Add(Start(ctx, r.Lane, r.Body, ct));
                        continue;
                    }
                }
                // Re-balance: the others stop at their next step, the rest is sliced again without this body.
                Progress?.Invoke($"{LaneReport.Short(r.Body.Name)} {rep.State} ({rep.Why}): re-balancing its steps to the others");
                next = crew.Where(b => !ReferenceEquals(b, r.Body) && b.Available).ToList();
                if (next.Count == 0) next = null; // nobody left: the finale (anyone available) does the rest
                ctx.Yield = true;
            }
        }
        }
        catch (OperationCanceledException)
        {
            // Every lane writes its last checkpoint before the build reports "cancelled" (resume per lane).
            try { await Task.WhenAll(active.Select(r => (Task)r.Task)).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
        lock (ctx.Plan) foreach (var l in ctx.Plan.Lanes) if (l.State == "building") l.State = "yielded";
        return (next, last);
    }

    /// <summary>Waiting for a downed body vs. slicing its steps over the others: whichever finishes sooner.</summary>
    private bool ShouldWait(CrewContext ctx, Run lost, List<Run> live)
    {
        if (lost.Body.BackInSeconds is not { } back) return false;
        var bp = ctx.Plan;
        double Left(PlanLane l) => CrewSlicer.LaneTicks(l.Steps.Select(i => bp.Steps[i]).Where(s => !s.Finished), default) / 20.0;
        var others = live.Where(r => r.Lane != lost.Lane && !r.Task.IsCompleted).Select(r => Left(r.Lane)).ToList();
        double mine = Left(lost.Lane);
        if (others.Count == 0) return true;
        double waitEnd = Math.Max(others.Max(), back + mine);
        double rebalanceEnd = (others.Sum() + mine) / others.Count + RebalanceCost.TotalSeconds;
        return waitEnd <= rebalanceEnd;
    }

    // ------------------------------------------------------------------ hive crew (clones are separate processes)

    /// <summary>
    /// Main's side of a hive crew build: the gate (main's body), the slice over main and <paramref name="clones"/>, one hive job
    /// per clone lane (<see cref="IBuildJobs.StartLane"/>: the clone runs <c>build{bp, lane}</c>), main's own lane here, the
    /// clones' progress read back from their lane rows, then the finale by main (scaffold down, what's left).
    /// </summary>
    public async Task<CrewReport> RunHiveAsync(long id, VoxelPlan design, IBuildBody main, IReadOnlyList<CrewMember> clones, IBuildJobs jobs,
        CancellationToken ct)
    {
        var report = new CrewReport();
        int dim = main.World.Dimension;
        var plans = single.Plans;
        try
        {
            if (await GateAsync(id, design, main, report, ct).ConfigureAwait(false) is false) return report;
            var prev = BuildPlan.Resume(plans?.Get(id, 0), design.Hash());
            var (voxel, vpart) = ConstructionExecutor.Shape(design, BuildPart.Whole(design, dim), prev?.Transform ?? BuildTransform.None, dim);
            var members = new List<CrewMember> { new(main.Name, ConstructionExecutor.StartCell(main.World), main.Count(Items.WaterBucket, 0) > 0) };
            members.AddRange(clones.Where(c => !c.Name.Equals(main.Name, StringComparison.OrdinalIgnoreCase)));
            var o = single.Options(main, vpart, prev) with { WaterAvailable = members.Any(m => m.Water) };
            var bp = CrewSlicer.Slice(voxel, main.World.PlanningView, members, vpart, o, prev);
            bp.BlueprintId = id;
            bp.Version = design.Hash();
            bp.Dim = dim;
            bp.State = "building";
            plans?.Save(bp);
            report.Plan = bp;
            report.Rounds.Add(bp);
            if (bp.Lanes.Count > 1)
            {
                Progress?.Invoke($"split into {bp.Lanes.Count} lanes: {bp.CrewText()}");
                var ctx = new CrewContext(id, voxel, bp, plans, ownsPlan: true) { Reload = true };
                foreach (var lane in bp.Lanes.Where(l => !l.Body.Equals(main.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var why = jobs.StartLane(lane.Body, id, lane.Index);
                    lock (bp) { lane.State = why == null ? "building" : "lost"; lane.Note = why; }
                    if (why != null) Progress?.Invoke($"lane {LaneReport.Short(lane.Body)} not started: {why}");
                }
                plans?.Save(bp);
                if (bp.Lanes.FirstOrDefault(l => l.Body.Equals(main.Name, StringComparison.OrdinalIgnoreCase)) is { } mine)
                    report.Lanes.Add(await new LaneRunner(this, ctx, mine, main).RunAsync(ct).ConfigureAwait(false));
                await WaitRemoteAsync(ctx, main.Name, ct).ConfigureAwait(false);
                plans?.Save(bp);
            }
            var f = await single.RunAsync(id, design, BuildPart.Whole(design, dim), main, ct, previous: bp).ConfigureAwait(false);
            report.Finale = f;
            report.Plan = f.Plan;
            report.Stopped = f.Stopped;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report.Stopped = "cancelled";
        }
        return report;
    }

    private static bool Terminal(string state) => state is "done" or "lost" or "stuck" or "cancelled" or "failed" or "yielded";

    /// <summary>Until every clone lane is terminal (its own row says so) or has made no progress for <see cref="RemoteStall"/>.</summary>
    private async Task WaitRemoteAsync(CrewContext ctx, string main, CancellationToken ct)
    {
        var bp = ctx.Plan;
        int lastDone = -1;
        var lastChange = _time.GetUtcNow();
        while (true)
        {
            ctx.Plans?.MergeLanes(bp);
            List<PlanLane> remote;
            lock (bp) remote = bp.Lanes.Where(l => !l.Body.Equals(main, StringComparison.OrdinalIgnoreCase) && !Terminal(l.State)).ToList();
            if (remote.Count == 0) return;
            int done = bp.Steps.Count(s => s.Finished);
            if (done != lastDone) { lastDone = done; lastChange = _time.GetUtcNow(); }
            else if (_time.GetUtcNow() - lastChange > RemoteStall)
            {
                lock (bp) foreach (var l in remote) { l.State = "lost"; l.Note = "no progress"; }
                Progress?.Invoke($"lane {string.Join(", ", remote.Select(l => LaneReport.Short(l.Body)))} made no progress: finishing it myself");
                return;
            }
            await Task.Delay(RemotePoll, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A clone's side of a hive crew build (its <c>build{bp, lane}</c>): the persisted plan's lane <paramref name="laneIndex"/>,
    /// run by this body, progress written to the lane's own row (main owns the plan row and the finale).
    /// </summary>
    public async Task<LaneReport> RunLaneAsync(long id, VoxelPlan design, int laneIndex, IBuildBody body, CancellationToken ct)
    {
        var plans = single.Plans;
        if (plans?.Get(id, 0) is not { } bp || bp.IsStale(design.Hash()))
            return new LaneReport { Lane = laneIndex, Body = body.Name, State = "failed", Why = "no crew plan for this design (or only a stale one: main re-slices it)" };
        plans.MergeLanes(bp);
        if (laneIndex < 0 || laneIndex >= bp.Lanes.Count)
            return new LaneReport { Lane = laneIndex, Body = body.Name, State = "failed", Why = $"no lane {laneIndex} (the plan has {bp.Lanes.Count})" };
        if (single.GateOn && bp.Gate.State is GateState.Pending or GateState.Waiting && bp.Steps.Any(s => s.Gate && !s.Finished))
            return new LaneReport { Lane = laneIndex, Body = body.Name, State = "failed", Why = "waiting for the confirmation gate" };
        var (voxel, _) = ConstructionExecutor.Shape(design, BuildPart.Whole(design, body.World.Dimension), bp.Transform, body.World.Dimension);
        var ctx = new CrewContext(id, voxel, bp, plans, ownsPlan: false) { Reload = true };
        return await new LaneRunner(this, ctx, bp.Lanes[laneIndex], body).RunAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>What the lanes of one round share: the plan (locked for every change), the stand reservations, the storage turn.</summary>
internal sealed class CrewContext(long id, VoxelPlan voxel, BuildPlan plan, BuildPlanStore? plans, bool ownsPlan)
{
    public long Id => id;
    public VoxelPlan Voxel => voxel;
    public BuildPlan Plan => plan;
    public BuildPlanStore? Plans => plans;
    /// <summary>This process writes the plan row (in-process coordinator, hive main); a clone writes only its lane row.</summary>
    public bool OwnsPlan => ownsPlan;
    /// <summary>Read the other lanes' progress rows while waiting (they run in other processes).</summary>
    public bool Reload { get; init; }
    public StandBoard Stands { get; } = new();
    /// <summary>One body at the chests at a time: the bill's shares are fetched staggered, not as a race to the same chest.</summary>
    public SemaphoreSlim Storage { get; } = new(1, 1);
    private volatile bool _yield;
    /// <summary>Stop at the next step boundary (re-slice).</summary>
    public bool Yield { get => _yield; set => _yield = value; }
}

/// <summary>
/// Where each body stands (or is about to): a body reserves a stand before walking there, so no two bodies ever use one cell (a
/// body is two blocks tall: feet and head); a placement waits while another body stands in its cell.
/// </summary>
internal sealed class StandBoard
{
    private readonly Dictionary<string, List<BlockPos>> _at = new(StringComparer.OrdinalIgnoreCase);

    private static bool Overlap(BlockPos a, BlockPos b) => a.X == b.X && a.Z == b.Z && Math.Abs(a.Y - b.Y) <= 1;

    /// <summary>Reserves <paramref name="feet"/> for <paramref name="body"/> (on top of where it is now); false = another body's.</summary>
    public bool TryTake(string body, BlockPos feet)
    {
        lock (_at)
        {
            foreach (var (b, cells) in _at)
                if (!b.Equals(body, StringComparison.OrdinalIgnoreCase) && cells.Any(c => Overlap(c, feet))) return false;
            if (!_at.TryGetValue(body, out var mine)) _at[body] = mine = new();
            if (!mine.Contains(feet)) mine.Add(feet);
            return true;
        }
    }

    /// <summary>Arrived at <paramref name="feet"/>: every other cell this body held is free again.</summary>
    public void Keep(string body, BlockPos feet)
    {
        lock (_at) _at[body] = [feet];
    }

    /// <summary>Another body stands in (or is headed to) <paramref name="cell"/>.</summary>
    public bool Occupied(string body, BlockPos cell)
    {
        lock (_at)
            return _at.Any(kv => !kv.Key.Equals(body, StringComparison.OrdinalIgnoreCase) && kv.Value.Any(c => c == cell || c.Up() == cell));
    }

    public BlockPos? At(string body) { lock (_at) return _at.TryGetValue(body, out var c) && c.Count > 0 ? c[^1] : null; }

    public void Clear() { lock (_at) _at.Clear(); }
}

/// <summary>One body running its lane: dependency waits, stand reservations, its share of the bill, the steps, checkpoints.</summary>
internal sealed class LaneRunner(CrewExecutor x, CrewContext c, PlanLane lane, IBuildBody body)
{
    private enum Outcome { Done, Failed, Missing }

    private readonly LaneReport _r = new() { Lane = lane.Index, Body = body.Name };
    private WorldState W => body.World;
    private BuildPlan Bp => c.Plan;
    private int _column = -1;
    private readonly List<ClaimKey> _claims = new();
    private DateTimeOffset _reloadedAt;
    private BlockPos _home;

    public async Task<LaneReport> RunAsync(CancellationToken ct)
    {
        using var scope = body.Enter();
        lock (Bp) lane.State = "building";
        _home = ConstructionExecutor.StartCell(W);
        await TakeStandAsync(_home, W.Dimension, ct).ConfigureAwait(false); // where it stands now is its own until it moves
        int since = 0, fails = 0;
        try
        {
            await PrefetchAsync(ct).ConfigureAwait(false);
            // The lane's steps in plan order; each round does the first one that is ready (its waits met, its stand free, not
            // filling a cell an earlier step of this lane still has to stand in), so one late support never holds up the rest.
            var queue = lane.Steps.Where(i => !Bp.Steps[i].Finished).ToList();
            var tries = new Dictionary<int, int>();
            var idleSince = x.Time.GetUtcNow();
            bool parked = false;
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (c.Yield) { _r.State = "yielded"; return _r; }
                if (!body.Available) { _r.State = "lost"; _r.Why = "body not in the game"; return _r; }
                PlanStep? pick = null;
                string? whyNot = null;
                var earlier = new HashSet<BlockPos>();
                foreach (var i in queue.ToList())
                {
                    var s = Bp.Steps[i];
                    if (s.Finished) { queue.Remove(i); continue; }
                    var ready = Ready(i, s);
                    if (ready == false) { queue.Remove(i); continue; } // needs a skipped step: the finale's
                    bool fillsEarlierStand = s.IsPlacement && earlier.Contains(s.Target);
                    if (s.Column < 0 && s.Stand != default) { earlier.Add(s.Stand); earlier.Add(s.Stand.Up()); }
                    if (ready == null) { whyNot ??= $"#{i} waits for another lane's step"; continue; }
                    if (fillsEarlierStand) { whyNot ??= $"#{i} would fill a stand this lane still needs"; continue; }
                    if (!await TryReserveAsync(s, ct).ConfigureAwait(false)) { whyNot ??= $"#{i}'s stand or cell is another body's"; continue; }
                    pick = s;
                    break;
                }
                if (pick == null)
                {
                    if (queue.Count == 0) break;
                    var waited = x.Time.GetUtcNow() - idleSince;
                    if (waited > x.WaitLimit)
                    {
                        foreach (var i in queue) Bp.Steps[i].Note = "waited too long: " + (whyNot ?? "nothing ready");
                        break; // what's left goes to the finale
                    }
                    if (!parked && waited > x.ParkAfter && InTheWay()) { parked = true; await ParkAsync(ct).ConfigureAwait(false); }
                    Reload();
                    await Task.Delay(x.Poll, x.Time, ct).ConfigureAwait(false);
                    continue;
                }
                parked = false;
                var o = await StepAsync(pick, ct).ConfigureAwait(false);
                idleSince = x.Time.GetUtcNow();
                if (o == Outcome.Failed)
                {
                    if ((tries[pick.Index] = tries.GetValueOrDefault(pick.Index) + 1) >= 3) queue.Remove(pick.Index);
                    if (++fails >= x.StuckAfter) { _r.State = "stuck"; _r.Why = pick.Note; return _r; }
                }
                else
                {
                    queue.Remove(pick.Index);
                    if (o == Outcome.Done) fails = 0;
                }
                if (++since >= x.CheckpointEvery || pick.Action != StepAction.Place) { since = 0; Checkpoint(); }
            }
            _r.State = "done";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _r.State = "cancelled";
        }
        finally
        {
            try
            {
                if (_column >= 0 && body.Available) await LeaveColumnAsync(CancellationToken.None).ConfigureAwait(false);
                if (_r.State == "done") await ParkAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { x.Log?.LogDebug(ex, "lane {Lane} wrap-up failed", lane.Index); }
            if (body.Claims is { } b) foreach (var k in _claims) try { await b.ReleaseAsync(k, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            _r.Left = lane.Steps.Count(i => !Bp.Steps[i].Finished);
            try
            {
                var left = lane.Steps.Where(i => i >= 0 && i < Bp.Steps.Count && (!Bp.Steps[i].Finished || Bp.Steps[i].Status == StepStatus.Skipped)).ToList();
                if (left.Count > 0)
                    _r.Skips = BuildSkips.Line(left.Select(i => Bp.Steps[i]).Select(s => (s.Target, s.Note ?? (s.Finished ? null : "not reached: " + _r.State))));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { x.Log?.LogDebug(ex, "lane {Lane} skip reasons failed", lane.Index); }
            lock (Bp) { lane.State = _r.State; lane.Note = _r.Why; }
            Checkpoint();
        }
        return _r;
    }

    // ---------------------------------------------------------------- waits

    /// <summary>Every step this one needs is done (the shared plan, the other lanes' rows, or the world shows it): true; one of
    /// them was skipped (it never will be): false; not yet: null.</summary>
    private bool? Ready(int i, PlanStep s)
    {
        var deps = (lane.Waits.TryGetValue(i, out var ws) ? ws : []).Concat(s.Deps).Where(d => d >= 0 && d < Bp.Steps.Count && d != i);
        foreach (var d in deps)
        {
            var ds = Bp.Steps[d];
            if (ds.Finished && ds.Status != StepStatus.Skipped || Holds(ds)) continue;
            if (ds.Status == StepStatus.Skipped) { s.Note = $"needs step #{d} (skipped)"; return false; }
            return null;
        }
        return true;
    }

    /// <summary>The step's effect stands in this body's world (another process's lane did it).</summary>
    private bool Holds(PlanStep s)
    {
        var t = s.Target;
        if (!W.IsLoaded(t.X, t.Z)) return false;
        int id = W.GetBlock(t);
        return s.Action switch
        {
            StepAction.Place => c.Voxel.Blocks.TryGetValue(t, out var want) && DesignChecks.SameState(want, id, W.GetMeta(t)),
            StepAction.Scaffold => BuildBlocks.Supports(id),
            StepAction.Clear or StepAction.RemoveScaffold => !BuildBlocks.Supports(id),
            StepAction.PourWater => BlockShapes.IsWater(id),
            StepAction.PickupWater => !BlockShapes.IsWater(id),
            _ => false,
        };
    }

    private void Reload()
    {
        if (!c.Reload || c.Plans == null || x.Time.GetUtcNow() - _reloadedAt < x.RemotePoll) return;
        _reloadedAt = x.Time.GetUtcNow();
        try { c.Plans.MergeLanes(Bp); } catch (Exception ex) { x.Log?.LogDebug(ex, "lane rows unreadable"); }
    }

    /// <summary>Another lane still has to place into the cell this body stands in, or to stand where it stands.</summary>
    private bool InTheWay()
    {
        return c.Stands.At(body.Name) is { } feet && Needed(feet);
    }

    /// <summary>Back to where it started (outside the way of the others), holding that cell.</summary>
    private async Task ParkAsync(CancellationToken ct)
    {
        if (!body.Available) return;
        if (_column >= 0 && !await LeaveColumnAsync(ct).ConfigureAwait(false)) return;
        // Home is where another lane needs to be (the slice was cut from where everyone stood): step aside to a free cell instead.
        if (ConstructionExecutor.StartCell(W) == _home || Needed(_home))
        {
            if (!Needed(ConstructionExecutor.StartCell(W)) || Aside() is not { } aside) return;
            _home = aside;
        }
        if (!await TakeStandAsync(_home, W.Dimension, ct).ConfigureAwait(false)) return;
        var g = await body.GotoAsync(_home, ct).ConfigureAwait(false);
        if (g.Ok) await ArriveAsync(_home).ConfigureAwait(false);
    }

    /// <summary>Another lane's pending step stands in, or fills, this cell.</summary>
    private bool Needed(BlockPos feet)
    {
        static bool Overlap(BlockPos a, BlockPos b) => a.X == b.X && a.Z == b.Z && Math.Abs(a.Y - b.Y) <= 1;
        lock (Bp)
            return Bp.Steps.Any(s => !s.Finished && s.Lane != lane.Index
                && (s.IsPlacement && (s.Target == feet || s.Target == feet.Up()) || s.Stand != default && Overlap(s.Stand, feet)));
    }

    /// <summary>The nearest walkable cell nobody's pending step needs and no other body holds.</summary>
    private BlockPos? Aside()
    {
        var from = ConstructionExecutor.StartCell(W);
        var flood = Pathfinder.Explore(W.PlanningView, from, new PathOptions { AllowOpenDoors = true, AllowParkour = false, MaxDrop = 2 }, 600);
        foreach (var (cell, _) in flood.OrderBy(kv => kv.Value))
        {
            if (cell == from || Needed(cell) || c.Voxel.Blocks.ContainsKey(cell) || c.Voxel.Blocks.ContainsKey(cell.Up())) continue;
            if (c.Stands.Occupied(body.Name, cell) || c.Stands.Occupied(body.Name, cell.Up())) continue;
            if (body.Claims?.IsFree(ClaimKey.Area(cell, cell.Up(), W.Dimension)) == false) continue;
            return cell;
        }
        return null;
    }

    /// <summary>The step's stand is this body's now (no other body stands in or heads to it; on the claim board too) and nobody
    /// stands in the cell it fills. One try: false = not now.</summary>
    private async Task<bool> TryReserveAsync(PlanStep s, CancellationToken ct)
    {
        int dim = W.Dimension;
        if (s.IsPlacement && (c.Stands.Occupied(body.Name, s.Target) || body.Claims?.IsFree(ClaimKey.Block(s.Target, dim)) == false)) return false;
        return s.Stand == default || await TakeStandAsync(s.Stand, dim, ct).ConfigureAwait(false);
    }

    /// <summary>Reserves a stand (here and on the claim board). Where the body stands now stays held too until it arrives.</summary>
    private async Task<bool> TakeStandAsync(BlockPos stand, int dim, CancellationToken ct)
    {
        if (!c.Stands.TryTake(body.Name, stand)) return false;
        if (body.Claims is not { } board) return true;
        var key = ClaimKey.Area(stand, stand.Up(), dim);
        if (_claims.Contains(key)) return true;
        var r = await board.TryClaimAsync(key, TimeSpan.FromSeconds(120), $"build #{c.Id} lane {LaneReport.Short(body.Name)} stand", ct).ConfigureAwait(false);
        if (!r.Ok) return false;
        _claims.Add(key);
        return true;
    }

    /// <summary>The body is at <paramref name="feet"/>: every other cell it held is free again (here and on the board).</summary>
    private async Task ArriveAsync(BlockPos feet)
    {
        c.Stands.Keep(body.Name, feet);
        if (body.Claims is not { } board) return;
        var keep = ClaimKey.Area(feet, feet.Up(), W.Dimension);
        foreach (var k in _claims.Where(k => k != keep).ToList())
        {
            try { await board.ReleaseAsync(k, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            _claims.Remove(k);
        }
    }

    // ---------------------------------------------------------------- materials

    /// <summary>This lane's share of the bill, from storage (the obtainer: exact item and variant), one body at a time.</summary>
    private async Task PrefetchAsync(CancellationToken ct)
    {
        List<(int Item, int Damage, int Count)> needs;
        lock (Bp)
            needs = lane.Steps.Select(i => Bp.Steps[i]).Where(s => s.Action == StepAction.Place && !s.Finished)
                .GroupBy(s => (s.Item, s.Damage)).Select(g => (g.Key.Item, g.Key.Damage, g.Count())).ToList();
        if (needs.All(n => body.Count(n.Item, n.Damage) >= n.Count)) return;
        await c.Storage.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (item, dmg, count) in needs)
                if (body.Count(item, dmg) < count)
                    await body.EnsureAsync(item, dmg, Math.Min(count, 64 * 4), ct).ConfigureAwait(false);
        }
        finally { c.Storage.Release(); }
    }

    private async Task<bool> HaveAsync(int item, int dmg, CancellationToken ct)
    {
        int left;
        lock (Bp) left = lane.Steps.Select(i => Bp.Steps[i]).Count(s => s.Action == StepAction.Place && !s.Finished && s.Item == item && s.Damage == dmg);
        await c.Storage.WaitAsync(ct).ConfigureAwait(false);
        try { await body.EnsureAsync(item, dmg, Math.Max(1, Math.Min(left, 64 * 4)), ct).ConfigureAwait(false); }
        finally { c.Storage.Release(); }
        return body.Count(item, dmg) > 0;
    }

    // ---------------------------------------------------------------- one step

    private async Task<Outcome> StepAsync(PlanStep s, CancellationToken ct)
    {
        var w = W;
        var voxel = c.Voxel;
        switch (s.Action)
        {
            case StepAction.Clear:
                {
                    int id = w.GetBlock(s.Target);
                    if (id == 0 || BlockShapes.IsLiquid(id)) { Set(s, StepStatus.Verified); return Outcome.Done; }
                    if (body.IsProtected(s.Target, id))
                    {
                        _r.Deviations.Add($"{BuildBlocks.Name(id, 0)} at {voxel.Local(s.Target)} is protected: left");
                        Set(s, StepStatus.Skipped, "protected");
                        return Outcome.Done;
                    }
                    // From its own reserved stand (a crew never lets the client path somewhere another body may stand).
                    if (!await MoveAsync(s, needStand: true, ct).ConfigureAwait(false)) return Outcome.Failed;
                    var r = s.Column >= 0 ? await body.DigHereAsync(s.Target, ct).ConfigureAwait(false) : await body.DigAsync(s.Target, ct).ConfigureAwait(false);
                    if (!r.Ok) { s.Note = ConstructionExecutor.Short(r.Message); _r.Failed++; return Outcome.Failed; }
                    _r.Cleared++;
                    Set(s, StepStatus.Verified);
                    return Outcome.Done;
                }
            case StepAction.Place:
            case StepAction.Scaffold:
                return await PlaceAsync(s, ct).ConfigureAwait(false);
            case StepAction.RemoveScaffold:
                {
                    int id = w.GetBlock(s.Target);
                    if (id == 0 || voxel.Blocks.ContainsKey(s.Target)) { lock (Bp) Bp.ScaffoldUp.Remove(s.Target); Set(s, StepStatus.Verified); return Outcome.Done; }
                    bool ours;
                    lock (Bp) ours = Bp.ScaffoldUp.Contains(s.Target);
                    if (!ours && !w.Placed.IsPlaced(s.Target, w.Dimension, id)) { Set(s, StepStatus.Skipped, "not ours"); return Outcome.Done; }
                    if (!await MoveAsync(s, needStand: s.Column >= 0, ct).ConfigureAwait(false)) return Outcome.Failed;
                    var r = s.Column >= 0 ? await body.DigHereAsync(s.Target, ct).ConfigureAwait(false) : await body.DigAsync(s.Target, ct).ConfigureAwait(false);
                    if (!r.Ok) { s.Note = ConstructionExecutor.Short(r.Message); return Outcome.Failed; }
                    lock (Bp) { Bp.ScaffoldUp.Remove(s.Target); s.Status = StepStatus.Verified; }
                    return Outcome.Done;
                }
            case StepAction.PourWater:
                {
                    if (!await MoveAsync(s, needStand: true, ct).ConfigureAwait(false)) return Outcome.Failed;
                    var r = await body.PourWaterAsync(s.Target, s.Face ?? BlockFace.Top, ct).ConfigureAwait(false);
                    if (!r.Ok) { s.Note = ConstructionExecutor.Short(r.Message); return Outcome.Failed; }
                    Set(s, StepStatus.Done);
                    x.Progress?.Invoke($"{LaneReport.Short(body.Name)} poured a water column to build down ({s.Note})");
                    return Outcome.Done;
                }
            case StepAction.PickupWater:
                {
                    if (!await MoveAsync(s, needStand: true, ct).ConfigureAwait(false)) return Outcome.Failed;
                    var r = await body.PickupWaterAsync(s.Target, ct).ConfigureAwait(false);
                    if (!r.Ok) { s.Note = ConstructionExecutor.Short(r.Message); return Outcome.Failed; }
                    lock (Bp)
                    {
                        s.Status = StepStatus.Verified;
                        foreach (var col in Bp.Columns.Where(col => col.Source == s.Target)) col.Removed = true;
                    }
                    return Outcome.Done;
                }
        }
        return Outcome.Done;
    }

    private async Task<Outcome> PlaceAsync(PlanStep s, CancellationToken ct)
    {
        var w = W;
        var voxel = c.Voxel;
        bool scaffold = s.Action == StepAction.Scaffold;
        if (!scaffold && voxel.Blocks.TryGetValue(s.Target, out var want) && DesignChecks.SameState(want, w.GetBlock(s.Target), w.GetMeta(s.Target)))
        { _r.Already++; Set(s, StepStatus.Verified); return Outcome.Done; }
        if (scaffold && BuildBlocks.Supports(w.GetBlock(s.Target))) { Set(s, StepStatus.Verified); return Outcome.Done; }
        int existing = w.GetBlock(s.Target);
        if (!Aiming.IsReplaceable(w, existing))
        {
            // Someone built there (or it's the wrong block): never dig it from a lane; the finale re-plans around it.
            bool ours;
            lock (Bp) ours = Bp.Placed.Contains(s.Target);
            ours |= w.Placed.IsPlaced(s.Target, w.Dimension, existing);
            _r.Deviations.Add($"{voxel.Local(s.Target)} holds {BuildBlocks.Name(existing, w.GetMeta(s.Target))}{(ours ? "" : " (not mine: someone built there)")}");
            s.Note = ours ? "wrong block" : "someone built there";
            return Outcome.Missing; // stays pending: the finale re-plans around it
        }
        int item, dmg;
        if (scaffold)
        {
            lock (Bp) item = ConstructionExecutor.ScaffoldItem(body, Bp);
            dmg = 0;
            if (item == 0) { s.Note = "missing scaffold blocks (cobblestone or planks)"; return Outcome.Missing; }
        }
        else
        {
            (item, dmg) = (s.Item, s.Damage);
            if (body.Count(item, dmg) == 0 && !await HaveAsync(item, dmg, ct).ConfigureAwait(false))
            {
                s.Note = "missing " + ConstructionExecutor.Name(item, dmg);
                return Outcome.Missing; // stays pending: everything else goes on, the finale asks/pauses
            }
        }
        if (!await MoveAsync(s, needStand: true, ct).ConfigureAwait(false)) return Outcome.Failed;
        var r = await body.PlaceAsync(new BuildStep(scaffold ? StepKind.Scaffold : StepKind.Place, s.Target, scaffold ? item : s.Id, scaffold ? 0 : s.Meta,
            null, s.Face, s.HiddenFace, s.Yaw), item, dmg, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            _r.Failed++;
            s.Note = ConstructionExecutor.Short(r.Message);
            return Outcome.Failed;
        }
        lock (Bp)
        {
            s.Status = scaffold ? StepStatus.Verified : StepStatus.Done;
            if (!Bp.Placed.Contains(s.Target)) Bp.Placed.Add(s.Target);
            if (!scaffold && BuildBlocks.KindOf(s.Id) == BuildKind.Door) Bp.Placed.Add(s.Target.Up());
            if (scaffold && !Bp.ScaffoldUp.Contains(s.Target)) Bp.ScaffoldUp.Add(s.Target);
        }
        if (scaffold) { _r.Scaffolded++; return Outcome.Done; }
        _r.Placed++;
        if (voxel.Blocks.TryGetValue(s.Target, out var planned) && DesignChecks.SameState(planned, w.GetBlock(s.Target), w.GetMeta(s.Target)))
            Set(s, StepStatus.Verified);
        return Outcome.Done;
    }

    private void Set(PlanStep s, StepStatus status, string? note = null)
    {
        lock (Bp)
        {
            s.Status = status;
            if (note != null) s.Note = note;
        }
    }

    /// <summary>To the step's stand: out of (or into, down to the depth of) a water column first.</summary>
    private async Task<bool> MoveAsync(PlanStep s, bool needStand, CancellationToken ct)
    {
        var bp = Bp;
        if (s.Column >= 0 && s.Column < bp.Columns.Count)
        {
            var col = bp.Columns[s.Column];
            if (_column >= 0 && _column != s.Column && !await LeaveColumnAsync(ct).ConfigureAwait(false)) return false;
            var r = await body.SwimToDepthAsync(col.Swim, col.Entry, s.Stand.Y, ct).ConfigureAwait(false);
            if (!r.Ok) { s.Note = "water column: " + ConstructionExecutor.Short(r.Message); return false; }
            _column = s.Column;
            await ArriveAsync(s.Stand).ConfigureAwait(false);
            return true;
        }
        if (_column >= 0 && !await LeaveColumnAsync(ct).ConfigureAwait(false)) return false;
        if (!needStand || s.Stand == default) return true;
        if (ConstructionExecutor.StartCell(W) == s.Stand) { await ArriveAsync(s.Stand).ConfigureAwait(false); return true; }
        var g = await body.GotoAsync(s.Stand, ct).ConfigureAwait(false);
        if (!g.Ok) s.Note = "goto: " + ConstructionExecutor.Short(g.Message);
        else await ArriveAsync(s.Stand).ConfigureAwait(false);
        return true; // the action itself says whether it worked from here
    }

    private async Task<bool> LeaveColumnAsync(CancellationToken ct)
    {
        if (_column < 0) return true;
        var col = Bp.Columns[_column];
        var r = await body.SwimOutAsync(col.Swim, col.Entry, ct).ConfigureAwait(false);
        if (!r.Ok) return false;
        _column = -1;
        await ArriveAsync(col.Entry).ConfigureAwait(false);
        return true;
    }

    private void Checkpoint()
    {
        try
        {
            c.Plans?.SaveLane(Bp, lane);
            if (c.OwnsPlan) c.Plans?.Save(Bp);
        }
        catch (Exception ex) { x.Log?.LogDebug(ex, "lane {Lane} checkpoint failed", lane.Index); }
    }
}
