using System.Text;
using System.Text.RegularExpressions;

namespace MineClaw.Agent.Rag;

/// <summary>Query tokenization shared by FTS, the LIKE fallback and relevance gating.</summary>
public static partial class TextTokens
{
    public const int MaxTerms = 16;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "an", "and", "or", "but", "is", "are", "was", "were", "be", "been", "being", "to", "of", "in", "on", "at", "for",
        "with", "you", "your", "yours", "me", "my", "we", "us", "our", "it", "its", "this", "that", "these", "those",
        "what", "where", "when", "how", "why", "who", "which", "can", "could", "would", "should", "will", "shall", "do", "does",
        "did", "done", "have", "has", "had", "not", "no", "yes", "yeah", "yep", "ok", "okay", "hey", "hi", "hello", "lol", "lmao",
        "pls", "plz", "please", "just", "so", "up", "out", "get", "got", "go", "going", "gonna", "come", "like", "some", "any",
        "all", "there", "here", "then", "than", "too", "very", "really", "also", "from", "by", "as", "about", "into", "over",
        "now", "if", "im", "i'm", "its", "it's", "dont", "don't", "thanks", "thank", "thx", "ty", "u", "ur", "him", "her", "them",
        "they", "he", "she", "his", "hers", "their", "am", "let", "lets", "let's", "want", "wanna", "need", "know", "think",
        "make", "made", "one", "thing", "things", "stuff", "maybe", "much", "many", "more", "most", "again", "still", "well",
        // event-format words
        "chat", "whisper", "task", "idle", "check", "joined", "left", "player", "note", "events", "new",
    };

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Distinct lowercase search terms: letters required, ≥2 chars, stopwords and <paramref name="ignore"/> removed.</summary>
    public static List<string> Terms(string? text, IEnumerable<string>? ignore = null, int max = MaxTerms)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var ign = IgnoredParts(ignore);
        foreach (var raw in NonWord().Split(text.ToLowerInvariant()))
        {
            if (!IsTerm(raw, ign)) continue;
            if (!result.Contains(raw)) result.Add(raw);
            if (result.Count >= max) break;
        }
        return result;
    }

    private static HashSet<string>? IgnoredParts(IEnumerable<string>? ignore)
    {
        if (ignore == null) return null;
        var ign = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in ignore)
            foreach (var part in NonWord().Split(w.ToLowerInvariant()))
                if (part.Length > 0) ign.Add(part);
        return ign;
    }

    /// <summary>2..40 chars with a letter, not a stopword, not ignored.</summary>
    private static bool IsTerm(string raw, HashSet<string>? ign) =>
        raw.Length >= 2 && raw.Length <= 40 && raw.Any(char.IsLetter) && !Stop.Contains(raw) && !(ign != null && ign.Contains(raw));

    /// <summary>Crude stem for the LIKE fallback ("trees" → "tree", "cobblestone" → "cobblest").</summary>
    public static string Stem(string term) => term.Length <= 4 ? term : term[..Math.Max(4, Math.Min(term.Length - 2, 8))];

    /// <summary>Hash of the "material" text: case, whitespace and numbers ignored, so count/coordinate churn never re-embeds.</summary>
    public static string MaterialHash(string title, string body)
    {
        var s = Digits().Replace((title + "\n" + body).ToLowerInvariant(), "#");
        return VectorMath.Hash(Spaces().Replace(s, " ").Trim());
    }

    public static string Ago(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return "just now";
        if (t < TimeSpan.FromHours(1)) return $"{(int)t.TotalMinutes}m ago";
        if (t < TimeSpan.FromDays(2)) return $"{(int)t.TotalHours}h ago";
        return $"{(int)t.TotalDays}d ago";
    }

    /// <summary>FTS5 MATCH expression: each term quoted (no syntax injection) as a prefix query, OR-ed.</summary>
    public static string FtsQuery(IEnumerable<string> terms)
    {
        var sb = new StringBuilder();
        foreach (var t in terms)
        {
            if (sb.Length > 0) sb.Append(" OR ");
            sb.Append('"').Append(t.Replace("\"", "")).Append("\"*");
        }
        return sb.ToString();
    }
}
