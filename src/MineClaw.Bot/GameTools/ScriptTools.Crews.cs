using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Bodies;
using MineClaw.Scripting;
using MineClaw.Scripting.Crew;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Crews this brain runs over some of its bodies (Fox 10-30: "a hive should be able to assign scripts to all or some or just one of
/// its bodies"): <c>script{name, body:["67","69"]|"all"|"67"}</c> on a script that works through roles (everyBody/parallel/spawn) is
/// ONE bodiless coordinator over exactly those bodies, not a copy per body, and no body of the brain's own is held by it (main keeps
/// talking and working; bodies outside the subset keep their jobs). The brain's planner sends the resolved bodies in
/// <see cref="CrewBodiesKey"/> (never in a schema: the body parameter lives in the cached bodies hint). A hive spawned with
/// <c>script=X</c> starts the same kind of crew over the bodies it is given (late joins via its member list). One body, one crew: a
/// body another live crew holds is reported, not taken. The crew's one result wakes the brain; its lanes never do.
/// </summary>
public sealed partial class ScriptBridge
{
    /// <summary>The planner's hidden field on a script call: the crew's bodies (short names), or "all".</summary>
    public const string CrewBodiesKey = "__bodies";

    /// <summary>A running crew this bridge coordinates.</summary>
    public sealed class CrewRun
    {
        private readonly Func<IReadOnlyList<string>> _bodies;

        internal CrewRun(string id, string script, ScriptSession session, Func<IReadOnlyList<string>> bodies)
        {
            Id = id;
            Script = script;
            Session = session;
            _bodies = bodies;
        }

        public string Id { get; }
        public string Script { get; }
        public ScriptSession Session { get; }
        /// <summary>Its bodies in game now.</summary>
        public IReadOnlyList<string> Bodies => _bodies();
        public bool Stop(string reason) => Session.Stop(reason);
        public string State() => $"crew {Script} on {Ranges(Bodies)}: {Session.State()}";
    }

    /// <summary>
    /// What a crew runs on. <see cref="Bodies"/> = a fixed list (each must be free now; the rest are reported); null = every body of
    /// this bridge's crew port that no other live crew holds, late logins too (or exactly <see cref="Members"/>, e.g. the accounts a
    /// hive spawned straight onto the script, tagged as they are hosted).
    /// </summary>
    public sealed record CrewSpec
    {
        public required ScriptRequest Request { get; init; }
        public IReadOnlyList<string>? Bodies { get; init; }
        public Func<IReadOnlyCollection<string>>? Members { get; init; }
        /// <summary>Bodies still to come (everyBody waits for them); null = none beyond the port's own.</summary>
        public Func<int>? Pending { get; init; }
        public string? Id { get; init; }
        /// <summary>Its end (ok, one line), however it ends.</summary>
        public Action<bool, string>? Ended { get; init; }
        /// <summary>True: no <c>[crew …]</c> wake for the brain (the caller reports the end itself).</summary>
        public bool Quiet { get; init; }
    }

    private readonly ConcurrentDictionary<string, CrewRun> _crewRuns = new(StringComparer.Ordinal);
    private int _crewSeq;

    /// <summary>The crews this bridge coordinates now.</summary>
    public IReadOnlyList<CrewRun> CrewRuns => _crewRuns.Values.ToList();

    /// <summary>"deforest@maid_chan_101#2-1730312345678": readable (script, coordinator) and never reused (the db board outlives runs).</summary>
    public string NewCrewId(string script) =>
        $"{script}@{Controller.Host.Username}#{Interlocked.Increment(ref _crewSeq)}-{_time.GetUtcNow().ToUnixTimeMilliseconds()}";

    /// <summary>"67-69, 101": short names as ranges.</summary>
    private static string Ranges(IReadOnlyList<string> names) => names.Count == 0 ? "no body yet" : string.Join(",", names);

    /// <summary>
    /// Starts ONE bodiless coordinator (<see cref="Coordinator"/>) of <paramref name="c"/>'s script over its bodies. Returns the run and
    /// "ok: …" (with the bodies another crew holds, "not taken: 67 (in crew …)"), or null and why not.
    /// </summary>
    public (CrewRun? Run, string Text) StartCrew(CrewSpec c)
    {
        if (Crew is not { } port) return (null, "no other bodies here to run a crew on");
        var req0 = c.Request;
        if (CrewScriptRefusal(req0) is { } why) return (null, why);
        var id = c.Id ?? NewCrewId(req0.Name);
        var notTaken = new List<string>();
        var (members, pending, error) = CrewMembers(c, port, id, notTaken);
        if (error != null) return (null, error);
        var sub = new SubsetCrewPort(port, members!, id, pending);
        Crews.Open(id); // its bodies are this crew's from now on (one body, one crew; the wake digest), not from its first lane
        var controller = Coordinator(sub, id);
        var req = req0 with { CrewId = id, Unattended = true, Coordinator = Controller.Host.Username };
        var (s, err) = controller.Start(req);
        if (s == null)
        {
            Crews.Close(id);
            controller.Dispose();
            return (null, err ?? "can't start");
        }
        var run = new CrewRun(id, req.Name, s, sub.Bodies);
        _crewRuns[id] = run;
        _log?.LogInformation("crew {Id}: {Script} v{V} over {Bodies} (bodiless coordinator){NotTaken}", id, req.Name, req.Version,
            c.Bodies != null ? string.Join(",", members!()) : c.Members != null ? "its spawned bodies" : "all bodies", NotTakenSuffix(notTaken));
        _ = s.Completion.ContinueWith(_ => OnCrewEnded(c, id, req.Name, s, controller), TaskScheduler.Default);
        return (run, $"ok: crew {req.Name} running on {CrewWho(c, members!)}: ONE coordinator, no body of yours held; you hear its result " +
            $"(script{{op:cancel,name:{req.Name}}} ends it)" + NotTakenSuffix(notTaken));
    }

    /// <summary>Why a script can't coordinate a crew (legacy Lua, or no everyBody role), else null.</summary>
    private static string? CrewScriptRefusal(ScriptRequest req)
    {
        if (req.Lang != ScriptLanguage.Js) return $"{req.Name} is legacy Lua: a crew script is JS (everyBody)";
        if (!Crews.IsCrewScript(req.Code))
            return $"{req.Name} isn't a crew script: its main() works the bodies through a role (everyBody('worker', args)); give each body its own run";
        return null;
    }

    private static string NotTakenSuffix(List<string> notTaken) => notTaken.Count > 0 ? "; not taken: " + string.Join(", ", notTaken) : "";

    /// <summary>The crew's bodies and the ones still to come: a named list (each held for this crew now; a body that left is
    /// dropped, never waited for), else the spec's members or every body (late ones too). Bodies another crew holds go to
    /// <paramref name="notTaken"/>; an error when a named list got none.</summary>
    private static (Func<IReadOnlyCollection<string>>? Members, Func<int>? Pending, string? Error) CrewMembers(CrewSpec c, ICrewPort port, string id,
        List<string> notTaken)
    {
        Func<int>? pending = c.Pending;
        if (c.Bodies is { } list)
        {
            var got = HoldListed(port, list, id, notTaken);
            if (got.Count == 0) return (null, null, "no body free for this crew: " + string.Join(", ", notTaken));
            return (() => got, pending ?? (() => 0), null);
        }
        Func<IReadOnlyCollection<string>> members = c.Members ?? (() => port.Bodies());
        if (c.Members == null) pending ??= port.Pending;
        foreach (var b in port.Bodies())
            if (port.HeldBy(b) is { } other && !string.Equals(other, id, StringComparison.Ordinal) && members().Contains(b, StringComparer.OrdinalIgnoreCase))
                notTaken.Add($"{b} (in crew {other})");
        return (members, pending, null);
    }

    /// <summary>Holds each listed body for crew <paramref name="id"/>; the ones it can't (another crew's, offline, a hive script's)
    /// go to <paramref name="notTaken"/> with why.</summary>
    private static List<string> HoldListed(ICrewPort port, IReadOnlyList<string> list, string id, List<string> notTaken)
    {
        var online = port.Bodies();
        var got = new List<string>();
        foreach (var b in list.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (port.HeldBy(b) is { } other && !string.Equals(other, id, StringComparison.Ordinal)) notTaken.Add($"{b} (in crew {other})");
            else if (!online.Contains(b, StringComparer.OrdinalIgnoreCase)) notTaken.Add($"{b} (not free for a crew: offline or a hive script's)");
            else if (!port.Hold(b, id)) notTaken.Add($"{b} (in crew {port.HeldBy(b)})");
            else got.Add(b);
        }
        return got;
    }

    private static string CrewWho(CrewSpec c, Func<IReadOnlyCollection<string>> members)
    {
        if (c.Bodies != null) return $"{members().Count} bod{(members().Count == 1 ? "y" : "ies")} ({string.Join(",", members())})";
        return c.Members != null ? "the bodies spawned for it, as they log in" : "all your bodies, late ones too";
    }

    /// <summary>A crew's end: free its bodies and coordinator, report its result line, and wake the brain once with it.</summary>
    private void OnCrewEnded(CrewSpec c, string id, string name, ScriptSession s, ScriptController controller)
    {
        _crewRuns.TryRemove(id, out var _);
        Crews.Close(id);
        controller.Dispose();
        var line = (s.Outcome ?? (s.Ok ? "ok" : "failed")).ReplaceLineEndings(" ").Trim();
        // "ok: script deforest done (deforest: 3/3 bodies done (…), 4m)" → its own result line (the tag says done/failed).
        if (System.Text.RegularExpressions.Regex.Match(line, @"^ok: script \S+ done \((.*)\)$") is { Success: true } m) line = m.Groups[1].Value;
        line = ScriptText.Cap(line, 300);
        _log?.LogInformation("crew {Id}: {Status}: {Line}", id, s.Status, line);
        try { c.Ended?.Invoke(s.Ok, line); } catch (Exception ex) { _log?.LogWarning(ex, "crew {Id}: end hook failed", id); }
        // The one wake a crew costs the brain: its result (or failure), never a lane's.
        if (!c.Quiet) PostOwn(GameEvent.Notice($"[crew {name} {(s.Ok ? "done" : "failed")}] {line}", EventPriority.Normal) with { MustWake = true });
    }

    /// <summary>The crew run a name means (its script or its id), or null.</summary>
    public CrewRun? FindCrew(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = ScriptLibrary.ParseRef(CleanRef(name)).Name;
        return _crewRuns.Values.FirstOrDefault(r => r.Id.Equals(name.Trim(), StringComparison.Ordinal))
            ?? _crewRuns.Values.Where(r => r.Script.Equals(n, StringComparison.OrdinalIgnoreCase)).MaxBy(r => r.Session.Started);
    }

    /// <summary>
    /// The planner's question (<see cref="Agent.BotAgent.CrewRun"/>): is this script call a run of code that works through roles? Then a
    /// body list or "all" means ONE crew over those bodies instead of one run per body.
    /// </summary>
    public bool IsCrewCall(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || DefaultOp(input) is not ("run" or "replace")) return false;
        var code = ToolInput.Str(input, "code");
        if (string.IsNullOrWhiteSpace(code))
        {
            if (ToolInput.Str(input, "goal") is { Length: > 0 } || ToolInput.Str(input, "name") is not { Length: > 0 } n) return false;
            try { code = Library.Resolve(CleanRef(n), IsScriptable, out _)?.Code; }
            catch (Exception ex) { _log?.LogDebug(ex, "crew check: resolve {Name} failed", n); return false; }
        }
        return code != null && LanguageOf(code) == ScriptLanguage.Js && Crews.IsCrewScript(code);
    }

    /// <summary>The planner's resolved crew bodies on a script call: a list, an empty list for "all", or null (no crew call).</summary>
    private static IReadOnlyList<string>? CrewBodiesOf(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(CrewBodiesKey, out var b)) return null;
        if (b.ValueKind == JsonValueKind.String) return string.Equals(b.GetString(), "all", StringComparison.OrdinalIgnoreCase) ? [] : [b.GetString()!];
        if (b.ValueKind != JsonValueKind.Array) return null;
        return b.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
    }

    /// <summary>The brain's crew run (<see cref="CrewBodiesKey"/> set): a replace first ends the running crew of that script.</summary>
    private async Task<ToolResult> RunCrewAsync(ScriptRequest req, IReadOnlyList<string> bodies, bool replace, CancellationToken ct)
    {
        if (replace && FindCrew(req.Name) is { } old)
        {
            old.Stop("replaced by planner");
            try { await old.Session.Completion.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); } catch (TimeoutException) { }
        }
        var (run, text) = StartCrew(new CrewSpec { Request = req, Bodies = bodies.Count == 0 ? null : bodies });
        return run == null ? ToolResult.Error("not started: " + text) : ToolResult.Success(text);
    }

    /// <summary>op=status: this body's script, then every crew this brain coordinates.</summary>
    private string StatusWithCrews()
    {
        var crews = CrewRuns;
        var own = Controller.Status();
        return crews.Count == 0 ? own : own + " | " + string.Join(" | ", crews.Select(r => r.State()));
    }

    /// <summary>op=cancel name=X where X is a crew this brain coordinates: stop it (its lanes stop with it).</summary>
    private async Task<ToolResult?> CancelCrewAsync(JsonElement input, CancellationToken ct)
    {
        if (FindCrew(ToolInput.Str(input, "name")) is not { } run) return null;
        run.Stop("cancelled by planner");
        try { await run.Session.Completion.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
        catch (TimeoutException) { return ToolResult.Success($"cancelling crew {run.Script} (its lanes are finishing an atomic action)"); }
        return ToolResult.Success($"crew {run.Script} cancelled: " + (run.Session.Outcome ?? "stopped"));
    }
}
