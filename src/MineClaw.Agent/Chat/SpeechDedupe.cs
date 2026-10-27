using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>
/// One chat output per addressed message: drops sentences that restate something the bot already said this turn (or an
/// earlier sentence of the same reply), e.g. "Okie foxy, I'll make you a door too~" followed a round later by "I'll make
/// you a door too, foxy~". Similarity = shared content words / the smaller sentence's content words (≥ 0.6, ≥ 2 words;
/// numbers and persona fillers don't count). A reply that OPENS by restating an earlier line is a paraphrase: dropped whole.
/// </summary>
public static class SpeechDedupe
{
    public const double Threshold = 0.6;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "but", "for", "you", "your", "with", "that", "this", "just", "i'm", "i'll", "it's", "its", "are", "was", "will",
        "can", "have", "has", "not", "too", "all", "any", "out", "now", "here", "there", "then", "okie", "okay", "uwu", "owo", "nya",
        "foxy", "hehe", "yeah", "yep", "sure", "thing", "got", "like", "some", "one", "it'll", "i've", "we'll",
    };

    /// <summary>The text without sentences already said (in <paramref name="said"/>) or repeated within itself; "" if nothing new.</summary>
    public static string DropRepeats(string text, IEnumerable<string> said, out List<string> dropped)
    {
        dropped = new List<string>();
        var seen = said.SelectMany(Sentences).Select(Words).Where(w => w.Count > 0).ToList();
        var sentences = Sentences(text).ToList();
        if (sentences.Count > 0 && seen.Count > 0 && Words(sentences[0]) is { Count: >= 2 } first && seen.Any(p => Similar(p, first)))
        {
            dropped.AddRange(sentences);
            return "";
        }
        var kept = new List<string>();
        foreach (var s in sentences)
        {
            var w = Words(s);
            if (w.Count >= 2 && seen.Any(p => Similar(p, w))) { dropped.Add(s); continue; }
            kept.Add(s);
            if (w.Count > 0) seen.Add(w);
        }
        return string.Join(" ", kept).Trim();
    }

    /// <summary>True when <paramref name="text"/> brings nothing new over <paramref name="said"/>.</summary>
    public static bool IsRepeat(string text, IEnumerable<string> said) => text.Trim().Length > 0 && DropRepeats(text, said, out _).Length == 0;

    private static bool Similar(HashSet<string> a, HashSet<string> b)
    {
        int inter = a.Count(b.Contains);
        int min = Math.Min(a.Count, b.Count);
        return inter >= 2 && min > 0 && inter / (double)min >= Threshold;
    }

    private static IEnumerable<string> Sentences(string text) =>
        Regex.Split(text.Trim(), @"(?<=[.!?~])\s+").Select(s => s.Trim()).Where(s => s.Length > 0);

    private static HashSet<string> Words(string s) =>
        Regex.Matches(s.ToLowerInvariant(), "[a-z][a-z']{2,}").Select(m => m.Value.Trim('\''))
            .Where(w => w.Length > 0 && !Stop.Contains(w)).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
