namespace MineClaw.Agent.Brainstem;

/// <summary>
/// "Agent:Brainstem": the small deterministic behaviour controller that always runs under the LLM brain. It reacts in
/// C# with zero tokens — turns the head to whoever talks to the bot, runs unambiguous reflex commands ("stop", "come
/// here", "follow me", "jump", "look at me", "give me 5 cobble"), starts the obvious first step of a clear job (walk toward
/// the gen / tree / player / place) while the brain plans, and fills idle time with social behaviour. Every action goes
/// through the normal gated tools, so the client's safety (EdgeGuard, survival, combat) still applies underneath.
/// </summary>
public sealed class BrainstemOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Turn the head toward the speaker on the next tick when addressed chat arrives (before debounce or the LLM).</summary>
    public bool HeadTurn { get; set; } = true;

    /// <summary>Also a single arm swing as a "nod" with the head turn (idle body only).</summary>
    public bool Nod { get; set; } = true;

    /// <summary>
    /// Reflex commands executed in C# on an exact (conservative) match: any of stop, come, follow, gesture, look, give.
    /// Remove an entry to hand that phrase to the brain.
    /// </summary>
    public string[] Reflexes { get; set; } = ["stop", "come", "follow", "gesture", "look", "give"];

    /// <summary>Only these players' give/drop requests run as a reflex (empty = trusted + regular players from Agent:Self).</summary>
    public string[] GiveFrom { get; set; } = [];

    /// <summary>
    /// The chat a reflex handled still wakes the brain (it sees "[reflex] ..." and may comment, or stay silent). Off = a
    /// reflex-only line never costs a model call.
    /// </summary>
    public bool ReflexWakesBrain { get; set; } = true;

    /// <summary>Start the obvious first step (walk/look only) of a clear job while the brain plans; the brain adopts or cancels it.</summary>
    public bool SpeculativeStart { get; set; } = true;

    /// <summary>A brain move to within this many blocks of the speculative target adopts the walk (no restart).</summary>
    public double AdoptWithinBlocks { get; set; } = 3;

    /// <summary>Within this many blocks it is a retarget (the walk is replaced, progress kept); farther = cancelled.</summary>
    public double RetargetWithinBlocks { get; set; } = 10;

    /// <summary>Speculative walks never target anything farther than this.</summary>
    public double MaxSpeculativeDistance { get; set; } = 48;

    /// <summary>Social idle behaviour (head tracking, glances, looking at pointed blocks, thinking fidget). All C#.</summary>
    public SocialOptions Social { get; set; } = new();

    /// <summary>Pre-observation "[here]" block on chat-triggered turns (position landmark, inventory, speaker, nearby builds).</summary>
    public bool HereBlock { get; set; } = true;

    /// <summary>Cap on the [here] block (estimated tokens).</summary>
    public int HereMaxTokens { get; set; } = 250;

    /// <summary>
    /// Effort for chat turns whose intent is simple (a reflex, one clear job, small talk): "" = the tier's configured effort.
    /// Claude Opus 5.5 can't turn thinking off, so effort is the lever. Applies to the whole turn (an effort change resets the
    /// history cache, so it never flips mid-turn).
    /// </summary>
    public string SimpleTurnEffort { get; set; } = "low";

    /// <summary>Thinking override for simple turns ("" = the tier's; "off" → between_tools on Sonnet 5.5, omitted on Opus 5.5).</summary>
    public string SimpleTurnThinking { get; set; } = "";

    /// <summary>Brain goto/follow returns at once ("moving to X, ~6s") and runs in the background; jobs queue behind it.</summary>
    public bool NonBlockingMoves { get; set; } = true;

    /// <summary>How long a non-blocking move is awaited inline (catches an instant "no path" before answering).</summary>
    public int MoveInlineWaitMs { get; set; } = 1500;

    /// <summary>Inline wait for other jobs (craft, withdraw, deposit): long enough for a quick one to answer in the same
    /// turn, short enough that a slow one never holds the brain (was Agent:InlineToolWaitSeconds = 20 s).</summary>
    public int JobInlineWaitMs { get; set; } = 3000;

    /// <summary>Jobs known to run long: they answer "running in background" after <see cref="MoveInlineWaitMs"/>.</summary>
    public string[] LongJobTools { get; set; } =
        ["chop_tree", "delegate", "harvest_generator", "mine", "script", "follow", "run_routine", "skill", "smelt", "collect_drops"];

    /// <summary>Start a streamed tool_use (movement or read-only) the moment its block completes, before the message ends.</summary>
    public bool StreamingExecution { get; set; } = true;
}

/// <summary>"Agent:Brainstem:Social": cheap, void-safe social idle behaviour.</summary>
public sealed class SocialOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Head-track whoever is talking (or, failing that, the nearest player) while idle.</summary>
    public bool HeadTracking { get; set; } = true;
    /// <summary>Glance at a player who comes within <see cref="GlanceRadius"/>.</summary>
    public bool Glance { get; set; } = true;
    public double GlanceRadius { get; set; } = 6;
    /// <summary>Look at the block a player just pointed at (punched).</summary>
    public bool LookAtPointed { get; set; } = true;
    /// <summary>
    /// When the conversation partner walks away mid-conversation, follow loosely: idle body only (a job always outranks
    /// social behaviour, so this is off during jobs), safe ground only, plain walk without parkour or building.
    /// </summary>
    public bool LooseFollow { get; set; } = true;
    /// <summary>Loose follow starts beyond this distance and stops within <see cref="FollowStopBlocks"/>.</summary>
    public double FollowStartBlocks { get; set; } = 7;
    public double FollowStopBlocks { get; set; } = 4;
    /// <summary>Never loose-follow a partner farther than this (they left; that's not a conversation any more).</summary>
    public double FollowMaxBlocks { get; set; } = 24;
    /// <summary>A conversation lasts this long after the partner's last line.</summary>
    public int ConversationSeconds { get; set; } = 60;
    /// <summary>A small "thinking" look-around / swing once the brain's turn has run this long with the body idle (0 = off).</summary>
    public int ThinkingFidgetAfterMs { get; set; } = 3000;
    /// <summary>Social tick period.</summary>
    public int TickMs { get; set; } = 250;
}
