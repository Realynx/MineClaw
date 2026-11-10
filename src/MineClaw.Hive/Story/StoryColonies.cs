using System.Reflection;
using System.Text.Json.Nodes;
using MineClaw.Agent.Routines;
using MineClaw.Scripting.Story;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Story;

/// <summary>
/// Every colony of this bot (main's process): create (spawn the script-only bodies of group <c>story-&lt;n&gt;</c>), release (bodies
/// deposit and log out; the state stays), resume after a restart (the same accounts first), and the once-a-second tick that keeps
/// each director going. Pawn scripts find their colony through <see cref="StoryPorts"/>.
/// </summary>
public sealed class StoryColonies : IDisposable
{
    private readonly object _lock = new();
    private readonly List<StoryColony> _all = new();
    private readonly IStoryHost _host;
    private readonly StoryStore? _store;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private Timer? _timer;
    private int _ticking;

    public StoryColonies(IStoryHost host, StoryStore? store = null, TimeProvider? time = null, ILogger? log = null)
    {
        _host = host;
        _store = store;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    public IStoryHost Host => _host;
    public IReadOnlyList<StoryColony> All { get { lock (_lock) return _all.ToList(); } }
    public StoryColony? Get(string id) { lock (_lock) return _all.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); }

    /// <summary>Loads the saved colonies; the active ones respawn their pawns (their old accounts first) and resume.</summary>
    public int Load()
    {
        if (_store == null) return 0;
        var loaded = _store.Load(m => new StoryColony(m, _host, _time, _store, _log));
        lock (_lock) _all.AddRange(loaded);
        foreach (var c in loaded.Where(c => c.Meta.Active)) Spawn(c, c.Meta.Wanted, c.Pawns.Keys.ToList());
        return loaded.Count;
    }

    /// <summary>Starts the tick (1 s): the watchdog, leases, benches, saving.</summary>
    public void Start(TimeSpan? period = null)
    {
        var p = period ?? TimeSpan.FromSeconds(1);
        _timer = new Timer(_ => Tick(), null, p, p);
    }

