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
/// The wiring behind <see cref="AgentRuntime.Create"/>, one step per concern in construction order (the built-in tool
/// registration order is part of the cached prefix; hooks are attached in the order the components exist).
/// </summary>
internal sealed class AgentRuntimeBuilder
{
    private readonly AgentOptions _o;
    private readonly Func<string, CancellationToken, Task> _sendChat;
    private readonly ILlmClient _llm;
    private readonly Func<BlockPos?>? _position;
    private readonly IDemonstrationSource? _demonstrations;
    private readonly ILoggerFactory? _loggers;
    private readonly TimeProvider _time;
    private readonly bool _persist;
    private readonly IEmbeddingProvider? _embeddings;

    private ToolRegistry _tools = null!;
    private EventQueue _events = null!;
    private CommandGate _commands = null!;
    private ChatRouter _chat = null!;
    private UsageTracker _usage = null!;
    private ILogger _chatLog = null!;
    private AgentDb _db = null!;
    private IEmbeddingProvider _embedder = null!;
    private RagIndex _index = null!;
    private Memory.Grooming.MemoryGroomer _groomer = null!;
    private TerseOptions _terse = null!;
    private string? _raptorStyle;
    private RaptorMemory? _raptor;
    private RaptorScheduler? _raptorJobs;
    private MemoryStore _memory = null!;
    private ObservationStore _observations = null!;
    private ContextManager _context = null!;
    private EpisodeIndexer _episodes = null!;
    private ActivityManager _activities = null!;
    private RoutineExecutor _routines = null!;
    private string _selfScope = null!;
    private SelfProfile? _self;
    private SelfGrowth? _growth;
    private WorkerAgent? _worker;
    private DirectiveStore _rules = null!;
    private StandingOrders _orders = null!;
    private MineClaw.Data.Versioning.WorldMode _world = null!;
    private string _knowledge = "";
    private bool _truncated;
    private int _chunks;
    private MemoryAgent? _memAgent;
    private BotAgent _agent = null!;
    private AutoRetriever? _retriever;
    private ReflectionPass? _reflection;
    private Telemetry.LatencyTracker _latency = null!;
    private Telemetry.InterruptTracker _interrupts = null!;

    public AgentRuntimeBuilder(AgentOptions options, Func<string, CancellationToken, Task> sendChat, ILlmClient? llm, Func<BlockPos?>? position,
        IDemonstrationSource? demonstrations, ILoggerFactory? loggers, TimeProvider? time, bool persist, IEmbeddingProvider? embeddings)
    {
        _o = options;
        _sendChat = sendChat;
        _time = time ?? TimeProvider.System;
        _llm = llm ?? LlmClientFactory.Create(options.Llm); // nothing on Ollama = the plain AnthropicLlmClient, as before
        options.Prices.RegisterProviders(options.Llm);  // Ollama models: tracked, priced from Prices:Ollama ($0 default)
        _position = position;
        _demonstrations = demonstrations;
        _loggers = loggers;
        _persist = persist;
        _embeddings = embeddings;
    }

    private string? Data(string file) => _persist ? Path.Combine(_o.DataDirectory, file) : null;

    private ILogger Log(string name) => _loggers?.CreateLogger(name) ?? NullLogger.Instance;

    private string Under(string f) => Path.IsPathRooted(f) ? f : Path.Combine(_o.DataDirectory, f);

    public AgentRuntime Build()
    {
        CreateChatAndBudget();
        AttachAddressingLogs();
        OpenMemory();
        CreateRaptor();
        CreateStores();
        CreateSelf();
        RegisterBuiltInTools();
        // Stale lessons in memory (e.g. "saplings can't be planted in the dark"): zero-token, idempotent, every start.
        KnowledgeCorrections.Apply(_index, _o.KnowledgeDirectory, Log("MineClaw.Rag"));
        ApplyWorldMode();
        LoadKnowledge();
        CreateDelegates();
        CreateAgent();
        WireWorker();
        CreateTelemetryAndVoice();
        AttachToAgent();
        StartBackground();
        return Result();
    }

