using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Deterministic, network-free embeddings: hashed bag-of-words (FNV) over 256 dims with a tiny synonym map so
/// "ingots"/"iron" land near "metal" — enough to exercise the semantic list independently of FTS.
/// </summary>
public sealed class FakeEmbedder : IEmbeddingProvider
{
    private static readonly Dictionary<string, string> Synonyms = new()
    {
        ["ingot"] = "metal", ["ingots"] = "metal", ["iron"] = "metal",
        ["cobblestone"] = "stone", ["cobble"] = "stone",
    };

    public int Calls;
    public int Texts;
    public bool Fail { get; set; }
    public string Model => "fake-bow";
    public int Dimensions => 256;
    public bool IsAvailable => true;

    public Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (Fail) throw new HttpRequestException("fake outage");
        Interlocked.Add(ref Texts, inputs.Count);
        long tokens = 0;
        var vectors = inputs.Select(text =>
        {
            var v = new float[Dimensions];
            foreach (var raw in System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), "[^a-z]+"))
            {
                if (raw.Length < 3) continue;
                var t = Synonyms.GetValueOrDefault(raw, raw);
                v[(int)(Fnv(t) % (uint)Dimensions)] += 1;
                tokens++;
            }
            return v;
        }).ToList();
        return Task.FromResult(new EmbeddingResponse(vectors, tokens));
    }

    private static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}

public static class Snapshot
{
    /// <summary>Byte-exact rendering of what we'd send (block runtime types included).</summary>
    public static string Of(IEnumerable<LlmMessage> messages) =>
        string.Join("\n", messages.Select(m => m.Role + ":" + string.Join("|", m.Content.Select(b => b.GetType().Name + JsonSerializer.Serialize(b, b.GetType())))));

    public static string Of(IEnumerable<LlmSystemBlock> system) => string.Join("\n", system.Select(s => JsonSerializer.Serialize(s)));

    public static string Of(IEnumerable<LlmToolDefinition> tools) =>
        string.Join("\n", tools.Select(t => t.Name + t.Description + t.InputSchema.GetRawText()));
}
