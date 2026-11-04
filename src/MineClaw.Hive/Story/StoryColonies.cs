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
        foreach (var c in All)
        {
            try { c.Tick(); }
            catch (Exception ex) { _log.LogWarning(ex, "story {Colony}: tick failed", c.Id); } // a timer callback never throws
            try { RetrySeats(c); }
            catch (Exception ex) { _log.LogWarning(ex, "story {Colony}: seat retry failed", c.Id); }
        }
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
        var now = _time.GetUtcNow();
        lock (_nextRetry)
        {
            if (!force && _nextRetry.TryGetValue(c.Id, out var at) && now < at) return;
            _nextRetry[c.Id] = now + RetryEvery;
        }
        var missing = new List<string>();
        foreach (var a in c.PawnNames())
        {
            if (_host.Live(a).Online) { c.NoteTrouble(a, null); continue; }
            var seat = _host.Seat(a);
            if (!seat.Hosted) missing.Add(a);
            else c.NoteTrouble(a, seat.Why is { } w && w.StartsWith("not whitelisted — ", StringComparison.Ordinal) && !w.EndsWith("re-adding", StringComparison.Ordinal) ? w : null);
        }
        if (missing.Count > 0)
        {
            var res = _host.Bring(c.Id, missing, overCap: false);
            Came(c, res);
            return;
        }
        int short_ = c.Meta.Wanted - c.PawnNames().Count;
        if (short_ > 0 && _host.Seats() is { } s && s.Free - 1 >= s.Kept)
        {
            (IReadOnlyList<string> Accounts, string? Why) r;
            try { r = _host.Spawn(c.Id, short_, null); } catch (Exception ex) { r = ([], ex.Message); }
            if (r.Accounts.Count > 0)
            {
                c.Joined(r.Accounts);
                c.Alert("info", $"{r.Accounts.Count} more pawn(s) joined now that seats are free ({string.Join(", ", r.Accounts.Select(MineClaw.Hive.Hierarchy.HiveNode.ShortOf))})");
                c.Save(force: true);
            }
        }
    }

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

    /// <summary>A new colony with <paramref name="pawns"/> (1..12) script-only bodies → (id, error).</summary>
    public (StoryColony? Colony, string? Error) Create(string name, int pawns, P3? home)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40) return (null, "name: 1-40 characters");
        if (pawns is < 1 or > StoryColony.MaxPawns) return (null, $"pawns: 1..{StoryColony.MaxPawns}");
        StoryColony c;
        lock (_lock)
        {
            int n = 1;
            while (_all.Any(x => x.Id == $"story-{n}")) n++;
            c = new StoryColony(new ColonyMeta { Id = $"story-{n}", Name = name.Trim(), Created = _time.GetUtcNow(), Home = home, Wanted = pawns }, _host, _time, _store, _log);
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
        if (r.Accounts.Count < n) c.Alert("warn", $"only {r.Accounts.Count} of {n} pawns came: {r.Why ?? "no room"}");
        _log.LogInformation("story {Colony}: {N} pawn(s) spawned ({Names})", c.Id, r.Accounts.Count, string.Join(", ", r.Accounts));
        return r.Why;
    }

    // ================================================================ adding and removing pawns (the colonist bar's + and −)

    /// <summary>
    /// + : <paramref name="n"/> more pawns. The colony's wanted size grows (at most <see cref="StoryColony.MaxPawns"/>); as many as the
    /// free seats allow (never the seats kept for offline regulars) spawn now through the usual path (managed accounts, whitelisted
    /// through main), the rest are pending and come with the seat retry as seats free up.
    /// </summary>
    public (JsonObject? Result, string? Error) AddPawns(StoryColony c, int n)
    {
        if (!c.Meta.Active) return (null, "the colony is released: resume it first");
        if (n is < 1 or > StoryColony.MaxPawns) return (null, $"count: 1..{StoryColony.MaxPawns}");
        int staying = c.Staying, wanted = Math.Max(c.Meta.Wanted, staying);
        if (wanted + n > StoryColony.MaxPawns)
            return (null, $"a colony has at most {StoryColony.MaxPawns} pawns ({wanted} now{(wanted > staying ? $", {wanted - staying} of them joining" : "")})");
        c.Meta.Wanted = wanted + n;
        int now = n;
        string? why = null;
        if (_host.Seats() is { } s)
        {
            now = Math.Clamp(s.Free - s.Kept, 0, n);
            if (now < n) why = $"server full (player cap: {s.Free} free, {s.Kept} kept for regulars)";
        }
        var came = new List<string>();
        if (now > 0)
        {
            (IReadOnlyList<string> Accounts, string? Why) r;
            try { r = _host.Spawn(c.Id, now, null); } catch (Exception ex) { r = ([], ex.Message); }
            came.AddRange(r.Accounts);
            if (r.Accounts.Count < now) why ??= r.Why ?? "no account free";
            c.Joined(r.Accounts);
            Scripting.Crew.Crews.Open(c.Id);
        }
        _log.LogInformation("story {Colony}: + {N} pawn(s): {Came} now, wanted {Wanted} {Why}", c.Id, n, came.Count, c.Meta.Wanted, why ?? "");
        c.Alert("info", came.Count == n ? $"{n} pawn(s) joining: {string.Join(", ", came.Select(MineClaw.Hive.Hierarchy.HiveNode.ShortOf))}"
            : $"{came.Count} of {n} new pawn(s) joining now; the rest come when seats free up ({why})");
        c.Save(force: true);
        if (came.Count > 0) c.BringHome("joined", came);
        return (Roster(c, why), null);
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

    private static JsonObject Roster(StoryColony c, string? why)
    {
        var names = c.PawnNames().Where(a => !c.IsLeaving(a)).ToList();
        int pending = Math.Max(0, c.Meta.Wanted - names.Count);
        var blocked = new JsonArray();
        if (why != null && pending > 0) blocked.Add(new JsonObject { ["reason"] = why });
        return new JsonObject
        {
            ["pawns"] = new JsonArray(names.Select(a => (JsonNode)a!).ToArray()),
            ["wanted"] = c.Meta.Wanted,
            ["pending"] = new JsonArray(Enumerable.Range(0, pending).Select(_ => (JsonNode)new JsonObject { ["reason"] = why ?? "waiting for a seat" }).ToArray()),
            ["leaving"] = new JsonArray(c.PawnNames().Where(c.IsLeaving).Select(a => (JsonNode)a!).ToArray()),
            ["blocked"] = blocked,
        };
    }

    /// <summary>The pawns deposit and log out; the colony's state is kept (it is inactive until resumed).</summary>
    public int Release(StoryColony c)
    {
        c.Meta.Active = false;
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
