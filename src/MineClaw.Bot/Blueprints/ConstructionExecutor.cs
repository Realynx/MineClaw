using System.Globalization;
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

    public string Text(string what) =>
        $"{what}: placed {Placed}" + (Already > 0 ? $", {Already} already there" : "") + (Cleared > 0 ? $", cleared {Cleared}" : "")
        + (Scaffolded > 0 ? $", scaffold {Scaffolded} ({ScaffoldRemoved} removed)" : "")
        + (Missing.Count > 0 ? $", {Missing.Count} missing" : "") + (Deviations.Count > 0 ? $"; deviations: {string.Join("; ", Deviations.Take(3))}" : "")
        + (Stopped != null ? $"; stopped: {Stopped}" : "");
}

/// <summary>
/// Runs a construction plan with one body (per part, optionally one layer): materials first through the body's acquisition,
/// a claim on the part's area (renewed), clutch blocks at height, obstructions cleared, placements in support order with the
/// facing yaw-set metas need, temporary scaffolding (self-placed: removable), checkpoints every few placements (resume after a
/// restart re-plans from the world: what stands is skipped), a per-layer before/after comparison with deviations, and the
/// scaffolding taken down at the end. Missing materials stop it with "missing &lt;item&gt; ..." (a hauler can answer that).
/// </summary>
public sealed class ConstructionExecutor(BlueprintOptions o, BlueprintStore? store = null, IClaimBoard? claims = null, TimeProvider? time = null,
    ILogger? log = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Progress lines (layer done, deviations) for the activity/brain.</summary>
    public Action<string>? Progress { get; set; }

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
        var bp = ConstructionPlanner.Plan(plan, w.PlanningView, part, o.MaxScaffold);
        var steps = bp.Steps.Where(s => layer is not { } ly || s.Kind == StepKind.RemoveScaffold || s.Pos.Y - plan.Origin.Y == ly).ToList();
        if (layer != null) steps.RemoveAll(s => s.Kind == StepKind.RemoveScaffold && !rec.Scaffold.Contains(s.Pos) && !steps.Any(x => x.Kind == StepKind.Scaffold && x.Pos == s.Pos));
        // Scaffold left by an interrupted run comes down too (it is ours: the placed-block log says so).
        foreach (var old in rec.Scaffold.Where(p => !steps.Any(s => s.Pos == p && s.Kind == StepKind.RemoveScaffold)).ToList())
            steps.Add(new BuildStep(StepKind.RemoveScaffold, old, w.GetBlock(old), 0));
        report.Unreachable.AddRange(bp.Unreachable);
        report.Total = rec.Total = steps.Count(s => s.Kind == StepKind.Place);

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
        try
        {
            if (await ClaimAsync().ConfigureAwait(false) is { } held) { report.Stopped = held; return Finish(); }

            // Materials for every placement (scaffold counted as clutch blocks).
            var needs = steps.Where(s => s.Kind == StepKind.Place).GroupBy(s => s.Item).Select(g => (g.Key.Item, g.Key.Damage, Count: g.Count())).ToList();
            foreach (var (item, dmg, count) in needs)
            {
                if (body.Count(item, dmg) >= count) continue;
                if (await body.EnsureAsync(item, dmg, Math.Min(count, 64 * 4), ct).ConfigureAwait(false) is { } why && body.Count(item, dmg) == 0)
                { report.Stopped = why; return Finish(); }
            }
            if (plan.Max.Y - plan.Min.Y >= 2 || steps.Any(s => s.Kind == StepKind.Scaffold)) _ = await body.ClutchAsync(ct).ConfigureAwait(false);

            int sinceCheckpoint = 0;
            int? currentLayer = null;
            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();
                if (await ClaimAsync().ConfigureAwait(false) is { } lost) { report.Stopped = lost; break; }
                if (step.Kind == StepKind.Place && currentLayer != step.Pos.Y)
                {
                    if (currentLayer is { } done) CheckLayer(done);
                    currentLayer = step.Pos.Y;
                }
                switch (step.Kind)
                {
                    case StepKind.Clear:
                        {
                            int id = w.GetBlock(step.Pos);
                            if (id == 0) break;
                            if (body.IsProtected(step.Pos, id)) { report.Deviations.Add($"{BuildBlocks.Name(id, 0)} at {plan.Local(step.Pos)} is protected: left"); break; }
                            var r = await body.DigAsync(step.Pos, ct).ConfigureAwait(false);
                            if (r.Ok) report.Cleared++;
                            else report.Deviations.Add($"couldn't clear {plan.Local(step.Pos)}: {Short(r.Message)}");
                            break;
                        }
                    case StepKind.Scaffold:
                        {
                            if (BuildBlocks.Supports(w.GetBlock(step.Pos))) break;
                            int sid = ScaffoldItem(body, needs);
                            if (sid == 0) { report.Stopped = "missing scaffold blocks (cobblestone or planks)"; break; }
                            var r = await body.PlaceAsync(step with { Id = sid }, sid, 0, ct).ConfigureAwait(false);
                            if (!r.Ok) { report.Deviations.Add($"scaffold at {plan.Local(step.Pos)}: {Short(r.Message)}"); break; }
                            report.Scaffolded++;
                            if (!rec.Scaffold.Contains(step.Pos)) rec.Scaffold.Add(step.Pos);
                            store?.SaveBuild(rec);
                            break;
                        }
                    case StepKind.RemoveScaffold:
                        {
                            int id = w.GetBlock(step.Pos);
                            if (id == 0 || plan.Blocks.ContainsKey(step.Pos)) { rec.Scaffold.Remove(step.Pos); break; }
                            // Only our own placement comes down (protection rule 1: self-placed is always minable).
                            if (!w.Placed.IsPlaced(step.Pos, w.Dimension, id) && !rec.Scaffold.Contains(step.Pos)) break;
                            var r = await body.DigAsync(step.Pos, ct).ConfigureAwait(false);
                            if (r.Ok) { report.ScaffoldRemoved++; rec.Scaffold.Remove(step.Pos); store?.SaveBuild(rec); }
                            else report.Deviations.Add($"scaffold at {plan.Local(step.Pos)} left: {Short(r.Message)}");
                            break;
                        }
                    default:
                        {
                            if (plan.Blocks.TryGetValue(step.Pos, out var want) && DesignChecks.SameState(want, w.GetBlock(step.Pos), w.GetMeta(step.Pos)))
                            { report.Already++; break; }
                            var (item, dmg) = step.Item;
                            if (body.Count(item, dmg) == 0)
                            {
                                int left = steps.Count(s => s.Kind == StepKind.Place && s.Item == step.Item && !Done(s));
                                if (await body.EnsureAsync(item, dmg, Math.Min(left, 64 * 4), ct).ConfigureAwait(false) is { } why && body.Count(item, dmg) == 0)
                                { report.Stopped = why; break; }
                            }
                            var r = await PlaceAsync(body, step, item, dmg, ct).ConfigureAwait(false);
                            if (!r.Ok)
                            {
                                report.Failed++;
                                report.Deviations.Add($"{BuildBlocks.Name(step.Id, step.Meta)} at {plan.Local(step.Pos)}: {Short(r.Message)}");
                                if (r.Message.Contains("void", StringComparison.OrdinalIgnoreCase) || r.Message.Contains("danger", StringComparison.OrdinalIgnoreCase))
                                    report.Stopped = r.Message;
                                break;
                            }
                            report.Placed++;
                            rec.Placed++;
                            Verify(step);
                            if (++sinceCheckpoint >= Math.Max(1, o.CheckpointEvery)) { sinceCheckpoint = 0; store?.SaveBuild(rec); }
                            break;
                        }
                }
                if (report.Stopped != null) break;
            }
            if (currentLayer is { } lastLayer && report.Stopped == null) CheckLayer(lastLayer);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report.Stopped = "cancelled";
        }
        return Finish();

        bool Done(BuildStep s) => plan.Blocks.TryGetValue(s.Pos, out var want) && DesignChecks.SameState(want, w.GetBlock(s.Pos), w.GetMeta(s.Pos));

        void Verify(BuildStep s)
        {
            if (!plan.Blocks.TryGetValue(s.Pos, out var want)) return;
            int id = w.GetBlock(s.Pos), meta = w.GetMeta(s.Pos);
            if (id != want.Id) { report.Deviations.Add($"{plan.Local(s.Pos)} is {BuildBlocks.Name(id, meta)}, planned {BuildBlocks.Name(want.Id, want.Meta)}"); return; }
            if (want.Kind == BuildKind.Stairs && (meta & 3) != (want.Meta & 3))
                report.Deviations.Add($"stairs at {plan.Local(s.Pos)} go up {BuildBlocks.StairsAscend(meta).Name()}, planned {BuildBlocks.StairsAscend(want.Meta).Name()}");
            if (want.Kind == BuildKind.Door && (meta & 1) != (want.Meta & 1))
                report.Deviations.Add($"door at {plan.Local(s.Pos)} turned across the wall");
        }

        void CheckLayer(int y)
        {
            var cells = plan.Blocks.Values.Where(b => b.Pos.Y == y && part.Cells.Contains(b.Pos)).ToList();
            var miss = cells.Where(b => !DesignChecks.SameState(b, w.GetBlock(b.Pos), w.GetMeta(b.Pos))).ToList();
            var line = string.Create(CultureInfo.InvariantCulture, $"layer {y - plan.Origin.Y}: {cells.Count - miss.Count}/{cells.Count}") +
                       (miss.Count > 0 ? $", missing {string.Join(" ", miss.Take(3).Select(b => plan.Local(b.Pos)))}" : "");
            Progress?.Invoke(line);
            log?.LogInformation("blueprint #{Id} {Part}: {Line}", blueprintId, part.Label, line);
        }

        BuildReport Finish()
        {
            foreach (var b in plan.Blocks.Values.Where(b => part.Cells.Contains(b.Pos) && (layer is not { } ly || b.Pos.Y - plan.Origin.Y == ly)))
                if (w.IsLoaded(b.Pos.X, b.Pos.Z) && !DesignChecks.SameState(b, w.GetBlock(b.Pos), w.GetMeta(b.Pos))) report.Missing.Add(b.Pos);
            bool partDone = report.Stopped == null && report.Missing.Count == 0 && rec.Scaffold.Count == 0 && layer == null;
            // A finished part is done; anything else stays "building" (resumable: unplaced cells and leftover scaffold).
            rec.State = partDone || (layer == null && report.Stopped == null && report.Missing.Count == 0) ? "done" : "building";
            rec.Note = report.Text(part.Label);
            store?.SaveBuild(rec);
            if (claims != null && (partDone || report.Stopped != null))
                try { claims.ReleaseAsync(part.Claim, CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception) { }
            return report;
        }
    }

    /// <summary>The cheapest clutch block on hand that the plan doesn't still need all of.</summary>
    private static int ScaffoldItem(IBuildBody body, List<(int Item, int Damage, int Count)> needs)
    {
        foreach (var id in GameTools.ClutchBlocks.Ids)
        {
            int need = needs.Where(n => n.Item == id).Sum(n => n.Count);
            if (body.Count(id, -1) > need) return id;
        }
        return GameTools.ClutchBlocks.Ids.FirstOrDefault(id => body.Count(id, -1) > 0);
    }

    /// <summary>Directional blocks first walk to where the placer faces the right way (stairs, doors, chests, gates).</summary>
    private static async Task<ActionResult> PlaceAsync(IBuildBody body, BuildStep step, int item, int dmg, CancellationToken ct)
    {
        var w = body.World;
        if (step.Look is { } look)
        {
            var (dx, dz) = look.Offset();
            foreach (int k in new[] { 2, 1, 3 })
                foreach (int dy in new[] { 0, -1, 1 })
                {
                    var s = new BlockPos(step.Pos.X - dx * k, step.Pos.Y + dy, step.Pos.Z - dz * k);
                    if (!Standable(w, s)) continue;
                    if (w.Player.Position.ToBlockPos() != s) await body.GotoAsync(s, ct).ConfigureAwait(false);
                    goto place;
                }
        }
    place:
        var r = await body.PlaceAsync(step, item, dmg, ct).ConfigureAwait(false);
        if (!r.Ok && r.Message.Contains("standing in it", StringComparison.OrdinalIgnoreCase))
        {
            // Step out of the cell (any standable neighbour two away) and try once more.
            foreach (var (dx, dz) in new[] { (2, 0), (-2, 0), (0, 2), (0, -2) })
            {
                var s = new BlockPos(step.Pos.X + dx, step.Pos.Y, step.Pos.Z + dz);
                if (!Standable(w, s)) continue;
                if ((await body.GotoAsync(s, ct).ConfigureAwait(false)).Ok) return await body.PlaceAsync(step, item, dmg, ct).ConfigureAwait(false);
            }
        }
        return r;
    }

    private static bool Standable(WorldState w, BlockPos s) =>
        w.IsLoaded(s.X, s.Z) && !Data.Blocks.Get(w.GetBlock(s)).Solid && !Data.Blocks.Get(w.GetBlock(s.Up())).Solid
        && BuildBlocks.Supports(w.GetBlock(s.Down())) && !Data.Blocks.IsLava(w.GetBlock(s)) && !Data.Blocks.IsLava(w.GetBlock(s.Up()));

    private static string Short(string s) => s.Length <= 90 ? s : s[..89] + "…";
}
