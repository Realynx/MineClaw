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
        if (op is "update" or "seek_seeds") return GrowZoneOp(op, id, grow); // growing zones (StoryColony.Farming.cs)
        if (op == "irrigation") return Irrigation(id); // water channels the zone wants (StoryColony.Water.cs)
        if (op.StartsWith("bill_", StringComparison.Ordinal)) return GrowBillOp(op, id, grow);
        if (kind == "grow" || kind == null && op == "add" && grow?["plant"] != null) kind = "farm";
        if (kind == "any") kind = null;
        if (op == "rename")
        {
            lock (_lock)
            {
                if (Zones.FirstOrDefault(z => z.Id == id) is not { } rz) return (null, "id: no such zone");
                rz.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
                rz.Rev = Touch();
                return (new JsonObject { ["id"] = rz.Id, ["name"] = rz.Name }, null);
            }
        }
        if (op == "remove" && id != null)
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
        if (op is not ("add" or "remove")) return (null, "op must be add|remove|rename|update|seek_seeds|bill_add|bill_update|bill_remove|bill_pause|bill_resume|bill_reorder");
        if (area is not { } a) return (null, "area [x1,y1,z1,x2,y2,z2] required");
        if (kind == "stockpile") return (null, "storage is chests: register them with /storage (no ground stockpiles)");
        if (kind != null && !ZoneKinds.Contains(kind)) return (null, $"kind must be {string.Join("|", ZoneKinds)}{(op == "remove" ? "|any" : "")}");
        if ((long)(a.X2 - a.X1 + 1) * (a.Z2 - a.Z1 + 1) > 256 * 256) return (null, "zone too large (256 x 256 max)");
        var cells = ZoneCells.Rect(a);
        lock (_lock)
        {
            if (op == "remove")
            {
                var er = ZonePainter.Erase(Zones, kind, cells, () => NextId("z"));
                ZonesChanged(er);
                return (ZoneResultJson(er, null), null);
            }
            ZoneState? into = null;
            if (grow?["into"] is { } intoNode)
            {
                var iid = intoNode is JsonValue iv && iv.TryGetValue<string>(out var s) ? s : intoNode.ToJsonString();
                if (Zones.FirstOrDefault(z => z.Id == iid) is not { } iz) return (null, "into: no such zone");
                if (kind != null && kind != iz.Kind) return (null, $"into: {iz.Id} is a {iz.Kind} zone, not {kind}");
                into = iz;
            }
            if (into == null && kind == null) return (null, $"kind must be {string.Join("|", ZoneKinds)}");
            var template = new ZoneState { Kind = into?.Kind ?? kind! };
            var warnings = new List<string>();
            if (into == null && grow != null && (grow["plant"] != null || grow["sow"] != null || grow["harvest"] != null))
            {
                if (template.Kind != "farm") return (null, "only farm (growing) zones take plant, sow and harvest");
                if (ApplyGrow(template, grow) is { } gerr) return (null, gerr);
                // A growing zone's soil is judged on the painted cells: refused when they can't grow the plant and join no zone of it.
                var probe = template.CopySettings("~probe");
                probe.SetCells(cells.Clone(), a.Y1, a.Y2);
                var (bad, warn) = ValidateGrow(probe);
                _growScan.Remove(probe.Id);
                if (bad != null)
                {
                    bool joins = Zones.Any(z => z.Kind == "farm" && string.Equals(z.Plant, template.Plant, StringComparison.OrdinalIgnoreCase) && z.Cells.Touches(cells));
                    if (!joins) return (null, bad);
                    warnings.Add(bad);
                }
                warnings.AddRange(warn);
            }
            if (template.Kind == "fish")
            {
                // Only still water is a fishing zone's (StoryColony.Fishing.cs): the rest of the area is ignored, not overwritten.
                var (water, ferr) = FishPaintCells(cells, a.Y1);
                if (ferr != null) return (null, ferr);
                cells = water!;
            }
            ZoneState Make()
            {
                var z = (into ?? template).CopySettings(NextId("z"));
                z.Name = into == null && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
                if (z.Kind == "farm" && z.Plant != null && z.Bills == null) z.Bills = [new GrowBill { Id = NextId("gb"), Plant = z.Plant }];
                return z;
            }
            var (painted, err) = ZonePainter.Paint(Zones, template.Kind, template.Plant, cells, a.Y1, a.Y2, into, Make, () => NextId("z"));
            if (err != null || painted == null) return (null, err ?? "nothing painted");
            // A name given with a paint that joined an unnamed zone names it.
            if (!string.IsNullOrWhiteSpace(name) && Zones.FirstOrDefault(z => z.Id == painted.Id) is { Name: null } named)
            {
                named.Name = name.Trim();
                painted.Changed.Add(named);
            }
            ZonesChanged(painted);
            return (ZoneResultJson(painted, warnings), null);
        }
    }

    /// <summary>After a paint or an erase: revisions, the delta's removals, the zones' scan caches, beds, idle pawns.</summary>
    private void ZonesChanged(ZonePaintResult r)
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
    }

    private ZoneIx _zix = new();

    private long ZoneStamp()
    {
        long s = Zones.Count * 7919L;
        foreach (var z in Zones) s = s * 31 + z.Rev;
        return s;
    }

    /// <summary>The home-column index, rebuilt when a zone changed (an integer pass decides).</summary>
    private ZoneIx Zix()
    {
        long stamp = ZoneStamp();
        var ix = _zix;
        if (ix.Stamp == stamp) return ix;
        var n = new ZoneIx { Stamp = stamp };
        foreach (var z in Zones.ToArray())
            if (z.Kind == "home")
                foreach (var k in z.Cells.Keys) n.Home.TryAdd(k, z);
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