    private void CreateChatAndBudget()
    {
        _tools = new ToolRegistry(_o.MaxToolResultChars);
        foreach (var (name, cap) in _o.ToolResultCaps) _tools.SetCap(name, cap);
        _events = new EventQueue(_o.Events, _time, _o.ChatDebounce);
        var commandLog = Log("MineClaw.Commands");
        _commands = new CommandGate(_o.Commands, () => _o.Self.TrustedPlayers, _time, commandLog,
            _persist && !string.IsNullOrWhiteSpace(_o.Commands.AuditFile) ? Data(_o.Commands.AuditFile) : null);
        // The agent's outgoing choke point: a '/' line that isn't always-allowed or armed by run_command never leaves.
        var commands = _commands;
        var sendChat = _sendChat;
        Func<string, CancellationToken, Task> gatedSend = (line, ct) => commands.AllowOutgoing(line) ? sendChat(line, ct) : Task.CompletedTask;
        _chat = new ChatRouter(_o.Chat, _events, gatedSend, _time) { Commands = _commands, Debounce = _o.ChatDebounce, Stats = _events.Stats };
        var llmOptions = _o.Llm;
        _usage = new UsageTracker(_o.Budget, _o.Prices, _time, Data(_o.Budget.UsageFile))
        {
            Classify = m => llmOptions.TierOfModel(m),
            ProfileOf = m => llmOptions.ProfileOfModel(m),
        };
    }

    private void AttachAddressingLogs()
    {
        var options = _o;
        var chat = _chat;
        chat.IsRegular = p => options.Self.TrustedPlayers.Concat(options.Self.RegularPlayers).Any(r => r.Equals(p, StringComparison.OrdinalIgnoreCase));
        chat.Classifier = AddressClassifier.Create(_llm, options, _usage, Log("MineClaw.Chat"));
        var chatLog = _chatLog = Log("MineClaw.Chat");
        chat.AddressDecided += (m, d) =>
        {
            if (m.Kind is not (ChatKind.Public or ChatKind.Whisper)) return;
            chatLog.LogInformation("addressing: {Player} \"{Text}\" → {Verdict} ({Reason}: {Why})", m.Player, TextUtil.Truncate(m.Text, 60),
                d.Addressed ? "us" : "not us", d.Reason, d.Why);
        };
        chat.BotDetected += (name, why) => chatLog.LogInformation("addressing: {Name} is a bot ({Why}): it wakes us only when it names us, once per {Min} min", name, why, options.Chat.BotReplyCooldownMinutes);
        chat.AddressMissed += (player, missed, how) =>
            chatLog.LogInformation("addressing miss: {Player} \"{Missed}\" was ignored, then addressed by {How}; {Stats}", player, missed, how, chat.Addressing);
    }

    private void OpenMemory()
    {
        var rag = _o.Rag;
        _db = AgentDb.Open(_persist ? Under(rag.DbFile) : null, Log("MineClaw.Db"),
            seedPath: _persist && !string.IsNullOrWhiteSpace(rag.SeedFile) ? Under(rag.SeedFile) : null);
        _embedder = ChooseEmbedder();
        _index = new RagIndex(_db, rag, _embedder, _usage, _o.Embeddings, _time, Log("MineClaw.Rag"));
        // Every doc write is groomed first (normalize, de-noise, merge, phrase, size, redact; activity coalescing).
        _groomer = new Memory.Grooming.MemoryGroomer(_o.Memory.Groom, Memory.Grooming.GroomEnvironment.From(_o, _db, time: _time), Log("MineClaw.Groom"));
        _index.Groomer = _groomer;
        _terse = _o.Terse ?? new TerseOptions();
        _raptorStyle = Prompts.RaptorStyle(_terse.For(TersePurpose.Raptor)) is { Length: > 0 } rs ? rs : null;
    }

    private IEmbeddingProvider ChooseEmbedder()
    {
        var rag = _o.Rag;
        var embedder = _embeddings ?? (rag.Enabled ? EmbeddingProviders.Create(_o.Embeddings, _o.Llm.Ollama) : NullEmbeddingProvider.Instance);
        if (!rag.Enabled) return NullEmbeddingProvider.Instance;
        if (_embeddings != null) return embedder;
        // One db never mixes vector sizes (a provider/model switch with another dimension count is refused unless Reembed).
        var (checkedEmbedder, note) = EmbeddingDimensionGuard.Check(_db, embedder, _o.Embeddings.Reembed);
        if (note != null) Log("MineClaw.Rag").LogWarning("{Note}", note);
        return checkedEmbedder;
    }

