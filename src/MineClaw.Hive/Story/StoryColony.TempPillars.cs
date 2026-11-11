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
    /// <summary>A pillar to take down from its top: the mark that leads the job, the top cell, the cell over it, the cell beside that to
    /// walk back out to, the cells top first, and the other cells' lease keys.</summary>
    private sealed record TempPillar(DesignationState Mark, P3 Top, P3 Stand, P3 Back, List<P3> Cells, List<string> MoreKeys)
    {
        public JsonObject Json() => new()
        {
            ["back"] = Back.ToString(),
            ["cells"] = new JsonArray(Cells.Select(c => (JsonNode)c.ToString()).ToArray()),
        };
    }

    /// <summary>The pillar <paramref name="d"/>'s block is part of, when it can be taken down from its top by this pawn; else null (the
    /// block is dug from beside, as before).</summary>
    private TempPillar? TempPillarFor(PawnState p, IStoryWorld w, DesignationState d)
    {
        if (!Meta.RescueTeleport || d.At is not { } at || Meta.TempBlocks is not { Count: > 1 } temps) return null;
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
        if (!OpenAt(w, stand) || !OpenAt(w, stand.Add(0, 1, 0)) || StoodOnByOthers(p.Name).Contains(topCell)) return null;
        if ((BackFrom(w, stand) ?? DropInFrom(w, stand)) is not { } back) return null;
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
