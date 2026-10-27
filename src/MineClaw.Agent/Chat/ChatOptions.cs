namespace MineClaw.Agent.Chat;

/// <summary>Chat parsing/addressing/rate-limit config. Arrays (not lists) so config binding replaces defaults.</summary>
public sealed class ChatOptions
{
    /// <summary>The bot's login username (whitelisted on the server). Its own chat lines are ignored.</summary>
    public string Username { get; set; } = "maid_chan";

    /// <summary>
    /// Default name the bot answers to in chat. Players can rename it at runtime (identity tool);
    /// the current identity is persisted in MemoryStore and overrides these defaults.
    /// </summary>
    public string DisplayName { get; set; } = "claudechan";

    /// <summary>Default extra names (case-insensitive, whole word; spaces also match "_"/"-"/nothing).</summary>
    public string[] Aliases { get; set; } = ["claude chan", "claude"];

    /// <summary>Command used to whisper ({0}=player, {1}=message). Vanilla b1.8.1: /tell.</summary>
    public string WhisperFormat { get; set; } = "/tell {0} {1}";

    /// <summary>Regexes with named groups <c>name</c> and <c>msg</c>, tried after § codes are stripped.</summary>
    public string[] WhisperPatterns { get; set; } =
    [
        @"^(?<name>[A-Za-z0-9_]{1,16}) whispers(?: to you)?:? (?<msg>.+)$",
        @"^\[(?<name>[A-Za-z0-9_]{1,16}) ?-> ?(?:me|you)\] ?(?<msg>.+)$",
        @"^From (?<name>[A-Za-z0-9_]{1,16}): ?(?<msg>.+)$",
    ];

    public string[] JoinPatterns { get; set; } = [@"^(?<name>[A-Za-z0-9_]{1,16}) (?:joined the game|has joined(?: the game)?)\.?$"];

    public string[] LeavePatterns { get; set; } = [@"^(?<name>[A-Za-z0-9_]{1,16}) (?:left the game|has left(?: the game)?)\.?$"];

    /// <summary>Public chat: vanilla <c>&lt;name&gt; msg</c>, Bukkit-style <c>[rank] name: msg</c> and <c>name » msg</c>.</summary>
    public string[] PublicPatterns { get; set; } =
    [
        @"^<(?<name>[^>]{1,40})> ?(?<msg>.*)$",
        @"^(?:\[[^\]]{0,24}\] ?)*(?<name>[A-Za-z0-9_]{1,16})(?: ?\[[^\]]{0,24}\])*: (?<msg>.+)$",
        @"^(?:\[[^\]]{0,24}\] ?)*(?<name>[A-Za-z0-9_]{1,16}) ?(?:»|>>) (?<msg>.+)$",
    ];

    /// <summary>Words that address everyone at once ("hey everyone"), honoured in small groups.</summary>
    public string[] GroupWords { get; set; } = ["everyone", "everybody", "guys", "y'all", "yall", "anyone", "anybody", "team", "all of you", "you all", "both of you"];

    /// <summary>
    /// Conversation continuity: a player's line within this long of the bot talking to them (or them talking to it) counts as
    /// addressed. Live 2026-10-27: Fox's "give me my bucket back" 20 s after the bot's question to him was ignored for 72 s.
    /// </summary>
    public int ReplyWindowSeconds { get; set; } = 90;

    /// <summary>
    /// How the bot and players call each human (login → names, case-insensitive). A line that starts by calling ANOTHER player
    /// ("robin look", "foxy, ...") is player-to-player chat; a bot line naming one ("master, ...") is spoken to them.
    /// </summary>
    public Dictionary<string, string[]> PlayerNicknames { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["poofimafox"] = ["fox", "foxy", "master"],
        ["kiwi"] = ["robin"],
    };

    /// <summary>Lines that are imperatives about the bot's items/tasks/hive ("give me my bucket", "stop", "you...") count as addressed.</summary>
    public bool ImperativesAddressed { get; set; } = true;

    /// <summary>The cheap "is this line for the bot?" classifier for unsure lines (one tiny Haiku call, regulars only).</summary>
    public AddressClassifierOptions Classifier { get; set; } = new();

    /// <summary>"everyone"-style messages count as addressed when at most this many humans are online.</summary>
    public int SmallGroupSize { get; set; } = 3;

    /// <summary>If only one human is online, everything they say is addressed to the bot.</summary>
    public bool SoloPlayerAlwaysAddressed { get; set; } = true;

    /// <summary>Anti-spam: max addressed messages per player per window; extra ones become background context.</summary>
    public int PerPlayerMaxMessages { get; set; } = 6;
    public int PerPlayerWindowSeconds { get; set; } = 30;

    /// <summary>
    /// Startup chatter cap: until a player talks to the bot (or <see cref="StartupWindowSeconds"/> pass), all unprompted chat
    /// together is at most this many lines.
    /// </summary>
    public int StartupMaxLines { get; set; } = 2;
    public int StartupWindowSeconds { get; set; } = 180;

    /// <summary>Outgoing: max lines per reply, and pause between lines.</summary>
    public int MaxLinesPerReply { get; set; } = 3;
    public int LineDelayMs { get; set; } = 800;
}

/// <summary>"Agent:Chat:Classifier": the cheap tie-breaker for lines that might be for the bot.</summary>
public sealed class AddressClassifierOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Only while the bot is this close to the speaker (blocks)...</summary>
    public double NearBlocks { get; set; } = 16;
    /// <summary>...or in an active thread with them (the bot talked with them within this many minutes).</summary>
    public int ThreadMinutes { get; set; } = 5;
    /// <summary>Hard cap on classifier calls per hour (cost guard).</summary>
    public int MaxPerHour { get; set; } = 30;
    /// <summary>Give up waiting for the classifier after this long (the line stays background context).</summary>
    public int TimeoutMs { get; set; } = 4000;
}