    private void CreateRaptor()
    {
        var rag = _o.Rag;
        if (!rag.Enabled || !rag.Raptor.Enabled || !_embedder.IsAvailable) return;
        var summarizer = new HaikuSummarizer(_llm, _o.Llm, _usage) { StyleHint = _raptorStyle };
        _raptor = new RaptorMemory(_db, _index, new CachedEmbedder(_db, _embedder, _o.Embeddings, _usage), summarizer, rag.Raptor,
            log: Log("MineClaw.Raptor"));
        _index.Augmenter = _raptor; // recall / ask_memory / auto-retrieval: hybrid lists + RAPTOR, fused by RRF
        _raptor.Store.LoadWritesBack = rag.Raptor.Maintenance; // a clone only reads main's tree
        if (_persist && rag.Raptor.Maintenance)
            _raptorJobs = new RaptorScheduler(_raptor, summarizer, _db, _usage, _time, Log("MineClaw.Raptor"));
    }

    private void CreateStores()
    {
        _memory = new MemoryStore(_db, _index, _time, Data("memory.json"));
        _observations = new ObservationStore(_index, _time);
        _context = new ContextManager(_o.Context);
        _episodes = new EpisodeIndexer(_index, _context, _position, _time, Log("MineClaw.Rag"));
        _activities = new ActivityManager(_tools, _events, _time);
        _routines = new RoutineExecutor(_tools);
    }

    private void CreateSelf()
    {
        _selfScope = _o.Hive.ScopeFor(_o.Chat.Username);
        if (_selfScope == SelfSchema.MainScope) _o.Hive.MainUsername ??= _o.Chat.Username;
        _self = _o.Self.Enabled ? new SelfProfile(_db, _o.Self, _index, _time, _selfScope) : null;
        if (_self == null) return;
        var hiveProfile = _selfScope != SelfSchema.MainScope ? new SelfProfile(_db, _o.Self, _index, _time, SelfSchema.HiveScope) : null;
        var chat = _chat;
        _growth = new SelfGrowth(new SelfStore(_db, _o.Self), _self, _o.Chat.Username, _o.Hive, hiveProfile,
            () => [chat.Username, chat.DisplayName, .. chat.Aliases], _o.Triage.ActionWords, _llm, _usage, _time, Log("MineClaw.Self"));
        _growth.Attach(_context); // zero-token signal hook on every committed episode
        if (_o.Rag.Enabled && _o.Self.SelfTree && _embedder.IsAvailable)
            _growth.Tree = new SelfTree(_index, _raptor?.Embedder ?? new CachedEmbedder(_db, _embedder, _o.Embeddings, _usage),
                new HaikuSummarizer(_llm, _o.Llm, _usage) { StyleHint = _raptorStyle }, _o.Self, _time, Log("MineClaw.Self"));
    }

    /// <summary>Built-in, game-agnostic tools (registered first so their order is stable — it's part of the cached prefix).</summary>
    private void RegisterBuiltInTools()
    {
        RegisterStop();
        MemoryTools.RegisterAll(_tools, _memory, _position);
        CreateRules();
        // Foreign bots' command prefixes ("c trees" is robinchan's) survive restarts; trusted players' rules declare them.
        CommandPrefixStore.Attach(_chat, _db, _rules, _time, _chatLog);
        WorldTools.Register(_tools, _observations, _position, _time, _memory);
        IdentityTools.ApplyPersisted(_memory, _chat);
        IdentityTools.RegisterAll(_tools, _memory, _chat);
        RoutineTools.RegisterAll(_tools, _memory, _routines);
        var orders = _orders = new StandingOrders(_memory, _tools, _activities, _events, _o.Chat.Username, _o.Idle, _time, Log("MineClaw.Orders"));
        var activities = _activities;
        HumanTools.RegisterAskPlayer(_tools, _chat, _o.Questions, _events, _ => orders.ResumeIfIdle("question timed out", activities.Shutdown),
            AskedBefore.For(_db, _chat, _time));
        orders.Register();
        if (_demonstrations != null) HumanTools.RegisterWatchDemo(_tools, _chat, _memory, _demonstrations);
        if (_self != null) SelfTools.RegisterAll(_tools, _self, _index, _time, _growth);
        CommandTools.Register(_tools, _commands, _chat);
    }

