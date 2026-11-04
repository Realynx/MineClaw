using System.Globalization;
using MineClaw.Agent.Memory.Grooming;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Admin;

/// <summary>What a compaction did (or, dry, would do).</summary>
public sealed record ActivityCompactionReport(bool Ok, bool DryRun, int DocsBefore, int DocsAfter, int ActivityBefore, int ActivityAfter,
    int Processed, int Removed, int Rewritten, int Episodes, int DirtySummaries, string Message, IReadOnlyList<string> Samples, string? Backup = null);

/// <summary>
/// The one-off compaction of per-minute activity episodes (idempotent): every activity rollup doc that isn't a coalesced episode
/// yet goes, oldest first, through the SAME groomer chain new ingestion uses (<see cref="MemoryGroomer"/>: the coalesce stage folds
/// routine spans, novel ones stay individual), so the result is identical to what live ingestion now writes. Docs absorbed into
/// another episode are deleted; the RAPTOR summaries above every removed or rewritten leaf are marked dirty (they refresh within
/// the RAPTOR daily budget; untouched sections keep their summaries). Only <c>source = 'activity'</c> docs with an <c>act:</c> ref
/// are touched: player notes, beliefs, directives, the self-profile and everything else are never read for writing.
/// </summary>
public sealed class ActivityCompaction
{
    private readonly AgentDb _db;
    private readonly RagIndex _rag;
    private readonly MemoryGroomer _groomer;
    private readonly RaptorMemory? _raptor;
    private readonly RaptorScheduler? _maintenance;
    private readonly ILogger _log;

    public ActivityCompaction(AgentDb db, RagIndex rag, MemoryGroomer groomer, RaptorMemory? raptor = null, RaptorScheduler? maintenance = null,
        ILogger? log = null)
    {
        _db = db;
        _rag = rag;
        _groomer = groomer;
        _raptor = raptor;
        _maintenance = maintenance;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Through an admin context (the Explorer's local file, the live bot's service).</summary>
    public static ActivityCompaction For(MemoryAdminContext ctx)
    {
        var groomer = ctx.Rag.Groomer as MemoryGroomer
                      ?? new MemoryGroomer(ctx.Options.Memory.Groom, GroomEnvironment.From(ctx.Options, ctx.Db), ctx.Logger("MineClaw.Groom"));
        return new ActivityCompaction(ctx.Db, ctx.Rag, groomer, ctx.Raptor, ctx.RaptorMaintenance, ctx.Logger("MineClaw.Admin"));
    }

    private sealed record Raw(long Id, string Ref, string Kind, string Title, string Body, int Dim, Box? Bounds, long Updated,
        string? Player, long? Start, long? End, string? PlaceKind, string? PlaceKey, string? PlaceName);

    private int ActivityDocs() => (int)_db.ScalarLong("SELECT count(*) FROM docs WHERE source = 'activity' AND level = 0 AND substr(ref, 1, 4) = 'act:'");

    private List<Raw> Pending()
    {
        bool rollups = _db.TableExists("activity_rollups");
        var sql = rollups
            ? """
              SELECT d.id, d.ref, d.kind, d.title, d.body, d.dimension, d.min_x, d.min_y, d.min_z, d.max_x, d.max_y, d.max_z, d.updated,
                     min(r.player), min(r.start), max(r.end), min(r.place_kind), min(r.place_key), min(r.place_name)
              FROM docs d LEFT JOIN activity_rollups r ON r.doc_ref = d.ref
              WHERE d.source = 'activity' AND d.level = 0 AND substr(d.ref, 1, 4) = 'act:'
              GROUP BY d.id ORDER BY coalesce(min(r.start), d.updated), d.id
              """
            : """
              SELECT d.id, d.ref, d.kind, d.title, d.body, d.dimension, d.min_x, d.min_y, d.min_z, d.max_x, d.max_y, d.max_z, d.updated,
                     NULL, NULL, NULL, NULL, NULL, NULL
              FROM docs d WHERE d.source = 'activity' AND d.level = 0 AND substr(d.ref, 1, 4) = 'act:' ORDER BY d.updated, d.id
              """;
        return _db.Query(sql, r => new Raw(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), (int)r.GetInt64(5),
            r.IsDBNull(6) ? null : new Box(r.GetDouble(6), r.GetDouble(7), r.GetDouble(8), r.GetDouble(9), r.GetDouble(10), r.GetDouble(11)),
            r.GetInt64(12), AgentDb.StrOrNull(r, 13), AgentDb.LongOrNull(r, 14), AgentDb.LongOrNull(r, 15), AgentDb.StrOrNull(r, 16),
            AgentDb.StrOrNull(r, 17), AgentDb.StrOrNull(r, 18)));
    }

