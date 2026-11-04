using System.Text.RegularExpressions;
using MineClaw.Agent.Chat;

namespace MineClaw.Agent.Brainstem;

/// <summary>
/// The kind of a player's line for latency telemetry (<see cref="Category"/>) and for reaction triggers (<see cref="Intent"/>).
/// Categories: <c>help</c> (a call for help: "help!", "maid save me"), <c>command</c> (asks the bot to act), <c>question</c>,
/// <c>greeting</c> (hello/bye/welcome back) and <c>banter</c> (everything social: thanks, praise, jokes, emotes, insults).
/// </summary>
public sealed record ChatClass(string Intent, string Category)
{
    public static readonly ChatClass Banter = new("banter", ChatIntent.Banter);
}

/// <summary>
/// Deterministic, zero-token classification of one chat line (C#). Conservative where it matters: a reflex/speculative command
/// shape is a command; small talk only when <see cref="QuickReply.Classify"/> agrees. Intents (the reaction-library trigger
/// names): come, follow, stop, give, gesture, look, status, thanks, praise, insult, greeting, farewell, help, question,
/// command, banter.
/// </summary>
public static class ChatIntent
{
    public const string Command = "command", Question = "question", Banter = "banter", Greeting = "greeting", Help = "help";

    /// <summary>Every category, in report order.</summary>
    public static readonly string[] Categories = [Command, Question, Banter, Greeting, Help];

    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly Regex HelpExtra = new(@"\bhelp\s+me\b.*\b(?:zombies?|skeletons?|creepers?|spiders?|mobs?|dying|falling|lava|fire|void)\b|\b(?:zombies?|skeletons?|creepers?|spiders?|mobs?)\b.{0,20}\b(?:on|attacking|chasing|killing|after)\s+me\b", O);
    private static readonly Regex Hello = new(@"^(?:h+i+|hello+|hey+a?|heya|hiya|yo+|sup|howdy|gm|good (?:morning|evening|afternoon|day)|o/|\\o|wb|welcome back|i'?m back|im back|back)$", O);
    private static readonly Regex Bye = new(@"^(?:bye+|bye bye|cya|see (?:ya|you)(?: later| soon| tomorrow)?|later|brb|gn|g'?night|good ?night|nighty(?: night)?|gtg|g2g|logging off)$", O);
    private static readonly Regex Thanks = new(@"\b(?:thanks?|thx|ty|tysm|thank (?:you|u))\b", O);
    private static readonly Regex Praise = new(@"\b(?:good (?:girl|job|work|bot|maid|one)|nice(?: one| job| work)?|well done|great (?:job|work)|amazing|awesome|cute|adorable|the best|best maid|love (?:you|u|ya)|ily|<3)\b", O);
    private static readonly Regex Insult = new(@"\b(?:stupid|dumb|idiot|useless|trash|garbage|shut up|stfu|annoying|bad (?:bot|maid|girl)|you suck|u suck|hate (?:you|u)|worst)\b", O);
    private static readonly Regex Status = new(@"^(?:what (?:are|r) (?:you|u) (?:doing|up to)|wyd|what'?s up|whats up|how (?:are|r) (?:you|u)(?: doing)?|hru|(?:are )?(?:you|u) ok(?:ay)?|where (?:are|r) (?:you|u))\??$", O);
    private static readonly Regex QuestionStart = new(@"^(?:how|why|what|where|when|who|which|is|are|was|were|do|does|did|can|could|would|will|should|have|has|any)\b", O);
    private static readonly Regex Imperative = new(@"^(?:(?:can|could|would|will) (?:you|u) (?:please |pls )?)?(?:please |pls )?(?:go|come|get|grab|bring|fetch|give|drop|toss|mine|dig|chop|cut|build|craft|make|place|put|take|move|follow|stop|wait|look|help me|open|close|plant|harvest|farm|smelt|cook|eat|kill|attack|deposit|store|withdraw|collect|gather|clear|fill|light|expand|extend|finish|start|keep|try|run|use|fix|sort|organi[sz]e|turn|jump|spin|wave|crouch|sneak|hop|guard|defend|clean)\b", O);

