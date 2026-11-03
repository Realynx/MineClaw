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
            try { c.Tick(); }
            catch (Exception ex) { _log.LogWarning(ex, "story {Colony}: tick failed", c.Id); } // a timer callback never throws
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
