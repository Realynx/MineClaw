using System.Globalization;
using System.Text;
using MineClaw.Agent.Rag;
using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

public sealed partial class WorldIndexer
{
    private static string P(BlockPos p) => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}");

    /// <summary>Short human name of an entry ("cobble gen", "wheat farm", "storage chest" via label).</summary>
    internal static string DisplayName(string kind, string? subType, string? label) => kind switch
    {
        IndexKinds.Generator => "cobble gen",
        IndexKinds.Farm => (subType ?? "").Replace('_', ' ') + " farm",
        IndexKinds.UnknownStructure => "unknown build",
        IndexKinds.CraftingTable => "crafting table",
        IndexKinds.NoteBlock => "note block",
        _ => kind.Replace('_', ' '),
    } + (label is { Length: > 0 } && kind != IndexKinds.Sign ? $" \"{label}\"" : "");

    private string? Near(IndexEntry e)
    {
        var n = NearestEntry(e.Anchor, _o.NearRadius,
            x => x.Kind is not (IndexKinds.Island or IndexKinds.Hangout or IndexKinds.Activity or IndexKinds.Sign) && x.Kind != e.Kind, e);
        return n == null ? null : DisplayName(n.Kind, n.SubType, null);
    }

    private string? On(IndexEntry e) => e.Kind == IndexKinds.Island ? null : IslandName(e.Anchor.X, e.Anchor.Z);

    private static string ItemList(IReadOnlyList<ItemCount> items, int max)
    {
        if (items.Count == 0) return "empty";
        var s = string.Join(", ", items.Take(max).Select(i => $"{i.Name} x{i.Count}"));
        return items.Count > max ? s + $" +{items.Count - max} more" : s;
    }

    /// <summary>One entry's text and fields while it is rendered.</summary>
    private sealed class EntryText(IndexEntry e, string? on, string? near)
    {
        public readonly IndexEntry E = e;
        public readonly string? On = on;
        public readonly StringBuilder Sb = new();
        public readonly Dictionary<string, object?> F = new(StringComparer.Ordinal);

        /// <summary>" \"label\" on main island near cobble gen".</summary>
        public void Where()
        {
            if (E.Label is { Length: > 0 } && E.Kind != IndexKinds.Sign) Sb.Append($" \"{E.Label}\"");
            if (On != null) Sb.Append(" on ").Append(On);
            if (near != null) Sb.Append(" near ").Append(near);
        }
    }

    /// <summary>The text of each kind with its own wording; every other kind is "display name at x,y,z" + where.</summary>
    private static readonly Dictionary<string, Action<WorldIndexer, EntryText>> Renderers = new(StringComparer.Ordinal)
    {
        [IndexKinds.Chest] = (_, t) => RenderContainer(t),
        [IndexKinds.Dispenser] = (_, t) => RenderContainer(t),
        [IndexKinds.Furnace] = (_, t) => RenderFurnace(t),
        [IndexKinds.Sign] = (_, t) => RenderSign(t),
        [IndexKinds.Generator] = (_, t) => RenderGenerator(t),
        [IndexKinds.Farm] = (_, t) => RenderFarm(t),
        [IndexKinds.Tree] = (_, t) => RenderTree(t),
        [IndexKinds.UnknownStructure] = (_, t) => RenderUnknownStructure(t),
        [IndexKinds.Island] = (w, t) => w.RenderIsland(t),
        [IndexKinds.Portal] = (_, t) => RenderPortal(t),
        [IndexKinds.Hazard] = (_, t) => t.Sb.Append(RenderHazard(t.E, t.F)),
        [IndexKinds.Hangout] = (_, t) => RenderHangout(t),
    };

    /// <summary>Compact retrieval text + fields JSON for one entry.</summary>
    private (string Text, string Fields) Render(IndexEntry e)
    {
        string? on = On(e);
        var t = new EntryText(e, on, IndexKinds.IsArea(e.Kind) ? null : Near(e));
        if (on != null) t.F["island"] = on;
        if (e.Label != null) t.F["label"] = e.Label;
        if (Renderers.TryGetValue(e.Kind, out var render)) render(this, t);
        else
        {
            t.Sb.Append($"{DisplayName(e.Kind, e.SubType, null)} at {P(e.Anchor)}");
            t.Where();
        }
        if (e.Protect.Count > 0 && e.Kind != IndexKinds.Generator) t.F["protected"] = true;
        return (t.Sb.ToString(), Json(t.F));
    }

    private static void RenderContainer(EntryText t)
    {
        var e = t.E;
        t.Sb.Append($"{e.Kind} at {P(e.Anchor)}");
        if (e.Cells.Count > 1) { t.Sb.Append(" (double)"); t.F["double"] = true; }
        t.Where();
        if (e.Contents == null) return;
        t.Sb.Append("; has ").Append(ItemList(e.Contents, 5));
        t.F["slots_used"] = e.Contents.Count;
    }

    private static void RenderFurnace(EntryText t)
    {
        var (e, sb, f) = (t.E, t.Sb, t.F);
        sb.Append($"furnace at {P(e.Anchor)}");
        if (e.FurnaceLit) sb.Append(" (lit)");
        f["lit"] = e.FurnaceLit;
        t.Where();
        var (i, fuel, o) = e.FurnaceSlots;
        if (i is { } inp)
        {
            var res = MineClaw.Data.Smelting.ResultFor(inp.ItemId);
            sb.Append($"; last: {inp.Count} {inp.Name} -> {(res != null ? ItemName(res.OutputId) : "?")}");
            f["in"] = inp.Name;
        }
        if (o is { } outp) { sb.Append(i == null ? $"; out: {outp.Name} x{outp.Count}" : $", out {outp.Count}"); f["out"] = outp.Name; }
        if (fuel is { } fu) { sb.Append($"; fuel {fu.Name} x{fu.Count}"); f["fuel"] = fu.Name; }
    }

    private static void RenderSign(EntryText t)
    {
        var e = t.E;
        t.Sb.Append($"sign at {P(e.Anchor)} \"{e.Label}\"");
        if (e.Extra.GetValueOrDefault("labels") is string labels) { t.Sb.Append(" on ").Append(labels.Replace('_', ' ')); t.F["labels"] = labels; }
        else t.Where();
    }

    private static readonly string[] GeneratorFields = ["dig", "stand", "makes", "confirmed", "regens", "lava", "water"];

    private static void RenderGenerator(EntryText t)
    {
        var e = t.E;
        var dig = (List<int[]>)e.Extra["dig"]!;
        bool ok = e.Extra["confirmed"] is true;
        t.Sb.Append($"cobble_generator at {string.Join(",", dig[0])}");
        t.Where();
        t.Sb.Append($": makes {e.Extra["makes"]}; dig {string.Join(" ", dig.Take(3).Select(d => string.Join(",", d)))}");
        if (e.Extra["stand"] is int[] st) t.Sb.Append($" stand {string.Join(",", st)}");
        t.Sb.Append(ok ? $"; confirmed ({e.Extra["regens"]} regens)" : "; unconfirmed");
        foreach (var k in GeneratorFields) t.F[k] = e.Extra[k];
        t.F["protect"] = e.Protect.Count;
    }

    private static void RenderFarm(EntryText t)
    {
        var e = t.E;
        t.Sb.Append($"{(e.SubType ?? "").Replace('_', ' ')} farm {e.Max.X - e.Min.X + 1}x{e.Max.Z - e.Min.Z + 1} at {P(e.Min)}");
        t.Where();
        var parts = FarmParts(e);
        if (parts.Count > 0) t.Sb.Append(": ").Append(string.Join(", ", parts));
        foreach (var (k, v) in e.Extra) t.F[k] = v;
    }

    /// <summary>"farmland 6", "crops 6 (2 ripe)", "stems 1", "melon 1", "sugar cane 2 (max h2)", "on sand", "by water".</summary>
    private static List<string> FarmParts(IndexEntry e)
    {
        var x = e.Extra;
        var parts = new List<string>();
        if (x.GetValueOrDefault("farmland") is int fl) parts.Add($"farmland {fl}");
        if (x.GetValueOrDefault("crops") is int cr) parts.Add($"crops {cr} ({x["ripe"]} ripe)");
        if (x.GetValueOrDefault("stems") is int stm) parts.Add($"stems {stm}");
        if (x.GetValueOrDefault("fruit") is int fr) parts.Add($"{e.SubType} {fr}");
        if (x.GetValueOrDefault("plants") is int pl) parts.Add($"{(e.SubType ?? "").Replace('_', ' ')} {pl}" + Tallest(x));
        if (x.GetValueOrDefault("soil") is string soil) parts.Add("on " + soil);
        if (x.GetValueOrDefault("water") is int w && w > 0) parts.Add("by water");
        return parts;
    }

    private static string Tallest(Dictionary<string, object?> x) => x.GetValueOrDefault("tallest") is int h ? $" (max h{h})" : "";

    private static void RenderTree(EntryText t)
    {
        var e = t.E;
        t.Sb.Append(e.Extra["saplings"] is int sp && sp > 0 && e.Extra["logs"] is 0 ? $"tree spot at {P(e.Anchor)}" : $"tree at {P(e.Anchor)}");
        t.Where();
        t.Sb.Append($": {e.Extra["trees"]} trees, {e.Extra["logs"]} logs, {e.Extra["saplings"]} saplings");
        if (e.Extra.GetValueOrDefault("note") is string note) t.Sb.Append("; ").Append(note).Append(" (by me)");
        foreach (var (k, v) in e.Extra) t.F[k] = v;
    }

    private static void RenderUnknownStructure(EntryText t)
    {
        var e = t.E;
        var blocks = (Dictionary<string, int>)e.Extra["blocks"]!;
        t.Sb.Append($"unknown_structure {e.Max.X - e.Min.X + 1}x{e.Max.Y - e.Min.Y + 1}x{e.Max.Z - e.Min.Z + 1} at {P(e.Min)}..{P(e.Max)}");
        t.Where();
        t.Sb.Append(": ").Append(string.Join(", ", blocks.Select(kv => $"{kv.Key} {kv.Value}")));
        if (e.Extra.GetValueOrDefault("by") is string by) { t.Sb.Append($"; likely by {by}"); t.F["by"] = by; }
        if (e.Extra.GetValueOrDefault("used_by") is string ub) { t.Sb.Append($"; used by {ub}"); t.F["used_by"] = ub; }
        t.Sb.Append("; ask players what it is");
        t.F["blocks"] = blocks;
        t.F["ask"] = true;
    }

    private static readonly string[] IslandFields = ["columns", "blocks", "top", "edges"];

    private void RenderIsland(EntryText t)
    {
        var (e, sb) = (t.E, t.Sb);
        var top = (Dictionary<string, int>)e.Extra["top"]!;
        var edges = (Dictionary<string, int>)e.Extra["edges"]!;
        sb.Append($"{IslandName(e)} {e.Max.X - e.Min.X + 1}x{e.Max.Z - e.Min.Z + 1} ({e.Extra["columns"]} cols) y{e.Min.Y}-{e.Max.Y} at {P(e.Min)}..{P(e.Max)}: ");
        sb.Append(string.Join(" ", top.Select(kv => $"{kv.Key} {kv.Value}")));
        sb.Append(MineClaw.Data.Versioning.WorldMode.Current.HasVoidBelow ? "; void edges" : "; edges").Append($" N{edges["N"]} E{edges["E"]} S{edges["S"]} W{edges["W"]}");
        var has = IslandHas(e);
        if (has.Count > 0) sb.Append("; has ").Append(string.Join(", ", has));
        t.F["main"] = e.Key == _mainIsland;
        foreach (var k in IslandFields) t.F[k] = e.Extra[k];
    }

    /// <summary>"chest x2, furnace, cobble gen": the six commonest things on an island.</summary>
    private List<string> IslandHas(IndexEntry island) =>
        _entries.Values.Where(x => x.Kind is not (IndexKinds.Island or IndexKinds.Hangout or IndexKinds.Activity or IndexKinds.Sign)
                                   && _islands.OwnerOf(new BlockPos(x.Anchor.X, 0, x.Anchor.Z)) == island.Key)
            .GroupBy(x => DisplayName(x.Kind, x.SubType, null)).OrderByDescending(g => g.Count()).Take(6)
            .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key).ToList();

    private static void RenderPortal(EntryText t)
    {
        var e = t.E;
        var st = (int[])e.Extra["stand"]!;
        var fr = (List<int[]>)e.Extra["frame"]!;
        t.Sb.Append($"nether portal at {P(e.Anchor)} (frame {string.Join(",", fr[0])}..{string.Join(",", fr[1])})");
        t.Where();
        t.Sb.Append($"; step in at {string.Join(",", st)}");
        if (e.Extra["approach"] is int[] ap) t.Sb.Append($", approach {string.Join(",", ap)}");
        foreach (var (k, v) in e.Extra) t.F[k] = v;
    }

    private static void RenderHangout(EntryText t)
    {
        var e = t.E;
        t.Sb.Append($"{e.Extra["player"]} hangs out around {P(e.Anchor)}");
        if (t.On != null) t.Sb.Append(" on ").Append(t.On);
        t.Sb.Append($" (~{e.Extra["minutes"]} min, last {((DateTimeOffset)e.Extra["last"]!).ToLocalTime():MM-dd HH:mm})");
        t.F["player"] = e.Extra["player"];
        t.F["minutes"] = e.Extra["minutes"];
    }

    // ------------------------------------------------------------------ hangouts (coarse, rate-limited; never per-tick movement)

    private sealed class Dwell
    {
        public double Seconds, SumX, SumY, SumZ;
        public int Samples;
        public DateTimeOffset Last;
        public bool Seeded;
    }

    private readonly Dictionary<(string Player, int Bx, int Bz), Dwell> _dwell = new();
    private DateTimeOffset _lastSample;

    private void SamplePlayers(DateTimeOffset now)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _o.PlayerSampleSeconds));
        if (now - _lastSample < interval) return;
        double dt = _lastSample == default ? interval.TotalSeconds : Math.Min((now - _lastSample).TotalSeconds, interval.TotalSeconds * 3);
        _lastSample = now;
        int b = Math.Max(4, _o.HangoutBucket);
        foreach (var p in _world.Entities.All())
        {
            if (p.Kind != EntityKind.Player || p.Name is not { Length: > 0 } name || name.Equals(_selfName, StringComparison.OrdinalIgnoreCase)) continue;
            int bx = (int)Math.Floor(p.Position.X / b), bz = (int)Math.Floor(p.Position.Z / b);
            var d = Dwelt(name, bx, bz, p.Position, dt, now);
            if (d.Seconds < _o.HangoutMinMinutes * 60) continue;
            var anchor = new BlockPos((int)Math.Floor(d.SumX / d.Samples), (int)Math.Floor(d.SumY / d.Samples), (int)Math.Floor(d.SumZ / d.Samples));
            var e = GetOrCreate(IndexKinds.Hangout, $"{name}@{bx},{bz}", out bool created);
            if (created && !d.Seeded) SeedDwell(d, e.Key, anchor, b);
            e.Anchor = anchor;
            e.Min = new BlockPos(bx * b, anchor.Y - 2, bz * b);
            e.Max = new BlockPos(bx * b + b - 1, anchor.Y + 2, bz * b + b - 1);
            e.Extra["player"] = name;
            e.Extra["minutes"] = (int)Math.Round(d.Seconds / 60);
            e.Extra["last"] = now;
            e.MinWriteInterval = TimeSpan.FromMinutes(Math.Max(1, _o.HangoutMinWriteMinutes));
            e.Dirty = true;
        }
    }

    /// <summary>Adds one sample to the player's dwell in that bucket.</summary>
    private Dwell Dwelt(string name, int bx, int bz, Vec3d pos, double dt, DateTimeOffset now)
    {
        var key = (name, bx, bz);
        if (!_dwell.TryGetValue(key, out var d)) _dwell[key] = d = new Dwell();
        d.Seconds += dt;
        d.SumX += pos.X; d.SumY += pos.Y; d.SumZ += pos.Z;
        d.Samples++;
        d.Last = now;
        return d;
    }

    /// <summary>A hangout new this session continues the minutes its stored row had.</summary>
    private void SeedDwell(Dwell d, string key, BlockPos anchor, int b)
    {
        d.Seeded = true;
        try
        {
            var old = _sink.Near(IndexKinds.Hangout, anchor.X, anchor.Y, anchor.Z, b * 2, 10, _dim).FirstOrDefault(r => r.Key == key);
            if (old?.FieldsJson is { } json && System.Text.Json.JsonDocument.Parse(json).RootElement.TryGetProperty("minutes", out var m))
                d.Seconds += m.GetDouble() * 60;
        }
        catch { /* best effort */ }
    }

    // Activity summaries are produced by PlayerActivityTracker; nothing to flush here.
    private void FlushActivity(DateTimeOffset now) { }

    // ------------------------------------------------------------------ place lookups for the activity tracker

    /// <summary>The indexed place at or next to a position (structure/station within <paramref name="radius"/>, else the island).</summary>
    internal PlaceRef? PlaceAt(BlockPos p, double radius = 4)
    {
        lock (_pumpLock)
        {
            var e = NearestEntry(p, radius, x => x.Kind is not (IndexKinds.Island or IndexKinds.Hangout or IndexKinds.Activity or IndexKinds.Sign));
            if (e != null) return new PlaceRef(e.Kind, e.Key, DisplayName(e.Kind, e.SubType, e.Label), e.Min, e.Max);
            var i = IslandAt(p.X, p.Z);
            return i == null ? null : new PlaceRef(i.Kind, i.Key, IslandName(i), i.Min, i.Max);
        }
    }

    /// <summary>Notes a player using an unknown structure (shown in its text; the brain asks about it).</summary>
    internal void NoteStructureUse(string key, string player)
    {
        lock (_pumpLock)
        {
            if (Find(IndexKinds.UnknownStructure, key) is not { } e) return;
            if (e.Extra.GetValueOrDefault("used_by") as string == player) return;
            e.Extra["used_by"] = player;
            e.Dirty = true;
            var u = new UnknownStructure(e.Key, e.Min, e.Max, e.WrittenText ?? "");
            lock (_unknownLock) _pendingUnknown[e.Key] = u;
        }
    }

    /// <summary>Notes the player whose building activity overlaps an unknown structure.</summary>
    internal void NoteStructureBuilder(BlockPos at, string player)
    {
        lock (_pumpLock)
        {
            var e = NearestEntry(at, 2, x => x.Kind == IndexKinds.UnknownStructure);
            if (e == null || e.Extra.GetValueOrDefault("by") as string == player) return;
            e.Extra["by"] = player;
            e.Dirty = true;
        }
    }

    /// <summary>Lowest solid y of the island under column (x, z), or null.</summary>
    internal int? IslandBottom(int x, int z)
    {
        lock (_pumpLock) return IslandAt(x, z)?.Min.Y;
    }

    /// <summary>Containers the bot has opened (and therefore manages), by position.</summary>
    internal bool IsKnownContainer(BlockPos p)
    {
        lock (_pumpLock) return _stationCells.TryGetValue(p, out var e) && e.Contents != null;
    }
}

/// <summary>A named place for activity text ("cobble gen", "chest \"Iron\"", "main island").</summary>
public sealed record PlaceRef(string Kind, string Key, string Name, BlockPos Min, BlockPos Max);
