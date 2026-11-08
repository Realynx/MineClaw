using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MineClaw.World;

namespace MineClaw.Hive.Story;

/// <summary>
/// Builds reconcile with the world (docs/web/story-mode.md "Builds reconcile with the world"; Fox 11-05: "We're not even detecting the
/// stairs … rotated the wrong way … it's saying there's no way to place blocks, but there definitely is"). Every few seconds each
/// build cell is compared with the live block (id and the meta bits the placement decides, as <see cref="Matches"/> does): the design's
/// block → done; air → to place; its block turned another way → misrotated (taken back and placed again when it can be); another block
/// (natural stone or dirt, a block put there by mistake) → to clear first. A cell once done that no longer matches reopens, and its
/// build with it. The support rule is the pawn client's own (<see cref="PlaceRules.IsSupport"/>), so a cell is offered once something
/// beside it can be clicked; a build's cells over another build's unplaced ones wait for those, and a truly floating cell gets a
/// scaffold block under it that is dug again once it stands.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>How often every build's cells are compared with the world.</summary>
    public static readonly TimeSpan ReconcileEvery = TimeSpan.FromSeconds(5);

    private DateTimeOffset _reconciledAt = DateTimeOffset.MinValue;

    /// <summary>Done cells that didn't match on the last pass (<c>build|x,y,z</c>): reopened when the next pass agrees (the view's copy
    /// of a fresh placement can come a moment after the pawn's report).</summary>
    private HashSet<string> _driftSeen = new(StringComparer.Ordinal);

    /// <summary>Blocked-cell alerts said per build and reason (coordinates left out): a hundred cells failing alike are one alert.</summary>
    private readonly HashSet<string> _cellAlerted = new(StringComparer.Ordinal);

    /// <summary>What a pass found changed in one build.</summary>
    private struct Drift
    {
        public int Turned, Other, Missing;
        public readonly int Total => Turned + Other + Missing;
    }

    private void ReconcileTick()
    {
        if (World is not { } w || Now - _reconciledAt < ReconcileEvery) return;
        _reconciledAt = Now;
        Reconcile(w);
    }

    /// <summary>One pass over every unpaused build (done ones too: a finished house that changed reopens).</summary>
    internal void Reconcile(IStoryWorld w)
    {
        BuildConflictsPass();
        PreservePass(w); // preserved zones: their changed cells become their repair builds' ghosts (StoryColony.PreserveRepair.cs)
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var temps = TempCellSet();
        foreach (var b in Builds.ToList())
            if (b.Status != "paused") ReconcileBuild(w, b, seen, temps);
        _driftSeen = seen;
    }

    private void ReconcileBuild(IStoryWorld w, BuildState b, HashSet<string> seen, HashSet<P3> temps)
    {
        var drift = new Drift();
        foreach (var g in b.Ghosts)
        {
            if (BuildConflicted(g) || !w.Loaded(g.At.X, g.At.Z) || QuarryHeld(b, g)) continue;
            if (ReplacementBuild(b) && temps.Contains(g.At)) continue;
            if (ReplacementAir(b, g) && g.State != "leased")
            {
                ReconcileAir(w, b, g, seen, ref drift);
            }
            else if (Reconcilable(g)) ReconcileCell(w, b, g, seen, ref drift);
        }
        ConflictStatus(b); // a quarry digs what is left (StoryColony.QuarryConflicts.cs)
        if (drift.Total > 0) Reopened(b, drift);
        else if (b.Status is not ("done" or "paused") && AllBuilt(b)) { b.Status = "done"; b.Reason = null; b.Rev = Touch(); }
    }

    /// <summary>Every cell shows the design (built by hand, or the view caught up), or is left misrotated.</summary>
    private static bool AllBuilt(BuildState b) => b.Ghosts.Any(g => g.Job) && b.Ghosts.Where(g => g.Job).All(g => g.State is "done" or "misrotated");

    /// <summary>A placed block of the design (not a dig, a pour, a second half, one at work now or one pawns can't place).</summary>
    private static bool Reconcilable(GhostState g) =>
        g.Job && !g.Remove && !StoryConstruction.IsLiquid(g) && g.State is not ("leased" or "unsupported");

    /// <summary>A canceled builder's last atomic placement can arrive after replacement was queued. Empty-room ownership catches
    /// that late block, but never competes with a pawn's registered temporary access or a newer construction directive.</summary>
    private void ReconcileAir(IStoryWorld w, BuildState b, GhostState g, HashSet<string> seen, ref Drift drift)
    {
        var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z);
        if (id == 0) { Settled(b, g); return; }
        if (g.State == "done")
        {
            var key = $"{b.Id}|{g.At}";
            if (!_driftSeen.Contains(key)) { seen.Add(key); return; }
            Reopen(b, g);
            drift.Other++;
        }
        if (g.Id != id || g.Meta != meta) { g.Id = id; g.Meta = meta; g.Rev = Touch(); }
        if (StoryBlocks.Undiggable.Contains(id))
        {
            string why = "cannot clear " + BlockLabel(w, id);
            if (g.State != "blocked" || g.Reason != why) { g.State = "blocked"; g.Reason = why; g.Rev = Touch(); }
        }
        else if (g.State == "blocked" && g.Reason?.StartsWith("cannot clear ", StringComparison.Ordinal) == true) Reopen(b, g);
    }

    private void ReconcileCell(IStoryWorld w, BuildState b, GhostState g, HashSet<string> seen, ref Drift drift)
    {
        var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z);
        if (Matches(g, id, meta, Modern)) { Settled(b, g); return; }
        if (g.State == "done") { DoneDrifted(b, g, id, meta, seen, ref drift); return; }
        // Recover saved dependency failures from older runtimes; the support-aware picker decides when they are ready.
        if (g.State == "blocked" && g.Reason is { } reason && NoFaceYet(reason))
        {
            Reopen(b, g);
            if (b.Status == "blocked") { b.Status = "queued"; b.Reason = null; b.Rev = Touch(); }
            else if (b.Reason is { } held && NoFaceYet(held)) { b.Reason = null; b.Rev = Touch(); }
        }
        if (g.State == "misrotated" && StoryBlocks.Replaceable.Contains(id)) Reopen(b, g); // its block is gone: placed anew
        MarkHolds(w, g, id, meta);
    }

    /// <summary>The cell shows the design's block: done (built by hand, or the view caught up), nothing to clear.</summary>
    private void Settled(BuildState b, GhostState g)
    {
        SetHolds(g, null, null);
        if (g.State != "done") CellDone(b, g);
    }

    /// <summary>A done cell that no longer matches: seen twice in a row, it reopens (a block turned wrong that holds items is said instead).</summary>
    private void DoneDrifted(BuildState b, GhostState g, int id, int meta, HashSet<string> seen, ref Drift drift)
    {
        var key = $"{b.Id}|{g.At}";
        if (!_driftSeen.Contains(key)) { seen.Add(key); return; }
        bool turned = WrongTurn(g, id, meta, Modern);
        if (turned) drift.Turned++;
        else if (StoryBlocks.Replaceable.Contains(id)) drift.Missing++;
        else drift.Other++;
        Reopen(b, g);
        if (turned && KeepsItems(id)) Misrotated(b, g, meta); // a chest or furnace is never broken open for its rotation
        else if (World is { } w) MarkHolds(w, g, id, meta);
    }

    /// <summary>The cell is to be built again: todo, its failures, rest and take-back forgotten; a second half (a door's top) with it.</summary>
    private void Reopen(BuildState b, GhostState g)
    {
        ForgetCellFailures(b, g);
        g.State = "todo";
        g.Rev = Touch();
        foreach (var o in b.Ghosts)
            if (!o.Job && o.State == "done" && Math.Abs(o.At.X - g.At.X) + Math.Abs(o.At.Y - g.At.Y) + Math.Abs(o.At.Z - g.At.Z) == 1)
            { o.State = "todo"; o.Rev = g.Rev; }
    }

    /// <summary>A build some of whose finished cells changed: queued again and said once.</summary>
    private void Reopened(BuildState b, Drift d)
    {
        if (b.Status is "done" or "blocked") { b.Status = "queued"; b.Reason = null; }
        b.Rev = Touch();
        MaterialsChanged();
        WakeIdle();
        SayReopened(b, d);
    }

    /// <summary>How often one build's reopening is alerted; between, the cells are counted and said with the next alert.</summary>
    public static readonly TimeSpan ReopenAlertEvery = TimeSpan.FromMinutes(10);

    /// <summary>Per build: when its reopening was last alerted, and the cells reopened since.</summary>
    private readonly Dictionary<string, (DateTimeOffset At, int Since)> _reopenSaid = new(StringComparer.Ordinal);

    /// <summary>One alert per build per <see cref="ReopenAlertEvery"/> (live 11-05: ×22 in 7 minutes), with a running count of the rest.</summary>
    private void SayReopened(BuildState b, Drift d)
    {
        var parts = new List<string>();
        if (d.Turned > 0) parts.Add($"{d.Turned} turned the wrong way");
        if (d.Other > 0) parts.Add($"{d.Other} hold another block");
        if (d.Missing > 0) parts.Add($"{d.Missing} missing");
        var text = $"Build {b.Id} ({b.Label}): {d.Total} block{(d.Total == 1 ? "" : "s")} no longer match the design ({string.Join(", ", parts)}): reopened";
        if (_reopenSaid.TryGetValue(b.Id, out var said) && Now - said.At < ReopenAlertEvery)
        {
            _reopenSaid[b.Id] = (said.At, said.Since + d.Total);
            _log.LogInformation("story {Colony}: {Text}", Id, text);
            return;
        }
        if (said.Since > 0) text += $" ({said.Since} more reopened since the last alert)";
        _reopenSaid[b.Id] = (Now, 0);
        Alert("warn", text);
    }

    /// <summary>What an open cell holds when it isn't the design's block: another block (clear) or its block turned wrong (misrotated).</summary>
    private void MarkHolds(IStoryWorld w, GhostState g, int id, int meta)
    {
        if (StoryBlocks.Replaceable.Contains(id) || g.State is not ("todo" or "blocked")) { SetHolds(g, null, null); return; }
        if (WrongTurn(g, id, meta, Modern))
        {
            string Words(int m) => BlockOrientation.Describe(g.Id, m, Modern) ?? $"meta {m}";
            SetHolds(g, "misrotated", $"placed {Words(meta)}, wants {Words(g.Meta)}");
            return;
        }
        SetHolds(g, "clear", $"holds {BlockLabel(w, id)}: dug out first");
    }

    private void SetHolds(GhostState g, string? kind, string? words)
    {
        if (g.Holds == kind && g.HoldsWords == words) return;
        g.Holds = kind;
        g.HoldsWords = words;
        g.Rev = Touch();
    }

    // ------------------------------------------------------------------ support: the client's own rule

    /// <summary>A block a placement can click against, by the pawn client's rule (<see cref="PlaceRules.IsSupport"/>: anything with a
    /// collision box that doesn't open when clicked: stairs, slabs, fences, panes, glass and leaves too), in the colony's view.</summary>
    private bool Clickable(P3 p) => World is { } w && PlaceRules.IsSupport(AccessOf(w), new BlockPos(p.X, p.Y, p.Z));

    /// <summary>Something beside the cell can be clicked now (the client won't say "nothing solid on any side").</summary>
    private bool HasSupport(P3 p) => StoryConstruction.Supported(p, Clickable);

    /// <summary>A seed a pawn can start on: a dig, a pour, or a block with something to place against.</summary>
    private bool SeedReady(GhostState g) => g.Remove || StoryConstruction.IsLiquid(g) || HasSupport(g.At);

    /// <summary>
    /// None of the level's open cells has anything to place against. When one of them touches a cell another build (or another pawn's
    /// batch) is still to place, they wait for it (a house's planks over its unbuilt stone footing). Else the level floats: the nearest
    /// cell with a spot beside it (under it first) that can hold a scaffold block → (cell, spot); null = it waits.
    /// </summary>
    private (GhostState Seed, P3 Spot)? FloatingSeed(BuildState b, List<GhostState> open, P3 from)
    {
        var pending = PendingPlaceCells(b);
        foreach (var g in open)
            foreach (var n in Neighbours(g.At))
                if (pending.Contains(n)) return null;
        foreach (var g in open.Where(g => !g.Remove).OrderBy(g => g.At.Y).ThenBy(g => g.At.Dist(from)))
            if (ScaffoldSpot(g.At) is { } spot) return (g, spot);
        return null;
    }

    /// <summary>The scaffold spot when this seed is the floating level's (<see cref="FloatingSeed"/>), else null.</summary>
    private static P3? ScaffoldOf(GhostScan scan, GhostState seed) => scan.Scaffold is { } sc && sc.Seed == seed.At ? sc.Spot : null;

    /// <summary>Cells still to be placed that will give support once in: other builds' open ones, and this build's leased ones.</summary>
    private HashSet<P3> PendingPlaceCells(BuildState b)
    {
        var set = new HashSet<P3>();
        foreach (var o in Builds)
        {
            if (o.Status is "done") continue;
            foreach (var g in o.Ghosts)
                if (g.Job && !g.Remove && !Conflicted(g) && (o == b ? g.State == "leased" : g.State is "todo" or "leased" or "blocked")) set.Add(g.At);
        }
        return set;
    }

    /// <summary>Where a scaffold block can go for a floating cell: an open spot beside it (under it first), no build's cell, never on a
    /// container's lid or a workstation (it would shut it), with something to click against itself.</summary>
    private P3? ScaffoldSpot(P3 cell)
    {
        if (World is not { } w) return null;
        var cells = Builds.SelectMany(b => b.Ghosts).Select(g => g.At).ToHashSet();
        foreach (var n in Neighbours(cell))
        {
            var (id, _) = w.Block(n.X, n.Y, n.Z);
            if (id == 0 && w.Loaded(n.X, n.Z) && !cells.Contains(n) && !OnALid(w, n) && HasSupport(n)) return n;
        }
        return null;
    }

    /// <summary>Containers and workstations a temporary block never stands on: a chest under a block won't open.</summary>
    internal static readonly HashSet<int> LidBlocks = [54, 146, 130, 61, 62, 58, 23, 158, 154, 117, 116, 145];

    private static bool OnALid(IStoryWorld w, P3 n) => LidBlocks.Contains(w.Block(n.X, n.Y - 1, n.Z).Id);

    /// <summary>The six neighbours, under first.</summary>
    private static IEnumerable<P3> Neighbours(P3 p)
    {
        yield return p.Add(0, -1, 0);
        yield return p.Add(1, 0, 0);
        yield return p.Add(-1, 0, 0);
        yield return p.Add(0, 0, 1);
        yield return p.Add(0, 0, -1);
        yield return p.Add(0, 1, 0);
    }

    // ------------------------------------------------------------------ failures said once

    private static readonly Regex Coords = new(@"-?\d+,-?\d+,-?\d+", RegexOptions.Compiled);

    /// <summary>
    /// A clear the pawn's dig refused for good (a protected block, a chest, a never-break mark: never just "occupied" again): the cell
    /// is blocked at once with that reason, said once per build and reason. A clear that is only "not now" (out of reach, busy, the
    /// pawn stood on it) or waits for a tool is not one.
    /// </summary>
    private static bool ClearRefused(string why) =>
        why.StartsWith("can't clear the cell", StringComparison.Ordinal) && !Transient(why)
        && !why.Contains("I'm standing on", StringComparison.Ordinal) && !why.Contains("would lose the drop", StringComparison.Ordinal);

    /// <summary>The build's reason and its alert, once per build and kind of failure (live 11-05: 116 "occupied by stone" warnings for
    /// one build in an hour).</summary>
    private void SayCellBlocked(PawnState p, BuildState b, GhostState g, string why)
    {
        b.Reason = $"can't place at {g.At}: {why}";
        b.Rev = Touch();
        if (_cellAlerted.Add($"{b.Id}|{Coords.Replace(why, "…")}")) Alert("warn", $"Build {b.Id}: {b.Reason}", p.Name);
        else _log.LogInformation("story {Colony}: build {Build} cell {At} blocked: {Why}", Id, b.Id, g.At, why);
    }
}
