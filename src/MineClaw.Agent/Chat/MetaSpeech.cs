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

    /// <summary>A short sentence that only narrates internal mechanics.</summary>
    public static bool IsMeta(string sentence)
    {
        var t = sentence.Trim();
        if (t.Length == 0 || !MetaRx().IsMatch(t)) return false;
        int words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return words <= 10;
    }
}
