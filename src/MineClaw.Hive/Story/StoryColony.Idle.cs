namespace MineClaw.Hive.Story;

/// <summary>
/// Idle behaviour (docs/web/story-mode.md "Idle behaviour"). A pawn with nothing to do in its work (or anything) hours, and any pawn
/// in its free hours, wanders: the director hands it one leg at a time, a safe cell of the home area to stroll to and linger on
/// (kind <c>idle</c>, label "Idle (wandering)"); the pawn script walks there and lingers inside <c>story.during</c>, so the moment the
/// director takes the leg back (real work came up: it is handed over in the same step) the walk or the linger is cut. A cancelled or
/// failed leg is never a failure: no back-off, no alert, no rest.
///
/// Personal space: every idle, resting and brought-home target is reserved per pawn (<see cref="_spots"/>, a light and short-lived
/// reservation, apart from the work leases: it never blocks digging, building or hauling) and kept at least
/// <see cref="Room"/> blocks from every other pawn's reservation and standing spot when there is room, never on the same cell.
/// At night idle pawns stay in (a roofed cell of a home zone, or a lit one) or else rest at a spread spot by home.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>Around the primary home, where idle pawns wander when the colony has no home zones.</summary>
    public const int WanderRadius = 14;
    /// <summary>The separations tried, most room first; the last one only keeps two pawns off one cell.</summary>
    internal static readonly double[] Room = [2.5, 2.0, 1.0];
    /// <summary>How long a target nobody's job holds any more (a teleport's, a flight's) stays reserved.</summary>
    public static readonly TimeSpan SpotHold = TimeSpan.FromSeconds(30);
    /// <summary>A wander target the pawn couldn't reach rests this long (for every pawn).</summary>
    public static readonly TimeSpan WanderRest = TimeSpan.FromSeconds(60);

    private sealed record Spot(P3 At, string? Job, DateTimeOffset Until);

    private readonly Dictionary<string, Spot> _spots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, P3> _restSpot = new(StringComparer.OrdinalIgnoreCase);
    private (DateTimeOffset At, string Key, List<P3> Cells)? _idleCells;
    private DateTimeOffset _wanderCheck = DateTimeOffset.MinValue;

    /// <summary>The colony's dice for wander legs (tests seed it).</summary>
    internal Random Rng { get; set; } = new();

    /// <summary>Live idle reservations (tests, diagnostics): pawn → cell.</summary>
    public IReadOnlyDictionary<string, P3> Spots()
    {
        lock (_lock) return _spots.Where(kv => SpotLive(kv.Key, kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value.At, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A reservation counts while it hasn't run out and the job it was made for is still the pawn's.</summary>
    private bool SpotLive(string pawn, Spot s) =>
        s.Until > Now && (s.Job == null || _current.TryGetValue(pawn, out var cur) && cur.Id == s.Job);

    private void Reserve(string pawn, P3 at, ActiveJob? job, TimeSpan hold) => _spots[pawn] = new Spot(at, job?.Id, Now + hold);

    /// <summary>Where the other pawns stand and are headed (live reservations), and every other player in view.</summary>
    private List<P3> Taken(string pawn)
    {
        var list = new List<P3>();
        foreach (var (name, s) in _spots)
            if (!name.Equals(pawn, StringComparison.OrdinalIgnoreCase) && SpotLive(name, s)) list.Add(s.At);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pawn };
        foreach (var q in Pawns.Values)
        {
            if (q.Name.Equals(pawn, StringComparison.OrdinalIgnoreCase)) continue;
            var l = Host.Live(q.Name);
            if (l.Online && l.Dim == (World?.Dimension ?? l.Dim)) list.Add(l.Block);
        }
        if (World is { } w)
            foreach (var e in w.Entities())
                if (e.Kind == "player" && !names.Contains(e.Name)) list.Add(e.Block);
        return list;
    }

    /// <summary>
    /// The colony's own cells an idle pawn keeps off: designated and build cells (standing on one, under one or in one), the fronts of
    /// its storages and workbenches.
    /// </summary>
    private Func<P3, bool> Busy()
    {
        var cells = new HashSet<P3>();
        foreach (var d in Designations)
        {
            if (d.At is { } a) cells.Add(a);
            if (d.Blocks != null) foreach (var b in d.Blocks) cells.Add(b);
        }
        foreach (var b in Builds)
            if (b.Status != "done")
                foreach (var g in b.Ghosts)
                    if (g.State is not ("done" or "unsupported" or "misrotated")) cells.Add(g.At);
        var fronts = Storages.SelectMany(s => s.DoubleWith is { } d ? new[] { s.Chest, d } : [s.Chest]).Concat(Benches.Select(b => b.Pos)).ToList();
        // Inside a wall or fence ring still going up: an idle pawn there gets walled in (FakeServer: a hut's pawn shut in).
        var rings = Builds.Where(b => b.Status != "done").Select(Footprint).Where(f => f.Walled).ToList();
        // Loops, not Any(lambda): this runs per idle cell, and each inner lambda over c was a closure and a delegate per call.
        return c =>
        {
            if (cells.Contains(c) || cells.Contains(c.Add(0, -1, 0)) || cells.Contains(c.Add(0, 1, 0))) return true;
            foreach (var f in rings)
                if (c.X >= f.X1 && c.X <= f.X2 && c.Z >= f.Z1 && c.Z <= f.Z2) return true;
            foreach (var f in fronts)
                if (Math.Abs(f.X - c.X) <= 1 && Math.Abs(f.Z - c.Z) <= 1 && Math.Abs(f.Y - c.Y) <= 1) return true;
            return Forbidden(c) || Forbidden(c.Add(0, -1, 0));
        };
    }

    /// <summary>
    /// The safe cells of the home area (cached 5 s): the home zones' columns when the colony has any, else <see cref="WanderRadius"/>
    /// around the primary home. At night only the ones that are in (roofed, in a home zone) or lit.
    /// </summary>
    private List<P3> IdleCells(bool night)
    {
        if (World is not { } w) return [];
        var homes = Zones.Where(z => z.Kind == "home").ToList();
        var anchor = Home();
        var key = $"{night}|{anchor}|{string.Join(";", homes.Select(z => z.Id + ":" + z.Rev))}";
        if (_idleCells is { } c && c.Key == key && Now - c.At < TimeSpan.FromSeconds(5)) return c.Cells;
        var cells = new List<P3>();
        var seen = new HashSet<(int, int)>();
        void Column(int x, int z, int refY, bool inZone)
        {
            if (!seen.Add((x, z)) || StoryWander.GroundNear(w, x, z, refY) is not { } at) return;
            // Never a cave cell (live 11-04: pawns wandered into caves and couldn't get back out); a home zone's own floors (a basement
            // at the zone's height) still count.
            if (StoryWander.Underground(w, at) && !(inZone && at.Y >= refY - 1)) return;
            if (night && !(inZone && StoryWander.Roofed(w, at)) && !(w.NightLight(at.X, at.Y, at.Z) is >= 8)) return;
            cells.Add(at);
        }
        if (homes.Count > 0)
        {
            long columns = homes.Sum(z => (long)z.Cells.Count);
            int step = columns > 2000 ? (int)Math.Ceiling(Math.Sqrt(columns / 2000.0)) : 1; // a huge zone is sampled, not scanned whole
            foreach (var z in homes)
                foreach (var (x, zz) in z.Cells.Sorted())
                    if ((x - z.Area.X1) % step == 0 && (zz - z.Area.Z1) % step == 0) Column(x, zz, z.Area.Y1, true);
        }
        else if (anchor is { } h)
            for (int dx = -WanderRadius; dx <= WanderRadius; dx++)
                for (int dz = -WanderRadius; dz <= WanderRadius; dz++)
                    if (dx * dx + dz * dz <= WanderRadius * WanderRadius) Column(h.X + dx, h.Z + dz, h.Y, false);
        _idleCells = (Now, key, cells);
        return cells;
    }

    /// <summary>The open cells with at least <paramref name="room"/> to every other pawn (most room first), or empty.</summary>
    private static List<P3> WithRoom(IEnumerable<P3> cells, List<P3> taken, out double room, double[]? rooms = null)
    {
        var all = cells.ToList();
        foreach (var r in rooms ?? Room)
        {
            var ok = new List<P3>();
            foreach (var c in all)
            {
                bool free = true; // a loop, not taken.All(lambda over c): a closure per cell
                foreach (var t in taken)
                    if (StoryWander.Gap(c, t) < r || t == c) { free = false; break; }
                if (free) ok.Add(c);
            }
            if (ok.Count > 0) { room = r; return ok; }
        }
        room = 0;
        return [];
    }

    /// <summary>
    /// A spread spot at home for a pawn that stays put (rests with no bed, stands in at night, flees home): the one it had while it is
    /// still free, else the free cell nearest <paramref name="anchor"/> with room around it. Not reserved (the caller reserves).
    /// </summary>
    private P3? RestSpot(PawnState p, PawnLive? live, P3 anchor)
    {
        if (World == null) return null;
        var busy = Busy();
        var taken = Taken(p.Name);
        var cells = IdleCells(false);
        if (_restSpot.TryGetValue(p.Name, out var had) && cells.Contains(had) && !busy(had) && taken.All(t => StoryWander.Gap(had, t) >= Room[^1] && t != had))
            return had;
        var ok = WithRoom(cells.Where(c => !busy(c)), taken, out _);
        if (ok.Count == 0) return null;
        var spot = ok.OrderBy(c => c.Dist(anchor)).First();
        _restSpot[p.Name] = spot;
        return spot;
    }

    /// <summary>
    /// A hold that stays put at home (resting with no bed): at the pawn's own spread spot, reserved for the job, else at the anchor.
    /// </summary>
    private ActiveJob RestHold(PawnState p, PawnLive live, string label, string state, P3? anchor, int s, string type)
    {
        var spot = anchor is { } a ? RestSpot(p, live, a) : null;
        var j = Hold(label, state, spot ?? anchor, s, type);
        if (spot is { } at)
        {
            j.Json["dist"] = 0;
            Reserve(p.Name, at, j, TimeSpan.FromSeconds(s + 30));
        }
        return j;
    }

    /// <summary>A spread spot by home for one pawn going there now (a flight, a teleport, a walk home), reserved for a while; null = none.</summary>
    private P3? SpreadHome(PawnState p, PawnLive? live, P3 home)
    {
        if (RestSpot(p, live, home) is not { } spot) return null;
        Reserve(p.Name, spot, null, SpotHold);
        return spot;
    }

    /// <summary>The end of a wander leg (done, failed or cut): never a failure. An unreachable target rests a while.</summary>
    private void WanderEnded(PawnState? p, ActiveJob leg, string status)
    {
        if (p != null && _spots.TryGetValue(p.Name, out var s) && s.Job == leg.Id) _spots.Remove(p.Name);
        if (status == "failed" && leg.At is { } at && leg.Json["wander"] != null) _skip["wander:" + at] = Now + WanderRest;
    }

    /// <summary>The pawn script's question while it walks or lingers: is this still its job?</summary>
    public bool Holds(string body, string job)
    {
        lock (_lock) return _current.TryGetValue(body, out var cur) && cur.Id == job;
    }

    /// <summary>
    /// Between and during legs (every couple of seconds, at once after new work): a wandering pawn whose hours now hold real work (an
    /// urgent need, or a job of its work types) gets it in the same step, leased, so the next pawn sees it taken; a leg whose hours are
    /// over (the schedule moved on) is taken back. The pawn script's <c>story.during</c> sees the change and cuts the walk or the
    /// linger; nothing is stopped or restarted, and nothing counts as failed.
    /// </summary>
    private void WakeWanderers()
    {
        if (!_wakePending && Now - _wanderCheck < TimeSpan.FromSeconds(1)) return;
        _wanderCheck = Now;
        foreach (var k in _spots.Where(kv => kv.Value.Until <= Now).Select(kv => kv.Key).ToList()) _spots.Remove(k);
        if (Meta.Paused || !Meta.Active) return;
        int hour = HourNow;
        // New work (a wake): every wanderer looks. Otherwise only one whose last empty search is out of date, one a tick
        // (live 11-04: a full search per wanderer every 2 s, ~110 ms a tick with 4 idle pawns).
        bool wake = _wakePending;
        int budget = 1;
        long stamp = WorkStamp();
        foreach (var p in Pawns.Values) WakeWanderer(p, hour, wake, stamp, ref budget);
    }

    private void WakeWanderer(PawnState p, int hour, bool wake, long stamp, ref int budget)
    {
        if (p.Drafted || !_current.TryGetValue(p.Name, out var cur) || cur.Source != "wander") return;
        var live = Host.Live(p.Name);
        if (!live.Online) return;
        var block = ScheduleBlockAt(p, hour);
        bool workHours = block is ScheduleBlocks.Work or ScheduleBlocks.Anything;
        if (!LegStillOn(p, cur, block, workHours) || p.Queue.Count > 0)
        {
            _current.Remove(p.Name); // its hours (or its rest) moved on, or it has orders: it asks again
            p.Rev = Bump();
            return;
        }
        // A resting leg too: work never waits for a rest.
        if (WandererWork(p, live, workHours, wake, stamp, ref budget) is not { } job) return;
        if (job.Lease != null) _leases[job.Lease] = (p.Name, Now + LeaseTtl);
        foreach (var k in job.MoreLeases) _leases[k] = (p.Name, Now + LeaseTtl);
        job.Assign?.Invoke(); // a build batch leases its cells now (as Next does)
        _current[p.Name] = job;
        p.Rev = Bump();
    }

    /// <summary>A resting leg ends with the back-off, or the moment there is work for it (a rest never holds a pawn back from work); a
    /// free-time leg with its free hours; a work-hours leg with its work hours.</summary>
    private bool LegStillOn(PawnState p, ActiveJob cur, string block, bool workHours) =>
        cur.SourceId == "rest" ? RestLeft(p) != null : cur.SourceId == "free" ? block == ScheduleBlocks.Free : workHours;

    /// <summary>An urgent need, or (in its work hours, on a wake or when its probe is due) its work.</summary>
    private ActiveJob? WandererWork(PawnState p, PawnLive live, bool workHours, bool wake, long stamp, ref int budget) =>
        Urgent(p, live) ?? (workHours && (wake || ProbeDue(p, stamp, ref budget)) ? ProbeWork(p, live, stamp) : null);
}