    public void Tick()
    {
        // A long reconciliation may outlive the periodic timer. Its state is current when it finishes;
        // duplicate callbacks must not occupy pool workers waiting for that same colony lock.
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
        try
        {
            foreach (var c in All)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { c.Tick(); }
                catch (Exception ex) { _log.LogWarning(ex, "story {Colony}: tick failed", c.Id); } // a timer callback never throws
                finally { StoryPerf.Note(ref StoryPerf.Ticks, ref StoryPerf.TickTicks, ref StoryPerf.TickMax, t0); }
                try { RetrySeats(c); }
                catch (Exception ex) { _log.LogWarning(ex, "story {Colony}: seat retry failed", c.Id); }
            }
        }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    /// <summary>How long after a direct order the restart pass runs: time for the cut scripts to stop, well before the next tick.</summary>
    public static readonly TimeSpan NudgeDelay = TimeSpan.FromMilliseconds(150);
    private readonly Dictionary<string, ITimer> _nudges = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A direct order cut pawns' scripts: the colony's restart pass (<see cref="StoryColony.RestartNow"/>) runs once,
    /// <see cref="NudgeDelay"/> from now, instead of at the next tick. Nudges inside that window share the one pass. Never for
    /// draft, pause or designations, and it never wakes an idle pawn (only the tick does that, by its own rules).
    /// </summary>
    public void Nudge(StoryColony c)
    {
        lock (_nudges)
        {
            if (_nudges.ContainsKey(c.Id)) return;
            _nudges[c.Id] = _time.CreateTimer(_ => Restart(c), null, NudgeDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Restart(StoryColony c)
    {
        lock (_nudges)
            if (_nudges.Remove(c.Id, out var timer)) timer.Dispose();
        try { c.RestartNow(); }
        catch (Exception ex) { _log.LogWarning(ex, "story {Colony}: restart pass failed", c.Id); } // a timer callback never throws
    }

    /// <summary>Is the account a pawn of an active colony (spawned or still waiting for a seat)? The startup reconcile keeps those.</summary>
    public bool Owns(string account) => All.Any(c => c.Meta.Active && c.HasPawn(account));

    /// <summary>How often an active colony tries again to bring in the pawns that aren't hosted (the player cap, a body that left).</summary>
    public TimeSpan RetryEvery { get; set; } = TimeSpan.FromSeconds(15);
    private readonly Dictionary<string, DateTimeOffset> _nextRetry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The seat retry: an active colony's pawns that aren't hosted (they didn't fit under the player cap, or yielded a seat to a regular)
    /// are brought in again as seats free up, the same accounts only; a colony founded short grows to its wanted size the same way.
    /// Refusals become each pawn's offline_reason. Hosted pawns whose login trouble needs the owner (main can't whitelist) raise an alert.
    /// </summary>
    public void RetrySeats(StoryColony c, bool force = false)
    {
        if (!c.Meta.Active || !_host.InGame) return;
        if (!RetryDue(c, force)) return;
        var missing = UnhostedPawns(c);
        if (missing.Count > 0)
        {
            var res = _host.Bring(c.Id, missing, overCap: false);
            Came(c, res);
            return;
        }
        GrowToWanted(c);
    }

    /// <summary>The colony's retry is due (or forced): the next one is set <see cref="RetryEvery"/> from now.</summary>
    private bool RetryDue(StoryColony c, bool force)
    {
        var now = _time.GetUtcNow();
        lock (_nextRetry)
        {
            if (!force && _nextRetry.TryGetValue(c.Id, out var at) && now < at) return false;
            _nextRetry[c.Id] = now + RetryEvery;
            return true;
        }
    }

    /// <summary>The pawns not in game whose bodies aren't hosted; the hosted ones' login trouble is noted (or cleared).</summary>
    private List<string> UnhostedPawns(StoryColony c)
    {
        var missing = new List<string>();
        foreach (var a in c.PawnNames())
        {
            if (_host.Live(a).Online) { c.NoteTrouble(a, null); continue; }
            var seat = _host.Seat(a);
            if (!seat.Hosted) missing.Add(a);
            else c.NoteTrouble(a, OwnerTrouble(seat.Why));
        }
        return missing;
    }

    /// <summary>Login trouble that needs the owner: main can't whitelist the body (and isn't re-adding it).</summary>
    private static string? OwnerTrouble(string? why) =>
        why is { } w && w.StartsWith("not whitelisted — ", StringComparison.Ordinal) && !w.EndsWith("re-adding", StringComparison.Ordinal) ? w : null;

    /// <summary>A colony founded short (or grown with +) comes up to its wanted size as seats and memory free up (never the seats kept
    /// for regulars): as many as fit, each retry.</summary>
    private void GrowToWanted(StoryColony c)
    {
        int short_ = c.Meta.Wanted - c.PawnNames().Count;
        if (short_ <= 0 || _host.SeatInfo() is not { Room: > 0 } s) return;
        short_ = Math.Min(short_, s.Room);
        (IReadOnlyList<string> Accounts, string? Why) r;
        try { r = _host.Spawn(c.Id, short_, null); } catch (Exception ex) { r = ([], ex.Message); }
        if (r.Accounts.Count == 0) return;
        c.Joined(r.Accounts);
        c.Alert("info", $"{r.Accounts.Count} more pawn(s) joined now that seats are free ({ShortNames(r.Accounts)})");
        c.Save(force: true);
    }

    /// <summary>"101, 102, 105" (at most 8 names, then "+N more": a colony has no upper limit).</summary>
    private static string ShortNames(IReadOnlyList<string> accounts) =>
        string.Join(", ", accounts.Take(8).Select(MineClaw.Hive.Hierarchy.HiveNode.ShortOf)) + (accounts.Count > 8 ? $" +{accounts.Count - 8} more" : "");

    private void Came(StoryColony c, IReadOnlyList<(string Account, string? Why)> res)
    {
        var came = res.Where(r => r.Why == null).Select(r => r.Account).ToList();
        if (came.Count > 0)
        {
            c.Joined(came); // their scripts talk to this director again
            Scripting.Crew.Crews.Open(c.Id);
            _log.LogInformation("story {Colony}: bringing {Names} online", c.Id, string.Join(", ", came));
        }
        c.NoteSeats(res);
    }

    /// <summary>
    /// "Bring online" (<c>POST …/online</c>): these pawns (null = every offline one) are whitelisted again (through main) and (re)started
    /// now, the player cap permitting; <paramref name="overCap"/> lets them take the seats kept for offline regulars. → (started, blocked).
    /// </summary>
    public (List<string> Started, List<(string Pawn, string Reason)> Blocked) BringOnline(StoryColony c, IReadOnlyList<string>? pawns, bool overCap)
    {
        var started = new List<string>();
        var blocked = new List<(string, string)>();
        var names = pawns ?? c.PawnNames().Where(a => !_host.Live(a).Online).ToList();
        var bring = new List<string>();
        foreach (var a in names)
        {
            if (!c.Meta.Active) blocked.Add((a, "the colony is released: resume it first"));
            else if (_host.Live(a).Online) started.Add(a); // in game already
            else bring.Add(a);
        }
        if (bring.Count > 0)
        {
            var res = _host.Bring(c.Id, bring, overCap);
            Came(c, res);
            foreach (var (a, why) in res)
                if (why == null) started.Add(a); else blocked.Add((a, why));
        }
        return (started, blocked);
    }

    /// <summary>
    /// A new colony that wants <paramref name="pawns"/> script-only bodies (1 or more: no upper limit) → (id, error). As many as fit
    /// now come (the server's seats, never the regulars', and the memory ceiling); the rest wait and join with the seat retry as room
    /// frees up. It fails only when not one could come.
    /// </summary>
    public (StoryColony? Colony, string? Error) Create(string name, int pawns, P3? home)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40) return (null, "name: 1-40 characters");
        if (pawns < 1) return (null, "pawns: 1 or more");
        StoryColony c;
        lock (_lock)
        {
            int n = 1;
            while (_all.Any(x => x.Id == $"story-{n}")) n++;
            c = new StoryColony(new ColonyMeta { Id = $"story-{n}", Name = name.Trim(), Created = _time.GetUtcNow(), Home = home, Wanted = pawns, WaterRetried = true, WaterCellsFixed = true }, _host, _time, _store, _log);
            _all.Add(c);
        }
        var why = Spawn(c, pawns, null);
        if (c.Pawns.Count == 0)
        {
            lock (_lock) _all.Remove(c);
            return (null, why ?? "no pawn could spawn");
        }
        c.Save(force: true);
        c.BringHome("colony founded"); // pawns that came online far away (a reused body) go home; later joins: the tick
        return (c, null);
    }

    private string? Spawn(StoryColony c, int n, IReadOnlyList<string>? prefer)
    {
        (IReadOnlyList<string> Accounts, string? Why) r;
        try { r = _host.Spawn(c.Id, Math.Max(1, n), prefer); }
        catch (Exception ex) { r = ([], ex.Message); }
        c.Joined(r.Accounts);
        Scripting.Crew.Crews.Open(c.Id); // its bodies are this group's (main's wake digest holds their events back)
        if (r.Accounts.Count < n && r.Accounts.Count > 0)
            c.Alert("info", $"{r.Accounts.Count} of {n} pawns came now; the other {n - r.Accounts.Count} wait and join as room frees up ({r.Why ?? "no room"})");
        else if (r.Accounts.Count < n) c.Alert("warn", $"no pawn could come: {r.Why ?? "no room"}");
        _log.LogInformation("story {Colony}: {N} pawn(s) spawned ({Names})", c.Id, r.Accounts.Count, string.Join(", ", r.Accounts));
        return r.Why;
    }

    // ================================================================ adding and removing pawns (the colonist bar's + and −)

    /// <summary>
    /// + : <paramref name="n"/> more pawns (no upper limit). The colony's wanted size grows; as many as the free seats allow (never the
    /// seats kept for offline regulars) and the memory ceiling takes spawn now through the usual path (managed accounts, whitelisted
    /// through main), the rest are pending and come with the seat retry as room frees up.
    /// </summary>
    public (JsonObject? Result, string? Error) AddPawns(StoryColony c, int n)
    {
        if (!c.Meta.Active) return (null, "the colony is released: resume it first");
        if (n < 1) return (null, "count: 1 or more");
        int wanted = Math.Max(c.Meta.Wanted, c.Staying);
        if (n > int.MaxValue - wanted) return (null, "count: too many to count");
        c.Meta.Wanted = wanted + n;
        var (now, why) = SeatsNow(n);
        var came = new List<string>();
        if (now > 0) why = SpawnInto(c, now, came, why);
        _log.LogInformation("story {Colony}: + {N} pawn(s): {Came} now, wanted {Wanted} {Why}", c.Id, n, came.Count, c.Meta.Wanted, why ?? "");
        c.Alert("info", came.Count == n ? $"{n} pawn(s) joining: {ShortNames(came)}"
            : $"{came.Count} of {n} new pawn(s) joining now; the other {n - came.Count} come as room frees up ({why})");
        c.Save(force: true);
        if (came.Count > 0) c.BringHome("joined", came);
        return (Roster(c, why), null);
    }

    /// <summary>How many of <paramref name="n"/> new pawns come now (the free seats, never the ones kept for regulars, and the memory
    /// ceiling), and why not all: the limit that binds.</summary>
    private (int Now, string? Why) SeatsNow(int n)
    {
        if (_host.SeatInfo() is not { } s) return (n, null);
        return (Math.Clamp(s.Room, 0, n), s.WhyWaiting(n));
    }

    /// <summary>Spawns <paramref name="now"/> pawns into the colony (added to <paramref name="came"/>); the reason when fewer came.</summary>
    private string? SpawnInto(StoryColony c, int now, List<string> came, string? why)
    {
        (IReadOnlyList<string> Accounts, string? Why) r;
        try { r = _host.Spawn(c.Id, now, null); } catch (Exception ex) { r = ([], ex.Message); }
        came.AddRange(r.Accounts);
        if (r.Accounts.Count < now) why ??= r.Why ?? "no account free";
        c.Joined(r.Accounts);
        Scripting.Crew.Crews.Open(c.Id);
        return why;
    }

    /// <summary>
    /// − : the pawn leaves. Its job, claims and bed go back now; it stores what it carries, then logs out and its account is released.
    /// The last pawn can't be removed (release the colony instead).
    /// </summary>
    public (JsonObject? Result, string? Error, bool NotFound) RemovePawn(StoryColony c, string pawn)
    {
        var (ps, err) = c.Resolve([pawn]);
        if (err != null) return (null, err, true);
        var p = ps[0];
        if (c.IsLeaving(p.Name)) return (Roster(c, null), null, false);
        if (c.Staying <= 1) return (null, "the last pawn can't be removed: release the colony instead", false);
        c.Leave(p.Name);
        c.Meta.Wanted = Math.Max(Math.Max(1, c.Staying), c.Meta.Wanted - 1);
        _log.LogInformation("story {Colony}: − {Pawn} leaves (wanted {Wanted})", c.Id, p.Name, c.Meta.Wanted);
        c.Alert("info", $"{p.Display} is leaving the colony: its things go to storage first", p.Name);
        c.Save(force: true);
        return (Roster(c, null), null, false);
    }

    /// <summary>{pawns, wanted, pending:[{reason, count}], leaving, blocked:[{reason}]}: the pawns still to come are one entry with a
    /// count (never one per pawn: there is no upper limit).</summary>
    private static JsonObject Roster(StoryColony c, string? why)
    {
        var names = c.PawnNames().Where(a => !c.IsLeaving(a)).ToList();
        int pending = Math.Max(0, c.Meta.Wanted - names.Count);
        var blocked = new JsonArray();
        if (why != null && pending > 0) blocked.Add(new JsonObject { ["reason"] = why });
        var waiting = new JsonArray();
        if (pending > 0) waiting.Add(new JsonObject { ["reason"] = why ?? "waiting for a seat", ["count"] = pending });
        return new JsonObject
        {
            ["pawns"] = new JsonArray(names.Select(a => (JsonNode)a!).ToArray()),
            ["wanted"] = c.Meta.Wanted,
            ["pending"] = waiting,
            ["leaving"] = new JsonArray(c.PawnNames().Where(c.IsLeaving).Select(a => (JsonNode)a!).ToArray()),
            ["blocked"] = blocked,
        };
    }

    /// <summary>The pawns deposit and log out; the colony's state is kept (it is inactive until resumed).</summary>
    public int Release(StoryColony c)
    {
        c.Meta.Active = false;
        c.ResetSpeed(); // the bodies walk at normal speed again (StoryColony.Speed.cs)
        c.Unbind();
        int n;
        try { n = _host.Release(c.Id); } catch (Exception ex) { _log.LogWarning(ex, "story {Colony}: release failed", c.Id); n = 0; }
        Scripting.Crew.Crews.Close(c.Id);
        c.Alert("info", $"colony released: {n} pawn(s) deposit and log out (its state is kept)");
        c.Save(force: true);
        return n;
    }

    /// <summary>A released colony spawns its pawns again (its old accounts first).</summary>
    public string? Resume(StoryColony c)
    {
        if (c.Meta.Active) return null;
        c.Meta.Active = true;
        var why = Spawn(c, Math.Max(1, c.Meta.Wanted), c.Pawns.Keys.ToList());
        c.Save(force: true);
        c.BringHome("colony resumed");
        return why;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_nudges)
        {
            foreach (var t in _nudges.Values) t.Dispose();
            _nudges.Clear();
        }
        foreach (var c in All) { try { c.Save(force: true); } catch (Exception) { } }
    }

    // ================================================================ the default pawn script

    public const string PawnScript = "story_pawn";
    public const string DefaultAuthor = "mineclaw (default)";

    /// <summary>The shipped <c>story_pawn</c> source (embedded).</summary>
    public static string PawnCode()
    {
        using var s = typeof(StoryColonies).Assembly.GetManifestResourceStream("MineClaw.Hive.Story.story_pawn.js")
            ?? throw new InvalidOperationException("story_pawn.js resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>
    /// Installs <c>story_pawn</c> into the script library by default: saved when missing, and updated when the saved active version is
    /// an older shipped default (a version someone else saved is left alone). Returns what it did.
    /// </summary>
    public static string Install(ScriptLibrary library)
    {
        var code = PawnCode();
        var cur = library.Get(PawnScript)?.Script;
        if (cur != null)
        {
            var active = ScriptLibrary.Active(cur);
            if (active.Code.Replace("\r\n", "\n") == code) return "current";
            if (!string.Equals(active.Author, DefaultAuthor, StringComparison.Ordinal)) return "kept (edited by " + (active.Author ?? "someone") + ")";
        }
        var res = library.SaveVersion(PawnScript, code, note: "shipped default", author: DefaultAuthor, purpose: MineClaw.Scripting.ScriptLint.Purpose(code),
            argNames: [], lang: "js");
        return res.Refused ? "refused" : cur == null ? "installed" : "updated";
    }
}
