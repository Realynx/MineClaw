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
    /// <summary>The plan this run executed (summary, techniques, gate).</summary>
    public BuildPlan? Plan { get; set; }
    /// <summary>Paused at the confirmation gate, waiting for a trusted answer.</summary>
    public bool AtGate { get; set; }

    public string Text(string what) =>
        $"{what}: placed {Placed}" + (Already > 0 ? $", {Already} already there" : "") + (Cleared > 0 ? $", cleared {Cleared}" : "")
        + (Scaffolded > 0 ? $", scaffold {Scaffolded} ({ScaffoldRemoved} removed)" : "")
        + (Missing.Count > 0 ? $", {Missing.Count} missing" : "") + (Deviations.Count > 0 ? $"; deviations: {string.Join("; ", Deviations.Take(3))}" : "")
        + (Stopped != null ? $"; stopped: {Stopped}" : "");
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
public sealed class ConstructionExecutor(BlueprintOptions o, BlueprintStore? store = null, IClaimBoard? claims = null, TimeProvider? time = null,
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

    private bool GateOn => Gates != null && Ask != null && o.GateBlocks > 0;

    /// <summary>Slices (or loads) the plan for a part without building: the "build it" summary and the supervisor view.</summary>
    public BuildPlan PlanFor(long blueprintId, VoxelPlan design, BuildPart part, IBuildBody body, bool save = true)
    {
        var w = body.World;
        var prev = Plans?.Get(blueprintId, part.Index);
        if (prev != null && prev.Version != design.Hash()) prev = null;
        var (voxel, p) = Shape(design, part, prev?.Transform ?? BuildTransform.None, w.Dimension);
        var bp = BuildPlanner.Plan(voxel, w.PlanningView, StartCell(w), p, Options(body, part, prev), prev);
        bp.BlueprintId = blueprintId;
        bp.Version = design.Hash();
        bp.Dim = w.Dimension;
        if (save) Plans?.Save(bp);
        return bp;
    }

    private BuildPlannerOptions Options(IBuildBody body, BuildPart part, BuildPlan? prev, IReadOnlySet<BlockPos>? keep = null,
        IReadOnlySet<(int, int)>? hold = null) => new()
    {
        Keep = keep ?? new HashSet<BlockPos>(),
        Hold = hold ?? new HashSet<(int, int)>(),
        Buckets = Math.Max(1, body.Count(Items.WaterBucket, 0)),
        AllowHidden = o.HiddenFaces,
        MaxScaffold = Math.Max(o.MaxScaffold, 6) + (body.Count(Items.WaterBucket, 0) > 0 ? 6 : 0),
        WaterAvailable = body.Count(Items.WaterBucket, 0) > 0,
        GateBlocks = GateOn && part.Index == 0 && prev?.Gate.State is null or GateState.Pending ? o.GateBlocks : 0,
        IsProtected = body.IsProtected,
    };

    private static BlockPos StartCell(WorldState w) => Pathfinder.StartCellFor(w.PlanningView, w.Player.Position) ?? w.Player.Position.ToBlockPos();

    /// <summary>The design as built: the gate corrections applied, and the part's cells moved with it.</summary>
    private static (VoxelPlan Voxel, BuildPart Part) Shape(VoxelPlan design, BuildPart part, BuildTransform t, int dim)
    {
        if (t.IsNone) return (design, part);
        var voxel = PlanTransform.Apply(design, t);
        return (voxel, part.Count <= 1 ? BuildPart.Whole(voxel, dim) : part with { Cells = voxel.Blocks.Keys.Concat(voxel.Air).ToHashSet() });
    }

    public async Task<BuildReport> RunAsync(long blueprintId, VoxelPlan plan, BuildPart part, IBuildBody body, CancellationToken ct, int? layer = null)
    {
        var report = new BuildReport();
        var w = body.World;
        var rec = store?.GetBuild(blueprintId, part.Index) ?? new BuildRecord { BlueprintId = blueprintId, Part = part.Index };
        rec.Parts = part.Count;
        rec.Owner = body.Name;
        rec.State = "building";
        rec.Origin = plan.Origin;
        rec.Dim = w.Dimension;
        var design = plan;

        var bp = PlanFor(blueprintId, design, part, body, save: false);
        var (voxel, vpart) = Shape(design, part, bp.Transform, w.Dimension);
        bp.State = "building";
        report.Plan = bp;
        Plans?.Save(bp);
        report.Unreachable.AddRange(bp.Infeasible.Select(i => i.Pos));
        if (bp.Infeasible.Count > 0) Progress?.Invoke("can't place " + string.Join("; ", bp.Infeasible.Take(3).Select(i => $"{voxel.Local(i.Pos)} ({i.Why})")));

        // Claim the part's area (hive: disjoint parts, so bodies never work in each other's cells).
        var ttl = TimeSpan.FromSeconds(Math.Max(20, o.ClaimSeconds));
        DateTimeOffset renewAt = DateTimeOffset.MinValue;
        async Task<string?> ClaimAsync()
        {
            if (claims == null) return null;
            var now = _time.GetUtcNow();
            if (now < renewAt) return null;
            var c = await claims.TryClaimAsync(part.Claim, ttl, $"blueprint #{blueprintId} {part.Label}", ct).ConfigureAwait(false);
            renewAt = now + ttl / 2;
            return c.Ok ? null : $"build area {part.Label} is claimed by {c.Holder}";
        }

        var shortItems = new Dictionary<(int, int), string>();
        var foreign = new HashSet<BlockPos>();
        int column = -1, replans = 0, sinceCheckpoint = 0;
        int? currentLayer = null;
        HashSet<int>? included = null;
        try
        {
            if (await ClaimAsync().ConfigureAwait(false) is { } held) { report.Stopped = held; return Finish(); }

            // Stopped while asking (restart, death, stop): ask again, never carry on unasked.
            if (bp.Gate.State == GateState.Waiting && GateOn && part.Index == 0)
            {
                var g = await GateAsync().ConfigureAwait(false);
                if (g == GateOutcome.Stop) return Finish();
            }

            await PrefetchAsync().ConfigureAwait(false);
            // A shortfall: plan around the missing items (their blocks wait; nothing is planned to rest on them).
            if (shortItems.Count > 0)
            {
                bp = Replan(bp);
                report.Plan = bp;
                included = null;
            }
            if (voxel.Max.Y - voxel.Min.Y >= 2 || bp.Steps.Any(s => s.Action == StepAction.Scaffold)) _ = await body.ClutchAsync(ct).ConfigureAwait(false);

            restart:
            included = Included(bp);
            report.Total = rec.Total = bp.Steps.Count(s => s.Action == StepAction.Place && included.Contains(s.Index));
            for (int i = 0; i < bp.Steps.Count; i++)
            {
                var step = bp.Steps[i];
                if (step.Finished || !included.Contains(i)) continue;
                ct.ThrowIfCancellationRequested();
                if (await ClaimAsync().ConfigureAwait(false) is { } lost) { report.Stopped = lost; break; }
                if (step.Deps.Any(d => d >= 0 && d < bp.Steps.Count && d != i && (!bp.Steps[d].Finished || bp.Steps[d].Status == StepStatus.Skipped)))
                {
                    step.Note = "waits for an earlier step";
                    continue;
                }
                if (part.Index > 0 && step.IsPlacement && !await OtherPartsGateAsync().ConfigureAwait(false))
                {
                    report.Stopped = "waiting for the first part's confirmation gate";
                    break;
                }
                if (step.Action == StepAction.Place && currentLayer != step.Target.Y)
                {
                    if (currentLayer is { } done) CheckLayer(done);
                    currentLayer = step.Target.Y;
                }

                var outcome = await StepAsync(step).ConfigureAwait(false);
                if (report.Stopped != null) break;
                if (outcome == StepOutcome.Replan && replans < o.MaxReplans)
                {
                    replans++;
                    bp = Replan(bp);
                    report.Plan = bp;
                    goto restart;
                }
                if (outcome == StepOutcome.Placed && step.Gate && GateOn && part.Index == 0 && bp.Gate.State == GateState.Pending
                    && bp.Steps.Where(s => s.Gate).All(s => s.Finished))
                {
                    var g = await GateAsync().ConfigureAwait(false);
                    if (g == GateOutcome.Stop) break;
                    if (g == GateOutcome.Replanned) goto restart;
                }
                if (step.Action is StepAction.PourWater or StepAction.PickupWater or StepAction.Scaffold or StepAction.RemoveScaffold
                    || ++sinceCheckpoint >= Math.Max(1, o.CheckpointEvery))
                {
                    sinceCheckpoint = 0;
                    Checkpoint();
                }
            }
            if (report.Stopped == null && column >= 0) await LeaveColumnAsync().ConfigureAwait(false);
            if (currentLayer is { } lastLayer && report.Stopped == null) CheckLayer(lastLayer);
            if (report.Stopped == null && shortItems.Count > 0)
                report.Stopped = shortItems.Values.First() + (shortItems.Count > 1 ? $" (+{shortItems.Count - 1} more items)" : "")
                                 + "; built everything else, paused until I have them";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report.Stopped = "cancelled";
        }
        return Finish();

        // ---------------------------------------------------------------- one step

        async Task<StepOutcome> StepAsync(PlanStep step)
        {
            switch (step.Action)
            {
                case StepAction.Clear:
                    {
                        int id = w.GetBlock(step.Target);
                        if (id == 0 || BlockShapes.IsLiquid(id)) { step.Status = StepStatus.Verified; return StepOutcome.Done; }
                        if (foreign.Contains(step.Target)) { Skip(step, "someone else's block: left"); return StepOutcome.Done; }
                        if (body.IsProtected(step.Target, id))
                        {
                            report.Deviations.Add($"{BuildBlocks.Name(id, 0)} at {voxel.Local(step.Target)} is protected: left");
                            Skip(step, "protected");
                            return StepOutcome.Done;
                        }
                        if (!await MoveAsync(step, needStand: false).ConfigureAwait(false)) return StepOutcome.Done;
                        var r = step.Column >= 0 ? await body.DigHereAsync(step.Target, ct).ConfigureAwait(false) : await body.DigAsync(step.Target, ct).ConfigureAwait(false);
                        if (r.Ok) { report.Cleared++; step.Status = StepStatus.Verified; }
                        else { report.Deviations.Add($"couldn't clear {voxel.Local(step.Target)}: {Short(r.Message)}"); Skip(step, Short(r.Message)); }
                        return StepOutcome.Done;
                    }
                case StepAction.Place:
                case StepAction.Scaffold:
                    return await PlaceStepAsync(step).ConfigureAwait(false);
                case StepAction.RemoveScaffold:
                    {
                        int id = w.GetBlock(step.Target);
                        if (id == 0 || voxel.Blocks.ContainsKey(step.Target)) { bp.ScaffoldUp.Remove(step.Target); step.Status = StepStatus.Verified; return StepOutcome.Done; }
                        // Only our own placement comes down (protection rule 1: self-placed is always minable).
                        if (!w.Placed.IsPlaced(step.Target, w.Dimension, id) && !bp.ScaffoldUp.Contains(step.Target) && !rec.Scaffold.Contains(step.Target))
                        { Skip(step, "not ours"); return StepOutcome.Done; }
                        if (!await MoveAsync(step, needStand: step.Column >= 0).ConfigureAwait(false)) return StepOutcome.Done;
                        var r = step.Column >= 0 ? await body.DigHereAsync(step.Target, ct).ConfigureAwait(false) : await body.DigAsync(step.Target, ct).ConfigureAwait(false);
                        if (r.Ok)
                        {
                            report.ScaffoldRemoved++;
                            bp.ScaffoldUp.Remove(step.Target);
                            rec.Scaffold.Remove(step.Target);
                            step.Status = StepStatus.Verified;
                        }
                        else { report.Deviations.Add($"scaffold at {voxel.Local(step.Target)} left: {Short(r.Message)}"); Skip(step, Short(r.Message)); }
                        return StepOutcome.Done;
                    }
                case StepAction.PourWater:
                    {
                        if (!await MoveAsync(step, needStand: true).ConfigureAwait(false)) return StepOutcome.Done;
                        var r = await body.PourWaterAsync(step.Target, step.Face ?? BlockFace.Top, ct).ConfigureAwait(false);
                        if (!r.Ok) { report.Deviations.Add($"water column at {voxel.Local(step.Target)}: {Short(r.Message)}"); Skip(step, Short(r.Message)); return StepOutcome.Done; }
                        step.Status = StepStatus.Done;
                        Progress?.Invoke($"poured a water column to build down ({step.Note})");
                        return StepOutcome.Done;
                    }
                case StepAction.PickupWater:
                    {
                        if (!await MoveAsync(step, needStand: true).ConfigureAwait(false)) return StepOutcome.Done;
                        var r = await body.PickupWaterAsync(step.Target, ct).ConfigureAwait(false);
                        if (r.Ok)
                        {
                            step.Status = StepStatus.Verified;
                            foreach (var c in bp.Columns.Where(c => c.Source == step.Target)) c.Removed = true;
                        }
                        else { report.Deviations.Add($"water at {voxel.Local(step.Target)} left: {Short(r.Message)}"); Skip(step, Short(r.Message)); }
                        return StepOutcome.Done;
                    }
            }
            return StepOutcome.Done;
        }

        async Task<StepOutcome> PlaceStepAsync(PlanStep step)
        {
            bool scaffold = step.Action == StepAction.Scaffold;
            if (!scaffold && voxel.Blocks.TryGetValue(step.Target, out var want) && DesignChecks.SameState(want, w.GetBlock(step.Target), w.GetMeta(step.Target)))
            { report.Already++; step.Status = StepStatus.Verified; return StepOutcome.Done; }
            if (scaffold && BuildBlocks.Supports(w.GetBlock(step.Target))) { step.Status = StepStatus.Verified; return StepOutcome.Done; }
            int existing = w.GetBlock(step.Target);
            if (!Aiming.IsReplaceable(w, existing))
            {
                // Someone built there (or it's the wrong block): never dig a player's block on our own; re-plan around it.
                bool ours = bp.Placed.Contains(step.Target) || w.Placed.IsPlaced(step.Target, w.Dimension, existing);
                report.Deviations.Add($"{voxel.Local(step.Target)} holds {BuildBlocks.Name(existing, w.GetMeta(step.Target))}{(ours ? "" : " (not mine: someone built there)")}");
                if (!ours) foreign.Add(step.Target);
                Skip(step, ours ? "wrong block" : "someone built there");
                return StepOutcome.Replan;
            }
            int item, dmg;
            if (scaffold)
            {
                item = ScaffoldItem(body, bp);
                dmg = 0;
                if (item == 0) { report.Stopped = "missing scaffold blocks (cobblestone or planks)"; return StepOutcome.Done; }
            }
            else
            {
                (item, dmg) = (step.Item, step.Damage);
                if (body.Count(item, dmg) == 0 && !await HaveAsync(item, dmg).ConfigureAwait(false))
                {
                    step.Note = "missing " + Name(item, dmg);
                    return StepOutcome.Done; // stays pending: everything that doesn't need it goes on
                }
            }
            if (!await MoveAsync(step, needStand: true).ConfigureAwait(false)) return StepOutcome.Done;
            var r = await body.PlaceAsync(new BuildStep(scaffold ? StepKind.Scaffold : StepKind.Place, step.Target, scaffold ? item : step.Id, scaffold ? 0 : step.Meta,
                null, step.Face, step.HiddenFace, step.Yaw), item, dmg, ct).ConfigureAwait(false);
            if (!r.Ok)
            {
                report.Failed++;
                report.Deviations.Add($"{(scaffold ? "scaffold" : BuildBlocks.Name(step.Id, step.Meta))} at {voxel.Local(step.Target)}: {Short(r.Message)}");
                if (r.Message.Contains("void", StringComparison.OrdinalIgnoreCase) || r.Message.Contains("danger", StringComparison.OrdinalIgnoreCase))
                { report.Stopped = r.Message; return StepOutcome.Done; }
                step.Note = Short(r.Message);
                return StepOutcome.Replan; // re-plan from where I am now (another stand/face/technique)
            }
            step.Status = StepStatus.Done;
            bp.Placed.Add(step.Target);
            if (BuildBlocks.KindOf(step.Id) == BuildKind.Door && !scaffold) bp.Placed.Add(step.Target.Up());
            if (scaffold)
            {
                report.Scaffolded++;
                if (!bp.ScaffoldUp.Contains(step.Target)) bp.ScaffoldUp.Add(step.Target);
                if (!rec.Scaffold.Contains(step.Target)) rec.Scaffold.Add(step.Target);
                step.Status = StepStatus.Verified;
                return StepOutcome.Done;
            }
            report.Placed++;
            rec.Placed++;
            if (step.Gate) bp.Gate.Blocks.Add(step.Target);
            if (Verify(step)) step.Status = StepStatus.Verified;
            return StepOutcome.Placed;
        }

        /// <summary>To the step's stand: out of (or into, down to the depth of) a water column first.</summary>
        async Task<bool> MoveAsync(PlanStep step, bool needStand)
        {
            if (step.Column >= 0 && step.Column < bp.Columns.Count)
            {
                var c = bp.Columns[step.Column];
                if (column >= 0 && column != step.Column) await LeaveColumnAsync().ConfigureAwait(false);
                var r = await body.SwimToDepthAsync(c.Swim, c.Entry, step.Stand.Y, ct).ConfigureAwait(false);
                if (!r.Ok) { report.Stopped = $"water column at {voxel.Local(c.Source)}: {Short(r.Message)}"; return false; }
                column = step.Column;
                return true;
            }
            if (column >= 0 && !await LeaveColumnAsync().ConfigureAwait(false)) return false;
            if (!needStand || step.Stand == default) return true;
            if (StartCell(w) == step.Stand) return true;
            var g = await body.GotoAsync(step.Stand, ct).ConfigureAwait(false);
            if (!g.Ok) step.Note = "goto: " + Short(g.Message);
            return true; // the action itself says whether it worked from here
        }

        async Task<bool> LeaveColumnAsync()
        {
            if (column < 0) return true;
            var c = bp.Columns[column];
            var r = await body.SwimOutAsync(c.Swim, c.Entry, ct).ConfigureAwait(false);
            if (!r.Ok) { report.Stopped = $"couldn't climb out of the water column: {Short(r.Message)}"; return false; }
            column = -1;
            return true;
        }

        // ---------------------------------------------------------------- materials

        async Task PrefetchAsync()
        {
            var needs = bp.Steps.Where(s => s.Action == StepAction.Place && !s.Finished && (included ??= Included(bp)).Contains(s.Index))
                .GroupBy(s => (s.Item, s.Damage)).Select(g => (g.Key.Item, g.Key.Damage, Count: g.Count())).ToList();
            foreach (var (item, dmg, count) in needs)
            {
                if (body.Count(item, dmg) >= count) continue;
                var why = await body.EnsureAsync(item, dmg, Math.Min(count, 64 * 4), ct).ConfigureAwait(false);
                if (why != null && body.Count(item, dmg) == 0) shortItems[(item, dmg)] = why;
            }
        }

        /// <summary>Gets more of an item when the inventory ran out (once per item per run when it can't: no repeated asks).</summary>
        async Task<bool> HaveAsync(int item, int dmg)
        {
            if (shortItems.ContainsKey((item, dmg)) && !await Retry().ConfigureAwait(false)) return false;
            int left = bp.Steps.Count(s => s.Action == StepAction.Place && !s.Finished && s.Item == item && s.Damage == dmg);
            var why = await body.EnsureAsync(item, dmg, Math.Max(1, Math.Min(left, 64 * 4)), ct).ConfigureAwait(false);
            if (body.Count(item, dmg) > 0) { shortItems.Remove((item, dmg)); return true; }
            shortItems[(item, dmg)] = why ?? $"missing {Name(item, dmg)} (need {left}, have 0)";
            return false;
            Task<bool> Retry() => Task.FromResult(body.Count(item, dmg) > 0);
        }

        // ---------------------------------------------------------------- gate

        async Task<GateOutcome> GateAsync()
        {
            if (bp.Gate.Correction is { } pendingFix) return await CorrectAsync(pendingFix).ConfigureAwait(false);
            var q = GateQuestion?.Invoke(bp, voxel) ?? BuildGate.Question(bp, voxel, voxel.Blueprint.Name, _ => null, null, null);
            bp.Gate.State = GateState.Waiting;
            bp.Gate.Question = q;
            bp.Gate.AskedAt = _time.GetUtcNow();
            if (bp.Gate.Blocks.Count == 0) bp.Gate.Blocks = bp.Steps.Where(s => s.Gate && s.Finished).Select(s => s.Target).ToList();
            Checkpoint();
            if (column >= 0) await LeaveColumnAsync().ConfigureAwait(false);
            await Ask!(q).ConfigureAwait(false);
            Progress?.Invoke("paused at the confirmation gate: " + q);
            var reply = await Gates!.WaitAsync(blueprintId, TimeSpan.FromMinutes(Math.Max(0.01, o.GateMinutes)), ct).ConfigureAwait(false);
            // A restart (or another process) may have recorded the answer in the plan meanwhile.
            if (reply == null && Plans?.Get(blueprintId, part.Index) is { Gate.State: GateState.Approved } fresh) { bp.Gate = fresh.Gate; return GateOutcome.Continue; }
            switch (reply?.Verdict)
            {
                case null:
                    report.Stopped = "paused at the confirmation gate: no answer yet (no default: say 'keep going' or what to change)";
                    report.AtGate = true;
                    return GateOutcome.Stop;
                case GateVerdict.Hold:
                    report.Stopped = $"paused at the confirmation gate: {reply.By} said \"{Short(reply.Text)}\"; tell me what to change";
                    report.AtGate = true;
                    return GateOutcome.Stop;
                case GateVerdict.Approve:
                    bp.Gate.State = GateState.Approved;
                    bp.Gate.ApprovedBy = reply.By;
                    Checkpoint();
                    Progress?.Invoke($"{reply.By} confirmed the first blocks: carrying on");
                    return GateOutcome.Continue;
                case GateVerdict.Undo:
                    var undone = await UndoPlacedAsync(body, bp, w, ct).ConfigureAwait(false);
                    bp.State = "undone";
                    report.Stopped = $"undone: took down {undone} block{(undone == 1 ? "" : "s")}";
                    return GateOutcome.Stop;
                default:
                    return await CorrectAsync(reply.Correction!).ConfigureAwait(false);
            }
        }

        async Task<GateOutcome> CorrectAsync(BuildTransform fix)
        {
            // The gate blocks come down (self-placed, so allowed), the plan turns/mirrors/moves, the gate repeats.
            if (column >= 0) await LeaveColumnAsync().ConfigureAwait(false);
            foreach (var p in bp.Gate.Blocks.Concat(bp.Placed).Distinct().OrderByDescending(p => p.Y).ToList())
            {
                int id = w.GetBlock(p);
                if (id == 0) continue;
                var r = await body.DigAsync(p, ct).ConfigureAwait(false);
                if (r.Ok) { bp.Placed.Remove(p); report.Placed = Math.Max(0, report.Placed - 1); }
                else report.Deviations.Add($"couldn't take down gate block {p.X},{p.Y},{p.Z}: {Short(r.Message)}");
            }
            bp.Transform = bp.Transform.Then(fix);
            bp.Gate.Correction = null;
            bp.Gate.State = GateState.Pending;
            bp.Gate.Rounds++;
            bp.Gate.Blocks.Clear();
            bp.Gate.Question = null;
            (voxel, vpart) = Shape(design, part, bp.Transform, w.Dimension);
            var steps = bp.Steps;
            bp = BuildPlanner.Plan(voxel, w.PlanningView, StartCell(w), vpart, Options(body, part, bp), bp);
            bp.BlueprintId = blueprintId;
            bp.Version = design.Hash();
            bp.Dim = w.Dimension;
            bp.State = "building";
            report.Plan = bp;
            Checkpoint();
            Progress?.Invoke($"re-planned {bp.Transform.Text}: placing the first blocks again to check");
            return GateOutcome.Replanned;
        }

        async Task<bool> OtherPartsGateAsync()
        {
            if (!GateOn || Plans == null) return true;
            for (int k = 0; k < Math.Max(1, o.GateMinutes * 30); k++)
            {
                var first = Plans.Get(blueprintId, 0);
                if (first == null || first.Gate.State is GateState.Approved or GateState.Off) return true;
                await Task.Delay(TimeSpan.FromSeconds(2), _time, ct).ConfigureAwait(false);
            }
            return false;
        }

        // ---------------------------------------------------------------- bookkeeping

        BuildPlan Replan(BuildPlan current)
        {
            var next = BuildPlanner.Plan(voxel, w.PlanningView, StartCell(w), vpart,
                Options(body, part, current, foreign, shortItems.Keys.Select(k => (k.Item1, k.Item2)).ToHashSet()), current);
            next.BlueprintId = blueprintId;
            next.Version = design.Hash();
            next.Dim = w.Dimension;
            next.State = "building";
            // Someone else's blocks stay: a block that appeared in a planned cell since we started (not ours) is reported, not dug.
            foreach (var s in next.Steps.Where(s => s.Action == StepAction.Clear && !s.Finished))
            {
                int id = w.GetBlock(s.Target);
                bool ours = next.Placed.Contains(s.Target) || w.Placed.IsPlaced(s.Target, w.Dimension, id);
                if (!foreign.Contains(s.Target) && (ours || !voxel.Blocks.ContainsKey(s.Target))) continue;
                if (foreign.Add(s.Target) || !report.Deviations.Any(d => d.Contains(voxel.Local(s.Target))))
                    report.Deviations.Add($"{voxel.Local(s.Target)} holds {BuildBlocks.Name(id, w.GetMeta(s.Target))} (not mine: someone built there)");
                s.Status = StepStatus.Skipped;
                s.Note = "someone built there";
            }
            Plans?.Save(next);
            Progress?.Invoke($"re-planned the rest ({next.Steps.Count(s => !s.Finished)} steps) after a deviation");
            return next;
        }

        HashSet<int> Included(BuildPlan p)
        {
            if (layer is not { } ly) return Enumerable.Range(0, p.Steps.Count).ToHashSet();
            var set = new HashSet<int>();
            var q = new Queue<int>(p.Steps.Where(s => s.Action == StepAction.Place && s.Target.Y - voxel.Origin.Y == ly).Select(s => s.Index));
            while (q.Count > 0)
            {
                int k = q.Dequeue();
                if (!set.Add(k)) continue;
                foreach (var d in p.Steps[k].Deps) if (d >= 0 && d < p.Steps.Count) q.Enqueue(d);
            }
            bool top = ly >= voxel.Blueprint.H - 1;
            foreach (var s in p.Steps.Where(s => s.Action is StepAction.RemoveScaffold or StepAction.PickupWater))
                if (top || rec.Scaffold.Contains(s.Target)) set.Add(s.Index);
            return set;
        }

        void Skip(PlanStep s, string why)
        {
            s.Status = StepStatus.Skipped;
            s.Note = why;
        }

        void Checkpoint()
        {
            Plans?.Save(bp);
            store?.SaveBuild(rec);
        }

        bool Verify(PlanStep s)
        {
            if (!voxel.Blocks.TryGetValue(s.Target, out var want)) return true;
            int id = w.GetBlock(s.Target), meta = w.GetMeta(s.Target);
            // A variant is part of the block: a stone slab (44:0) in a cobblestone-slab (44:3) cell is wrong, not done.
            if (id != want.Id || !DesignChecks.SameState(want, id, meta))
            { report.Deviations.Add($"{voxel.Local(s.Target)} is {BuildBlocks.Name(id, meta)}, planned {BuildBlocks.Name(want.Id, want.Meta)}"); return false; }
            if (want.Kind == BuildKind.Stairs && (meta & 3) != (want.Meta & 3))
            { report.Deviations.Add($"stairs at {voxel.Local(s.Target)} go up {BuildBlocks.StairsAscend(meta).Name()}, planned {BuildBlocks.StairsAscend(want.Meta).Name()}"); return false; }
            if (want.Kind == BuildKind.Door && (meta & 1) != (want.Meta & 1))
            { report.Deviations.Add($"door at {voxel.Local(s.Target)} turned across the wall"); return false; }
            return true;
        }

        void CheckLayer(int y)
        {
            var cells = voxel.Blocks.Values.Where(b => b.Pos.Y == y && vpart.Cells.Contains(b.Pos)).ToList();
            var miss = cells.Where(b => !DesignChecks.SameState(b, w.GetBlock(b.Pos), w.GetMeta(b.Pos))).ToList();
            var line = string.Create(CultureInfo.InvariantCulture, $"layer {y - voxel.Origin.Y}: {cells.Count - miss.Count}/{cells.Count}") +
                       (miss.Count > 0 ? $", missing {string.Join(" ", miss.Take(3).Select(b => voxel.Local(b.Pos)))}" : "");
            Progress?.Invoke(line);
            log?.LogInformation("blueprint #{Id} {Part}: {Line}", blueprintId, part.Label, line);
        }

        BuildReport Finish()
        {
            foreach (var b in voxel.Blocks.Values.Where(b => vpart.Cells.Contains(b.Pos) && (layer is not { } ly || b.Pos.Y - voxel.Origin.Y == ly)))
                if (w.IsLoaded(b.Pos.X, b.Pos.Z) && !DesignChecks.SameState(b, w.GetBlock(b.Pos), w.GetMeta(b.Pos))) report.Missing.Add(b.Pos);
            bool partDone = report.Stopped == null && report.Missing.Count == 0 && bp.ScaffoldUp.Count == 0 && layer == null;
            // A finished part is done; anything else stays "building" (resumable: unplaced cells, leftover scaffold, the gate).
            rec.State = bp.State == "undone" ? "undone" : partDone || (layer == null && report.Stopped == null && report.Missing.Count == 0) ? "done" : "building";
            rec.Scaffold = bp.ScaffoldUp.ToList();
            rec.Note = report.Text(part.Label);
            if (bp.State != "undone") bp.State = rec.State == "done" ? "done" : report.AtGate ? "gate" : "paused";
            bp.Note = report.Stopped;
            Plans?.Save(bp);
            store?.SaveBuild(rec);
            report.Plan = bp;
            if (claims != null && (partDone || report.Stopped != null))
                try { claims.ReleaseAsync(part.Claim, CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception) { }
            return report;
        }
    }

    private enum StepOutcome { Done, Placed, Replan }
    private enum GateOutcome { Continue, Stop, Replanned }

    /// <summary>
    /// Takes down everything a build placed (its ledger, scaffolding included), top first; only blocks still standing.
    /// Self-placed, so the protection policy allows it. Returns how many came down.
    /// </summary>
    public static async Task<int> UndoPlacedAsync(IBuildBody body, BuildPlan bp, WorldState w, CancellationToken ct)
    {
        int n = 0;
        foreach (var p in bp.Placed.Concat(bp.ScaffoldUp).Distinct().OrderByDescending(p => p.Y).ToList())
        {
            if (w.GetBlock(p) == 0) { bp.Placed.Remove(p); continue; }
            var r = await body.DigAsync(p, ct).ConfigureAwait(false);
            if (!r.Ok) continue;
            n++;
            bp.Placed.Remove(p);
            bp.ScaffoldUp.Remove(p);
        }
        return n;
    }

    /// <summary>The cheapest clutch block on hand that the plan doesn't still need all of.</summary>
    private static int ScaffoldItem(IBuildBody body, BuildPlan bp)
    {
        foreach (var id in GameTools.ClutchBlocks.Ids)
        {
            int need = bp.Steps.Count(s => s.Action == StepAction.Place && !s.Finished && s.Item == id);
            if (body.Count(id, -1) > need) return id;
        }
        return GameTools.ClutchBlocks.Ids.FirstOrDefault(id => body.Count(id, -1) > 0);
    }

    private static string Name(int item, int damage) => GameBuildBody.ItemName(item, damage);

    private static string Short(string s) => s.Length <= 90 ? s : s[..89] + "…";
}
