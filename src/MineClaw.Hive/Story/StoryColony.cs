using System.Text.Json.Nodes;
using MineClaw.Scripting.Story;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Story;

/// <summary>A job a pawn holds now (never persisted: a restart drops its lease and the director picks again).</summary>
public sealed class ActiveJob
{
    public string Id { get; init; } = "";
    /// <summary>The work type, or need | order | schedule | idle.</summary>
    public string Type { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public P3? At { get; init; }
    public string? Lease { get; init; }
    public JsonObject Json { get; init; } = new();
    public DateTimeOffset Since { get; init; }
    public double? Progress { get; set; }
    /// <summary>The pawn's state while it runs: working | moving | eating | sleeping | fleeing | fighting | idle | drafted.</summary>
    public string State { get; init; } = "working";
    /// <summary>What it works off: designation | ghost | bill | furnace | order | deliver | move | drop | zone | crop | sow | feed | guard | need.</summary>
    public string Source { get; init; } = "";
    public string? SourceId { get; init; }
    public List<(P3 Chest, Stack Stack)> Takes { get; } = new();
    public List<(P3 Chest, List<Stack> Stacks)> Puts { get; } = new();
    public double Xp { get; init; } = 20;
    public int Count { get; init; }
}

/// <summary>
/// One colony's director (docs/web/story-mode.md): its pawns and their characteristics, priorities, schedules and orders, zones,
/// designations, the build queue, storages and workbench bills; the job picker and the leases (two pawns never share a target);
/// the state the browser polls (full, or a delta since a revision); persistence. Every pawn of the colony runs the saved
/// <c>story_pawn</c> script, which asks <see cref="Next"/> and reports through <see cref="Report"/>: no model is ever involved.
/// Thread-safe: one lock (pawn scripts, routes and the tick all come here).
/// </summary>
public sealed partial class StoryColony : IStoryPort
{
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Dictionary<string, (string Owner, DateTimeOffset Until)> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveJob> _current = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _started = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _idleAlert = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastSource = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(long Rev, string Kind, string Id)> _removed = new();
    private long _rev;
    private bool _dirty;
    private int _jobSeq;

    public static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(90);
    public const int MaxPawns = 12;

