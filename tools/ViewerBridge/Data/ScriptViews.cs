using System.Text;
using System.Text.Json;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Runtime;
using MineClaw.Scripting;

namespace MineClaw.ViewerBridge.Data;

public sealed record ScriptLibraryResponse(DateTimeOffset At, IReadOnlyList<ScriptLibraryEntry> Scripts, string? Source = null);
public sealed record ScriptLintResponse(string Name, int Version, ScriptLintInfo Lint, IReadOnlyList<ScriptParamInfo> Params, bool Offline);
public sealed record DiffLineDto(string Op, string Text, int? ALine, int? BLine);
public sealed record DiffHunkDto(int AStart, int ALines, int BStart, int BLines, IReadOnlyList<DiffLineDto> Lines);
public sealed record ScriptDiffDto(string Name, int A, int B, int Added, int Removed, IReadOnlyList<DiffHunkDto> Hunks, string Unified);

/// <summary>
/// The saved script library of the read-only db (explorer-spec §9, "This database"): <c>routines</c> rows with code and the
/// <c>standing_orders</c> that run them, through the bot's own <see cref="ScriptLibraryReport"/> with <see cref="OfflineLint"/>.
/// Ported from tools/MemoryExplorer/Services/ScriptLibrarySource.cs.
/// </summary>
public sealed class DbScripts(AgentDb db)
{
    private (List<Routine> Rows, List<StandingOrder> Orders) Load()
    {
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
        return (rows, orders);
    }

    public IReadOnlyList<ScriptLibraryEntry> List()
    {
        var (rows, orders) = Load();
        return ScriptLibraryReport.List(rows, orders, OfflineLint.Analyze);
    }

    public ScriptVersionDetail? Version(string name, int? version)
    {
        var (rows, orders) = Load();
        var r = rows.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return r == null ? null : ScriptLibraryReport.Detail(r, version, orders, OfflineLint.Analyze);
    }
}

/// <summary>Lint and declared params without a bot: the script's own calls count as known game functions.</summary>
public static class OfflineLint
{
    public const string Note = "offline lint: game function names not checked";

    public static ScriptAnalysis Analyze(string code, bool lua)
    {
        var lang = lua ? ScriptLanguage.Lua : ScriptLanguage.Js;
        var known = new List<string>(ScriptSession.Aliases.Keys);
        try { known.AddRange(ScriptLint.CalledNames(code)); } catch (Exception) { }
        var report = ScriptLint.Check(code, known, language: lang);
        var findings = report.Findings.Select(f => new ScriptLintLine(f.Severity == LintSeverity.Error ? "error" : "warn", f.Line, f.Rule, f.Message)).ToList();
        int errors = findings.Count(f => f.Severity == "error"), warnings = findings.Count - errors;
        var lint = new ScriptLintInfo(errors > 0 ? "errors" : warnings > 0 ? "warnings" : "ok", errors, warnings, findings, Note);
        var ps = lua ? [] : ScriptLint.Params(code).Select(p => new ScriptParamInfo(p.Name, p.Default)).ToList();
        return new ScriptAnalysis(ps, lint);
    }
}

/// <summary>
/// Line diff between two script versions (LCS, whitespace at line ends ignored), folded to hunks with context, plus unified text.
/// Ported from tools/MemoryExplorer/Services/ScriptDiff.cs.
/// </summary>
public static class ScriptDiff
{
    private enum K { Same, Added, Removed }
    private readonly record struct L(K Kind, int? A, int? B, string Text);

    public static ScriptDiffDto Build(string name, int va, int vb, string? oldText, string? newText, int context = 3)
    {
        var lines = Lines(oldText, newText);
        var hunks = new List<DiffHunkDto>();
        var changed = Enumerable.Range(0, lines.Count).Where(i => lines[i].Kind != K.Same).ToList();
        var unified = new StringBuilder().Append("--- v").Append(va).Append('\n').Append("+++ v").Append(vb).Append('\n');
        int idx = 0;
        while (idx < changed.Count)
        {
            int from = Math.Max(0, changed[idx] - context), to = Math.Min(lines.Count - 1, changed[idx] + context);
            while (idx + 1 < changed.Count && changed[idx + 1] - context <= to + 1) { idx++; to = Math.Min(lines.Count - 1, changed[idx] + context); }
            idx++;
            var h = lines.Skip(from).Take(to - from + 1).ToList();
            int aStart = h.FirstOrDefault(l => l.A != null).A ?? 0, bStart = h.FirstOrDefault(l => l.B != null).B ?? 0;
            int aLines = h.Count(l => l.A != null), bLines = h.Count(l => l.B != null);
            var dl = h.Select(l => new DiffLineDto(l.Kind switch { K.Added => "+", K.Removed => "-", _ => " " }, l.Text, l.A, l.B)).ToList();
            hunks.Add(new DiffHunkDto(aStart, aLines, bStart, bLines, dl));
            unified.Append("@@ -").Append(aStart).Append(',').Append(aLines).Append(" +").Append(bStart).Append(',').Append(bLines).Append(" @@\n");
            foreach (var l in dl) unified.Append(l.Op).Append(l.Text).Append('\n');
        }
        return new ScriptDiffDto(name, va, vb, lines.Count(l => l.Kind == K.Added), lines.Count(l => l.Kind == K.Removed), hunks, unified.ToString());
    }

    private static List<L> Lines(string? oldText, string? newText)
    {
        var a = Split(oldText);
        var b = Split(newText);
        int n = a.Length, m = b.Length;
        if ((long)n * m > 4_000_000) return [.. a.Select((t, i) => new L(K.Removed, i + 1, null, t)), .. b.Select((t, i) => new L(K.Added, null, i + 1, t))];
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = Eq(a[i], b[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var list = new List<L>(n + m);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (Eq(a[x], b[y])) { list.Add(new L(K.Same, x + 1, y + 1, b[y])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { list.Add(new L(K.Removed, x + 1, null, a[x])); x++; }
            else { list.Add(new L(K.Added, null, y + 1, b[y])); y++; }
        }
        for (; x < n; x++) list.Add(new L(K.Removed, x + 1, null, a[x]));
        for (; y < m; y++) list.Add(new L(K.Added, null, y + 1, b[y]));
        return list;
    }

    private static string[] Split(string? text) => string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    private static bool Eq(string a, string b) => string.Equals(a.TrimEnd(), b.TrimEnd(), StringComparison.Ordinal);
}
