using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MineClaw.Raptor;
using MineClaw.Raptor.Model;
using MineClaw.Raptor.Numerics;
using MineClaw.Raptor.Text;

namespace MineClaw.Raptor.Tests;

/// <summary>
/// Deterministic bag-of-words embedder: each lowercase token maps to a fixed Gaussian vector seeded by its FNV-1a
/// hash; a text embeds to the normalized sum. Texts that share words are similar, unrelated texts are ~orthogonal.
/// </summary>
public sealed partial class HashingEmbedder(int dimensions = 256) : IEmbedder
{
    private readonly ConcurrentDictionary<string, float[]> _tokens = new();
    public string Model => $"fake-hash-{Dimensions}";
    public int Dimensions { get; } = dimensions;
    public int Calls;
    public long Texts;

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex Word();

    public float[] Embed(string text)
    {
        var v = new float[Dimensions];
        foreach (Match m in Word().Matches(text.ToLowerInvariant()))
        {
            var tv = _tokens.GetOrAdd(m.Value, TokenVector);
            for (int i = 0; i < v.Length; i++) v[i] += tv[i];
        }
        VectorMath.Normalize(v);
        return v;
    }

    private float[] TokenVector(string token)
    {
        uint h = 2166136261;
        foreach (var c in token) { h ^= c; h *= 16777619; }
        var rng = new Rng(h);
        var v = new float[Dimensions];
        for (int i = 0; i < v.Length; i++) v[i] = (float)rng.NextGaussian();
        return v;
    }

    public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        Interlocked.Add(ref Texts, texts.Count);
        return Task.FromResult(new EmbeddingResult(texts.Select(Embed).ToList(), texts.Sum(t => (long)ApproxTokenCounter.Instance.Count(t))));
    }
}

/// <summary>
/// Extractive "summarizer": the context's words ranked by frequency (ties: first occurrence), up to MaxTokens.
/// Shared topic words survive; specific facts survive only while the cluster is small, like a real abstractive summary.
/// </summary>
public sealed partial class KeywordSummarizer : ISummarizer
{
    public int Calls;
    public readonly ConcurrentBag<SummaryRequest> Requests = new();

    [GeneratedRegex(@"[A-Za-z0-9]+")]
    private static partial Regex Word();

    public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        Requests.Add(request);
        var words = Word().Matches(request.Context).Select(m => m.Value.ToLowerInvariant()).ToList();
        var first = new Dictionary<string, int>();
        var freq = new Dictionary<string, int>();
        for (int i = 0; i < words.Count; i++)
        {
            first.TryAdd(words[i], i);
            freq[words[i]] = freq.GetValueOrDefault(words[i]) + 1;
        }
        var ranked = freq.Keys.OrderByDescending(w => freq[w]).ThenBy(w => first[w]);
        var tok = ApproxTokenCounter.Instance;
        var outWords = new List<string>();
        int total = 0;
        foreach (var w in ranked)
        {
            int c = tok.Count(" " + w);
            if (total + c > request.MaxTokens) break;
            outWords.Add(w);
            total += c;
        }
        var text = string.Join(' ', outWords);
        return Task.FromResult(new SummaryResult(text, tok.Count(request.SystemPrompt) + tok.Count(request.UserPrompt), total));
    }
}

/// <summary>Topic / subtopic / fact corpus with exact ground truth for retrieval tests.</summary>
public sealed class SyntheticCorpus
{
    public sealed record Leaf(int Id, int Topic, int Subtopic, string Text, string Query);

    public List<Leaf> Leaves { get; } = [];
    public int Topics { get; }
    public int Subtopics { get; }

    /// <summary>Pseudo-word of letters only (one approx token): prefix + base-26 code.</summary>
    public static string W(char prefix, int code)
    {
        var s = new System.Text.StringBuilder().Append(prefix);
        do { s.Append((char)('a' + code % 26)); code /= 26; } while (code > 0);
        return s.ToString();
    }

    public SyntheticCorpus(int leaves, int topics, int subtopics, int seed = 1)
    {
        Topics = topics;
        Subtopics = subtopics;
        var rng = new Rng(seed);
        for (int i = 0; i < leaves; i++)
        {
            int t = i % topics;
            int s = (i / topics) % subtopics;
            string tw = W('t', t), sw = W('s', t * subtopics + s);
            string f1 = W('f', 3 * i), f2 = W('f', 3 * i + 1), f3 = W('f', 3 * i + 2);
            string w1 = W('w', t * 40 + rng.NextInt(40)), w2 = W('w', t * 40 + rng.NextInt(40));
            var text = $"{tw} {sw} {f1} {f2} {f3} {w1} {w2}.";
            Leaves.Add(new Leaf(i, t, s, text, $"{tw} {f1} {f2}"));
        }
    }

    public IEnumerable<RaptorDocument> Documents(Func<Leaf, string?>? kind = null) =>
        Leaves.Select(l => new RaptorDocument(l.Text, Key: $"leaf:{l.Id}", Kind: kind?.Invoke(l) ?? "fact", Source: $"topic{l.Topic}"));
}

public static class TestOptions
{
    /// <summary>Paper defaults; only parallelism capped so tests do not starve the machine.</summary>
    public static RaptorOptions Default => new() { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) };
}
