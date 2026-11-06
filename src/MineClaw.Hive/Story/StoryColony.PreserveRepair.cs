using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Story;

/// <summary>
/// Preserve zones repair themselves through the build machinery (docs/web/story-mode.md "Preserve zones"). Each reconcile pass (every
/// <see cref="ReconcileEvery"/>) compares every preserved cell with the world by <see cref="PreserveRules"/>; the cells that differ are the
/// ghosts of the zone's repair build (kind <c>preserve</c>, one per zone): a kept block missing → placed (dirt for the ground family), a
/// foreign block in its cell → cleared then placed, its block turned wrong → taken back and placed again, a foreign block in an open cell
/// → dug out (an ordered clear). From there it is Build work like any build: batches, stands, support and scaffolds, materials and craft
/// tasks, one alert per reason. A cell that matches again leaves the build (the delta's <c>removed.ghosts</c>). Cells an owner's
/// directive holds (another build's, a designation's, a quarry's columns) are skipped, and taken again as the directive leaves them once
/// it ends; a pawn's temporary block is skipped (the temporary-block rule cleans it). A cell two preserved zones share is the oldest's.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>One zone's repairs are said at most this often (the cells found between are counted into the next alert).</summary>
    public static readonly TimeSpan PreserveAlertEvery = TimeSpan.FromMinutes(10);

    /// <summary>Per zone: when its damage was last alerted, and the cells found since.</summary>
    private readonly Dictionary<string, (DateTimeOffset At, int Since)> _preserveSaid = new(StringComparer.Ordinal);

    /// <summary>Per zone: what the last pass found (the zone panel's status).</summary>
    private readonly Dictionary<string, PreserveCounts> _preserveStatus = new(StringComparer.Ordinal);

    /// <summary>One cell that differs from the snapshot: what goes there (a place) or what is dug out (a dig), and how it differs.</summary>
    private readonly record struct Damage(P3 At, int Id, int Meta, bool Dig, string How);

    /// <summary>A pass's findings for one zone.</summary>
    internal sealed class PreserveCounts
    {
        public int Missing, Foreign, Turned, Unknown, Held;
        public int Total => Missing + Foreign + Turned;
    }

    /// <summary>What one zone's pass reads: the cells directives hold, the pawns' temporary blocks, the cells an older zone repairs.</summary>
    private sealed record PreserveScan(IStoryWorld W, HashSet<P3> Held, HashSet<P3> Temps, HashSet<P3> Claimed, bool Modern, Dictionary<long, ZoneState> Quarries);

    /// <summary>Every preserved zone against the world (the reconcile pass, before the builds' own).</summary>
    private void PreservePass(IStoryWorld w)
    {
        DropOrphanRepairs();
        var zones = Zones.Where(z => z.Preserve is { On: true }).ToList();
        if (zones.Count == 0) return;
        var scan = new PreserveScan(w, DirectiveCells(), TempCellSet(), new HashSet<P3>(), Modern, Zix().Quarry);
        foreach (var z in zones)
        {
            Resync(w, z);
            var counts = new PreserveCounts();
            var damage = ScanPreserved(scan, z, counts);
            _preserveStatus[z.Id] = counts;
            SyncRepairs(z, damage, counts);
        }
    }

    /// <summary>The cells owner directives hold: other builds' cells (any status: a finished build keeps its own), the owner's dig, chop
    /// and deconstruct marks (not the colony's own cleanups), and quarries' columns from their top down.</summary>
    private HashSet<P3> DirectiveCells()
    {
        var set = new HashSet<P3>();
        foreach (var b in Builds)
            if (b.Kind != "preserve") foreach (var g in b.Ghosts) set.Add(g.At);
        foreach (var d in Designations)
        {
            if (d.Auto || d.Kind is not ("mine" or "deconstruct" or "chop")) continue;
            if (d.At is { } at) set.Add(at);
            if (d.Kind == "chop" && d.Blocks != null) set.UnionWith(d.Blocks);
        }
        return set;
    }

    /// <summary>The zone's cells that differ now (the snapshot's unknown cells filled, directive cells marked and taken again as they're left).</summary>
    private List<Damage> ScanPreserved(PreserveScan s, ZoneState z, PreserveCounts counts)
    {
        var damage = new List<Damage>();
        var snap = z.Preserve!.Data!;
        bool changed = false;
        foreach (var (key, col) in snap.Cols)
        {
            int x = ZoneCells.KX(key), cz = ZoneCells.KZ(key);
            if (!s.W.Loaded(x, cz)) { counts.Unknown += col.Count(v => v == PreserveSnap.Unknown); continue; }
            bool quarry = QuarryColumn(s, x, cz, out int quarryTop);
            for (int i = 0; i < col.Length; i++)
            {
                var p = new P3(x, snap.Lo + i, cz);
                bool held = s.Held.Contains(p) || quarry && p.Y <= quarryTop;
                changed |= ScanCell(s, z, col, i, p, held, counts, damage);
            }
        }
        if (changed) { z.Preserve.Changed(); _dirty = true; }
        return damage;
    }

    /// <summary>A quarry's column (it digs from its top down: those cells are the quarry's).</summary>
    private bool QuarryColumn(PreserveScan s, int x, int z, out int top)
    {
        top = int.MinValue;
        if (!s.Quarries.TryGetValue(ZoneCells.Key(x, z), out var q)) return false;
        top = QuarryTopOf(q);
        return true;
    }

    /// <summary>One cell: held by a directive (marked), released by one or unknown (taken now), else compared. True = the snapshot changed.</summary>
    private bool ScanCell(PreserveScan s, ZoneState z, int[] col, int i, P3 p, bool held, PreserveCounts counts, List<Damage> damage)
    {
        int v = col[i];
        if (held)
        {
            counts.Held++;
            if (v == PreserveSnap.Held) return false;
            col[i] = PreserveSnap.Held;
            return true;
        }
        if (v < 0) { col[i] = TakeCell(s.W, s.Temps, p.X, p.Y, p.Z); return true; } // unknown, or a directive's cell it left
        if (s.Temps.Contains(p) || !s.Claimed.Add(p)) return false;
        if (Differs(s, z.Kind, v, p) is { } d) { damage.Add(d); Count(counts, d.How); }
        return false;
    }

    private static void Count(PreserveCounts c, string how)
    {
        if (how == "missing") c.Missing++;
        else if (how == "turned") c.Turned++;
        else c.Foreign++;
    }

    /// <summary>How the world's block differs from the snapshot's at a cell, or null when it keeps it.</summary>
    private static Damage? Differs(PreserveScan s, string kind, int v, P3 p)
    {
        int sid = PreserveSnap.IdOf(v), smeta = PreserveSnap.MetaOf(v);
        if (PreserveRules.Ignored(sid, kind)) return null;
        var (id, meta) = s.W.Block(p.X, p.Y, p.Z);
        if (PreserveRules.Open(sid))
            return PreserveRules.Foreign(id, kind) ? new Damage(p, id, meta, true, "foreign") : null;
        if (PreserveRules.Keeps(sid, smeta, id, meta, s.Modern)) return null;
        var (rid, rmeta) = PreserveRules.Restore(sid, smeta);
        string how = PreserveRules.Open(id) || StoryBlocks.Replaceable.Contains(id) ? "missing" : Turned(rid, rmeta, id, meta) ? "turned" : "foreign";
        return new Damage(p, rid, rmeta, false, how);
    }

    /// <summary>The same block and variant, turned another way (a stair, a furnace's front, a log's axis).</summary>
    private static bool Turned(int wantId, int wantMeta, int id, int meta)
    {
        int mask = MineClaw.Bot.Blueprints.DesignFacing.VariantMask(wantId);
        return StoryDrift.Canon(id) == StoryDrift.Canon(wantId) && (meta & mask) == (wantMeta & mask);
    }

    // ------------------------------------------------------------------ the repair build

    /// <summary>The zone's repair build, or null.</summary>
    private BuildState? RepairBuildOf(ZoneState z) =>
        z.Preserve?.Build is { } bid ? Builds.FirstOrDefault(b => b.Id == bid && b.Kind == "preserve") : null;

    /// <summary>Repair builds whose zone went or stopped preserving go with it.</summary>
    private void DropOrphanRepairs()
    {
        foreach (var b in Builds.Where(b => b.Kind == "preserve").ToList())
            if (!Zones.Any(z => z.Preserve is { On: true } && z.Preserve.Build == b.Id)) RemoveBuild(b);
    }

    /// <summary>The repair build's ghosts become the cells that differ now: kept ones stay (a done one damaged again reopens), new ones
    /// join, and the ones that match again (or a directive holds now) leave, unless a pawn is at work on them.</summary>
    private void SyncRepairs(ZoneState z, List<Damage> damage, PreserveCounts counts)
    {
        var b = RepairBuildOf(z);
        if (b == null && damage.Count == 0) return;
        b ??= NewRepairBuild(z);
        var want = new Dictionary<P3, Damage>();
        foreach (var d in damage) want[d.At] = d;
        int fresh = 0;
        foreach (var g in b.Ghosts.ToList()) fresh += KeepOrDrop(b, g, want);
        foreach (var d in want.Values) { b.Ghosts.Add(RepairGhost(d)); fresh++; }
        RepairStatus(b);
        if (fresh == 0) return;
        MaterialsChanged();
        WakeIdle();
        SayDamage(z, b, counts, fresh);
    }

    /// <summary>One ghost against what differs now → 1 when it is (again) to be repaired.</summary>
    private int KeepOrDrop(BuildState b, GhostState g, Dictionary<P3, Damage> want)
    {
        if (want.Remove(g.At, out var d) && SameRepair(g, d))
        {
            if (g.State != "done") return 0;
            Reopen(b, g); // matched once, damaged again
            return 1;
        }
        if (g.State == "leased") return 0; // a pawn is at it: its report decides
        DropGhost(b, g);
        if (d.How == null) return 0;
        b.Ghosts.Add(RepairGhost(d)); // what goes there changed (a new snapshot, another block in the way)
        return 1;
    }

    private static bool SameRepair(GhostState g, Damage d) =>
        g.Remove == d.Dig && (d.Dig ? g.Id == d.Id : g.Id == d.Id && g.Meta == d.Meta);

    /// <summary>A ghost leaves the repair build: its failures are forgotten and the web is told (<c>removed.ghosts</c>).</summary>
    private void DropGhost(BuildState b, GhostState g)
    {
        ForgetCellFailures(b, g);
        _leases.Remove($"g:{b.Id}|{g.At}");
        b.Ghosts.Remove(g);
        Removed("ghost", $"{b.Id}|{g.At}");
        b.Rev = Touch();
    }

    /// <summary>A zone's repair build: first in the queue (keeping what stands comes before new work), never removed by hand.</summary>
    private BuildState NewRepairBuild(ZoneState z)
    {
        var b = new BuildState
        {
            Id = NextId("b"), Kind = "preserve", Label = $"repairs: {ZoneTitle(z)}", Material = "", Created = Now, Status = "done",
            Order = Builds.Count == 0 ? 0 : Builds.Min(x => x.Order) - 1, Rev = Touch(),
        };
        Builds.Add(b);
        z.Preserve!.Build = b.Id;
        return b;
    }

    /// <summary>A zone's name, else its kind and id ("growing zone z3").</summary>
    private static string ZoneTitle(ZoneState z) => z.Name is { Length: > 0 } n ? n : $"{(z.Kind == "farm" ? "growing zone" : z.Kind)} {z.Id}";

    /// <summary>The ghost for one cell: a dig (an ordered clear), the block to place (unsupported when pawns can't place it: shown, said,
    /// never a job), or a second half that comes with its first.</summary>
    private GhostState RepairGhost(Damage d)
    {
        var g = new GhostState { At = d.At, Id = d.Id, Meta = d.Meta, Remove = d.Dig, Rev = Touch() };
        if (d.Dig || World is not { } w) return g;
        if (MineClaw.Bot.Blueprints.DesignFacing.SecondHalf(d.Id, d.Meta))
        {
            g.Job = false; // a door's top, a bed's head: placed with its other half
            return g;
        }
        var (item, why) = StoryConstruction.PlacingItem(w.Data, d.Id, d.Meta);
        g.Item = item;
        if (item == null) { g.Job = false; g.State = "unsupported"; g.Reason = why; }
        return g;
    }

    /// <summary>The build is done with nothing to repair, queued again when something is (paused stays paused).</summary>
    private void RepairStatus(BuildState b)
    {
        bool open = b.Ghosts.Any(g => g.Job && g.State is "todo" or "leased" or "blocked");
        string status = b.Status == "paused" ? "paused" : !open ? "done" : b.Status == "done" ? "queued" : b.Status;
        if (status == b.Status) return;
        b.Status = status;
        if (status == "done") b.Reason = null;
        b.Rev = Touch();
    }

    /// <summary>One alert per zone per <see cref="PreserveAlertEvery"/> with the counts; the cells found between are said with the next.</summary>
    private void SayDamage(ZoneState z, BuildState b, PreserveCounts c, int fresh)
    {
        var parts = new List<string>();
        if (c.Missing > 0) parts.Add($"{c.Missing} missing");
        if (c.Foreign > 0) parts.Add($"{c.Foreign} foreign");
        if (c.Turned > 0) parts.Add($"{c.Turned} turned");
        var text = $"Preserved zone {ZoneTitle(z)}: {c.Total} block{(c.Total == 1 ? "" : "s")} changed ({string.Join(", ", parts)}): queued for repair ({b.Id})";
        if (_preserveSaid.TryGetValue(z.Id, out var said) && Now - said.At < PreserveAlertEvery)
        {
            _preserveSaid[z.Id] = (said.At, said.Since + fresh);
            _log.LogInformation("story {Colony}: {Text}", Id, text);
            return;
        }
        if (said.Since > 0) text += $" ({said.Since} more found since the last alert)";
        _preserveSaid[z.Id] = (Now, 0);
        Alert("warn", text);
    }

    // ------------------------------------------------------------------ the zone's state

    /// <summary>
    /// The zone's <c>preserve</c> object: <c>{on, below, above, y1, y2, cells, taken_at, build, unknown, held, damage:{missing, foreign,
    /// turned}, to_repair, status}</c> ("intact", "12 blocks to repair: 9 missing, 3 foreign; waiting for 6 dirt").
    /// </summary>
    internal JsonObject? PreserveJson(ZoneState z)
    {
        if (z.Preserve is not { } p) return null;
        var (lo, hi) = PreserveRange(z, p);
        var c = _preserveStatus.GetValueOrDefault(z.Id) ?? new PreserveCounts();
        var b = RepairBuildOf(z);
        return new JsonObject
        {
            ["on"] = p.On, ["below"] = p.Below, ["above"] = p.Above, ["y1"] = lo, ["y2"] = hi, ["cells"] = p.Data?.Cells ?? 0,
            ["taken_at"] = p.TakenAt, ["build"] = b?.Id, ["unknown"] = c.Unknown, ["held"] = c.Held,
            ["damage"] = new JsonObject { ["missing"] = c.Missing, ["foreign"] = c.Foreign, ["turned"] = c.Turned },
            ["to_repair"] = c.Total, ["status"] = PreserveStatus(c, b),
        };
    }

    private static string PreserveStatus(PreserveCounts c, BuildState? b)
    {
        if (c.Total == 0) return c.Unknown > 0 ? $"intact ({c.Unknown} blocks not seen yet)" : "intact";
        var parts = new List<string>();
        if (c.Missing > 0) parts.Add($"{c.Missing} missing");
        if (c.Foreign > 0) parts.Add($"{c.Foreign} foreign");
        if (c.Turned > 0) parts.Add($"{c.Turned} turned");
        var s = $"{c.Total} block{(c.Total == 1 ? "" : "s")} to repair: {string.Join(", ", parts)}";
        if (b is { Status: "paused" }) return s + "; repairs paused";
        return b is { Status: "blocked" or "waiting", Reason: { } why } ? $"{s}; {RepairWait(why)}" : s;
    }

    /// <summary>A build's held reason as the zone says it ("need 6 dirt" → "waiting for 6 dirt").</summary>
    private static string RepairWait(string why) => why.StartsWith("need ", StringComparison.Ordinal) ? "waiting for " + why[5..] : why;
}
