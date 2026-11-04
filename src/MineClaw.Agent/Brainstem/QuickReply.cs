using System.Text.Json;
using System.Text.RegularExpressions;

namespace MineClaw.Agent.Brainstem;

/// <summary>What the deterministic pre-filter makes of an addressed chat batch.</summary>
public enum QuickKind
{
    /// <summary>Actions, items, the world, memory, plans, answers to questions, anything unclear: the brain decides.</summary>
    Brain,
    /// <summary>Greetings, thanks, praise, laughter, emotes, just the bot's name: safe for a cheap-only reply.</summary>
    SmallTalk,
    /// <summary>"how are you", "what are you doing", "where are you": cheap-only, answered from [here] and the state header.</summary>
    Status,
}

/// <summary>
/// The cheap side of two-phase replies (zero tokens here): the strict small-talk pre-filter, the correction detector, the
/// single cheap-tier prompt (ack or final reply) and the lints that keep both honest. An ack is non-committal (no facts,
/// numbers, items, yes/no, promises or claims); a final cheap-only reply makes no promise to act and no claim of having done
/// anything, and every number in it must come from the context it was given.
/// </summary>
public static class QuickReply
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // Whole clauses only (after the bot's name and filler are stripped). Deliberately excludes "ok", "yes", "no", "sure":
    // those answer something and need the brain.
    private static readonly Regex SmallTalkRx = new(@"^(?:" +
        @"h+i+|hii+|hello+|hey+a?|heya|hiya|yo+|sup|wassup|howdy|gm|gn|good (?:morning|evening|night|afternoon|day)|g'?night|nighty(?: night)?|" +
        @"bye+|bye bye|cya|see (?:ya|you)(?: later| soon)?|later|brb|back|i'?m back|im back|wb|welcome back|o/|\\o|" +
        @"thanks?(?: (?:a lot|so much|babe|girl))?|thx|ty|tysm|thank (?:you|u)(?: so much)?|" +
        @"good (?:girls?|job|work|bot|maid|one)|nice(?: one| job| work)?|well done|great(?: job| work)?|amazing|awesome|cute|so cute|" +
        @"(?:you'?re|ur|you are) (?:so )?(?:cute|the best|amazing|awesome|adorable|a good girl)|love (?:you|u|ya)|ily|<3|best maid|" +
        @"lol+|lmao+|lmfao|haha+|hah+|hehe+|xd+|:3|:\)|:d|uwu|owo|nya+|rofl|omg|wow+|yay+|aww+|bruh|mood|same" +
        @")$", O);

    private static readonly Regex StatusRx = new(@"^(?:" +
        @"how (?:are|r) (?:you|u|ya)(?: doing| today)?|how'?s it going|how (?:you|u) doing|hru|wbu|" +
        @"(?:are )?(?:you|u) (?:ok(?:ay)?|good|alright|there|alive|awake|still there)|r u ok|" +
        @"what (?:are|r) (?:you|u) (?:doing|up to)|wyd|wbu|what'?s up|whats up|wassup|" +
        @"where (?:are|r) (?:you|u)" +
        @")$", O);

    private static readonly Regex ClauseSplit = new(@"[,.!?;~]+|\s+(?:and|&)\s+", O);
    private static readonly Regex Digit = new(@"\d", O);

    private static readonly Regex CorrectionRx = new(
        @"^\W*(?:no+|nah|nope|wait|huh|what\??|wdym|hm+)\b[\s,!.]|\bi meant\b|\bi mean\b|\bnot what i\b|\bthat'?s not\b|\bi (?:said|asked|was asking)\b|" +
        @"\byou (?:didn'?t|did not) (?:answer|listen|get it)\b|\bi didn'?t ask\b|\bmisunderstood\b|\bwrong\b|\blisten\b|\bread (?:again|it again)\b", O);

    /// <summary>
    /// Strict pre-filter: SmallTalk/Status only when EVERY clause of every line is on the allowlist (a lone name counts as a
    /// greeting) and nothing in it is a reflex, a job, a digit or an item. Everything else (and anything unsure) = Brain.
    /// </summary>
    public static QuickKind Classify(IReadOnlyList<string> lines, IEnumerable<string>? names = null, bool emote = false)
    {
        if (lines.Count == 0 || lines.Count > 2) return QuickKind.Brain;
        var nameList = (names ?? []).ToList();
        bool status = false;
        foreach (var line in lines)
        {
            var kind = ClassifyLine(line, nameList, emote);
            if (kind == QuickKind.Brain) return QuickKind.Brain;
            if (kind == QuickKind.Status) status = true;
        }
        return status ? QuickKind.Status : QuickKind.SmallTalk;
    }

    private static QuickKind ClassifyLine(string? line, List<string> nameList, bool emote)
    {
        var raw = (line ?? "").Replace("<3", " ily ", StringComparison.Ordinal); // a heart, not a number
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 80 || Digit.IsMatch(raw)) return QuickKind.Brain;
        if (IntentMatcher.Reflex(raw, nameList, _ => true).IsReflex || IntentMatcher.Speculative(raw, nameList).IsSpeculative) return QuickKind.Brain;
        return emote ? EmoteKind(raw) : ClauseKind(raw, nameList);
    }

    /// <summary>"/me pats maid_chan": fine unless it involves an item or a job ("hands maid_chan a bucket").</summary>
    private static QuickKind EmoteKind(string raw)
    {
        var words = Regex.Matches(raw.ToLowerInvariant(), "[a-z_]{3,}").Select(m => m.Value);
        if (words.Any(w => MineClaw.Data.Items.ByName(w) != null || MineClaw.Data.Blocks.ByName(w) != null)) return QuickKind.Brain;
        if (Regex.IsMatch(raw, @"\b(?:give|gives|hands?|toss(?:es)?|throws?|drops?|points?|shows?)\b", O)) return QuickKind.Brain;
        return QuickKind.SmallTalk;
    }

    /// <summary>Every clause (names removed) small talk, or status; anything else is for the brain.</summary>
    private static QuickKind ClauseKind(string raw, List<string> nameList)
    {
        var norm = IntentMatcher.Normalize(raw, nameList);
        foreach (var n in nameList.Where(n => !string.IsNullOrWhiteSpace(n)))
            norm = Regex.Replace(norm, $@"\b{Regex.Escape(n.Trim().ToLowerInvariant())}\b", " ", O);
        var clauses = ClauseSplit.Split(norm).Select(c => Regex.Replace(c, @"\s+", " ").Trim(' ', '@', '\'', '"')).Where(c => c.Length > 0).ToList();
        bool status = false;
        foreach (var c in clauses)
        {
            if (SmallTalkRx.IsMatch(c)) continue;
            if (StatusRx.IsMatch(c)) { status = true; continue; }
            return QuickKind.Brain;
        }
        return status ? QuickKind.Status : QuickKind.SmallTalk;
    }

    /// <summary>"no, I meant the other chest", "wait what", "that's not what I asked".</summary>
    public static bool IsCorrection(string text) => !string.IsNullOrWhiteSpace(text) && CorrectionRx.IsMatch(text.Trim());

    // ---------------- lints ----------------

    private static readonly Regex Coords = new(@"-?\d+\s*[, ]\s*-?\d+(?:\s*[, ]\s*-?\d+)?", O);
    private static readonly Regex AckBanned = new(
        @"\b(?:yes|yeah|yep|yup|ya|no|nope|nah|sure|ok(?:ay)?|done|finished|got|have|has|had|found|there(?:'s| is| are)|i'?ve|i did|already|" +
        @"will|i'?ll|gonna|going to|can'?t|cannot|won'?t|never|always|is|are|was|were|all|every|stack|stacks|ready|here you go|here)\b", O);
    private static readonly Regex FinalBanned = new(
        @"\b(?:i'?ll|i will|will do|gonna|going to|on it|omw|on my way|coming|be right there|let me|lemme|right away|done|finished|" +
        @"got (?:it|them|em|the|you)|found|placed|built|crafted|mined|chopped|grabbed|brought|gave|giving)\b", O);

    /// <summary>The cleaned ack, or null when it breaks a rule (then the bot stays silent): one line, ≤ maxWords words and
    /// maxChars chars, no digits/coordinates, no yes/no, promises or claims, no command.</summary>
    public static string? LintAck(string? text, int maxWords = 5, int maxChars = 30)
    {
        var t = Clean(text);
        if (t == null) return null;
        if (t.Length > maxChars || Words(t) > maxWords || Words(t) == 0) return null;
        if (Digit.IsMatch(t) || AckBanned.IsMatch(t)) return null;
        return t;
    }

    /// <summary>
    /// The cleaned cheap-only final reply, or null (then the brain answers): one line, ≤ maxChars, no coordinates, no
    /// promise to act or claim of an action, and every number must appear in <paramref name="context"/> (header/[here]).
    /// </summary>
    public static string? LintFinal(string? text, string context, int maxChars = 120)
    {
        var t = Clean(text);
        if (t == null || t.Length > maxChars) return null;
        if (Coords.IsMatch(t) || FinalBanned.IsMatch(t)) return null;
        foreach (Match m in Regex.Matches(t, @"\d+"))
            if (!Regex.IsMatch(context ?? "", $@"(?<!\d){m.Value}(?!\d)")) return null;
        return t;
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Replace("\r", "").Trim();
        if (t.Contains('\n')) return null;
        t = t.Trim('"', '\'', '`', ' ');
        if (t.Length == 0 || t.StartsWith('/') || t.Contains('{') || t.Contains('[')) return null;
        if (t is "(silent)" or "." or "...") return null;
        return t;
    }

    private static int Words(string t) => t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ---------------- the single cheap call ----------------

    /// <summary>Parses {"needs_brain":bool,"say":"..."}; anything unreadable = needs the brain, nothing to say.</summary>
    public static (bool NeedsBrain, string? Say) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (true, null);
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        if (a < 0 || b <= a) return (true, null);
        try
        {
            using var doc = JsonDocument.Parse(text[a..(b + 1)]);
            var root = doc.RootElement;
            bool needs = !root.TryGetProperty("needs_brain", out var nb) || nb.ValueKind != JsonValueKind.False;
            string? say = root.TryGetProperty("say", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return (needs, say);
        }
        catch (JsonException) { return (true, null); }
    }

    private static string PersonaLine(string name, string persona) =>
        $"You are {name}, a maid bot playing Minecraft with your humans. Persona: {Trunc(persona, 420)}";

    /// <summary>System prompt for the triage+reply call (small-talk candidates): one JSON object, either a final reply or an ack.</summary>
    public static string TriageSystem(string name, string persona, string? self, int maxWords) =>
        PersonaLine(name, persona) + (string.IsNullOrWhiteSpace(self) ? "" : "\n" + Trunc(self, 500)) + $$"""

        Write ONE in-game chat line as yourself: short, casual, ASCII only, no coordinates.
        needs_brain=false ONLY for small talk you can fully answer right now: greeting, thanks, praise, an emote, how are you,
        what are you doing / where are you (from [state]/[here] only). Never promise or claim any action, item or fact that is
        not in [state]/[here]; no numbers unless they are there.
        Anything about actions, items, the world, memory or plans, or if unsure: needs_brain=true and say = a quick
        non-committal ack of at most {{maxWords}} words (e.g. "on it~", "hm, lemme see", "which one?"): no facts, no yes/no.
        Output only JSON: {"needs_brain":true|false,"say":"..."}
        """;

    /// <summary>System prompt for the plain ack (brain-bound messages).</summary>
    public static string AckSystem(string name, string persona, int maxWords, int maxChars) =>
        PersonaLine(name, persona) + $"""

        The player just talked to you and you need a moment to think. Write ONE quick acknowledgement in persona: at most
        {maxWords} words and {maxChars} characters, e.g. "on it!", "omw~", "which chest?", "hm, lemme see".
        Non-committal: no facts, numbers, items, yes/no, promises or claims. Output only the line.
        """;

    private static string Trunc(string s, int max) { s = (s ?? "").Trim(); return s.Length <= max ? s : s[..max]; }
}
