using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>
/// Last-moment fact checks on outgoing model speech (night review 10-27 B20): the text was written before the newest state
/// arrived, so claims the live state already contradicts are dropped right before sending.
/// <list type="bullet">
/// <item>Presence: "Foxy just logged off, ..." while Foxy re-joined during the turn (07:45:35: joined 0.85 s before the line).</item>
/// <item>Own work: "I'm back on my block now" while this turn's walk is still running, "done" while the job it just started
/// is still running (07:02:12: the delegate had only just started; it failed 81 s later).</item>
/// </list>
/// "I'm heading back" / "I'm on it" / "working on it" stay: only arrival and completion claims are checked.
/// </summary>
public static partial class SpeechFacts
{
    [GeneratedRegex(@"\b(?:(?:just|has|have|already)\s+)?(?:logged\s+(?:off|out)|left\s+(?:the\s+game|the\s+server|us)|(?:just|has|have)\s+left|went\s+(?:offline|to\s+bed)|is\s+(?:offline|gone)|signed\s+off|disconnected)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeftRx();

    [GeneratedRegex(@"\bI'?m\s+(?:back|already|now|finally)\s+(?:at|on|by|in|here|there|home)\b|\bI'?m\s+(?:here|there|home)\b(?!\s+(?:to|for|if|when))|\bI(?:'ve|\s+have)?\s+(?:arrived|made\s+it|got\s+(?:here|there|back))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArrivedRx();

    [GeneratedRegex(@"\bI(?:'ve|\s+have)?\s+(?:finished|completed|mined|deposited|stashed|crafted|smelted|built|filled|delivered|harvested)\b|\b(?:all\s+)?(?:done|finished)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompletedRx();

    /// <summary>A completion word about the future or negated ("when it's done", "not done yet", "I'll have it finished soon").</summary>
    [GeneratedRegex(@"\b(?:will|'ll|gonna|going\s+to|when|once|until|till|after|soon|should|yet|not|almost|nearly|half|if)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotYetRx();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceRx();

    [GeneratedRegex(@",\s+|\s+and\s+|;\s+|\s+but\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClauseRx();

    /// <summary>
    /// Drops sentences claiming a player left while that player is online again: <paramref name="online"/> are the players
    /// online now, <paramref name="rejoined"/> those who joined since the turn started (their leave is what the model is
    /// reporting, even under a nickname). A sentence naming an online player's login is stale whenever it was written.
    /// </summary>
    public static string DropStalePresence(string text, IReadOnlyCollection<string> online, IReadOnlyCollection<string> rejoined, List<string> dropped)
    {
        if (string.IsNullOrWhiteSpace(text) || online.Count == 0 || !LeftRx().IsMatch(text)) return text;
        return MapSentences(text, s =>
        {
            if (!LeftRx().IsMatch(s)) return s;
            bool names = online.Any(p => Regex.IsMatch(s, @"(?<![A-Za-z0-9_])" + Regex.Escape(p) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase));
            if (!names && rejoined.Count == 0) return s;
            dropped.Add(s);
            return "";
        });
    }

    /// <summary>
    /// Drops the clause of a claim the bot's own running work contradicts: arrival while <paramref name="moving"/>, completion
    /// while <paramref name="working"/> (a job started this turn still runs). The rest of the sentence stays.
    /// </summary>
    public static string DropPrematureClaims(string text, bool moving, bool working, List<string> dropped)
    {
        if (string.IsNullOrWhiteSpace(text) || (!moving && !working)) return text;
        bool Premature(string clause) => (moving && ArrivedRx().IsMatch(clause)) || (working && CompletedRx().IsMatch(clause) && !NotYetRx().IsMatch(clause));
        if (!Premature(text)) return text;
        return MapSentences(text, s =>
        {
            if (!Premature(s)) return s;
            // Clauses with their leading separators: "Got it foxy" | ", I'm back on my block now" | " and 67 is headed ...".
            var parts = new List<(string Sep, string Text)>();
            int at = 0;
            string sep = "";
            foreach (Match m in ClauseRx().Matches(s))
            {
                parts.Add((sep, s[at..m.Index]));
                sep = m.Value;
                at = m.Index + m.Length;
            }
            parts.Add((sep, s[at..]));
            var kept = new List<(string Sep, string Text)>();
            foreach (var p in parts)
            {
                if (Premature(p.Text)) { dropped.Add(p.Text.Trim()); continue; }
                kept.Add(p);
            }
            if (kept.Count == 0) return "";
            var sb = new System.Text.StringBuilder(kept[0].Text.TrimStart());
            for (int i = 1; i < kept.Count; i++) sb.Append(kept[i].Sep).Append(kept[i].Text);
            var r = sb.ToString().Trim();
            // The sentence end went with a dropped last clause: give it one back.
            if (r.Length > 0 && ".!?~".IndexOf(r[^1]) < 0 && ".!?~".IndexOf(s.TrimEnd()[^1]) >= 0) r += s.TrimEnd()[^1];
            return r;
        });
    }

    private static string MapSentences(string text, Func<string, string> map)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('/')) { if (line.Length > 0) lines.Add(line); continue; }
            var kept = SentenceRx().Split(line).Select(s => map(s.Trim())).Where(s => s.Length > 0).ToList();
            if (kept.Count > 0) lines.Add(string.Join(" ", kept));
        }
        return string.Join("\n", lines).Trim();
    }
}
