using System.Text.RegularExpressions;

namespace MineClaw.Agent.Rules;

/// <summary>Where a standing rule applies: one design, one place, one activity (building, farming...) or everywhere.</summary>
public enum DirectiveScopeKind { General, Design, Place, Activity }

public sealed record DirectiveScope(DirectiveScopeKind Kind, string? Value)
{
    public static readonly DirectiveScope General = new(DirectiveScopeKind.General, null);

    /// <summary>"design:4", "place:3,64,5", "activity:build", "general".</summary>
    public string Key => Kind == DirectiveScopeKind.General ? "general" : $"{Kind.ToString().ToLowerInvariant()}:{Value}";

    /// <summary>"design #4", "at 3,64,5", "building", "general".</summary>
    public string Describe() => Kind switch
    {
        DirectiveScopeKind.Design => $"design #{Value}",
        DirectiveScopeKind.Place => $"at {Value}",
        DirectiveScopeKind.Activity => DirectiveParser.TopicName(Value ?? ""),
        _ => "general",
    };

    public static DirectiveScope Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key == "general") return General;
        int c = key.IndexOf(':');
        if (c <= 0) return General;
        return Enum.TryParse<DirectiveScopeKind>(key[..c], ignoreCase: true, out var k) ? new(k, key[(c + 1)..]) : General;
    }
}

