using MineClaw.Agent.Budget;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent;

/// <summary>All agent configuration; bind from appsettings section "Agent".</summary>
public sealed class AgentOptions
{
    public LlmOptions Llm { get; set; } = new();
    public BudgetOptions Budget { get; set; } = new();
    public PriceTable Prices { get; set; } = new();
    public ChatOptions Chat { get; set; } = new();
    public EventQueueOptions Events { get; set; } = new();
    public ContextOptions Context { get; set; } = new();
    public TriageOptions Triage { get; set; } = new();

    /// <summary>Directory for memory.json and usage.json.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Every *.md here is appended to the cached system prompt at startup (null/missing = none).</summary>
    public string? KnowledgeDirectory { get; set; } = "docs/knowledge";

    /// <summary>Cap on knowledge text (chars) so a big doc can't silently inflate every request.</summary>
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

    /// <summary>Max chars of any tool result the model sees.</summary>
    public int MaxToolResultChars { get; set; } = 400;

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

    /// <summary>Use the cheap tier (with an escalate tool) for conversational batches without action words.</summary>
    public bool CheapFirstForChat { get; set; } = true;
}
