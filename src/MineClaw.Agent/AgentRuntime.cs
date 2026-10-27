using MineClaw.Agent.Budget;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Human;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Agent.Self;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Tools;
using MineClaw.Agent.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent;

/// <summary>
/// Composition root for the agent layer. Typical host wiring:
/// <code>
/// // options.Chat.Username = "maid_chan" (login), options.Chat.DisplayName = "claudechan" (chat name, renameable)
/// var rt = AgentRuntime.Create(options, (line, ct) => client.SendChatAsync(line, ct), position: () => ...);
/// rt.Tools.Register("mine", "Mine N blocks of a type nearby.", ToolSchema.Create().Str("block").Int("count").Build(),
///     async (input, ct) => ToolResult.Success(await client.MineAsync(...)), longRunning: true);
/// client.ChatReceived += line => rt.Chat.OnServerChat(line);
/// client.HealthChanged += hp => { if (hp &lt;= 6) rt.Events.Post(GameEvent.LowHealth(hp)); };
/// rt.Agent.StateHeader = () => $"pos {x},{y},{z} hp {hp}/20 food {food} held {item}";
/// await rt.Agent.RunAsync(ct);
/// </code>
/// Register game tools before the first turn; the tool list is part of the cached prefix. Dispose to flush/close the db.
/// </summary>
public sealed class AgentRuntime : IDisposable
{
    public required AgentOptions Options { get; init; }
    public required ToolRegistry Tools { get; init; }
    public required EventQueue Events { get; init; }
    public required ChatRouter Chat { get; init; }
    public required UsageTracker Usage { get; init; }
    public required MemoryStore Memory { get; init; }
    public required ContextManager Context { get; init; }
    public required ActivityManager Activities { get; init; }
    public required RoutineExecutor Routines { get; init; }
    public required BotAgent Agent { get; init; }
    public required ILlmClient Llm { get; init; }
    /// <summary>The single SQLite database (memory, RAG index, chat log, observations, self-profile).</summary>
    public required AgentDb Db { get; init; }
    /// <summary>Hybrid local retrieval (FTS5 + vectors + R*Tree).</summary>
    public required RagIndex Rag { get; init; }
    /// <summary>
    /// World-fact ingestion + structured queries for the C# WorldIndexer: <c>Upsert</c>/<c>UpsertMany</c>/<c>Remove</c>,
    /// <c>FindItemInContainers</c>, <c>Nearest</c>, <c>ListKinds</c>.
    /// </summary>
    public required ObservationStore Observations { get; init; }
    public required EpisodeIndexer Episodes { get; init; }
    public AutoRetriever? Retriever { get; init; }
    /// <summary>RAPTOR tree fused into retrieval (null when RAG/RAPTOR is off or embeddings are not configured).</summary>
    public RaptorMemory? Raptor { get; init; }
    /// <summary>Background RAPTOR maintenance (incremental insert, RefreshDirty, off-peak rebuild); persistent runtimes only.</summary>
    public RaptorScheduler? RaptorMaintenance { get; init; }
    public SelfProfile? Self { get; init; }
    public ReflectionPass? Reflection { get; init; }
    /// <summary>Worker/builder tier behind the brain's <c>delegate</c> tool (null when Agent:Worker:Enabled is false).</summary>
    public WorkerAgent? Worker { get; init; }
    /// <summary>Cheap-tier retrieval loop behind the brain's <c>ask_memory</c> tool (null when disabled).</summary>
    public MemoryAgent? MemoryAgent { get; init; }

    /// <summary>
    /// Splits the registered tools between brain and worker (<see cref="WorkerOptions.BrainTools"/> / <see cref="WorkerOptions.WorkerTools"/>).
    /// Call once after the host has registered its game tools, before the first turn. No-op when the worker is disabled.
    /// </summary>
    public void ApplyBrainSplit() => BrainSplit.Apply(Tools, Options.Worker);
    public required IEmbeddingProvider Embeddings { get; init; }
    /// <summary>True if the in-prompt knowledge hit its cap (MaxKnowledgeChars, or Rag.MaxCoreChars for core rules).</summary>
    public bool KnowledgeTruncated { get; init; }
    /// <summary>Chars of knowledge in the cached prompt (core rules only when RAG is on).</summary>
    public int KnowledgeChars { get; init; }
    /// <summary>Knowledge chunks available to retrieval.</summary>
    public int KnowledgeChunks { get; init; }

