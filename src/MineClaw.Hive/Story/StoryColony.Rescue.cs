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
        if (j.Source == "escape") return true; // still stuck: help is under way (Stuck)
        bool stuck = _stuck.TryGetValue(p.Name, out var st) && st.Asked;
        bool guard = j.Source == "guard";
        bool noWay = Unreachable(why) || OutOfReach(why);
        bool reach = ReachOnly(why) && j.Source != "designation"; // a designation's "can't reach" is the soft rest (Failed)
        if (!stuck && !guard && !noWay && !reach) return false;
        if (j.Lease is { } lease)
        {
            var key = PawnKey(lease, p.Name);
            int n = _pawnFails[key] = _pawnFails.GetValueOrDefault(key) + 1;
            _skip[key] = Now + (guard ? MobRest : PawnBackoff(n));
        }
        if (NoWay.IsMatch(why)) NoteUnreachable(p, j);
        if (stuck || guard || reach || j.Lease == null) return true;
        // N different pawns couldn't reach it: then it is the target's failure.
        if (!_unreachBy.TryGetValue(j.Lease, out var by)) _unreachBy[j.Lease] = by = new(StringComparer.OrdinalIgnoreCase);
        by.Add(p.Name);
        int online = Pawns.Values.Count(o => Host.Live(o.Name).Online);
        if (by.Count < Math.Clamp(online, 1, UnreachPawns)) return true;
        _unreachBy.Remove(j.Lease);
        // Out of reach even from a pillar for every pawn that tried: the designation waits with why (no failure, no alert).
        if (OutOfReach(why) && ReachWait(j, why)) return true;
        return false;
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
        run.Clear();
        _log.LogInformation("story {Colony}: {Pawn} looks stuck at {At}: {N} targets out of reach in a row", Id, p.Name, live.Block, StuckStreak);
    }

    /// <summary>A job done: the pawn isn't stuck; a rescuer's blocks arrived; the target was reached after all.</summary>
    private void Reached(PawnState p, ActiveJob j)
    {
        _unreachRun.Remove(p.Name);
        if (_stuck.Remove(p.Name, out var st) && st.Asked) Alert("info", $"{p.Display} is out (was stuck at {st.At})", p.Name);
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
            if (st.At.Dist(live.X, live.Y, live.Z) > 4) { _stuck.Remove(p.Name); return null; } // it got out (a teleport, a fall, its own way)
            return Escape(p, live, st.Asked ? "stuck: getting out" : "stuck? heading home");
        }
        if (p.Queue.Count == 0 && World is { } w && w.Loaded(live.Block.X, live.Block.Z) && StoryWander.Underground(w, live.Block) && !MiningNear(live.Block)
            && !Zones.Any(z => z.Kind == "home" && z.Contains(live.Block.X, live.Block.Z)))
        {
            _stuck[p.Name] = new StuckState { Since = Now, At = live.Block, Why = "under ground with no mining to do" };
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
        P3? to = null;
        P3 at;
        lock (_lock)
        {
            if (!Pawns.TryGetValue(body, out p)) return "not a pawn of this colony";
            var live = Host.Live(body);
            if (!_stuck.TryGetValue(body, out var st)) _stuck[body] = st = new StuckState { Since = Now, At = live.Block };
            at = st.At;
            st.Why = why;
            if (!st.Asked)
            {
                st.Asked = true;
                st.Since = Now;
                _log.LogInformation("story {Colony}: {Pawn} is stuck at {At}: {Why}", Id, body, live.Block, why);
            }
            bool helper = !st.Delivered && Pawns.Values.Any(o => o != p && !o.Drafted && !_stuck.ContainsKey(o.Name) && Host.Live(o.Name).Online
                && (PillarStack(Host.Live(o.Name).Inventory) != null || FindStored(s => PillarIds.Contains(s.Id), live.Block) != null));
            if (helper && Now - st.Since < RescueWait)
            {
                if (!st.Alerted) { st.Alerted = true; Alert("info", $"{p.Display} is stuck at {live.Block} ({Short(why)}): a pawn brings it blocks", body); }
                return "help is on its way: a pawn brings blocks";
            }
            if (Meta.RescueTeleport && Home() is { } home && World is { } w) to = SpreadHome(p, live, home) ?? SafeSpot(w, home);
            if (to == null)
            {
                string reason = !Meta.RescueTeleport ? "rescue teleport is off" : "no safe spot at home";
                if (!_homeAlert.TryGetValue("stuck:" + body, out var last) || Now - last > TimeSpan.FromMinutes(10))
                {
                    _homeAlert["stuck:" + body] = Now;
                    Alert("warn", $"{p.Display} is stuck at {live.Block} and can't get out ({Short(why)}); nobody can bring it blocks and {reason}", body);
                }
                return "nobody can help: " + reason;
            }
        }
        string? err;
        try { err = Host.Teleport(body, to.Value, $"story {Id}: rescue ({p.Display} stuck at {at})"); }
        catch (Exception ex) { err = ex.Message; }
        lock (_lock)
        {
            if (err == null)
            {
                _stuck.Remove(body);
                _broughtHome[body] = Now;
                _log.LogInformation("story {Colony}: {Pawn} teleported home to {To} (stuck at {At}: {Why})", Id, body, to, at, why);
                Alert("info", $"{p.Display} was stuck at {at} and was teleported home to {to}", body);
                return "teleported home";
            }
            if (!_homeAlert.TryGetValue("stuck:" + body, out var last) || Now - last > TimeSpan.FromMinutes(10))
            {
                _homeAlert["stuck:" + body] = Now;
                Alert("warn", $"{p.Display} is stuck at {at} and can't get out ({Short(why)}); no teleport: {err}", body);
            }
            return "nobody can help: no teleport (" + err + ")";
        }
    }

    private static Stack? PillarStack(IReadOnlyList<Stack> inv) =>
        PillarIds.Select(id => inv.FirstOrDefault(s => s.Id == id && s.Count > 0)).FirstOrDefault(s => s != null);

    /// <summary>Blocks for a stuck pawn: from what this pawn carries, else from storage, tossed to it from the pit's rim.</summary>
    private ActiveJob? RescueJob(PawnState p, PawnLive live)
    {
        if (_stuck.Count == 0 || _stuck.ContainsKey(p.Name)) return null;
        foreach (var (name, st) in _stuck)
        {
            if (!st.Asked || st.Delivered || Now - st.Since > RescueWait * 2 || !Pawns.TryGetValue(name, out var who)) continue;
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
