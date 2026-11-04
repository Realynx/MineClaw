using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Memory.Grooming;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Beliefs;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Agent.Self;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Admin;

/// <summary>
/// The stores an admin edit goes through. <see cref="OpenLocal"/> opens a local db file (a snapshot) with its own stores and owns
/// them; <see cref="FromRuntime"/> borrows the running bot's own stores (owns nothing), so edits hit the same in-memory mirrors,
/// FTS triggers, vectors and RAPTOR tree the bot uses.
/// </summary>
public sealed class MemoryAdminContext : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    private MemoryAdminContext() { }

    public required AgentDb Db { get; init; }
    public required RagIndex Rag { get; init; }
    public required MemoryStore Memory { get; init; }
    public required BeliefStore Beliefs { get; init; }
    public SelfProfile? Self { get; init; }
    public required ObservationStore Observations { get; init; }
    /// <summary>The RAPTOR tree (null when RAPTOR is off, embeddings are unavailable, or a local db's tree was built with another
    /// embedding model; see <see cref="RaptorNote"/>).</summary>
    public RaptorMemory? Raptor { get; init; }
    /// <summary>The live bot's RAPTOR maintenance loop (null for a local db): edits signal it instead of syncing concurrently.</summary>
    public RaptorScheduler? RaptorMaintenance { get; init; }
    /// <summary>Why <see cref="Raptor"/> is null, if it is.</summary>
    public string? RaptorNote { get; init; }
    public required AgentOptions Options { get; init; }
    public UsageTracker? Usage { get; init; }
    public ILlmClient? Llm { get; init; }
    public required IEmbeddingProvider Embedder { get; init; }
    public ILoggerFactory? Logs { get; init; }
    /// <summary>Fallback logger when there is no factory (the supervisor's own logger in the live bot).</summary>
    public ILogger? Log { get; init; }
    /// <summary>True for <see cref="FromRuntime"/>.</summary>
    public bool IsLive { get; init; }
    /// <summary>Serializes admin edits on this context.</summary>
    internal SemaphoreSlim WriteGate { get; } = new(1, 1);
    /// <summary>One RAPTOR section rebuild at a time (separate from edits: the tree itself is serialized by its index writer).</summary>
    internal SemaphoreSlim RebuildGate { get; } = new(1, 1);

    public bool ReadOnly => Db.ReadOnly;

    internal ILogger Logger(string name) => Logs?.CreateLogger(name) ?? Log ?? NullLogger.Instance;

    /// <summary>
    /// Opens a LOCAL db file read-write (migrations run, like a bot start) with its own stores; the RAPTOR tree is loaded when
    /// RAPTOR is on, embeddings are available and the stored tree matches the embedder (a mismatching tree is left alone, never
    /// cleared). <paramref name="readOnly"/> opens it with <see cref="AgentDb.OpenReadOnly"/>: every edit then fails and nothing
    /// is written. Owns and disposes everything.
    /// </summary>
    public static MemoryAdminContext OpenLocal(string dbPath, AgentOptions options, IEmbeddingProvider embedder, ILlmClient? llm,
        ILoggerFactory? logs = null, bool readOnly = false)
    {
        ILogger Log(string n) => logs?.CreateLogger(n) ?? NullLogger.Instance;
        var owned = new List<IDisposable>();
        try
        {
            var db = readOnly ? AgentDb.OpenReadOnly(dbPath) : AgentDb.Open(dbPath, Log("MineClaw.Db"));
            owned.Add(db);
            var usage = new UsageTracker(options.Budget, options.Prices, path: null)
            {
                Classify = m => options.Llm.TierOfModel(m),
                ProfileOf = m => options.Llm.ProfileOfModel(m),
            };
            var rag = new RagIndex(db, options.Rag, embedder, usage, options.Embeddings, log: Log("MineClaw.Rag"), backgroundEmbedding: false);
            owned.Add(rag);
            // Admin writes (edits, shard imports, the activity compaction) are groomed exactly like the bot's own.
            rag.Groomer = new MemoryGroomer(options.Memory.Groom, GroomEnvironment.From(options, db), Log("MineClaw.Groom"));
            if (!readOnly) KnowledgeCorrections.Apply(rag, options.KnowledgeDirectory, Log("MineClaw.Rag"));
            var memory = new MemoryStore(db, rag);
            owned.Add(memory);
            var beliefs = new BeliefStore(db, rag);
            var observations = new ObservationStore(rag);
            var self = OpenSelf(db, options, rag, readOnly, Log);

            RaptorMemory? raptor = null;
            string? note = RaptorUnavailable(db, options, embedder);
            if (note == null)
            {
                raptor = OpenRaptor(db, rag, options, embedder, llm, usage, readOnly, Log);
                owned.Add(raptor);
                rag.Augmenter = raptor;
                raptor.LoadAsync().GetAwaiter().GetResult();
            }
            var ctx = new MemoryAdminContext
            {
                Db = db, Rag = rag, Memory = memory, Beliefs = beliefs, Self = self, Observations = observations, Raptor = raptor, RaptorNote = note,
                Options = options, Usage = usage, Llm = llm, Embedder = embedder, Logs = logs,
            };
            // Dispose order: tree, memory, index, db.
            owned.Reverse();
            ctx._owned.AddRange(owned);
            return ctx;
        }
        catch
        {
            owned.Reverse();
            foreach (var o in owned) { try { o.Dispose(); } catch (Exception) { } }
            throw;
        }
    }

    /// <summary>The self profile; on a read-only db it may be unavailable (null).</summary>
    private static SelfProfile? OpenSelf(AgentDb db, AgentOptions options, RagIndex rag, bool readOnly, Func<string, ILogger> log)
    {
        try { return new SelfProfile(db, options.Self, rag); }
        catch (Exception ex) when (readOnly) { log("MineClaw.Admin").LogDebug(ex, "self profile unavailable on a read-only db"); return null; }
    }

    /// <summary>Why the RAPTOR tree isn't loaded (off, no embeddings, a tree built with another embedder); null = load it.</summary>
    private static string? RaptorUnavailable(AgentDb db, AgentOptions options, IEmbeddingProvider embedder)
    {
        if (!options.Rag.Enabled || !options.Rag.Raptor.Enabled) return "RAPTOR is off (Agent:Rag:Raptor:Enabled)";
        if (!embedder.IsAvailable) return "embeddings are not configured";
        if (StoredTreeModel(db) is { } stored && stored != (embedder.Model, embedder.Dimensions))
            return $"the stored tree was built with {stored.Model}@{stored.Dims}, the embedder is {embedder.Model}@{embedder.Dimensions}; tree left alone";
        return null;
    }

    private static RaptorMemory OpenRaptor(AgentDb db, RagIndex rag, AgentOptions options, IEmbeddingProvider embedder, ILlmClient? llm,
        UsageTracker usage, bool readOnly, Func<string, ILogger> log)
    {
        var summarizer = new HaikuSummarizer(llm ?? new NoLlm(), options.Llm, usage) { StyleHint = StyleHint(options) };
        var raptor = new RaptorMemory(db, rag, new CachedEmbedder(db, embedder, options.Embeddings, usage), summarizer, options.Rag.Raptor,
            log: log("MineClaw.Raptor"));
        raptor.Store.LoadWritesBack = !readOnly;
        return raptor;
    }

    /// <summary>The running bot's own stores (main runtime). Owns nothing.</summary>
    public static MemoryAdminContext FromRuntime(AgentRuntime rt, ILoggerFactory? logs = null, ILogger? log = null) => new()
    {
        Log = log,
        Db = rt.Db, Rag = rt.Rag, Memory = rt.Memory, Beliefs = BeliefStore.For(rt.Memory), Self = rt.Self, Observations = rt.Observations,
        Raptor = rt.Raptor, RaptorMaintenance = rt.RaptorMaintenance, RaptorNote = rt.Raptor == null ? "RAPTOR is off in this bot" : null,
        Options = rt.Options, Usage = rt.Usage, Llm = rt.Llm, Embedder = rt.Embeddings, Logs = logs, IsLive = true,
    };

    /// <summary>The same terse hint the bot's own summarizer uses (Agent:Terse:Purposes:raptor).</summary>
    internal static string? StyleHint(AgentOptions o) =>
        Prompts.RaptorStyle((o.Terse ?? new TerseOptions()).For(TersePurpose.Raptor)) is { Length: > 0 } s ? s : null;

    private static (string Model, int Dims)? StoredTreeModel(AgentDb db)
    {
        var json = db.GetMeta(AgentDbRaptorStore.MetaKey);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(json);
            var r = d.RootElement;
            return (r.GetProperty("EmbeddingModel").GetString() ?? "", r.GetProperty("Dimensions").GetInt32());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public void Dispose()
    {
        foreach (var o in _owned) { try { o.Dispose(); } catch (Exception) { } }
        _owned.Clear();
    }

    /// <summary>Stand-in when a local context has no LLM: every summary call fails (RAPTOR then writes pending placeholders).</summary>
    private sealed class NoLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("no LLM client configured for this admin context");
    }
}