    /// <summary>The intent and category of one line (the bot's own names are stripped first).</summary>
    public static ChatClass Classify(string? text, IEnumerable<string>? names = null, bool emote = false)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return ChatClass.Banter;
        var nameList = (names ?? []).ToList();
        if (AddressingText.IsHelpCall(raw) || HelpExtra.IsMatch(raw)) return new("help", Help);
        var reflex = IntentMatcher.Reflex(raw, nameList, _ => true);
        if (reflex.IsReflex) return new(ReflexIntent(reflex.Kind), Command);
        var norm = Normalized(raw, nameList);
        if (emote) return EmoteClass(norm);
        return SocialClass(raw, norm, nameList) ?? RequestClass(raw, norm, nameList);
    }

    private static ChatClass EmoteClass(string norm) => new(Insult.IsMatch(norm) ? "insult" : Praise.IsMatch(norm) ? "praise" : "banter", Banter);

    private static string ReflexIntent(IntentKind kind) => kind switch
    {
        IntentKind.Stop => "stop", IntentKind.Come => "come", IntentKind.Follow => "follow", IntentKind.Give => "give",
        IntentKind.LookAtMe => "look", _ => "gesture",
    };

    /// <summary>The line without the bot's names, filler and a trailing "everyone" ("gn everyone").</summary>
    private static string Normalized(string raw, List<string> nameList)
    {
        var norm = IntentMatcher.Normalize(raw, nameList);
        foreach (var n in nameList.Where(n => !string.IsNullOrWhiteSpace(n)))
            norm = Regex.Replace(norm, $@"\b{Regex.Escape(n.Trim().ToLowerInvariant())}\b", " ", O);
        norm = Regex.Replace(norm, @"\s+", " ").Trim(' ', ',', '!', '.', '~', '@');
        return Regex.Replace(norm, @"[\s,]+(?:everyone|everybody|all|guys|y'?all|chat|folks|peeps)$", "", O).Trim(); // "gn everyone"
    }

    /// <summary>A status question, a clear job, a greeting, a farewell or an insult; else null.</summary>
    private static ChatClass? SocialClass(string raw, string norm, List<string> nameList)
    {
        if (Status.IsMatch(norm)) return new("status", Question);
        var spec = IntentMatcher.Speculative(raw, nameList);
        if (spec.IsSpeculative) return new(spec.Kind == IntentKind.SpecToSpeaker ? "come" : "command", Command);
        if (Hello.IsMatch(norm)) return new("greeting", Greeting);
        if (Bye.IsMatch(norm)) return new("farewell", Greeting);
        if (Insult.IsMatch(norm)) return new("insult", Banter);
        return null;
    }

    /// <summary>A command, a question, thanks/praise, small talk or (anything else) a command.</summary>
    private static ChatClass RequestClass(string raw, string norm, List<string> nameList)
    {
        bool asks = raw.TrimEnd().EndsWith('?') || QuestionStart.IsMatch(norm);
        bool imperative = Imperative.IsMatch(norm);
        // "can you grab cobble?" is a command phrased as a question.
        if (imperative && (!asks || Regex.IsMatch(norm, @"^(?:can|could|would|will) (?:you|u)\b", O))) return new("command", Command);
        if (asks) return new("question", Question);
        return TalkClass(raw, norm, nameList);
    }

    /// <summary>Thanks, praise, small talk, or a simple line without "you should..." is banter; anything else a command.</summary>
    private static ChatClass TalkClass(string raw, string norm, List<string> nameList)
    {
        if (Thanks.IsMatch(norm)) return new("thanks", Banter);
        if (Praise.IsMatch(norm)) return new("praise", Banter);
        var quick = QuickReply.Classify([raw], nameList);
        if (quick == QuickKind.SmallTalk) return new("banter", Banter);
        return IntentMatcher.IsSimple([raw], nameList) && !Regex.IsMatch(norm, @"\b(?:you|u)\b.*\b(?:should|need|must|have to)\b", O)
            ? new("banter", Banter) : new("command", Command);
    }

    /// <summary>The category of a batch of lines: the most urgent one wins (help &gt; command &gt; question &gt; greeting &gt; banter).</summary>
    public static string CategoryOf(IEnumerable<string> lines, IEnumerable<string>? names = null)
    {
        var cats = lines.Select(l => Classify(l, names).Category).ToList();
        foreach (var c in new[] { Help, Command, Question, Greeting }) if (cats.Contains(c)) return c;
        return Banter;
    }
}
