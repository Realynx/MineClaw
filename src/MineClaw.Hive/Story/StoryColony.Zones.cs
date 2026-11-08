using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Zones as cell sets, painted RimWorld-style (docs/web/story-mode.md "Zones"; the engine is <see cref="ZonePainter"/>): add merges
/// with touching zones of the same type and overwrites other work zones; remove erases cells (zones left empty go, a zone cut in two
/// becomes two). Home and keep-out are overlay layers. A cell index answers "which house is this column in" in O(1).
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>
    /// <c>POST …/zones</c>: add | remove (erase an area, or one zone by id) | rename | update | seek_seeds | bill_* (growing zones:
    /// StoryColony.Farming.cs). <paramref name="body"/> is the whole request (growing settings, <c>into</c>, bill fields).
    /// </summary>
    public (JsonNode? Result, string? Error) ZoneOp(string op, string? kind, Area? area, string? id = null, string? name = null, JsonObject? grow = null)
    {
        if (name is { Length: > 40 }) return (null, "name: at most 40 characters");
        if (ZoneByIdOp(op, id, name, grow) is { } byId) return byId;
        kind = ZoneKindOf(op, kind, grow);
        if (op is not ("add" or "remove")) return (null, "op must be add|remove|rename|update|seek_seeds|irrigation|preserve|bill_add|bill_update|bill_remove|bill_pause|bill_resume|bill_reorder");
        if (area is not { } a) return (null, "area [x1,y1,z1,x2,y2,z2] required");
        if (ZonePaintError(op, kind, a) is { } err) return (null, err);
        var cells = ZoneCells.Rect(a);
        lock (_lock)
            return op == "remove" ? EraseZones(kind, cells) : PaintZone(kind, a, cells, name, grow);
    }

    /// <summary>The ops on one zone by id (a mine zone's mode, growing settings, irrigation, growing bills, rename, remove); null = not one.</summary>
    private (JsonNode? Result, string? Error)? ZoneByIdOp(string op, string? id, string? name, JsonObject? grow)
    {
        if (op == "update" && grow?["mode"] != null) return ZoneModeOp(id, grow);
        if (op is "update" or "seek_seeds") return GrowZoneOp(op, id, grow); // growing zones (StoryColony.Farming.cs)
        if (op == "irrigation") return Irrigation(id); // water channels the zone wants (StoryColony.Water.cs)
        if (op == "preserve") return PreserveOp(id, grow); // keep its blocks as they are (StoryColony.Preserve.cs)
        if (op.StartsWith("bill_", StringComparison.Ordinal)) return GrowBillOp(op, id, grow);
        if (op == "rename") return RenameZone(id, name);
        if (op == "remove" && id != null) return RemoveZone(id);
        return null;
    }

    /// <summary>A mine zone's mode: ores | quarry (StoryColony.Quarry.cs).</summary>
    private (JsonNode? Result, string? Error) ZoneModeOp(string? id, JsonObject grow)
    {
        lock (_lock)
        {
            if (Zones.FirstOrDefault(z => z.Id == id) is not { } mz) return (null, "id: no such zone");
            if (mz.Kind != "mine") return (null, "only mine zones take a mode (ores | quarry)");
            return MineModeOp(mz, grow["mode"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : null);
        }
    }

    private (JsonNode? Result, string? Error) RenameZone(string? id, string? name)
    {
        lock (_lock)
        {
            if (Zones.FirstOrDefault(z => z.Id == id) is not { } rz) return (null, "id: no such zone");
            rz.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            rz.Rev = Touch();
            return (new JsonObject { ["id"] = rz.Id, ["name"] = rz.Name }, null);
        }
    }

    private (JsonNode? Result, string? Error) RemoveZone(string id)
    {
        lock (_lock)
        {
            if (Zones.FirstOrDefault(z => z.Id == id) is not { } gz) return (null, "id: no such zone");
            Zones.Remove(gz);
            var r = new ZonePaintResult();
            r.Deleted.Add(gz.Id);
            ZonesChanged(r);
            return (ZoneResultJson(r, null), null);
        }
    }

    /// <summary>"grow" (or a plant with no kind) is a farm; "any" (erasing) is every kind.</summary>
    private static string? ZoneKindOf(string op, string? kind, JsonObject? grow)
    {
        if (kind == "grow" || kind == null && op == "add" && grow?["plant"] != null) return "farm";
        return kind == "any" ? null : kind;
    }

    private static string? ZonePaintError(string op, string? kind, Area a)
    {
        if (kind == "stockpile") return "storage is chests: register them with /storage (no ground stockpiles)";
        if (kind != null && !ZoneKinds.Contains(kind)) return $"kind must be {string.Join("|", ZoneKinds)}{(op == "remove" ? "|any" : "")}";
        if ((long)(a.X2 - a.X1 + 1) * (a.Z2 - a.Z1 + 1) > 256 * 256) return "zone too large (256 x 256 max)";
        return null;
    }

    private (JsonNode? Result, string? Error) EraseZones(string? kind, ZoneCells cells)
    {
        var er = ZonePainter.Erase(Zones, kind, cells, () => NextId("z"));
        ZonesChanged(er);
        return (ZoneResultJson(er, null), null);
    }

    /// <summary>Paints the cells as a zone of the kind (or into an existing zone): merged with touching zones of its type, over others.</summary>
    private (JsonNode? Result, string? Error) PaintZone(string? kind, Area a, ZoneCells cells, string? name, JsonObject? grow)
    {
        var (into, ierr) = PaintInto(kind, grow);
        if (ierr != null) return (null, ierr);
        var template = new ZoneState { Kind = into?.Kind ?? kind! };
        var warnings = new List<string>();
        var serr = into == null ? NewZoneSettings(template, a, cells, grow, warnings) : null;
        if (serr != null) return (null, serr);
        var (paintCells, ferr) = PaintCells(template, cells, a);
        if (ferr != null) return (null, ferr);
        var before = PreserveBefore(); // a preserved zone merged into another gives its cells' snapshot over (StoryColony.Preserve.cs)
        var (painted, err) = ZonePainter.Paint(Zones, template.Kind, template.Plant, paintCells!, a.Y1, a.Y2, into, () => MakeZone(into, template, name), () => NextId("z"));
        if (err != null || painted == null) return (null, err ?? "nothing painted");
        NamePainted(painted, name);
        ZonesChanged(painted, before);
        PaintedQuarry(painted, warnings); // over builds' cells: the overlap is said (StoryColony.QuarryConflicts.cs)
        PaintedPreserve(painted, warnings); // over builds, quarries, designations; a quarry over preserved zones (StoryColony.Preserve.cs)
        return (ZoneResultJson(painted, warnings), null);
    }

    /// <summary>The zone <c>into</c> names (it must be of the kind given); without one, a kind is required.</summary>
    private (ZoneState? Into, string? Error) PaintInto(string? kind, JsonObject? grow)
    {
        if (grow?["into"] is not { } intoNode) return (null, kind == null ? $"kind must be {string.Join("|", ZoneKinds)}" : null);
        var iid = intoNode is JsonValue iv && iv.TryGetValue<string>(out var s) ? s : intoNode.ToJsonString();
        if (Zones.FirstOrDefault(z => z.Id == iid) is not { } iz) return (null, "into: no such zone");
        if (kind != null && kind != iz.Kind) return (null, $"into: {iz.Id} is a {iz.Kind} zone, not {kind}");
        return (iz, null);
    }

    /// <summary>A new zone's settings: a mine zone's mode, a growing zone's plant, sowing and harvest (its soil judged on the cells).</summary>
    private string? NewZoneSettings(ZoneState template, Area a, ZoneCells cells, JsonObject? grow, List<string> warnings)
    {
        if (template.Kind == "mine" && MineZoneSettings(template, a, cells, grow) is { } merr) return merr;
        if (template.Kind == "preserve") return PreserveZoneSettings(template, cells, a, grow);
        if (grow != null && (grow["plant"] != null || grow["sow"] != null || grow["harvest"] != null)) return GrowZoneSettings(template, a, cells, grow, warnings);
        return null;
    }

    /// <summary>A new mine zone is a quarry unless asked for ores only; a 1-wide quarry is refused (a trench, not a quarry).</summary>
    private string? MineZoneSettings(ZoneState template, Area a, ZoneCells cells, JsonObject? grow)
    {
        var mode = grow?["mode"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : "quarry";
        if (mode is not ("ores" or "quarry")) return "mode must be ores|quarry";
        template.Mode = mode;
        if (mode == "quarry" && (a.X1 == a.X2 || a.Z1 == a.Z2) && !Zones.Any(z => IsQuarry(z) && z.Cells.Touches(cells))) return QuarryTooNarrow;
        return null;
    }

    /// <summary>A growing zone's soil is judged on the painted cells: refused when they can't grow the plant and join no zone of it.</summary>
    private string? GrowZoneSettings(ZoneState template, Area a, ZoneCells cells, JsonObject grow, List<string> warnings)
    {
        if (template.Kind != "farm") return "only farm (growing) zones take plant, sow and harvest";
        if (ApplyGrow(template, grow) is { } gerr) return gerr;
        var probe = template.CopySettings("~probe");
        probe.SetCells(cells.Clone(), a.Y1, a.Y2);
        var (bad, warn) = ValidateGrow(probe);
        _growScan.Remove(probe.Id);
        if (bad != null)
        {
            bool joins = Zones.Any(z => z.Kind == "farm" && string.Equals(z.Plant, template.Plant, StringComparison.OrdinalIgnoreCase) && z.Cells.Touches(cells));
            if (!joins) return bad;
            warnings.Add(bad);
        }
        warnings.AddRange(warn);
        return null;
    }

    /// <summary>Only still water is a fishing zone's (StoryColony.Fishing.cs): the rest of the area is ignored, not overwritten.</summary>
    private (ZoneCells? Cells, string? Error) PaintCells(ZoneState template, ZoneCells cells, Area a) =>
        template.Kind == "fish" ? FishPaintCells(cells, a.Y1) : (cells, null);

    /// <summary>A zone the paint creates: the template's (or the joined zone's) settings, the name given, a farm's first bill.</summary>
    private ZoneState MakeZone(ZoneState? into, ZoneState template, string? name)
    {
        var z = (into ?? template).CopySettings(NextId("z"));
        z.Name = into == null && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
        if (z.Kind == "farm" && z.Plant != null && z.Bills == null) z.Bills = [new GrowBill { Id = NextId("gb"), Plant = z.Plant }];
        return z;
    }

    /// <summary>A name given with a paint that joined an unnamed zone names it.</summary>
    private void NamePainted(ZonePaintResult painted, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Zones.FirstOrDefault(z => z.Id == painted.Id) is not { Name: null } named) return;
        named.Name = name.Trim();
        painted.Changed.Add(named);
    }

    /// <summary>After a paint or an erase: revisions, the delta's removals, the zones' scan caches, beds, idle pawns, preserved zones'
    /// snapshots (<paramref name="before"/>: the preserved snapshots before a paint, for merges).</summary>
    private void ZonesChanged(ZonePaintResult r, Dictionary<string, PreserveSnap>? before = null)
    {
        foreach (var z in r.Changed)
        {
            z.Rev = Touch();
            _zoneScan.Remove(z.Id);
            _growScan.Remove(z.Id);
        }
        foreach (var id in r.Deleted.Concat(r.Merged.SelectMany(m => m.From)))
        {
            Removed("zone", id);
            _zoneScan.Remove(id);
            _growScan.Remove(id);
        }
        _bedScan = null;
        StopInvalidQuarryBatches();
        PreserveZonesChanged(r, before);
        WakeIdle();
    }

    private static JsonObject ZoneResultJson(ZonePaintResult r, List<string>? warnings)
    {
        static JsonArray Ids(IEnumerable<string> ids) => new(ids.Select(i => (JsonNode)i!).ToArray());
        var o = new JsonObject
        {
            ["created"] = Ids(r.Created),
            ["merged"] = new JsonArray(r.Merged.Select(m => (JsonNode)new JsonObject { ["into"] = m.Into, ["from"] = Ids(m.From) }).ToArray()),
            ["grown"] = Ids(r.Grown),
            ["shrunk"] = Ids(r.Shrunk),
            ["split"] = new JsonArray(r.Split.Select(s => (JsonNode)new JsonObject { ["from"] = s.From, ["into"] = Ids(s.Into) }).ToArray()),
            ["deleted"] = Ids(r.Deleted),
            ["removed"] = Ids(r.Deleted), // the zones that went (before cell sets, remove answered this)
        };
        if (r.Id != null) o["id"] = r.Id;
        if (warnings is { Count: > 0 }) o["warnings"] = Ids(warnings);
        return o;
    }

    // ---------------------------------------------------------------- the cell index

    private sealed class ZoneIx
    {
        public long Stamp = long.MinValue;
        /// <summary>Column → the (oldest) home zone it is in.</summary>
        public readonly Dictionary<long, ZoneState> Home = new();
        /// <summary>Column → the (oldest) quarry zone it is in (StoryColony.QuarryConflicts.cs).</summary>
        public readonly Dictionary<long, ZoneState> Quarry = new();
    }

    private ZoneIx _zix = new();

    private long ZoneStamp()
    {
        long s = Zones.Count * 7919L;
        foreach (var z in Zones) s = s * 31 + z.Rev;
        return s;
    }

    /// <summary>The home- and quarry-column index, rebuilt when a zone changed (an integer pass decides).</summary>
    private ZoneIx Zix()
    {
        long stamp = ZoneStamp();
        var ix = _zix;
        if (ix.Stamp == stamp) return ix;
        var n = new ZoneIx { Stamp = stamp };
        foreach (var z in Zones.ToArray())
        {
            var into = z.Kind == "home" ? n.Home : IsQuarry(z) ? n.Quarry : null;
            if (into != null) foreach (var k in z.Cells.Keys) into.TryAdd(k, z);
        }
        _zix = n;
        return n;
    }

    /// <summary>The home zone (house) the column is in, else null.</summary>
    internal ZoneState? HomeZoneAt(int x, int z) => Zix().Home.GetValueOrDefault(ZoneCells.Key(x, z));

    /// <summary>
    /// Older saves: a rectangle zone becomes its cells (unchanged), a growing zone's single plant a one-bill list. True when anything
    /// changed (the store then saves it).
    /// </summary>
    internal bool MigrateZones()
    {
        bool changed = false;
        foreach (var z in Zones)
        {
            if (z.Rows == null) { z.SetCells(ZoneCells.Rect(z.Area), z.Area.Y1, z.Area.Y2); changed = true; }
            if (z.Kind == "farm" && z.Plant != null && z.Bills == null) { z.Bills = [new GrowBill { Id = NextId("gb"), Plant = z.Plant }]; changed = true; }
        }
        return changed;
    }
}
