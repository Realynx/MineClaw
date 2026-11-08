using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    /// <summary>A rescan can rewind the working layer to a high remnant. Its access still starts at the excavated floor below,
    /// so keep the entire stair to that floor usable rather than stopping repairs at the remnant's height.</summary>
    private static void QuarryAccess(IStoryWorld w, ZoneState z, QuarryScan q)
    {
        q.WorkFeet = QuarryAccessColumns(z, q.Dig.Concat(q.SealFix.Select(f => f.At)))
            .Select(c => QuarryColumnFeet(w, c.X, c.Z, q.Y)).OfType<int>().Cast<int?>().Min();
        int access = q.WorkFeet is { } feet ? q.Top + 1 - feet : q.Layer + 1;
        q.AccessLayer = Math.Clamp(Math.Max(q.Layer + 1, access), 0, q.MaxK);
    }

    private static HashSet<(int X, int Z)> QuarryAccessColumns(ZoneState z, IEnumerable<P3> targets)
    {
        var columns = new HashSet<(int X, int Z)>();
        foreach (var target in targets)
            for (int dx = -3; dx <= 3; dx++)
                for (int dz = -3; dz <= 3; dz++)
                    if (dx * dx + dz * dz <= 9 && z.Contains(target.X + dx, target.Z + dz))
                        columns.Add((target.X + dx, target.Z + dz));
        return columns;
    }

    /// <summary>The first surface below an open column. Stop at solid or liquid: a cave beneath an intact floor is not access.</summary>
    private static int? QuarryColumnFeet(IStoryWorld w, int x, int z, int targetY)
    {
        if (!w.Loaded(x, z)) return null;
        for (int y = Math.Min(w.Height - 3, targetY + 1); y >= Math.Max(1, targetY - Scripting.Story.ReachScan.MaxPillar - 4); y--)
        {
            if (StoryWander.Passable(w.Block(x, y, z).Id)) continue;
            return StoryWander.Standable(w, x, y + 1, z) ? y + 1 : null;
        }
        return null;
    }

    private ActiveJob? QuarryStairRepair(PawnState p, PawnLive live, ZoneState z, QuarryScan q)
    {
        int fixLayer = q.StepFix.Select(f => q.Top - f.At.Y).DefaultIfEmpty(int.MaxValue).Min();
        int clearLayer = q.StairClear.Select(f => f.Layer).DefaultIfEmpty(int.MaxValue).Min();
        if (fixLayer == int.MaxValue && clearLayer == int.MaxValue) return null;
        int layer = Math.Min(fixLayer, clearLayer);
        ActiveJob? job;
        if (clearLayer <= fixLayer)
        {
            var at = q.StairClear.Where(c => c.Layer == layer).OrderByDescending(c => c.At.Y).First().At;
            job = QuarryHeadroomJob(p, live, z, q, at);
        }
        else
        {
            var (at, clear) = q.StepFix.First(f => q.Top - f.At.Y == layer);
            if (!Open("b:" + at, p.Name, at)) return null;
            bool lava = World is { } world && IsSourceAt(world, at, lava: true);
            P3? scaffold = !lava && !clear && !HasSupport(at) ? QuarryScaffoldSpot(z, at) : null;
            if (!lava && !clear && !HasSupport(at) && scaffold == null)
            {
                Blocked($"quarry {ZoneName(z)}: stair step {at} needs a supported scaffold");
                return null;
            }
            job = StepBlockJob(p, live, z, at, clear, live.Block);
            if (job != null && scaffold is { } spot)
            {
                if (!QuarryScaffoldSupplies(job, live))
                {
                    Blocked($"quarry {ZoneName(z)}: needs two blocks for stair step {at} and its temporary support");
                    return null;
                }
                job.Json["scaffold"] = new JsonObject { [at.ToString()] = spot.ToString() };
            }
        }
        // Repair from the last intact step, never ask a pawn to traverse the gap/obstruction it is assigned to fix.
        if (job != null) job.Json["quarry_access_y"] = q.Top + 1 - Math.Max(0, layer - 1);
        return job;
    }

    /// <summary>An open side support remains exposed after the permanent step is placed. Prefer the outside wall, independent
    /// of stair direction, and avoid supports hidden under either the preceding step or the new step.</summary>
    private P3? QuarryScaffoldSpot(ZoneState z, P3 at)
    {
        if (World is not { } w) return null;
        var built = BuildCellSet();
        var sides = new[] { at.Add(1, 0, 0), at.Add(-1, 0, 0), at.Add(0, 0, 1), at.Add(0, 0, -1) };
        foreach (var n in sides.OrderBy(n => z.Contains(n.X, n.Z)))
        {
            if (w.Loaded(n.X, n.Z) && w.Block(n.X, n.Y, n.Z).Id == 0 && !built.Contains(n)
                && StoryWander.Passable(w.Block(n.X, n.Y + 1, n.Z).Id)
                && StoryWander.Passable(w.Block(n.X, n.Y + 2, n.Z).Id)
                && !Occupied(n) && !OnALid(w, n) && HasSupport(n)) return n;
        }
        return null;
    }

    private ActiveJob? QuarryHeadroomJob(PawnState p, PawnLive live, ZoneState z, QuarryScan q, P3 at)
    {
        if (World is not { } w || !QuarryCellOpen(p, w, at, StoodOnByOthers(p.Name)) || Occupied(at)) return null;
        int id = w.Block(at.X, at.Y, at.Z).Id;
        if (id is StoryQuarry.Bedrock or Spawner || LidBlocks.Contains(id) || BuildCellSet().Contains(at)
            || StoryBlocks.Liquids.Contains(id) || LiquidBeside(w, at) != null || _quarryDanger.Contains(at) || UnderFalling(w, q, at))
        {
            Blocked($"quarry {ZoneName(z)}: stair headroom at {at} is protected or unsafe to clear");
            return null;
        }
        var tool = PlanTool(p, live, id);
        if (tool is { Wait: not null }) return null;
        return WithTool(tool, Job(WorkTypes.Mine, "dig", $"clearing the quarry stair (y {at.Y})", at, "b:" + at, "zone", z.Id));
    }

    /// <summary>The SDK reserves a cheap block for the scaffold before placing the permanent step. Different dirt/cobble stacks
    /// can supply the two placements; one carried block alone must not start a scaffold it cannot finish.</summary>
    private bool QuarryScaffoldSupplies(ActiveJob job, PawnLive live)
    {
        bool SelectStepBlock()
        {
            var stock = live.Inventory.Concat(job.Takes.Select(t => t.Stack)).Where(s => PillarAny.Contains(s.Id))
                .GroupBy(s => (s.Id, s.Meta)).Select(g => new Stack(g.Key.Id, g.Key.Meta, g.Sum(s => s.Count))).ToList();
            var cheap = stock.FirstOrDefault(s => s.Id == 4 && s.Count > 0) ?? stock.FirstOrDefault(s => s.Id == 3 && s.Count > 0);
            var finish = cheap != null ? stock.FirstOrDefault(s => s.Count > (s.Id == cheap.Id && s.Meta == cheap.Meta ? 1 : 0))
                : stock.FirstOrDefault(s => s.Count >= 2);
            if (finish == null) return false;
            job.Json["block"] = ItemName(finish.Id, finish.Meta);
            return true;
        }
        if (SelectStepBlock()) return true;
        var fetch = job.Json["fetch"] as JsonArray ?? new JsonArray();
        foreach (var store in Storages.Where(s => ChestOpen(s.Chest)).OrderBy(s => s.Chest.Dist(live.Block)))
            foreach (var stack in store.Contents.Where(s => PillarAny.Contains(s.Id)))
            {
                int planned = job.Takes.Where(t => t.Chest == store.Chest && t.Stack.Id == stack.Id && t.Stack.Meta == stack.Meta).Sum(t => t.Stack.Count);
                int n = Math.Min(2, stack.Count - planned);
                if (n <= 0) continue;
                foreach (var entry in Fetch(store.Chest, stack, n)) fetch.Add(entry?.DeepClone());
                job.Json["fetch"] = fetch;
                job.Takes.Add((store.Chest, stack with { Count = n }));
                if (SelectStepBlock()) return true;
            }
        return false;
    }
}
