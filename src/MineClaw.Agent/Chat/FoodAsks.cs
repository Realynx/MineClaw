using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>
/// Food nagging guard (live 2026-10-27: "got any food?" / "still starving lol" in six replies in 20 minutes): clauses that
/// ask for food or report the bot's own hunger reach each player at most once per <see cref="Interval"/>. A player who just
/// brought food up themselves ("are you hungry?") gets the answer.
/// </summary>
public sealed class FoodAsks
{
    private static readonly Regex Ask = new(
        @"\b(?:got|have|has|any|spare|share|gimme|give\s+me|could\s+i\s+(?:have|get)|can\s+i\s+(?:have|get)|bring\s+me|toss\s+me)\b[^.!?,]{0,40}\b(?:food|snacks?|bread|apples?|meat|pork(?:chops?)?|fish|steak|stew)\b" +
        @"|\bcould\s+i\s+have\s+one\b|\b(?:starving|hungry|famished|0\s+food|zero\s+food|no\s+food|food\s+(?:is\s+|at\s+)?0|half\s+(?:health|hp))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PlayerTopic = new(@"\b(?:food|hungry|starv\w*|eat\w*|bread|apples?|pork\w*|fish|steak|snacks?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly PerPlayerOnce _once;

    public FoodAsks(TimeSpan? interval = null) => _once = new PerPlayerOnce(interval ?? TimeSpan.FromMinutes(10));

    public TimeSpan Interval => _once.Interval;

    public static bool IsFoodLine(string text) => Ask.IsMatch(text ?? "");

    /// <summary>Is this player line about food (an invitation to answer)?</summary>
    public static bool IsFoodTopic(string text) => PlayerTopic.IsMatch(text ?? "");

    /// <summary>
    /// <paramref name="text"/> with food clauses dropped for players asked within the interval (all of <paramref name="audience"/>,
    /// or anyone when it's empty); at most one food clause per reply. <paramref name="playerBroughtItUp"/>: a recent line of
    /// theirs was about food (then the reply is an answer, not nagging).
    /// </summary>
    public string Filter(string text, IReadOnlyCollection<string> audience, DateTimeOffset now, Func<string, bool>? playerBroughtItUp = null)
    {
        if (string.IsNullOrWhiteSpace(text) || !Ask.IsMatch(text)) return text ?? "";
        if (audience.Any(p => playerBroughtItUp?.Invoke(p) == true)) { _once.Mark(audience, now); return text; }
        bool recent = _once.Recent(audience, now);
        bool used = false;
        var result = Clauses.Drop(text, c =>
        {
            if (!Ask.IsMatch(c)) return false;
            if (recent || used) return true;
            used = true;
            return false;
        });
        if (used) _once.Mark(audience, now);
        return result;
    }
}

/// <summary>
/// "Robin greeted twice on join" (live 2026-10-27: "heyy robin welcome back" then 4 s later "ROBIN hiii uwu, ..."): a greeting
/// clause reaches each player at most once per <see cref="Interval"/>; the rest of the reply goes out.
/// </summary>
public sealed class Greetings
{
    private static readonly Regex Greeting = new(
        @"^\W*(?:[a-z_]+\s+)?(?:h+e+y+|h+i+|ha+i+|hello+|hallo|yo+|hiya|howdy|welcome\s+back|wb|good\s+(?:morning|evening|afternoon))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WelcomeBack = new(@"\bwelcome\s+(?:back|home)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly PerPlayerOnce _once;

    public Greetings(TimeSpan? interval = null) => _once = new PerPlayerOnce(interval ?? TimeSpan.FromMinutes(3));

    public TimeSpan Interval => _once.Interval;

    public static bool IsGreeting(string clause)
    {
        var c = clause ?? "";
        return (Greeting.IsMatch(c) && c.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 6) || WelcomeBack.IsMatch(c);
    }

    /// <summary>Greeting clauses dropped for players greeted within the interval; a greeting that goes out is recorded.</summary>
    public string Filter(string text, IReadOnlyCollection<string> audience, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text) || audience.Count == 0) return text ?? "";
        bool recent = _once.Recent(audience, now, includeEveryone: false);
        bool greeted = false;
        var result = Clauses.Drop(text, c =>
        {
            if (!IsGreeting(c)) return false;
            greeted = true;
            return recent;
        });
        if (greeted && !recent) _once.Mark(audience, now);
        return result;
    }
}

/// <summary>Once-per-interval bookkeeping per player ("*" = said to nobody in particular).</summary>
internal sealed class PerPlayerOnce(TimeSpan interval)
{
    private const string Everyone = "*";
    private readonly Dictionary<string, DateTimeOffset> _at = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public TimeSpan Interval { get; } = interval;

    public bool Recent(IReadOnlyCollection<string> audience, DateTimeOffset now, bool includeEveryone = true)
    {
        lock (_lock)
        {
            bool Hit(string p) => _at.TryGetValue(p, out var a) && now - a < Interval;
            if (audience.Count == 0) return Hit(Everyone) || _at.Values.Any(a => now - a < Interval);
            return audience.Any(p => Hit(p) || (includeEveryone && Hit(Everyone)));
        }
    }

    public void Mark(IReadOnlyCollection<string> audience, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (audience.Count == 0) _at[Everyone] = now;
            foreach (var p in audience) _at[p] = now;
        }
    }
}

/// <summary>Clause-level editing of chat text (sentences, then comma clauses), keeping lines, emotes and punctuation.</summary>
internal static class Clauses
{
    private static readonly Regex Sentence = new(@"(?<=[.!?~])\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Comma = new(@",\s+", RegexOptions.CultureInvariant);

    public static string Drop(string text, Func<string, bool> drop)
    {
        var lines = new List<string>();
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            if (raw.TrimStart().StartsWith('/')) { lines.Add(raw); continue; }
            var sentences = new List<string>();
            foreach (var s in Sentence.Split(raw))
            {
                var t = s.Trim();
                if (t.Length == 0) continue;
                var end = t[^1] is '.' or '!' or '?' or '~' ? t[^1].ToString() : "";
                var body = end.Length > 0 ? t[..^1] : t;
                var parts = Comma.Split(body).ToList();
                var kept = parts.Where(c => !drop(c.Trim())).ToList();
                if (kept.Count == 0) continue;
                if (kept.Count == parts.Count) { sentences.Add(t); continue; }
                var joined = string.Join(", ", kept.Select(k => k.Trim())).Trim();
                if (joined.Length == 0) continue;
                if (!ReferenceEquals(kept[^1], parts[^1]) && end == "?") end = ""; // the question was the dropped part
                sentences.Add(joined + end);
            }
            if (sentences.Count > 0) lines.Add(string.Join(" ", sentences));
        }
        return string.Join("\n", lines).Trim();
    }
}
