using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Memory;

namespace MineClaw.Agent.Routines;

// The saved-script library as read-only data for viewers: the Supervisor's /api/scripts routes and the Memory Explorer's Scripts
// page (from a local db or a snapshot). snake_case JSON on the wire; nothing here writes.

/// <summary>A declared tunable: its default in the code (<c>params({n: 64})</c>) and the saved override, if any.</summary>
public sealed record ScriptParamInfo(string Name, string? Default, string? Tuned = null);

public sealed record ScriptLintLine(string Severity, int Line, string Rule, string Message);

/// <summary>Lint verdict of one version: ok|warnings|errors|unknown, the counts and the findings.</summary>
public sealed record ScriptLintInfo(string Status, int Errors, int Warnings, IReadOnlyList<ScriptLintLine> Findings, string? Note = null)
{
    public static readonly ScriptLintInfo Unknown = new("unknown", 0, 0, [], "not linted here");
}

/// <summary>What a code analyzer (the Scripting layer's lint) says about one version: declared params and lint.</summary>
public sealed record ScriptAnalysis(IReadOnlyList<ScriptParamInfo> Params, ScriptLintInfo Lint);

/// <summary>
/// One run of a script: from the live telemetry (<c>ScriptRunRecord</c>: outcome, actions, failure line) or the saved per-version
/// param runs (<c>ScriptVersion.ParamRuns</c>: params, ok, seconds, yield, timings).
/// </summary>
/// <param name="Source">"live" (this process's telemetry) or "saved" (the row's param runs).</param>
public sealed record ScriptRunInfo(DateTimeOffset At, bool Ok, double Seconds, string Source, string? Script = null, string? Outcome = null,
    int? Actions = null, int? FailLine = null, int? Version = null, string? Params = null, string? Timings = null, string? Body = null,
    int? Yield = null, string? Status = null);

/// <summary>One saved version's facts and stats.</summary>
public sealed record ScriptVersionInfo(int V, string Lang, string? Author, string AuthorKind, DateTimeOffset? SavedAt, bool Active, int Runs,
    int Successes, int Failures, double AvgSeconds, DateTimeOffset? LastRun, IReadOnlyList<string> TopFailures, int Lines);

/// <summary>A row of the library list.</summary>
public sealed record ScriptLibraryEntry(string Name, int Version, int Versions, string Lang, string? Author, string AuthorKind, string? Purpose,
    IReadOnlyList<ScriptParamInfo> Params, DateTimeOffset? LastRun, int Runs, double SuccessRate, double AvgSeconds, string? Timings,
    IReadOnlyList<string> UsedBy, ScriptLintInfo Lint, string? Migration, bool Archived, string? PromotedAs, string? LastOutcome,
    string? DuplicateOf, DateTimeOffset Updated, bool HasLuaHistory);

/// <summary>One version in full: code, lint, params, the whole history, recent runs.</summary>
public sealed record ScriptVersionDetail(ScriptLibraryEntry Script, ScriptVersionInfo Version, string Code, ScriptLintInfo Lint,
    IReadOnlyList<ScriptParamInfo> Params, IReadOnlyList<ScriptVersionInfo> History, IReadOnlyList<ScriptRunInfo> Runs, string? RevertNote,
    string? TunedParams);

/// <summary>Builds the library views from routines rows, standing orders and live run telemetry. Never mutates its input.</summary>
public static class ScriptLibraryReport
{
    public delegate ScriptAnalysis Analyzer(string code, bool lua);

