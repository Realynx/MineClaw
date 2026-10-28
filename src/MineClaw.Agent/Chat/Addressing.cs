using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>Why a player line was (or wasn't) taken as addressed to the bot.</summary>
public enum AddressReason
{
    No,
    Name,
    Whisper,
    /// <summary>Within the reply window of the bot talking with this player (either direction).</summary>
    Continuity,
    Solo,
    Group,
    /// <summary>An imperative about the bot's items/tasks/hive, or "you/your" ("give me my bucket back", "stop", "come").</summary>
    Imperative,
    /// <summary>Obvious player-to-player chat: it calls another online human ("bob look", "robin, come").</summary>
    OtherPlayer,
    /// <summary>The speaker is looking at the bot (or standing right next to it and nobody else).</summary>
    Gaze,
    /// <summary>A reply to a question the bot just asked this player.</summary>
    AnswerToUs,
    /// <summary>"I'm not talking to you" (or a later unnamed line inside that do-not-respond window).</summary>
    Rejected,
    /// <summary>The player is talking with someone else right now (looking at them, or that one answered them last).</summary>
    OtherConversation,
    /// <summary>Unclear, and another bot is in the conversation: stay out of it.</summary>
    Unclear,
    /// <summary>A line from another (non-hive) bot: not naming us, a reply to us, or over the one-reply-per-5-min limit.</summary>
    FromBot,
    /// <summary>The cheap classifier said yes.</summary>
    Classifier,
}

public static class AddressReasonExtensions
{
    public static bool IsAddressed(this AddressReason r) =>
        r is not (AddressReason.No or AddressReason.OtherPlayer or AddressReason.Rejected or AddressReason.OtherConversation
            or AddressReason.Unclear or AddressReason.FromBot);

    /// <summary>
    /// How sure the router is that the line is for the bot (1 = named/whispered). The quick ack runs only at or above
    /// <see cref="Brainstem.ReplyOptions.AckMinConfidence"/>: never on a line that might be someone else's.
    /// </summary>
    public static double Confidence(this AddressReason r) => r switch
    {
        AddressReason.Name or AddressReason.Whisper => 1.0,
        AddressReason.Gaze or AddressReason.AnswerToUs => 0.9,
        AddressReason.Continuity => 0.8,
        AddressReason.Group or AddressReason.Solo => 0.7,
        AddressReason.Classifier => 0.6,
        AddressReason.Imperative => 0.5,
        _ => 0,
    };
}

/// <summary>Addressing telemetry (logged with the chat-flow line; misses are what the classifier and continuity are for).</summary>
public sealed class AddressingStats
{
    public long Continuity;
    public long Imperatives;
    public long ClassifierCalls;
    public long ClassifierYes;
    /// <summary>A line we ignored that the player then repeated by name within the reply window.</summary>
    public long Misses;

    public override string ToString() =>
        $"addressing: continuity {Continuity}, imperatives {Imperatives}, classifier {ClassifierYes}/{ClassifierCalls} yes, misses {Misses}";
}

/// <summary>Deterministic text tests behind <see cref="ChatRouter.ReasonFor"/>.</summary>
public static class AddressingText
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static string Word(string name) =>
        @"(?<![A-Za-z0-9_])@?" + string.Join(@"[\s_-]*", name.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)) + @"(?![A-Za-z0-9_])";

    /// <summary>The name appears as a whole word.</summary>
    public static bool Mentions(string text, string name) =>
        !string.IsNullOrWhiteSpace(name) && Regex.IsMatch(text ?? "", Word(name), O);

    /// <summary>The name is used to call someone: line start ("robin look", "hey fox,"), "name," anywhere, or a trailing ", name".</summary>
    public static bool Vocative(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var w = Word(name);
        var t = text ?? "";
        return Regex.IsMatch(t, @"^\W*(?:(?:hey|yo|hi|oi|ok|okay|omg|lol)\W+)?" + w, O)
            || Regex.IsMatch(t, w + @"\s*,", O)
            || Regex.IsMatch(t, @",\s*" + w + @"\W*$", O);
    }

    private static readonly Regex MentionLead = new(
        @"(?:\b(?:to|with|for|at|tell|help|follow|give|than|and|from|like|about|of|by|ask|listen|and|or|is|was|me|mine)\s+)$", O);

    /// <summary>
    /// The login is used to talk to that player, not merely mentioned: "bob look at this", "bob you there?" call Bob; "go listen
    /// to poofimafox" / "follow kiwi" only mention them (the line is for the bot).
    /// </summary>
    public static bool CallsLogin(string text, string login)
    {
        if (string.IsNullOrWhiteSpace(login)) return false;
        var t = text ?? "";
        foreach (Match m in Regex.Matches(t, Word(login), O))
            if (!MentionLead.IsMatch(t[..m.Index])) return true;
        return false;
    }

    private static readonly Regex Imperative = new(
        @"^\W*(?:(?:ok(?:ay)?|now|and|also|pls|plz|please|just|then|so|hey|yo|oi|can\s+(?:you|u)|could\s+(?:you|u)|will\s+(?:you|u))\W+)*" +
        @"(?:give|giv|gimme|bring|drop|toss|throw|hand|return|stop|halt|wait|come|follow|go|get|grab|fetch|craft|make|mine|chop|cut|" +
        @"farm|harvest|replant|plant|build|place|put|fill|dig|finish|continue|keep|start|help|check|look|watch|deposit|store|" +
        @"withdraw|take|use|equip|eat|spawn|recall|send|have|tell|show|clear|fix|guard|jump|crouch|sneak|turn|stay|quit|leave|" +
        @"pick|sort|empty|open|close|light|smelt|cook|collect|gather|work|move|hurry|stand|sit|listen|remember|forget|cancel)\b", O);

    private static readonly Regex YouWords = new(@"\b(?:you|your|yours|yourself|you'?re|youre|u|ur|ya)\b", O);
    private static readonly Regex HiveWords = new(@"\b(?:clones?|hive|maids?|bots?)\b", O);
    private static readonly Regex GiveBack = new(@"\b(?:give|giv|gimme|hand|toss)\b.*\b(?:me|back)\b|\bmy\s+\w+\s+back\b", O);

    /// <summary>
    /// A line plausibly for the bot without its name: an imperative ("give me my bucket back", "stop", "come", "finish the farm"),
    /// "you/your", or the hive ("clone 69", "the maids"). Callers first rule out lines that call another player.
    /// </summary>
    public static bool IsBotDirected(string text)
    {
        var t = text ?? "";
        return Imperative.IsMatch(t) || YouWords.IsMatch(t) || HiveWords.IsMatch(t) || GiveBack.IsMatch(t);
    }
}
