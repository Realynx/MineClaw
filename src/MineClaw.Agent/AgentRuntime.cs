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
using MineClaw.Agent.Rules;
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
    /// <summary>Server-command gate: every outgoing chat line passes it; <c>run_command</c> needs a trusted player's exact line.</summary>
    public required CommandGate Commands { get; init; }
    public required UsageTracker Usage { get; init; }
    public required MemoryStore Memory { get; init; }
    public required ContextManager Context { get; init; }
    /// <summary>The main body's ActivityManager (one brain, many bodies: an alias; other bodies own theirs, reached through
    /// <see cref="BotAgent.ActivitiesFor"/>).</summary>
    public required ActivityManager Activities { get; init; }
    public required RoutineExecutor Routines { get; init; }
    public required BotAgent Agent { get; init; }
    public required ILlmClient Llm { get; init; }
    /// <summary>The single SQLite database (memory, RAG index, chat log, observations, self-profile).</summary>
    public required AgentDb Db { get; init; }
    /// <summary>The world this db plays (skyblock or normal), from its active knowledge packs (multi-version P6).</summary>
    public MineClaw.Data.Versioning.WorldMode World { get; init; } = MineClaw.Data.Versioning.WorldMode.SkyblockWorld;
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
    /// <summary>Personality growth v2: signals, aggregates, the [self] block, hive shard/promotion (null when Self is off).</summary>
    public SelfGrowth? Growth { get; init; }
    /// <summary>Worker/builder tier behind the brain's <c>delegate</c> tool (null when Agent:Worker:Enabled is false).</summary>
    public WorkerAgent? Worker { get; init; }
    /// <summary>Cheap-tier retrieval loop behind the brain's <c>ask_memory</c> tool (null when disabled).</summary>
    public MemoryAgent? MemoryAgent { get; init; }
    /// <summary>Chat → reaction / first move / first action latency (zero tokens; TUI p50s, latency-*.jsonl).</summary>
    public required Telemetry.LatencyTracker Latency { get; init; }
    /// <summary>Script interrupt → resume latency and cost per path (declared/learned/patch/full; interrupts-*.jsonl).</summary>
    public Telemetry.InterruptTracker Interrupts { get; init; } = new();

    /// <summary>
    /// Splits the registered tools between brain and worker (<see cref="WorkerOptions.BrainTools"/> / <see cref="WorkerOptions.WorkerTools"/>).
    /// Call once after the host has registered its game tools, before the first turn. No-op when the worker is disabled.
    /// </summary>
    public void ApplyBrainSplit()
    {
        BrainSplit.Apply(Tools, Options.Worker);
        // The brain's door to the single-step macros it no longer has definitions for (hosts re-register it once every
        // macro exists, e.g. after vision tools).
        if (Options.Worker.Enabled && Options.Worker.BrainTools.Contains(DoTool.ToolName, StringComparer.OrdinalIgnoreCase)) DoTool.Register(Tools);
    }
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
        ILoggerFactory? loggers = null, TimeProvider? time = null, bool persist = true, IEmbeddingProvider? embeddings = null) =>
        new AgentRuntimeBuilder(options, sendChat, llm, position, demonstrations, loggers, time, persist, embeddings).Build();

    /// <summary>
    /// Continuity across a restart: the newest journal plus the last episodes of the previous run (mechanical summaries,
    /// zero tokens) when they are under 30 minutes old. A redeploy no longer starts from an empty history (no compaction
    /// had ever run, so there was no journal to restore).
    /// </summary>
    internal static string RestoredJournal(EpisodeIndexer episodes, DateTimeOffset now)
    {
        var journal = episodes.LatestJournal() ?? "";
        var recent = episodes.RecentBeforeRestart(TimeSpan.FromMinutes(30), 6, now);
        if (recent.Count == 0) return journal;
        var tail = "[before restart] " + string.Join("\n", recent.Select(r => TextUtil.Truncate(TextUtil.OneLine(r), 260)));
        return journal.Length > 0 ? journal + "\n" + tail : tail;
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
