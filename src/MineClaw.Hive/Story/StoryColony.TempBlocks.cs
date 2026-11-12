using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Story;

/// <summary>
/// Temporary blocks (docs/web/story-mode.md "Temporary blocks"). Fox 11-05: "I'm seeing a lot of stray planks have been placed down as
/// well" and "Bots should delete whatever they put down unless it was a directive to build it. So, nerd poles, other blocks. If a bot
/// can't navigate something without deleting its nerd pole, then it should teleport." Live 11-04: maid_chan_67 boxed itself in with 9
/// oak planks at 193,37,430, died beside the box a minute later and was teleported home; the box stayed (the client's own clean-up only
/// runs when the body is idle by it), like 2-high plank stubs from tree pillars and walks' pillars and bridges (a Story job never ran the
/// client's clean-up at all).
/// <list type="bullet">
/// <item>Every block a pawn puts down for itself is registered (<see cref="ColonyMeta.TempBlocks"/>): the SDK's reach and escape pillars and
/// build scaffolds as they go up (<see cref="Temporary"/>), the body's own walk scaffolds, shelter boxes and clutch blocks from its client
/// (<see cref="IStoryHost.TemporaryBlocks"/>). A build's cell is a directive, never temporary.</item>
/// <item>The pawn takes them back itself (the pillar on the way down, the scaffold once its cell stands, <c>clear_temporary</c> after every
/// job). A cell is forgotten once the world shows it without that block.</item>
/// <item>One left behind (its pawn offline, or <see cref="LeftAfter"/> on and more than <see cref="LeftRange"/> away: a failed or cut job, a
/// teleport home) becomes a deconstruct designation any builder takes (self-placed: always minable), said in one alert per minute.</item>
/// <item>On load the colony looks once for its pawns' stray blocks near home (<see cref="SweepStrays"/>).</item>
/// </list>
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>A temporary block its pawn hasn't taken back this long, while away from it, is a cleanup job.</summary>
    public static readonly TimeSpan LeftAfter = TimeSpan.FromSeconds(45);
    /// <summary>How far its pawn is from it when it counts as left behind.</summary>
    public const double LeftRange = 8;
    /// <summary>A registered placement the view never showed is dropped after this.</summary>
    private static readonly TimeSpan NeverSeen = TimeSpan.FromSeconds(20);

    private DateTimeOffset _tempTick = DateTimeOffset.MinValue, _tempAlertAt = DateTimeOffset.MinValue;
    private readonly List<TempBlockState> _tempQueued = new();
    private bool _straysSwept;

    private List<TempBlockState> Temps => Meta.TempBlocks ??= new();

    private void CancelTempCleanup(DesignationState d)
    {
        if (Meta.TempBlocks == null) return;
        foreach (var t in Meta.TempBlocks.Where(t => t.Designation == d.Id))
        {
            t.Cancelled = true;
            t.Designation = null;
            _tempQueued.Remove(t);
            KeepTemp(t);
            _dirty = true;
        }
    }

    // Forget the body's own cleanup ledger too, so its post-job sweep cannot undo the cancellation.
    private void KeepTemp(TempBlockState t)
    {
        foreach (var p in Pawns.Keys) Host.KeepTemporaryBlock(p, t.At);
    }

    /// <summary>The SDK's report: a block put down for itself (or taken back). See <see cref="Scripting.Story.IStoryPort.Temporary"/>.</summary>
    public void Temporary(string body, string cell, bool placed, string why)
    {
        if (ParseCell(cell) is not { } at) return;
        lock (_lock)
        {
            if (!Pawns.ContainsKey(body) || !placed && Meta.TempBlocks == null) return;
            Temps.RemoveAll(t => t.At == at && t.Designation == null);
            if (placed) Temps.Add(new TempBlockState { At = at, Id = SeenId(body, at), Pawn = body, Kind = why, Since = Now });
            else if (Temps.Count == 0) Meta.TempBlocks = null;
            _dirty = true;
        }
    }

    /// <summary>The block at a cell as its pawn's own view shows it (the server's answer reaches the pawn first), else the colony's view.</summary>
    private int SeenId(string pawn, P3 at)
    {
        IStoryWorld? w = null;
        try { w = Host.ViewOf(pawn); } catch (Exception) { /* main's view */ }
        w ??= World;
        return w != null && w.Loaded(at.X, at.Z) ? w.Block(at.X, at.Y, at.Z).Id : 0;
    }

    /// <summary>Every few seconds (the colony tick, under the lock): adopt the bodies' own temporary blocks, forget the ones gone, queue the
    /// ones left behind, look once for strays.</summary>
    private void TempTick()
    {
        if (Now - _tempTick < TimeSpan.FromSeconds(3) || World is not { } w) return;
        _tempTick = Now;
        var cells = new BuildCells(() => BuildCellSet()); // made only when a temp block is there to check (51,000 cells live 11-10)
        AdoptBodyTemps(cells);
        if (Meta.TempBlocks is { Count: > 0 } temps)
        {
            var marks = Designations.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var t in temps.ToList()) TempCheck(w, t, cells, marks);
        }
        if (!_straysSwept) SweepStrays(w);
        SweepDoneQuarries(w); // StoryColony.QuarrySweep.cs
        AlertQueuedTemps();
        AlertLeftTemps();
    }

    /// <summary>The pawns' clients' own temporary blocks (walk scaffolds, shelter boxes, clutch blocks) join the registry.</summary>
    private void AdoptBodyTemps(BuildCells buildCells)
    {
        foreach (var p in Pawns.Values)
        {
            IReadOnlyList<(P3 At, int Id)> mine;
            try { mine = Host.TemporaryBlocks(p.Name); } catch (Exception) { continue; }
            foreach (var (at, id) in mine.ToArray())
            {
                if (PreservedTemp(at, id)) { Host.KeepTemporaryBlock(p.Name, at); continue; }
                if (!buildCells.Contains(at) && !(Meta.TempBlocks?.Any(t => t.At == at) ?? false)) { Temps.Add(new TempBlockState { At = at, Id = id, Pawn = p.Name, Kind = "scaffold", Since = Now }); _dirty = true; }
            }
        }
    }

    /// <summary>A solid build directive takes ownership of its block. Intentional air still permits temporary access whose owner
    /// will remove it; forgetting that registry would let the replacement cleanup mine a live pillar.</summary>
    private HashSet<P3> BuildCellSet(bool includeReplacementAir = false)
    {
        int n = 0;
        foreach (var b in Builds) n += b.Ghosts.Count;
        var set = new HashSet<P3>(n);
        var temps = ActiveTempCells(); // a temp left to the owner in a design's cell is the design's to clear: forgotten here
        foreach (var b in Builds)
        {
            bool replacement = ReplacementBuild(b); // its spec read once a build, not per cell
            foreach (var g in b.Ghosts)
                if (includeReplacementAir || !(g.Remove && replacement) && !(replacement && temps.Contains(g.At))) set.Add(g.At);
        }
        return set;
    }

    /// <summary>The build cells (<see cref="BuildCellSet"/>), made on the first question.</summary>
    private sealed class BuildCells(Func<HashSet<P3>> make)
    {
        private HashSet<P3>? _set;
        public bool Contains(P3 at) => (_set ??= make()).Contains(at);
    }

    /// <summary>One registered cell: gone (forgotten, its cleanup mark too), a build's cell (not ours to take), its mark gone (marked
    /// again), left behind (queued). <paramref name="marks"/>: the ids of every designation now.</summary>
    private void TempCheck(IStoryWorld w, TempBlockState t, BuildCells buildCells, HashSet<string> marks)
    {
        if (!w.Loaded(t.At.X, t.At.Z)) return;
        int now = w.Block(t.At.X, t.At.Y, t.At.Z).Id;
        AdoptSeen(t, now);
        if (PreservedTemp(t.At, now)) { RetirePreservedTemp(t); return; }
        if (Gone(t, now) || buildCells.Contains(t.At)) { ForgetTemp(t); return; }
        MarkGone(t, marks);
        if (t.Cancelled || t.DeferredReason != null) { KeepTemp(t); return; }
        if (!LeftBehind(t))
        {
            // An owner back on its tower needs the whole column, including a bottom once queued while it was away.
            if (InUseAsPillar(t, Host.Live(t.Pawn)) && Designations.FirstOrDefault(d => d.Id == t.Designation) is { Auto: true } cleanup)
            {
                foreach (var (owner, job) in _current.ToList())
                    if (job.Source == "designation" && job.SourceId == cleanup.Id && Pawns.TryGetValue(owner, out var p))
                        Supersede(p, "temporary access still in use");
                RemoveTempDesignation(t);
                _tempQueued.Remove(t);
                _dirty = true;
            }
            return;
        }
        if (t.RetryAfter > Now && Designations.FirstOrDefault(d => d.Id == t.Designation) is { } resting)
            _skip[DesignationKey(resting)] = t.RetryAfter.Value;
        if (t.Designation == null && t.Id != 0) QueueTemp(t);
    }

    private bool PreservedTemp(P3 at, int id) => id != 0
        && PreserveFilter()?.Invoke(new MineClaw.World.BlockPos(at.X, at.Y, at.Z), id) == true;

    /// <summary>A preserved foundation may have been placed by a pawn, but it is no longer disposable access. Retire only the
    /// registry's automatic cleanup; a manual designation at the same cell remains the owner's order.</summary>
    private void RetirePreservedTemp(TempBlockState temp)
    {
        if (temp.Designation is { } id && Designations.Any(d => d.Id == id && d.Auto))
            foreach (var (name, job) in _current.ToList())
                if (job.Source == "designation" && job.SourceId == id && Pawns.TryGetValue(name, out var pawn))
                    Supersede(pawn, "temporary cleanup is now preserved");
        KeepTemp(temp);
        _tempQueued.Remove(temp);
        ForgetTemp(temp);
    }

    /// <summary>A placement registered before the view showed it: its block, once it does.</summary>
    private void AdoptSeen(TempBlockState t, int now)
    {
        if (t.Id != 0 || now == 0 || StoryBlocks.Liquids.Contains(now)) return;
        t.Id = now;
        t.Adopted = true; // seen, not reported: never taught to a cleaner as the hive's own (OwnTempBlocks)
        _dirty = true;
    }

    /// <summary>The block isn't there any more (dug, replaced), or the view never showed it at all.</summary>
    private bool Gone(TempBlockState t, int now) => t.Id == 0 ? Now - t.Since > NeverSeen : !SameTempBlock(t.Id, now);

    /// <summary>The block placed there still, the way the hive's placed-block log counts it: dirt that grew grass is the same dirt, and
    /// grass recorded (a stray seen grown over) that went back to dirt under a block is the same grass. Only the block's id counts,
    /// never its meta (a 1.12.2 stone or planks variant, a turned log), so a block that only changed state is never "somebody else's".
    /// Before, a grass record that turned to dirt read as dug, and the pillar block was forgotten while it still stood.</summary>
    internal static bool SameTempBlock(int placed, int now) => MineClaw.World.PlacedBlockLog.SameBlock(placed, now);

    /// <summary>
    /// Its removal mark went while the block still stands: it was finished on a report the colony's view couldn't check, or dropped
    /// with a quarry's, an ore's or a replaced design's work. The registry kept pointing at a mark that no longer exists (live 11-11:
    /// 31 such entries, 26 of them scaffolds): nobody would ever take the block. It is marked again (when left behind) instead; a
    /// block gone with its mark was already forgotten (<see cref="Gone"/>).
    /// </summary>
    private void MarkGone(TempBlockState t, HashSet<string> marks)
    {
        if (t.Designation is not { } id || marks.Contains(id)) return;
        t.Designation = null;
        _tempQueued.Remove(t);
        _dirty = true;
        _log.LogInformation("story {Colony}: the removal mark {Mark} of the temporary {Block} at {At} ({Kind}) is gone while the block stands: marking it again",
            Id, id, BlockLabel(t.Id), t.At, t.Kind);
    }

    private void ForgetTemp(TempBlockState t)
    {
        Temps.Remove(t);
        if (Temps.Count == 0) Meta.TempBlocks = null;
        _dirty = true;
        RemoveTempDesignation(t);
    }

    private void RemoveTempDesignation(TempBlockState t)
    {
        if (t.Designation is { } id && Designations.FirstOrDefault(d => d.Id == id) is { Auto: true } des)
        {
            Designations.Remove(des);
            Removed("designation", des.Id);
        }
        t.Designation = null;
    }

    /// <summary>Its pawn won't take it back: offline or gone from the colony, or away from it for <see cref="LeftAfter"/>.</summary>
    private bool LeftBehind(TempBlockState t)
    {
        if (!Pawns.ContainsKey(t.Pawn)) return true;
        var live = Host.Live(t.Pawn);
        if (!live.Online) return true;
        if (InUseAsPillar(t, live)) return false;
        return Now - t.Since >= LeftAfter && t.At.Dist(live.X, live.Y, live.Z) > LeftRange;
    }

    /// <summary>Its pawn stands over it on its own pillar, still using it, unless it stands there to take that pillar down
    /// (<see cref="TakingDownColumn"/>): then the column is the cleanup's, never "temporary access still in use", which cut the dig with
    /// the pawn partway down the pit and took the rest of the marks away.</summary>
    private bool InUseAsPillar(TempBlockState t, PawnLive live) => HelperInUse(t, live) || OnOwnPillar(t, live) && !TakingDownColumn(t.Pawn, t.At);

    /// <summary>
    /// A helper column its pawn is building up beside a pillar to take that pillar down (StoryColony.TempPillars.cs, the SDK's
    /// <c>unpoleAlongside</c>): in use for the whole job, however tall. The job runs well past <see cref="LeftAfter"/> and the pawn is
    /// far over the column's lowest blocks, which then read as left behind: marked, alerted, and given to another pawn to take down
    /// from under it.
    /// </summary>
    private bool HelperInUse(TempBlockState t, PawnLive live) => live.Online && t.Kind == "pillar helper"
        && _current.TryGetValue(t.Pawn, out var j) && j.Json["unpole"]?["beside"] is JsonValue b && b.TryGetValue<string>(out var s)
        && ParseCell(s) is { } ground && ground.X == t.At.X && ground.Z == t.At.Z;

    /// <summary>The pawn's current job takes the pillar in this cell's column down from its top (StoryColony.TempPillars.cs).</summary>
    private bool TakingDownColumn(string pawn, P3 at) =>
        _current.TryGetValue(pawn, out var j) && j.Json["unpole"] != null && j.At is { } top && top.X == at.X && top.Z == at.Z;

    private static bool OnOwnPillar(TempBlockState t, PawnLive live) => live.Online
        && t.Kind is "reach pillar" or "escape pillar" or "pillar helper" && live.Y > t.At.Y
        && live.Y - t.At.Y <= Scripting.Story.ReachScan.MaxPillar + 2
        && Math.Abs(live.X - (t.At.X + 0.5)) <= 0.8 && Math.Abs(live.Z - (t.At.Z + 0.5)) <= 0.8;

    /// <summary>A deconstruct designation (the colony's own, Auto) for a block left behind; one already on the cell is reused.
    /// <paramref name="said"/>: the caller says it (the stray look), not the minute's alert.</summary>
    private void QueueTemp(TempBlockState t, bool said = false)
    {
        var des = Designations.FirstOrDefault(d => d.At == t.At && d.Kind is "deconstruct" or "mine");
        if (des == null)
        {
            des = new DesignationState
            {
                Id = NextId("d"), Kind = "deconstruct", At = t.At, Created = Now, Auto = true, Rev = Touch(),
                Label = $"removing a temporary {BlockLabel(t.Id)} ({t.Kind}) left by {PawnDisplay(t.Pawn)}",
            };
            Designations.Add(des);
            WakeIdle();
        }
        t.Designation = des.Id;
        _dirty = true;
        if (!said) _tempQueued.Add(t);
        _log.LogInformation("story {Colony}: temporary {Block} at {At} ({Kind}) left by {Pawn}: queued for removal", Id, BlockLabel(t.Id), t.At, t.Kind, t.Pawn);
    }

    private string BlockLabel(int id) => World is { } w && w.Data.BlockExists(id) ? w.Data.Block(id).DisplayName.ToLowerInvariant() : "block";

    private string PawnDisplay(string pawn) => Pawns.TryGetValue(pawn, out var p) ? p.Display : pawn;

    /// <summary>The blocks queued since the last alert, said at most once a minute: how many, of what, where (a few).</summary>
    private void AlertQueuedTemps()
    {
        if (_tempQueued.Count == 0 || Now - _tempAlertAt < TimeSpan.FromMinutes(1)) return;
        _tempAlertAt = Now;
        var kinds = _tempQueued.GroupBy(t => BlockLabel(t.Id)).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}");
        var where = string.Join(", ", _tempQueued.Select(t => t.At.ToString()).Distinct().Take(4));
        Alert("info", $"{_tempQueued.Count} temporary block(s) the pawns left behind ({string.Join(", ", kinds)}; at {where}{(_tempQueued.Count > 4 ? ", …" : "")}): queued for removal");
        _tempQueued.Clear();
    }

    // ---------------------------------------------------------------- cleanups that fail (live 11-06, the first sweep: a stonebrick where a
    // pawn's dirt had been was refused as someone's path ×3; old shelter boxes in a cave at 221,39..41,424..426 "unreachable even by
    // pillaring/bridging", retried every minute)

    /// <summary>The rest after a cleanup's first miss of its block; each miss after doubles it, up to <see cref="TempUnreachRestMax"/>.</summary>
    public static readonly TimeSpan TempUnreachRest = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan TempUnreachRestMax = TimeSpan.FromHours(8);

    /// <summary>The rest after the <paramref name="misses"/>-th miss: 30 min, 1 h, 2 h, 4 h, then 8 h.</summary>
    internal static TimeSpan TempUnreachBackoff(int misses) =>
        TimeSpan.FromMinutes(Math.Min(TempUnreachRestMax.TotalMinutes, TempUnreachRest.TotalMinutes * Math.Pow(2, Math.Clamp(misses - 1, 0, 10))));

    /// <summary>Why a cleanup is parked for the owner. "out of reach" is no longer written (a block out of reach is retried, kept
    /// marked); a save from before has its entries reopened on load.</summary>
    private const string OutOfReachLeft = "out of reach", NotOursLeft = "not the pawns' block any more";

    private readonly List<(P3 At, string Why)> _tempLeft = new();
    private readonly List<(P3 At, string Why)> _tempUnreached = new();
    private DateTimeOffset _tempLeftSaid = DateTimeOffset.MinValue, _tempUnreachedSaid = DateTimeOffset.MinValue;

    /// <summary>
    /// On load (the constructor, before any job): cleanups parked "out of reach" get a fresh chance (live 11-11: 81 entries, 63 of
    /// them escape pillars, most over a day old, with no mark at all, so nobody ever took them and the owner saw pillars "not even
    /// marked for destruction"): now a pawn takes a pillar down from its top, and a block still out of reach rests and is tried again.
    /// One refused as protected ("not the pawns' block any more") stays the owner's: its body's protection judged the block somebody
    /// else's (the 11-06 stonebrick in a pawn's dirt cell), and reopening it would teach that block to a cleaner as the hive's own.
    /// </summary>
    private void ReopenParkedTemps()
    {
        int n = 0;
        foreach (var t in Meta.TempBlocks ?? [])
        {
            if (t.Cancelled || t.DeferredReason != OutOfReachLeft) continue;
            t.DeferredReason = null;
            t.Misses = 0;
            t.RetryAfter = null;
            n++;
        }
        if (n == 0) return;
        _dirty = true;
        _log.LogInformation("story {Colony}: {N} temporary block(s) once left out of reach get another cleanup", Id, n);
    }

    /// <summary>The temporary block whose cleanup designation this job worked, or null.</summary>
    private TempBlockState? TempOf(ActiveJob j) =>
        j.Source == "designation" && j.SourceId is { } id ? Meta.TempBlocks?.FirstOrDefault(t => t.Designation == id) : null;

    internal bool IsTempMark(DesignationState d) => d.Auto && d.Kind == "deconstruct" && (Meta.TempBlocks?.Any(t => t.Designation == d.Id) ?? false);

    /// <summary>
    /// A cleanup that failed. Refused as protected although the cleaner's placed-block log was taught the block first: the owner's
    /// protection (a never-break mark, a protected area) or somebody's block in its place, so it is never dug: listed for the owner.
    /// Out of reach: never given up (Fox's rule: a pawn's own block never stays). It rests, longer each miss
    /// (<see cref="TempUnreachBackoff"/>), keeps its mark with the reason on the map, and is said in the owner's alert of blocks the
    /// pawns can't take back yet. Anything else (a fight, a busy chest) is an ordinary failure. True = handled.
    /// </summary>
    private bool TempCleanupFailed(PawnState p, ActiveJob j, string why)
    {
        if (TempOf(j) is not { } t) return false;
        if (why.Contains("protected", StringComparison.OrdinalIgnoreCase)) { LeaveTemp(t, NotOursLeft); return true; }
        if (!Unreachable(why) && !OutOfReach(why) && !ReachOnly(why)) return false;
        var rest = TempUnreachBackoff(++t.Misses);
        _dirty = true;
        // A pillar's job: its other cells rest with its top (else each was picked alone and dug from beside, missing one by one).
        var mates = j.MoreLeases.Count == 0 ? new List<TempBlockState>() : Designations.Where(d => j.MoreLeases.Contains(DesignationKey(d)))
            .SelectMany(d => Temps.Where(o => o.Designation == d.Id && o != t)).ToList();
        foreach (var o in mates.Prepend(t))
        {
            o.Misses = t.Misses;
            o.RetryAfter = Now + rest;
            if (Designations.FirstOrDefault(d => d.Id == o.Designation) is { } des)
            {
                _skip[DesignationKey(des)] = o.RetryAfter.Value;
                NoteWait(des, $"out of reach for now (tried {t.Misses}×, again in {Minutes(rest)}): {Short(why)}");
            }
        }
        _tempUnreached.Add((t.At, t.Kind));
        _log.LogInformation("story {Colony}: temporary {Block} at {At} ({Kind}) out of reach for {Pawn} ({Why}): miss {N}, resting {Rest}", Id, BlockLabel(t.Id),
            t.At, t.Kind, p.Name, Short(why), t.Misses, Minutes(rest));
        AlertLeftTemps();
        return true;
    }

    private static string Minutes(TimeSpan t) => t.TotalMinutes >= 60 ? $"{t.TotalHours:0.#} h" : $"{t.TotalMinutes:0} min";

    /// <summary>The cleanup is dropped and the cell listed for the owner (one alert every few minutes with every cell since).</summary>
    private void LeaveTemp(TempBlockState t, string why)
    {
        RemoveTempDesignation(t);
        t.DeferredReason = why;
        KeepTemp(t);
        _dirty = true;
        _tempLeft.Add((t.At, why));
        _log.LogInformation("story {Colony}: temporary block at {At} left for the owner: {Why}", Id, t.At, why);
        AlertLeftTemps();
    }

    /// <summary>Every cell left to the owner, and every cell a cleanup couldn't reach (still marked, tried again later), since the
    /// last alert, grouped by why or kind (at most every 5 minutes; the tick says the rest).</summary>
    private void AlertLeftTemps()
    {
        static string Cells(IEnumerable<(P3 At, string Why)> xs) =>
            string.Join("; ", xs.GroupBy(x => x.Why).Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.At.ToString()).Distinct().Take(8))}"));
        if (_tempLeft.Count > 0 && Now - _tempLeftSaid >= TimeSpan.FromMinutes(5))
        {
            _tempLeftSaid = Now;
            Alert("info", $"Left for you to remove ({_tempLeft.Count} block(s) the pawns can't take back): {Cells(_tempLeft)}");
            _tempLeft.Clear();
        }
        if (_tempUnreached.Count > 0 && Now - _tempUnreachedSaid >= TimeSpan.FromMinutes(5))
        {
            _tempUnreachedSaid = Now;
            Alert("info", $"{_tempUnreached.Count} temporary block(s) the pawns couldn't reach yet: still marked, tried again later ({Cells(_tempUnreached)})");
            _tempUnreached.Clear();
        }
    }
}
