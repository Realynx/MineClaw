using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Story;

/// <summary>
/// Stuck pawns (docs/web/story-mode.md "Stuck pawns"). Live 11-04 (release 20261103-204549, story-1): maid_chan_114 stood in a hole
/// two deep ("no path … it's 2 blocks up; unreachable even by pillaring/bridging" ×5) while it failed grass after grass; each failure
/// rested the target (and counted against it) for the whole colony, so the other pawns stood idle in front of a field of work.
/// <list type="bullet">
/// <item><b>A failure is the pawn's own</b> when the target is out of reach from where it stands ("no path", "no route",
/// "unreachable", a monster it couldn't find): the target rests for that pawn only and others take it. Only when
/// <see cref="UnreachPawns"/> different pawns couldn't reach it (fewer when fewer online pawns do its work) is it the target's failure.
/// A stuck pawn's failures never count against a target.</item>
/// <item><b>Getting out</b> is the pawn's (<c>story.go</c>/<c>story.escape</c>: walk out, dig a staircase, nerd-pole, dig walls for
/// blocks). The colony sees what the pawn can't: <see cref="StuckStreak"/> unreachable targets in a row, or a pawn under ground with
/// no mining near: it gets an <c>escape</c> job (home).</item>
/// <item><b>Help</b> when it still can't (<see cref="Stuck"/>): another pawn brings blocks (from what it carries or from storage)
/// and tosses them in; after <see cref="RescueWait"/> without getting out, or with nobody to help, the colony teleports it home
/// (<see cref="ColonyMeta.RescueTeleport"/>, through the op command path; logged and said), else it says so once.</item>
/// </list>
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>Different pawns that must fail to reach a target before it is the target's failure.</summary>
    public const int UnreachPawns = 3;
    /// <summary>Unreachable targets in a row (within <see cref="StuckWindow"/>) after which the colony treats the pawn as stuck.</summary>
    public const int StuckStreak = 3;
    public static readonly TimeSpan StuckWindow = TimeSpan.FromSeconds(60);
    /// <summary>How long a stuck pawn waits for a rescuer's blocks before the colony teleports it home.</summary>
    public static readonly TimeSpan RescueWait = TimeSpan.FromSeconds(90);
    /// <summary>Failed escape steps after which the colony teleports a stuck pawn home whatever the time (Fox 11-04: "they should
    /// teleport sooner").</summary>
    public const int StuckFailedSteps = 2;
    /// <summary>A rescuer is sent only when it is idle and this close: else the teleport.</summary>
    public const double RescueRange = 16;

    /// <summary>The escape budget (option <c>stuck_teleport_after_s</c>, default 20 s, at least 5).</summary>
    private TimeSpan StuckBudget => TimeSpan.FromSeconds(Math.Max(5, Meta.StuckTeleportAfterS));

    /// <summary>Its escape had its time (or its failed steps): the colony teleports it home now.</summary>
    private bool BudgetOver(StuckState st) => Now - st.Since >= StuckBudget || st.Failed >= StuckFailedSteps;
    /// <summary>Blocks a rescuer brings.</summary>
    public const int RescueBlocks = 8;

    /// <summary>Blocks a pawn pillars with (cobblestone, dirt, stone, netherrack, sandstone, stone bricks, then planks and logs: a build's
    /// material, last).</summary>
    internal static readonly int[] PillarIds = [4, 3, 1, 87, 24, 98, 5, 17, 162];

    private sealed class StuckState
    {
        public DateTimeOffset Since;
        public P3 At;
        public string Why = "";
        /// <summary>The pawn itself said it can't get out (<see cref="Stuck"/>); else only the colony suspects it (a streak, a cave).</summary>
        public bool Asked;
        public bool Delivered;
        public bool Alerted;
        /// <summary>What it tried last (its escape job's report: "trying a staircase toward home", "couldn't climb out …").</summary>
        public string? Step;
        public int Attempts;
        /// <summary>The colony sent it home by /tp: it is out once it shows up there (else the server refused it).</summary>
        public DateTimeOffset? TeleportedAt;
        /// <summary>Teleports sent that didn't take; retried <see cref="TeleportRetry"/> apart, given up after <see cref="MaxTeleportTries"/>
        /// (live 11-04: one /tp dropped in a backed-up chat queue left a pawn on "escape attempt 6" with no second try).</summary>
        public int TeleportMisses;
        public DateTimeOffset TeleportRetryAt;
        public bool TeleportFailed => TeleportMisses >= MaxTeleportTries;
        /// <summary>Escape jobs that failed (each one a full try: out by itself, walk home, climb).</summary>
        public int Failed;
        /// <summary>Why the last teleport home couldn't be sent (option off, no op path, the pawn offline…).</summary>
        public string? TeleportError;
    }

    /// <summary>How long a teleport home has to show in the pawn's position before the colony takes it as refused.</summary>
    public static readonly TimeSpan TeleportCheck = TimeSpan.FromSeconds(10);
    /// <summary>A teleport that didn't take is sent again this much later, up to <see cref="MaxTeleportTries"/> times.</summary>
    public static readonly TimeSpan TeleportRetry = TimeSpan.FromSeconds(30);
    public const int MaxTeleportTries = 3;

    /// <summary>The pawn's stuck state for the browser: {since, at, reason, step, attempts}, or null.</summary>
    private JsonObject? StuckJson(string pawn) => _stuck.TryGetValue(pawn, out var st)
        ? new JsonObject { ["since"] = st.Since, ["at"] = st.At.Json(), ["reason"] = st.Why, ["step"] = st.Step, ["attempts"] = st.Attempts }
        : null;

    /// <summary>A step of a stuck pawn's escape (its job's progress or failure note), for the log and the browser.</summary>
    internal void EscapeStep(string pawn, string note)
    {
        if (!_stuck.TryGetValue(pawn, out var st) || st.Step == note) return;
        st.Step = Short(note);
        _log.LogInformation("story {Colony}: {Pawn} stuck: {Step}", Id, pawn, st.Step);
        if (Pawns.TryGetValue(pawn, out var p)) p.Rev = Bump();
    }

    private readonly Dictionary<string, StuckState> _stuck = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _unreachBy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pawnFails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(DateTimeOffset At, string Key)>> _unreachRun = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A target's rest key for one pawn.</summary>
    internal static string PawnKey(string key, string pawn) => key + "|" + pawn;

    private static readonly Regex NoWay = new(@"no path|no route|unreachable", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NotFound = new(@"\bno [a-z_ ]+ within \d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The target is out of reach from where the pawn stands (no route there, a monster it couldn't find).</summary>
    internal static bool Unreachable(string why) => NoWay.IsMatch(why) || NotFound.IsMatch(why);

    /// <summary>A reach refusal at the target ("can't reach dirt"): from where another pawn stands it may be in reach.</summary>
    internal static bool ReachOnly(string why) =>
        why.Contains("can't reach", StringComparison.OrdinalIgnoreCase) || why.Contains("can't see/reach", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pawn's rest after its n-th miss of one target: 30 s, 1 min, then 5 min.</summary>
    private static TimeSpan PawnBackoff(int n) => n >= 3 ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30 * Math.Max(1, n));

    /// <summary>A failure that is this pawn's own (see the class summary): rested for it alone. True = handled (not the target's failure).</summary>
    private bool PawnOnly(PawnState p, ActiveJob j, string why)
    {
        if (j.Source is "order" or "deliver" or "wander") return false;
        if (j.Source == "escape")
        {
            EscapeStep(p.Name, why);
            if (_stuck.TryGetValue(p.Name, out var es)) es.Failed++;
            return true; // still stuck: its next escape (or the teleport, once the budget is spent) follows
        }
        var miss = MissOf(p, j, why);
        if (!miss.Any) return false;
        RestForPawn(p, j, why, miss.Guard);
        if (miss.RestsOnlyThePawn || j.Lease == null) return true;
        return UnreachableForEveryone(p, j.Lease, j, why);
    }

    /// <summary>Why a failure may be the pawn's own: it is stuck (said so itself or seen by the colony: its failures never count against
    /// a target), a guard's monster, no way there, a reach refusal (a designation's "can't reach" is the soft rest instead: Failed).</summary>
    private readonly record struct PawnMiss(bool Stuck, bool Guard, bool NoWay, bool Reach)
    {
        public bool Any => Stuck || Guard || NoWay || Reach;
        /// <summary>Never the target's failure, however many pawns miss it (only no way there may become one).</summary>
        public bool RestsOnlyThePawn => Stuck || Guard || Reach;
    }

    /// <remarks>A drop collect that found nothing (its drop still in view, nothing written off: <see cref="DropMissed"/>) is no way
    /// there too: the drop rests for this pawn, and for everyone only once <see cref="UnreachPawns"/> pawns missed it.</remarks>
    private PawnMiss MissOf(PawnState p, ActiveJob j, string why) =>
        new(_stuck.ContainsKey(p.Name), j.Source == "guard", Unreachable(why) || OutOfReach(why) || DropMissed(j, why),
            ReachOnly(why) && j.Source != "designation");

    /// <summary>The target rests for this pawn only (a guard's monster a short while, else longer each time); no way there is noted.</summary>
    private void RestForPawn(PawnState p, ActiveJob j, string why, bool guard)
    {
        if (MissKey(j) is { } lease) RestForPawn(p, lease, guard ? MobRest : null);
        if (NoWay.IsMatch(why)) NoteUnreachable(p, j);
        if (j.Source == "ghost" && j.SourceId is { } cell) CellMissSaid(cell, why); // the build cell says why it rests (StoryColony.Builds.cs)
    }

    /// <summary>The target (a lease key, or a build cell's) rests for this pawn only: <paramref name="rest"/>, else longer each time.</summary>
    private void RestForPawn(PawnState p, string key, TimeSpan? rest = null)
    {
        var pk = PawnKey(key, p.Name);
        int n = _pawnFails[pk] = _pawnFails.GetValueOrDefault(pk) + 1;
        _skip[pk] = Now + (rest ?? PawnBackoff(n));
    }

    // Several zone guards share a mob without an exclusive lease, but it remains one stable target for each pawn's failure history.
    private static string? MissKey(ActiveJob j) => j.Lease ?? (j.GuardZone ? "e:" + j.SourceId : null);

    /// <summary>N different pawns couldn't reach it: then it is the target's failure (false), unless it is out of reach even from a
    /// pillar for every pawn that tried: the designation waits with why (no failure, no alert).</summary>
    private bool UnreachableForEveryone(PawnState p, string lease, ActiveJob j, string why)
    {
        if (UnreachableForSome(p, lease, j.Type)) return true;
        return OutOfReach(why) && ReachWait(j, why);
    }

    /// <summary>The pawn missed the target (a lease key, or a build cell's): true while fewer different pawns missed it than do its work
    /// (clamped to 1..<see cref="UnreachPawns"/>); false once that many did, and the count starts over.</summary>
    private bool UnreachableForSome(PawnState p, string key, string type)
    {
        if (!_unreachBy.TryGetValue(key, out var by)) _unreachBy[key] = by = new(StringComparer.OrdinalIgnoreCase);
        by.Add(p.Name);
        if (by.Count < Math.Clamp(WorkersOf(type), 1, UnreachPawns)) return true;
        _unreachBy.Remove(key);
        return false;
    }

    /// <summary>Online pawns that could be sent to a target of this work type: the work on (1-4) and not drafted. A job of no work type
    /// counts every online pawn. Counting every online pawn for all (before 11-09) meant one or two builders among farmers and haulers
    /// never reached the count: an unreachable cell or mark was walked to for good and never got its reason.</summary>
    private int WorkersOf(string type)
    {
        bool known = WorkTypes.Known(type);
        int n = 0;
        foreach (var o in Pawns.Values)
            if ((!known || o.Priorities.GetValueOrDefault(type) is >= 1 and <= 4 && !o.Drafted) && Host.Live(o.Name).Online) n++;
        return n;
    }

    /// <summary>A run of unreachable targets: the colony takes the pawn for stuck (an escape job).</summary>
    private void NoteUnreachable(PawnState p, ActiveJob j)
    {
        if (!_unreachRun.TryGetValue(p.Name, out var run)) _unreachRun[p.Name] = run = new();
        run.Add((Now, MissKey(j) ?? j.Id));
        run.RemoveAll(r => Now - r.At > StuckWindow);
        if (_stuck.ContainsKey(p.Name)) return;
        if (LostBesidePit(p, j)) { StuckBesidePit(p); return; }
        if (run.Select(r => r.Key).Distinct().Count() < StuckStreak) return;
        var live = Host.Live(p.Name);
        _stuck[p.Name] = new StuckState { Since = Now, At = live.Block, Why = $"{StuckStreak} targets out of reach in a row" };
        NoteHazard(p.Name, live.Block);
        run.Clear();
        _log.LogInformation("story {Colony}: {Pawn} looks stuck at {At}: {N} targets out of reach in a row", Id, p.Name, live.Block, StuckStreak);
    }

    /// <summary>
    /// A quarry job out of reach for a pawn under ground outside every quarry's columns and every home: a cave beside the pit. Near
    /// the quarry it is "mining near", so it was taken for lost only after <see cref="StuckStreak"/> misses within a minute, and one
    /// failed walk can take longer than that. One miss is enough.
    /// </summary>
    private bool LostBesidePit(PawnState p, ActiveJob j)
    {
        if (j.Source != "zone" || Zones.FirstOrDefault(z => z.Id == j.SourceId) is not { } zone || !IsQuarry(zone)) return false;
        var live = Host.Live(p.Name);
        var at = live.Block;
        return live.Online && World is { } w && w.Loaded(at.X, at.Z) && StoryWander.Underground(w, at) && QuarryColumn(at) == null
            && !Zones.Any(z => z.Kind == "home" && z.Contains(at.X, at.Z)) && QuarryNear(zone, at) && !DiggingBesidePit(p, zone, at);
    }

    /// <summary>
    /// Under ground beside the pit for its own digging, not lost: in this quarry's wall-ore tunnel (its last job was a vein dig from
    /// it, or one of its vein cells is within 4), or near other mining (any other mine or deconstruct mark, a build, an ores-only zone
    /// within 16, as <see cref="MiningNear"/> has it). One quarry miss sent such a miner to escape, cutting its work off.
    /// </summary>
    private bool DiggingBesidePit(PawnState p, ZoneState quarry, P3 at)
    {
        if (_veinFrom.TryGetValue(p.Name, out var from) && from == quarry.Id) return true;
        foreach (var kind in new[] { "mine", "deconstruct" })
            if (Index().ByKind.TryGetValue(kind, out var list)
                && list.Any(x => x.Target.Dist(at.X, at.Y, at.Z) <= (x.D.OreOrigin is { Source: "zone" } o && o.Id == quarry.Id ? 4 : 16))) return true;
        if (Builds.Any(b => b.Status != "done" && b.Ghosts.Any(g => g.At.Dist(at.X, at.Y, at.Z) <= 16))) return true;
        return Zones.Any(z => z.Kind == "mine" && !IsQuarry(z)
            && z.Area.Center.Dist(at.X, at.Y, at.Z) <= 16 + Math.Max(z.Area.X2 - z.Area.X1, z.Area.Z2 - z.Area.Z1));
    }

    /// <summary>The quarry a pawn's last finished job followed wall ore from (a vein dig), by pawn; any other job ends it.</summary>
    private readonly Dictionary<string, string> _veinFrom = new(StringComparer.OrdinalIgnoreCase);

    private void StuckBesidePit(PawnState p)
    {
        var live = Host.Live(p.Name);
        _stuck[p.Name] = new StuckState { Since = Now, At = live.Block, Why = "under ground beside a quarry, its work out of reach" };
        NoteHazard(p.Name, live.Block);
        _unreachRun.Remove(p.Name);
        _log.LogInformation("story {Colony}: {Pawn} looks stuck at {At}: under ground beside a quarry, its work out of reach", Id, p.Name, live.Block);
    }

    /// <summary>The quarry whose columns hold this cell (at any height), or null.</summary>
    private ZoneState? QuarryColumn(P3 at) => Zix().Quarry.TryGetValue(ZoneCells.Key(at.X, at.Z), out var z) ? z : null;

    /// <summary>A job done: the pawn isn't stuck; a rescuer's blocks arrived; the target was reached after all.</summary>
    private void Reached(PawnState p, ActiveJob j)
    {
        _unreachRun.Remove(p.Name);
        if (j.Source != "none" && _stuck.Remove(p.Name, out var st))
        {
            if (st.Asked) Alert("info", $"{p.Display} is out (was stuck at {st.At})", p.Name);
            if (st.Attempts > 0) _log.LogInformation("story {Colony}: {Pawn} is out (stuck at {At} after {N} escape attempt(s))", Id, p.Name, st.At, st.Attempts);
        }
        if (MissKey(j) is { } key)
        {
            // A build batch ends done also when its first cell was missed: that cell's misses end when it is built (CellDone).
            if (j.Source != "ghost") { _unreachBy.Remove(key); _pawnFails.Remove(PawnKey(key, p.Name)); _reachWaits.Remove(key); }
            _quarryMiss.Remove(key);
        }
        if (j.Source == "rescue" && j.SourceId is { } who && _stuck.TryGetValue(who, out var s)) s.Delivered = true;
        // A dig batch's cells are done one by one as plain jobs (no OreOrigin): their mark still names it here.
        if (j.Source == "designation" && (j.OreOrigin ?? Designations.FirstOrDefault(d => d.Id == j.SourceId)?.OreOrigin) is { Source: "zone" } vein)
            _veinFrom[p.Name] = vein.Id;
        else _veinFrom.Remove(p.Name);
    }

    /// <summary>A target tried again on purpose (retry, re-marked): every pawn's own rest of it ends too.</summary>
    internal void ForgetPawnFails(string key)
    {
        _unreachBy.Remove(key);
        _reachWaits.Remove(key);
        // Removing while walking the keys is safe for Dictionary (since .NET Core 3.0), so nothing is copied.
        foreach (var k in _pawnFails.Keys)
            if (k.Length > key.Length && k[key.Length] == '|' && k.StartsWith(key, StringComparison.Ordinal)) { _pawnFails.Remove(k); _skip.Remove(k); }
    }

    /// <summary>Is the pawn stuck (said so itself, or suspected by the colony)? Tests, the inspector.</summary>
    public bool IsStuck(string pawn) { lock (_lock) return _stuck.ContainsKey(pawn); }

    /// <summary>
    /// The escape job of a stuck pawn, or of one under ground with no mining near (a cave it walked or fell into): out by itself,
    /// else home (<c>story.run</c> kind <c>escape</c>). A pawn that moved away from where it got stuck is free again.
    /// </summary>
    private ActiveJob? EscapeJob(PawnState p, PawnLive live)
    {
        if (_stuck.TryGetValue(p.Name, out var st)) return StuckEscape(p, live, st);
        if (!LostUnderground(p, live)) return null;
        _stuck[p.Name] = new StuckState { Since = Now, At = live.Block, Why = "under ground with no mining to do" };
        NoteHazard(p.Name, live.Block);
        return Escape(p, live, "under ground: heading back up");
    }

    /// <summary>Under ground with nothing queued, no mining near and not in a house: a cave it walked or fell into. Never down in a
    /// quarry's pit (its columns, under its top): the stair's steps and the seals left standing over a miner there read as ground over
    /// it, and deep in a small quarry every pick sent its miners home.</summary>
    private bool LostUnderground(PawnState p, PawnLive live) =>
        p.Queue.Count == 0 && World is { } w && w.Loaded(live.Block.X, live.Block.Z) && StoryWander.Underground(w, live.Block) && !MiningNear(live.Block)
        && !Zones.Any(z => z.Kind == "home" && z.Contains(live.Block.X, live.Block.Z)) && QuarryAt(live.Block) == null;

    /// <summary>
    /// Sticky until it is out: only escape work, never a regular task (live 11-04: pawns in a ravine cycled through tasks for 20
    /// minutes, ~100 failures each, because moving a few blocks along the ravine floor read as "got out"). When the budget is spent
    /// (time, or failed escape steps): home at once, by /tp (rescue_teleport).
    /// </summary>
    private ActiveJob? StuckEscape(PawnState p, PawnLive live, StuckState st)
    {
        if (st.TeleportedAt is { } sent && AfterTeleport(p, live, st, sent) is (true, var settled)) return settled;
        if (BudgetOver(st) && TeleportHome(p, live, st, $"escape budget spent ({(int)(Now - st.Since).TotalSeconds} s, {st.Failed} failed step(s))"))
            return Hold("stuck: teleported home, arriving", "moving", s: 3, type: "need");
        st.Attempts++;
        if (st.Attempts == 1 || st.Attempts % 5 == 0)
            _log.LogInformation("story {Colony}: {Pawn} stuck at {At} ({Why}): escape attempt {N}", Id, p.Name, live.Block, st.Why, st.Attempts);
        return Escape(p, live, st.Asked ? $"stuck: getting out (attempt {st.Attempts})" : "stuck? heading home");
    }

    /// <summary>A teleport home was sent: home (out: no job), still arriving (a hold), or it didn't take (Done false: the escape goes
    /// on, the teleport is tried again later).</summary>
    private (bool Done, ActiveJob? Job) AfterTeleport(PawnState p, PawnLive live, StuckState st, DateTimeOffset sent)
    {
        if (Home() is { } h && h.Dist(live.X, live.Y, live.Z) <= HomeRange / 2 && Math.Abs(live.Y - h.Y) <= 6)
        {
            _stuck.Remove(p.Name);
            _log.LogInformation("story {Colony}: {Pawn} is home after the teleport (was stuck at {At})", Id, p.Name, st.At);
            return (true, null);
        }
        if (Now - sent < TeleportCheck) return (true, Hold("stuck: teleported home, arriving", "moving", s: 3, type: "need"));
        st.TeleportedAt = null;
        st.TeleportMisses++;
        st.TeleportRetryAt = Now + TeleportRetry;
        if (st.TeleportFailed)
        {
            _log.LogWarning("story {Colony}: {Pawn}'s teleport home didn't take {N} times (still at {At}): the server refused /tp?", Id, p.Name, st.TeleportMisses, live.Block);
            Alert("warn", $"{p.Display}'s teleport home didn't take {st.TeleportMisses} times (still at {live.Block}): is the bot op, and /tp allowed by the command gate?", p.Name);
        }
        else
            _log.LogInformation("story {Colony}: {Pawn}'s teleport home didn't take (still at {At}): trying again in {S} s ({N}/{Max})", Id, p.Name, live.Block,
                (int)TeleportRetry.TotalSeconds, st.TeleportMisses, MaxTeleportTries);
        return (false, null);
    }

    private ActiveJob Escape(PawnState p, PawnLive live, string label)
    {
        var home = Home();
        P3? to = home is { } h ? SpreadHome(p, live, h) ?? h : null;
        return Job("need", "escape", label, to, null, "escape", p.Name, "moving", j => { j["dist"] = 2; j["s"] = 8; }, xp: 0);
    }

    /// <summary>Digging work within 16 blocks (a mine or deconstruct designation, a mine zone, a build: a basement): being under ground
    /// is the job then. A quarry is near within 16 across of its columns, at any depth up to 2 over its top (measured from where it was
    /// painted, the miners of a quarry deeper than 16 plus its size were never near it).</summary>
    private bool MiningNear(P3 at)
    {
        foreach (var kind in new[] { "mine", "deconstruct" })
            if (Index().ByKind.TryGetValue(kind, out var list) && list.Any(x => x.Target.Dist(at.X, at.Y, at.Z) <= 16)) return true;
        if (Builds.Any(b => b.Status != "done" && b.Ghosts.Any(g => g.At.Dist(at.X, at.Y, at.Z) <= 16))) return true;
        return Zones.Any(z => z.Kind == "mine" && (IsQuarry(z) ? QuarryNear(z, at)
            : z.Area.Center.Dist(at.X, at.Y, at.Z) <= 16 + Math.Max(z.Area.X2 - z.Area.X1, z.Area.Z2 - z.Area.Z1)));
    }

    /// <summary>Within 16 blocks across of the quarry's bounds, and no more than 2 over its top.</summary>
    private bool QuarryNear(ZoneState z, P3 at)
    {
        int dx = Math.Max(0, Math.Max(z.Area.X1 - at.X, at.X - z.Area.X2)), dz = Math.Max(0, Math.Max(z.Area.Z1 - at.Z, at.Z - z.Area.Z2));
        return dx * dx + dz * dz <= 16 * 16 && at.Y <= QuarryTopOf(z) + 2;
    }

    /// <summary>
    /// A pawn says it can't get out by itself (the SDK's <c>story.escape</c> tried stairs, a pillar and digging for blocks). While a
    /// rescuer can bring blocks it waits for them; after <see cref="RescueWait"/>, or with nobody to help, the colony teleports it home
    /// (<see cref="ColonyMeta.RescueTeleport"/>); else it says so once. Returns what happens (the pawn's report).
    /// </summary>
    public string Stuck(string body, string why)
    {
        PawnState? p;
        lock (_lock)
        {
            if (!Pawns.TryGetValue(body, out p)) return "not a pawn of this colony";
            var live = Host.Live(body);
            var st = AskedStuck(body, live, why);
            // Blocks thrown in help a pit; a ravine, a canyon or a cave a pawn can't climb out of is the teleport's (a rescuer would
            // only walk in after it).
            bool pit = why.Contains("in a pit", StringComparison.OrdinalIgnoreCase);
            if (pit && !st.Delivered && !BudgetOver(st) && Pawns.Values.Any(o => CanBringBlocks(o, p, live)))
            {
                if (!st.Alerted)
                {
                    st.Alerted = true;
                    _log.LogInformation("story {Colony}: {Pawn} stuck: asking a pawn to bring blocks", Id, body);
                    Alert("info", $"{p.Display} is stuck at {live.Block} ({Short(why)}): a pawn brings it blocks", body);
                }
                return "help is on its way: a pawn brings blocks";
            }
            if (st.TeleportedAt != null || TeleportHome(p, live, st, why)) return "teleported home";
            return "nobody can help: " + NoHelpReason(st);
        }
    }

    /// <summary>The pawn's stuck state (a new one marks the spot a hazard), with its own reason; asked once.</summary>
    private StuckState AskedStuck(string body, PawnLive live, string why)
    {
        if (!_stuck.TryGetValue(body, out var st)) { _stuck[body] = st = new StuckState { Since = Now, At = live.Block }; NoteHazard(body, live.Block); }
        st.Why = why;
        if (!st.Asked)
        {
            st.Asked = true; // the budget runs from when the colony first saw it stuck
            _log.LogInformation("story {Colony}: {Pawn} is stuck at {At}: {Why}", Id, body, live.Block, why);
        }
        return st;
    }

    /// <summary>Another pawn, free (not drafted, not stuck, idle), online within <see cref="RescueRange"/>, with blocks carried or stored.</summary>
    private bool CanBringBlocks(PawnState o, PawnState stuck, PawnLive live) =>
        o != stuck && !o.Drafted && !_stuck.ContainsKey(o.Name) && IdleNow(o)
        && Host.Live(o.Name) is { Online: true } ol && ol.Block.Dist(live.X, live.Y, live.Z) <= RescueRange
        && (PillarStack(Host.Live(o.Name).Inventory) != null || FindStored(s => PillarIds.Contains(s.Id), live.Block) != null);

    private string NoHelpReason(StuckState st) =>
        !Meta.RescueTeleport ? "rescue teleport is off" : st.TeleportFailed ? "the teleport home didn't take"
        : st.TeleportError is { } te ? "no teleport (" + te + ")" : "no safe spot at home";

    // ---------------------------------------------------------------- stuck spots

    /// <summary>How long a spot where a pawn got stuck keeps other pawns' work away from it.</summary>
    public static readonly TimeSpan HazardFor = TimeSpan.FromMinutes(5);
    private readonly List<(P3 At, string Pawn, DateTimeOffset Until)> _hazards = new();

    /// <summary>
    /// A pawn got stuck here: for <see cref="HazardFor"/> no other pawn is sent to a target within 3 blocks of it (and 4 up or down).
    /// Live 11-04: three pawns stuck in one hole at 243,53,427, each sent after the work the first one left there.
    /// </summary>
    private void NoteHazard(string pawn, P3 at)
    {
        _hazards.RemoveAll(h => h.Until <= Now);
        _hazards.RemoveAll(h => h.At == at);
        _hazards.Add((at, pawn, Now + HazardFor));
        _log.LogInformation("story {Colony}: {At} is a stuck spot ({Pawn}): no other pawn's work there for {Min} min", Id, at, pawn, HazardFor.TotalMinutes);
    }

    /// <summary>A target by a spot where another pawn got stuck (the picker skips it for now).</summary>
    private bool NearHazard(P3 at, string pawn)
    {
        if (_hazards.Count == 0) return false;
        foreach (var h in _hazards)
            if (h.Until > Now && !string.Equals(h.Pawn, pawn, StringComparison.OrdinalIgnoreCase)
                && Math.Abs(h.At.X - at.X) <= 3 && Math.Abs(h.At.Z - at.Z) <= 3 && Math.Abs(h.At.Y - at.Y) <= 4) return true;
        return false;
    }

    /// <summary>Has nothing to do right now (an idle hold or leg, or no job): free to go and help.</summary>
    private bool IdleNow(PawnState o) => !_current.TryGetValue(o.Name, out var c) || c.Source is "none" or "wander";

    /// <summary>
    /// Teleports a stuck pawn home now (option <see cref="ColonyMeta.RescueTeleport"/>; the op command path, its gate and blocklist).
    /// True = sent (it is out once it shows up at home, <see cref="TeleportCheck"/>); false = not possible (said once; it keeps trying
    /// to get out by itself).
    /// </summary>
    private bool TeleportHome(PawnState p, PawnLive live, StuckState st, string why)
    {
        if (st.TeleportFailed || st.TeleportedAt != null || Now < st.TeleportRetryAt) return false;
        st.TeleportError = null;
        var (to, err) = TeleportRescue(p, live, st);
        if (err == null)
        {
            st.TeleportedAt = Now;
            _broughtHome[p.Name] = Now;
            _log.LogInformation("story {Colony}: {Pawn} teleporting home to {To} (rescue_teleport; stuck at {At}: {Why})", Id, p.Name, to, st.At, why);
            Alert("info", $"{p.Display} was stuck at {st.At} and was teleported home to {to}", p.Name);
            return true;
        }
        st.TeleportError = err;
        if (!_homeAlert.TryGetValue("stuck:" + p.Name, out var last) || Now - last > TimeSpan.FromMinutes(10))
        {
            _homeAlert["stuck:" + p.Name] = Now;
            _log.LogWarning("story {Colony}: {Pawn} stuck at {At} can't be teleported home: {Err}", Id, p.Name, st.At, err);
            Alert("warn", $"{p.Display} is stuck at {st.At} and can't get out ({Short(st.Why)}); no teleport: {err}", p.Name);
        }
        return false;
    }

    /// <summary>The teleport itself: where to (a spot at home), and why it can't be (null: sent).</summary>
    private (P3? To, string? Error) TeleportRescue(PawnState p, PawnLive live, StuckState st)
    {
        if (!Meta.RescueTeleport) return (null, "rescue teleport is off");
        P3? to = Home() is { } home && World is { } w ? SpreadHome(p, live, home) ?? SafeSpot(w, home) : null;
        if (to == null) return (null, "no safe spot at home");
        try { return (to, Host.Teleport(p.Name, to.Value, $"story {Id}: rescue ({p.Display} stuck at {st.At})")); }
        catch (Exception ex) { return (to, ex.Message); }
    }

    private static Stack? PillarStack(IReadOnlyList<Stack> inv) =>
        PillarIds.Select(id => inv.FirstOrDefault(s => s.Id == id && s.Count > 0)).FirstOrDefault(s => s != null);

    /// <summary>Blocks for a stuck pawn: from what this pawn carries, else from storage, tossed to it from the pit's rim.</summary>
    private ActiveJob? RescueJob(PawnState p, PawnLive live)
    {
        if (_stuck.Count == 0 || _stuck.ContainsKey(p.Name)) return null;
        foreach (var (name, st) in _stuck)
            if (PitRescueOpen(st) && Pawns.TryGetValue(name, out var who) && RescueTrip(p, live, name, who) is { } j) return j;
        return null;
    }

    /// <summary>A pawn that asked for help in a pit, not helped or teleported yet, with budget left.</summary>
    private bool PitRescueOpen(StuckState st) =>
        st.Asked && !st.Delivered && st.TeleportedAt == null && st.Why.Contains("in a pit", StringComparison.OrdinalIgnoreCase) && !BudgetOver(st);

    private ActiveJob? RescueTrip(PawnState p, PawnLive live, string name, PawnState who)
    {
        if (Host.Live(name).Block.Dist(live.X, live.Y, live.Z) > RescueRange) return null; // too far: the teleport is quicker
        var key = "rescue:" + name;
        if (!Open(key, p.Name, null)) return null;
        var sl = Host.Live(name);
        if (!sl.Online) return null;
        var mine = PillarStack(live.Inventory);
        var stored = mine == null ? FindStored(s => PillarIds.Contains(s.Id), live.Block) : null;
        if (mine == null && stored == null) return null;
        var item = mine ?? stored!.Value.Stack;
        var j = Job(WorkTypes.Haul, "give", $"rescuing {who.Display}: bringing {ItemName(item.Id, item.Meta)}", sl.Block, key, "rescue", name, "working", f =>
        {
            f["to"] = name; f["item"] = ItemName(item.Id, item.Meta); f["n"] = RescueBlocks;
            if (stored is { } s) f["fetch"] = Fetch(s.Store.Chest, s.Stack, RescueBlocks);
        }, xp: 10);
        if (stored is { } s2) j.Takes.Add((s2.Store.Chest, s2.Stack with { Count = Math.Min(RescueBlocks, s2.Stack.Count) }));
        return j;
    }
}
