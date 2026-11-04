using System.Text.RegularExpressions;

namespace MineClaw.Agent.Bodies;

/// <summary>
/// One brain, many bodies: which bodies a player line addresses ("67, come here", "hey 69", "maid chan 420", "67 and 69 mine the
/// gen"), so chat to "67" becomes body 67's turn focus while the brain stays one mind. The rules are the hive ChatArbiter's
/// (docs/design/one-brain-many-bodies.md section 2, "BodyAddressing holds ChatArbiter's number and explicit-name regex rules"):
/// a body's login / "maid chan 67" / "#67" / "clone 67" addresses it anywhere unless a mention word precedes it ("tell maid
/// chan 67"); a bare number only at the start of a line or sentence, next to an address word ("hey 69", "69 maid"), after a
/// discourse word with a comma ("also 67, ..."), or chained to an addressed body ("67 and 69"); never a quantity or a
/// coordinate ("get 67 cobble", "6,67,8"). The arbiter keeps its own copy until step 5 removes the hive.
/// </summary>
public sealed class BodyAddressing
{
    private readonly string _main;
    private readonly List<string> _clones;
    private readonly Dictionary<string, Regex> _explicit = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Regex> _number = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Regex> _bare = new(StringComparer.OrdinalIgnoreCase);
    private readonly Regex _mainRegex;