    /// <summary>Every saved script (routines with code), most recently run first.</summary>
    public static IReadOnlyList<ScriptLibraryEntry> List(IEnumerable<Routine> routines, IEnumerable<StandingOrder>? orders = null,
        Analyzer? analyze = null, IEnumerable<ScriptRunInfo>? live = null)
    {
        var ord = orders?.ToList() ?? [];
        var runs = live?.ToList() ?? [];
        return routines.Where(r => r.Script is { Code.Length: > 0 })
            .Select(r => Entry(r, ord, analyze, runs))
            .OrderByDescending(e => e.LastRun ?? DateTimeOffset.MinValue).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>One version of <paramref name="r"/> (null = the active one), or null when it has no such version.</summary>
    public static ScriptVersionDetail? Detail(Routine r, int? version, IEnumerable<StandingOrder>? orders = null, Analyzer? analyze = null,
        IEnumerable<ScriptRunInfo>? live = null)
    {
        if (r.Script is not { Code.Length: > 0 } s) return null;
        var history = Versions(s);
        var v = history.FirstOrDefault(x => x.V == (version ?? s.Version));
        if (v == null) return null;
        var code = CodeOf(s, v.V) ?? "";
        var a = Analyze(analyze, code, v.Lang == "lua");
        var runs = Runs(r.Name, s, live?.ToList() ?? [], v.V);
        return new ScriptVersionDetail(Entry(r, orders?.ToList() ?? [], analyze, live?.ToList() ?? []), Info(s, v), code, a.Lint,
            WithTuned(a.Params, s.Params), history.Select(x => Info(s, x)).ToList(), runs, s.RevertNote, s.Params);
    }

    private static ScriptLibraryEntry Entry(Routine r, List<StandingOrder> orders, Analyzer? analyze, List<ScriptRunInfo> live)
    {
        var s = r.Script!;
        var history = Versions(s);
        var active = history.FirstOrDefault(v => v.V == s.Version) ?? history[^1];
        var lang = LangOf(active.Lang);
        var a = Analyze(analyze, s.Code, lang == "lua");
        var lastTimings = active.ParamRuns.LastOrDefault(p => p.Timings != null)?.Timings
            ?? live.Where(x => Same(x, r.Name) && x.Timings != null).OrderByDescending(x => x.At).FirstOrDefault()?.Timings;
        var lastRun = Max(s.LastRun, active.LastRun, live.Where(x => Same(x, r.Name)).Select(x => (DateTimeOffset?)x.At).DefaultIfEmpty().Max());
        return new ScriptLibraryEntry(r.Name, s.Version, history.Count, lang, active.Author ?? s.Author, AuthorKind(active.Author ?? s.Author, lang == "lua"),
            s.Purpose, WithTuned(a.Params, s.Params), lastRun, s.Runs, s.Runs == 0 ? 0 : Math.Round((double)s.Successes / s.Runs, 3),
            Math.Round(s.AvgSeconds, 1), lastTimings, UsedBy(r.Name, s, orders), a.Lint, s.Migration, s.ArchiveFlagged != null, s.PromotedAs,
            s.LastOutcome, s.DuplicateOf, r.Updated, history.Any(v => v.IsLua) && lang == "js");
    }

    private static DateTimeOffset? Max(params DateTimeOffset?[] xs) => xs.Where(x => x != null).DefaultIfEmpty(null).Max();

    private static bool Same(ScriptRunInfo x, string name) => x.Script != null && string.Equals(Clean(x.Script), Clean(name), StringComparison.Ordinal);

    private static ScriptAnalysis Analyze(Analyzer? analyze, string code, bool lua)
    {
        if (analyze == null) return new ScriptAnalysis([], ScriptLintInfo.Unknown);
        try { return analyze(code, lua); }
        catch (Exception ex) { return new ScriptAnalysis([], ScriptLintInfo.Unknown with { Note = "lint failed: " + ex.Message }); }
    }

    private static IReadOnlyList<ScriptParamInfo> WithTuned(IReadOnlyList<ScriptParamInfo> declared, string? tunedJson)
    {
        var tuned = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(tunedJson))
            try
            {
                using var d = JsonDocument.Parse(tunedJson);
                if (d.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in d.RootElement.EnumerateObject()) tuned[p.Name] = p.Value.GetRawText();
            }
            catch (JsonException) { }
        var list = declared.Select(p => p with { Tuned = tuned.GetValueOrDefault(p.Name) }).ToList();
        foreach (var (k, v) in tuned) if (list.All(p => p.Name != k)) list.Add(new ScriptParamInfo(k, null, v));
        return list;
    }

    /// <summary>The version list (oldest first) without touching the row: legacy rows get their history synthesized from Previous.</summary>
    public static IReadOnlyList<ScriptVersion> Versions(RoutineScript s)
    {
        var list = s.Versions.OrderBy(v => v.V).ToList();
        if (list.Any(v => v.V == s.Version)) return list;
        for (int i = s.Previous.Count - 1; i >= 0; i--)
        {
            int v = s.Version - 1 - i;
            if (v >= 1 && list.All(x => x.V != v)) list.Add(new ScriptVersion { V = v, Code = s.Previous[i] });
        }
        list.Add(new ScriptVersion
        {
            V = s.Version, Code = s.Code, Author = s.Author, Runs = s.Runs, Successes = s.Successes, Failures = s.Failures,
            TotalSeconds = s.TotalSeconds, LastRun = s.LastRun, Lang = s.Lang,
        });
        return list.OrderBy(v => v.V).ToList();
    }

