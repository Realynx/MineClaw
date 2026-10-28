using System.Globalization;
using System.Text;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory.Grooming;
using MineClaw.Agent.Rag;

namespace MineClaw.MemoryExplorer.Services;

/// <summary>Raw → groomed, for the groom preview card.</summary>
public sealed record GroomPreview(string Raw, string Groomed, string Stages, int InputDocs, int OutputDocs);

/// <summary>
/// The groom preview: any doc of the open bank (by ref) or pasted text through the same chain the bot runs on every write
/// (<see cref="MemoryGroomer"/>, dry run: reads the bank's activity episode state, never writes it). Read-only; no edit mode needed.
/// The optional LLM rewrite stage runs only when <c>Agent:Memory:Groom:Rewrite:Enabled</c> is on and the cheap tier is configured.
/// </summary>
public static class GroomPreviewService
{
    public static readonly IReadOnlyList<string> Sources = ["paste", "log", "activity", "episode", "journal", "memory", "structure", "rule", "claim", "hive"];

    public static async Task<GroomPreview> PreviewAsync(MemoryBank? bank, ExplorerConfig config, string input, string source, CancellationToken ct = default)
    {
        var options = config.Agent;
        IGroomRewriter? rewriter = null;
        if (options.Memory.Groom.Rewrite.Enabled && config.LlmConfigured && LocalAdminTarget.LlmProblems(config).Count == 0)
            rewriter = new LlmGroomRewriter(LlmClientFactory.Create(options.Llm), options.Memory.Groom.Rewrite, prices: options.Prices);
        var groomer = new MemoryGroomer(options.Memory.Groom, GroomEnvironment.From(options, bank?.Db, rewriter));
        var ctx = groomer.Context(dryRun: true);
        var text = input.Trim();

        if (bank != null && LoadDoc(bank, text) is { } doc)
        {
            var trace = await groomer.GroomAsync(doc, ctx, ct);
            return new GroomPreview(Describe([doc]), Describe(trace.Output), Steps(trace), 1, trace.Output.Count);
        }
        var raw = LogIngest.Parse(text, source, LogIngest.DayOf(null));
        var docs = await LogIngest.GroomAsync(groomer, text, ctx, source, null, ct);
        return new GroomPreview(text, Describe(docs), $"stages for '{source}': {string.Join(" → ", groomer.StagesFor(source))}", raw.Count, docs.Count);
    }

    /// <summary>A doc of the bank by ref (activity rollups carry their rollup row as meta, like live ingestion).</summary>
    private static RagDoc? LoadDoc(MemoryBank bank, string reference)
    {
        if (reference.Length == 0 || reference.Contains('\n') || reference.Length > 200) return null;
        var row = bank.Db.Query("SELECT ref, kind, tag, title, body, source, dimension, updated FROM docs WHERE ref = $r",
            r => new RagDoc(r.GetString(0), r.GetString(1), r.GetString(3), r.GetString(4))
            {
                Tag = AgentDb.StrOrNull(r, 2), Source = AgentDb.StrOrNull(r, 5), Dimension = (int)r.GetInt64(6), At = AgentDb.FromUnixMs(r.GetInt64(7)),
            }, ("$r", reference)).FirstOrDefault();
        if (row == null || !bank.Db.TableExists("activity_rollups")) return row;
        var meta = bank.Db.Query("SELECT player, start, end, place_kind, place_key, place_name FROM activity_rollups WHERE doc_ref = $r LIMIT 1",
            r =>
            {
                var m = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ActivityMeta.Player] = r.GetString(0),
                    [ActivityMeta.Start] = r.GetInt64(1).ToString(CultureInfo.InvariantCulture),
                    [ActivityMeta.End] = r.GetInt64(2).ToString(CultureInfo.InvariantCulture),
                };
                if (!r.IsDBNull(3)) m[ActivityMeta.PlaceKind] = r.GetString(3);
                if (!r.IsDBNull(4)) m[ActivityMeta.PlaceKey] = r.GetString(4);
                if (!r.IsDBNull(5)) m[ActivityMeta.PlaceName] = r.GetString(5);
                return m;
            }, ("$r", reference)).FirstOrDefault();
        return meta == null ? row : row with { Meta = meta };
    }

    private static string Describe(IReadOnlyList<RagDoc> docs)
    {
        if (docs.Count == 0) return "(dropped: nothing memory-worthy)";
        var sb = new StringBuilder();
        foreach (var d in docs)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append('[').Append(d.Kind).Append(d.Source != null ? " · " + d.Source : "").Append(" · ").Append(d.Ref).Append("] ").Append(d.Title).Append('\n').Append(d.Body);
        }
        return sb.ToString();
    }

    private static string Steps(GroomTrace t) =>
        string.Join("\n", t.Steps.Select(s => $"{s.Stage}: " + (s.Docs.Count == 0 ? "(dropped)" : string.Join(" ‖ ", s.Docs.Select(d => d.Body.Replace('\n', ' '))))));
}
