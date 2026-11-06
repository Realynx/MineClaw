using System.Text.Json.Nodes;
using MineClaw.World;

namespace MineClaw.Hive.Story;

/// <summary>
/// Preserve zones (docs/web/story-mode.md "Preserve zones"; Fox 11-06: "A zone that protects the current state of the blocks, and it can be
/// a three-dimensional zone. You select it 2D, and then in the settings you can change how deep it goes … if a creeper blows up or
/// something changes, one of the pawns gets queued to fix it to the original state. I want to be able to toggle it on for any other zone
/// as well … my wheat field has taken some hits — craters from creepers"). A zone of kind <c>preserve</c> (an overlay layer), or any other
/// zone but a mine zone with Preserve switched on, keeps a snapshot of every block from <see cref="PreserveState.Below"/> under its painted
/// level to <see cref="PreserveState.Above"/> over it. The snapshot is a build design: the cells that differ now are the ghosts of the
/// zone's repair build (StoryColony.PreserveRepair.cs), placed, cleared and turned by the build machinery. Owner directives win: a build's,
/// a designation's or a quarry's cells are theirs while they stand and the zone keeps what they leave. Pawns never dig a preserved block
/// for other work (the dig filter handed to their bodies); only the repairs' ordered clears do.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>The most a Preserve setting reaches under or over the zone's painted level.</summary>
    public const int PreserveMaxRange = 32;
    /// <summary>The most cells (columns × layers) one zone keeps.</summary>
    public const long PreserveMaxCells = 262_144;

    /// <summary><c>{op:"preserve", id, on?, below?, above?, retake?}</c> → <c>{id, preserve, warnings?}</c>.</summary>
    private (JsonNode? Result, string? Error) PreserveOp(string? id, JsonObject? body)
    {
        if (World is not { } w) return (null, "no world view yet");
        lock (_lock)
        {
            if (Zones.FirstOrDefault(z => z.Id == id) is not { } z) return (null, "id: no such zone");
            if (z.Kind == "mine") return (null, "a mine zone digs its blocks: it can't preserve them (paint a Preserve zone beside it instead)");
            var (on, below, above, retake, perr) = PreserveArgs(z, body);
            if (perr != null) return (null, perr);
            if (on && PreserveTooBig(z, below, above) is { } big) return (null, big);
            var warnings = new List<string>();
            SetPreserve(w, z, on, below, above, retake, warnings);
            var o = new JsonObject { ["id"] = z.Id, ["preserve"] = PreserveJson(z) };
            AddWarnings(o, warnings);
            return (o, null);
        }
    }

    /// <summary>The request's settings over the zone's own (on by default, its range kept); an error for a bad value.</summary>
    private static (bool On, int Below, int Above, bool Retake, string? Error) PreserveArgs(ZoneState z, JsonObject? body)
    {
        var cur = z.Preserve;
        bool on = Bool(body, "on") ?? true;
        bool retake = Bool(body, "retake") ?? false;
        var (below, berr) = RangeArg(body, "below", cur?.Below ?? 1);
        var (above, aerr) = RangeArg(body, "above", cur?.Above ?? 3);
        if (z.Kind == "preserve" && !on) return (on, below, above, retake, "a Preserve zone is always on: erase or remove it instead");
        return (on, below, above, retake, berr ?? aerr);
    }

    private static bool? Bool(JsonObject? body, string key) => body?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>A whole number 0..<see cref="PreserveMaxRange"/>, else the default.</summary>
    private static (int Value, string? Error) RangeArg(JsonObject? body, string key, int dflt)
    {
        if (body?[key] is not { } n) return (dflt, null);
        if (n is JsonValue v && v.TryGetValue<int>(out var i) && i is >= 0 and <= PreserveMaxRange) return (i, null);
        return (dflt, $"{key}: a whole number 0..{PreserveMaxRange}");
    }

    /// <summary>Too many cells for one zone's snapshot: said with the numbers.</summary>
    private static string? PreserveTooBig(ZoneState z, int below, int above)
    {
        long cells = (long)z.Cells.Count * (z.Area.Y2 + above - (z.Area.Y1 - below) + 1);
        return cells > PreserveMaxCells
            ? $"{cells} blocks to keep: at most {PreserveMaxCells} (fewer columns, or less depth or height)"
            : null;
    }

    /// <summary>Switches the zone's Preserve on (a new snapshot when it was off or retaken; the range's new cells taken now) or off (the
    /// snapshot and the repair build go).</summary>
    private void SetPreserve(IStoryWorld w, ZoneState z, bool on, int below, int above, bool retake, List<string> warnings)
    {
        if (!on) { PreserveOff(z); return; }
        var p = z.Preserve ??= new PreserveState();
        bool fresh = !p.On || p.Data == null || retake;
        p.On = true;
        p.Below = below;
        p.Above = above;
        if (fresh) { p.Data = null; p.TakenAt = Now; ForgetRepairs(z); }
        Resync(w, z);
        PreserveWarnings(z, warnings);
        z.Rev = Touch();
        WakeIdle();
    }

    private void PreserveOff(ZoneState z)
    {
        if (z.Preserve == null) return;
        if (z.Preserve.Build is { } bid && Builds.FirstOrDefault(b => b.Id == bid) is { } b) RemoveBuild(b);
        z.Preserve = null;
        _preserveSaid.Remove(z.Id);
        _preserveStatus.Remove(z.Id);
        z.Rev = Touch();
    }

    /// <summary>A new snapshot: the repair build's cells start over (what differs from the new one is found on the next pass).</summary>
    private void ForgetRepairs(ZoneState z)
    {
        if (RepairBuildOf(z) is not { } b) return;
        foreach (var g in b.Ghosts.Where(g => g.State != "leased").ToList()) DropGhost(b, g);
    }

    /// <summary>The zone's y range kept: from its painted level's bottom − below to its top + above, inside the world.</summary>
    private (int Lo, int Hi) PreserveRange(ZoneState z, PreserveState p)
    {
        int top = (World?.Height ?? 256) - 1;
        return (Math.Clamp(z.Area.Y1 - p.Below, 0, top), Math.Clamp(z.Area.Y2 + p.Above, 0, top));
    }

    /// <summary>
    /// The snapshot follows the zone's cells and range: cells it held keep their values, new ones are taken from the world now (unknown
    /// while their column isn't loaded), cells that left are dropped. Nothing changes when the cells and range are the same.
    /// </summary>
    private void Resync(IStoryWorld w, ZoneState z)
    {
        if (z.Preserve is not { On: true } p) return;
        var (lo, hi) = PreserveRange(z, p);
        var old = p.Data;
        if (old != null && old.Lo == lo && old.Hi == hi && old.Cols.Count == z.Cells.Count && z.Cells.Keys.All(old.Cols.ContainsKey)) return;
        var cols = new Dictionary<long, int[]>(z.Cells.Count);
        var temps = TempCellSet();
        foreach (var k in z.Cells.Keys) cols[k] = ResyncColumn(w, temps, k, lo, hi, old);
        p.Data = new PreserveSnap(lo, hi, cols);
        p.TakenAt ??= Now;
        _dirty = true;
    }

    /// <summary>One column of the new snapshot: the old values where it had them, the world's block elsewhere.</summary>
    private static int[] ResyncColumn(IStoryWorld w, HashSet<P3> temps, long key, int lo, int hi, PreserveSnap? old)
    {
        int x = ZoneCells.KX(key), z = ZoneCells.KZ(key);
        var col = new int[hi - lo + 1];
        var had = old != null && old.Cols.TryGetValue(key, out var oc) ? oc : null;
        bool loaded = w.Loaded(x, z);
        for (int y = lo; y <= hi; y++)
        {
            bool kept = had != null && y >= old!.Lo && y <= old.Hi;
            col[y - lo] = kept ? had![y - old!.Lo] : loaded ? TakeCell(w, temps, x, y, z) : PreserveSnap.Unknown;
        }
        return col;
    }

    /// <summary>The block now, as the snapshot keeps it: a pawn's temporary block is air (it is cleaned, never kept).</summary>
    private static int TakeCell(IStoryWorld w, HashSet<P3> temps, int x, int y, int z)
    {
        if (temps.Contains(new P3(x, y, z))) return 0;
        var (id, meta) = w.Block(x, y, z);
        return PreserveSnap.Pack(id, meta);
    }

    /// <summary>The cells of the pawns' registered temporary blocks.</summary>
    private HashSet<P3> TempCellSet() => Meta.TempBlocks is { Count: > 0 } t ? t.Select(x => x.At).ToHashSet() : [];

    /// <summary>After a paint, an erase or a removal: each preserved zone's snapshot follows its cells; a merged-in zone's snapshot is kept
    /// for its cells; repair builds of zones that went (or stopped preserving) go.</summary>
    private void PreserveZonesChanged(ZonePaintResult r, Dictionary<string, PreserveSnap>? before)
    {
        if (World is not { } w) return;
        foreach (var z in r.Changed.Where(z => z.Preserve is { On: true }))
        {
            Resync(w, z);
            foreach (var (into, from) in r.Merged.Where(m => m.Into == z.Id))
                foreach (var f in from)
                    if (before?.GetValueOrDefault(f) is { } fs) MergeSnapshot(z.Preserve!, fs);
        }
        DropOrphanRepairs();
    }

    /// <summary>The preserved zones' snapshots by id (before a paint: a zone merged into another gives its cells' values over).</summary>
    private Dictionary<string, PreserveSnap> PreserveBefore() =>
        Zones.Where(z => z.Preserve is { On: true, Data: not null }).ToDictionary(z => z.Id, z => z.Preserve!.Data!);

    /// <summary>A merged-in zone's values for the cells both snapshots hold (the merged zone took them from the world a moment ago).</summary>
    private static void MergeSnapshot(PreserveState into, PreserveSnap from)
    {
        if (into.Data is not { } d) return;
        foreach (var (k, col) in d.Cols)
        {
            if (!from.Cols.TryGetValue(k, out var fc)) continue;
            for (int y = Math.Max(d.Lo, from.Lo); y <= Math.Min(d.Hi, from.Hi); y++) col[y - d.Lo] = fc[y - from.Lo];
        }
        into.Changed();
    }

    /// <summary>A new Preserve zone (kind <c>preserve</c>): its range from the paint (<c>below</c>, <c>above</c>), checked against its size.</summary>
    private static string? PreserveZoneSettings(ZoneState template, ZoneCells cells, Area a, JsonObject? body)
    {
        var (below, berr) = RangeArg(body, "below", 1);
        var (above, aerr) = RangeArg(body, "above", 3);
        if ((berr ?? aerr) is { } err) return err;
        long n = (long)cells.Count * (a.Y2 + above - (a.Y1 - below) + 1);
        if (n > PreserveMaxCells) return $"{n} blocks to keep: at most {PreserveMaxCells} (fewer columns, or less depth or height)";
        template.Preserve = new PreserveState { Below = below, Above = above, TakenAt = null };
        return null;
    }

    /// <summary>The paint's answer: a preserved zone's overlap with builds, quarries and designations; a quarry's with preserved zones.</summary>
    private void PaintedPreserve(ZonePaintResult painted, List<string> warnings)
    {
        if (Zones.FirstOrDefault(z => z.Id == painted.Id) is not { } z) return;
        if (z.Preserve is { On: true }) PreserveWarnings(z, warnings);
        QuarryOverPreserved(z, warnings);
    }

    // ------------------------------------------------------------------ owner directives win: said when they meet

    /// <summary>What a newly preserved zone shares with builds, quarries and designations (theirs while they stand).</summary>
    private void PreserveWarnings(ZoneState z, List<string> warnings)
    {
        foreach (var b in Builds.Where(b => b.Kind != "preserve"))
            if (b.Ghosts.Count(g => PreservedBy(z, g.At)) is var n and > 0)
                warnings.Add($"overlaps build {b.Id} ({b.Label}): {n} of its blocks are the build's while it stands, then kept as it leaves them");
        foreach (var q in Zones.Where(IsQuarry))
            if (q.Cells.Overlaps(z.Cells))
                warnings.Add($"overlaps quarry {ZoneName(q)}: the quarry digs its columns there (not repaired)");
        int d = Designations.Count(x => !x.Auto && x.At is { } at && PreservedBy(z, at));
        if (d > 0) warnings.Add($"{d} designation{(d == 1 ? "" : "s")} in it: done as asked, then kept as they leave it");
    }

    /// <summary>The zone's snapshot holds the cell.</summary>
    private static bool PreservedBy(ZoneState z, P3 p) => z.Preserve is { On: true, Data: { } d } && d.At(p.X, p.Y, p.Z) != null;

    /// <summary>The preserved zone holding the cell (the oldest), or null.</summary>
    private ZoneState? PreservedAt(P3 p) => Zones.FirstOrDefault(z => PreservedBy(z, p));

    /// <summary>New builds over preserved cells: said in the answer (the build's while it stands).</summary>
    private void BuildsOverPreserved(List<BuildState> made, List<string> warnings)
    {
        if (!Zones.Any(z => z.Preserve is { On: true })) return;
        foreach (var b in made)
        {
            var hit = b.Ghosts.Select(g => PreservedAt(g.At)).Where(z => z != null).ToList();
            if (hit.Count > 0)
                warnings.Add($"build {b.Id}: {hit.Count} block{(hit.Count == 1 ? "" : "s")} lie in preserved zone {ZoneName(hit[0]!)}: the build's while it stands, then kept as it leaves them");
        }
    }

    /// <summary>New designations over preserved cells: said in the answer.</summary>
    private void DesignationsOverPreserved(List<DesignationState> made, JsonObject answer)
    {
        if (!Zones.Any(z => z.Preserve is { On: true })) return;
        var hit = made.Where(d => d.At is { } at && PreservedAt(at) != null).ToList();
        if (hit.Count == 0) return;
        var z = PreservedAt(hit[0].At!.Value)!;
        answer["warnings"] = new JsonArray((JsonNode)$"{hit.Count} target{(hit.Count == 1 ? "" : "s")} lie in preserved zone {ZoneName(z)}: done as asked, then kept as they leave it");
    }

    /// <summary>A quarry painted (or switched on) over preserved zones: said in the answer.</summary>
    private void QuarryOverPreserved(ZoneState q, List<string> warnings)
    {
        if (!IsQuarry(q)) return;
        foreach (var z in Zones.Where(z => z.Preserve is { On: true } && z.Cells.Overlaps(q.Cells)))
            warnings.Add($"overlaps preserved zone {ZoneName(z)}: the quarry digs its columns there (not repaired)");
    }

    // ------------------------------------------------------------------ the dig filter: pawns never dig a preserved block for other work

    private Func<BlockPos, int, bool>? _preserveFilter;
    private string _preserveKey = "";

    /// <summary>
    /// The preserved blocks as an immutable predicate for the pawn bodies (pos, block id → keep it): a kept snapshot cell that holds its
    /// block. Their digs refuse it (a quarry's, a pillar's, a clutch's or a path's), except a repair's ordered clear. A cell an owner's
    /// directive holds, an open cell and a changed block are not kept. Rebuilt when a snapshot is replaced; null with none. Under <c>_lock</c>.
    /// </summary>
    internal Func<BlockPos, int, bool>? PreserveFilter()
    {
        var kept = Zones.Where(z => z.Preserve is { On: true, Data: not null }).Select(z => (Snap: z.Preserve!.Data!, z.Kind)).ToArray();
        var key = string.Join(";", kept.Select(k => $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Snap)}:{k.Kind}"));
        if (key == _preserveKey) return _preserveFilter;
        _preserveKey = key;
        bool modern = Modern;
        _preserveFilter = kept.Length == 0 ? null : (pos, id) => KeptIn(kept, pos, id, modern);
        return _preserveFilter;
    }

    private static bool KeptIn((PreserveSnap Snap, string Kind)[] kept, BlockPos pos, int id, bool modern)
    {
        foreach (var (snap, kind) in kept)
        {
            if (snap.At(pos.X, pos.Y, pos.Z) is not (>= 0 and var v)) continue;
            int sid = PreserveSnap.IdOf(v);
            if (PreserveRules.Open(sid) || PreserveRules.Ignored(sid, kind)) continue;
            if (PreserveRules.KeepsId(sid, id, modern)) return true;
        }
        return false;
    }
}
