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

    /// <summary>"Agent:Brainstem:Reply": two-phase replies (streamed brain chat, quick cheap-tier ack, cheap-only small talk).</summary>
    public ReplyOptions Reply { get; set; } = new();

    /// <summary>"Agent:Brainstem:Character": act first, think in parallel: prefetched in-character lines said instantly, typing timing.</summary>
    public CharacterOptions Character { get; set; } = new();
}

/// <summary>
/// "Agent:Brainstem:Character" (docs/design/reactive-character.md). The body reacts at once (gaze, gesture, reflex walk) and,
/// when a fitting prefetched line is ready, says it after a human typing delay; the brain's turn sees "[already said]" and
/// "[already doing]" and only amends. Lines are written in the background by one cheap batched call (memory tier: wording, not
/// scripts) for the moments likely in the current context, tagged with that context and dropped when it changes.
/// </summary>
public sealed class CharacterOptions
{
    /// <summary>Say a prefetched line instantly for reflexes, speculative starts and small talk.</summary>
    public bool InstantLines { get; set; } = true;

    /// <summary>Small talk (greeting, thanks, praise, status) answered by an instant line needs no model call at all.</summary>
    public bool InstantSmallTalkIsFinal { get; set; } = true;

    /// <summary>Background line prefetch (one cheap call per context, at most <see cref="MaxPrefetchPerHour"/>).</summary>
    public bool Prefetch { get; set; } = true;
    public int MaxPrefetchPerHour { get; set; } = 12;
    /// <summary>Lines per moment a prefetch asks for.</summary>
    public int LinesPerMoment { get; set; } = 3;
    /// <summary>Check the bank this often (idle or during long jobs); a call only when the context changed or moments ran low.</summary>
    public int RefreshSeconds { get; set; } = 30;
    /// <summary>A prepared line older than this is dropped even if the context didn't change.</summary>
    public int LineMaxAgeMinutes { get; set; } = 20;
    /// <summary>No line (prefetched or the brain's own) repeats within this many minutes (phrase memory).</summary>
    public int RepeatMinutes { get; set; } = 30;
    public int MaxLineChars { get; set; } = 90;
    public int MaxTokens { get; set; } = 700;

    /// <summary>Typing delay before an instant line: think + per char, jittered ±20 %, capped (ms).</summary>
    public int ThinkMs { get; set; } = 160;
    public int PerCharMs { get; set; } = 22;
    public int MaxTypingMs { get; set; } = 900;

    /// <summary>The moments a prefetch prepares (greet/bye are per online player; the rest generic).</summary>
    public string[] Moments { get; set; } = ["greet", "bye", "thanks", "praise", "tease", "status", "hurt", "come", "follow", "stop", "onit", "sorry"];
}

/// <summary>
/// "Agent:Brainstem:Reply": respond quickly, then conclusively. (1) Each completed chat line the brain streams is sent the
/// moment it finishes, through the usual filters. (2) If no brain line is out <see cref="AckAfterMs"/> after an addressed
/// message (and no reflex handled it), one Haiku call writes a ≤5-word non-committal ack ("on it~", "which chest?"),
/// linted (no digits, coordinates, counts, yes/no or claims; else silence). (3) Safe small talk (greetings, thanks,
/// emotes, "how are you", "what are you doing") is triaged by the same single cheap call: a final reply without any Opus
/// call when it needs no brain. A correction right after a cheap-only reply ("no, I meant...") escalates and counts a miss.
/// </summary>
public sealed class ReplyOptions
{
    /// <summary>Send each completed streamed chat line of the brain at once (sentence/line boundaries).</summary>
    public bool StreamChat { get; set; } = true;

    /// <summary>After the first streamed line of a round, a sentence end flushes only once this many chars are buffered
    /// (keeps chat from splitting into tiny lines); a line/block end always flushes.</summary>
    public int StreamMinChars { get; set; } = 60;

    /// <summary>Quick cheap-tier acknowledgement when the brain is slow to say anything.</summary>
    public bool QuickAck { get; set; } = true;

    /// <summary>Ack when no brain line streamed this long after the addressed message was queued.</summary>
    public int AckAfterMs { get; set; } = 2000;

    /// <summary>The ack call starts this much earlier (Haiku latency), so the line can land at <see cref="AckAfterMs"/>.</summary>
    public int AckCallLeadMs { get; set; } = 900;

    /// <summary>Ack hard caps (words, chars).</summary>
    public int AckMaxWords { get; set; } = 5;
    public int AckMaxChars { get; set; } = 30;

    /// <summary>No ack for plain small talk that goes to the brain anyway (a low-effort brain answers it fast).</summary>
    public bool AckSkipSmallTalk { get; set; } = true;

    /// <summary>
    /// The quick ack runs only for lines the router is at least this sure are for the bot (named/whispered 1.0, gaze/answer
    /// 0.9, continuity 0.8; an unnamed imperative 0.5). Live 10-27: Robin asked robinchan "you have to respond to everything
    /// currently?" and main acked "hm, lemme think~".
    /// </summary>
    public double AckMinConfidence { get; set; } = 0.75;

    /// <summary>Safe small talk is answered by the cheap tier alone (no Opus call) when its one call says no brain is needed.</summary>
    public bool CheapOnly { get; set; } = true;

    /// <summary>Cap on a cheap-only final reply (chars).</summary>
    public int CheapMaxChars { get; set; } = 120;

    /// <summary>max_tokens of the quick call.</summary>
    public int MaxTokens { get; set; } = 60;

    /// <summary>A correction from the same player this soon after a cheap-only reply escalates (and counts a miss).</summary>
    public int CorrectionWindowSeconds { get; set; } = 120;
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
