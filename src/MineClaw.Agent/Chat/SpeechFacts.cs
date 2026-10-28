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

    /// <summary>What a task result says it really got: "generator: 4/64 cobblestone in 4m15s (dug 64 at 5,64,7)".</summary>
    public readonly record struct TaskCount(string Item, int Actual, int Target, int? Dug);

    [GeneratedRegex(@"\b(\d+)/(\d+) (cobblestone|cobble)\b(?:[^;]*?\(dug (\d+)\b)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaskCountRx();

    /// <summary>The counts the turn's task results state (ledger-checked by the tool layer), newest last.</summary>
    public static IReadOnlyList<TaskCount> TaskCounts(IEnumerable<Events.GameEvent> events)
    {
        var list = new List<TaskCount>();
        foreach (var e in events)
        {
            if (e.Kind is not (Events.EventKind.TaskCompleted or Events.EventKind.TaskFailed)) continue;
            foreach (Match m in TaskCountRx().Matches(e.Text ?? ""))
                list.Add(new TaskCount("cobble", int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                    m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : null));
        }
        return list;
    }

    [GeneratedRegex(@"\b(?<verb>mined|dug|got|gathered|collected|harvested|grabbed|made|finished|completed|stored|deposited|brought|delivered)\s+(?<q>(?:all\s+(?:the\s+|of\s+the\s+)?|another\s+|the\s+|a\s+full\s+)?)(?<n>\d+)\b(?<rest>\s+[A-Za-z]+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountClaimRx();

    private static readonly HashSet<string> CountWords = new(StringComparer.OrdinalIgnoreCase)
        { "cobble", "cobblestone", "cobbles", "more", "of", "for", "blocks", "and", "but", "at", "from", "in", "this", "that", "so", "now", "today" };

    /// <summary>
    /// Honesty guard for public and internal text (live 10-28 06:49: "I mined all 64, but again only 4 cobble ended up in the
    /// chest" after "[task ok] harvest_generator: dug 64/64, stored 4"): a claim that the bot got more of a task's item than
    /// its result states ("mined all 64", "finished the 24") becomes the real count ("mined only 4 of the 64"). "Dug 64" stays
    /// when it really dug 64; other items' numbers are untouched.
    /// </summary>
    public static string CorrectCounts(string text, IReadOnlyList<TaskCount> facts, List<string>? corrected = null)
    {
        if (string.IsNullOrWhiteSpace(text) || facts.Count == 0) return text;
        return CountClaimRx().Replace(text, m =>
        {
            int n = int.Parse(m.Groups["n"].Value);
            string next = m.Groups["rest"].Success ? m.Groups["rest"].Value.Trim() : "";
            var fact = facts.LastOrDefault(f => f.Target == n);
            bool countWord = next.Length == 0 || CountWords.Contains(next) || next.StartsWith("cobble", StringComparison.OrdinalIgnoreCase);
            if (fact == default && countWord) fact = facts[^1];
            if (fact == default || n <= fact.Actual) return m.Value;
            if (m.Groups["verb"].Value.Equals("dug", StringComparison.OrdinalIgnoreCase) && fact.Dug is { } dug && n <= dug) return m.Value;
            string fixedText = $"{m.Groups["verb"].Value} only {fact.Actual} of the {n}{m.Groups["rest"].Value}";
            corrected?.Add($"{m.Value} -> {fixedText}");
            return fixedText;
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
