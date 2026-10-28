using System.Text.Json;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Runtime;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.Scripting;

namespace MineClaw.MemoryExplorer.Services;

/// <summary>Where the Scripts page reads the saved library from: an opened db (local / snapshot) or the live Supervisor.</summary>
public interface IScriptLibrarySource
{
    string Label { get; }
    bool IsLive { get; }
    Task<IReadOnlyList<ScriptLibraryEntry>> ListAsync(CancellationToken ct);
    /// <summary>One version (null = the active one), or null.</summary>
    Task<ScriptVersionDetail?> VersionAsync(string name, int? version, CancellationToken ct);
}

/// <summary>
/// The library of an opened (read-only) db: the <c>routines</c> rows with code and the <c>standing_orders</c> that run them,
/// linted here. Offline lint can't know this bot's game functions, so calls the script makes are taken as known (syntax,
/// forbidden constructs, machine and loop checks still apply); the live source lints with the real ones.
/// </summary>
public sealed class DbScriptLibrarySource(AgentDb db, string label) : IScriptLibrarySource
{
    private List<Routine>? _rows;
    private List<StandingOrder>? _orders;

    public string Label => label;
    public bool IsLive => false;

    private (List<Routine> Rows, List<StandingOrder> Orders) Load()
    {
        if (_rows != null) return (_rows, _orders!);
        var rows = new List<Routine>();
        if (db.TableExists("routines"))
            foreach (var json in db.Query("SELECT data FROM routines", r => r.GetString(0)))
                try { if (JsonSerializer.Deserialize<Routine>(json, AtomicFile.Json) is { Script.Code.Length: > 0 } rt) rows.Add(rt); }
                catch (JsonException) { }
        var orders = new List<StandingOrder>();
        if (db.TableExists("standing_orders"))
            foreach (var json in db.Query("SELECT data FROM standing_orders", r => r.GetString(0)))
                try { if (JsonSerializer.Deserialize<StandingOrder>(json, AtomicFile.Json) is { } o) orders.Add(o); }
                catch (JsonException) { }
        return (_rows = rows, _orders = orders);
    }

    public Task<IReadOnlyList<ScriptLibraryEntry>> ListAsync(CancellationToken ct) => Task.Run(() =>
    {
        var (rows, orders) = Load();
        return ScriptLibraryReport.List(rows, orders, OfflineLint.Analyze);
    }, ct);

    public Task<ScriptVersionDetail?> VersionAsync(string name, int? version, CancellationToken ct) => Task.Run(() =>
    {
        var (rows, orders) = Load();
        var r = rows.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return r == null ? null : ScriptLibraryReport.Detail(r, version, orders, OfflineLint.Analyze);
    }, ct);
}

/// <summary>The live library over the Supervisor (<c>/api/scripts</c>, <c>/api/scripts/{name}/{v}</c>; token header, GET only).</summary>
public sealed class LiveScriptLibrarySource(SupervisorEndpoint endpoint) : IScriptLibrarySource, IDisposable
{
    private readonly HttpClient _http = endpoint.CreateClient(TimeSpan.FromSeconds(8));

    public string Label => "live · " + endpoint.BaseUrl;
    public bool IsLive => true;

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var res = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if ((int)res.StatusCode is 401 or 403) throw new InvalidOperationException("unauthorized: check ~/.mineclaw-supervisor-token");
        if ((int)res.StatusCode == 404) return null;
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return HiveJson.Parse<T>(body);
    }

    private sealed record ListReply(DateTimeOffset At, List<ScriptLibraryEntry> Scripts);

    public async Task<IReadOnlyList<ScriptLibraryEntry>> ListAsync(CancellationToken ct) =>
        (await GetAsync<ListReply>("api/scripts", ct).ConfigureAwait(false))?.Scripts ?? [];

    public Task<ScriptVersionDetail?> VersionAsync(string name, int? version, CancellationToken ct) =>
        GetAsync<ScriptVersionDetail>($"api/scripts/{Uri.EscapeDataString(name)}{(version is { } v ? "/" + v : "")}", ct);

    public void Dispose() => _http.Dispose();
}

/// <summary>Lint and declared params without a bot: the script's own calls count as known game functions.</summary>
public static class OfflineLint
{
    public static ScriptAnalysis Analyze(string code, bool lua)
    {
        var lang = lua ? ScriptLanguage.Lua : ScriptLanguage.Js;
        var known = new List<string>(ScriptSession.Aliases.Keys);
        try { known.AddRange(ScriptLint.CalledNames(code)); } catch (Exception) { }
        var report = ScriptLint.Check(code, known, language: lang);
        var findings = report.Findings.Select(f => new ScriptLintLine(f.Severity == LintSeverity.Error ? "error" : "warning", f.Line, f.Rule, f.Message)).ToList();
        int errors = findings.Count(f => f.Severity == "error"), warnings = findings.Count - errors;
        var lint = new ScriptLintInfo(errors > 0 ? "errors" : warnings > 0 ? "warnings" : "ok", errors, warnings, findings,
            "offline lint: game function names not checked");
        var ps = lua ? [] : ScriptLint.Params(code).Select(p => new ScriptParamInfo(p.Name, p.Default)).ToList();
        return new ScriptAnalysis(ps, lint);
    }
}
