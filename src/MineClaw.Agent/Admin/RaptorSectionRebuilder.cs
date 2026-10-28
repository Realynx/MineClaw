using System.Diagnostics;
using System.Text;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Raptor;
using MineClaw.Raptor.Model;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Admin;

/// <summary>
/// Admin rebuilds of one section of the RAPTOR tree, on the cheap (Haiku 5.5) tier under a hard dollar cap:
/// <list type="bullet">
/// <item><c>node</c>: <see cref="RaptorIndex.RebuildSubtreeAsync"/> of one summary node (re-cluster + re-summarize its subtree);</item>
/// <item><c>kind</c>: bring that kind's docs' vectors up to date, re-insert their leaves (one atomic remove + insert, counted as
/// replacing) and refresh the dirty summaries (<see cref="RaptorIndex.RefreshDirtyAsync"/>) within the budget.</item>
/// </list>
/// <see cref="Estimate"/> prices the job up front (PriceTable of the cheap model + embedding price); <see cref="RunAsync"/>
/// refuses when the estimate exceeds MaxUsd or the bot's budget is Paused, enforces MaxUsd as a hard cap (RAPTOR budget and the
/// summarizer's own cost cap; what it can't cover stays a pending placeholder), reports progress, honours cancellation, returns
/// the summary diffs and writes an audit entry. One job at a time per context.
/// </summary>
public sealed class RaptorSectionRebuilder
{
    private readonly MemoryAdminContext _ctx;
    private readonly AdminAudit _audit;
    private readonly string _target;
    private readonly ILogger _log;

    public RaptorSectionRebuilder(MemoryAdminContext ctx, AdminAudit audit, string target)
    {
        _ctx = ctx;
        _audit = audit;
        _target = target;
        _log = ctx.Logger("MineClaw.Admin");
    }

    /// <summary>The summarizer used by the last run (tests, diagnostics).</summary>
    public HaikuSummarizer? LastSummarizer { get; private set; }

    /// <summary>
    /// Summaries to reuse, keyed by <see cref="SummaryCache.Hash"/> of their exact cluster text (a shard's summaries cache): a
    /// rebuild that reproduces an identical cluster takes the text without a model call (its vector is in embed_cache).
    /// </summary>
    public IReadOnlyDictionary<string, string>? ReuseSummaries { get; set; }

    /// <summary>Summaries the last run took from <see cref="ReuseSummaries"/>.</summary>
    public int LastReused { get; private set; }

    private string Model => _ctx.Options.Llm.CheapModel;

    /// <summary>max_tokens headroom HaikuSummarizer grants (a summary may run to summarization_length × this).</summary>
    private const double OutputHeadroom = 2.5;

    // ======================= estimate =======================

    public RebuildEstimate Estimate(RebuildRequest r)
    {
        if (_ctx.Raptor is not { } raptor) return Empty("no RAPTOR tree: " + (_ctx.RaptorNote ?? "unavailable"));
        var scope = (r.Scope ?? "").Trim().ToLowerInvariant();
        try
        {
            return scope switch
            {
                "node" => EstimateNode(raptor, r),
                "kind" => EstimateKind(raptor, r),
                _ => Empty("scope must be node or kind"),
            };
        }
        catch (ArgumentException ex) { return Empty(ex.Message); }
    }

    private RebuildEstimate Empty(string note) => new(0, 0, 0, 0, 0, Model, note);

    private RebuildEstimate EstimateNode(RaptorMemory raptor, RebuildRequest r)
    {
        if (r.Node is not { } node) throw new ArgumentException("scope node needs node (the summary node index)");
        var e = raptor.Index.EstimateSubtree(node);
        long output = (long)Math.Ceiling(e.Summaries * (double)raptor.Index.Options.SummarizationLength * OutputHeadroom);
        long embed = output;
        return Price(e.Summaries, e.InputTokens, output, embed,
            $"rebuild #{node} (layer {e.Layer}): {e.Leaves} leaves, ~{e.Summaries} summaries; ancestors of #{node} are not re-summarized");
    }

    /// <summary>Doc refs of the kind's leaves (and the docs eligible for the tree) plus the summaries a refresh would touch.</summary>
    private sealed record KindPlan(List<DocRow> Docs, HashSet<int> Touched, long InputTokens, long MissingVectorTokens, int Missing);

    private sealed record DocRow(string Ref, string Kind, string? Source, string Title, string Body, float[]? Vec, int Tokens);

