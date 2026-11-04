using System.Globalization;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory.Grooming;
using MineClaw.Agent.Rag;

namespace MineClaw.ViewerBridge.Data;

public sealed record EditKindSchema(string Kind, IReadOnlyList<string> Ops, IReadOnlyList<EditField> Fields);
public sealed record EditSchemaResponse(IReadOnlyList<EditKindSchema> Kinds, string ReasonField = "__reason");

public sealed record GroomPreviewBody(string Source, string? Text = null, string? DocRef = null, bool Rewrite = false);
public sealed record GroomedDoc(string Kind, string Source, string Ref, string Title, string Body);
public sealed record GroomStage(string Stage, string? Note, int In, int Out);
public sealed record GroomPreviewResponse(IReadOnlyList<GroomedDoc> Docs, int InCount, int OutCount, IReadOnlyList<GroomStage> Stages, double? RewriteCostUsd, string? Note);

/// <summary>The edit-form schema (MemoryEditSchema) and the read-only groom preview (ported from the explorer's GroomPreviewService).</summary>
public static class AdminViews
{
    public static readonly string[] EditKinds = ["fact", "claim", "trait", "structure", "place", "observation", "lesson"];
    public static readonly string[] GroomSources = ["paste", "log", "activity", "episode", "journal", "memory", "structure", "rule", "claim", "hive"];

    public static EditSchemaResponse Schema() =>
        new(EditKinds.Select(k => new EditKindSchema(k, MemoryEditSchema.Ops(k), MemoryEditSchema.For(k))).ToList());

    /// <summary>
    /// The bot's groom chain as a dry run (reads the db's activity episode state, never writes it): a doc of the db by ref, or
    /// pasted text through <see cref="LogIngest"/>. The LLM rewrite stage runs only when asked AND
    /// <c>Agent:Memory:Groom:Rewrite:Enabled</c> AND the cheap tier is configured.
    /// </summary>
    public static async Task<GroomPreviewResponse> GroomAsync(MemoryBank bank, BridgeConfig config, GroomPreviewBody req, CancellationToken ct)
    {
        var options = config.Agent;
        var rewriter = Rewriter(config, req, out var note);
        var groomer = new MemoryGroomer(options.Memory.Groom, GroomEnvironment.From(options, bank.Db, rewriter));
        var ctx = groomer.Context(dryRun: true);
        var reference = (req.DocRef ?? "").Trim();
        if (reference.Length > 0) return await GroomDocAsync(bank, groomer, ctx, reference, note, ct);
        return await GroomTextAsync(groomer, ctx, req, note, ct);
    }

    /// <summary>The LLM rewrite stage when asked and allowed; else null (with a note when it was asked but is off).</summary>
    private static IGroomRewriter? Rewriter(BridgeConfig config, GroomPreviewBody req, out string? note)
    {
        note = null;
        if (!req.Rewrite) return null;
        var options = config.Agent;
        if (options.Memory.Groom.Rewrite.Enabled && config.LlmConfigured)
            return new LlmGroomRewriter(LlmClientFactory.Create(options.Llm), options.Memory.Groom.Rewrite, prices: options.Prices);
        note = "rewrite skipped: Agent:Memory:Groom:Rewrite:Enabled is off or the cheap tier is not configured";
        return null;
    }

    /// <summary>One doc of the db through the chain, with the count after every stage.</summary>
    private static async Task<GroomPreviewResponse> GroomDocAsync(MemoryBank bank, MemoryGroomer groomer, GroomContext ctx, string reference, string? note,
        CancellationToken ct)
    {
        if (LoadDoc(bank, reference) is not { } doc) throw new KeyNotFoundException("no doc with that ref");
        var trace = await groomer.GroomAsync(doc, ctx, ct);
        var stages = new List<GroomStage>();
        int prev = 1;
        foreach (var s in trace.Steps)
        {
            stages.Add(new GroomStage(s.Stage, s.Docs.Count == 0 ? "dropped" : null, prev, s.Docs.Count));
            prev = s.Docs.Count;
        }
        return new GroomPreviewResponse(Docs(trace.Output), 1, trace.Output.Count, stages, null, note);
    }

    /// <summary>Pasted text through <see cref="LogIngest"/> (only the chain's ends are counted).</summary>
    private static async Task<GroomPreviewResponse> GroomTextAsync(MemoryGroomer groomer, GroomContext ctx, GroomPreviewBody req, string? note,
        CancellationToken ct)
    {
        var text = (req.Text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("text or doc_ref is required");
        var raw = LogIngest.Parse(text, req.Source, LogIngest.DayOf(null));
        var docs = await LogIngest.GroomAsync(groomer, text, ctx, req.Source, null, ct);
        var names = groomer.StagesFor(req.Source);
        var chain = names.Select((n, i) => new GroomStage(n, i == names.Count - 1 ? null : "per-stage counts are only traced for a single doc", raw.Count, i == names.Count - 1 ? docs.Count : raw.Count)).ToList();
        return new GroomPreviewResponse(Docs(docs), raw.Count, docs.Count, chain, null, note);
    }

    private static List<GroomedDoc> Docs(IReadOnlyList<RagDoc> docs) => docs.Select(d => new GroomedDoc(d.Kind, d.Source ?? "", d.Ref, d.Title, d.Body)).ToList();

    /// <summary>A doc of the db by ref (activity rollups carry their rollup row as meta, like live ingestion).</summary>
    private static RagDoc? LoadDoc(MemoryBank bank, string reference)
    {
        if (reference.Contains('\n') || reference.Length > 200) return null;
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
}
