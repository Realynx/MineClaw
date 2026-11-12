using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Taking a pillar of the pawns' temporary blocks down from its top (docs/web/story-mode.md "Temporary blocks"). Fox 11-11: "I see some
/// left over pillars it made … one of them is not even marked for destruction." Live 11-11: 63 escape pillars (pawns climbing out of
/// the quarry and a strip mine) stood with their cleanup given up "out of reach": each cell was a mark dug from beside, and below the
/// rim the pit floor had no walkable way down, so no pawn could stand beside the lower cells, and none could pillar up beside them.
/// <list type="bullet">
/// <item>A vertical run of marked temporary blocks whose top has room over it and a walkable cell beside that (the rim it was climbed
/// to), or a rim up to 3 over the top's stand to drop in from (the stub a per-cell cleanup left), is one job: the pawn walks or drops
/// onto the top (<c>stand</c>) and digs down beneath itself a block at a time (<c>unpole</c>: the cells top first, and <c>back</c>,
/// the cell it walks back out to). The top's mark leads the job; the others are leased with it, and rest with it after a miss. Its
/// own builder may take it: standing over its column for this job is not "temporary access still in use".</item>
/// <item>A pillar whose top nobody can walk or drop onto (live 11-12: escape pillars 18 to 39 blocks tall on open ground, 208 cleanups
/// "out of reach" in 3 h) comes down the owner's way (Fox: "pillar up and then delete 2 blocks in each level as you come down"): from
/// a ground cell next to it (<c>unpole.beside</c>, the job's <c>stand</c>) the pawn poles its own helper column up beside the pillar
/// until its top is in reach, then comes down taking the pillar's block and the helper block under its feet at each level. Both
/// columns end gone. Only a pillar with no ground next to it to start from is out of reach; its marks then rest and say so.</item>
/// <item>Down on the floor with no way back (a pit, a cave), the pawn asks for the rescue teleport home (the owner's rule: it removes
/// what it can and teleports rather than leave the blocks). Only offered with <see cref="ColonyMeta.RescueTeleport"/> on: without
/// it the pawn would pillar out again.</item>
/// <item>Every cell a cleanup digs that the registry has from its pawn's own report is taught to the cleaner's placed-block log first,
/// in memory only (<see cref="IStoryHost.OwnTemporaryBlock"/>): the hive's shared log keeps only the newest 8,192 placements, and
/// without the row the step-down rule refuses the block under the cleaner's feet and soft protection may refuse the dig.</item>
/// </list>
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>A pillar to take down: the mark that leads the job, the top cell, where the pawn stands to start (over the top, or the
    /// ground cell beside the pillar when it builds up alongside), the cell beside the top to walk back out to (from the top only), the
    /// cells top first, and the other cells' lease keys.</summary>
    private sealed record TempPillar(DesignationState Mark, P3 Top, P3 Stand, P3? Back, List<P3> Cells, List<string> MoreKeys)
    {
        /// <summary>Built up alongside from the ground (<see cref="Stand"/> is that ground cell), not walked onto from its top.</summary>
        public bool Alongside => Back == null;

        /// <summary>Where the job is sent: the top (from its top), else the pillar's lowest cell (the walk there ends on the ground beside it).</summary>
        public P3 Target => Alongside ? Cells[^1] : Top;

        /// <summary>How many blocks the pawn builds up beside it at most (its pillar hint).</summary>
        public int Climb => Alongside ? Math.Max(0, Top.Y - Stand.Y) : 0;

        public JsonObject Json()
        {
            var o = new JsonObject();
            if (Back is { } back) o["back"] = back.ToString();
            else o["beside"] = Stand.ToString();
            o["cells"] = new JsonArray(Cells.Select(c => (JsonNode)c.ToString()).ToArray());
            return o;
        }
    }

    /// <summary>The pillar <paramref name="d"/>'s block is part of, when this pawn can take it down as one job: from its top (rescue
    /// teleport on, its top walkable or dropped onto), else built up alongside from the ground beside it; else null (the block is dug
    /// from beside, as before: a lone block, or a pillar with no ground next to it).</summary>
    private TempPillar? TempPillarFor(PawnState p, IStoryWorld w, DesignationState d)
    {
        if (d.At is not { } at || Meta.TempBlocks is not { Count: > 1 } temps) return null;
        var column = new Dictionary<P3, TempBlockState>();
        foreach (var t in temps)
            if (t.At.X == at.X && t.At.Z == at.Z && t.Designation != null && !t.Cancelled && t.DeferredReason == null && t.Id != 0) column[t.At] = t;
        bool Part(P3 c) => column.TryGetValue(c, out var t) && SameTempBlock(t.Id, w.Block(c.X, c.Y, c.Z).Id);
        if (!Part(at)) return null;
        int top = at.Y, bottom = at.Y;
        while (Part(new P3(at.X, top + 1, at.Z))) top++;
        while (Part(new P3(at.X, bottom - 1, at.Z))) bottom--;
        if (top == bottom) return null; // a lone block: dug from beside
        var topCell = new P3(at.X, top, at.Z);
        var stand = topCell.Add(0, 1, 0);
        if (StoodOnByOthers(p.Name).Contains(topCell)) return null; // somebody up there: never dug out from under them
        P3? back = Meta.RescueTeleport && OpenAt(w, stand) && OpenAt(w, stand.Add(0, 1, 0)) ? BackFrom(w, stand) ?? DropInFrom(w, stand) : null;
        if (back == null)
        {
            if (AlongsideFrom(w, at.X, at.Z, bottom, top) is not { } ground) return null;
            stand = ground;
        }
        // The top's own mark leads the job (not resting, nobody at it); every other cell's mark goes with it.
        var lead = column[topCell];
        if (lead.RetryAfter > Now || Designations.FirstOrDefault(x => x.Id == lead.Designation) is not { } mark || !Open(DesignationKey(mark), p.Name, null))
            return null;
        var cells = Enumerable.Range(bottom, top - bottom + 1).Reverse().Select(y => new P3(at.X, y, at.Z)).ToList();
        var more = new List<string>();
        foreach (var c in cells.Skip(1))
        {
            if (Designations.FirstOrDefault(x => x.Id == column[c].Designation) is not { } m) continue;
            var key = DesignationKey(m);
            if (!Free(key, p.Name)) return null; // another pawn is at part of it already
            more.Add(key);
        }
        return new TempPillar(mark, topCell, stand, back, cells, more);
    }

    /// <summary>How far under the pillar's base the ground beside it may be (the helper column then starts lower: a pillar whose
    /// lowest blocks are already gone floats over the ground, the old per-cell cleanup dug those from the ground and left the rest up
    /// there), and over it (the pillar's lowest cells are then dug from that ground, below the feet).</summary>
    private const int AlongsideBelow = 48, AlongsideAbove = 2;

    /// <summary>
    /// The ground cell next to the pillar column x,z (bottom..top) a pawn builds its helper column up from: a floor that isn't liquid
    /// under two open cells, and the column over it open (no liquid) up to a head over the pillar's top. Nearest the pillar's base
    /// wins, a cell at or under it before one over it. None: no ground beside it to start from, the pillar is out of reach.
    /// </summary>
    private static P3? AlongsideFrom(IStoryWorld w, int x, int z, int bottom, int top)
    {
        P3? best = null;
        int bestCost = int.MaxValue;
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            int cx = x + dx, cz = z + dz;
            if (!w.Loaded(cx, cz)) continue;
            for (int y = bottom + AlongsideAbove; y >= Math.Max(1, bottom - AlongsideBelow); y--)
            {
                if (!StoryWander.Standable(w, cx, y, cz))
                {
                    if (!StoryWander.Passable(w.Block(cx, y, cz).Id)) break; // solid: ground under it has no open column up
                    continue;
                }
                int cost = y <= bottom ? bottom - y : 2 * (y - bottom) + 1;
                if (cost < bestCost && ShaftOpen(w, cx, cz, y, top + 1)) { best = new P3(cx, y, cz); bestCost = cost; }
                break; // the highest ground in this column within the range: lower cells are under it
            }
        }
        return best;
    }

    /// <summary>Open (walk-through, no liquid) from <paramref name="from"/> up to <paramref name="to"/>, with no lava beside it.</summary>
    private static bool ShaftOpen(IStoryWorld w, int x, int z, int from, int to)
    {
        for (int y = from; y <= Math.Min(to, w.Height - 1); y++)
        {
            if (!StoryWander.Passable(w.Block(x, y, z).Id)) return false;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (w.Block(x + dx, y, z + dz).Id is 10 or 11) return false;
        }
        return true;
    }

    private static bool OpenAt(IStoryWorld w, P3 c) => OpenCell(w.Block(c.X, c.Y, c.Z).Id);

    /// <summary>A cell beside the top's stand a pawn walks on and off it from (level with it, a step up or a step down): floor under two
    /// open cells, never liquid. None: the top is out of walking reach, so the pillar is dug from beside.</summary>
    private static P3? BackFrom(IStoryWorld w, P3 stand)
    {
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            foreach (int dy in new[] { 0, 1, -1 })
            {
                var c = stand.Add(dx, dy, dz);
                int floor = w.Block(c.X, c.Y - 1, c.Z).Id;
                if (OpenCell(floor) || StoryBlocks.Liquids.Contains(floor)) continue;
                if (!OpenAt(w, c) || !OpenAt(w, c.Add(0, 1, 0))) continue;
                if (dy > 0 && !OpenAt(w, stand.Add(0, 2, 0))) continue; // a step up needs head room over the stand
                return c;
            }
        return null;
    }

    /// <summary>The pathfinder's largest voluntary drop (PathOptions.MaxDrop's default).</summary>
    private const int PillarDropIn = 3;

    /// <summary>
    /// A cell beside the column, 2 to <see cref="PillarDropIn"/> blocks over the top's stand, a pawn steps off to drop onto the top:
    /// floor under two open cells, and the column open from there down to the stand. The stub the old per-cell cleanup left in a pit
    /// (its upper cells dug from the rim, the rest out of reach, live 11-11) has its top 3 under the rim's floor, below any step.
    /// There is no walking back up: the job's <c>back</c> is this rim cell, and a pawn that can't walk to it from the bottom is
    /// teleported home (the stranded call). None: dug from beside.
    /// </summary>
    private static P3? DropInFrom(IStoryWorld w, P3 stand)
    {
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            for (int dy = 2; dy <= PillarDropIn; dy++)
            {
                var c = stand.Add(dx, dy, dz);
                int floor = w.Block(c.X, c.Y - 1, c.Z).Id;
                if (OpenCell(floor) || StoryBlocks.Liquids.Contains(floor)) continue;
                if (!OpenAt(w, c) || !OpenAt(w, c.Add(0, 1, 0))) continue;
                bool clear = true;
                for (int y = 2; y <= dy + 1 && clear; y++) clear = OpenAt(w, stand.Add(0, y, 0)); // the fall, and the step over it
                if (clear) return c;
            }
        return null;
    }

    /// <summary>
    /// The cleaner's body learns each block it is sent to take back as the hive's own (<see cref="IStoryHost.OwnTemporaryBlock"/>), while
    /// the colony's view still shows the block its registry has there: placed by a pawn, so always minable (a block somebody changed
    /// is <see cref="Gone"/> and forgotten, never taught). Only a block the registry has from its pawn's own report (the SDK's
    /// placement, the body's ledger, the hive's placed-block log): one it adopted from the view (<see cref="TempBlockState.Adopted"/>)
    /// may be anybody's, and one refused as protected stays parked (<see cref="ReopenParkedTemps"/>), so the owner's block is never
    /// taught. The body keeps the lesson in memory only (never saved to the hive's shared table).
    /// </summary>
    private void OwnTempBlocks(string pawn, IStoryWorld w, IEnumerable<P3> cells)
    {
        if (Meta.TempBlocks is not { Count: > 0 } temps) return;
        foreach (var c in cells)
        {
            int now = w.Loaded(c.X, c.Z) ? w.Block(c.X, c.Y, c.Z).Id : 0;
            if (now == 0 || temps.FirstOrDefault(t => t.At == c && t.Id != 0) is not { } t || t.Adopted || t.DeferredReason != null
                || !SameTempBlock(t.Id, now)) continue;
            try { Host.OwnTemporaryBlock(pawn, c, now, t.Pawn); } catch (Exception) { /* the dig says why if its body still refuses */ }
        }
    }
}
