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
        ILoggerFactory? loggers = null, TimeProvider? time = null, bool persist = true, IEmbeddingProvider? embeddings = null)
    {
        time ??= TimeProvider.System;
        llm ??= LlmClientFactory.Create(options.Llm); // nothing on Ollama = the plain AnthropicLlmClient, as before
        options.Prices.RegisterProviders(options.Llm);  // Ollama models: tracked, priced from Prices:Ollama ($0 default)
        string? Data(string file) => persist ? Path.Combine(options.DataDirectory, file) : null;
        ILogger Log(string name) => loggers?.CreateLogger(name) ?? NullLogger.Instance;

        var tools = new ToolRegistry(options.MaxToolResultChars);
        foreach (var (name, cap) in options.ToolResultCaps) tools.SetCap(name, cap);
        var events = new EventQueue(options.Events, time, options.ChatDebounce);
        var commandLog = Log("MineClaw.Commands");
        var commands = new CommandGate(options.Commands, () => options.Self.TrustedPlayers, time, commandLog,
            persist && !string.IsNullOrWhiteSpace(options.Commands.AuditFile) ? Data(options.Commands.AuditFile) : null);
        // The agent's outgoing choke point: a '/' line that isn't always-allowed or armed by run_command never leaves.
        Func<string, CancellationToken, Task> gatedSend = (line, ct) => commands.AllowOutgoing(line) ? sendChat(line, ct) : Task.CompletedTask;
        var chat = new ChatRouter(options.Chat, events, gatedSend, time) { Commands = commands, Debounce = options.ChatDebounce, Stats = events.Stats };
        var usage = new UsageTracker(options.Budget, options.Prices, time, Data(options.Budget.UsageFile))
        {
            Classify = m => options.Llm.TierOfModel(m),
            ProfileOf = m => options.Llm.ProfileOfModel(m),
        };

        chat.IsRegular = p => options.Self.TrustedPlayers.Concat(options.Self.RegularPlayers).Any(r => r.Equals(p, StringComparison.OrdinalIgnoreCase));
        chat.Classifier = AddressClassifier.Create(llm, options, usage, Log("MineClaw.Chat"));
        var chatLog = Log("MineClaw.Chat");
        chat.AddressDecided += (m, d) =>
        {
            if (m.Kind is not (ChatKind.Public or ChatKind.Whisper)) return;
            chatLog.LogInformation("addressing: {Player} \"{Text}\" → {Verdict} ({Reason}: {Why})", m.Player, TextUtil.Truncate(m.Text, 60),
                d.Addressed ? "us" : "not us", d.Reason, d.Why);
        };
        chat.BotDetected += (name, why) => chatLog.LogInformation("addressing: {Name} is a bot ({Why}): it wakes us only when it names us, once per {Min} min", name, why, options.Chat.BotReplyCooldownMinutes);
        chat.AddressMissed += (player, missed, how) =>
            chatLog.LogInformation("addressing miss: {Player} \"{Missed}\" was ignored, then addressed by {How}; {Stats}", player, missed, how, chat.Addressing);

        var rag = options.Rag;
        string Under(string f) => Path.IsPathRooted(f) ? f : Path.Combine(options.DataDirectory, f);
        var db = AgentDb.Open(persist ? Under(rag.DbFile) : null, Log("MineClaw.Db"),
            seedPath: persist && !string.IsNullOrWhiteSpace(rag.SeedFile) ? Under(rag.SeedFile) : null);
        var embedder = embeddings ?? (rag.Enabled ? EmbeddingProviders.Create(options.Embeddings, options.Llm.Ollama) : NullEmbeddingProvider.Instance);
        if (!rag.Enabled) embedder = NullEmbeddingProvider.Instance;
        else if (embeddings == null)
        {
            // One db never mixes vector sizes (a provider/model switch with another dimension count is refused unless Reembed).
            var (checkedEmbedder, note) = EmbeddingDimensionGuard.Check(db, embedder, options.Embeddings.Reembed);
            embedder = checkedEmbedder;
            if (note != null) Log("MineClaw.Rag").LogWarning("{Note}", note);
        }
        var index = new RagIndex(db, rag, embedder, usage, options.Embeddings, time, Log("MineClaw.Rag"));
        // Every doc write is groomed first (normalize, de-noise, merge, phrase, size, redact; activity coalescing).
        var groomer = new global::MineClaw.Agent.Memory.Grooming.MemoryGroomer(options.Memory.Groom, global::MineClaw.Agent.Memory.Grooming.GroomEnvironment.From(options, db, time: time), Log("MineClaw.Groom"));
        index.Groomer = groomer;
        var terse = options.Terse ?? new TerseOptions();
        var raptorStyle = Prompts.RaptorStyle(terse.For(TersePurpose.Raptor)) is { Length: > 0 } rs ? rs : null;
        RaptorMemory? raptor = null;
        RaptorScheduler? raptorJobs = null;
        if (rag.Enabled && rag.Raptor.Enabled && embedder.IsAvailable)
        {
            var summarizer = new HaikuSummarizer(llm, options.Llm, usage) { StyleHint = raptorStyle };
            raptor = new RaptorMemory(db, index, new CachedEmbedder(db, embedder, options.Embeddings, usage), summarizer, rag.Raptor,
                log: Log("MineClaw.Raptor"));
            index.Augmenter = raptor; // recall / ask_memory / auto-retrieval: hybrid lists + RAPTOR, fused by RRF
            raptor.Store.LoadWritesBack = rag.Raptor.Maintenance; // a clone only reads main's tree
            if (persist && rag.Raptor.Maintenance)
                raptorJobs = new RaptorScheduler(raptor, summarizer, db, usage, time, Log("MineClaw.Raptor"));
        }
        var memory = new MemoryStore(db, index, time, Data("memory.json"));
        var observations = new ObservationStore(index, time);
        var context = new ContextManager(options.Context);
        var episodes = new EpisodeIndexer(index, context, position, time, Log("MineClaw.Rag"));
        var activities = new ActivityManager(tools, events, time);
        var routines = new RoutineExecutor(tools);
        var selfScope = options.Hive.ScopeFor(options.Chat.Username);
        if (selfScope == SelfSchema.MainScope) options.Hive.MainUsername ??= options.Chat.Username;
        SelfProfile? self = options.Self.Enabled ? new SelfProfile(db, options.Self, index, time, selfScope) : null;
        SelfGrowth? growth = null;
        if (self != null)
        {
            var hiveProfile = selfScope != SelfSchema.MainScope ? new SelfProfile(db, options.Self, index, time, SelfSchema.HiveScope) : null;
            growth = new SelfGrowth(new SelfStore(db, options.Self), self, options.Chat.Username, options.Hive, hiveProfile,
                () => [chat.Username, chat.DisplayName, .. chat.Aliases], options.Triage.ActionWords, llm, usage, time, Log("MineClaw.Self"));
            growth.Attach(context); // zero-token signal hook on every committed episode
            if (rag.Enabled && options.Self.SelfTree && embedder.IsAvailable)
                growth.Tree = new SelfTree(index, raptor?.Embedder ?? new CachedEmbedder(db, embedder, options.Embeddings, usage),
                    new HaikuSummarizer(llm, options.Llm, usage) { StyleHint = raptorStyle }, options.Self, time, Log("MineClaw.Self"));
        }

        // Built-in, game-agnostic tools (registered first so their order is stable — it's part of the cached prefix).
        WorkerAgent? worker = null;
        tools.Register("stop", "Stop the current task/activity.", ToolSchema.Empty, (_, _) =>
        {
            // What the worker was actually doing (and why), so a correction to the players is the real story, not a guess.
            var trail = worker?.Trail;
            var what = activities.Cancel("stop");
            if (what == null) return Task.FromResult(ToolResult.Success("nothing running"));
            return Task.FromResult(ToolResult.Success($"stopped {what}" +
                (what.Contains(WorkerAgent.ToolName, StringComparison.Ordinal) && !string.IsNullOrEmpty(trail) ? $"; worker was: {trail}" : "")));
        }, cheapTier: true);
        MemoryTools.RegisterAll(tools, memory, position);
        // Trusted players' instructions are standing rules, not claims (live 10-28 claim#9): shared store, one-time claim migration.
        var rules = DirectiveStore.For(db, index);
        rules.Time = time;
        rules.CloneId = options.Chat.Username;
        rules.IsTrusted = p => options.Self.TrustedPlayers.Any(t => t.Equals(p, StringComparison.OrdinalIgnoreCase));
        rules.Nick = p => chat.NicknamesOf(p).FirstOrDefault() ?? p;
        try { rules.MigrateClaims(Log("MineClaw.Rules")); }
        catch (Exception ex) { Log("MineClaw.Rules").LogWarning(ex, "directive migration failed"); }
        // Foreign bots' command prefixes ("c trees" is robinchan's) survive restarts; trusted players' rules declare them.
        CommandPrefixStore.Attach(chat, db, rules, time, chatLog);
        WorldTools.Register(tools, observations, position, time, memory);
        IdentityTools.ApplyPersisted(memory, chat);
        IdentityTools.RegisterAll(tools, memory, chat);
        RoutineTools.RegisterAll(tools, memory, routines);
        var orders = new StandingOrders(memory, tools, activities, events, options.Chat.Username, options.Idle, time, Log("MineClaw.Orders"));
        HumanTools.RegisterAskPlayer(tools, chat, options.Questions, events, _ => orders.ResumeIfIdle("question timed out", activities.Shutdown),
            AskedBefore.For(db, chat, time));
        orders.Register();
        if (demonstrations != null) HumanTools.RegisterWatchDemo(tools, chat, memory, demonstrations);
        if (self != null) SelfTools.RegisterAll(tools, self, index, time, growth);
        CommandTools.Register(tools, commands, chat);

        // Stale lessons in memory (e.g. "saplings can't be planted in the dark"): zero-token, idempotent, every start.
        KnowledgeCorrections.Apply(index, options.KnowledgeDirectory, Log("MineClaw.Rag"));

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
        if (options.Worker.Enabled)
        {
            worker = new WorkerAgent(options.Worker, options.Llm, llm, tools, usage, knowledge, time, Log("MineClaw.Worker"),
                terse.For(TersePurpose.Worker));
            worker.Register();
        }
        MemoryAgent? memAgent = null;
        if (options.AskMemory.Enabled)
        {
            memAgent = new MemoryAgent(options.AskMemory, llm, tools, usage, Log("MineClaw.Memory"), terse.For(TersePurpose.AskMemory));
            memAgent.Register();
        }

        var agent = new BotAgent(options, llm, tools, events, chat, usage, context, activities, knowledge, time,
            loggers?.CreateLogger<BotAgent>(), knowledgeIsCore: rag.Enabled);
        agent.Orders = orders;
        agent.StandingRules = rules.HeaderLine;
        // Cached line (startup only): which delegate/hive profile fits what. Absent unless ≥2 profiles are usable.
        if (worker != null && ProfilePrompts.GuidanceLine(options.Llm) is { } profiles) agent.ExtendSystem(profiles);
        // A swapped-in knowledge pack (memory shards) names its Minecraft version in the cached prefix; the default b1.8.1 adds nothing.
        if (rag.Enabled && Admin.Shards.KnowledgePack.PromptLine(db) is { } packLine) agent.ExtendSystem(packLine);
        if (worker != null)
        {
            worker.StateHeader = () => agent.StateHeader?.Invoke() ?? "";
            // needs_input with a default: the worker carries on; the brain hears the question (and asks/amends).
            worker.Questions = options.Questions;
            worker.Asked += (q, d) => events.Post(GameEvent.Notice(
                $"worker asks: {TextUtil.Truncate(q, 120)} (it carries on with \"{d}\"; ask the player with a default, amend_task if they differ)",
                EventPriority.Normal));
            // Chat that wakes the brain mid-task says what the worker is doing, so it can amend instead of restarting.
            agent.BusyDetail = () => worker.IsRunning && worker.Trail is { Length: > 0 } t
                ? "worker: " + t + (worker.LastLiveAmendment is { } la ? $" (already forwarded to the worker: {la})" : "")
                : null;
            // A player's "just like 5" / "never mind" mid-task goes to the worker at once (brainstem path, zero tokens): the brain's
            // amend_task arrived 6 s later, after the worker had crafted 24 torches (live 10-27 21:26).
            chat.Accepted += m =>
            {
                try { if (m.Player is { Length: > 0 } p) worker.AmendFromChat(p, m.Text); }
                catch (Exception) { /* chat never breaks */ }
            };
        }
        var retriever = !rag.Enabled || !rag.AutoRetrieve ? null
            : raptor != null ? new AutoRetriever(new RaptorRetrievalIndex(index, raptor), rag, context, episodes)
            : new AutoRetriever(index, context, episodes);
        var reflection = self != null ? new ReflectionPass(self, index, llm, usage, time, Log("MineClaw.Self")) { Growth = growth, Terse = terse.For(TersePurpose.Reflection) } : null;
        agent.Position = position;
        // Latency telemetry: chat → first visible reaction / movement / game action, and the model rounds before it.
        var latency = new Telemetry.LatencyTracker(time) { Bot = options.Chat.Username };
        agent.Latency = latency;
        tools.Executing += (name, _) => latency.NoteTool(name, ActivityManager.RunningKind is ActivityKind.Speculative or ActivityKind.Social);
        activities.Started += (_, kind, steps) =>
        {
            if (kind != ActivityKind.Social && steps.Count > 0 && ActivityManager.MovementTools.Contains(steps[0].Tool))
                latency.NoteMove(kind == ActivityKind.Speculative);
        };
        activities.SpeculationResolved += (adopted, _) => latency.NoteSpeculation(adopted);
        if (worker != null) worker.CallCompleted += latency.NoteRound;
        agent.Rag = index;
        agent.Retriever = retriever;
        agent.Self = self;
        agent.Reflection = reflection;
        agent.Growth = growth;
        // A clone's seed quirks are created once at first spawn (config, else one cheap call), then frozen.
        if (persist && growth is { IsClone: true }) _ = Task.Run(() => growth.EnsureSeedsAsync());
        if (rag.Enabled && rag.RestoreJournal) context.SetJournal(RestoredJournal(episodes, time.GetUtcNow()));
        // The one-off compaction of per-minute activity episodes (opt-in; idempotent; backup first), before the tree loads.
        if (persist && rag.Raptor.Maintenance && options.Memory.Groom.Activity.CompactOnStartup && db.Path != null)
        {
            var clog = Log("MineClaw.Admin");
            try
            {
                var compaction = new global::MineClaw.Agent.Admin.ActivityCompaction(db, index, groomer, raptor, null, clog);
                var preview = compaction.RunAsync(dryRun: true).GetAwaiter().GetResult();
                if (preview.Processed > 0)
                {
                    var backup = global::MineClaw.Agent.Admin.AdminBackup.Create(db, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db.Path))!, "backups"));
                    var done = compaction.RunAsync(dryRun: false, backup: backup).GetAwaiter().GetResult();
                    clog.LogInformation("startup activity compaction (backup {Backup}): {Message}", backup, done.Message);
                }
            }
            catch (Exception ex) { clog.LogWarning(ex, "startup activity compaction failed; memory left as it was"); }
        }
        // Off the caller's thread: loads the stored tree, then (persistent runtimes) runs maintenance in the background.
        if (raptorJobs != null) raptorJobs.Start();
        else raptor?.StartLoading();

        return new AgentRuntime
        {
            Options = options, Tools = tools, Events = events, Chat = chat, Commands = commands, Usage = usage, Memory = memory,
            Context = context, Activities = activities, Routines = routines, Agent = agent, Llm = llm,
            Db = db, Rag = index, Observations = observations, Episodes = episodes, Retriever = retriever, Self = self,
            Reflection = reflection, Growth = growth, Embeddings = embedder, Worker = worker, MemoryAgent = memAgent, Raptor = raptor, RaptorMaintenance = raptorJobs,
            Latency = latency,
            KnowledgeTruncated = truncated, KnowledgeChars = knowledge.Length, KnowledgeChunks = chunks,
        };
    }

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
