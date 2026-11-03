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

    /// <summary>Restores the revision counter after a load (objects keep their saved revs).</summary>
    internal void SetRev(long rev) { lock (_lock) _rev = Math.Max(_rev, rev); }

    private string NextId(string prefix) { Meta.Seq++; _dirty = true; return prefix + Meta.Seq; }

    public void Alert(string level, string text, string? pawn = null)
    {
        lock (_lock)
        {
            Alerts.Add(new AlertState(Now, level, text, pawn, Touch()));
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
            p.Rev = Touch();
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
                    if (p != null) p.Rev = Touch();
                    return true;
                case "done":
                    _current.Remove(body);
                    Release(cur);
                    if (p != null) Done(p, cur, note);
                    _lastSource[body] = cur.Source;
                    if (p != null) p.Rev = Touch();
                    return true;
                default:
                    _current.Remove(body);
                    Release(cur);
                    if (p != null) Failed(p, cur, note);
                    _lastSource[body] = "failed";
                    if (p != null) p.Rev = Touch();
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
            try { Host.Interrupt(p.Name, why); } catch (Exception ex) { _log.LogDebug(ex, "story: interrupt {Pawn}", p.Name); }
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
        List<string> restart = new();
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
            if (Now - _benchScan > TimeSpan.FromSeconds(30)) { _benchScan = Now; ScanBenches(); }
        }
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
            if (!_dirty && !force) return;
            _dirty = false;
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

    /// <summary>The colony's home point: its setting, else the first home zone's centre, else the first pawn's spot.</summary>
    public P3? Home()
    {
        if (Meta.Home is { } h) return h;
        if (Zones.FirstOrDefault(z => z.Kind == "home") is { } hz) return hz.Area.Center;
        return null;
    }
}
