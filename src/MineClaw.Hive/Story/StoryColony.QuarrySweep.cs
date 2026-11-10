using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// What a quarry leaves standing, said and swept (docs/web/story-mode.md "Quarry hazards", "Temporary blocks"). Each full look from the
/// top (every 30 s, and once more when a look from the working layer finds it done) classes every cell still standing from the top down
/// to the working layer: its stair's steps, cells left standing beside lava or water, under sand or gravel, as a cave's wall, where a
/// pawn met danger, no pawn may break (protected) or the owner kept, walled in, and liquid closed in by rock (a pool). The state's
/// <c>left</c> (by kind), <c>leftovers</c> (up to 64 of those cells, no steps), <c>sealed</c> and the status's summary come from that
/// record, never from what this process happened to see (the summary counted every hazard ever met, plugs long since dug too). The
/// owner's rule: natural seals, capped pools and kept cells stay, and are listed. A quarry that becomes done says so once, and the
/// pawns' blocks in or beside it that are none of its own (its steps, the seals its place jobs put there: <see cref="ZoneState.QuarryPlaced"/>)
/// are swept like any stray (StoryColony.TempStrays.cs).
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>At most this many leftover cells go into the state.</summary>
    public const int LeftoversMax = 64;

    /// <summary>At most this many placed cells are kept per quarry.</summary>
    private const int QuarryPlacedMax = 4096;

    /// <summary>A full look's record: count by kind (steps too), the first cells that aren't steps, how many those are, the top-most
    /// cave wall's y (0 = none).</summary>
    private sealed record QuarryLeft(SortedDictionary<string, int> Counts, List<(P3 At, string Kind)> Cells, int Sealed, int CaveY);

    private readonly Dictionary<string, QuarryLeft> _quarryLeft = new(StringComparer.Ordinal);

    /// <summary>A cell of a full look that stays: what the scan left standing (a pool when the cell is the liquid itself), or a step.</summary>
    private static void NoteLeft(IStoryWorld w, QuarryScan q, P3 at, P3? step, QuarryCell kind)
    {
        if (kind == QuarryCell.Hazard)
            q.Left!.Add((at, StoryBlocks.Liquids.Contains(w.Block(at.X, at.Y, at.Z).Id) ? "pool" : q.Found[^1].Kind));
        else if (at == step && at.Y >= q.Bottoms[(at.X, at.Z)] && Ground(w.Block(at.X, at.Y, at.Z).Id))
            q.Left!.Add((at, "step"));
    }

    /// <summary>After a full look: the record kept for the state and status, placed cells no longer standing forgotten, and a quarry that
    /// just became done said once and swept.</summary>
    private void QuarryLeftLook(IStoryWorld w, ZoneState z, QuarryScan q, bool becameDone)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int cave = 0, sealedCount = 0;
        var cells = new List<(P3 At, string Kind)>();
        foreach (var (at, kind) in q.Left!) // top-most first: the look goes down
        {
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            if (kind == "step") continue;
            sealedCount++;
            if (cells.Count < LeftoversMax) cells.Add((at, kind));
            if (kind == "cave") cave = Math.Max(cave, at.Y);
        }
        _quarryLeft[z.Id] = new QuarryLeft(counts, cells, sealedCount, cave);
        PruneQuarryPlaced(w, z);
        if (!becameDone) return;
        Alert("info", $"Quarry {ZoneName(z)} is done: dug down to bedrock"
            + (sealedCount > 0 ? $"; {sealedCount} cell(s) left standing apart from its stair (listed in the Quarries window)" : ""));
        _quarryDoneSweep.Add(z.Id); // at the next tick: a look may run inside a loop over the designations the sweep adds to
    }

    /// <summary>Quarries that became done since the last tick, to sweep for strays.</summary>
    private readonly HashSet<string> _quarryDoneSweep = new(StringComparer.Ordinal);

    /// <summary>The colony tick (StoryColony.TempBlocks.cs TempTick): the stray sweep of each quarry that became done.</summary>
    private void SweepDoneQuarries(IStoryWorld w)
    {
        if (_quarryDoneSweep.Count == 0) return;
        foreach (var id in _quarryDoneSweep.ToList())
            if (Zones.FirstOrDefault(z => z.Id == id) is { } z && IsQuarry(z)) SweepQuarryStrays(w, z);
        _quarryDoneSweep.Clear();
    }

    /// <summary>"6 water, 1 cave": what stays apart from the stair, most first.</summary>
    private static string LeftWords(QuarryLeft left) =>
        string.Join(", ", left.Counts.Where(c => c.Key != "step").OrderByDescending(c => c.Value).Select(c => $"{c.Value} {c.Key}"));

    /// <summary>The state's <c>left</c>: count by kind.</summary>
    private static JsonObject LeftJson(QuarryLeft? left)
    {
        var o = new JsonObject();
        if (left != null) foreach (var (kind, n) in left.Counts) o[kind] = n;
        return o;
    }

    /// <summary>The state's <c>leftovers</c>: <c>{at, kind}</c>, top-most first.</summary>
    private static JsonArray LeftoversJson(QuarryLeft? left)
    {
        var a = new JsonArray();
        if (left != null) foreach (var (at, kind) in left.Cells) a.Add(new JsonObject { ["at"] = at.Json(), ["kind"] = kind });
        return a;
    }

    /// <summary>A finished quarry <c>place</c> job's cell (a seal, a plug) is the quarry's own from now on; a step needs no record.</summary>
    private void QuarryPlaceDone(ActiveJob j)
    {
        if (j.Kind != "place" || j.Source != "zone" || j.At is not { } at || Zones.FirstOrDefault(z => z.Id == j.SourceId) is not { } z || !IsQuarry(z)) return;
        if (QuarryStepAt(z, at, StoryQuarry.Ring(z.Cells))) return;
        z.QuarryPlaced ??= new();
        if (z.QuarryPlaced.Contains(at) || z.QuarryPlaced.Count >= QuarryPlacedMax) return;
        z.QuarryPlaced.Add(at);
        z.Rev = Touch();
    }

    /// <summary>Placed cells the world shows open again (dug once no liquid was beside them, or by a player) are forgotten.</summary>
    private void PruneQuarryPlaced(IStoryWorld w, ZoneState z)
    {
        if (z.QuarryPlaced is not { Count: > 0 } placed) return;
        int gone = placed.RemoveAll(c => w.Loaded(c.X, c.Z) && w.Block(c.X, c.Y, c.Z).Id is var id && OpenCell(id) && !StoryBlocks.Liquids.Contains(id));
        if (gone == 0) return;
        if (placed.Count == 0) z.QuarryPlaced = null;
        z.Rev = Touch();
    }

    /// <summary>A cell of the quarry's stair path (any layer from its top: the leading steps it skips too).</summary>
    private bool QuarryStepAt(ZoneState z, P3 at, List<(int X, int Z)>? ring)
    {
        if (ring == null) return false;
        int k = QuarryTopOf(z) - at.Y;
        return k >= 0 && ring[k % ring.Count] == (at.X, at.Z);
    }

    /// <summary>Every quarry's own cells: its stair's path down to y 1 and its placed cells.</summary>
    private HashSet<P3> QuarryOwnCells()
    {
        var own = new HashSet<P3>();
        foreach (var z in Zones)
        {
            if (!IsQuarry(z)) continue;
            if (z.QuarryPlaced != null) own.UnionWith(z.QuarryPlaced);
            if (StoryQuarry.Ring(z.Cells) is not { } ring) continue;
            int top = QuarryTopOf(z);
            for (int k = 0; top - k >= 1; k++) own.Add(StoryQuarry.Step(ring, top, k));
        }
        return own;
    }

    /// <summary>In or right beside the columns of this quarry.</summary>
    private static bool BesideQuarry(ZoneState z, P3 at) =>
        z.Contains(at.X, at.Z) || z.Contains(at.X + 1, at.Z) || z.Contains(at.X - 1, at.Z) || z.Contains(at.X, at.Z + 1) || z.Contains(at.X, at.Z - 1);

    /// <summary>
    /// Once, before the first stray look of a colony saved before quarries recorded their placed cells: every block a pawn of this
    /// colony placed in or beside a quarry (a scaffold kind, not a registered temporary block) is taken as that quarry's own, so the
    /// upgrade never sweeps the seals already there (until then nothing in or beside a quarry was swept at all).
    /// </summary>
    private void AdoptQuarrySeals(IReadOnlyList<(P3 At, int Id, string Bot)> placed)
    {
        if (Meta.QuarrySealsAdopted) return;
        Meta.QuarrySealsAdopted = true;
        _dirty = true;
        var temps = (Meta.TempBlocks ?? []).Select(t => t.At).ToHashSet();
        var world = World;
        foreach (var z in Zones.Where(IsQuarry))
            foreach (var (at, id, bot) in placed)
            {
                if (!Pawns.ContainsKey(bot) || !TempKinds.Contains(id) || temps.Contains(at) || !BesideQuarry(z, at)) continue;
                if (world != null && world.Loaded(at.X, at.Z) && !SameTempBlock(id, world.Block(at.X, at.Y, at.Z).Id)) continue;
                z.QuarryPlaced ??= new();
                if (z.QuarryPlaced.Count >= QuarryPlacedMax || z.QuarryPlaced.Contains(at)) continue;
                z.QuarryPlaced.Add(at);
                z.Rev = Touch();
            }
    }

    /// <summary>When a quarry becomes done: the pawns' stray blocks in or beside it (none of its own, none holding back a liquid) become
    /// deconstruct designations, said in one alert.</summary>
    private void SweepQuarryStrays(IStoryWorld w, ZoneState z)
    {
        IReadOnlyList<(P3 At, int Id, string Bot)> placed;
        try { placed = Host.PlacedByBots(); } catch (Exception) { return; }
        AdoptQuarrySeals(placed);
        var cand = StrayCandidates(w, placed, at => BesideQuarry(z, at));
        if (cand.Count == 0) return;
        var builds = BuildCellSet(includeReplacementAir: true);
        var found = Clusters(cand.Keys).Where(g => LooksTemporary(g, builds)).SelectMany(g => g).Take(StrayMax).Select(at => cand[at]).ToList();
        foreach (var t in found) { Temps.Add(t); QueueTemp(t, said: true); }
        if (found.Count > 0)
            Alert("info", $"Quarry {ZoneName(z)}: found {found.Count} stray block(s) the pawns left in or beside it (at "
                + $"{string.Join(", ", found.Select(t => t.At.ToString()).Take(5))}{(found.Count > 5 ? ", …" : "")}): queued for removal");
    }
}