/// <summary>
/// Zero-token reading of a trusted player's line as a directive (an instruction, rule or preference for the bots) rather than a
/// world fact: rule markers ("only build in daytime", "never till grass", "stay quiet unless spoken to", "at night, mine cobble
/// instead", "kiwi wants maid-chan to call her robin-sama"), its scope (design #4, a place, an activity) and its topics.
/// Conservative: a plain fact ("there's iron in that chest", "Fox's strat: ... jump into the void") is not an instruction.
/// </summary>
public static partial class DirectiveParser
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Things the bots do (the verb after "never/always/don't/only").</summary>
    private const string Acts =
        @"(?:till|break|dig|mine|build|place|take|touch|use|go|jump|enter|kill|attack|hit|say|talk|chat|speak|open|eat|craft|burn|light|" +
        @"leave|give|drop|throw|sell|trade|chop|cut|harvest|plant|replant|move|swap|empty|store|put|fight|farm|grind|wander|follow|" +
        @"spawn|recall|call|answer|reply|ask|mention|stand|walk|fly|swim|log\s+off|stay|wait|work|smelt|cook|fish|hunt|explore|bring)";

    private static readonly Regex[] Markers =
    [
        new(@"\b(?:rules?|standing\s+orders?|policy|instructions?)\b", O),
        new(@"\bonly\s+(?:ever\s+)?(?:in|when|at|during|if|after|before|while|on|with|by\s+day|by\s+night|" + Acts + @")\b", O),
        new(@"\b(?:never|always|don'?t|do\s+not|dont|no\s+more|stop|avoid|quit)\s+(?:ever\s+)?(?:\w+ing\s+)?" + Acts + @"\w*\b", O),
        new(@"\b(?:never|always)\s+ever\b", O),
        new(@"\b(?:must|should|have\s+to|need\s+to|make\s+sure|be\s+sure\s+to)\s+(?:always\s+|never\s+|not\s+)?" + Acts + @"\w*\b", O),
        new(@"\b(?:from\s+now\s+on|instead|not\s+allowed|isn'?t\s+allowed|aren'?t\s+allowed|forbid\w*|permission|off[-\s]limits)\b", O),
        new(@"\b(?:wants?|wanted|asked|asks|told|tells|prefers?|likes?)\s+(?:me|us|you|the\s+bots?|bots|clones?|all\s+clones|maid[-_\s]?chan\w*|\d{2,4})\s+to\b", O),
        new(@"\b(?:ok'?d|okayed|okay'?d|approved|allowed|agreed)\b[^.!?]*\bbut\b", O),
        new(@"\b(?:stay|keep|be)\s+(?:quiet|silent|away|out|off|close|near|inside|home)\b|\bunless\s+(?:addressed|spoken|asked|told|called)\b", O),
        new(@"\bcall\s+(?:me|her|him|them)\b|\bapplies\s+to\b|\bapply\s+to\s+(?:me|us|all)\b", O),
    ];

    /// <summary>A rule being lifted or loosened ("you can build at night now", "forget the daytime rule", "no longer").</summary>
    [GeneratedRegex(
        @"\b(?:can|may|could|are\s+allowed\s+to|it'?s\s+(?:ok|okay|fine|safe)\s+to|ok(?:ay)?\s+to|fine\s+to|feel\s+free\s+to|go\s+ahead\s+and)\b[^.!?]*\b(?:now|again|anymore|after\s+all|from\s+now\s+on)\b" +
        @"|\bno\s+longer\b|\banymore\b|\bnever\s*mind\b|\bnvm\b" +
        @"|\b(?:forget|drop|cancel|scrap|lift|ignore|undo|revoke)\s+(?:the|that|my|this|his|her|their|fox'?s|robin'?s)\s+(?:[\w'-]+\s+){0,3}(?:rule|order|instruction)s?\b" +
        @"|\b(?:rule|order|instruction)s?\s+(?:is\s+|are\s+)?(?:off|over|lifted|cancell?ed|gone|void)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RevokeRx();

    /// <summary>Never stored, whoever says it.</summary>
    [GeneratedRegex(@"\b(?:api[\s_-]?keys?|passwords?|credentials?|secret\s+keys?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretRx();

    [GeneratedRegex(@"\b(?:design|blueprint|build|tower|plan)\s*(?:id\s*)?#\s*(\d{1,6})\b|\b(?:design|blueprint)\s+(\d{1,6})\b|\(#(\d{1,6})\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DesignRx();

    [GeneratedRegex(@"(-?\d{1,6})\s*,\s*(-?\d{1,3})\s*,\s*(-?\d{1,6})")]
    private static partial Regex CoordsRx();

    /// <summary>Time-of-day conditions ("only in daytime", "at night"): shown with the current time where rules are surfaced.</summary>
    [GeneratedRegex(@"\b(?:day|days|daytime|daylight|night|nights|nighttime|dark|darkness|morning|evening|dusk|dawn|sunset|sunrise)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeOfDayRx();

    /// <summary>Topic → words that bring it into play. Order is the scope preference for an unscoped rule.</summary>
    private static readonly (string Topic, string Name, Regex Rx)[] Topics =
    [
        ("build", "building", new(@"\b(?:build\w*|built|tower|construct\w*|blueprints?|designs?|roof|walls?|bridge|platform)\b", O)),
        ("farm", "farming", new(@"\b(?:farm\w*|wheat|harvest\w*|till\w*|hoe|replant\w*|crops?|seeds?|plant\w*|grass|bread)\b", O)),
        ("chop", "tree chopping", new(@"\b(?:chop\w*|trees?|logs?|saplings?|wood)\b", O)),
        ("mine", "mining", new(@"\b(?:min(?:e|es|ing)|cobble\w*|gen|generator|dig\w*|quarry)\b", O)),
        ("store", "storage", new(@"\b(?:chests?|stor(?:e|es|ing|age)|deposit\w*|stash\w*)\b", O)),
        ("fight", "fighting", new(@"\b(?:mobs?|kill\w*|fight\w*|creepers?|zombies?|skeletons?|spiders?|attack\w*|grinder)\b", O)),
        ("chat", "chat", new(@"\b(?:say|said|chat\w*|talk\w*|quiet|silent|call\s+(?:me|her|him|them)|addressed|spoken|speak\w*|nicknames?|greet\w*|-sama|-chan)\b", O)),
    ];

    public static bool IsSecret(string text) => SecretRx().IsMatch(text ?? "");

    /// <summary>The line reads as an instruction / rule / preference for the bots (or the lifting of one).</summary>
    public static bool IsInstruction(string text)
    {
        var t = text ?? "";
        if (t.Trim().Length == 0 || IsSecret(t)) return false;
        return IsRevocation(t) || Markers.Any(r => r.IsMatch(t));
    }

    public static bool IsRevocation(string text) => RevokeRx().IsMatch(text ?? "");

    public static bool MentionsTimeOfDay(string text) => TimeOfDayRx().IsMatch(text ?? "");

    /// <summary>
    /// The time window a rule limits its subject to: "day" ("the drop tower only in daytime, at night mine cobble", "no building
    /// at night", "build by day only"), "night" ("only mine at night"), or null when it names no limit. Only the clause that says
    /// only/never decides: "at night mine cobble" after it is what to do instead, not another limit.
    /// </summary>
    public static string? OnlyWhen(string text)
    {
        var t = text ?? "";
        if (OnlyRx().Match(t) is { Success: true } m) return Fold(m.Groups[1].Value);
        if (WhenOnlyRx().Match(t) is { Success: true } m2) return Fold(m2.Groups[1].Value);
        if (NotAtRx().Match(t) is { Success: true } m3) return Fold(m3.Groups[1].Value) == "night" ? "day" : "night";
        return null;
        static string Fold(string w) => w.StartsWith("day", StringComparison.OrdinalIgnoreCase) || w.Equals("daylight", StringComparison.OrdinalIgnoreCase)
                                        || w.Equals("morning", StringComparison.OrdinalIgnoreCase) ? "day" : "night";
    }

    [GeneratedRegex(@"\bonly\b[^.;,!?]{0,24}?\b(?:in|during|by|at|when it'?s)?\s*(?:the\s+)?(day\w*|daylight|morning|night\w*|dark)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OnlyRx();
    [GeneratedRegex(@"\b(?:by|in|during)\s+(?:the\s+)?(day\w*|daylight|night\w*|dark)\s+only\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WhenOnlyRx();
    [GeneratedRegex(@"\b(?:no|not|never|don'?t|do not|stop)\b[^.;,!?]{0,40}?\b(?:at|during|in|by)\s+(?:the\s+)?(night\w*|dark|day\w*|daylight)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotAtRx();

    /// <summary>Time-of-day words in the text (lowercased, day/daytime/daylight folded to "day", night/dark to "night").</summary>
    public static IReadOnlySet<string> TimeWords(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in TimeOfDayRx().Matches(text ?? ""))
        {
            var w = m.Value.ToLowerInvariant();
            set.Add(w.StartsWith("day", StringComparison.Ordinal) || w is "morning" or "sunrise" or "dawn" ? "day"
                : w.StartsWith("night", StringComparison.Ordinal) || w.StartsWith("dark", StringComparison.Ordinal) || w is "evening" or "dusk" or "sunset" ? "night" : w);
        }
        return set;
    }

    /// <summary>Activity topics the text is about ("build", "mine", "farm", ...).</summary>
    public static IReadOnlyList<string> TopicsOf(string text)
    {
        var t = text ?? "";
        return Topics.Where(x => x.Rx.IsMatch(t)).Select(x => x.Topic).ToList();
    }

    public static string TopicName(string topic) => Topics.FirstOrDefault(x => x.Topic == topic).Name ?? topic;

    /// <summary>Design id named in the text ("design #4", "build #4", "(#4)"), or null.</summary>
    public static string? DesignOf(string text)
    {
        var m = DesignRx().Match(text ?? "");
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
    }

    /// <summary>
    /// Scope: a named design first, then the first activity topic (a place named on the way, "store them in chest 14,64,-5", is
    /// detail), then a place (x,y,z) for a rule about nothing else, else general.
    /// </summary>
    public static DirectiveScope ScopeOf(string text)
    {
        if (DesignOf(text) is { } id) return new(DirectiveScopeKind.Design, id);
        var topics = TopicsOf(text ?? "");
        if (topics.Count > 0) return new(DirectiveScopeKind.Activity, topics[0]);
        var c = CoordsRx().Match(text ?? "");
        return c.Success ? new(DirectiveScopeKind.Place, $"{c.Groups[1].Value},{c.Groups[2].Value},{c.Groups[3].Value}") : DirectiveScope.General;
    }
}