    private RebuildEstimate EstimateKind(RaptorMemory raptor, RebuildRequest r)
    {
        var plan = PlanKind(raptor, r.Kind);
        long output = (long)Math.Ceiling(plan.Touched.Count * (double)raptor.Index.Options.SummarizationLength * OutputHeadroom);
        return Price(plan.Touched.Count, plan.InputTokens, output, output + plan.MissingVectorTokens,
            $"kind {r.Kind}: {plan.Docs.Count} docs ({plan.Missing} need vectors), ~{plan.Touched.Count} summaries to refresh (incl. ones already dirty)");
    }

    private KindPlan PlanKind(RaptorMemory raptor, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("scope kind needs kind (a memory kind or a doc kind)");
        var (kinds, prefix) = MemoryKinds.DocKindsOf(kind);
        if (kinds.Count == 0) throw new ArgumentException($"{kind} has no docs in the RAPTOR tree");
        var docs = Docs(raptor, kinds, prefix);
        var refs = docs.Select(d => d.Ref).ToHashSet(StringComparer.Ordinal);
        int overhead = raptor.Index.PromptOverheadTokens;
        var (touched, input) = raptor.Index.Read(t =>
        {
            var set = new HashSet<int>(t.AllNodes.Values.Where(n => !n.IsLeaf && (n.Dirty || n.SummaryPending)).Select(n => n.Index));
            var stack = new Stack<int>(t.Leaves.Where(l => l.Key != null && refs.Contains(l.Key)).SelectMany(l => l.Parents));
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (!set.Add(i)) continue;
                foreach (var p in t[i].Parents) stack.Push(p);
            }
            // Ancestors of nodes already dirty are dirty too (MarkDirtyUpward), so the set is closed upward.
            long tokens = set.Sum(i => t[i].Children.Sum(c => (long)t[c].TokenCount) + overhead);
            return (set, tokens);
        });
        var missing = docs.Where(d => d.Vec == null).ToList();
        return new KindPlan(docs, touched, input, missing.Sum(d => (long)d.Tokens), missing.Count);
    }

    /// <summary>The kind's level-0 docs the tree policy admits (vector = the current one, null when missing/stale).</summary>
    private List<DocRow> Docs(RaptorMemory raptor, IReadOnlyList<string> kinds, string? prefix)
    {
        var sql = new StringBuilder("""
            SELECT d.ref, d.kind, d.source, d.title, d.body, v.vec, d.token_count FROM docs d
            LEFT JOIN vectors v ON v.doc_id = d.id AND v.model = $m AND v.dims = $d AND v.hash = d.hash
            WHERE d.level = 0 AND d.kind IN (
            """);
        var args = new List<(string, object?)> { ("$m", raptor.Embedder.Model), ("$d", raptor.Embedder.Dimensions) };
        sql.Append(string.Join(",", kinds.Select((_, i) => "$k" + i))).Append(')');
        for (int i = 0; i < kinds.Count; i++) args.Add(("$k" + i, kinds[i]));
        if (prefix != null)
        {
            sql.Append(" AND substr(d.ref, 1, length($p)) = $p");
            args.Add(("$p", prefix));
        }
        sql.Append(" ORDER BY d.id");
        return _ctx.Db.Query(sql.ToString(), r => new DocRow(r.GetString(0), r.GetString(1), AgentDb.StrOrNull(r, 2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : VectorMath.FromBlob((byte[])r.GetValue(5)), (int)r.GetInt64(6)), [.. args])
            .Where(d => raptor.Policy.Includes(d.Ref, d.Kind, d.Source, 0)).ToList();
    }

    private RebuildEstimate Price(int summaries, long input, long output, long embed, string note)
    {
        var prices = _ctx.Usage?.Prices ?? _ctx.Options.Prices;
        var p = prices.For(Model);
        decimal usd = input * p.InputPerM / 1_000_000m + output * p.OutputPerM / 1_000_000m + EmbedUsd(embed);
        return new RebuildEstimate(summaries, input, output, embed, Math.Round(usd, 6), Model, note);
    }

    private decimal EmbedUsd(long tokens) => tokens * _ctx.Options.Embeddings.PricePerMTok / 1_000_000m;

    // ======================= run =======================

    public async Task<RebuildResult> RunAsync(RebuildRequest r, IProgress<RebuildProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var scope = (r.Scope ?? "").Trim().ToLowerInvariant();
        var who = string.IsNullOrWhiteSpace(r.Who) ? "admin" : r.Who.Trim();
        string id = scope == "node" ? r.Node?.ToString() ?? "" : r.Kind ?? "";
        RebuildResult result;
        decimal spent = 0;
        if (Refusal(r, scope) is { } why) result = new RebuildResult(false, why, [], 0, sw.Elapsed);
        else
        {
            var estimate = Estimate(r);
            if (estimate.Summaries == 0 && estimate.EmbeddingTokens == 0 && scope == "node") result = new RebuildResult(false, estimate.Note, [], 0, sw.Elapsed);
            else if (estimate.Usd > r.MaxUsd)
                result = new RebuildResult(false, $"estimated ${estimate.Usd:F4} exceeds the ${r.MaxUsd:F2} cap; nothing was spent ({estimate.Note})", [], 0, sw.Elapsed);
            else
            {
                await _ctx.RebuildGate.WaitAsync(ct).ConfigureAwait(false);
                decimal embedBefore = _ctx.Usage?.Today.EmbeddingUsd ?? 0;
                var summarizer = new HaikuSummarizer(_ctx.Llm!, _ctx.Options.Llm, _ctx.Usage)
                {
                    StyleHint = MemoryAdminContext.StyleHint(_ctx.Options),
                };
                LastSummarizer = summarizer;
                try
                {
                    // Hard cap: what's left after the embedding estimate, enforced twice (RAPTOR reservations + the summarizer's own stop).
                    decimal summaryCap = Math.Max(0, r.MaxUsd - EmbedUsd(estimate.EmbeddingTokens));
                    summarizer.MaxCostUsd = summaryCap;
                    var budget = summarizer.Budget(summaryCap);
                    var reuse = ReuseSummaries is { Count: > 0 } map ? new ReusingSummarizer(summarizer, map) : null;
                    ISummarizer s = reuse ?? (ISummarizer)summarizer;
                    result = scope == "node"
                        ? await RunNodeAsync(r.Node!.Value, budget, s, progress, ct).ConfigureAwait(false)
                        : await RunKindAsync(r.Kind!, budget, s, progress, ct).ConfigureAwait(false);
                    LastReused = reuse?.Reused ?? 0;
                    if (LastReused > 0) result = result with { Message = result.Message + $" ({LastReused} cached summaries reused)" };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    result = new RebuildResult(false, "cancelled; the tree is as it was before the unfinished step", [], 0, sw.Elapsed);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                {
                    result = new RebuildResult(false, ex.Message, [], 0, sw.Elapsed);
                }
                finally { _ctx.RebuildGate.Release(); }
                decimal embedUsd = _ctx.Usage != null ? _ctx.Usage.Today.EmbeddingUsd - embedBefore : 0;
                spent = summarizer.CostUsd + Math.Max(0, embedUsd);
                result = result with { SpentUsd = spent, Elapsed = sw.Elapsed };
            }
        }
        progress?.Report(new RebuildProgress("done", 1, 1, result.Message));
        _audit.Write(new AuditEntry(DateTimeOffset.UtcNow, who, _target, "rebuild", scope, id, null,
            result.Ok ? $"{result.Diffs.Count} summary change(s), ${spent:F4}" : null, null, result.Ok, result.Message));
        return result;
    }

    private string? Refusal(RebuildRequest r, string scope)
    {
        if (scope is not ("node" or "kind")) return "scope must be node or kind";
        if (_ctx.Raptor == null) return "no RAPTOR tree: " + (_ctx.RaptorNote ?? "unavailable");
        if (!_ctx.Raptor.Ready) return "the RAPTOR tree is still loading; try again shortly";
        if (_ctx.ReadOnly) return "the database is open read-only; nothing was changed";
        if (_ctx.Llm == null) return "no LLM client for summaries";
        if (r.MaxUsd <= 0) return "max_usd must be > 0";
        if (_ctx.Usage?.Mode == BudgetMode.Paused) return "the bot's budget is Paused; no model calls until it rolls over";
        if (scope == "node" && r.Node == null) return "scope node needs node (the summary node index)";
        if (scope == "kind" && string.IsNullOrWhiteSpace(r.Kind)) return "scope kind needs kind";
        return null;
    }

    private async Task<RebuildResult> RunNodeAsync(int node, RaptorBudget budget, ISummarizer summarizer, IProgress<RebuildProgress>? progress, CancellationToken ct)
    {
        var raptor = _ctx.Raptor!;
        var p = progress == null ? null : new Relay(x => progress.Report(new RebuildProgress(x.Stage, x.Done, x.Total)));
        var report = await raptor.Index.RebuildSubtreeAsync(node, budget, p, ct, summarizer).ConfigureAwait(false);
        var diffs = report.Changes.Select(c => new SummaryDiff(c.Index, c.Layer, c.OldText, c.NewText)).ToList();
        var msg = $"rebuilt #{node} (layer {report.Layer}, {report.Leaves} leaves): {report.Summaries} summaries, {report.Placeholders} pending, " +
                  $"{report.Replaced} replaced, {report.Created} new, {report.Kept} shared kept";
        _log.LogInformation("admin raptor {Message}", msg);
        return new RebuildResult(true, msg, diffs, 0, TimeSpan.Zero);
    }

    private async Task<RebuildResult> RunKindAsync(string kind, RaptorBudget budget, ISummarizer summarizer, IProgress<RebuildProgress>? progress, CancellationToken ct)
    {
        var raptor = _ctx.Raptor!;
        progress?.Report(new RebuildProgress("embed", 0, 3, "bringing vectors up to date"));
        if (_ctx.Rag.Embedder.IsAvailable) await _ctx.Rag.FlushEmbeddingsAsync(ct).ConfigureAwait(false);
        var plan = PlanKind(raptor, kind);
        var docs = plan.Docs.Where(d => d.Vec != null)
            .Select(d => new RaptorDocument(RaptorTreePolicy.LeafText(d.Title, d.Body), d.Ref, d.Kind, d.Source) { Chunk = false, Embedding = d.Vec })
            .ToList();
        int skipped = plan.Docs.Count - docs.Count;
        var before = Summaries(raptor);
        progress?.Report(new RebuildProgress("reinsert", 1, 3, $"{docs.Count} leaves"));
        var (removed, insert) = docs.Count > 0 ? await raptor.Index.ReplaceLeavesAsync(docs, ct).ConfigureAwait(false) : (0, new InsertReport(0, 0, 0, 0, 0));
        progress?.Report(new RebuildProgress("refresh", 2, 3, $"{raptor.Index.DirtyCount} dirty summaries"));
        var refresh = await raptor.Index.RefreshDirtyAsync(budget, ct, summarizer).ConfigureAwait(false);
        var after = Summaries(raptor);
        var diffs = new List<SummaryDiff>();
        foreach (var (i, (layer, text)) in before)
        {
            if (!after.TryGetValue(i, out var a)) diffs.Add(new SummaryDiff(i, layer, text, null));
            else if (a.Text != text) diffs.Add(new SummaryDiff(i, layer, text, a.Text));
        }
        foreach (var (i, (layer, text)) in after) if (!before.ContainsKey(i)) diffs.Add(new SummaryDiff(i, layer, null, text));
        diffs = diffs.OrderByDescending(d => d.Layer).ThenBy(d => d.Index).ToList();
        var msg = $"kind {kind}: {removed} leaves out, {insert.Leaves} in ({insert.Attached} attached{(skipped > 0 ? $", {skipped} without a vector skipped" : "")}); " +
                  $"{refresh.Refreshed} summaries refreshed, {refresh.Splits} splits, {refresh.RemainingDirty} still dirty{(refresh.BudgetExhausted ? " (budget spent)" : "")}";
        _log.LogInformation("admin raptor {Message}", msg);
        return new RebuildResult(true, msg, diffs, 0, TimeSpan.Zero);
    }

    private static Dictionary<int, (int Layer, string Text)> Summaries(RaptorMemory raptor) =>
        raptor.Index.Read(t => t.AllNodes.Values.Where(n => !n.IsLeaf).ToDictionary(n => n.Index, n => (n.Layer, n.Text)));

    /// <summary>Answers from the reuse map when the cluster text is identical; otherwise asks the real summarizer.</summary>
    private sealed class ReusingSummarizer(ISummarizer inner, IReadOnlyDictionary<string, string> reuse) : ISummarizer
    {
        private int _reused;
        public int Reused => _reused;

        public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken ct)
        {
            if (reuse.TryGetValue(SummaryCache.Hash(request.Context), out var text))
            {
                Interlocked.Increment(ref _reused);
                return Task.FromResult(new SummaryResult(text, 0, 0, 0));
            }
            return inner.SummarizeAsync(request, ct);
        }
    }

    private sealed class Relay(Action<(string Stage, int Done, int Total)> on) : IProgress<(string Stage, int Done, int Total)>
    {
        public void Report((string Stage, int Done, int Total) value) => on(value);
    }
}
