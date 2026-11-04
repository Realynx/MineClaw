using System.Text.RegularExpressions;

namespace MineClaw.ViewerBridge.Graph;

/// <summary>
/// Local topic labels for graph nodes: TF-IDF over each node's own text plus (for summaries) the term counts of everything below it,
/// with document frequencies taken per layer so a term every sibling cluster shares (the bot's name, "standing") scores nothing.
/// Deterministic (ties break on the ordinal term), no model calls.
/// </summary>
public static partial class TopicLabeler
{
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_']{2,}")]
    private static partial Regex Word();

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "was", "were", "are", "has", "have", "had", "not", "but", "its", "it's", "his", "her",
        "she", "him", "they", "them", "their", "then", "than", "there", "these", "those", "which", "while", "when", "where", "what", "who", "whom",
        "will", "would", "can", "could", "should", "into", "onto", "over", "under", "about", "after", "before", "between", "each", "every", "also",
        "only", "just", "any", "all", "some", "more", "most", "other", "such", "one", "two", "three", "four", "five", "per", "via", "out", "off",
        "own", "both", "being", "been", "did", "does", "doing", "done", "use", "used", "uses", "using", "make", "makes", "made", "like", "near",
        "around", "including", "include", "includes", "various", "several", "many", "much", "very", "still", "already", "again", "once", "until",
        "bot", "bots", "player", "players", "named", "noted", "reported", "logged", "listed", "recorded", "described", "items", "item", "block",
        "blocks", "times", "time", "list", "lists", "while", "during", "through", "without", "within", "across", "since", "because", "though",
        "you", "your", "our", "we", "i'm", "don't", "doesn't", "isn't", "can't", "won't", "didn't", "yes", "no", "now", "here", "too", "how",
        "why", "may", "might", "must", "get", "got", "set", "put", "see", "seen", "say", "said", "says", "new", "old", "first", "last", "next",
        "summary", "layer", "leaf", "leaves", "note", "notes", "id", "ids", "x1", "x2", "x3",
        "probably", "twice", "repeatedly", "mostly", "mainly", "action", "actions", "account", "accounts", "between", "logged", "times",
        "oct", "nov", "dec", "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept",
        "january", "february", "march", "april", "june", "july", "august", "september", "october", "november", "december",
        "today", "yesterday", "morning", "evening", "night", "later", "earlier", "minutes", "seconds", "hours", "ticks",
        "moved", "working", "need", "needs", "needed", "event", "events", "opening", "hour", "report", "reports", "stretch", "remarking",
        "answering", "appeared", "happened", "remained", "continued", "started", "ended", "tried", "retried", "events", "plus",
        "picked", "opened", "took", "went", "came", "kept", "left", "right", "number", "large", "small", "several", "others",
    };

    /// <summary>
    /// Generic action verbs and activity-log words: they say what kind of line a node holds, never its topic (a graph of gen-mining
    /// episodes otherwise reads "broke"). Labels then name the topics: cobble gen, tree farm, Fox, carousel.
    /// </summary>
    public static readonly IReadOnlySet<string> ActionWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "broke", "broken", "break", "breaking", "mined", "mine", "mining", "chopped", "chop", "chopping", "dug", "dig", "digging", "cleared",
        "harvested", "planted", "placed", "place", "placing", "picked", "pick", "collected", "collect", "stored", "store", "took", "take",
        "taking", "crafted", "craft", "lit", "fought", "hit", "hits", "taken", "got", "gave", "put", "dropped", "switched", "equipped", "used",
        "reorganized", "up", "at", "down", "unknown", "activity", "session", "sessions", "probably", "crew", "slain", "killed",
        "arrived", "joined", "chatted", "said", "says",
    };

    /// <summary>True when the (lower-case) word never labels a topic.</summary>
    public static bool IsStopWord(string k) => Stop.Contains(k) || ActionWords.Contains(k);

    /// <summary>Fills <see cref="GraphNode.Terms"/> for every node.</summary>
    public static void Label(GraphModel m, int maxTerms = 3)
    {
        var terms = Compute(m, maxTerms);
        for (int s = 0; s < m.Nodes.Length; s++) m.Nodes[s].Terms = terms[s];
    }

    /// <summary>The top terms per slot (parallel to <see cref="GraphModel.Nodes"/>).</summary>
    public static string[][] Compute(GraphModel m, int maxTerms = 3)
    {
        int n = m.Nodes.Length;
        var own = new Dictionary<string, int>[n];
        // Display form: the most frequent surface spelling of each lower-cased term (ties: ordinal).
        var forms = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        for (int s = 0; s < n; s++) own[s] = Count(m.Nodes[s].Text, forms);
        var agg = Aggregate(m, own);
        var result = new string[n][];
        foreach (var layerGroup in Enumerable.Range(0, n).GroupBy(s => m.Nodes[s].Layer))
        {
            var members = layerGroup.ToArray();
            var df = DocumentFrequency(members, agg);
            foreach (var s in members) result[s] = TopTerms(agg[s], df, members.Length, maxTerms, forms);
        }
        return result;
    }

    /// <summary>
    /// Term distribution bottom-up (slot descending = layer ascending): a summary blends its own text (40%) with the mean distribution
    /// of its children (60%), so a curated summary and the leaves under it both shape the label.
    /// </summary>
    private static Dictionary<string, double>[] Aggregate(GraphModel m, Dictionary<string, int>[] own)
    {
        int n = m.Nodes.Length;
        var agg = new Dictionary<string, double>[n];
        for (int s = n - 1; s >= 0; s--)
        {
            var g = m.Nodes[s];
            var mine = Normalize(own[s]);
            if (g.Children.Length == 0) { agg[s] = mine; continue; }
            var d = new Dictionary<string, double>(StringComparer.Ordinal);
            double ownShare = mine.Count > 0 ? 0.4 : 0, kidShare = (1 - ownShare) / g.Children.Length;
            foreach (var (k, v) in mine) d[k] = v * ownShare;
            foreach (var c in g.Children)
                foreach (var (k, v) in agg[c]) d[k] = d.GetValueOrDefault(k) + v * kidShare;
            agg[s] = d;
        }
        return agg;
    }

    /// <summary>In how many of a layer's nodes each term "occurs": carries at least 0.1% of the node's distribution (one stray mention
    /// deep in a big subtree doesn't make a word common).</summary>
    private static Dictionary<string, int> DocumentFrequency(int[] members, Dictionary<string, double>[] agg)
    {
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in members)
            foreach (var (k, w) in agg[s])
                if (w >= 0.001) df[k] = df.GetValueOrDefault(k) + 1;
        return df;
    }

    /// <summary>A node's best terms by tf·idf (ids and handles weaker), one per stem, in their display form.</summary>
    private static string[] TopTerms(Dictionary<string, double> dist, Dictionary<string, int> df, int docs, int maxTerms,
        Dictionary<string, Dictionary<string, int>> forms)
    {
        var scored = new List<(string Term, double Score)>();
        foreach (var (k, tf) in dist)
        {
            // Single-document layers (one root) fall back to plain term frequency.
            double idf = docs <= 1 ? 1 : Math.Log((1.0 + docs) / (1.0 + df.GetValueOrDefault(k))) + 0.35;
            if (tf < 1e-4) continue;
            // Ids and handles (digits, underscores) are weaker topic words than plain words.
            double shape = k.Any(ch => char.IsDigit(ch) || ch == '_') ? 0.45 : 1;
            scored.Add((k, tf * idf * shape));
        }
        scored.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : string.CompareOrdinal(a.Term, b.Term));
        var pick = new List<string>(maxTerms);
        var stems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (k, _) in scored)
        {
            if (!stems.Add(Stem(k))) continue;
            pick.Add(Display(k, forms));
            if (pick.Count == maxTerms) break;
        }
        return pick.ToArray();
    }

    private static Dictionary<string, double> Normalize(Dictionary<string, int> counts)
    {
        double total = counts.Values.Sum();
        var d = new Dictionary<string, double>(counts.Count, StringComparer.Ordinal);
        if (total <= 0) return d;
        foreach (var (k, v) in counts) d[k] = v / total;
        return d;
    }

    private static Dictionary<string, int> Count(string text, Dictionary<string, Dictionary<string, int>> forms)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match w in Word().Matches(text))
        {
            var surface = w.Value.TrimEnd('\'');
            var k = surface.ToLowerInvariant();
            if (k.EndsWith("'s", StringComparison.Ordinal)) { k = k[..^2]; surface = surface[..^2]; }
            if (k.Length < 3 || IsStopWord(k) || k.All(char.IsDigit)) continue;
            d[k] = d.GetValueOrDefault(k) + 1;
            if (!forms.TryGetValue(k, out var f)) forms[k] = f = new Dictionary<string, int>(StringComparer.Ordinal);
            f[surface] = f.GetValueOrDefault(surface) + 1;
        }
        return d;
    }

    private static string Stem(string k) => k.Length > 4 && k.EndsWith("es", StringComparison.Ordinal) ? k[..^2]
        : k.Length > 3 && k.EndsWith('s') ? k[..^1] : k;

    private static string Display(string k, Dictionary<string, Dictionary<string, int>> forms) =>
        forms.TryGetValue(k, out var f) ? f.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key : k;
}
