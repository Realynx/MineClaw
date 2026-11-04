using System.Globalization;
using MineClaw.Bot.Building;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Blueprints;

/// <summary>What one body's run achieved.</summary>
public sealed class BuildReport
{
    public int Placed, Already, Failed, Cleared, Scaffolded, ScaffoldRemoved;
    public List<string> Deviations { get; } = new();
    public List<BlockPos> Missing { get; } = new();
    public List<BlockPos> Unreachable { get; } = new();
    /// <summary>Why the run stopped early ("missing cobblestone (need 40, have 12): ..."), or null.</summary>
    public string? Stopped { get; set; }
    public bool Complete => Stopped == null && Missing.Count == 0;
    public int Total { get; set; }
    /// <summary>Why the cells left are left, grouped with counts and a sample ("13 no stand: above my reach (I can't climb) e.g.
    /// -27,71,13; 676 waiting on supports e.g. -33,76,7; 0 protected"); null when nothing was left (<see cref="BuildSkips"/>).</summary>
    public string? Skips { get; set; }
    /// <summary>The plan this run executed (summary, techniques, gate).</summary>
    public BuildPlan? Plan { get; set; }
    /// <summary>Paused at the confirmation gate, waiting for a trusted answer.</summary>
    public bool AtGate { get; set; }
    /// <summary>A gate-only run (the crew's first phase): the gate blocks are up and a trusted player said yes (or no gate applies).</summary>
    public bool GatePassed { get; set; }
    /// <summary>How the body got down off the build when the run ended up there ("came down off the build: dug 3 of my scaffold,
    /// placed 4 as a stair"), or why it couldn't ("stranded: …"); null when it could walk home (<see cref="ExitRoute"/>).</summary>
    public string? Exit { get; set; }

    public string Text(string what) =>
        $"{what}: placed {Placed}" + (Already > 0 ? $", {Already} already there" : "") + (Cleared > 0 ? $", cleared {Cleared}" : "")
        + (Scaffolded > 0 ? $", scaffold {Scaffolded} ({ScaffoldRemoved} removed)" : "")
        + (Missing.Count > 0 ? $", {Missing.Count} missing" : "") + (Skips != null ? $" — {Skips}" : "") + (Deviations.Count > 0 ? $"; deviations: {string.Join("; ", Deviations.Take(3))}" : "")
        + (Stopped != null ? $"; stopped: {Stopped}" : "") + (Exit != null ? $"; {Exit}" : "");
}

