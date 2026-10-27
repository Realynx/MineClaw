using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>
/// Deterministic last line of defence against the model narrating its own plumbing in game chat ("Delegating the craft.",
/// "running script", "checking memory"): short sentences that are purely meta are dropped from outgoing model text.
/// Real speech that merely starts with such a word ("running over to you!") is kept.
/// </summary>
public static partial class MetaSpeech
{
    [GeneratedRegex(
        @"^\W*(?:ok(?:ay)?[,!. ]+|now[,!. ]+|let me\s+|i'?ll\s+|i'?m\s+)?(?:delegat(?:e|ing|ed)\b|checking (?:my )?memory\b|asking (?:my )?memory\b|using (?:a |the )?tools?\b|calling (?:a |the |my )?(?:tool|worker|delegate|script|hive|ask_memory)\b|running (?:a |the |my |this )?(?:script|routine|tool|delegate|worker|task\b)|script\b|(?:spawning|recalling) (?:a |the )?clone\b|hive\b|tool call\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaRx();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceRx();

    /// <summary>
    /// The text with purely meta sentences removed (short, at most 10 words); <paramref name="suppressed"/> lists what was dropped.
    /// Emote lines ("/me ...") are left alone.
    /// </summary>
    public static string Strip(string text, out List<string> suppressed)
    {
        suppressed = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('/')) { lines.Add(raw); continue; }
            // A whole line of data ("4,64,6 cobblestone", "4,6=cobblestone 5,6=water"): a tool/scan dump, never speech.
            if (IsData(line)) { suppressed.Add(line); continue; }
            var kept = new List<string>();
            foreach (var s in SentenceRx().Split(line))
            {
                var t = s.Trim();
                if (t.Length == 0) continue;
                if (IsMeta(t)) suppressed.Add(t);
                else kept.Add(t);
            }
            if (kept.Count > 0) lines.Add(string.Join(" ", kept));
        }
        return string.Join("\n", lines).Trim();
    }

    [GeneratedRegex(@"^(?:-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?){1,2}|\(-?\d+(?:\s*,\s*-?\d+){1,2}\))$", RegexOptions.CultureInvariant)]
    private static partial Regex CoordRx();

    [GeneratedRegex(@"^[a-z][a-z_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRx();

    private static readonly HashSet<string> SpeechWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "im", "i'm", "me", "my", "you", "your", "u", "we", "us", "it", "is", "are", "was", "the", "a", "an", "at", "on", "in", "to",
        "of", "and", "or", "but", "so", "here", "there", "this", "that", "now", "ok", "okay", "yes", "no", "not", "got", "have", "has",
        "go", "going", "gonna", "will", "can", "need", "want", "see", "look", "with", "from", "by", "for", "foxy", "robin", "uwu", "nya",
        "pls", "please", "sorry", "thanks", "hi", "hey", "found", "mining", "mine", "put", "left", "right", "next", "near", "by",
    };

    /// <summary>
    /// A line that is pure data: coordinates plus block/item names (and numbers / separators), no sentence. Needs at least one
    /// coordinate; any word of normal speech ("I'm", "at", "the", "foxy") makes it speech.
    /// </summary>
    public static bool IsData(string line)
    {
        var t = (line ?? "").Trim();
        if (t.Length == 0) return false;
        int coords = 0, names = 0;
        foreach (var raw in Regex.Split(t, @"[\s|;]+"))
        {
            var tok = raw.Trim().Trim('(', ')', '[', ']', '{', '}').TrimEnd(',', '.', ':');
            if (tok.Length == 0) continue;
            // "4,6=cobblestone" / "4,64,6:water" / "stone@4,63,6" / "x3"
            foreach (var part in Regex.Split(tok, @"[=:@]"))
            {
                if (part.Length == 0) continue;
                if (CoordRx().IsMatch(part)) { coords++; continue; }
                if (Regex.IsMatch(part, @"^(?:x?\d+|\d+x|-?\d+(?:\.\d+)?%?)$")) continue;
                if (Regex.IsMatch(part, @"^\W+$")) continue;
                if (!NameRx().IsMatch(part) || SpeechWords.Contains(part)) return false;
                names++;
            }
        }
        // A bare coordinate is an answer ("14,64,-5"); coordinates WITH block/item names and nothing else are a dump.
        return coords > 0 && names > 0 && names <= coords * 4 + 2;
    }

    /// <summary>A short sentence that only narrates internal mechanics.</summary>
    public static bool IsMeta(string sentence)
    {
        var t = sentence.Trim();
        if (t.Length == 0 || !MetaRx().IsMatch(t)) return false;
        int words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return words <= 10;
    }
}
