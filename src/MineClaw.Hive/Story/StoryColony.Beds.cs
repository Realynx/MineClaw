using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>A bed the colony knows: its foot block, the home zone it stands in (null = by the primary home), who sleeps there.</summary>
public sealed record BedInfo(P3 Pos, string? Zone, string? Pawn);

/// <summary>
/// Homes and beds (docs/web/story-mode.md "Homes and beds"): a colony has one primary home (its anchor, <see cref="ColonyMeta.Home"/>)
/// and any number of home zones (houses). "At home" is the union of the home zones and <see cref="HomeRadius"/> around the primary
/// home. Beds are found in the home zones and within <see cref="BedRadius"/> of the primary home (a bed's two halves are one bed,
/// named by its foot); each pawn has at most one bed and each bed at most one pawn. In its sleep hours a pawn goes to its bed (else the
/// nearest free one) and sleeps there at night.
/// </summary>
public sealed partial class StoryColony
{
    public const int Bed = 26;
    /// <summary>Around the primary home, what counts as home besides the home zones.</summary>
    public const int HomeRadius = 24;
    /// <summary>Beds are looked for this far around the primary home (and in every home zone).</summary>
    public const int BedRadius = 32;

    private (DateTimeOffset At, List<BedInfo> Beds)? _bedScan;
    private readonly HashSet<string> _noBedAlerted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>At home: in a home zone, or within <see cref="HomeRadius"/> of the primary home (horizontally).</summary>
    internal bool InHome(P3 at, P3? home = null)
    {
        if (HomeZoneAt(at.X, at.Z) != null) return true; // the zones' cells (StoryColony.Zones.cs)
        return (home ?? Meta.Home ?? Home()) is { } h && Math.Sqrt((double)(at.X - h.X) * (at.X - h.X) + (double)(at.Z - h.Z) * (at.Z - h.Z)) <= HomeRadius;
    }

    /// <summary>The foot of the bed whose half is at <paramref name="at"/> (meta: bit 8 = head, bits 0-1 = facing: 0 +z, 1 −x, 2 −z, 3 +x), else null.</summary>
    internal static P3? BedFoot(IStoryWorld w, P3 at)
    {
        var (id, meta) = w.Block(at.X, at.Y, at.Z);
        if (id != Bed) return null;
        if ((meta & 8) == 0) return at;
        var (dx, dz) = (meta & 3) switch { 0 => (0, 1), 1 => (-1, 0), 2 => (0, -1), _ => (1, 0) };
        return at.Add(-dx, 0, -dz);
    }

    /// <summary>The beds now (scanned at most every 10 s), with their sleepers; assigned beds out of the scan are listed too.</summary>
    public List<BedInfo> Beds()
    {
        lock (_lock)
        {
            if (_bedScan is not { } c || Now - c.At > TimeSpan.FromSeconds(10)) _bedScan = c = (Now, ScanBeds());
            var list = c.Beds.Select(b => b with { Pawn = Meta.Beds.GetValueOrDefault(b.Pos.ToString()) }).ToList();
            foreach (var (key, pawn) in Meta.Beds)
                if (TryP3(key) is { } pos && list.All(b => b.Pos != pos))
                    list.Add(new BedInfo(pos, ZoneAt(pos), pawn));
            return list;
        }
    }

