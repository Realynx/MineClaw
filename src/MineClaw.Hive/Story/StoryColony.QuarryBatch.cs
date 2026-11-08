using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    internal const int QuarryBatchMax = 12;

    /// <summary>Keep nearby work on one access trip. Below the working layer only thin remnants qualify: never bore into the
    /// next intact floor. The SDK mines the reserved cells while up on its pillar and again while taking the pillar down.</summary>
    private ActiveJob QuarryDigBatch(PawnState p, PawnLive live, IStoryWorld w, ZoneState z, QuarryScan q, ActiveJob job,
        HashSet<P3> mine, HashSet<P3> stoodOn)
    {
        var seed = job.At!.Value;
        var tool = PlanTool(p, live, w.Block(seed.X, seed.Y, seed.Z).Id);
        var cells = new List<P3> { seed };
        bool Near(P3 at) => (at.X - seed.X) * (at.X - seed.X) + (at.Z - seed.Z) * (at.Z - seed.Z) <= 9;
        bool Takes(P3 at) => !mine.Contains(at) && QuarryCellOpen(p, w, at, stoodOn)
            && CompatibleDigTool(tool, PlanTool(p, live, w.Block(at.X, at.Y, at.Z).Id));
        foreach (var at in q.Dig.Where(at => at != seed && Near(at)).OrderBy(at => at.Dist(seed)))
        {
            if (cells.Count >= QuarryBatchMax) break;
            if (Takes(at)) cells.Add(at);
        }
        // A scratch scan applies the same liquid/falling/cave rules without changing the working layer's status.
        var check = new QuarryScan { Top = q.Top, Bottom = q.Bottom, Ring = q.Ring };
        foreach (var (column, bottom) in q.Bottoms) check.Bottoms[column] = bottom;
        var columns = z.Cells.Sorted().Where(c => Near(new P3(c.X, seed.Y, c.Z)))
            .OrderBy(c => Math.Abs(c.X - seed.X) + Math.Abs(c.Z - seed.Z)).ToList();
        var stopped = new HashSet<(int X, int Z)>();
        for (int y = seed.Y - 1; y >= Math.Max(q.Bottom, seed.Y - Scripting.Story.ReachScan.MaxPillar) && cells.Count < QuarryBatchMax; y--)
            foreach (var (x, zz) in columns)
            {
                if (cells.Count >= QuarryBatchMax) break;
                if (stopped.Contains((x, zz))) continue;
                var at = new P3(x, y, zz);
                if (OpenCell(w.Block(x, y, zz).Id)) continue;
                P3? step = q.Ring is { } ring ? StoryQuarry.Step(ring, q.Top, q.Top - y) : null;
                if (!ThinRemnant(w, at) || ClassifyQuarryCell(w, z, check, at, step) != QuarryCell.Dig
                    || HeldForMissingStep(w, q, at) || !Takes(at)) { stopped.Add((x, zz)); continue; }
                cells.Add(at);
            }
        if (cells.Count < 2) return job;
        job.DigCells = cells;
        job.Json["dig_cells"] = new JsonArray(cells.Select(at => (JsonNode)at.ToString()).ToArray());
        job.MoreLeases.AddRange(cells.Skip(1).Select(at => "b:" + at));
        return job;
    }

    private static bool CompatibleDigTool(ToolPlan? first, ToolPlan? next) => next == null
        || next.Wait == null && first != null && first.Hold == next.Hold && first.HoldEnch == next.HoldEnch
            && first.Take == next.Take && first.TakeEnch == next.TakeEnch;

    private static bool ThinRemnant(IStoryWorld w, P3 at) => new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }
        .Count(d => w.Loaded(at.X + d.Item1, at.Z + d.Item2)
            && OpenCell(w.Block(at.X + d.Item1, at.Y, at.Z + d.Item2).Id)) >= 3;

    private static bool HeldForMissingStep(IStoryWorld w, QuarryScan q, P3 at) => q.Ring is { } ring
        && q.Top - at.Y is var k && k > 0 && StoryQuarry.Step(ring, q.Top, k - 1) == at.Add(0, 1, 0)
        && StepNeedsBlock(w, at.Add(0, 1, 0)) != null;

    /// <summary>Each attempted cell has its own success/failure and lease. Untouched cells stay reserved until the job ends.</summary>
    private void DigProgress(PawnState p, ActiveJob j, string? note)
    {
        foreach (var held in j.MoreLeases)
            if (HoldsLease(held, p.Name)) _leases[held] = (p.Name, Now + LeaseTtl);
        if (note == null || !note.StartsWith("digcell ", StringComparison.Ordinal) || j.DigCells == null) return;
        int split = note.IndexOf(' ', 8);
        if (split < 0) return;
        var text = note[8..split];
        var cell = j.DigCells.FindIndex(at => at.ToString() == text);
        if (cell < 0) return; // never credit an unreserved cell or a duplicate report
        string status = note[(split + 1)..];
        if (status != "done" && !status.StartsWith("failed: ", StringComparison.Ordinal)) return;
        var at = j.DigCells[cell];
        j.DigCells.RemoveAt(cell);
        j.DigReported = true;
        var key = "b:" + at;
        j.MoreLeases.Remove(key);
        if (key != j.Lease && HoldsLease(key, p.Name)) _leases.Remove(key);
        var single = DigCellJob(j, at);
        if (status == "done") Done(p, single, null);
        else Failed(p, single, status[8..]);
        if (single.Source == "ghost" && single.SourceId != null) j.Cells?.Remove(single.SourceId);
        j.DigDesignations.Remove(at);
    }

    private static ActiveJob DigCellJob(ActiveJob batch, P3 at)
    {
        bool extra = batch.DigDesignations.TryGetValue(at, out var designation);
        return new ActiveJob
        {
            Id = batch.Id + "@" + at, Type = batch.Type, Kind = "dig", At = at, Lease = "b:" + at,
            Source = extra ? "designation" : batch.Source, SourceId = extra ? designation : batch.SourceId,
            Cells = !extra && batch.Source == "ghost" && batch.SourceId != null ? [batch.SourceId] : null,
            Xp = batch.Xp, Since = batch.Since,
        };
    }

    /// <summary>The batch's fetch is settled once; per-cell reports already accounted for successes and failures.</summary>
    private void DigBatchDone(PawnState p, ActiveJob j)
    {
        SettleStock(j);
        GearTakesEnded(j, done: true);
        ChestsReached(p, j);
        // Compatibility with older callers: only the seed they actually removed is credited, never untouched tail cells.
        if (!j.DigReported && j.At is { } at && (j.Source is "designation" or "ghost"
            || World is { } w && w.Loaded(at.X, at.Z) && OpenCell(w.Block(at.X, at.Y, at.Z).Id)))
            Done(p, DigCellJob(j, at), null);
        ZoneWorkDone(j);
    }

    private int CancelQuarryBatches(Area? area, IReadOnlyList<P3>? blocks)
    {
        int count = 0;
        foreach (var (owner, job) in _current.ToList())
            if (job.DigCells?.Any(at => area?.Contains(at) == true || blocks?.Contains(at) == true) == true
                && Pawns.TryGetValue(owner, out var p))
            {
                Supersede(p, "task cancelled by owner");
                p.Rev = Bump();
                count++;
            }
        return count;
    }

    /// <summary>Changing a quarry or its keep-out area stops its old batch before it can touch a removed or forbidden cell.</summary>
    private void StopInvalidQuarryBatches()
    {
        OreTick();
        foreach (var (owner, job) in _current.ToList())
        {
            if (job.DigCells is not { } cells || !Pawns.TryGetValue(owner, out var p)) continue;
            if (job.Source != "zone") continue;
            var zone = Zones.FirstOrDefault(z => z.Id == job.SourceId && z.Kind == "mine");
            bool quarry = job.Json["vein_quarry"]?.GetValue<bool>() ?? true;
            if (zone != null && IsQuarry(zone) == quarry
                && cells.All(at => (job.DigDesignations.ContainsKey(at) || zone.Contains(at.X, at.Z)) && !Forbidden(at))) continue;
            Supersede(p, "quarry changed");
            p.Rev = Bump();
        }
    }
}
