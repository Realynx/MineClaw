using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MineClaw.Hive.Story;

/// <summary>
/// Fishing zones (docs/web/story-mode.md "Fishing"). Fox 11-03: "Make me a fishing zone where pawns can fish using fishing poles;
/// it must be over water blocks." A zone of kind <c>fish</c> (a work zone) holds only still-water cells (source blocks under open
/// air); a pawn with Fish work and a rod (carried, fetched from storage, else a craft task for one) walks to a free shore cell beside it,
/// casts at the nearest water cell and fishes for a session (the <c>fish</c> primitive: cast, wait for a bite, reel in, repeat);
/// its catch is delivered to storage like any gathering. Optional bills: fish forever, until N raw fish in storage, or N catches.
/// </summary>
public sealed partial class StoryColony
{
    public const int FishingRod = 346, RawFish = 349, StringItem = 287;
    /// <summary>A session's length (the primitive reels in early once the bills are met).</summary>
    public const int FishSessionSeconds = 150;
    /// <summary>A zone needs this many water cells, and a standable shore cell within <see cref="ShoreReach"/> of them.</summary>
    public const int MinFishWater = 4, ShoreReach = 3;

    private readonly Dictionary<string, (DateTimeOffset At, List<P3> Water)> _fishScan = new();
    private readonly Dictionary<string, DateTimeOffset> _fishAlert = new();
    private static readonly Regex Caught = new(@"(\d+) fish caught", RegexOptions.Compiled);

    /// <summary>The still water at the top of column x,z near <paramref name="level"/>: a source block (meta 0) under open air.</summary>
    internal static P3? WaterAt(IStoryWorld w, int x, int z, int level)
    {
        if (!w.Loaded(x, z)) return null;
        for (int y = Math.Min(w.Height - 2, level + 3); y >= level - 4 && y > 0; y--)
        {
            var (id, meta) = w.Block(x, y, z);
            if (StoryPlants.Water(id)) return meta == 0 ? new P3(x, y, z) : null; // flowing or falling water isn't fished
            if (!StoryWander.Passable(id)) return null; // covered: not open water
        }
        return null;
    }

    /// <summary>The cells of <paramref name="cells"/> that are still water (the fishing zone keeps only those).</summary>
    internal static ZoneCells WaterCells(IStoryWorld w, ZoneCells cells, int level)
    {
        var water = new ZoneCells();
        foreach (var (x, z) in cells.Sorted())
            if (WaterAt(w, x, z, level) != null) water.Add(x, z);
        return water;
    }

    /// <summary>
    /// Shore cells: standable (not in water, not on a waterfall: no liquid at the feet or under them, nothing that hurts) within
    /// <see cref="ShoreReach"/> of a water cell, at most <paramref name="max"/>.
    /// </summary>
    internal static List<P3> Shore(IStoryWorld w, IReadOnlyCollection<P3> water, int max = 64)
    {
        var seen = new HashSet<(int, int)>();
        var cols = water.Select(c => (c.X, c.Z)).ToHashSet();
        var list = new List<P3>();
        foreach (var c in water)
            for (int dx = -ShoreReach; dx <= ShoreReach; dx++)
                for (int dz = -ShoreReach; dz <= ShoreReach; dz++)
                {
                    int x = c.X + dx, z = c.Z + dz;
                    if (cols.Contains((x, z)) || !seen.Add((x, z)) || !w.Loaded(x, z)) continue;
                    if (ShoreCell(w, x, z, c.Y) is not { } cell) continue;
                    list.Add(cell);
                    if (list.Count >= max) return list;
                }
        return list;
    }

    /// <summary>A column's highest standable cell up to 3 above the water, unless it stands in or over liquid or something that hurts.</summary>
    private static P3? ShoreCell(IStoryWorld w, int x, int z, int waterY)
    {
        for (int y = waterY + 3; y >= waterY; y--)
        {
            if (!StoryWander.Standable(w, x, y, z)) continue;
            int below = w.Block(x, y - 1, z).Id, feet = w.Block(x, y, z).Id;
            if (StoryBlocks.Liquids.Contains(below) || StoryBlocks.Liquids.Contains(feet) || StoryWander.Hurts(below)) return null;
            return new P3(x, y, z);
        }
        return null;
    }