    /// <param name="sendChat">Sends one raw chat line (≤100 chars) to the server.</param>
    /// <param name="llm">Defaults to <see cref="AnthropicLlmClient"/> (API key from ANTHROPIC_API_KEY).</param>
    /// <param name="position">Current block position, for memory tools that default to "here" and spatial retrieval.</param>
    /// <param name="demonstrations">Enables <c>watch_demo</c> when the client layer can record players.</param>
    /// <param name="persist">False = in-memory database and no usage file (tests).</param>
    /// <param name="embeddings">Overrides the provider built from <see cref="AgentOptions.Embeddings"/> (tests use a fake).</param>
    public static AgentRuntime Create(AgentOptions options, Func<string, CancellationToken, Task> sendChat,
        ILlmClient? llm = null, Func<BlockPos?>? position = null, IDemonstrationSource? demonstrations = null,
        ILoggerFactory? loggers = null, TimeProvider? time = null, bool persist = true, IEmbeddingProvider? embeddings = null)
    {
        time ??= TimeProvider.System;
        llm ??= new AnthropicLlmClient(options.Llm);
        string? Data(string file) => persist ? Path.Combine(options.DataDirectory, file) : null;
        ILogger Log(string name) => loggers?.CreateLogger(name) ?? NullLogger.Instance;

        var tools = new ToolRegistry(options.MaxToolResultChars);
        foreach (var (name, cap) in options.ToolResultCaps) tools.SetCap(name, cap);
        var events = new EventQueue(options.Events, time);
        var chat = new ChatRouter(options.Chat, events, sendChat, time);
        var usage = new UsageTracker(options.Budget, options.Prices, time, Data(options.Budget.UsageFile))
        {
            Classify = m => options.Llm.TierOfModel(m),
        };

        var rag = options.Rag;
        string Under(string f) => Path.IsPathRooted(f) ? f : Path.Combine(options.DataDirectory, f);
        var db = AgentDb.Open(persist ? Under(rag.DbFile) : null, Log("MineClaw.Db"),
            seedPath: persist && !string.IsNullOrWhiteSpace(rag.SeedFile) ? Under(rag.SeedFile) : null);
        var embedder = embeddings
            ?? (rag.Enabled && options.Embeddings.IsConfigured ? new AzureOpenAIEmbeddingProvider(options.Embeddings) : NullEmbeddingProvider.Instance);
        if (!rag.Enabled) embedder = NullEmbeddingProvider.Instance;
        var index = new RagIndex(db, rag, embedder, usage, options.Embeddings, time, Log("MineClaw.Rag"));
        RaptorMemory? raptor = null;
        RaptorScheduler? raptorJobs = null;
        if (rag.Enabled && rag.Raptor.Enabled && embedder.IsAvailable)
        {
            var summarizer = new HaikuSummarizer(llm, options.Llm, usage);
            raptor = new RaptorMemory(db, index, new CachedEmbedder(db, embedder, options.Embeddings, usage), summarizer, rag.Raptor,
                log: Log("MineClaw.Raptor"));
            index.Augmenter = raptor; // recall / ask_memory / auto-retrieval: hybrid lists + RAPTOR, fused by RRF
            if (persist && rag.Raptor.Maintenance)
                raptorJobs = new RaptorScheduler(raptor, summarizer, db, usage, time, Log("MineClaw.Raptor"));
        }
        var memory = new MemoryStore(db, index, time, Data("memory.json"));
        var observations = new ObservationStore(index, time);
        var context = new ContextManager(options.Context);
        var episodes = new EpisodeIndexer(index, context, position, time, Log("MineClaw.Rag"));
        var activities = new ActivityManager(tools, events, time);
        var routines = new RoutineExecutor(tools);
        SelfProfile? self = options.Self.Enabled ? new SelfProfile(db, options.Self, index, time) : null;

        // Built-in, game-agnostic tools (registered first so their order is stable — it's part of the cached prefix).
        tools.Register("stop", "Stop the current task/activity.", ToolSchema.Empty, (_, _) =>
        {
            var what = activities.Cancel("stop");
            return Task.FromResult(ToolResult.Success(what != null ? $"stopped {what}" : "nothing running"));
        }, cheapTier: true);
        MemoryTools.RegisterAll(tools, memory, position);
        WorldTools.Register(tools, observations, position, time, memory);
        IdentityTools.ApplyPersisted(memory, chat);
        IdentityTools.RegisterAll(tools, memory, chat);
        RoutineTools.RegisterAll(tools, memory, routines);
        HumanTools.RegisterAskPlayer(tools, chat);
        if (demonstrations != null) HumanTools.RegisterWatchDemo(tools, chat, memory, demonstrations);
        if (self != null) SelfTools.RegisterAll(tools, self, index, time);

        string knowledge;
        bool truncated;
        int chunks = 0;
        if (rag.Enabled)
        {
            var k = KnowledgeIndexer.Sync(index, options.KnowledgeDirectory, rag);
            knowledge = k.Core;
            truncated = k.CoreTruncated;
            chunks = k.Chunks;
        }
        else knowledge = Prompts.LoadKnowledge(options.KnowledgeDirectory, options.MaxKnowledgeChars, out truncated);

        // Brain-only delegation tools (registered after the built-ins so the cached tool order stays stable).
        WorkerAgent? worker = null;
        if (options.Worker.Enabled)
        {
            worker = new WorkerAgent(options.Worker, options.Llm, llm, tools, usage, knowledge, time, Log("MineClaw.Worker"));
            worker.Register();
        }
        MemoryAgent? memAgent = null;
        if (options.AskMemory.Enabled)
        {
            memAgent = new MemoryAgent(options.AskMemory, llm, tools, usage, Log("MineClaw.Memory"));
            memAgent.Register();
        }

        var agent = new BotAgent(options, llm, tools, events, chat, usage, context, activities, knowledge, time,
            loggers?.CreateLogger<BotAgent>(), knowledgeIsCore: rag.Enabled);
        if (worker != null) worker.StateHeader = () => agent.StateHeader?.Invoke() ?? "";
        var retriever = !rag.Enabled || !rag.AutoRetrieve ? null
            : raptor != null ? new AutoRetriever(new RaptorRetrievalIndex(index, raptor), rag, context, episodes)
            : new AutoRetriever(index, context, episodes);
        var reflection = self != null ? new ReflectionPass(self, index, llm, usage, time, Log("MineClaw.Self")) : null;
        agent.Position = position;
        agent.Rag = index;
        agent.Retriever = retriever;
        agent.Self = self;
        agent.Reflection = reflection;
        if (rag.Enabled && rag.RestoreJournal && episodes.LatestJournal() is { Length: > 0 } journal) context.SetJournal(journal);
        // Off the caller's thread: loads the stored tree, then (persistent runtimes) runs maintenance in the background.
        if (raptorJobs != null) raptorJobs.Start();
        else raptor?.StartLoading();

        return new AgentRuntime
        {
            Options = options, Tools = tools, Events = events, Chat = chat, Usage = usage, Memory = memory,
            Context = context, Activities = activities, Routines = routines, Agent = agent, Llm = llm,
            Db = db, Rag = index, Observations = observations, Episodes = episodes, Retriever = retriever, Self = self,
            Reflection = reflection, Embeddings = embedder, Worker = worker, MemoryAgent = memAgent, Raptor = raptor, RaptorMaintenance = raptorJobs,
            KnowledgeTruncated = truncated, KnowledgeChars = knowledge.Length, KnowledgeChunks = chunks,
        };
    }

    public void Dispose()
    {
        RaptorMaintenance?.Dispose();
        Raptor?.Dispose();
        Rag.Dispose();
        Memory.Dispose();
        (Embeddings as IDisposable)?.Dispose();
        Db.Dispose();
    }
}
