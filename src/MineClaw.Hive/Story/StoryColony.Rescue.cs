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
/// <see cref="UnreachPawns"/> different pawns couldn't reach it (fewer when the colony has fewer online) is it the target's failure.
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

    /// <summary>Blocks a pawn pillars with (cobblestone, dirt, planks, stone, netherrack, sandstone, stone bricks, logs).</summary>
    internal static readonly int[] PillarIds = [4, 3, 5, 1, 87, 24, 98, 17, 162];

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

    private PawnMiss MissOf(PawnState p, ActiveJob j, string why) =>
        new(_stuck.ContainsKey(p.Name), j.Source == "guard", Unreachable(why) || OutOfReach(why), ReachOnly(why) && j.Source != "designation");

    /// <summary>The target rests for this pawn only (a guard's monster a short while, else longer each time); no way there is noted.</summary>
    private void RestForPawn(PawnState p, ActiveJob j, string why, bool guard)
    {
        if (j.Lease is { } lease)
        {
            var key = PawnKey(lease, p.Name);
            int n = _pawnFails[key] = _pawnFails.GetValueOrDefault(key) + 1;
            _skip[key] = Now + (guard ? MobRest : PawnBackoff(n));
        }
        if (NoWay.IsMatch(why)) NoteUnreachable(p, j);
    }

    /// <summary>N different pawns couldn't reach it: then it is the target's failure (false), unless it is out of reach even from a
    /// pillar for every pawn that tried: the designation waits with why (no failure, no alert).</summary>
    private bool UnreachableForEveryone(PawnState p, string lease, ActiveJob j, string why)
    {
        if (!_unreachBy.TryGetValue(lease, out var by)) _unreachBy[lease] = by = new(StringComparer.OrdinalIgnoreCase);
        by.Add(p.Name);
        int online = Pawns.Values.Count(o => Host.Live(o.Name).Online);
        if (by.Count < Math.Clamp(online, 1, UnreachPawns)) return true;
        _unreachBy.Remove(lease);
        return OutOfReach(why) && ReachWait(j, why);
    }

    /// <summary>A run of unreachable targets: the colony takes the pawn for stuck (an escape job).</summary>
    private void NoteUnreachable(PawnState p, ActiveJob j)
    {
        if (!_unreachRun.TryGetValue(p.Name, out var run)) _unreachRun[p.Name] = run = new();
        run.Add((Now, j.Lease ?? j.Id));
        run.RemoveAll(r => Now - r.At > StuckWindow);
        if (run.Select(r => r.Key).Distinct().Count() < StuckStreak || _stuck.ContainsKey(p.Name)) return;
        var live = Host.Live(p.Name);
        _stuck[p.Name] = new StuckState { Since = Now, At = live.Block, Why = $"{StuckStreak} targets out of reach in a row" };
        NoteHazard(p.Name, live.Block);
        run.Clear();
        _log.LogInformation("story {Colony}: {Pawn} looks stuck at {At}: {N} targets out of reach in a row", Id, p.Name, live.Block, StuckStreak);
    }

    /// <summary>A job done: the pawn isn't stuck; a rescuer's blocks arrived; the target was reached after all.</summary>
    private void Reached(PawnState p, ActiveJob j)
    {
        _unreachRun.Remove(p.Name);
        if (j.Source != "none" && _stuck.Remove(p.Name, out var st))
        {
            if (st.Asked) Alert("info", $"{p.Display} is out (was stuck at {st.At})", p.Name);
            if (st.Attempts > 0) _log.LogInformation("story {Colony}: {Pawn} is out (stuck at {At} after {N} escape attempt(s))", Id, p.Name, st.At, st.Attempts);
        }
        if (j.Lease != null) { _unreachBy.Remove(j.Lease); _pawnFails.Remove(PawnKey(j.Lease, p.Name)); }
        if (j.Source == "rescue" && j.SourceId is { } who && _stuck.TryGetValue(who, out var s)) s.Delivered = true;
    }

    /// <summary>A target tried again on purpose (retry, re-marked): every pawn's own rest of it ends too.</summary>
    internal void ForgetPawnFails(string key)
    {
        _unreachBy.Remove(key);
        foreach (var k in _pawnFails.Keys.Where(k => k.StartsWith(key + "|", StringComparison.Ordinal)).ToList()) { _pawnFails.Remove(k); _skip.Remove(k); }
    }

    /// <summary>Is the pawn stuck (said so itself, or suspected by the colony)? Tests, the inspector.</summary>
    public bool IsStuck(string pawn) { lock (_lock) return _stuck.ContainsKey(pawn); }

    /// <summary>
    /// The escape job of a stuck pawn, or of one under ground with no mining near (a cave it walked or fell into): out by itself,
    /// else home (<c>story.run</c> kind <c>escape</c>). A pawn that moved away from where it got stuck is free again.
    /// </summary>
    private ActiveJob? EscapeJob(PawnState p, PawnLive live)
    {
        if (_stuck.TryGetValue(p.Name, out var st))
        {
            // Sticky until it is out: only escape work, never a regular task (live 11-04: pawns in a ravine cycled through tasks for
            // 20 minutes, ~100 failures each, because moving a few blocks along the ravine floor read as "got out").
            if (st.TeleportedAt is { } sent)
            {
                if (Home() is { } h && h.Dist(live.X, live.Y, live.Z) <= HomeRange / 2 && Math.Abs(live.Y - h.Y) <= 6)
                {
                    _stuck.Remove(p.Name);
                    _log.LogInformation("story {Colony}: {Pawn} is home after the teleport (was stuck at {At})", Id, p.Name, st.At);
                    return null;
                }
                if (Now - sent < TeleportCheck) return Hold("stuck: teleported home, arriving", "moving", s: 3, type: "need");
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
            }
            // The budget is spent (time, or failed escape steps): home at once, by /tp (rescue_teleport).
            if (BudgetOver(st) && TeleportHome(p, live, st, $"escape budget spent ({(int)(Now - st.Since).TotalSeconds} s, {st.Failed} failed step(s))"))
                return Hold("stuck: teleported home, arriving", "moving", s: 3, type: "need");
            st.Attempts++;
            if (st.Attempts == 1 || st.Attempts % 5 == 0)
                _log.LogInformation("story {Colony}: {Pawn} stuck at {At} ({Why}): escape attempt {N}", Id, p.Name, live.Block, st.Why, st.Attempts);
            return Escape(p, live, st.Asked ? $"stuck: getting out (attempt {st.Attempts})" : "stuck? heading home");
        }
        if (p.Queue.Count == 0 && World is { } w && w.Loaded(live.Block.X, live.Block.Z) && StoryWander.Underground(w, live.Block) && !MiningNear(live.Block)
            && !Zones.Any(z => z.Kind == "home" && z.Contains(live.Block.X, live.Block.Z)))
        {
            _stuck[p.Name] = new StuckState { Since = Now, At = live.Block, Why = "under ground with no mining to do" };
            NoteHazard(p.Name, live.Block);
            return Escape(p, live, "under ground: heading back up");
        }
        return null;
    }

    private ActiveJob Escape(PawnState p, PawnLive live, string label)
    {
        var home = Home();
        P3? to = home is { } h ? SpreadHome(p, live, h) ?? h : null;
        return Job("need", "escape", label, to, null, "escape", p.Name, "moving", j => { j["dist"] = 2; j["s"] = 8; }, xp: 0);
    }

    /// <summary>Digging work within 16 blocks (a mine or deconstruct designation, a mine zone, a build: a basement): being under ground
    /// is the job then.</summary>
    private bool MiningNear(P3 at)
    {
        foreach (var kind in new[] { "mine", "deconstruct" })
            if (Index().ByKind.TryGetValue(kind, out var list) && list.Any(x => x.Target.Dist(at.X, at.Y, at.Z) <= 16)) return true;
        if (Builds.Any(b => b.Status != "done" && b.Ghosts.Any(g => g.At.Dist(at.X, at.Y, at.Z) <= 16))) return true;
        return Zones.Any(z => z.Kind == "mine" && z.Area.Center.Dist(at.X, at.Y, at.Z) <= 16 + Math.Max(z.Area.X2 - z.Area.X1, z.Area.Z2 - z.Area.Z1));
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
            if (!_stuck.TryGetValue(body, out var st)) { _stuck[body] = st = new StuckState { Since = Now, At = live.Block }; NoteHazard(body, live.Block); }
            st.Why = why;
            if (!st.Asked)
            {
                st.Asked = true; // the budget runs from when the colony first saw it stuck
                _log.LogInformation("story {Colony}: {Pawn} is stuck at {At}: {Why}", Id, body, live.Block, why);
            }
            // Blocks thrown in help a pit; a ravine, a canyon or a cave a pawn can't climb out of is the teleport's (a rescuer would
            // only walk in after it).
            bool pit = why.Contains("in a pit", StringComparison.OrdinalIgnoreCase);
            bool helper = pit && !st.Delivered && !BudgetOver(st) && Pawns.Values.Any(o => o != p && !o.Drafted && !_stuck.ContainsKey(o.Name) && IdleNow(o)
                && Host.Live(o.Name) is { Online: true } ol && ol.Block.Dist(live.X, live.Y, live.Z) <= RescueRange
                && (PillarStack(Host.Live(o.Name).Inventory) != null || FindStored(s => PillarIds.Contains(s.Id), live.Block) != null));
            if (helper)
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
            return "nobody can help: " + (!Meta.RescueTeleport ? "rescue teleport is off" : st.TeleportFailed ? "the teleport home didn't take"
                : st.TeleportError is { } te ? "no teleport (" + te + ")" : "no safe spot at home");
        }
    }

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
        string? err = !Meta.RescueTeleport ? "rescue teleport is off" : null;
        P3? to = null;
        if (err == null && Home() is { } home && World is { } w) to = SpreadHome(p, live, home) ?? SafeSpot(w, home);
        if (err == null && to == null) err = "no safe spot at home";
        if (err == null)
        {
            try { err = Host.Teleport(p.Name, to!.Value, $"story {Id}: rescue ({p.Display} stuck at {st.At})"); }
            catch (Exception ex) { err = ex.Message; }
        }
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

    private static Stack? PillarStack(IReadOnlyList<Stack> inv) =>
        PillarIds.Select(id => inv.FirstOrDefault(s => s.Id == id && s.Count > 0)).FirstOrDefault(s => s != null);

    /// <summary>Blocks for a stuck pawn: from what this pawn carries, else from storage, tossed to it from the pit's rim.</summary>
    private ActiveJob? RescueJob(PawnState p, PawnLive live)
    {
        if (_stuck.Count == 0 || _stuck.ContainsKey(p.Name)) return null;
        foreach (var (name, st) in _stuck)
        {
            if (!st.Asked || st.Delivered || st.TeleportedAt != null || !st.Why.Contains("in a pit", StringComparison.OrdinalIgnoreCase)
                || BudgetOver(st) || !Pawns.TryGetValue(name, out var who)) continue;
            if (Host.Live(name).Block.Dist(live.X, live.Y, live.Z) > RescueRange) continue; // too far: the teleport is quicker
            var key = "rescue:" + name;
            if (!Open(key, p.Name, null)) continue;
            var sl = Host.Live(name);
            if (!sl.Online) continue;
            var mine = PillarStack(live.Inventory);
            var stored = mine == null ? FindStored(s => PillarIds.Contains(s.Id), live.Block) : null;
            if (mine == null && stored == null) continue;
            var item = mine ?? stored!.Value.Stack;
            var j = Job(WorkTypes.Haul, "give", $"rescuing {who.Display}: bringing {ItemName(item.Id, item.Meta)}", sl.Block, key, "rescue", name, "working", f =>
            {
                f["to"] = name; f["item"] = ItemName(item.Id, item.Meta); f["n"] = RescueBlocks;
                if (stored is { } s) f["fetch"] = Fetch(s.Store.Chest, s.Stack, RescueBlocks);
            }, xp: 10);
            if (stored is { } s2) j.Takes.Add((s2.Store.Chest, s2.Stack with { Count = Math.Min(RescueBlocks, s2.Stack.Count) }));
            return j;
        }
        return null;
    }
}