    public StoryColony(ColonyMeta meta, IStoryHost host, TimeProvider? time = null, StoryStore? store = null, ILogger? log = null)
    {
        Meta = meta;
        Host = host;
        Store = store;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    public ColonyMeta Meta { get; }
    public IStoryHost Host { get; }
    public StoryStore? Store { get; }
    public Dictionary<string, PawnState> Pawns { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ZoneState> Zones { get; } = new();
    public List<DesignationState> Designations { get; } = new();
    public List<StorageState> Storages { get; } = new();
    public List<BuildState> Builds { get; } = new();
    public List<WorkbenchState> Benches { get; } = new();
    public List<AlertState> Alerts { get; } = new();
    public long Rev { get { lock (_lock) return _rev; } }
    public string Id => Meta.Id;

    private IStoryWorld? World => Host.World;
    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>A new revision (every change stamps its object with it: the delta's "since").</summary>
    private long Touch() { _dirty = true; return ++_rev; }

    /// <summary>A new revision for what isn't saved (a pawn's current job, alerts): the browser sees it, the db isn't written.</summary>
    private long Bump() => ++_rev;

    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;

    /// <summary>Restores the revision counter after a load (objects keep their saved revs).</summary>
    internal void SetRev(long rev) { lock (_lock) _rev = Math.Max(_rev, rev); }

    private string NextId(string prefix) { Meta.Seq++; _dirty = true; return prefix + Meta.Seq; }

    public void Alert(string level, string text, string? pawn = null)
    {
        lock (_lock)
        {
            Alerts.Add(new AlertState(Now, level, text, pawn, Bump()));
            if (Alerts.Count > 100) Alerts.RemoveRange(0, Alerts.Count - 100);
        }
        _log.LogInformation("story {Colony}: {Level}: {Text}", Id, level, text);
    }

    private void Removed(string kind, string id) => _removed.Add((Touch(), kind, id));

    // ================================================================ pawns

    /// <summary>Adds a pawn for an account (its characteristics generated once, seeded by the name), or keeps the saved one.</summary>
    public PawnState AddPawn(string account)
    {
        lock (_lock)
        {
            if (Pawns.TryGetValue(account, out var p)) return p;
            p = StoryCharacter.Generate(account);
            p.Rev = Touch();
            Pawns[account] = p;
            return p;
        }
    }

    private PawnState? Pawn(string name)
    {
        if (Pawns.TryGetValue(name, out var p)) return p;
        return Pawns.Values.FirstOrDefault(x => x.Display.Equals(name, StringComparison.OrdinalIgnoreCase)
            || MineClaw.Hive.Hierarchy.HiveNode.ShortOf(x.Name).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The pawns a request names (unknown names → error).</summary>
    public (List<PawnState> Pawns, string? Error) Resolve(IEnumerable<string>? names)
    {
        var list = new List<PawnState>();
        if (names == null) return (list, "pawns required");
        lock (_lock)
            foreach (var n in names)
            {
                if (Pawn(n) is not { } p) return ([], $"no pawn '{n}'");
                if (!list.Contains(p)) list.Add(p);
            }
        return list.Count == 0 ? (list, "pawns required") : (list, null);
    }

    // ================================================================ IStoryPort (the pawn script)

    public JsonObject? Next(string body)
    {
        lock (_lock)
        {
            if (!Pawns.TryGetValue(body, out var p)) return null;
            if (_current.TryGetValue(body, out var cur) && (cur.Lease == null || HoldsLease(cur.Lease, body))) return cur.Json;
            var live = Host.Live(body);
            var job = Pick(p, live);
            if (job.Lease != null) _leases[job.Lease] = (body, Now + LeaseTtl);
            _current[body] = job;
            p.Rev = Bump();
            return job.Json;
        }
    }

    public bool Report(string body, string job, string status, string? note, double? pct)
    {
        lock (_lock)
        {
            if (!_current.TryGetValue(body, out var cur) || cur.Id != job) return false;
            var p = Pawns.GetValueOrDefault(body);
            switch (status)
            {
                case "progress":
                    cur.Progress = pct;
                    if (cur.Lease != null) _leases[cur.Lease] = (body, Now + LeaseTtl);
                    if (p != null) p.Rev = Bump();
                    return true;
                case "done" when p != null && cur.Source == "designation" && StillThere(cur) is { } left:
                    // Live 11-03: harvest and cut designations vanished with no line in the log: the script's dig on a block out of
                    // its view did nothing and said ok. A designation is done only when the world agrees.
                    _current.Remove(body);
                    Release(cur);
                    Failed(p, cur, $"reported done, but {left}");
                    _lastSource[body] = "failed";
                    p.Rev = Bump();
                    return true;
                case "done":
                    _current.Remove(body);
                    Release(cur);
                    if (p != null) Done(p, cur, note);
                    _lastSource[body] = cur.Source;
                    if (p != null) p.Rev = Touch(); // its experience is saved
                    return true;
                default:
                    _current.Remove(body);
                    Release(cur);
                    if (p != null) Failed(p, cur, note);
                    _lastSource[body] = "failed";
                    if (p != null) p.Rev = Bump();
                    return true;
            }
        }
    }

    private bool HoldsLease(string key, string owner) =>
        _leases.TryGetValue(key, out var l) && string.Equals(l.Owner, owner, StringComparison.OrdinalIgnoreCase) && l.Until > Now;

    /// <summary>Free for this pawn: nobody holds it, the lease ran out, or it's this pawn's own.</summary>
    private bool Free(string key, string pawn) =>
        !_leases.TryGetValue(key, out var l) || l.Until <= Now || string.Equals(l.Owner, pawn, StringComparison.OrdinalIgnoreCase);

    private void Release(ActiveJob j)
    {
        if (j.Lease == null) return;
        _leases.Remove(j.Lease);
        if (j.Source == "ghost" && FindGhost(j.SourceId) is { } g && g.State == "leased") { g.State = "todo"; g.Rev = Touch(); }
    }

    /// <summary>Drops a pawn's current job (an order, drafted, paused, stop) and cuts its action so it asks again.</summary>
    private void Supersede(PawnState p, string why)
    {
        if (_current.Remove(p.Name, out var cur))
        {
            Release(cur);
            _started.Remove(p.Name); // the watchdog starts it again at once (no 5 s gap)
            try { Host.Interrupt(p.Name, why); } catch (Exception ex) { _log.LogDebug(ex, "story: interrupt {Pawn}", p.Name); }
        }
    }

    /// <summary>
    /// New work (a build, a designation, a bill, a zone, a storage, priorities). An idle hold is a few seconds and the pawn picks again
    /// by itself, so nothing is cut here; the next <see cref="Tick"/> only cuts a pawn stuck in a long idle (walking home from far) that
    /// has work now, at most once per <see cref="WakeCooldown"/>. Live 11-03: cutting every idle pawn's script on every priority click
    /// restarted ten scripts per click ("cancelled ... (new work)" several times a second) and none of them got anywhere.
    /// </summary>
    internal void WakeIdle() => _wakePending = true;

    private bool _wakePending;
    private readonly Dictionary<string, DateTimeOffset> _woken = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An idle hold younger than this ends by itself (the pawn then picks the new work): it isn't cut.</summary>
    public static readonly TimeSpan WakeAfter = TimeSpan.FromSeconds(8);
    /// <summary>A pawn is woken for new work at most this often.</summary>
    public static readonly TimeSpan WakeCooldown = TimeSpan.FromSeconds(30);

    internal const string NothingToDo = "idle: nothing to do";

    private void WakeNow()
    {
        if (!_wakePending) return;
        _wakePending = false;
        if (Meta.Paused || !Meta.Active) return;
        foreach (var p in Pawns.Values)
        {
            if (p.Drafted || !_current.TryGetValue(p.Name, out var cur) || cur.Source != "none" || cur.Label != NothingToDo) continue;
            if (Now - cur.Since < WakeAfter) continue;
            if (_woken.TryGetValue(p.Name, out var at) && Now - at < WakeCooldown) continue;
            var live = Host.Live(p.Name);
            if (!live.Online || Work(p, live) == null) continue;
            _woken[p.Name] = Now;
            Supersede(p, "new work");
        }
    }

    /// <summary>The current job of a pawn (null = none).</summary>
    public ActiveJob? CurrentOf(string pawn) { lock (_lock) return _current.GetValueOrDefault(pawn); }

    /// <summary>Lease holders now (tests, the delta): key → pawn.</summary>
    public IReadOnlyDictionary<string, string> Leases()
    {
        lock (_lock) return _leases.Where(l => l.Value.Until > Now).ToDictionary(l => l.Key, l => l.Value.Owner);
    }

    // ================================================================ the tick (watchdog, leases, furnaces, saving)

    /// <summary>Once a second or so: expired leases go back, a pawn whose script ended is restarted, benches are found, state saved.</summary>
    public void Tick()
    {
        List<string> restart = new(), joined = new();
        lock (_lock)
        {
            foreach (var k in _leases.Where(l => l.Value.Until <= Now).Select(l => l.Key).ToList())
            {
                var owner = _leases[k].Owner;
                _leases.Remove(k);
                if (_current.TryGetValue(owner, out var cur) && cur.Lease == k) { _current.Remove(owner); Release(cur); }
            }
            if (Meta.Active)
                foreach (var p in Pawns.Values)
                {
                    var live = Host.Live(p.Name);
                    if (!live.Online || live.ScriptRunning) continue;
                    if (_started.TryGetValue(p.Name, out var at) && Now - at < TimeSpan.FromSeconds(5)) continue;
                    _started[p.Name] = Now;
                    if (_current.Remove(p.Name, out var stale)) Release(stale);
                    restart.Add(p.Name);
                }
            if (Now - _benchScan > TimeSpan.FromSeconds(30)) { _benchScan = Now; ScanBenches(); RetrySuspended(); } // missing ingredients may be stored by now
            WakeNow();
            // A pawn that (re)joined: brought home when it came online far away (founding, resume, a reconnect).
            foreach (var p in Pawns.Values)
            {
                bool online = Host.Live(p.Name).Online;
                if (online && _online.Add(p.Name)) joined.Add(p.Name);
                else if (!online) _online.Remove(p.Name);
            }
        }
        if (joined.Count > 0 && Meta.Active) BringHome("joined", joined);
        foreach (var name in restart)
        {
            string? err;
            try { err = Host.StartPawn(name, Id); } catch (Exception ex) { err = ex.Message; }
            if (err != null) _log.LogDebug("story {Colony}: {Pawn} didn't start: {Err}", Id, name, err);
        }
        Save();
    }

    private DateTimeOffset _benchScan = DateTimeOffset.MinValue;

    /// <summary>Writes the colony if anything changed since the last save.</summary>
    public void Save(bool force = false)
    {
        if (Store == null) return;
        lock (_lock)
        {
            if (!force && (!_dirty || Now - _lastSave < TimeSpan.FromSeconds(5))) return; // at most every 5 s
            _dirty = false;
            _lastSave = Now;
            try { Store.Save(this); }
            catch (Exception ex) { _dirty = true; _log.LogWarning(ex, "story {Colony}: save failed", Id); }
        }
    }

    public void Pause(bool paused)
    {
        lock (_lock)
        {
            if (Meta.Paused == paused) return;
            Meta.Paused = paused;
            Touch();
            if (paused) foreach (var p in Pawns.Values) Supersede(p, "colony paused");
        }
        Save(force: true);
    }

    /// <summary>Spawned pawns: their accounts become pawns and their scripts talk to this director.</summary>
    public void Joined(IEnumerable<string> accounts)
    {
        foreach (var a in accounts)
        {
            AddPawn(a);
            StoryPorts.Bind(a, this);
        }
    }

    public void Unbind()
    {
        lock (_lock)
            foreach (var p in Pawns.Keys) StoryPorts.Unbind(p, this);
    }

    // ================================================================ home

    private readonly HashSet<string> _online = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _broughtHome = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _homeAlert = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A pawn farther than this from home when it joins (or when home moves) is brought there.</summary>
    public const double HomeRange = 48;
    /// <summary>One pawn is brought home at most this often (the teleport takes a moment to show in its position).</summary>
    public static readonly TimeSpan BringHomeCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Moves the colony's home (persisted) and brings the pawns that are far from it there.</summary>
    public void SetHome(P3 home)
    {
        lock (_lock)
        {
            Meta.Home = home;
            Touch();
            _broughtHome.Clear(); // a new home: every far pawn goes, whatever it did a minute ago
        }
        Alert("info", $"home moved to {home}");
        Save(force: true);
        BringHome("home moved");
    }

    /// <summary>
    /// Pawns more than <see cref="HomeRange"/> from home (or in another dimension) get there: the colony owner founded the colony or set
    /// its home, so the bot (op) teleports each to a standable spot at home (<see cref="SafeSpot"/>) through the host's op command path,
    /// whose blocklist still applies. Where that can't be done (b1.8.1's /tp takes no coordinates, the gate refused) the pawn gets a
    /// move order home instead. A pawn in another dimension can't be teleported across: an alert says so. One log line per pawn moved.
    /// Returns how many were sent home.
    /// </summary>
    public int BringHome(string why, IReadOnlyCollection<string>? only = null)
    {
        var moves = new List<(PawnState P, P3 To, double Dist)>();
        lock (_lock)
        {
            if (Home() is not { } home || World is not { } w || !Meta.Active) return 0;
            P3? spot = null;
            bool looked = false;
            foreach (var p in Pawns.Values)
            {
                if (only != null && !only.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
                var live = Host.Live(p.Name);
                if (!live.Online) continue;
                if (_broughtHome.TryGetValue(p.Name, out var at) && Now - at < BringHomeCooldown) continue;
                if (live.Dim != w.Dimension)
                {
                    if (!_homeAlert.TryGetValue("dim:" + p.Name, out var last) || Now - last > TimeSpan.FromMinutes(10))
                    {
                        _homeAlert["dim:" + p.Name] = Now;
                        Alert("warn", $"{p.Display} is in another dimension and can't be brought home from there: walk it through a portal", p.Name);
                    }
                    continue;
                }
                double dist = home.Dist(live.X, live.Y, live.Z);
                if (dist <= HomeRange) continue;
                if (!looked) { looked = true; spot = SafeSpot(w, home); }
                if (spot is not { } to)
                {
                    if (!_homeAlert.TryGetValue("spot", out var last) || Now - last > TimeSpan.FromMinutes(5))
                    {
                        _homeAlert["spot"] = Now;
                        Alert("warn", $"no safe spot to bring pawns to at home {home} (not loaded in the bot's view, or no ground there)");
                    }
                    return 0;
                }
                _broughtHome[p.Name] = Now;
                moves.Add((p, to, dist));
            }
        }
        foreach (var (p, to, dist) in moves)
        {
            string? err;
            try { err = Host.Teleport(p.Name, to, $"story {Id}: {why}"); } catch (Exception ex) { err = ex.Message; }
            lock (_lock)
            {
                if (err == null)
                {
                    Supersede(p, "brought home"); // whatever it was doing over there is cut; it picks again at home
                    Alert("info", $"{p.Display} brought home to {to} from {dist:0} blocks away ({why})", p.Name);
                }
                else
                {
                    // Walk it there instead: a move order runs before any work.
                    if (!p.Queue.Any(o => o.Kind == "move" && o.At == to))
                    {
                        p.Queue.Insert(0, new Order { Id = NextId("o"), Kind = "move", At = to, Given = Now });
                        p.Rev = Touch();
                        Supersede(p, "walking home");
                    }
                    Alert("info", $"{p.Display} walks home to {to} from {dist:0} blocks away ({why}; no teleport: {err})", p.Name);
                }
            }
        }
        return moves.Count;
    }

    /// <summary>
    /// A standable spot at home: solid ground (not a leaf, liquid, fire or cactus) under two free cells, the home cell itself when it is
    /// one (a cave or a floor), else the open-sky surface of the nearest column around it. Null when nothing around is loaded or standable.
    /// </summary>
    internal static P3? SafeSpot(IStoryWorld w, P3 home, int radius = 4)
    {
        bool Free(int x, int y, int z) => w.Block(x, y, z).Id is var id && (id == 0 || StoryBlocks.Plants.Contains(id) || id is 50 or 78);
        bool Floor(int x, int y, int z) => w.Block(x, y, z).Id is var id && !Free(x, y, z) && !StoryBlocks.Liquids.Contains(id)
            && !StoryBlocks.Leaves.Contains(id) && id is not (51 or 81 or 30 or 10 or 11);
        bool Standable(int x, int y, int z) => y > 0 && y < w.Height - 1 && Floor(x, y - 1, z) && Free(x, y, z) && Free(x, y + 1, z)
            && !StoryBlocks.Liquids.Contains(w.Block(x, y, z).Id);
        for (int r = 0; r <= radius; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    int x = home.X + dx, z = home.Z + dz;
                    if (!w.Loaded(x, z)) continue;
                    for (int dy = 0; dy <= 2; dy++) // the home cell itself, or just above it
                        if (Standable(x, home.Y + dy, z)) return new P3(x, home.Y + dy, z);
                    // The open-sky surface: the first block down from the top that isn't free.
                    int y = w.Height - 2;
                    while (y > 0 && Free(x, y, z)) y--;
                    if (y > 0 && Standable(x, y + 1, z)) return new P3(x, y + 1, z);
                }
        return null;
    }

    /// <summary>The colony's home point: its setting, else the first home zone's centre, else the first pawn's spot.</summary>
    public P3? Home()
    {
        if (Meta.Home is { } h) return h;
        if (Zones.FirstOrDefault(z => z.Kind == "home") is { } hz) return hz.Area.Center;
        return null;
    }
}