/// <summary>
/// Runs a blueprint part with one body, through the build slicer (<see cref="BuildPlanner"/>): the persisted
/// <see cref="BuildPlan"/> is loaded (resume: done steps re-verified against the world, the rest re-planned from where the
/// body is now) or sliced fresh; materials come through the body's acquisition (storage, crafting, the gen, then a question),
/// and a shortfall never ends the build: steps needing that item wait, everything else is built, and the run pauses
/// resumably ("missing …"). Each step goes to its planned stand (or water-column depth), clicks its planned face and is
/// verified; a deviation (missing, wrong or someone else's block) re-plans locally. After the first few placements the
/// confirmation gate pauses and asks once (no default: no trusted yes, no more building); a correction takes the gate blocks
/// down, transforms the plan and gates again. Scaffolding comes down top-first, water is taken back, every placed block is in
/// the plan's ledger (undo).
/// </summary>
public sealed partial class ConstructionExecutor(BlueprintOptions o, BlueprintStore? store = null, IClaimBoard? claims = null, TimeProvider? time = null,
    ILogger? log = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Progress lines (layer done, deviations, gate) for the activity/brain.</summary>
    public Action<string>? Progress { get; set; }

    /// <summary>Plan persistence (default: next to the designs when a store is given).</summary>
    public BuildPlanStore? Plans { get; init; } = store != null ? new BuildPlanStore(store.Db, time) : null;

    /// <summary>The confirmation gate's chat question and answer channel; no gate without both.</summary>
    public BuildGates? Gates { get; init; }
    public Func<string, Task>? Ask { get; init; }
    /// <summary>The gate question for a plan (landmark wording); default: <see cref="BuildGate.Question"/> without landmarks.</summary>
    public Func<BuildPlan, VoxelPlan, string>? GateQuestion { get; init; }

    /// <summary>
    /// The generic standing-rule check between steps (zero tokens; <c>DirectiveStore.TimeHold</c>): a reason to stop now, e.g. a
    /// trusted player's "the drop tower only in daytime" at dusk. The run then stops resumably and the body comes down off the
    /// build and walks back to where the build started before dark finishes falling. Null = carry on.
    /// </summary>
    public Func<long, string?>? Hold { get; set; }

    internal string? HoldNow(long id)
    {
        try { return Hold?.Invoke(id); } catch (Exception ex) { log?.LogDebug(ex, "build hold check failed"); return null; }
    }

    internal bool GateOn => Gates != null && Ask != null && o.GateBlocks > 0;
    internal BlueprintOptions Options0 => o;

    /// <summary>Slices (or loads) the plan for a part without building: the "build it" summary and the supervisor view.</summary>
    /// <param name="previous">The plan to carry on from (a crew's plan handed to its finale); default: the persisted one.</param>
    public BuildPlan PlanFor(long blueprintId, VoxelPlan design, BuildPart part, IBuildBody body, bool save = true, BuildPlan? previous = null)
    {
        var w = body.World;
        var prev = previous ?? Plans?.Get(blueprintId, part.Index);
        // A stale plan (another design version, an older slicer format) is planned again from the world, keeping its ledger and gate.
        var resumed = BuildPlan.Resume(prev, design.Hash());
        if (prev != null && !ReferenceEquals(resumed, prev))
        {
            log?.LogInformation("blueprint #{Id} {Part}: {Note} (format {Format}): keeping {Placed} placed, gate {Gate}", blueprintId, part.Label,
                resumed!.Note, prev.Format, resumed.Placed.Count, resumed.Gate.State);
            Progress?.Invoke($"{resumed.Note}: planning it again, keeping the gate ({resumed.Gate.State.ToString().ToLowerInvariant()}) and {resumed.Placed.Count} placed blocks");
        }
        prev = resumed;
        var (voxel, p) = Shape(design, part, prev?.Transform ?? BuildTransform.None, w.Dimension);
        BuildPlan bp;
        using (BuildPulse.While(body.Name)) bp = BuildPlanner.Plan(voxel, w.PlanningView, StartCell(w), p, Options(body, part, prev), prev);
        bp.BlueprintId = blueprintId;
        bp.Version = design.Hash();
        bp.Dim = w.Dimension;
        if (save) Plans?.Save(bp);
        return bp;
    }

    internal BuildPlannerOptions Options(IBuildBody body, BuildPart part, BuildPlan? prev, IReadOnlySet<BlockPos>? keep = null,
        IReadOnlySet<(int, int)>? hold = null) => new()
    {
        Keep = keep ?? new HashSet<BlockPos>(),
        Hold = hold ?? new HashSet<(int, int)>(),
        Buckets = Math.Max(1, body.Count(Items.WaterBucket, 0)),
        AllowHidden = o.HiddenFaces,
        HalvesFollowCursor = body.HalvesFollowCursor,
        Climb = o.Climb,
        MaxScaffold = Math.Max(o.MaxScaffold, 6) + (body.Count(Items.WaterBucket, 0) > 0 ? 6 : 0),
        WaterAvailable = body.Count(Items.WaterBucket, 0) > 0,
        GateBlocks = GateOn && part.Index == 0 && prev?.Gate.State is null or GateState.Pending ? o.GateBlocks : 0,
        IsProtected = body.IsProtected,
        StripsAbove = o.CrewStripsAbove,
    };

    internal static BlockPos StartCell(WorldState w) => Pathfinder.StartCellFor(w.PlanningView, w.Player.Position) ?? w.Player.Position.ToBlockPos();

    /// <summary>The design as built: the gate corrections applied, and the part's cells moved with it.</summary>
    internal static (VoxelPlan Voxel, BuildPart Part) Shape(VoxelPlan design, BuildPart part, BuildTransform t, int dim)
    {
        if (t.IsNone) return (design, part);
        var voxel = PlanTransform.Apply(design, t);
        return (voxel, part.Count <= 1 ? BuildPart.Whole(voxel, dim) : part with { Cells = voxel.Blocks.Keys.Concat(voxel.Air).ToHashSet() });
    }

    /// <param name="untilGate">The crew's first phase: one body places the gate blocks and asks; once a trusted player says yes the
    /// run ends (<see cref="BuildReport.GatePassed"/>) and the lanes take over. No gate to pass = passed at once.</param>
    public async Task<BuildReport> RunAsync(long blueprintId, VoxelPlan plan, BuildPart part, IBuildBody body, CancellationToken ct, int? layer = null,
        bool untilGate = false, BuildPlan? previous = null)
    {
        // async: a failure while setting up the run faults the task, as before.
        return await new BuildRun(this, blueprintId, plan, part, body, ct, layer, untilGate).RunAsync(previous).ConfigureAwait(false);
    }

    private BlueprintStore? Store0 => store;
    private IClaimBoard? Claims0 => claims;
    private ILogger? Log0 => log;

    private enum StepOutcome { Done, Placed, Replan }
    private enum GateOutcome { Continue, Stop, Replanned }

    /// <summary>
    /// Takes down everything a build placed (its ledger, scaffolding included), top first; only blocks still standing.
    /// Self-placed, so the protection policy allows it. Returns how many came down.
    /// </summary>
    /// <param name="keep">A block never dug even though the build placed it (default: the body's protection; the design tool passes
    /// never-break marks and generator walls/fluids too).</param>
    /// <param name="removed">Collects the cells taken down; <paramref name="left"/> the ledger cells left standing (protected, a dig
    /// the client refused).</param>
    public static async Task<int> UndoPlacedAsync(IBuildBody body, BuildPlan bp, WorldState w, CancellationToken ct,
        Func<BlockPos, int, bool>? keep = null, List<BlockPos>? removed = null, List<(BlockPos Pos, string Why)>? left = null)
    {
        keep ??= body.IsProtected;
        int n = 0;
        foreach (var p in bp.Placed.Concat(bp.Gate.Blocks).Concat(bp.ScaffoldUp).Distinct().OrderByDescending(p => p.Y).ToList())
        {
            int id = w.GetBlock(p);
            if (id == 0) { bp.Placed.Remove(p); bp.Gate.Blocks.Remove(p); continue; }
            // Only what is still ours: the placed-block log holds it, or it is what our finished step put there. A block someone
            // put in the cell since (they dug ours) is theirs: left, and dropped from the ledger.
            bool ours = w.Placed.IsPlaced(p, w.Dimension, id)
                        || bp.Steps.Any(s => s.Finished && (s.Target == p && (s.Action == StepAction.Scaffold || s.Action == StepAction.Place && s.Id == id)
                                                            || s.Target == p.Down() && s.Action == StepAction.Place && s.Id == id && BuildBlocks.KindOf(id) == BuildKind.Door));
            if (!ours) { bp.Placed.Remove(p); bp.Gate.Blocks.Remove(p); bp.ScaffoldUp.Remove(p); continue; }
            if (keep(p, id)) { left?.Add((p, "protected")); continue; }
            var r = await body.DigAsync(p, ct).ConfigureAwait(false);
            if (!r.Ok) { left?.Add((p, r.Message)); continue; }
            removed?.Add(p);
            n++;
            bp.Placed.Remove(p);
            bp.Gate.Blocks.Remove(p);
            bp.ScaffoldUp.Remove(p);
        }
        return n;
    }

    /// <summary>
    /// The body's way home at the end of a run (a lane, the finale, a paused or finished build): already home: nothing; able to
    /// walk down (a body up on the build while its way down still stands): it walks down now; else its own pillar is dug from on
    /// top or a 1-wide stair placed down (<see cref="ExitRoute"/>), the stair recorded as the plan's exit. Null = nothing to say;
    /// otherwise the line for the build result ("came down off the build: …" / "stranded: …").
    /// </summary>
    internal static async Task<string?> LeaveSafelyAsync(IBuildBody body, BuildPlan bp, VoxelPlan voxel, List<BlockPos>? recScaffold, CancellationToken ct,
        Action<string>? progress = null, ILogger? log = null)
    {
        var w = body.World;
        var home = bp.Home ?? new BlockPos(voxel.Min.X, voxel.Min.Y, voxel.Min.Z);
        var feet = StartCell(w);
        if (WayHome.AtHome(feet, home)) return null;
        if (WayHome.WalkDown(w.PlanningView, feet, home) is { } down) return await WalkDownAsync(body, down, ct).ConfigureAwait(false);
        int blocks = GameTools.ClutchBlocks.Ids.Sum(id => body.Count(id, -1));
        var plan = ExitRoute.Plan(w.PlanningView, feet, home,
            mine: p => !voxel.Blocks.ContainsKey(p) && (bp.ScaffoldUp.Contains(p) || bp.Exit.Contains(p) || recScaffold?.Contains(p) == true),
            mayPlace: p => !voxel.Blocks.ContainsKey(p) && !body.IsProtected(p, w.GetBlock(p)), blocks);
        if (plan == null)
            return Stranded(bp, $"stranded: no way down from {feet.X},{feet.Y},{feet.Z} (no pillar of mine to dig, no room for a stair{(blocks == 0 ? ", no blocks" : "")})", progress, log);
        int dug = 0, placed = 0;
        foreach (var m in plan.Moves)
        {
            ct.ThrowIfCancellationRequested();
            var r = await ExitMoveAsync(body, bp, m, recScaffold, ct).ConfigureAwait(false);
            if (!r.Ok)
            {
                var at = StartCell(w);
                return Stranded(bp, $"stranded at {at.X},{at.Y},{at.Z}: the way down failed ({m.Kind.ToString().ToLowerInvariant()} {m.Target.X},{m.Target.Y},{m.Target.Z}: {Short(r.Message)})", progress, log);
            }
            if (m.Kind == ExitKind.DigDown) dug++;
            else if (m.Kind != ExitKind.Walk) placed++;
        }
        var line = CameDown(dug, placed);
        progress?.Invoke(line);
        log?.LogInformation("blueprint #{Id}: {Line}", bp.BlueprintId, line);
        return line;
    }

    private static async Task<string?> WalkDownAsync(IBuildBody body, BlockPos down, CancellationToken ct)
    {
        var g = await body.GotoAsync(down, ct).ConfigureAwait(false);
        return g.Ok ? null : $"couldn't walk down off the build: {Short(g.Message)}";
    }

    private static string CameDown(int dug, int placed) => "came down off the build: " + string.Join(", ", new[]
    {
        dug > 0 ? $"dug {dug} of my scaffold" : null,
        placed > 0 ? $"placed {placed} as a stair (left as the way down)" : null,
    }.Where(x => x != null).DefaultIfEmpty("walked down"));

    private static string Stranded(BuildPlan bp, string why, Action<string>? progress, ILogger? log)
    {
        log?.LogWarning("blueprint #{Id}: {Why}", bp.BlueprintId, why);
        progress?.Invoke(why);
        return why;
    }

    /// <summary>One move of the way down: a walk, my own scaffold dug from on top, or a stair block placed (kept as the exit).</summary>
    private static async Task<ActionResult> ExitMoveAsync(IBuildBody body, BuildPlan bp, ExitMove m, List<BlockPos>? recScaffold, CancellationToken ct)
    {
        switch (m.Kind)
        {
            case ExitKind.Walk:
                return StartCell(body.World) == m.Stand ? ActionResult.Success("here") : await body.GotoAsync(m.Stand, ct).ConfigureAwait(false);
            case ExitKind.DigDown:
                var dug = await body.DigHereAsync(m.Target, ct).ConfigureAwait(false);
                if (dug.Ok) { bp.ScaffoldUp.Remove(m.Target); bp.Exit.Remove(m.Target); recScaffold?.Remove(m.Target); }
                return dug;
            default:
                int item = ScaffoldItem(body, bp);
                if (item == 0) return ActionResult.Fail("no blocks for a stair");
                var r = await body.PlaceAsync(new BuildStep(StepKind.Scaffold, m.Target, item, 0, null, m.Face, m.Hidden), item, 0, ct).ConfigureAwait(false);
                if (r.Ok && !bp.Exit.Contains(m.Target)) bp.Exit.Add(m.Target);
                return r;
        }
    }

    /// <summary>The cheapest clutch block on hand that the plan doesn't still need all of.</summary>
    internal static int ScaffoldItem(IBuildBody body, BuildPlan bp)
    {
        foreach (var id in GameTools.ClutchBlocks.Ids)
        {
            int need = bp.Steps.Count(s => s.Action == StepAction.Place && !s.Finished && s.Item == id);
            if (body.Count(id, -1) > need) return id;
        }
        return GameTools.ClutchBlocks.Ids.FirstOrDefault(id => body.Count(id, -1) > 0);
    }

    internal static string Name(int item, int damage) => GameBuildBody.ItemName(item, damage);

    internal static string Short(string s) => s.Length <= 90 ? s : s[..89] + "…";
}