    /// <summary>A fishing zone's water (cached 10 s; a finished session clears it).</summary>
    private List<P3> FishWater(IStoryWorld w, ZoneState z)
    {
        if (_fishScan.TryGetValue(z.Id, out var c) && Now - c.At < TimeSpan.FromSeconds(10)) return c.Water;
        var list = new List<P3>();
        foreach (var (x, zz) in z.Cells.Sorted())
        {
            if (list.Count >= 4096) break;
            if (WaterAt(w, x, zz, z.Area.Y1) is { } p) list.Add(p);
        }
        _fishScan[z.Id] = (Now, list);
        return list;
    }

    /// <summary>
    /// Painting a fishing zone: only still water is kept (the rest of the area is ignored, never overwritten). Refused with no
    /// water, fewer than <see cref="MinFishWater"/> water cells (with the fishing zones it joins), or no shore to stand on.
    /// </summary>
    private (ZoneCells? Cells, string? Error) FishPaintCells(ZoneCells cells, int level)
    {
        if (World is not { } w) return (null, "no world view yet: a fishing zone needs to see the water");
        var water = WaterCells(w, cells, level);
        if (water.Count == 0) return (null, "no still water here: a fishing zone is painted over water source blocks");
        int joined = water.Count + Zones.Where(z => z.Kind == "fish" && z.Cells.Touches(water)).Sum(z => z.Cells.Count);
        if (joined < MinFishWater) return (null, $"a fishing zone needs at least {MinFishWater} water cells ({joined} here)");
        var surface = water.Sorted().Select(c => WaterAt(w, c.X, c.Z, level)).OfType<P3>().ToList();
        if (Shore(w, surface, 1).Count == 0) return (null, $"no shore to stand on within {ShoreReach} blocks of the water");
        return (water, null);
    }

    /// <summary>Does the zone want fishing now: no bills (forever), or an unpaused bill whose target isn't met.</summary>
    private bool FishWanted(ZoneState z) => z.Bills is not { Count: > 0 } bills || bills.Any(b => !b.Paused && !FishBillMet(b));

    private bool FishBillMet(GrowBill b) => b.Mode switch
    {
        "until" => CountItem(RawFish, -1) >= b.Target,
        "count" => b.Done >= b.Target,
        _ => false,
    };

    /// <summary>How many more fish the active bill wants (a session stops there); 64 for forever.</summary>
    private int FishLeft(ZoneState z)
    {
        var b = z.Bills?.FirstOrDefault(b => !b.Paused && !FishBillMet(b));
        return b?.Mode switch
        {
            "until" => Math.Max(1, b.Target - CountItem(RawFish, -1)),
            "count" => Math.Max(1, b.Target - b.Done),
            _ => 64,
        };
    }

    /// <summary>The Fish work: a free shore cell by a fishing zone that wants fish, with a rod (carried or fetched).</summary>
    private ActiveJob? FishJob(PawnState p, PawnLive live)
    {
        if (World is not { } w) return null;
        var from = live.Block;
        var fishers = _current.Where(kv => !kv.Key.Equals(p.Name, StringComparison.OrdinalIgnoreCase) && kv.Value.Source == "fish" && kv.Value.At != null)
            .Select(kv => kv.Value.At!.Value).ToList();
        foreach (var z in Zones.Where(z => z.Kind == "fish" && FishWanted(z)).OrderBy(z => z.Area.Center.Dist(from)))
        {
            var water = FishWater(w, z);
            if (water.Count == 0) continue;
            // Spread out: at least 2 blocks from anyone already fishing.
            var shore = Shore(w, water).Where(s => fishers.All(f => StoryWander.Gap(s, f) >= 2) && Open("fish:" + s, p.Name, s))
                .OrderBy(s => s.Dist(from)).FirstOrDefault(new P3(int.MinValue, 0, 0));
            if (shore.X == int.MinValue) continue;
            var rod = live.Inventory.FirstOrDefault(s => s.Id == FishingRod);
            (StorageState Store, Stack Stack)? stored = rod == null ? FindStored(s => s.Id == FishingRod, from) : null;
            if (rod == null && stored == null) { NeedRod(w, z); return null; }
            var target = water.OrderBy(c => c.Dist(shore)).First();
            int left = FishLeft(z);
            var j = Job(WorkTypes.Fish, "fish", "fishing", shore, "fish:" + shore, "fish", z.Id, "working", f =>
            {
                f["water"] = target.ToString();
                f["s"] = FishSessionSeconds;
                f["max"] = left;
                if (stored is { } st) f["fetch"] = Fetch(st.Store.Chest, st.Stack, 1);
            }, xp: 25);
            if (stored is { } st2) j.Takes.Add((st2.Store.Chest, st2.Stack with { Count = 1 }));
            return j;
        }
        return null;
    }