    private static P3? TryP3(string s)
    {
        var parts = s.Split(',');
        return parts.Length == 3 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) && int.TryParse(parts[2], out var z) ? new P3(x, y, z) : null;
    }

    private string? ZoneAt(P3 at) => HomeZoneAt(at.X, at.Z)?.Id;

    private List<BedInfo> ScanBeds()
    {
        var found = new List<BedInfo>();
        if (World is not { } w) return found;
        // Every home zone's cells (8 above and below its level), then the primary home's surroundings.
        var areas = Zones.Where(z => z.Kind == "home").Select(z => (Area: Area.Of(z.Area.X1, z.Area.Y1 - 8, z.Area.Z1, z.Area.X2, z.Area.Y2 + 8, z.Area.Z2), Cells: (ZoneCells?)z.Cells)).ToList();
        if (Home() is { } h) areas.Add((Area.Of(h.X - BedRadius, h.Y - 12, h.Z - BedRadius, h.X + BedRadius, h.Y + 12, h.Z + BedRadius), null));
        var seen = new HashSet<P3>();
        long scanned = 0;
        foreach (var (a, cells) in areas)
            for (int x = a.X1; x <= a.X2; x++)
                for (int z = a.Z1; z <= a.Z2; z++)
                {
                    if (cells != null && !cells.Contains(x, z) || !w.Loaded(x, z)) continue;
                    if (!ScanBedColumn(w, a, x, z, seen, found, ref scanned)) return found;
                }
        return found;
    }

    /// <summary>The beds in one column (by their foot, each once); false when the scan's budget (1M blocks) is spent.</summary>
    private bool ScanBedColumn(IStoryWorld w, Area a, int x, int z, HashSet<P3> seen, List<BedInfo> found, ref long scanned)
    {
        for (int y = Math.Max(0, a.Y1); y <= Math.Min(w.Height - 1, a.Y2); y++)
        {
            if (++scanned > 1_000_000) return false;
            if (w.Block(x, y, z).Id != Bed || BedFoot(w, new P3(x, y, z)) is not { } foot || !seen.Add(foot)) continue;
            found.Add(new BedInfo(foot, ZoneAt(foot), null));
        }
        return true;
    }

    /// <summary>The state's <c>beds</c>.</summary>
    internal JsonArray BedsJson()
    {
        int dim = World?.Dimension ?? 0;
        return new JsonArray(Beds().Select(b => (JsonNode)new JsonObject
        {
            ["pos"] = b.Pos.Json(), ["dim"] = dim, ["pawn"] = b.Pawn, ["zone"] = b.Zone,
        }).ToArray());
    }

    /// <summary>assign {bed, pawn} | unassign {bed} → (beds, error, not found). One pawn per bed, one bed per pawn: assigning moves them.</summary>
    public (JsonNode? Result, string? Error, bool NotFound) BedsOp(string op, P3? bed, string? pawn)
    {
        if (op is not ("assign" or "unassign")) return (null, "op must be assign|unassign", false);
        if (bed is not { } at) return (null, "bed must be {x,y,z}", false);
        if (World is not { } w) return (null, "no world view yet", false);
        lock (_lock)
        {
            P3? foot = BedFoot(w, at);
            if (foot == null && Meta.Beds.ContainsKey(at.ToString())) foot = at; // a bed it knew (broken since, or out of view): unassign works
            if (foot is not { } f) return (null, $"no bed at {at}", false);
            var key = f.ToString();
            if (op == "assign")
            {
                if (AssignBed(key, pawn) is { } fail) return fail;
            }
            else Meta.Beds.Remove(key);
            Touch();
            _bedScan = null;
        }
        SaveSoon();
        return (new JsonObject { ["beds"] = BedsJson() }, null, false);
    }

    /// <summary>The pawn's bed is this one now (its old bed is free); the error when there is no such pawn.</summary>
    private (JsonNode? Result, string? Error, bool NotFound)? AssignBed(string key, string? pawn)
    {
        if (string.IsNullOrWhiteSpace(pawn)) return (null, "pawn required", false);
        if (Pawn(pawn) is not { } p) return (null, $"no pawn '{pawn}'", true);
        foreach (var k in Meta.Beds.Where(kv => kv.Value.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            Meta.Beds.Remove(k); // its old bed is free now
        Meta.Beds[key] = p.Name;
        _noBedAlerted.Remove(p.Name);
        return null;
    }

    /// <summary>The bed <paramref name="p"/> sleeps in tonight: its own (while it is still a bed), else the nearest free one at home.</summary>
    private BedInfo? BedFor(PawnState p, P3 from)
    {
        var beds = Beds();
        var w = World;
        var mine = beds.FirstOrDefault(b => string.Equals(b.Pawn, p.Name, StringComparison.OrdinalIgnoreCase));
        if (mine != null && (w == null || !w.Loaded(mine.Pos.X, mine.Pos.Z) || BedFoot(w, mine.Pos) == mine.Pos)) return mine;
        return beds.Where(b => b.Pawn == null && Open("bed:" + b.Pos, p.Name, null)).OrderBy(b => b.Pos.Dist(from)).FirstOrDefault();
    }

    /// <summary>Its sleep hour: to its bed (sleeping there at night), else rest at home and say once that it has no bed.</summary>
    private ActiveJob SleepJob(PawnState p, PawnLive live, int hour, P3? home)
    {
        if (BedFor(p, live.Block) is { } bed)
        {
            bool own = string.Equals(bed.Pawn, p.Name, StringComparison.OrdinalIgnoreCase);
            return Job("schedule", "sleep", $"sleeping ({hour:00}:00)", bed.Pos, own ? null : "bed:" + bed.Pos, "none", null, "sleeping",
                j => { j["bed"] = bed.Pos.ToString(); j["s"] = 30; j["dist"] = 2; }, xp: 0);
        }
        if (_noBedAlerted.Add(p.Name)) Alert("info", $"{p.Display} has no bed: sleeping rough at home", p.Name);
        return RestHold(p, live, $"sleeping, no bed ({hour:00}:00)", "sleeping", home, 30, "schedule"); // its own spot at home: never a pile
    }
}