    private void RegisterStop()
    {
        var activities = _activities;
        _tools.Register("stop", "Stop the current task/activity.", ToolSchema.Empty, (_, _) =>
        {
            // What the worker was actually doing (and why), so a correction to the players is the real story, not a guess.
            var trail = _worker?.Trail;
            var what = activities.Cancel("stop");
            if (what == null) return Task.FromResult(ToolResult.Success("nothing running"));
            return Task.FromResult(ToolResult.Success($"stopped {what}" +
                (what.Contains(WorkerAgent.ToolName, StringComparison.Ordinal) && !string.IsNullOrEmpty(trail) ? $"; worker was: {trail}" : "")));
        }, cheapTier: true);
    }

    /// <summary>Trusted players' instructions are standing rules, not claims (live 10-28 claim#9): shared store, one-time claim migration.</summary>
    private void CreateRules()
    {
        var options = _o;
        var chat = _chat;
        var rules = _rules = DirectiveStore.For(_db, _index);
        rules.Time = _time;
        rules.CloneId = options.Chat.Username;
        rules.IsTrusted = p => options.Self.TrustedPlayers.Any(t => t.Equals(p, StringComparison.OrdinalIgnoreCase));
        rules.Nick = p => chat.NicknamesOf(p).FirstOrDefault() ?? p;
        try { rules.MigrateClaims(Log("MineClaw.Rules")); }
        catch (Exception ex) { Log("MineClaw.Rules").LogWarning(ex, "directive migration failed"); }
    }

    /// <summary>The world mode (skyblock vs a normal world) from the active packs: a pack of another version counts as a plain world.</summary>
    private void ApplyWorldMode()
    {
        var worldPacks = ActivePacks();
        _world = MineClaw.Data.Versioning.WorldMode.FromModes(worldPacks.Modes?.Select(m => m.Mode));
        if (_o.ApplyWorldMode) MineClaw.Data.Versioning.WorldMode.SetDefault(_world);
    }

    private Admin.Shards.KnowledgePackInfo ActivePacks()
    {
        var packs = Admin.Shards.KnowledgePack.Active(_db);
        if (_o.GameVersion is { Length: > 0 } v && !v.Equals(packs.Version, StringComparison.OrdinalIgnoreCase))
            packs = new Admin.Shards.KnowledgePackInfo(v) { Modes = [] };
        return packs;
    }

    private void LoadKnowledge()
    {
        if (!_o.Rag.Enabled)
        {
            _knowledge = Prompts.LoadKnowledge(_o.KnowledgeDirectory, _o.MaxKnowledgeChars, out _truncated, ActivePacks());
            return;
        }
        var k = KnowledgeIndexer.Sync(_index, _o.KnowledgeDirectory, _o.Rag, _o.GameVersion);
        if (k.VersionMismatch != null) Log("MineClaw.Rag").LogWarning("knowledge: {Mismatch}", k.VersionMismatch);
        _knowledge = k.Core;
        _truncated = k.CoreTruncated;
        _chunks = k.Chunks;
    }

    /// <summary>Brain-only delegation tools (registered after the built-ins so the cached tool order stays stable).</summary>
    private void CreateDelegates()
    {
        if (_o.Worker.Enabled)
        {
            _worker = new WorkerAgent(_o.Worker, _o.Llm, _llm, _tools, _usage, _knowledge, _time, Log("MineClaw.Worker"),
                _terse.For(TersePurpose.Worker));
            _worker.Register();
        }
        if (_o.AskMemory.Enabled)
        {
            _memAgent = new MemoryAgent(_o.AskMemory, _llm, _tools, _usage, Log("MineClaw.Memory"), _terse.For(TersePurpose.AskMemory));
            _memAgent.Register();
        }
    }

