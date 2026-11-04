using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineClaw.Complexity;

/// <summary>Per-project rollup: functions measured, functions over the threshold, their summed excess, the worst one.</summary>
internal sealed record ProjectTotals(string Project, int Functions, int Over, int Excess, int Max);

/// <summary>
/// The report. Its JSON shape is shared with web/scripts/complexity.mjs so both can be diffed the same way:
/// { tool, language, convention, threshold, totals, byProject[], functions[] } with functions sorted by complexity, descending.
/// No timestamps, so re-running on unchanged code gives a byte-identical file.
/// </summary>
internal sealed class Report
{
    public const string Convention =
        "McCabe: 1 + if, ?:, while, do, for, foreach, catch, &&, ||, ??, ??=, non-default case labels, " +
        "switch-expression arms except a bare `_`, `and`/`or` patterns. Not counted: else, default, ?. (unless --count-conditional-access). " +
        "Local functions and lambdas are measured separately (unless --inline-lambdas).";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Tool => "tools/Complexity";
    public string Language => "csharp";
    public string ConventionText => Convention;
    public required int Threshold { get; init; }
    public required object Totals { get; init; }
    public required IReadOnlyList<ProjectTotals> ByProject { get; init; }
    public required IReadOnlyList<FunctionReport> Functions { get; init; }

    /// <summary>Builds the report from every measured function; lists only those over the threshold unless <paramref name="all"/>.</summary>
    public static Report Build(IReadOnlyList<FunctionReport> measured, int fileCount, int threshold, bool all)
    {
        var over = measured.Where(f => f.Complexity > threshold).ToList();
        var listed = (all ? measured : over)
            .OrderByDescending(f => f.Complexity).ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line)
            .ToList();
        var byProject = measured.GroupBy(f => f.Project)
            .Select(g => Rollup(g.Key, [.. g], threshold))
            .OrderByDescending(p => p.Excess).ThenBy(p => p.Project, StringComparer.Ordinal)
            .ToList();

        return new Report
        {
            Threshold = threshold,
            Totals = new { files = fileCount, functions = measured.Count, over = over.Count, excess = Excess(over, threshold) },
            ByProject = byProject,
            Functions = listed,
        };
    }

    private static ProjectTotals Rollup(string project, List<FunctionReport> functions, int threshold)
    {
        var over = functions.Where(f => f.Complexity > threshold).ToList();
        return new ProjectTotals(project, functions.Count, over.Count, Excess(over, threshold), functions.Max(f => f.Complexity));
    }

    private static int Excess(IEnumerable<FunctionReport> over, int threshold) => over.Sum(f => f.Complexity - threshold);

    public string ToJson() => JsonSerializer.Serialize(new
    {
        tool = Tool,
        language = Language,
        convention = ConventionText,
        threshold = Threshold,
        totals = Totals,
        byProject = ByProject,
        functions = Functions,
    }, Json) + "\n";

    /// <summary>Plain-text table: the functions (worst first, optionally the top n) then the per-project rollup.</summary>
    public string ToTable(int? top)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"CC",4} {"LOC",5}  {"location",-70} member");
        foreach (var f in Functions.Take(top ?? int.MaxValue))
        {
            sb.AppendLine($"{f.Complexity,4} {f.Loc,5}  {$"{f.File}:{f.Line}",-70} {f.Member}");
        }

        sb.AppendLine();
        sb.AppendLine($"{"over",5} {"excess",7} {"max",5} {"funcs",6}  project");
        foreach (var p in ByProject)
        {
            sb.AppendLine($"{p.Over,5} {p.Excess,7} {p.Max,5} {p.Functions,6}  {p.Project}");
        }

        sb.AppendLine();
        sb.AppendLine($"totals: {JsonSerializer.Serialize(Totals)}  (threshold {Threshold})");
        return sb.ToString();
    }
}
