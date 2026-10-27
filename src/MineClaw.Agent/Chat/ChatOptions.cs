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

    /// <summary>A player the bot just talked with is considered still talking to it for this long.</summary>
    public int ReplyWindowSeconds { get; set; } = 45;

    /// <summary>"everyone"-style messages count as addressed when at most this many humans are online.</summary>
    public int SmallGroupSize { get; set; } = 3;

    /// <summary>If only one human is online, everything they say is addressed to the bot.</summary>
    public bool SoloPlayerAlwaysAddressed { get; set; } = true;

    /// <summary>Anti-spam: max addressed messages per player per window; extra ones become background context.</summary>
    public int PerPlayerMaxMessages { get; set; } = 6;
    public int PerPlayerWindowSeconds { get; set; } = 30;

    /// <summary>Outgoing: max lines per reply, and pause between lines.</summary>
    public int MaxLinesPerReply { get; set; } = 3;
    public int LineDelayMs { get; set; } = 800;
}