    private static readonly Regex MainVocative = new(@"^\W*(?:(?:hey|yo|ok|okay|oi)\s+)?main(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GroupAddress = new(
        @"(?<![A-Za-z0-9_])(?:both of (?:you|u|ya)|you both|u both|you two|all of (?:you|u|ya)|you all|y'?all|every(?:one|body)|all (?:the |you )?(?:maids?|bots|clones)|(?:maids|bots|clones|you guys),|hey (?:maids|bots|guys))(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="main">The main body's login.</param>
    /// <param name="mainNames">What players call the main body (display name, aliases).</param>
    /// <param name="clones">The clone bodies' logins ("maid_chan_67").</param>
    public BodyAddressing(string main, IEnumerable<string> mainNames, IEnumerable<string> clones)
    {
        _main = main;
        _clones = clones.Where(c => !string.IsNullOrWhiteSpace(c) && !c.Equals(main, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mains = mainNames.Concat([main]).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = mains.Concat(["clone", "bot", "maid chan", "maid", "sis"]).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length).Select(Words);
        string nameAlt = string.Join("|", names);
        _mainRegex = new Regex($@"(?<![A-Za-z0-9_])@?(?:{string.Join("|", mains.OrderByDescending(n => n.Length).Select(Words))})(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var c in _clones)
        {
            var num = NumberOf(c);
            var user = string.Join(@"[\s_-]*", c.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
            var parts = new List<string> { $@"(?<![A-Za-z0-9_]){user}(?![A-Za-z0-9])" };
            if (num != null)
            {
                parts.Add($@"^[\s@#]*{num}(?![0-9])");                                   // "67, come here"
                _number[c] = new Regex($@"(?<![0-9A-Za-z_.,#-]){num}(?![0-9A-Za-z_]|[.,]-?[0-9]|,\s*-?[0-9]+\s*,\s*-?[0-9])", RegexOptions.CultureInvariant);
                parts.Add($@"(?<![A-Za-z0-9_])(?:{nameAlt})[\s_#-]*{num}(?![0-9])");     // "maid chan 420", "clone 69"
                parts.Add($@"(?<![A-Za-z0-9_])#{num}(?![0-9])");                          // "#1337"
                parts.Add($@"(?<![0-9A-Za-z_.,#-]){num}[\s,]+(?:maid|clone|bot|sis)(?![A-Za-z0-9])"); // "69 maid, ..."
                _bare[c] = new Regex($@"(?<![0-9]){num}(?![0-9])", RegexOptions.CultureInvariant);
            }
            _explicit[c] = new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        static string Words(string n) => string.Join(@"[\s_-]*", n.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
    }

    public string Main => _main;
    public IReadOnlyList<string> Clones => _clones;

    /// <summary>"maid_chan_67" → "67" (null without a number suffix).</summary>
    public static string? NumberOf(string login) => Regex.Match(login, @"(\d+)$") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>True when the line addresses several/all bodies at once ("both of you", "everyone", "all maids").</summary>
    public static bool IsGroupAddress(string? text) => text != null && GroupAddress.IsMatch(text);

    /// <summary>Every body the line addresses (logins, main included when named), in order of first mention.</summary>
    public IReadOnlyList<string> Addressed(string text, bool emote = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var named = new List<string>();
        int prevEnd = -1;
        foreach (var x in Mentions(text, emote))
        {
            if (x.Index < prevEnd) continue;
            if (!IsAddress(text, x.Index, x.End, x.Strong, prevEnd)) continue;
            prevEnd = x.End;
            if (!named.Contains(x.Bot, StringComparer.OrdinalIgnoreCase)) named.Add(x.Bot);
        }
        return named;
    }

    /// <summary>Every mention of a body in the line, in order (a strong mention first at a tie; weak ones inside a strong one dropped).</summary>
    private List<(string Bot, int Index, int End, bool Strong)> Mentions(string text, bool emote)
    {
        var cands = new List<(string Bot, int Index, int End, bool Strong)>();
        foreach (var c in _clones) AddCloneMentions(cands, c, text, emote);
        var clonesStrong = cands.Where(x => x.Strong).ToList();
        foreach (Match m in _mainRegex.Matches(text))
            if (!clonesStrong.Any(x => x.Index < m.Index + m.Length && m.Index < x.End)) // "maid chan 69" is 69, not main
                cands.Add((_main, m.Index, m.Index + m.Length, true));
        if (MainVocative.Match(text) is { Success: true } mv) cands.Add((_main, mv.Index, mv.Index + mv.Length, true)); // "main spin"
        var strong = cands.Where(x => x.Strong).ToList();
        return cands.Where(x => x.Strong || !strong.Any(y => y.Index <= x.Index && x.End <= y.End))
            .OrderBy(x => x.Index).ThenByDescending(x => x.Strong).ToList();
    }

    private void AddCloneMentions(List<(string Bot, int Index, int End, bool Strong)> cands, string c, string text, bool emote)
    {
        foreach (Match m in _explicit[c].Matches(text)) cands.Add((c, m.Index, m.Index + m.Length, true));
        if (_number.TryGetValue(c, out var n))
            foreach (Match m in n.Matches(text)) cands.Add((c, m.Index, m.Index + m.Length, false));
        if (emote && _bare.TryGetValue(c, out var b))
            foreach (Match m in b.Matches(text)) cands.Add((c, m.Index, m.Index + m.Length, true)); // "* kiwi pats 67"
    }

    /// <summary>The clone bodies the line addresses (main left out).</summary>
    public IReadOnlyList<string> AddressedClones(string text, bool emote = false) =>
        Addressed(text, emote).Where(n => !n.Equals(_main, StringComparison.OrdinalIgnoreCase)).ToList();

    private static readonly HashSet<string> RefWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "for", "with", "from", "than", "of", "at", "by", "about", "like", "and", "or", "nor", "is", "was", "are", "were", "be",
        "been", "am", "tell", "ask", "give", "gave", "send", "sent", "bring", "brought", "get", "got", "have", "has", "had", "let",
        "make", "made", "need", "needs", "want", "wants", "help", "told", "asked", "see", "saw", "call", "called", "me", "it", "that",
        "this", "the", "a", "an", "x", "only", "just", "around", "over", "under", "maybe", "exactly", "least", "most", "all", "did",
        "does", "do", "can", "could", "will", "would", "should", "where", "what", "who", "how", "whats", "wheres", "whos", "if",
        "when", "on", "in", "into", "onto", "my", "your", "his", "her", "their", "our", "take", "took", "use", "used", "mine", "dig",
        "collect", "grab", "fetch", "gather", "chop", "craft", "place", "put", "drop", "toss", "deposit", "withdraw", "leave", "keep",
        "spawn", "recall", "respawn", "summon", "pick", "picked", "not", "both", "either", "between", "via", "per",
    };
    private static readonly HashSet<string> AddressWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "hey", "hi", "hello", "yo", "oi", "hiya", "heya", "sup", "ok", "okay", "thanks", "thank", "thx", "ty", "please", "pls", "plz",
        "sorry", "dear", "nice", "gj", "gm", "gn", "bye", "cya", "wb", "welcome", "oy", "ayo", "yay",
        "maid", "maids", "chan", "clone", "bot", "sis", "sister", "job", "done", "work",
    };
    private static readonly HashSet<string> TrailingAddress = new(StringComparer.OrdinalIgnoreCase) { "maid", "chan", "clone", "bot", "sis" };
    private static readonly HashSet<string> Discourse = new(StringComparer.OrdinalIgnoreCase)
    {
        "also", "so", "and", "but", "now", "then", "well", "alright", "oh", "um", "uh", "anyway", "btw", "wait", "right", "yes", "yeah",
        "yea", "no", "nope", "good", "great",
    };
    private static readonly Regex LastWord = new(@"([A-Za-z']+)[^A-Za-z0-9']*$", RegexOptions.CultureInvariant);
    private static readonly Regex FirstWord = new(@"^\s*([A-Za-z]+)(?![A-Za-z])", RegexOptions.CultureInvariant);
    private static readonly Regex Joiner = new(@"^\s*(?:,|&|\+|and|n|or)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CoordBefore = new(@"-?[0-9]+\s*,\s*$", RegexOptions.CultureInvariant);

    private static bool IsAddress(string text, int index, int end, bool strong, int prevAddressedEnd)
    {
        var before = text[..index];
        if (StartsClause(text, before, index, prevAddressedEnd)) return true;
        var lw = LastWord.Match(before) is { Success: true } w ? w.Groups[1].Value.Trim('\'') : "";
        if (strong) return !RefWords.Contains(lw);
        if (CoordBefore.IsMatch(before)) return false;
        if (AddressWords.Contains(lw)) return true;
        return TrailedOrDiscourse(text[end..], lw);
    }

    /// <summary>The mention opens the line or a sentence, or follows the previous addressee with only a joiner between.</summary>
    private static bool StartsClause(string text, string before, int index, int prevAddressedEnd)
    {
        var trimmed = before.TrimEnd().TrimStart(' ', '\t', '@', '#');
        if (trimmed.Length == 0) return true;
        if (trimmed[^1] is '.' or '!' or '?' or ';') return true;
        return prevAddressedEnd >= 0 && prevAddressedEnd <= index && Joiner.IsMatch(text[prevAddressedEnd..index]);
    }

    /// <summary>"67 maid ...", or a discourse word then the name then ',' / ':' ("so 69, ...").</summary>
    private static bool TrailedOrDiscourse(string after, string lastWord)
    {
        if (FirstWord.Match(after) is { Success: true } fw && TrailingAddress.Contains(fw.Groups[1].Value)) return true;
        char next = after.TrimStart() is { Length: > 0 } a ? a[0] : '\0';
        if (Discourse.Contains(lastWord)) return next is ',' or ':';
        return false;
    }
}