    /// <summary>No rod anywhere: a craft task for one at the crafting table (3 sticks, 2 string); no string: the colony hears where from.</summary>
    private void NeedRod(IStoryWorld w, ZoneState z)
    {
        if (CountItem(StringItem, -1) < 2)
        {
            if (_fishAlert.TryGetValue("string", out var last) && Now - last < TimeSpan.FromMinutes(10)) return;
            _fishAlert["string"] = Now;
            Alert("warn", $"Fishing zone {ZoneName(z)} needs a fishing rod: no string for one (spiders drop it; cobwebs too)");
            return;
        }
        if (NeedTask(w, FishingRod, 1, $"fishing zone {ZoneName(z)} needs a rod")) return; // a craft task (StoryColony.CraftTasks.cs)
        if (_fishAlert.TryGetValue("rod", out var t) && Now - t < TimeSpan.FromMinutes(10)) return;
        _fishAlert["rod"] = Now;
        Alert("warn", $"Fishing zone {ZoneName(z)} needs a fishing rod: store one, or add a crafting table to craft one");
    }

    /// <summary>A session ended: its catch counts toward the zone (and its first unmet count bill).</summary>
    private void FishDone(ActiveJob j, string? note)
    {
        _fishScan.Remove(j.SourceId ?? "");
        if (note == null || Caught.Match(note) is not { Success: true } m || !int.TryParse(m.Groups[1].Value, out var n) || n <= 0) return;
        if (Zones.FirstOrDefault(z => z.Id == j.SourceId) is not { } zone) return;
        zone.Catches += n;
        if (zone.Bills?.FirstOrDefault(b => !b.Paused && b.Mode == "count" && b.Done < b.Target) is { } bill) bill.Done = Math.Min(bill.Target, bill.Done + n);
        zone.Rev = Touch();
    }

    /// <summary>The state's fishing fields: water cells, who is fishing there, catches, bills.</summary>
    private void FishJson(ZoneState z, JsonObject o)
    {
        if (World is { } w) o["water_cells"] = FishWater(w, z).Count;
        o["fishers"] = new JsonArray(_current.Where(kv => kv.Value.Source == "fish" && kv.Value.SourceId == z.Id)
            .Select(kv => (JsonNode)(Pawns.TryGetValue(kv.Key, out var p) ? p.Display : kv.Key)!).ToArray());
        o["catches"] = z.Catches;
        o["status"] = FishWanted(z) ? "fishing" : "all bills met: not fishing";
        if (z.Bills is { } bills)
            o["bills"] = new JsonArray(bills.Select(b => (JsonNode)new JsonObject
            {
                ["id"] = b.Id, ["plant"] = "fish", ["label"] = "Raw fish", ["mode"] = b.Mode, ["target"] = b.Target, ["done"] = b.Done,
                ["paused"] = b.Paused, ["met"] = FishBillMet(b), ["stored"] = b.Mode == "until" ? CountItem(RawFish, -1) : null,
                ["active"] = b == bills.FirstOrDefault(x => !x.Paused && !FishBillMet(x)),
            }).ToArray());
    }
}
