using MineClaw.Agent.Budget;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Self;

namespace MineClaw.Agent;

/// <summary>All agent configuration; bind from appsettings section "Agent".</summary>
public sealed class AgentOptions
{
    public LlmOptions Llm { get; set; } = new();
    public BudgetOptions Budget { get; set; } = new();
    public PriceTable Prices { get; set; } = new();
    public ChatOptions Chat { get; set; } = new();
    public EventQueueOptions Events { get; set; } = new();
    /// <summary>
    /// "Agent:ChatDebounce": per-player typing debounce before a turn, one cancel-and-restart while the first response is
    /// still generating, "[new chat]" injection at round boundaries, and reply merging for ask_player &amp; co.
    /// </summary>
    public ChatDebounceOptions ChatDebounce { get; set; } = new();
    /// <summary>"Agent:Questions": ask_player with a recommended default never parks the bot; timeouts go with the default.</summary>
    public Human.QuestionOptions Questions { get; set; } = new();
    /// <summary>"Agent:Idle": standing-order resume and the idle default job while players are online.</summary>
    public Human.IdleOptions Idle { get; set; } = new();
    public ContextOptions Context { get; set; } = new();
    public TriageOptions Triage { get; set; } = new();
    /// <summary>Local retrieval (SQLite FTS5 + vectors + R*Tree): "Agent:Rag".</summary>
    public RagOptions Rag { get; set; } = new();
    /// <summary>Embedding provider: "Agent:Embeddings" (empty key/endpoint = FTS-only).</summary>
    public EmbeddingOptions Embeddings { get; set; } = new();
    /// <summary>Evolving personality layer on top of <see cref="Persona"/>: "Agent:Self".</summary>
    public SelfOptions Self { get; set; } = new();
    /// <summary>
    /// Hive clones sharing this db (the host binds the top-level "Hive" section here). Decides this process's personality
    /// scope: a clone username → its own shard (+ the shared hive profile); anything else → "main".
    /// </summary>
    public HiveOptions Hive { get; set; } = new();
    /// <summary>Delegated multi-step tasks on the worker tier and the brain/worker tool split: "Agent:Worker".</summary>
    public WorkerOptions Worker { get; set; } = new();
    /// <summary>The brain's <c>ask_memory</c> tool (cheap-tier retrieval loop): "Agent:AskMemory".</summary>
    public AskMemoryOptions AskMemory { get; set; } = new();
    /// <summary>
    /// Caveman-style terse register for non-chat output (thinking, delegate specs, worker/ask_memory results, journal,
    /// reflection): "Agent:Terse" = off|lite|full|ultra, or an object with per-purpose overrides. Chat is never affected.
    /// </summary>
    public TerseOptions Terse { get; set; } = new();

    /// <summary>Server-command gate (always-allowed list, blocklist, authorisation window, audit file): "Agent:Commands".</summary>
    public CommandOptions Commands { get; set; } = new();

    /// <summary>
    /// A long-running call that failed on a target is not re-run on that target for this long (the previous failure is
    /// returned instead) unless the args materially differ or a player says "try again". 0 = off.
    /// </summary>
    public int FailureMemoryMinutes { get; set; } = 3;

    /// <summary>Directory for the SQLite db (mineclaw.db), usage.json and a legacy memory.json (imported once).</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Knowledge *.md files. With RAG on, only each file's core section (&lt;!-- core --&gt;...&lt;!-- /core --&gt;, else its first
    /// section) goes into the cached prompt; the rest is chunked by heading and retrieved. RAG off: everything in the prompt.
    /// </summary>
    public string? KnowledgeDirectory { get; set; } = "docs/knowledge";

    /// <summary>Cap on in-prompt knowledge text (chars, RAG off) so a big doc can't silently inflate every request.</summary>
    public int MaxKnowledgeChars { get; set; } = 40_000;

    /// <summary>
    /// The bot's personality: replaces the persona section of the system prompt (empty = <see cref="Prompts.DefaultPersona"/>).
    /// Core rules (short ASCII chat, tool use, void safety) still apply. Static per run: changing it busts the prompt cache.
    /// </summary>
    public string Persona { get; set; } = "";

    /// <summary>Extra context lines for the system prompt (e.g. who the humans are). Static: changing it busts the cache.</summary>
    public string PersonaExtra { get; set; } = "";

    /// <summary>Max model calls per episode before the turn is forcibly ended.</summary>
    public int MaxToolRounds { get; set; } = 8;

    /// <summary>How long a long-running tool chain may hold the model's turn before continuing in the background.</summary>
    public int InlineToolWaitSeconds { get; set; } = 20;

    /// <summary>Waking events at or above this priority cancel the current activity.</summary>
    public EventPriority InterruptPriority { get; set; } = EventPriority.High;

    /// <summary>Post an idle check-in after this many minutes with no episodes and no activity (0 = off).</summary>
    public int IdleCheckInMinutes { get; set; } = 20;

    /// <summary>Idle check-in interval while no human player is online (at least <see cref="IdleCheckInMinutes"/>; 0 = none
    /// then). Default hourly: an unattended hive mostly sleeps while its scripts and delegated loops run.</summary>
    public int IdleCheckInMinutesNoPlayers { get; set; } = 60;

