namespace MineClaw.Agent.Brainstem;

/// <summary>
/// "Agent:Gestures": body language driven by static text matching in the brainstem (zero tokens, no model call). The bot's
/// own chat and /me emotes play a matching gesture (agree → nod, refuse/sorry → shake, hello → wave + look at them, thanks →
/// bow, excitement → crouch-bob/hop); a player's addressed line turns the head to them; a player's yes/no to the bot's own
/// question gets a small nod. Lowest, social priority: never in danger, a fight, off the ground or within
/// <see cref="VoidMargin"/> of the void; sneak/hop only on an idle body; at most one gesture per <see cref="MinIntervalMs"/>.
/// </summary>
public sealed class GestureOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>At most one expressive gesture per this many ms (look-at-the-speaker has its own per-player interval).</summary>
    public int MinIntervalMs { get; set; } = 3000;

    /// <summary>Hive clones gesture too (off = only the main bot).</summary>
    public bool Clones { get; set; } = true;

    /// <summary>Logins (main or clone) that never gesture, e.g. ["maid_chan_420"].</summary>
    public string[] DisabledFor { get; set; } = [];

    /// <summary>The bot's own chat lines are classified (agree/refuse/greet/thanks/excited).</summary>
    public bool OwnChat { get; set; } = true;

    /// <summary>The bot's own "/me bows" / "*waves*" play that gesture.</summary>
    public bool Emotes { get; set; } = true;

    /// <summary>Addressed chat turns the head to the speaker (an overlay, so it works while walking too).</summary>
    public bool LookAtSpeaker { get; set; } = true;

    /// <summary>Per-player interval for <see cref="LookAtSpeaker"/>.</summary>
    public int LookAtIntervalMs { get; set; } = 3000;

    /// <summary>A player's yes/no within this many seconds of the bot's own question gets a small nod.</summary>
    public int AnswerNodSeconds { get; set; } = 60;

    /// <summary>No gesture within this many blocks of a deadly edge (the void, a long drop).</summary>
    public int VoidMargin { get; set; } = 2;

    /// <summary>No gesture with a hostile mob this close, or this soon after a danger event (s).</summary>
    public double DangerRadius { get; set; } = 8;
    public int DangerQuietSeconds { get; set; } = 5;

    /// <summary>No gesture at or below this health.</summary>
    public int MinHealth { get; set; } = 6;

    /// <summary>
    /// Extra rules, checked before the defaults (first match wins): {Pattern (regex, case-insensitive), Gesture
    /// (nod|shake|wave|greet|bow|bob|hop|excite|look|none), Scope (start|head|any|emote)}. "none" silences a phrase.
    /// </summary>
    public List<GestureRule> Table { get; set; } = [];

    /// <summary>Use only <see cref="Table"/> (no built-in rules).</summary>
    public bool ReplaceDefaults { get; set; }
}

/// <summary>One row of the gesture table.</summary>
public sealed class GestureRule
{
    public string Pattern { get; set; } = "";
    public string Gesture { get; set; } = "";
    /// <summary>start = the line's opening (after a "Name," vocative); head = its first sentence; any = anywhere; emote = a /me action.</summary>
    public string Scope { get; set; } = "start";

    public GestureRule() { }
    public GestureRule(string scope, string pattern, string gesture) { Scope = scope; Pattern = pattern; Gesture = gesture; }
}
