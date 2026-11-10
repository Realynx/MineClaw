using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// What a quarry meets on its way down (docs/web/story-mode.md "Quarry hazards"; Fox 11-05: "the quarry will also need to take into
/// consideration natural caves that it can come into, and lava and other dangerous things"). Each cell of the working layer is judged
/// against the world on every look:
/// <list type="bullet">
/// <item><b>Caves.</b> Cave air in the zone is already dug (no work; its floor and walls are quarry cells as usual). A cave beside the
/// zone (open cells under ground, or over a drop of 20 or more) is walled off: the zone's edge cell beside it is left standing; an edge
/// cell already open (the cave runs through it) gets a block placed. Where the edge cell is the stair's head room (up to 3 over a step),
/// it is dug and the cave's cell beside it gets the block instead, so the stair stays walkable. A missing step over cave air is placed
/// by the stair repair as before. The block for an open edge cell goes into the cave's cell too wherever it can attach there (the cave's
/// own rock beside it), so the pit keeps its cell. A closed pocket is no cave (<see cref="ClosedPocket"/>): an ore hole dug out of the
/// pit's wall, a hollow; it is left open and nothing is said.</item>
/// <item><b>Lava and water.</b> A cell with lava or water on any of its 6 sides (flowing down from above too) is never dug: it is left
/// standing as a seal. Lava sources in the zone are collected into buckets for storage. Water and flowing lava get a block placed
/// in them (cobblestone, stone or dirt: never planks); once no liquid remains beside that plug it is mined like any other cell.
/// Only liquid an open cell touches is such a job: a pocket closed in by rock, the water under a capped pool and lava under its lid
/// are contained (<see cref="LiquidExposed"/>), counted and left.</item>
/// <item><b>The stair's head room</b> (up to 3 over a step) is the exception to all of this: a block of it is never left standing,
/// or the stair ends there. The liquid behind it gets a block first, the block is then dug (StoryColony.QuarryAccess.cs).</item>
/// <item><b>Falling blocks.</b> A cell under sand or gravel left standing (a seal, beside water) is left standing too: nothing falls
/// into the pit onto a pawn.</item>
/// <item><b>Monster spawners</b> in or beside the working layer stop the quarry, with an alert, until the spawner is gone.</item>
/// <item><b>A pawn in danger</b> at a quarry cell (in lava, suffocating) leaves the cell standing as a seal.</item>
/// </list>
/// A seal to place comes before any more digging (like a missing step); one resting after failures doesn't hold the quarry. Each new
/// hazard is alerted once (not per look); the quarry's status and state say what was sealed and why.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>What a seal is placed with: never anything that burns.</summary>
    private static readonly int[] SealAny = [4, 1, 3];

    /// <summary>The spawner block (both versions).</summary>
    private const int Spawner = 52;

    /// <summary>A breached cave counts when the cell beside the zone is over a drop at least this deep (a ravine) or under ground.</summary>
    private const int DeepDrop = 20;

    /// <summary>Per quarry: every hazard cell found (→ its kind), for the status.</summary>
    private readonly Dictionary<string, Dictionary<P3, string>> _quarryHazards = new(StringComparer.Ordinal);

    /// <summary>Per quarry: the hazards alerted (where the lava, water or cave is): a cell near one of the same kind is the same hazard.</summary>
    private readonly Dictionary<string, List<(P3 Source, string Kind)>> _quarryHazardSaid = new(StringComparer.Ordinal);

    /// <summary>Quarry cells a pawn met danger at (in lava, suffocating): left standing from then on.</summary>
    private readonly HashSet<P3> _quarryDanger = new();

    private static string LiquidKind(int id) => StoryQuarry.Lava(id) ? "lava" : "water";

    /// <summary>A hazard found: left standing (no <paramref name="seal"/>), or a block to place at <paramref name="seal"/> first.</summary>
    private static QuarryCell NoteHazard(QuarryScan q, P3 at, string kind, P3 source, P3? seal = null)
    {
        q.Found.Add((at, kind, source));
        if (seal is not { } s) return QuarryCell.Hazard;
        if (!q.SealFix.Any(f => f.At == s)) q.SealFix.Add((s, kind));
        return QuarryCell.Seal;
    }

    /// <summary>An open cell of the layer (dug, or a cave's air): nothing to do, unless a cave opens beside it at the zone's edge.</summary>
    private QuarryCell OpenQuarryCell(IStoryWorld w, ZoneState z, QuarryScan q, P3 at)
    {
        if (CaveOutside(w, z, q, at) is not { } n) return QuarryCell.Skip;
        // Into the cave's cell when a block attaches there: the pit's own cell stays dug (a block in it stood in the pit for good).
        var seal = Headroom(q, at) || Attaches(w, n) ? n : at;
        // An old opening high above the floor may float in air on all six sides. Start where a block can attach;
        // the upper cells become placeable as the wall grows, rather than repeatedly sending miners to an impossible placement.
        bool supported = Attaches(w, seal);
        return NoteHazard(q, at, "cave", n, seal: supported ? seal : null);
    }

    /// <summary>A block placed in the cell has something to attach to: ground on one of its 6 sides.</summary>
    private static bool Attaches(IStoryWorld w, P3 c)
    {
        foreach (var p in Neighbours(c))
            if (Ground(w.Block(p.X, p.Y, p.Z).Id)) return true;
        return false;
    }

    /// <summary>A block of the layer: left standing beside a liquid, under a standing falling block, where a pawn met danger, or as the
    /// wall to a cave; else dug. A block of the stair's head room is never just left: StoryColony.QuarryAccess.cs.</summary>
    private QuarryCell SolidQuarryCell(IStoryWorld w, ZoneState z, QuarryScan q, P3 at)
    {
        // Never the quarry's to break, head room too: there the stair repair names it (StoryColony.QuarryAccess.cs HeadroomKept).
        if (_quarryRefused.Count > 0 && _quarryRefused.ContainsKey(at)) return Standing(q, at, "protected", at);
        if (KeptTemp(at)) return Standing(q, at, "kept", at); // StoryColony.Quarry.cs
        if (Headroom(q, at)) return HeadroomCell(w, q, at);
        if (LiquidBeside(w, at) is { } l) return Standing(q, at, LiquidKind(w.Block(l.X, l.Y, l.Z).Id), l);
        if (UnderFalling(w, q, at)) return Standing(q, at, "sand", at.Add(0, 1, 0));
        if (_quarryDanger.Contains(at)) return Standing(q, at, "danger", at);
        if (CaveOutside(w, z, q, at) is { } n) return Standing(q, at, "cave", n);
        return QuarryCell.Dig;
    }

    private static QuarryCell Standing(QuarryScan q, P3 at, string kind, P3 source) => NoteHazard(q, at, kind, source);

    /// <summary>
    /// Can the liquid in this cell get out, and a pawn get at it: a loaded, open cell that is no liquid on one of its 6 sides. With none
    /// it is contained (a pocket in rock, the water under a capped pool, lava under its lid): the cells round it stand, so it never
    /// spills, and no pawn has a face in view to plug or scoop it by (such seals failed "out of reach even from a pillar" for ever and
    /// held the quarry between tries). Counted and left; a cell opened beside it later makes it a seal to place again.
    /// </summary>
    private static bool LiquidExposed(IStoryWorld w, P3 at)
    {
        foreach (var n in Neighbours(at))
        {
            if (!w.Loaded(n.X, n.Z)) continue;
            int id = w.Block(n.X, n.Y, n.Z).Id;
            if (OpenCell(id) && !StoryBlocks.Liquids.Contains(id)) return true;
        }
        return false;
    }

    /// <summary>Sand or gravel left standing over the cell (a step of it is the stair repair's: replaced, the cell under it held).</summary>
    private static bool UnderFalling(IStoryWorld w, QuarryScan q, P3 at)
    {
        var over = at.Add(0, 1, 0);
        if (!StoryQuarry.Falls(w.Block(over.X, over.Y, over.Z).Id)) return false;
        return q.Ring is not { } ring || q.Top - over.Y is var k && (k < 0 || StoryQuarry.Step(ring, q.Top, k) != over);
    }

    /// <summary>The stair's head room: one of the 3 cells over a step (the walk onto it and the jump), never left standing.</summary>
    private static bool Headroom(QuarryScan q, P3 at)
    {
        if (q.Ring is not { } ring) return false;
        for (int d = 1; d <= 3; d++)
        {
            int k = q.Top - at.Y + d;
            if (k >= q.FirstStep && k <= q.MaxK && ring[k % ring.Count] == (at.X, at.Z)) return true;
        }
        return false;
    }

    /// <summary>A cave cell beside the zone's edge cell (outside the zone and any quarry): open (no liquid: those are seals already)
    /// and under ground or over a deep drop, and no closed pocket. Null = none.</summary>
    private P3? CaveOutside(IStoryWorld w, ZoneState z, QuarryScan q, P3 at)
    {
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var n = at.Add(dx, 0, dz);
            if (z.Cells.Contains(n.X, n.Z) || !w.Loaded(n.X, n.Z) || !CaveAir(w, n)) continue;
            // Another quarry's pit beside it is no cave; nor is a closed pocket.
            if (QuarryAt(n) == null && !ClosedPocket(w, q, n)) return n;
        }
        return null;
    }

    /// <summary>Does the block stand between the quarry and a cave: cave air outside every quarry's columns beside it, no closed pocket
    /// (the stray look keeps it, StoryColony.TempStrays.cs).</summary>
    private bool WallsOffCave(IStoryWorld w, ZoneState z, P3 at)
    {
        foreach (var n in Neighbours(at))
        {
            if (n.Y < 1 || QuarryColumn(n) != null || !w.Loaded(n.X, n.Z) || !CaveAir(w, n)) continue;
            if (Quarry(z) is not { } q || !ClosedPocket(w, q, n)) return true;
        }
        return false;
    }

    /// <summary>A closed pocket holds at most this many open cells (the vein batch's limit); more is a cave.</summary>
    private const int PocketCells = 64;

    /// <summary>
    /// Is this open cell part of a closed pocket: the open, non-liquid cells joined to it outside every quarry end within
    /// <see cref="PocketCells"/> and reach no unloaded column. An ore hole dug out of the pit's wall has 3 natural blocks over it, which
    /// is cave air: it was walled off with a block in the pit's own cell and alerted as "breached a cave" (C10). The answer is kept for
    /// every cell the flood met, for this look (<see cref="QuarryScan.Caves"/>), so a cave is flooded once per look.
    /// </summary>
    private bool ClosedPocket(IStoryWorld w, QuarryScan q, P3 from)
    {
        q.Caves ??= new();
        if (q.Caves.TryGetValue(from, out var known)) return !known;
        var seen = new HashSet<P3> { from };
        var todo = new Queue<P3>();
        todo.Enqueue(from);
        bool cave = false;
        while (todo.Count > 0 && !cave)
            foreach (var n in Neighbours(todo.Dequeue()))
            {
                if (n.Y < 0 || seen.Contains(n)) continue;
                if (n.Y >= w.Height || !w.Loaded(n.X, n.Z)) { cave = true; break; } // open to the sky, or not known
                int id = w.Block(n.X, n.Y, n.Z).Id;
                if (!OpenCell(id) || StoryBlocks.Liquids.Contains(id) || QuarryAt(n) != null) continue;
                seen.Add(n);
                if (seen.Count > PocketCells) { cave = true; break; }
                todo.Enqueue(n);
            }
        foreach (var c in seen) q.Caves[c] = cave;
        return !cave;
    }

    /// <summary>Open, no liquid, and under ground or over a drop of <see cref="DeepDrop"/> or more.</summary>
    private static bool CaveAir(IStoryWorld w, P3 n)
    {
        int id = w.Block(n.X, n.Y, n.Z).Id;
        if (!OpenCell(id) || StoryBlocks.Liquids.Contains(id)) return false;
        return StoryWander.Underground(w, n) || DropUnder(w, n) >= DeepDrop;
    }

    /// <summary>How many open cells are under the cell (counted to <see cref="DeepDrop"/>; the void counts as deep).</summary>
    private static int DropUnder(IStoryWorld w, P3 n)
    {
        int open = 0;
        for (int y = n.Y - 1; y >= n.Y - DeepDrop && open < DeepDrop; y--)
        {
            if (y < 0) return DeepDrop;
            if (!OpenCell(w.Block(n.X, y, n.Z).Id)) break;
            open++;
        }
        return open;
    }

    /// <summary>A monster spawner in or beside the working layer (the zone's box and 4 round it, 4 under to 1 over), or null.</summary>
    private static P3? SpawnerNear(IStoryWorld w, ZoneState z, QuarryScan q)
    {
        var (x1, z1, x2, z2) = z.Cells.Bounds();
        for (int x = x1 - 4; x <= x2 + 4; x++)
            for (int zz = z1 - 4; zz <= z2 + 4; zz++)
                if (w.Loaded(x, zz) && SpawnerInColumn(w, x, zz, q.Y - 4, q.Y + 1) is { } y) return new P3(x, y, zz);
        return null;
    }

    private static int? SpawnerInColumn(IStoryWorld w, int x, int z, int y1, int y2)
    {
        for (int y = Math.Max(1, y1); y <= y2; y++)
            if (w.Block(x, y, z).Id == Spawner) return y;
        return null;
    }

    /// <summary>
    /// Cells of the layer no dig can ever reach: closed in by what stays (steps, seals, the zone's outside) with no cell to dig on the way
    /// to them (a corner under a step, behind a cave's wall). Left standing, or the layer would wait for them for ever. A cell closed in
    /// only by cells still to dig is kept: it opens once they are gone.
    /// </summary>
    private void PruneWalledIn(IStoryWorld w, QuarryScan q)
    {
        if (q.Dig.Count == 0) return;
        var left = q.Dig.ToHashSet();
        var todo = new Queue<P3>(q.Dig.Where(c => Exposed(w, c)));
        var reached = todo.ToHashSet();
        while (todo.Count > 0)
            foreach (var n in Neighbours(todo.Dequeue()))
                if (left.Contains(n) && reached.Add(n)) todo.Enqueue(n);
        if (q.Left != null) foreach (var c in q.Dig) if (!reached.Contains(c)) q.Left.Add((c, "walled")); // StoryColony.QuarrySweep.cs
        int walled = q.Dig.RemoveAll(c => !reached.Contains(c));
        q.Hazards += walled;
    }

    // ------------------------------------------------------------------ jobs and waiting

    /// <summary>A seal to place (top-most first) that this pawn can take, else null. One that can't be started now (a lava source with
    /// no bucket, no block to seal with) never hides the others; what holds one of a kind holds them all, so each kind is asked once.</summary>
    private ActiveJob? SealJob(PawnState p, PawnLive live, ZoneState z, QuarryScan q)
    {
        bool lavaHeld = false, blockHeld = false;
        foreach (var (at, kind) in q.SealFix.OrderByDescending(f => f.At.Y))
        {
            if (!Open("b:" + at, p.Name, at)) continue;
            bool lava = World is { } w && KeptLava(w, at);
            if (lava ? lavaHeld : blockHeld) continue;
            var job = lava ? QuarryLavaJob(p, live, z, at)
                : QuarryBlockJob(p, live, z, at, false, live.Block, SealAny, $"sealing the quarry ({SealWords(kind)}, y {at.Y})", reach: !q.Plugs.Contains(at));
            if (job != null) return job;
            if (lava) lavaHeld = true; else blockHeld = true;
        }
        return null;
    }

    private static string SealWords(string kind) => kind switch
    {
        "cave" => "a cave opening",
        "lava" => "lava",
        "water" => "water",
        _ => kind,
    };

    /// <summary>A seal still to place that isn't resting after failures: nothing more is dug until it is in.</summary>
    private bool SealPending(QuarryScan q) => q.SealFix.Any(f => !QuarryResting(f.At));

    /// <summary>Why the quarry digs nothing now because of a hazard: a spawner, a seal to place; else null.</summary>
    private string? HazardWait(ZoneState z, QuarryScan q)
    {
        if (q.Spawner is { } sp) return $"stopped: a monster spawner at {sp} (break it, or move the quarry)";
        if (!SealPending(q)) return null;
        var f = q.SealFix.Where(s => !QuarryResting(s.At)).OrderByDescending(s => s.At.Y).First();
        if (World is { } w && KeptLava(w, f.At))
            return LavaHeld(z, f.At) is { } hold ? $"lava source at y {f.At.Y}: {hold}" : $"collecting a lava source at y {f.At.Y}";
        return $"sealing {SealWords(f.Kind)} at y {f.At.Y}";
    }

    /// <summary>A quarry job that failed because the pawn was in danger there: the cell is left standing from now on.</summary>
    private void NoteQuarryDanger(string lease, string why)
    {
        if (!Danger(why) || ParseCell(lease[2..]) is not { } at) return;
        _quarryDanger.Add(at);
        _quarryScan.Clear(); // looked at again now
    }

    private static bool Danger(string why) =>
        why.Contains("in lava", StringComparison.OrdinalIgnoreCase) || why.Contains("lava (standing", StringComparison.OrdinalIgnoreCase)
        || why.Contains("suffocat", StringComparison.OrdinalIgnoreCase);

    /// <summary>Quarry cells the server or the client won't let a pawn break (protected, preserved, unbreakable) → their quarry's id.
    /// They stand (the cell's back-off of 30 s, 1 min, then 10 min held the layer for good) and are forgotten once open.</summary>
    private readonly Dictionary<P3, string> _quarryRefused = new();

    /// <summary>A quarry dig refused for good: the cell is left standing, and said at most every 10 minutes per quarry (up to 5 cells).</summary>
    private void NoteQuarryRefused(ZoneState z, string lease, string why)
    {
        if (!Refused(why) || ParseCell(lease[2..]) is not { } at) return;
        _quarryRefused[at] = z.Id;
        _quarryScan.Remove(z.Id); // looked at again now
        if (_quarryAlert.TryGetValue("refused:" + z.Id, out var t) && Now - t < TimeSpan.FromMinutes(10)) return;
        _quarryAlert["refused:" + z.Id] = Now;
        var cells = _quarryRefused.Where(r => r.Value == z.Id).Select(r => r.Key.ToString()).ToList();
        Alert("warn", $"Quarry {ZoneName(z)}: {cells.Count} cell(s) left standing: no pawn may break them ({string.Join(", ", cells.Take(5))}"
            + $"{(cells.Count > 5 ? ", …" : "")}; {Short(why)})");
    }

    private static bool Refused(string why) =>
        why.Contains("protected", StringComparison.OrdinalIgnoreCase) || why.Contains("preserved", StringComparison.OrdinalIgnoreCase)
        || why.Contains("unbreakable", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ said once, shown in the status

    /// <summary>The hazards this look found that no look found before: one alert for them (never one per look).</summary>
    private void QuarryHazardNews(ZoneState z, QuarryScan q)
    {
        if (!_quarryHazards.TryGetValue(z.Id, out var seen)) _quarryHazards[z.Id] = seen = new();
        if (!_quarryHazardSaid.TryGetValue(z.Id, out var said)) _quarryHazardSaid[z.Id] = said = new();
        var found = q.Spawner is { } sp ? q.Found.Append((sp, "spawner", sp)) : q.Found;
        var fresh = new List<(P3 At, string Kind)>();
        foreach (var (at, kind, source) in found)
        {
            seen.TryAdd(at, kind);
            if (kind == "protected" || SameHazard(said, kind, source)) continue; // a refused cell has its own alert (NoteQuarryRefused)
            said.Add((source, kind));
            fresh.Add((at, kind));
        }
        if (fresh.Count > 0) Alert("warn", $"Quarry {ZoneName(z)}: {string.Join("; ", fresh.GroupBy(f => f.Kind).Select(HazardNews))}");
    }

    /// <summary>Already said: a hazard of the kind near this one (one cave, one lava pool, however many cells it touches).</summary>
    private static bool SameHazard(List<(P3 Source, string Kind)> said, string kind, P3 source)
    {
        int r = kind switch { "cave" => 8, "lava" or "water" => 4, _ => 1 };
        foreach (var (s, k) in said)
            if (k == kind && Math.Max(Math.Abs(s.X - source.X), Math.Max(Math.Abs(s.Y - source.Y), Math.Abs(s.Z - source.Z))) <= r) return true;
        return false;
    }

    private static string HazardNews(IGrouping<string, (P3 At, string Kind)> g)
    {
        int n = g.Count();
        int y = g.Max(f => f.At.Y);
        return g.Key switch
        {
            "cave" => $"breached a cave at y {y}: {n} opening cell(s) walled off",
            "lava" => $"{n} cell(s) at y {y} left standing beside lava or sealed over it (a seal)",
            "water" => $"{n} cell(s) at y {y} left standing beside water or sealed over it (a seal)",
            "sand" => $"{n} cell(s) at y {y} left standing under sand or gravel",
            "spawner" => $"a monster spawner at {g.First().At}: stopped until it is gone",
            "kept" => $"{n} cell(s) at y {y} left standing (a temporary block whose removal was cancelled)",
            _ => $"{n} cell(s) at y {y} left standing (a pawn was in danger there)",
        };
    }

    /// <summary>"; left standing: 3 cave, 2 lava (breached a cave at y 52)" from what the last full look found still standing (not the
    /// stair), or "". It counted every hazard this process ever met (plugs long since dug, seals still to place) and none after a restart.</summary>
    private string HazardSummary(ZoneState z)
    {
        if (!_quarryLeft.TryGetValue(z.Id, out var left) || left.Sealed == 0) return ""; // StoryColony.QuarrySweep.cs
        var text = "; left standing: " + LeftWords(left);
        return left.CaveY > 0 ? text + $" (breached a cave at y {left.CaveY})" : text;
    }

    /// <summary>The state's hazards: counts by kind, the spawner, the seals still to place.</summary>
    private JsonObject HazardsJson(ZoneState z, QuarryScan q)
    {
        var o = new JsonObject();
        if (_quarryHazards.TryGetValue(z.Id, out var seen))
            foreach (var g in seen.Values.GroupBy(k => k)) o[g.Key] = g.Count();
        if (q.Spawner is { } sp) o["spawner_at"] = sp.Json();
        o["to_seal"] = q.SealFix.Count;
        return o;
    }
}