    private void CreateAgent()
    {
        _agent = new BotAgent(_o, _llm, _tools, _events, _chat, _usage, _context, _activities, _knowledge, _time,
            _loggers?.CreateLogger<BotAgent>(), knowledgeIsCore: _o.Rag.Enabled);
        _agent.Orders = _orders;
        _agent.StandingRules = _rules.HeaderLine;
        // Cached line (startup only): which delegate/hive profile fits what. Absent unless ≥2 profiles are usable.
        if (_worker != null && ProfilePrompts.GuidanceLine(_o.Llm) is { } profiles) _agent.ExtendSystem(profiles);
        // A swapped-in knowledge pack (memory shards) names its Minecraft version in the cached prefix; the default b1.8.1 adds nothing.
        // The worker's cached prompt gets it too (it reads the same version's recipes and block facts).
        if (_o.Rag.Enabled && Admin.Shards.KnowledgePack.PromptLine(_db) is { } packLine)
        {
            _agent.ExtendSystem(packLine);
            _worker?.ExtendSystem(packLine);
        }
    }

    private void WireWorker()
    {
        if (_worker is not { } worker) return;
        var agent = _agent;
        var events = _events;
        worker.StateHeader = () => agent.StateHeader?.Invoke() ?? "";
        // needs_input with a default: the worker carries on; the brain hears the question (and asks/amends).
        worker.Questions = _o.Questions;
        worker.Asked += (q, d) => events.Post(GameEvent.Notice(
            $"worker asks: {TextUtil.Truncate(q, 120)} (it carries on with \"{d}\"; ask the player with a default, amend_task if they differ)",
            EventPriority.Normal));
        // Chat that wakes the brain mid-task says what the worker is doing, so it can amend instead of restarting.
        agent.BusyDetail = () => worker.IsRunning && worker.Trail is { Length: > 0 } t
            ? "worker: " + t + (worker.LastLiveAmendment is { } la ? $" (already forwarded to the worker: {la})" : "")
            : null;
        // A player's "just like 5" / "never mind" mid-task goes to the worker at once (brainstem path, zero tokens): the brain's
        // amend_task arrived 6 s later, after the worker had crafted 24 torches (live 10-27 21:26).
        _chat.Accepted += m =>
        {
            try { if (m.Player is { Length: > 0 } p) worker.AmendFromChat(p, m.Text); }
            catch (Exception) { /* chat never breaks */ }
        };
    }

    private void CreateTelemetryAndVoice()
    {
        var rag = _o.Rag;
        _retriever = !rag.Enabled || !rag.AutoRetrieve ? null
            : _raptor != null ? new AutoRetriever(new RaptorRetrievalIndex(_index, _raptor), rag, _context, _episodes)
            : new AutoRetriever(_index, _context, _episodes);
        _reflection = _self != null ? new ReflectionPass(_self, _index, _llm, _usage, _time, Log("MineClaw.Self")) { Growth = _growth, Terse = _terse.For(TersePurpose.Reflection) } : null;
        _agent.Position = _position;
        // Latency telemetry: chat → first visible reaction / movement / game action, and the model rounds before it.
        var chat = _chat;
        _latency = new Telemetry.LatencyTracker(_time) { Bot = _o.Chat.Username, Names = () => [chat.Username, chat.DisplayName, .. chat.Aliases] };
        _agent.Latency = _latency;
        CreateVoice();
        // Script interrupt → resume: every LLM call while an interrupt is open counts toward it (patch turn vs full turn).
        _interrupts = new Telemetry.InterruptTracker(_time) { Bot = _o.Chat.Username };
        _usage.Recorded += _interrupts.NoteCall;
        AttachLatencyHooks();
    }

