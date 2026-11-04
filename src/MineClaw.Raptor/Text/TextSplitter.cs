using System.Text.RegularExpressions;

namespace MineClaw.Raptor.Text;

/// <summary>
/// Port of utils.py <c>split_text</c>: split on ". ! ? \n", pack whole sentences into chunks of ≤ max_tokens
/// (counting " " + sentence), sentences longer than max_tokens are split on ", ; :" and packed the same way,
/// optional sentence overlap.
/// </summary>
public static partial class TextSplitter
{
    [GeneratedRegex(@"\.|!|\?|\n")]
    private static partial Regex ReferenceDelims();

    // Keep the delimiter on its sentence; a '.' between two digits (12.5) is not a boundary.
    [GeneratedRegex(@"(?<=[!?\n])|(?<=(?<!\d)\.)|(?<=\.)(?!\d)")]
    private static partial Regex PreservingDelims();

    [GeneratedRegex(@"[,;:]")]
    private static partial Regex SubDelims();

    public static List<string> Split(string text, ITokenCounter tokenizer, int maxTokens, int overlap = 0, SplitMode mode = SplitMode.Reference)
    {
        var sentences = mode == SplitMode.Reference ? ReferenceDelims().Split(text) : SplitPreserving(text);
        var nTokens = sentences.Select(s => tokenizer.Count(" " + s)).ToArray();
        var chunks = new List<string>();
        var current = new List<string>();
        int currentLength = 0;

        for (int si = 0; si < sentences.Length; si++)
        {
            var sentence = mode == SplitMode.Reference ? sentences[si] : sentences[si].Trim();
            int tokenCount = nTokens[si];
            if (string.IsNullOrWhiteSpace(sentence)) continue;
            if (tokenCount > maxTokens)
            {
                SplitLongSentence(sentence, tokenizer, maxTokens, overlap, chunks);
                continue;
            }
            if (currentLength + tokenCount > maxTokens)
                currentLength = Flush(chunks, ref current, nTokens, overlap);
            current.Add(sentence);
            currentLength += tokenCount;
        }
        if (current.Count > 0) chunks.Add(string.Join(" ", current));
        return chunks;
    }

    /// <summary>
    /// Emits the current chunk and keeps the overlap tail; returns the reference's carried length
    /// sum(n_tokens[max(0, len(current_chunk) - overlap):len(current_chunk)]).
    /// </summary>
    private static int Flush(List<string> chunks, ref List<string> current, int[] counts, int overlap)
    {
        chunks.Add(string.Join(" ", current));
        current = overlap > 0 ? current.Skip(Math.Max(0, current.Count - overlap)).ToList() : [];
        return SumRange(counts, Math.Max(0, current.Count - overlap), current.Count);
    }

    /// <summary>A sentence over max_tokens: split on ", ; :" and pack the pieces the same way.</summary>
    private static void SplitLongSentence(string sentence, ITokenCounter tokenizer, int maxTokens, int overlap, List<string> chunks)
    {
        var subs = SubDelims().Split(sentence).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        var subCounts = subs.Select(s => tokenizer.Count(" " + s)).ToArray();
        var sub = new List<string>();
        int subLength = 0;
        for (int t = 0; t < subs.Length; t++)
        {
            if (subLength + subCounts[t] > maxTokens && sub.Count > 0)
                subLength = Flush(chunks, ref sub, subCounts, overlap);
            sub.Add(subs[t]);
            subLength += subCounts[t];
        }
        if (sub.Count > 0) chunks.Add(string.Join(" ", sub));
    }

    private static string[] SplitPreserving(string text) => PreservingDelims().Split(text);

    private static int SumRange(int[] a, int from, int to)
    {
        int s = 0;
        for (int i = from; i < to && i < a.Length; i++) s += a[i];
        return s;
    }
}
