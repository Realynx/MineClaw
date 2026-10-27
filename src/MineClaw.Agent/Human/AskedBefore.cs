using System.Text.RegularExpressions;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Human;

/// <summary>
/// Hive question dedupe (user requirement 10-27): a bot never re-asks a player something another hive bot (main or a clone)
/// already asked them a few minutes ago. Every bot logs its own lines to the shared chat_log (kind <c>bot</c>), so the check is
/// one small query over the last minutes: a question from ANOTHER hive bot whose content words mostly match. The caller then
/// gets the earlier question and the player's answer (their next line), not a second ask. Zero tokens.
/// </summary>
public static partial class AskedBefore
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "you", "your", "can", "could", "would", "should", "want", "wanna", "need", "for", "with", "that", "this", "what",
        "where", "which", "who", "how", "are", "any", "some", "have", "has", "had", "does", "did", "will", "there", "here", "about",
        "please", "pls", "now", "just", "like", "them", "they", "one", "maybe", "okay", "yes", "uwu", "master", "bestie", "hey",
    };

    [GeneratedRegex("[a-z0-9]{3,}")]
    private static partial Regex WordRx();

    /// <summary>Content words of a question (case-folded, simple plural stripped, filler removed).</summary>
    public static HashSet<string> Words(string text) =>
        WordRx().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => !Stop.Contains(w))
            .Select(w => w.Length > 4 && w.EndsWith('s') ? w[..^1] : w).ToHashSet();

    /// <summary>Two questions ask the same thing: ≥ 2 shared content words covering ≥ 60 % of the shorter one.</summary>
    public static bool Same(string a, string b)
    {
        var (x, y) = (Words(a), Words(b));
        if (x.Count == 0 || y.Count == 0) return false;
        int shared = x.Count(y.Contains);
        return shared >= 2 && shared >= 0.6 * Math.Min(x.Count, y.Count);
    }

    /// <summary>
    /// The earlier question by another hive bot within <see cref="Window"/>, as one line for the caller ("maid_chan already asked
    /// PoofimaFox "…?" 2m ago; answer: "the oak one""), or null = not asked yet. Never throws (a busy db = ask normally).
    /// </summary>
    public static string? Find(AgentDb db, string self, Func<string, bool> isHiveBot, string question, string? player, DateTimeOffset now)
    {
        if (!question.Contains('?')) return null;
        try
        {
            var since = AgentDb.ToUnixMs(now - Window);
            var rows = db.Query("""
                SELECT at, player, text FROM chat_log WHERE kind IN ('bot', 'bot_whisper') AND at >= $s AND text LIKE '%?%'
                ORDER BY id DESC LIMIT 60
                """, r => (At: r.GetInt64(0), Bot: r.IsDBNull(1) ? "" : r.GetString(1), Text: r.GetString(2)), ("$s", since));
            foreach (var row in rows)
            {
                if (row.Bot.Length == 0 || row.Bot.Equals(self, StringComparison.OrdinalIgnoreCase) || !isHiveBot(row.Bot)) continue;
                if (!Same(row.Text, question)) continue;
                var answer = db.Query("""
                    SELECT text FROM chat_log WHERE kind = 'chat' AND at > $a AND ($p IS NULL OR player = $p COLLATE NOCASE)
                    ORDER BY id LIMIT 1
                    """, r => r.GetString(0), ("$a", row.At), ("$p", player)).FirstOrDefault();
                var ago = TextTokens.Ago(now - AgentDb.FromUnixMs(row.At));
                return $"{row.Bot} already asked{(player != null ? " " + player : "")} \"{TextUtil.Truncate(row.Text, 90)}\" {ago}; " +
                       (answer != null ? $"answer: \"{TextUtil.Truncate(answer, 90)}\"" : "no answer yet");
            }
        }
        catch (Exception) { /* ask normally */ }
        return null;
    }

    /// <summary>The hook for a runtime: only in a hive (the router knows its hive bots), else always null.</summary>
    public static Func<string, string?, string?> For(AgentDb db, ChatRouter chat, TimeProvider time) =>
        (question, player) => chat.IsHiveBot is { } hive ? Find(db, chat.Username, hive, question, player, time.GetUtcNow()) : null;
}
