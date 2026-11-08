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
        var cells = BuildCellSet();
        AdoptBodyTemps(cells);
        if (Meta.TempBlocks is { Count: > 0 } temps)
            foreach (var t in temps.ToList()) TempCheck(w, t, cells);
        if (!_straysSwept) SweepStrays(w);
        AlertQueuedTemps();
        AlertLeftTemps();
    }

    /// <summary>The pawns' clients' own temporary blocks (walk scaffolds, shelter boxes, clutch blocks) join the registry.</summary>
    private void AdoptBodyTemps(HashSet<P3> buildCells)
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
        var set = new HashSet<P3>();
        var temps = TempCellSet();
        foreach (var b in Builds) foreach (var g in b.Ghosts)
            if (includeReplacementAir || !ReplacementAir(b, g) && !(ReplacementBuild(b) && temps.Contains(g.At))) set.Add(g.At);
        return set;
    }

    /// <summary>One registered cell: gone (forgotten, its cleanup mark too), a build's cell (not ours to take), left behind (queued).</summary>
    private void TempCheck(IStoryWorld w, TempBlockState t, HashSet<P3> buildCells)
    {
        if (!w.Loaded(t.At.X, t.At.Z)) return;
        int now = w.Block(t.At.X, t.At.Y, t.At.Z).Id;
        AdoptSeen(t, now);
        if (PreservedTemp(t.At, now)) { RetirePreservedTemp(t); return; }
        if (Gone(t, now) || buildCells.Contains(t.At)) { ForgetTemp(t); return; }
        if (t.Cancelled || t.DeferredReason != null) { KeepTemp(t); return; }
        if (!LeftBehind(t))
        {
            // An owner back on its tower needs the whole column, including a bottom once queued while it was away.
            if (OnOwnPillar(t, Host.Live(t.Pawn)) && Designations.FirstOrDefault(d => d.Id == t.Designation) is { Auto: true } cleanup)
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
        _dirty = true;
    }

    /// <summary>The block isn't there any more (dug, replaced), or the view never showed it at all.</summary>
    private bool Gone(TempBlockState t, int now) => t.Id == 0 ? Now - t.Since > NeverSeen : !SameTempBlock(t.Id, now);

    /// <summary>The block placed there still (dirt that grew grass is the same dirt).</summary>
    private static bool SameTempBlock(int placed, int now) => placed == now || placed == 3 && now == 2;

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
        if (OnOwnPillar(t, live)) return false;
        return Now - t.Since >= LeftAfter && t.At.Dist(live.X, live.Y, live.Z) > LeftRange;
    }

    private static bool OnOwnPillar(TempBlockState t, PawnLive live) => live.Online
        && t.Kind is "reach pillar" or "escape pillar" && live.Y > t.At.Y
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

    /// <summary>A rest after a cleanup couldn't reach its block; a second miss leaves it to the owner.</summary>
    public static readonly TimeSpan TempUnreachRest = TimeSpan.FromMinutes(30);

    private readonly List<(P3 At, string Why)> _tempLeft = new();
    private DateTimeOffset _tempLeftSaid = DateTimeOffset.MinValue;

    /// <summary>The temporary block whose cleanup designation this job worked, or null.</summary>
    private TempBlockState? TempOf(ActiveJob j) =>
        j.Source == "designation" && j.SourceId is { } id ? Meta.TempBlocks?.FirstOrDefault(t => t.Designation == id) : null;

    internal bool IsTempMark(DesignationState d) => d.Auto && d.Kind == "deconstruct" && (Meta.TempBlocks?.Any(t => t.Designation == d.Id) ?? false);

    /// <summary>
    /// A cleanup that failed. Refused as protected: the block isn't the one a pawn put there any more (someone's block replaced it; a
    /// pawn's own is always minable), so it is never dug: forgotten and listed for the owner. Out of reach: rested
    /// <see cref="TempUnreachRest"/>, and on a second miss left to the owner (listed). Anything else (a fight, a busy chest) is an
    /// ordinary failure. True = handled.
    /// </summary>
    private bool TempCleanupFailed(PawnState p, ActiveJob j, string why)
    {
        if (TempOf(j) is not { } t) return false;
        if (why.Contains("protected", StringComparison.OrdinalIgnoreCase)) { LeaveTemp(t, "not the pawns' block any more"); return true; }
        if (!Unreachable(why) && !OutOfReach(why) && !ReachOnly(why)) return false;
        if (++t.Misses >= 2) { LeaveTemp(t, "out of reach"); return true; }
        t.RetryAfter = Now + TempUnreachRest;
        _dirty = true;
        if (Designations.FirstOrDefault(d => d.Id == t.Designation) is { } des) _skip[DesignationKey(des)] = Now + TempUnreachRest;
        _log.LogInformation("story {Colony}: temporary {Block} at {At} out of reach for {Pawn} ({Why}): resting {Min} min", Id, BlockLabel(t.Id), t.At,
            p.Name, Short(why), TempUnreachRest.TotalMinutes);
        return true;
    }

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

    /// <summary>Every cell left to the owner since the last alert, grouped by why (at most every 5 minutes; the tick says the rest).</summary>
    private void AlertLeftTemps()
    {
        if (_tempLeft.Count == 0 || Now - _tempLeftSaid < TimeSpan.FromMinutes(5)) return;
        _tempLeftSaid = Now;
        var groups = _tempLeft.GroupBy(x => x.Why).Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.At.ToString()).Take(8))}");
        Alert("info", $"Left for you to remove ({_tempLeft.Count} block(s) the pawns can't take back): {string.Join("; ", groups)}");
        _tempLeft.Clear();
    }
}