    /// <summary>The character's voice under the brain: prefetched in-character lines said instantly (docs/design/reactive-character.md).</summary>
    private void CreateVoice()
    {
        // main speaks; clones stay addressed-only
        if (_o.Brainstem is not { Enabled: true, Character: { } co } || !(co.InstantLines || co.Prefetch) || _selfScope != SelfSchema.MainScope) return;
        var options = _o;
        var chat = _chat;
        var growth = _growth;
        var bank = new Character.LineBank(co, _time);
        var prefetch = new Character.LinePrefetcher(bank, _llm, _usage, co,
            () => string.IsNullOrWhiteSpace(options.Persona) ? Prompts.DefaultPersona : options.Persona, () => chat.DisplayName, _time, Log("MineClaw.Character"))
        {
            Self = ps => growth?.RenderBlock(ps.ToList()),
        };
        _agent.Voice = new Character.CharacterVoice(co, chat, bank, prefetch, _time, Log("MineClaw.Character")) { Latency = _latency };
    }

    private void AttachLatencyHooks()
    {
        var latency = _latency;
        _tools.Executing += (name, _) => latency.NoteTool(name, ActivityManager.RunningKind is ActivityKind.Speculative or ActivityKind.Social);
        _activities.Started += (_, kind, steps) =>
        {
            if (kind != ActivityKind.Social && steps.Count > 0 && ActivityManager.MovementTools.Contains(steps[0].Tool))
                latency.NoteMove(kind == ActivityKind.Speculative);
        };
        _activities.SpeculationResolved += (adopted, _) => latency.NoteSpeculation(adopted);
        if (_worker != null) _worker.CallCompleted += latency.NoteRound;
    }

    private void AttachToAgent()
    {
        _agent.Rag = _index;
        _agent.Retriever = _retriever;
        _agent.Self = _self;
        _agent.Reflection = _reflection;
        _agent.Growth = _growth;
    }

    private void StartBackground()
    {
        // A clone's seed quirks are created once at first spawn (config, else one cheap call), then frozen.
        if (_persist && _growth is { IsClone: true } growth) _ = Task.Run(() => growth.EnsureSeedsAsync());
        if (_o.Rag.Enabled && _o.Rag.RestoreJournal) _context.SetJournal(AgentRuntime.RestoredJournal(_episodes, _time.GetUtcNow()));
        // The one-off compaction of per-minute activity episodes (opt-in; idempotent; backup first), before the tree loads.
        if (_persist && _o.Rag.Raptor.Maintenance && _o.Memory.Groom.Activity.CompactOnStartup && _db.Path != null) CompactActivityOnStartup();
        // Off the caller's thread: loads the stored tree, then (persistent runtimes) runs maintenance in the background.
        if (_raptorJobs != null) _raptorJobs.Start();
        else _raptor?.StartLoading();
    }

    private void CompactActivityOnStartup()
    {
        var clog = Log("MineClaw.Admin");
        try
        {
            var compaction = new Admin.ActivityCompaction(_db, _index, _groomer, _raptor, null, clog);
            var preview = compaction.RunAsync(dryRun: true).GetAwaiter().GetResult();
            if (preview.Processed > 0)
            {
                var backup = Admin.AdminBackup.Create(_db, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_db.Path!))!, "backups"));
                var done = compaction.RunAsync(dryRun: false, backup: backup).GetAwaiter().GetResult();
                clog.LogInformation("startup activity compaction (backup {Backup}): {Message}", backup, done.Message);
            }
        }
        catch (Exception ex) { clog.LogWarning(ex, "startup activity compaction failed; memory left as it was"); }
    }

    private AgentRuntime Result() => new()
    {
        Options = _o, World = _world, Tools = _tools, Events = _events, Chat = _chat, Commands = _commands, Usage = _usage, Memory = _memory,
        Context = _context, Activities = _activities, Routines = _routines, Agent = _agent, Llm = _llm,
        Db = _db, Rag = _index, Observations = _observations, Episodes = _episodes, Retriever = _retriever, Self = _self,
        Reflection = _reflection, Growth = _growth, Embeddings = _embedder, Worker = _worker, MemoryAgent = _memAgent, Raptor = _raptor, RaptorMaintenance = _raptorJobs,
        Latency = _latency,
        Interrupts = _interrupts,
        KnowledgeTruncated = _truncated, KnowledgeChars = _knowledge.Length, KnowledgeChunks = _chunks,
    };
}