    public static string? CodeOf(RoutineScript s, int v) =>
        v == s.Version ? s.Code : Versions(s).FirstOrDefault(x => x.V == v)?.Code;

    private static ScriptVersionInfo Info(RoutineScript s, ScriptVersion v) => new(v.V, LangOf(v.Lang), v.Author, AuthorKind(v.Author, v.IsLua),
        v.SavedAt, v.V == s.Version, v.Runs, v.Successes, v.Failures, v.Runs == 0 ? 0 : Math.Round(v.TotalSeconds / v.Runs, 1), v.LastRun,
        v.TopFailures(3).ToList(), (v.Code ?? "").Split('\n').Length);

    public static string LangOf(string? lang) => string.Equals(lang, "js", StringComparison.OrdinalIgnoreCase) ? "js" : "lua";

    /// <summary>opus (written by an Opus brain/author), migration (the Lua → JS migration), legacy (old Lua, no author), other.</summary>
    public static string AuthorKind(string? author, bool lua)
    {
        if (string.IsNullOrWhiteSpace(author)) return lua ? "legacy" : "unknown";
        if (author.StartsWith("migration/", StringComparison.OrdinalIgnoreCase)) return "migration";
        if (author.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
        return "other";
    }

    /// <summary>Who runs it: bodies with it as their active standing order, and the catalog skill it was promoted to.</summary>
    public static IReadOnlyList<string> UsedBy(string name, RoutineScript s, IEnumerable<StandingOrder> orders)
    {
        var list = new List<string>();
        var me = Clean(name);
        foreach (var o in orders)
        {
            if (!o.Active || OrderScript(o) is not { } n || Clean(ScriptLibrary.ParseRef(n).Name) != me) continue;
            list.Add($"{o.Bot}: standing order ({o.Job})");
        }
        if (s.PromotedAs is { Length: > 0 } skill) list.Add("skill " + skill);
        return list;
    }

    /// <summary>The script a standing order runs: <c>script {name}</c> (or code with a name) / <c>run_routine {name}</c>.</summary>
    public static string? OrderScript(StandingOrder o)
    {
        if (o.Tool is not ("script" or "run_routine" or "hive")) return null;
        try
        {
            var i = o.Input;
            if (i.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in new[] { "name", "script" })
                if (i.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } n) return n;
        }
        catch (InvalidOperationException) { }
        return null;
    }

    /// <summary>The same normalization as the script controller's names ("Cobble Loop" → "cobble_loop").</summary>
    public static string Clean(string? raw)
    {
        var s = Regex.Replace((raw ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (s.Length > 40) s = s[..40];
        return s.Length > 0 ? s : "script";
    }

    private static readonly Regex LineRx = new(@"\bat line (\d+)\b", RegexOptions.Compiled);

    /// <summary>"failed: script x at line 12 (goto: no route)" → 12.</summary>
    public static int? FailLine(string? outcome) =>
        outcome != null && LineRx.Match(outcome) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>Recent runs of a version, newest first (≤ 20): live telemetry first, the saved param runs for the rest.</summary>
    private static IReadOnlyList<ScriptRunInfo> Runs(string name, RoutineScript s, List<ScriptRunInfo> live, int v)
    {
        var list = live.Where(x => Same(x, name) && (x.Version == null || x.Version == v)).ToList();
        var saved = Versions(s).FirstOrDefault(x => x.V == v)?.ParamRuns ?? [];
        foreach (var p in saved)
        {
            if (list.Any(x => Math.Abs((x.At - p.At).TotalSeconds) < 3 && x.Ok == p.Ok)) continue;
            list.Add(new ScriptRunInfo(p.At, p.Ok, p.Seconds, "saved", Version: v, Params: p.Params, Timings: p.Timings, Yield: p.Yield));
        }
        return list.OrderByDescending(x => x.At).Take(20).ToList();
    }
}
