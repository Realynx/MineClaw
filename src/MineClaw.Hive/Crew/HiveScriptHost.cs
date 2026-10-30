using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.GameTools;
using MineClaw.Hive.Hierarchy;
using MineClaw.Scripting;
using MineClaw.Scripting.Crew;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Crew;

/// <summary>
/// Hive scripts in the process that hosts the bodies (main: its subhive host): each coordinator is its own bodiless controller
/// (<see cref="ScriptBridge.Coordinator"/>) over exactly its accounts (<see cref="SubsetCrewPort"/> on this process's body port), so
/// <c>bodies()</c> is live — late logins join, leavers drop — and lanes run on each body's own lane (its reflexes, its interrupts).
/// No brain: the run is unattended (danger pauses resume by themselves), and its end goes to the hierarchy, which releases the bodies
/// (deposit, logout, whitelist remove) and tells the brain the one result line.
/// </summary>
public sealed class HiveScriptHost(ScriptBridge bridge, ICrewPort bodies, ILogger? log = null) : IHiveScriptHost
{
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<string, ScriptSession> _running = new(StringComparer.OrdinalIgnoreCase);

    public (string? Name, string? Error) Prepare(string? script, string? code) => PrepareCrew(bridge, script, code);

    /// <summary>
    /// A crew script by name (or inline code from the Opus brain: linted and saved here) → (name, error): it must be JS and work its
    /// bodies through a role (its coordinator has no body to act with). Hive scripts and hives spawned straight onto a script use it.
    /// </summary>
    public static (string? Name, string? Error) PrepareCrew(ScriptBridge bridge, string? script, string? code)
    {
        var (name, error, text) = bridge.PrepareForHive(script, code, MineClaw.Agent.Tools.ToolCaller.Current?.Tag);
        if (name == null) return (null, error);
        if (text != null && ScriptLanguages.Detect(text) != ScriptLanguage.Js) return (null, $"{name} is legacy Lua: a hive script is JS (everyBody)");
        if (text != null && !Crews.IsCrewScript(text))
            return (null, $"{name} isn't a hive script: its main() works the bodies through a role (everyBody('worker', args)); run it on one body instead");
        return (name, null);
    }

    public string Start(string id, string script, string? args, Func<IReadOnlyCollection<string>> members, Action<bool, string> ended)
    {
        var v = bridge.Library.Resolve(script, null, out var why);
        if (v == null) return "error: " + (why ?? $"no saved script '{script}'");
        var controller = bridge.Coordinator(new SubsetCrewPort(bodies, members), id);
        var (s, err) = controller.Start(new ScriptRequest
        {
            Name = script, Code = v.Code, Language = ScriptLanguage.Js, ArgsJson = args, Version = v.V, Author = v.Author,
            // Its own board per run (the db board outlives runs: an old run's keys must not leak into this one).
            CrewId = $"hive:{id}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", Unattended = true, Coordinator = $"hive script {id} (no brain)",
        });
        if (s == null)
        {
            controller.Dispose();
            return "error: " + (err ?? "can't start");
        }
        _running[id] = s;
        _log.LogInformation("hive script {Id}: {Script} v{V} started (no brain)", id, script, v.V);
        _ = s.Completion.ContinueWith(_ =>
        {
            _running.TryRemove(id, out var _);
            controller.Dispose();
            var line = (s.Outcome ?? (s.Ok ? "ok" : "failed")).ReplaceLineEndings(" ").Trim();
            _log.LogInformation("hive script {Id}: {Status}: {Line}", id, s.Status, line);
            try { ended(s.Ok, line); } catch (Exception ex) { _log.LogWarning(ex, "hive script {Id}: end hook failed", id); }
        }, TaskScheduler.Default);
        return $"ok: {script} running";
    }

    public bool Stop(string id, string reason) => _running.TryGetValue(id, out var s) && s.Stop(reason);

    public string? Status(string id) => _running.TryGetValue(id, out var s) ? s.State() : null;
}