    /// <summary>Cap (chars) on the per-turn <c>[since last turn]</c> digest line (0 = never include it).</summary>
    public int TurnDigestMaxChars { get; set; } = 500;

    /// <summary>Default max chars of a tool result the model sees.</summary>
    public int MaxToolResultChars { get; set; } = 400;

    /// <summary>
    /// Per-tool result caps (chars) overriding <see cref="MaxToolResultChars"/>. look/window may return up to 1200 chars
    /// (look only uses it with detail=true); delegate/ask_memory answers are held to their summary size.
    /// </summary>
    public Dictionary<string, int> ToolResultCaps { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["look"] = 1200,
        ["window"] = 1200,
    };

    /// <summary>
    /// "Agent:Brainstem": the deterministic controller under the brain (head turn, reflex commands, speculative first steps,
    /// social idle, the [here] pre-observation, non-blocking moves, streaming execution, simple-turn effort).
    /// </summary>
    public Brainstem.BrainstemOptions Brainstem { get; set; } = new();

    /// <summary>When paused by budget, reply to addressed chat with a canned notice at most this often.</summary>
    public int PausedNoticeMinutes { get; set; } = 15;
}

/// <summary>Heuristics for picking the tier before any model call.</summary>
public sealed class TriageOptions
{
    /// <summary>Chat containing one of these words (whole word, case-insensitive) goes straight to the main tier.</summary>
    public string[] ActionWords { get; set; } =
    [
        "mine", "dig", "build", "craft", "smelt", "cook", "fetch", "bring", "collect", "gather", "follow", "place",
        "plant", "farm", "chop", "kill", "deposit", "store", "expand", "extend", "routine", "teach", "bridge",
        "come here", "go to", "make me", "make a", "make some", "get me", "grab me", "get some", "go get",
    ];

    /// <summary>
    /// Use the cheap tier (with an escalate tool) for conversational batches without action words. Off by default: the
    /// main (Opus) tier is the brain and handles all player conversation.
    /// </summary>
    public bool CheapFirstForChat { get; set; } = false;

    /// <summary>Plain idle check-ins run on the cheap tier (with escalate) instead of the brain.</summary>
    public bool IdleCheckInsOnCheapTier { get; set; } = true;
}

/// <summary>
/// The worker/builder tier ("Agent:Worker"). The brain hands multi-step execution to it with <c>delegate</c>; the worker
/// runs its own short tool loop (separate small context, own cached prefix) and returns ONE terse summary.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Off = no delegate tool and the brain sees every tool (legacy single-brain layout).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Max model calls per delegated task.</summary>
    public int MaxRounds { get; set; } = 24;
    /// <summary>Time budget when the brain omits max_minutes, and the hard ceiling for it.</summary>
    public int DefaultMinutes { get; set; } = 5;
    public int MaxMinutes { get; set; } = 20;
    /// <summary>Max estimated spend (USD) per delegated task.</summary>
    public decimal MaxCostUsd { get; set; } = 0.40m;
    /// <summary>Worker context ceiling (estimated tokens). Older tool results are cut first; the task ends if that's not enough.</summary>
    public int MaxContextTokens { get; set; } = 24_000;
    /// <summary>The single summary the brain gets back (chars).</summary>
    public int SummaryChars { get; set; } = 300;
    /// <summary>Include the Skyblock core rules in the worker's cached system prompt.</summary>
    public bool IncludeCoreKnowledge { get; set; } = true;

    /// <summary>
    /// Tools the brain (main tier) calls directly: cheap single-shot ones. Tools in neither list go to the worker only.
    /// <c>delegate</c> and <c>ask_memory</c> are always brain-only.
    /// </summary>
    public string[] BrainTools { get; set; } =
    [
        // goto/follow/chop_tree/act/mine/... are reached through the compact `do` meta-tool (one ~200-token definition).
        "stop", "status", "inventory", "look", "where_is", "do", "run_routine",
        "recall_detail", "remember_waypoint", "mark_structure", "describe_structure", "remember_note", "forget", "list_memory",
        "identity", "reflect_self", "personality_admin",
        "ask_player", "ask_stand", "watch_demo",
    ];

    /// <summary>Tools the worker may call (game macro tools + lookups). Never chat/identity/self/player-facing tools.</summary>
    public string[] WorkerTools { get; set; } =
    [
        "status", "inventory", "look", "window", "goto", "mine", "harvest_generator", "collect_drops", "craft", "smelt",
        "collect_furnace", "deposit", "withdraw", "organize_chest", "place", "dig", "use_block", "eat",
        "where_is", "recall", "remember_waypoint", "save_routine", "run_routine", "chop_tree",
    ];
}

/// <summary>"Agent:AskMemory": the brain asks a question, the cheap tier searches the RAG index and distils an answer.</summary>
public sealed class AskMemoryOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Max cheap-tier calls per question (each may run several lookups).</summary>
    public int MaxRounds { get; set; } = 4;
    /// <summary>Answer length the brain sees (chars).</summary>
    public int AnswerChars { get; set; } = 300;
    public int MaxTokens { get; set; } = 300;
    /// <summary>Lookup tools the memory loop may use (must be registered).</summary>
    public string[] Tools { get; set; } = ["recall", "where_is", "recall_detail"];
}
