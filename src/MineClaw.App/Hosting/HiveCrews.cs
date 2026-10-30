using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MineClaw.Bot.GameTools;
using MineClaw.Hive.Hierarchy.Bus;
using MineClaw.Scripting;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// A hive's crews over bodies it spawns for them (Fox 10-30: "it would be nice if it could spawn up its bodies with a script already
/// made, so its bodies just go straight to work"): a hive spawned with <c>script=X</c>, or its own <c>hive spawn bodies=N script=X</c>,
/// asks main for N bodies tagged with a crew id; ONE bodiless coordinator (<see cref="ScriptBridge.StartCrew"/>) runs X over exactly
/// the tagged accounts, each as it logs in (main whitelists them one at a time, b1.8.1 logs in one at a time), started by C# with no
/// brain turn. <c>everyBody</c> keeps waiting while some are still coming (allocated, not hosted or not in game yet); one that leaves
/// for good is dropped. The crew's one result line wakes the brain, which supervises (hive_report when the job is done).
/// </summary>
internal sealed class HiveCrews(ScriptBridge bridge, Func<string, object?, TimeSpan, Task<BusReply?>> send, ILogger log)
{
    private sealed class Roster
    {
        public int Expected;
        public int Left;
        public string? Refused;
        public readonly HashSet<string> Hosted = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly ConcurrentDictionary<string, Roster> _rosters = new(StringComparer.Ordinal);

    /// <summary>"ok: 3 subhive bodies (102-105) for 101's brain; …" → 3 (main's own reply; 0 = refused).</summary>
    private static readonly Regex Granted = new(@"^ok: (\d+) subhive bod", RegexOptions.Compiled);

    public static int GrantedOf(string? reply) => reply != null && Granted.Match(reply) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    /// <summary>A crew id for <paramref name="script"/> expecting <paramref name="expected"/> bodies (the id goes to main with the spawn).</summary>
    public string Reserve(string script, int expected)
    {
        var id = bridge.NewCrewId(script);
        _rosters[id] = new Roster { Expected = Math.Max(0, expected) };
        return id;
    }

    /// <summary>Main hosts <paramref name="account"/> here for crew <paramref name="crew"/>: it is a member from now on.</summary>
    public void Arrived(string? crew, string account)
    {
        if (crew != null && _rosters.TryGetValue(crew, out var r)) lock (r) r.Hosted.Add(account);
    }

    /// <summary>A body logged out for good: no crew waits for it any more.</summary>
    public void Left(string account)
    {
        foreach (var r in _rosters.Values)
            lock (r)
                if (r.Hosted.Remove(account)) r.Left++;
    }

    /// <summary>Main's answer to the spawn: how many bodies come (0 = refused; a running crew stops with why).</summary>
    public void Answer(string crew, string reply)
    {
        if (!_rosters.TryGetValue(crew, out var r)) return;
        int n = GrantedOf(reply);
        lock (r)
        {
            r.Expected = n;
            if (n == 0) r.Refused = reply;
        }
        if (n == 0 && bridge.FindCrew(crew) is { } run) run.Stop("no bodies: " + reply);
    }

    private IReadOnlyCollection<string> Members(string id) =>
        _rosters.TryGetValue(id, out var r) ? (IReadOnlyCollection<string>)Lock(r, () => r.Hosted.ToList()) : [];

    /// <summary>Still to come: allocated but not hosted yet, or hosted and still logging in.</summary>
    private int Pending(string id)
    {
        if (!_rosters.TryGetValue(id, out var r)) return 0;
        var online = bridge.Crew?.Bodies() ?? [];
        return Lock(r, () => Math.Max(0, r.Expected - r.Hosted.Count - r.Left)
            + r.Hosted.Count(a => !online.Contains(MineClaw.Bot.Bodies.Body.ShortName(a, false), StringComparer.OrdinalIgnoreCase)));
    }

    private static T Lock<T>(Roster r, Func<T> f) { lock (r) return f(); }

    /// <summary>Starts crew <paramref name="id"/>'s coordinator (script X by name, saved: linted, a crew script) over its tagged bodies.</summary>
    public (ScriptBridge.CrewRun? Run, string Text) Start(string id, string script, string? args)
    {
        if (!_rosters.TryGetValue(id, out var roster)) return (null, "no such crew");
        if (Lock(roster, () => roster.Refused) is { } refused) return (null, "no bodies: " + refused);
        var v = bridge.Library.Resolve(script, null, out var why);
        if (v == null) return (null, why ?? $"no saved script '{script}'");
        var r = bridge.StartCrew(new ScriptBridge.CrewSpec
        {
            Request = new ScriptRequest { Name = ScriptController.CleanName(script), Code = v.Code, Language = ScriptLanguage.Js, ArgsJson = args, Version = v.V, Author = v.Author },
            Members = () => Members(id), Pending = () => Pending(id), Id = id,
            Ended = (_, _) => _rosters.TryRemove(id, out _),
        });
        if (r.Run == null) _rosters.TryRemove(id, out _);
        else log.LogInformation("hive crew {Id}: {Script} started over the bodies spawned for it (no brain turn)", id, script);
        return r;
    }

    /// <summary>
    /// The hive tool's <c>spawn bodies=N script=X</c>: X must be a saved crew script (a DeepSeek brain never writes code); the crew starts
    /// at once, then main allocates, whitelists and hands over each body with the crew's id; each goes to work as it logs in.
    /// </summary>
    public async Task<string> SpawnAsync(string script, string? args, int n, string? task)
    {
        var (name, err) = MineClaw.Hive.Crew.HiveScriptHost.PrepareCrew(bridge, script, null);
        if (name == null) return "no: " + err + "; spawned nothing";
        var id = Reserve(name, n);
        var (run, text) = Start(id, name, args);
        if (run == null) return "no: " + text + "; spawned nothing";
        var reply = await send(BusTypes.Spawn, new SpawnReq(Kind: "subhive", Task: task ?? $"crew {name}", Bodies: n, Crew: id), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var t = reply == null ? "main didn't answer on the hive bus" : reply.Ok ? reply.As<string>() ?? "ok" : reply.Error ?? "refused";
        Answer(id, t);
        if (GrantedOf(t) == 0) return "no: " + t + "; crew not started";
        return $"{t}; they go straight to work on {name} as they log in: ONE crew over just them (script{{op:cancel,name:{name}}} ends it); you hear its result";
    }
}
