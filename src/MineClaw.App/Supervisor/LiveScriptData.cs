using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Runtime;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Hive.Supervisor;
using MineClaw.Scripting;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Supervisor;

/// <summary>
/// The script routes' data in the main process: the saved library (the shared routines rows, cloned before reading), lint
/// with this bot's real game functions, live run telemetry (main + in-process bodies), and the live debugger snapshot of any
/// body — main and in-process bodies straight from their script sessions, hive clones over the <c>script_dbg</c> IPC request.
/// Reads only.
/// </summary>
public sealed class LiveScriptData : IScriptSupervisorData
{
    private readonly BotHost _bot;
    private readonly string _main;
    private readonly Func<HiveSupervisor?> _hive;
    private readonly ILogger? _log;
    private readonly ConcurrentDictionary<string, ScriptAnalysis> _lint = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime At, Task<BodyScriptReply?> Reply)> _recent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A body's snapshot is shared by every client asking within this window (several viewers cost one probe).</summary>
    public TimeSpan ShareWindow { get; init; } = TimeSpan.FromMilliseconds(150);
    public TimeSpan CloneTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

    public LiveScriptData(BotHost bot, string main, Func<HiveSupervisor?> hive, ILogger? log = null)
    {
        _bot = bot;
        _main = main;
        _hive = hive;
        _log = log;
    }

    /// <summary>Every body this process controls: a hive's subhive bodies' scripts (from its snapshot) join <see cref="Running"/>.</summary>
    public HiveBodyList? Bodies { get; init; }

    private AgentRuntime Rt => _bot.Runtime;
    private ScriptBridge? Bridge => ScriptTools.Of(Rt);

    // ------------------------------------------------------------------ library

    /// <summary>The routines with code, deep-copied under the memory lock (the live rows keep changing as scripts run).</summary>
    private List<Routine> Routines() => Rt.Memory.Read(d => d.Routines.Where(r => r.Script is { Code.Length: > 0 })
        .Select(r => JsonSerializer.Deserialize<Routine>(JsonSerializer.Serialize(r, AtomicFile.Json), AtomicFile.Json)!).ToList());

    private IReadOnlyList<StandingOrder> Orders()
    {
        try { return Rt.Memory.StandingOrders(); }
        catch (Exception ex) { _log?.LogDebug(ex, "supervisor scripts: standing orders unavailable"); return []; }
    }

    public IReadOnlyList<ScriptLibraryEntry> Library() => ScriptLibraryReport.List(Routines(), Orders(), Analyze, LiveRuns());

    public ScriptVersionDetail? Version(string name, int? version)
    {
        var want = ScriptLibraryReport.Clean(ScriptLibrary.ParseRef(name).Name);
        var r = Routines().FirstOrDefault(x => ScriptLibraryReport.Clean(x.Name) == want);
        return r == null ? null : ScriptLibraryReport.Detail(r, version ?? ScriptLibrary.ParseRef(name).Version, Orders(), Analyze, LiveRuns());
    }

    /// <summary>Lint with this bot's real game functions (cached per code).</summary>
    private ScriptAnalysis Analyze(string code, bool lua) => _lint.GetOrAdd((lua ? "lua:" : "js:") + code, _ =>
    {
        var c = Bridge?.Controller;
        var actions = new List<string>(ScriptSession.Aliases.Keys);
        if (c != null)
        {
            actions.AddRange(c.Host.Actions.Select(a => a.Name));
            try { actions.AddRange(c.Host.Skills()); } catch (Exception) { }
        }
        return ScriptAnalysisOf(code, lua, actions, note: c == null ? "no script engine in this process: game functions not checked" : null);
    });

    /// <summary>Params and lint of one version as the library views show them.</summary>
    public static ScriptAnalysis ScriptAnalysisOf(string code, bool lua, IEnumerable<string> actions, string? note = null)
    {
        var lang = lua ? ScriptLanguage.Lua : ScriptLanguage.Js;
        var report = ScriptLint.Check(code, actions, language: lang);
        var findings = report.Findings.Select(f => new ScriptLintLine(f.Severity == LintSeverity.Error ? "error" : "warning", f.Line, f.Rule, f.Message)).ToList();
        int errors = findings.Count(f => f.Severity == "error"), warnings = findings.Count - errors;
        var lint = new ScriptLintInfo(errors > 0 ? "errors" : warnings > 0 ? "warnings" : "ok", errors, warnings, findings, note);
        var ps = ScriptLint.Params(code).Select(p => new ScriptParamInfo(p.Name, p.Default)).ToList();
        return new ScriptAnalysis(ps, lint);
    }

    private IEnumerable<ScriptController> Controllers()
    {
        if (Bridge?.Controller is { } mc) yield return mc;
        if (_bot.Bodies is { } reg)
            foreach (var b in reg.All)
                if (!b.IsMain && b.Scripts is { } c) yield return c;
    }

    private List<ScriptRunInfo> LiveRuns()
    {
        var list = new List<ScriptRunInfo>();
        foreach (var c in Controllers())
            foreach (var r in c.Runs)
                list.Add(new ScriptRunInfo(r.At, r.Status == ScriptStatus.Done, Math.Round(r.Duration.TotalSeconds, 1), "live", r.Name, r.Outcome, r.Actions,
                    ScriptLibraryReport.FailLine(r.Outcome), r.Version, r.Params, r.Timings, r.Body ?? "main", r.ItemsGained, r.Status.ToString().ToLowerInvariant()));
        return list;
    }

    // ------------------------------------------------------------------ live

    public Task<BodyScriptReply?> LiveAsync(string body, bool code, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = body.Trim() + (code ? "#code" : "");
        var now = DateTime.UtcNow;
        if (_recent.TryGetValue(key, out var hit) && now - hit.At < ShareWindow) return hit.Reply;
        // Shared by every request in the window, so it never takes one caller's cancellation (the clone request has its own timeout).
        var t = FetchAsync(body.Trim(), code, CancellationToken.None);
        _recent[key] = (now, t);
        if (_recent.Count > 64) foreach (var k in _recent.Where(kv => now - kv.Value.At > TimeSpan.FromSeconds(10)).Select(kv => kv.Key).ToList()) _recent.TryRemove(k, out _);
        return t;
    }

    private async Task<BodyScriptReply?> FetchAsync(string body, bool code, CancellationToken ct)
    {
        if (IsMain(body))
            return Reply("main", Bridge?.Controller, code);
        if (_bot.Bodies?.Find(body) is { } b)
            return Reply(b.IsMain ? "main" : b.Short, b.Scripts, code);
        if (_hive() is { } sup && sup.Resolve(body) is { } clone)
            return await CloneScriptAsync(sup, clone, code, ct).ConfigureAwait(false);
        if (Bodies?.Find(body) is { IsMain: false } r) return HiveBodyScript(r);
        return null;
    }

    /// <summary>A clone's script debugger snapshot, asked over its link (offline, or no answer, says so).</summary>
    private async Task<BodyScriptReply> CloneScriptAsync(HiveSupervisor sup, string clone, bool code, CancellationToken ct)
    {
        var slot = sup.Slots.FirstOrDefault(s => s.Name.Equals(clone, StringComparison.OrdinalIgnoreCase));
        if (slot is not { Connected: true }) return new BodyScriptReply(Short(clone), false, null, "clone offline");
        ScriptDebugSnapshot? snap = null;
        try { snap = await sup.RequestScriptDebugAsync(clone, code, CloneTimeout, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log?.LogDebug(ex, "supervisor scripts: clone {Clone} snapshot failed", clone); }
        return new BodyScriptReply(Short(clone), snap is { Live: true }, snap, snap == null ? "no script on this clone (or it didn't answer)" : null);
    }

    /// <summary>A hive's own body: its hive streams the script's name and line, not the debugger.</summary>
    private static BodyScriptReply HiveBodyScript(BodyRow r) =>
        new(r.Short, r.Script is { Status: "running" or "paused" or "starting" }, null,
            r.Script == null ? "no script on this body" : $"{r.Short} runs {r.Script.Name} (line {r.Script.Line}); its live debugger stays in its hive ({(r.Owner == null ? "main" : Short(r.Owner))})");

    private bool IsMain(string body) => body.Equals("main", StringComparison.OrdinalIgnoreCase) || body.Equals(_main, StringComparison.OrdinalIgnoreCase);

    private static BodyScriptReply Reply(string body, ScriptController? c, bool code)
    {
        if (c == null) return new BodyScriptReply(body, false, null, "this body has no script engine");
        if (c.Current is { } s) return new BodyScriptReply(body, true, s.DebugSnapshot(code));
        return new BodyScriptReply(body, false, c.Last?.DebugSnapshot(code), c.Last == null ? "no script has run on this body yet" : null);
    }

    private static string Short(string name) => Regex.Match(name, @"(\d+)$") is { Success: true } m ? m.Groups[1].Value : name;

    private static readonly Regex BrainScript = new(@"\(script (?<name>[a-z0-9_]+)(?<paused> paused)?", RegexOptions.Compiled);

    public IReadOnlyList<LiveScriptRow> Running()
    {
        var list = new List<LiveScriptRow>();
        if (Bridge?.Controller.Current is { } m) list.Add(Row(_main, "main", m));
        if (_bot.Bodies is { } reg)
            foreach (var b in reg.All)
                if (!b.IsMain && b.Scripts?.Current is { } s) list.Add(Row(b.Name, b.Short, s));
        if (_hive() is { } sup) AddCloneRows(sup, list);
        if (Bodies is { } all) AddHiveBodyRows(all, list);
        return list;
    }

    /// <summary>Clones report their brain state in the telemetry snapshot ("asleep (script cobble_loop 34%)"): no IPC per poll.</summary>
    private static void AddCloneRows(HiveSupervisor sup, List<LiveScriptRow> list)
    {
        foreach (var slot in sup.Slots)
        {
            if (!slot.Connected || slot.Snapshot?.Brain is not { } brain || BrainScript.Match(brain) is not { Success: true } bm) continue;
            if (list.Any(x => x.Body.Equals(slot.Name, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new LiveScriptRow(slot.Name, Short(slot.Name), bm.Groups["name"].Value, bm.Groups["paused"].Success ? "paused" : "running",
                Paused: bm.Groups["paused"].Success));
        }
    }

    /// <summary>Each row's owner from the body list, then a hive's subhive bodies: the script each one runs, from the snapshot its hive
    /// streams (name, status, line).</summary>
    private void AddHiveBodyRows(HiveBodyList all, List<LiveScriptRow> list)
    {
        IReadOnlyList<BodyRow> rows = [];
        try { rows = all.Rows(); } catch (Exception ex) { _log?.LogDebug(ex, "supervisor scripts: body list unavailable"); }
        var byName = rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Count; i++)
            if (byName.TryGetValue(list[i].Body, out var r) && !r.IsMain) list[i] = list[i] with { Owner = r.Owner };
        foreach (var r in rows)
            if (!r.IsMain && r.Online && r.Script is { } s && !list.Any(x => x.Body.Equals(r.Name, StringComparison.OrdinalIgnoreCase)))
                list.Add(new LiveScriptRow(r.Name, r.Short, s.Name, s.Status, s.Line, s.Paused, s.Version, Owner: r.Owner));
    }

    private static LiveScriptRow Row(string name, string shortName, ScriptSession s) =>
        new(name, shortName, s.Name, s.Status.ToString().ToLowerInvariant(), s.Line, s.Status == ScriptStatus.Paused, s.Request.Version, s.Request.Role);
}
