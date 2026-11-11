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
        if (BlockedHeadroom(q) is { } blocked) job = QuarryHeadroomJob(p, live, z, q, blocked);
        else
        {
            var (at, clear) = q.StepFix.First(f => q.Top - f.At.Y == layer);
            if (RepairFailedTwice(at)) StairHeldAlert(z, $"stair broken at y {at.Y}: {StepWait(z, q)}");
            if (!Open("b:" + at, p.Name, at)) return null;
            bool lava = World is { } world && IsSourceAt(world, at, lava: true);
            P3? scaffold = null;
            bool overStep = false;
            if (!lava && !clear && !HasSupport(at))
            {
                var built = BuildCellSet();
                scaffold = QuarryScaffoldSpot(z, at, built);
                if (scaffold == null && StepHeadScaffold(at, built) is { } head) (scaffold, overStep) = (head, true);
                if (scaffold == null)
                {
                    StepHeld(z, at, $"stair step {at} needs a supported scaffold: no free cell beside or over it");
                    return null;
                }
            }
            _quarryStepHeld.Remove(z.Id);
            // A scaffold over the step is clicked against the preceding step's side and the step against its underside: faces the
            // pawn on that step can't see, but reaches (b1.8.1's server never raytraces a click), so not the stationary reach.
            job = overStep ? QuarryBlockJob(p, live, z, at, false, live.Block, PillarAny, $"building the quarry stair (y {at.Y})", reach: false)
                : StepBlockJob(p, live, z, at, clear, live.Block);
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
        if (job != null) job.Json["quarry_access_y"] = q.Top + 1 - Math.Max(q.FirstStep, layer - 1);
        return job;
    }

    /// <summary>An open side support remains exposed after the permanent step is placed. Prefer the outside wall, independent
    /// of stair direction, and avoid supports hidden under either the preceding step or the new step.</summary>
    private P3? QuarryScaffoldSpot(ZoneState z, P3 at, HashSet<P3> built)
    {
        if (World is not { } w) return null;
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

    /// <summary>
    /// A missing step with nothing to click beside or under it and no open side to put a scaffold in (live 11-11: the wall outside
    /// it gone, the cells beside it open over the pit, the one under it dug; every idle pawn said "stair step … needs a supported
    /// scaffold" for good, and the quarry never went on). Its own head room, the cell over it, is beside the preceding step, the one
    /// the repair is done from: the scaffold goes there (against that step's side) and the step under it (against its underside),
    /// both within reach of a pawn on that step, and the scaffold is dug back from there (its top is in view). Unlike a column stood
    /// up from the pit floor under it, which no pawn on the stair reaches (the floor's top 5 and more below its eyes, and the pit a
    /// drop too deep to walk into while the stair is broken). A scaffold left behind is the head room's to clear: the stair's own look
    /// digs it. Null: that cell is not free (a body in it, a build's, lava) or has nothing to click (no step before it).
    /// </summary>
    private P3? StepHeadScaffold(P3 at, HashSet<P3> built)
    {
        if (World is not { } w || !w.Loaded(at.X, at.Z)) return null;
        var head = at.Add(0, 1, 0);
        int id = w.Block(head.X, head.Y, head.Z).Id;
        if (!PlaceInto(id) || StoryQuarry.Lava(id) || built.Contains(head) || Occupied(head)) return null;
        return HasSupport(head) ? head : null;
    }

    /// <summary>Per quarry: its top-most missing step that no scaffold can be put in for, and why, for its status
    /// (<see cref="StepWait"/>). Forgotten when that step gets a job.</summary>
    private readonly Dictionary<string, (P3 Step, string Why)> _quarryStepHeld = new(StringComparer.Ordinal);

    /// <summary>A missing step nothing can be done for now: said to the pawn that asked, on the quarry's status, and (unless only a body
    /// stands in the way) as the stair held up.</summary>
    private void StepHeld(ZoneState z, P3 at, string why)
    {
        Blocked($"quarry {ZoneName(z)}: {why}");
        _quarryStepHeld[z.Id] = (at, why);
        if (!Occupied(at.Add(0, 1, 0))) StairHeldAlert(z, why);
    }

    /// <summary>What <see cref="StepHeld"/> said for this step, or null.</summary>
    private string? StepHeldWhy(ZoneState z, P3 step) =>
        _quarryStepHeld.TryGetValue(z.Id, out var held) && held.Step == step ? held.Why : null;

    // ------------------------------------------------------------------ the stair's head room is never left blocked

    /// <summary>What is done first to clear a cell of the stair's head room.</summary>
    internal enum HeadDo { Dig, Plug, Collect, Held }

    /// <summary>How a cell of the stair's head room gets clear: what is done, at which cell, and the status' words for it
    /// (<see cref="HeadDo.Held"/>: nothing is, and the words say what stands there).</summary>
    internal readonly record struct HeadFix(HeadDo Do, P3 At, string Words);

    private static readonly (int Dx, int Dy, int Dz)[] SidesAndOver = [(1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0)];

    /// <summary>The liquid on a side of or over the cell: it flows in once the cell is dug. The one under it never does.</summary>
    private static P3? LiquidInto(IStoryWorld w, P3 at)
    {
        foreach (var (dx, dy, dz) in SidesAndOver)
            if (StoryBlocks.Liquids.Contains(w.Block(at.X + dx, at.Y + dy, at.Z + dz).Id)) return at.Add(dx, dy, dz);
        return null;
    }

    /// <summary>
    /// A block of the stair's head room, on its own layer's look. Left standing like any other cell beside a liquid, it blocked the
    /// stair for good once the pit was a step deeper ("stair blocked at …: clearing its headroom", with nothing cleared and nothing
    /// else offered). So: liquid on a side of or over it gets a block first (<see cref="QuarryScan.Plugs"/>), then it is dug; liquid
    /// right under it doesn't keep it (liquid never flows up: the step or cell under it then gets its block or bucket from above);
    /// a cell a pawn met danger at is dug all the same. A lava source in the quarry's own columns is collected, never plugged:
    /// with no open side to collect it by, the block stands and the stair says so when it gets there. Sand or gravel standing over
    /// it is taken from its top by the stair repair (<see cref="HeadroomFix"/>).
    /// </summary>
    private QuarryCell HeadroomCell(IStoryWorld w, QuarryScan q, P3 at)
    {
        if (LiquidInto(w, at) is not { } l) return UnderFalling(w, q, at) ? Standing(q, at, "sand", at.Add(0, 1, 0)) : QuarryCell.Dig;
        string kind = LiquidKind(w.Block(l.X, l.Y, l.Z).Id);
        if (!KeptLava(w, l)) q.Plugs.Add(l);
        else if (!LiquidExposed(w, l)) return Standing(q, at, kind, l);
        return NoteHazard(q, at, kind, l, seal: l);
    }

    /// <summary>The head-room cell the stair repair takes first (the top-most blocked step's highest), or null: none is blocked, or a
    /// missing step above it comes first.</summary>
    private static P3? BlockedHeadroom(QuarryScan q)
    {
        if (q.StairClear.Count == 0) return null;
        int layer = int.MaxValue;
        foreach (var c in q.StairClear) layer = Math.Min(layer, c.Layer);
        foreach (var f in q.StepFix) if (q.Top - f.At.Y < layer) return null;
        P3? top = null;
        foreach (var c in q.StairClear) if (c.Layer == layer && (top is not { } t || c.At.Y > t.Y)) top = c.At;
        return top;
    }

    /// <summary>
    /// What clears a cell in the head room of an excavated step, the first thing first: a liquid in it, on a side of it or over it
    /// (<see cref="LiquidFix"/>), then sand or gravel standing over it (it would fall into the stair: taken from its top, and what
    /// liquid touches it gets its block first), then the cell itself. What is not the quarry's to clear holds the stair, in words.
    /// The job, the status and the alert all read this one answer (kept on the look: <see cref="HeadFixOf"/>).
    /// </summary>
    private HeadFix HeadroomFix(IStoryWorld w, QuarryScan q, P3 at)
    {
        if (StoryBlocks.Liquids.Contains(w.Block(at.X, at.Y, at.Z).Id)) return LiquidFix(w, at, at);
        if (HeadroomKept(w, at) is { } kept) return new(HeadDo.Held, at, kept);
        if (LiquidInto(w, at) is { } l) return LiquidFix(w, at, l);
        var top = at;
        while (top.Y < w.Height - 1 && UnderFalling(w, q, top))
        {
            top = top.Add(0, 1, 0);
            if (LiquidInto(w, top) is { } wet) return LiquidFix(w, at, wet);
        }
        if (top == at) return new(HeadDo.Dig, at, "clearing its headroom");
        return HeadroomKept(w, top) is { } built ? new(HeadDo.Held, top, built)
            : new(HeadDo.Dig, top, "clearing the sand or gravel over it from the top");
    }

    /// <summary>A liquid in, beside or over the stair's head room gets a block, placed without <c>reach</c> (no pawn sees a face of a
    /// cell behind the wall). A lava source in a quarry's own columns gets its bucket instead; with no open side to fill one by,
    /// nothing: it is never plugged, and the stair waits.</summary>
    private HeadFix LiquidFix(IStoryWorld w, P3 head, P3 l)
    {
        string where = l == head ? "in it" : $"at {l}";
        if (!KeptLava(w, l)) return new(HeadDo.Plug, l, $"sealing the {LiquidKind(w.Block(l.X, l.Y, l.Z).Id)} {where} first");
        return LiquidExposed(w, l) ? new(HeadDo.Collect, l, $"a lava source {where}")
            : new(HeadDo.Held, l, $"the lava source {where} can't be collected (no open side) and is never plugged");
    }

    /// <summary>What the quarry never digs out of its stair's head room, in the status' words: bedrock, a spawner, a container or
    /// workstation, a build's block. Null: it is the quarry's to clear.</summary>
    private string? HeadroomKept(IStoryWorld w, P3 at)
    {
        int id = w.Block(at.X, at.Y, at.Z).Id;
        if (id == StoryQuarry.Bedrock) return "bedrock in its head room";
        if (id == Spawner) return "a monster spawner in its head room (break it, or move the quarry)";
        if (LidBlocks.Contains(id)) return $"{BlockIdName(w, id).Replace('_', ' ')} in its head room (move it)";
        if (_quarryRefused.ContainsKey(at)) return "a block no pawn may break in its head room (protected)";
        if (KeptTemp(at)) return "a temporary block whose removal was cancelled in its head room (remove it)";
        return BuildCellSet().Contains(at) ? "a build's block in its head room (move the build or the quarry)" : null;
    }

    /// <summary><see cref="HeadroomFix"/> for the look's blocked cell, worked out once per look: the status asks with every state
    /// delta, and the answer reads every build's cells.</summary>
    private HeadFix HeadFixOf(IStoryWorld w, QuarryScan q, P3 head)
    {
        if (q.Head is { } known && known.Cell == head) return known.Fix;
        var fix = HeadroomFix(w, q, head);
        q.Head = (head, fix);
        return fix;
    }

    /// <summary>What the stair's blocked head-room cell waits for, for the status and the idle audit.</summary>
    private string HeadroomWait(ZoneState z, QuarryScan q, P3 head)
    {
        if (World is not { } w) return "clearing its headroom";
        var fix = HeadFixOf(w, q, head);
        if (fix.Do == HeadDo.Held) return fix.Words;
        if (fix.Do == HeadDo.Collect && LavaHeld(z, fix.At) is { } hold) return $"{fix.Words}: {hold}";
        if (fix.Do == HeadDo.Dig && _quarryHeadTool.TryGetValue(z.Id, out var tool) && tool.Cell == fix.At)
            return $"{fix.Words}: waiting for a tool ({tool.Need})";
        return (fix.Do == HeadDo.Collect ? "collecting " : "") + fix.Words + RepairRest(fix.At);
    }

    /// <summary>Per quarry: the stair's head-room cell the last pawn to ask had no tool for, and the tool ("needs an iron pickaxe — …").
    /// The status said "clearing its headroom" with nobody clearing it. Forgotten when a pawn gets that dig.</summary>
    private readonly Dictionary<string, (P3 Cell, string Need)> _quarryHeadTool = new(StringComparer.Ordinal);

    /// <summary>": resting after failures (why)" while a stair repair's cell rests, as a resting quarry cell is shown; else "".</summary>
    private string RepairRest(P3 cell)
    {
        if (!QuarryResting(cell)) return "";
        string why = _quarryMiss.GetValueOrDefault("b:" + cell)?.Replace(" at " + cell, "", StringComparison.Ordinal) ?? "";
        return ": resting after failures" + (why.Length > 0 ? $" ({why})" : "");
    }

    /// <summary>A stair repair the pawns failed at twice (a click the server refuses) and that rests now.</summary>
    private bool RepairFailedTwice(P3 cell) => Skipped("b:" + cell) && _leaseFails.GetValueOrDefault("b:" + cell) >= 2;

    /// <summary>The stair can't get through by itself (what stands in its head room is not the quarry's to clear, or its repair failed
    /// twice): said at most every 10 minutes per quarry. The status and the idle audit carry the same words.</summary>
    private void StairHeldAlert(ZoneState z, string why)
    {
        if (_quarryAlert.TryGetValue("held:" + z.Id, out var t) && Now - t < TimeSpan.FromMinutes(10)) return;
        _quarryAlert["held:" + z.Id] = Now;
        Alert("warn", $"Quarry {ZoneName(z)} is held up: {why}. Nothing more is dug there until its stair is clear");
    }

    /// <summary>The next job that clears the stair's blocked head-room cell: a block into the liquid behind or in it, a bucket for a
    /// lava source of the quarry's own, a dig of what stands over it or of the cell itself. Nothing while what stands there is not
    /// the quarry's to clear: that is said (<see cref="StairHeldAlert"/>).</summary>
    private ActiveJob? QuarryHeadroomJob(PawnState p, PawnLive live, ZoneState z, QuarryScan q, P3 at)
    {
        if (World is not { } w) return null;
        var fix = HeadFixOf(w, q, at);
        if (fix.Do == HeadDo.Held || RepairFailedTwice(fix.At)) StairHeldAlert(z, $"stair blocked at {at}: {HeadroomWait(z, q, at)}");
        if (fix.Do == HeadDo.Held || Occupied(fix.At)) return null;
        int id = w.Block(fix.At.X, fix.At.Y, fix.At.Z).Id;
        if (fix.Do == HeadDo.Dig)
        {
            if (!QuarryCellOpen(p, w, fix.At, StoodOnByOthers(p.Name))) return null;
            var tool = PlanTool(p, live, id);
            if (tool is { Wait: { } need }) { _quarryHeadTool[z.Id] = (fix.At, WaitText(need)!); return null; }
            _quarryHeadTool.Remove(z.Id);
            return WithTool(tool, Job(WorkTypes.Mine, "dig", $"clearing the quarry stair (y {fix.At.Y})", fix.At, "b:" + fix.At, "zone", z.Id));
        }
        if (!StoryBlocks.Liquids.Contains(id) || !Open("b:" + fix.At, p.Name, fix.At)) return null;
        return fix.Do == HeadDo.Collect ? QuarryLavaJob(p, live, z, fix.At)
            : QuarryBlockJob(p, live, z, fix.At, false, live.Block, SealAny, $"sealing the quarry ({LiquidKind(id)}, y {fix.At.Y})", reach: false);
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
