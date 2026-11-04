using System.Text;

namespace MineClaw.Skills;

/// <summary>
/// Zero-token skill lookup. Skills never sit in a tool list: the model sees them only as one-line signatures returned by
/// <c>find_skill</c> or surfaced by auto-retrieval in the newest (uncached) message. This class is the local lexical
/// ranker (works offline, no embeddings); the Bot layer also indexes the same signatures in the RAG store.
/// </summary>
public static class SkillCatalog
{
    public const int MaxSignatureChars = 120;
    /// <summary>find_skill: at most this many signatures…</summary>
    public const int MaxResults = 5;
    /// <summary>…in at most this many chars.</summary>
    public const int MaxResultChars = 600;
    /// <summary>Hints appended to a delegated task: at most this many lines / chars (≈200 tokens).</summary>
    public const int MaxHints = 4, MaxHintChars = 560;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "me", "my", "some", "of", "to", "for", "and", "or", "in", "on", "at", "it", "its", "is", "be",
        "please", "can", "you", "could", "would", "get", "do", "make", "let", "lets", "i", "we", "our", "us",
        "with", "from", "this", "that", "there", "here", "up", "more", "bit", "few", "all", "any", "out", "into", "your",
    };

    /// <summary>Words that mean the same thing in requests ("bigger" → expand).</summary>
    private static readonly Dictionary<string, string> Syn = new(StringComparer.Ordinal)
    {
        ["bigger"] = "expand", ["larger"] = "expand", ["extend"] = "expand", ["widen"] = "expand", ["grow"] = "expand", ["enlarge"] = "expand",
        ["reed"] = "cane", ["sugarcane"] = "cane", ["sugar"] = "cane",
        ["shut"] = "close", ["unlock"] = "open",
        ["gate"] = "gate", ["hatch"] = "trapdoor",
        ["chop"] = "tree", ["log"] = "wood", ["logs"] = "wood",
        ["cook"] = "smelt", ["furnace"] = "smelt", ["bake"] = "smelt",
        ["chest"] = "storage", ["chests"] = "storage", ["store"] = "storage", ["stash"] = "deposit", ["dump"] = "deposit",
        ["sleep"] = "bed", ["night"] = "bed",
        ["wheat"] = "wheat", ["crop"] = "crops", ["crops"] = "crops", ["harvest"] = "harvest", ["farm"] = "farm",
        ["light"] = "torch", ["torches"] = "torch", ["lamp"] = "torch",
        ["platform"] = "floor", ["floor"] = "floor", ["island"] = "island",
        ["foundations"] = "foundation", ["underneath"] = "under", ["beneath"] = "under", ["below"] = "under",
        ["tower"] = "pillar", ["climb"] = "pillar",
        ["portal"] = "portal", ["nether"] = "nether", ["hell"] = "nether",
        ["obby"] = "obsidian",
        ["infinite"] = "infinite", ["endless"] = "infinite",
        ["bonemeal"] = "bonemeal", ["fertilize"] = "bonemeal", ["fertilise"] = "bonemeal",
        ["give"] = "give", ["hand"] = "give", ["pass"] = "give",
        ["wear"] = "equip", ["armour"] = "armor", ["dress"] = "equip",
        ["eat"] = "eat", ["food"] = "eat", ["hungry"] = "eat",
    };

    public static IEnumerable<string> Tokens(string text)
    {
        var sb = new StringBuilder();
        foreach (char ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); continue; }
            if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    public static string Stem(string w)
    {
        if (Syn.TryGetValue(w, out var s)) return s;
        w = StripSuffix(w);
        return Syn.TryGetValue(w, out var s2) ? s2 : w;
    }

    /// <summary>-ing, -ed, -es (not -ses), -s (not -ss): the first that applies.</summary>
    private static string StripSuffix(string w) => StripVerb(w) ?? StripPlural(w) ?? w;

    private static string? StripVerb(string w)
    {
        if (w.Length > 5 && w.EndsWith("ing")) return w[..^3];
        if (w.Length > 4 && w.EndsWith("ed")) return w[..^2];
        return null;
    }

    private static string? StripPlural(string w)
    {
        if (w.Length > 3 && w.EndsWith("es") && !w.EndsWith("ses")) return w[..^2];
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss")) return w[..^1];
        return null;
    }

    private static HashSet<string> Terms(string text) =>
        Tokens(text).Where(t => !Stop.Contains(t)).Select(Stem).ToHashSet(StringComparer.Ordinal);

    /// <summary>Score of one skill for a query (0 = unrelated).</summary>
    public static double Score(SkillSpec s, string query)
    {
        var q = Terms(query);
        if (q.Count == 0) return 0;
        var name = Terms(s.Name.Replace('_', ' '));
        var kw = Terms(s.Keywords);
        var sum = Terms(s.Summary);
        var cat = Stem(s.Category.ToString().ToLowerInvariant());
        double score = 0;
        foreach (var t in q)
        {
            if (name.Contains(t)) score += 3;
            else if (kw.Contains(t)) score += 2;
            else if (sum.Contains(t)) score += 1.5;
            else if (t == cat) score += 0.5;
        }
        if (score == 0) return 0;
        score /= Math.Sqrt(q.Count); // long requests shouldn't outscore short exact ones
        if (s.Status == SkillStatus.Stub) score *= 0.6;
        if (string.Equals(query.Trim().Replace(' ', '_'), s.Name, StringComparison.OrdinalIgnoreCase)) score += 10;
        return score;
    }

    public static IReadOnlyList<SkillSpec> Search(IEnumerable<SkillSpec> specs, string query, int k = MaxResults) =>
        specs.Select(s => (s, score: Score(s, query))).Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.s.Status).ThenBy(x => x.s.Name, StringComparer.Ordinal)
            .Take(Math.Max(0, k)).Select(x => x.s).ToArray();

    /// <summary>Signatures, one per line, within <paramref name="maxCount"/> lines and <paramref name="maxChars"/>.</summary>
    public static string Format(IEnumerable<SkillSpec> specs, int maxCount = MaxResults, int maxChars = MaxResultChars)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var s in specs)
        {
            if (n >= maxCount) break;
            var line = s.Signature;
            if (sb.Length + line.Length + (sb.Length > 0 ? 1 : 0) > maxChars) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
            n++;
        }
        return sb.ToString();
    }

    /// <summary>Hint block for a task ("skills:\n..."), or "" when nothing is relevant. ≤ <see cref="MaxHintChars"/>.</summary>
    public static string Hints(IEnumerable<SkillSpec> specs, string task, double minScore = 2.0)
    {
        var hits = specs.Select(s => (s, score: Score(s, task))).Where(x => x.score >= minScore)
            .OrderByDescending(x => x.score).Take(MaxHints).Select(x => x.s).ToArray();
        if (hits.Length == 0) return "";
        return "skills (call via skill):\n" + Format(hits, MaxHints, MaxHintChars - 26);
    }
}
