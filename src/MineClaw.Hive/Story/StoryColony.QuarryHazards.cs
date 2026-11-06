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
/// by the stair repair as before.</item>
/// <item><b>Lava and water.</b> A cell with lava or water on any of its 6 sides (flowing down from above too) is never dug: it is left
/// standing as a seal. Lava sources in the zone are collected into buckets for storage. Water and flowing lava get a block placed
/// in them (cobblestone, stone or dirt: never planks); once no liquid remains beside that plug it is mined like any other cell.</item>
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
        if (CaveOutside(w, z, at) is not { } n) return QuarryCell.Skip;
        var seal = Headroom(q, at) ? n : at;
        // An old opening high above the floor may float in air on all six sides. Start where a block can attach;
        // the upper cells become placeable as the wall grows, rather than repeatedly sending miners to an impossible placement.
        bool supported = Neighbours(seal).Any(p => Ground(w.Block(p.X, p.Y, p.Z).Id));
        return NoteHazard(q, at, "cave", n, seal: supported ? seal : null);
    }

    /// <summary>A block of the layer: left standing beside a liquid, under a standing falling block, where a pawn met danger, or as the
    /// wall to a cave (not in the stair's head room); else dug.</summary>
    private QuarryCell SolidQuarryCell(IStoryWorld w, ZoneState z, QuarryScan q, P3 at)
    {
        if (LiquidBeside(w, at) is { } l) return Standing(q, at, LiquidKind(w.Block(l.X, l.Y, l.Z).Id), l);
        if (UnderFalling(w, q, at)) return Standing(q, at, "sand", at.Add(0, 1, 0));
        if (_quarryDanger.Contains(at)) return Standing(q, at, "danger", at);
        if (!Headroom(q, at) && CaveOutside(w, z, at) is { } n) return Standing(q, at, "cave", n);
        return QuarryCell.Dig;
    }

    private static QuarryCell Standing(QuarryScan q, P3 at, string kind, P3 source) => NoteHazard(q, at, kind, source);

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
            if (k >= 0 && k <= q.MaxK && ring[k % ring.Count] == (at.X, at.Z)) return true;
        }
        return false;
    }

    /// <summary>A cave cell beside the zone's edge cell (outside the zone and any quarry): open (no liquid: those are seals already)
    /// and under ground or over a deep drop. Null = none.</summary>
    private P3? CaveOutside(IStoryWorld w, ZoneState z, P3 at)
    {
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var n = at.Add(dx, 0, dz);
            if (z.Cells.Contains(n.X, n.Z) || !w.Loaded(n.X, n.Z) || !CaveAir(w, n)) continue;
            if (QuarryAt(n) == null) return n; // another quarry's pit beside it is no cave
        }
        return null;
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
        int walled = q.Dig.RemoveAll(c => !reached.Contains(c));
        q.Hazards += walled;
    }

    // ------------------------------------------------------------------ jobs and waiting

    /// <summary>A seal to place (top-most first) that this pawn can take, else null.</summary>
    private ActiveJob? SealJob(PawnState p, PawnLive live, ZoneState z, QuarryScan q)
    {
        foreach (var (at, kind) in q.SealFix.OrderByDescending(f => f.At.Y))
        {
            if (!Open("b:" + at, p.Name, at)) continue;
            if (World is { } w && IsSourceAt(w, at, lava: true)) return QuarryLavaJob(p, live, z, at);
            return QuarryBlockJob(live, z, at, false, live.Block, SealAny, $"sealing the quarry ({SealWords(kind)}, y {at.Y})");
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
    private string? HazardWait(QuarryScan q)
    {
        if (q.Spawner is { } sp) return $"stopped: a monster spawner at {sp} (break it, or move the quarry)";
        if (!SealPending(q)) return null;
        var f = q.SealFix.Where(s => !QuarryResting(s.At)).OrderByDescending(s => s.At.Y).First();
        if (World is { } w && IsSourceAt(w, f.At, lava: true)) return $"collecting a lava source at y {f.At.Y}";
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
            if (SameHazard(said, kind, source)) continue;
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
            _ => $"{n} cell(s) at y {y} left standing (a pawn was in danger there)",
        };
    }

    /// <summary>"; sealed: 3 cave, 2 lava" from every hazard the quarry met, or "".</summary>
    private string HazardSummary(ZoneState z)
    {
        if (!_quarryHazards.TryGetValue(z.Id, out var seen) || seen.Count == 0) return "";
        var parts = seen.Values.Where(k => k != "spawner").GroupBy(k => k).Select(g => $"{g.Count()} {g.Key}").ToList();
        var cave = seen.Where(h => h.Value == "cave").Select(h => h.Key.Y).DefaultIfEmpty().Max();
        var text = parts.Count > 0 ? "; sealed: " + string.Join(", ", parts) : "";
        return cave > 0 ? text + $" (breached a cave at y {cave})" : text;
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
