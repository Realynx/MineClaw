using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// The irrigation planner for growing zones (docs/web/story-mode.md "Water"): farmland needs water within 4 blocks, so for a crop a
/// straight channel at soil level along the zone's longer side, one every 9 rows (a centre line in a zone 9 or narrower, and one more by
/// the far edge when it would be dry); sugar cane needs water right beside it, so one every 3 rows. Lines whose cells are all watered
/// already are left out, and so are cells indoors.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>One planned channel's shape: the lines' positions across the zone and how far each waters.</summary>
    private sealed record IrrigationShape(bool AlongX, bool Cane, int Cover, int Across, int Spacing, List<int> Lines);

    /// <summary>
    /// <c>POST …/zones {op:"irrigation", id}</c> → <c>{id, cells, dig, axis, lines, note}</c>: <c>cells</c> the sources at soil level,
    /// <c>dig</c> those that hold soil now (dug first: queue them as a remove item in the water item's group). Nothing is queued.
    /// </summary>
    public (JsonNode? Result, string? Error) Irrigation(string? id)
    {
        lock (_lock)
        {
            if (World is not { } w) return (null, "no world view yet");
            if (Zones.FirstOrDefault(z => z.Id == id) is not { } zone) return (null, "id: no such zone");
            if (zone.Kind != "farm") return (null, "irrigation is for growing zones");
            var shape = IrrigationShapeOf(w, zone);
            var cells = new List<P3>();
            var dig = new List<P3>();
            int kept = 0;
            foreach (var line in shape.Lines)
                if (IrrigationRow(w, zone, shape, line) is { } row) { kept++; AddIrrigationRow(w, row, cells, dig); }
            return (new JsonObject
            {
                ["id"] = zone.Id, ["axis"] = shape.AlongX ? "x" : "z", ["lines"] = kept,
                ["cells"] = new JsonArray(cells.Select(c => (JsonNode)c.Json()).ToArray()),
                ["dig"] = new JsonArray(dig.Select(c => (JsonNode)c.Json()).ToArray()),
                ["note"] = IrrigationNote(shape, kept, cells.Count, dig.Count),
            }, null);
        }
    }

    private IrrigationShape IrrigationShapeOf(IStoryWorld w, ZoneState zone)
    {
        var plant = ActivePlant(w, zone) ?? StoryPlants.Get(w.Data, zone.Plant);
        bool cane = plant?.Block == StoryPlants.Cane;
        int spacing = cane ? 3 : 9, cover = cane ? 1 : 4;
        var (x1, z1, x2, z2) = zone.Cells.Bounds();
        bool alongX = x2 - x1 >= z2 - z1; // the lines run along the longer side
        int lo = alongX ? z1 : x1, hi = alongX ? z2 : x2;
        return new IrrigationShape(alongX, cane, cover, hi - lo + 1, spacing, IrrigationLines(lo, hi, spacing, cover));
    }

    /// <summary>The lines across [lo, hi]: the middle when it is no wider than the spacing, else every spacing from the first cover, and
    /// one more at the far edge when the last line leaves it dry.</summary>
    internal static List<int> IrrigationLines(int lo, int hi, int spacing, int cover)
    {
        int across = hi - lo + 1;
        if (across <= spacing) return [lo + across / 2];
        var lines = new List<int>();
        for (int k = lo + cover; k <= hi; k += spacing) lines.Add(k);
        if (lines[^1] + cover < hi) lines.Add(hi);
        return lines;
    }

    /// <summary>The soil cells of one line, or null when every cell the line would water has water close enough already.</summary>
    private List<P3>? IrrigationRow(IStoryWorld w, ZoneState zone, IrrigationShape shape, int line)
    {
        var row = new List<P3>();
        bool dry = false;
        foreach (var (x, z) in zone.Cells.Sorted())
        {
            int a = shape.AlongX ? z : x;
            if (Math.Abs(a - line) > shape.Cover || IrrigationSoil(w, zone, x, z) is not { } soil) continue;
            dry |= !(shape.Cane ? StoryPlants.WaterBeside(w, soil) : StoryPlants.WaterNear(w, soil));
            if (a == line && !Indoors(w, soil)) row.Add(soil);
        }
        return dry && row.Count > 0 ? row : null;
    }

    /// <summary>A loaded column's soil near the zone's level.</summary>
    private static P3? IrrigationSoil(IStoryWorld w, ZoneState zone, int x, int z) => w.Loaded(x, z) ? SoilIn(w, x, z, zone.Area.Y1) : null;

    private static void AddIrrigationRow(IStoryWorld w, List<P3> row, List<P3> cells, List<P3> dig)
    {
        foreach (var c in row)
        {
            var (bid, bm) = w.Block(c.X, c.Y, c.Z);
            if (IsWaterId(bid) && bm == 0) continue;
            cells.Add(c);
            if (!TakesLiquid(bid)) dig.Add(c);
        }
    }

    private static string IrrigationNote(IrrigationShape shape, int kept, int cells, int dig)
    {
        if (cells == 0) return "the zone is watered already: every cell has water close enough";
        string what = shape.Cane ? "beside the sugar cane (every 3rd row)" : shape.Across <= shape.Spacing ? "down the middle" : "every 9th row";
        return $"{kept} channel{(kept == 1 ? "" : "s")} along {(shape.AlongX ? "x" : "z")} {what}: {cells} water cell{(cells == 1 ? "" : "s")}"
               + (dig > 0 ? $", {dig} dug out first" : "");
    }
}