    /// <summary>The rollup doc as live ingestion hands it to the groomer (same meta).</summary>
    private static RagDoc ToDoc(Raw r)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (r.Player != null) meta[ActivityMeta.Player] = r.Player;
        if (r.Start is { } s) meta[ActivityMeta.Start] = s.ToString(CultureInfo.InvariantCulture);
        if (r.End is { } e) meta[ActivityMeta.End] = e.ToString(CultureInfo.InvariantCulture);
        if (r.PlaceKind != null) meta[ActivityMeta.PlaceKind] = r.PlaceKind;
        if (r.PlaceKey != null) meta[ActivityMeta.PlaceKey] = r.PlaceKey;
        if (r.PlaceName != null) meta[ActivityMeta.PlaceName] = r.PlaceName;
        return new RagDoc(r.Ref, r.Kind, r.Title, r.Body)
        {
            Source = "activity", Bounds = r.Bounds, Dimension = r.Dim, Meta = meta,
            At = AgentDb.FromUnixMs(r.End ?? r.Updated),
        };
    }

    /// <summary>Marks every summary above the doc dirty (in the stored tree; a loaded tree also hears the delete).</summary>
    private int MarkAncestorsDirty(IReadOnlyCollection<long> docIds)
    {
        if (docIds.Count == 0) return 0;
        int n = 0;
        foreach (var chunk in docIds.Chunk(400))
        {
            var ids = string.Join(",", chunk);
            n += _db.Exec($"""
                WITH RECURSIVE up(id) AS (
                    SELECT parent FROM doc_edges WHERE child IN ({ids})
                    UNION SELECT e.parent FROM doc_edges e JOIN up ON e.child = up.id)
                UPDATE docs SET raptor_flags = raptor_flags | 2, dirty = 1 WHERE id IN (SELECT id FROM up) AND (raptor_flags & 2) = 0
                """);
        }
        return n;
    }

    /// <param name="dryRun">Only report: no doc, state or tree is written.</param>
    public async Task<ActivityCompactionReport> RunAsync(bool dryRun, CancellationToken ct = default, string? backup = null)
    {
        if (!dryRun && _db.ReadOnly)
            return new ActivityCompactionReport(false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, "the database is open read-only; nothing was changed", []);
        int docsBefore = (int)_db.ScalarLong("SELECT count(*) FROM docs");
        int actBefore = ActivityDocs();
        var ctx = _groomer.Context(dryRun);
        var pending = Pending().Where(r => !ctx.Episodes.IsEpisode(r.Ref)).ToList();
        var pass = new CompactionPass(this, ctx, dryRun);

        foreach (var batch in pending.Chunk(250))
        {
            ct.ThrowIfCancellationRequested();
            if (dryRun) pass.Step(batch);
            else _db.Tx(() => pass.Step(batch));
        }
        if (dryRun) pass.Removed = pass.Gone.Count;
        int removed = pass.Removed;
        int episodes = pass.Outputs.Keys.Count(k => !pass.Gone.Contains(k));

        // The tree takes the removals now (zero tokens); summaries above them stay dirty for the budgeted refresh.
        if (!dryRun && pending.Count > 0) await SyncTreeAsync(ct).ConfigureAwait(false);

        var (docsAfter, actAfter) = dryRun ? (docsBefore - removed, actBefore - removed) : ((int)_db.ScalarLong("SELECT count(*) FROM docs"), ActivityDocs());
        var samples = pass.Outputs.Values.Where(d => !pass.Gone.Contains(d.Ref)).OrderByDescending(d => d.Body.Length).Take(5).Select(d => d.Body).ToList();
        var msg = pending.Count == 0
            ? $"nothing to compact: {actBefore} activity episodes are already coalesced"
            : CompactedMessage(dryRun, pending.Count, episodes, pass, (docsBefore, docsAfter), (actBefore, actAfter));
        _log.LogInformation("activity compaction: {Message}", msg);
        return new ActivityCompactionReport(true, dryRun, docsBefore, docsAfter, actBefore, actAfter, pending.Count, removed, pass.Rewritten, episodes,
            pass.Dirty, msg, samples, backup);
    }

    private static string CompactedMessage(bool dryRun, int pending, int episodes, CompactionPass pass, (int Before, int After) docs,
        (int Before, int After) act) =>
        $"{(dryRun ? "would compact" : "compacted")} {pending} activity docs into {episodes} episodes " +
        $"({pass.Removed} removed, {pass.Rewritten} rewritten in place); docs {docs.Before} → {docs.After}, activity {act.Before} → {act.After}" +
        (dryRun ? "" : $"; {pass.Dirty} RAPTOR summaries marked dirty");

    /// <summary>Signals the maintenance loop, else syncs the RAPTOR tree here (bounded); a failure defers it.</summary>
    private async Task SyncTreeAsync(CancellationToken ct)
    {
        try
        {
            if (_maintenance != null) _maintenance.Signal();
            else if (_raptor is { Ready: true } raptor)
            {
                if (_rag.Embedder.IsAvailable) await _rag.FlushEmbeddingsAsync(ct).ConfigureAwait(false);
                for (int i = 0; i < 200; i++)
                    if (!(await raptor.SyncAsync(ct).ConfigureAwait(false)).Any) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "activity compaction: tree sync deferred"); }
    }

    /// <summary>One compaction run's grooming: every pending doc through the groomer, outputs kept, replaced docs removed.</summary>
    private sealed class CompactionPass(ActivityCompaction owner, GroomContext ctx, bool dryRun)
    {
        public int Removed, Rewritten, Dirty;
        public readonly Dictionary<string, RagDoc> Outputs = new(StringComparer.Ordinal);
        public readonly HashSet<string> Gone = new(StringComparer.Ordinal);

        /// <summary>One batch (one transaction when writing): groom each doc, then mark the tree dirty and delete what was replaced.</summary>
        public void Step(Raw[] batch)
        {
            var deleteIds = new List<long>();
            var rewrittenIds = new List<long>();
            var deleteRefs = new List<string>();
            foreach (var raw in batch)
            {
                if (Groom(raw, rewrittenIds)) continue;
                Gone.Add(raw.Ref);
                deleteIds.Add(raw.Id);
                deleteRefs.Add(raw.Ref);
            }
            if (dryRun) return;
            Dirty += owner.MarkAncestorsDirty([.. deleteIds, .. rewrittenIds]);
            foreach (var r in deleteRefs) if (owner._rag.Delete(r)) Removed++;
        }

        /// <summary>Grooms one doc (outputs kept, written unless dry); true when an output keeps its ref (a rewrite or as is).</summary>
        private bool Groom(Raw raw, List<long> rewrittenIds)
        {
            bool keepsRef = false;
            foreach (var d in owner._groomer.Groom(ToDoc(raw), ctx))
            {
                Outputs[d.Ref] = d;
                if (d.Ref == raw.Ref)
                {
                    keepsRef = true;
                    if (d.Body != raw.Body || d.Title != raw.Title) { Rewritten++; rewrittenIds.Add(raw.Id); }
                }
                if (!dryRun) owner._rag.UpsertGroomed(d);
            }
            return keepsRef;
        }
    }
}
