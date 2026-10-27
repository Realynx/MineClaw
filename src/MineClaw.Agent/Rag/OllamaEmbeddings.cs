using System.Net;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Rag;

/// <summary>
/// Ollama embeddings over plain HttpClient: <c>POST {Endpoint}/api/embed</c>, body <c>{"model":m,"input":[...]}</c>, reply
/// <c>{"embeddings":[[...]],"prompt_eval_count":n}</c>. The endpoint is <see cref="EmbeddingOptions.Endpoint"/> or, when that is
/// empty, the LLM's <c>Agent:Llm:Ollama:Endpoint</c>. Every vector must have <see cref="EmbeddingOptions.Dimensions"/> entries
/// (a model's size is fixed): a mismatch throws, so one db never mixes dimensions.
/// </summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly EmbeddingOptions _o;
    private readonly OllamaOptions _server;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public OllamaEmbeddingProvider(EmbeddingOptions options, OllamaOptions? llmServer = null, HttpClient? http = null)
    {
        _o = options;
        _server = new OllamaOptions
        {
            Endpoint = !string.IsNullOrWhiteSpace(options.Endpoint) && !options.Endpoint.Contains("YOUR-RESOURCE", StringComparison.OrdinalIgnoreCase)
                ? options.Endpoint : llmServer?.Endpoint ?? "",
            ApiKey = !string.IsNullOrWhiteSpace(options.ApiKey) ? options.ApiKey : llmServer?.ApiKey ?? "",
            KeepAlive = llmServer?.KeepAlive ?? "",
        };
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)) };
    }

    public string Model => _o.Model.Trim();
    public int Dimensions => _o.Dimensions;
    public bool IsAvailable => _o.IsOllamaConfigured && _server.IsConfigured;

    public async Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0) return new EmbeddingResponse([], 0);
        var payload = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["input"] = inputs.Select(s => s.Length > _o.MaxInputChars ? s[.._o.MaxInputChars] : s).ToArray(),
        };
        if (!string.IsNullOrWhiteSpace(_server.KeepAlive)) payload["keep_alive"] = _server.KeepAlive.Trim();
        var body = JsonSerializer.Serialize(payload);
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_server.BaseUri, "api/embed"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            OllamaLlmClient.Authorize(req, _server);
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
            catch (HttpRequestException) when (attempt < _o.MaxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
                continue;
            }
            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(text);
                    return Parse(doc.RootElement, inputs.Count, _o.Dimensions);
                }
                if ((int)resp.StatusCode >= 500 && attempt < _o.MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
                    continue;
                }
                throw new HttpRequestException($"ollama embed HTTP {(int)resp.StatusCode}: {Tools.TextUtil.Truncate(Tools.TextUtil.OneLine(text), 200)}",
                    null, resp.StatusCode);
            }
        }
    }

    internal static EmbeddingResponse Parse(JsonElement root, int expected, int dims)
    {
        if (!root.TryGetProperty("embeddings", out var embs) || embs.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("ollama embed response has no embeddings");
        var vectors = new List<float[]>();
        foreach (var e in embs.EnumerateArray())
        {
            var v = new float[e.GetArrayLength()];
            int i = 0;
            foreach (var x in e.EnumerateArray()) v[i++] = x.GetSingle();
            if (dims > 0 && v.Length != dims)
                throw new InvalidOperationException(
                    $"ollama embedding has {v.Length} dimensions but Agent:Embeddings:Dimensions is {dims}: set it to {v.Length} (that means re-embedding; see Reembed)");
            vectors.Add(v);
        }
        if (vectors.Count != expected) throw new InvalidOperationException($"ollama embed returned {vectors.Count} vectors for {expected} inputs");
        long tokens = root.TryGetProperty("prompt_eval_count", out var p) && p.TryGetInt64(out var n) ? n : 0;
        return new EmbeddingResponse(vectors, tokens);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public static class EmbeddingProviders
{
    /// <summary>The configured provider: Azure (default), Ollama, or none (FTS-only).</summary>
    public static IEmbeddingProvider Create(EmbeddingOptions o, OllamaOptions? llmServer = null)
    {
        if (o.IsOllama)
        {
            var p = new OllamaEmbeddingProvider(o, llmServer);
            return p.IsAvailable ? p : NullEmbeddingProvider.Instance;
        }
        return o.IsConfigured ? new AzureOpenAIEmbeddingProvider(o) : NullEmbeddingProvider.Instance;
    }
}

/// <summary>
/// One db never mixes vector dimensions: when it already holds vectors of another size than the configured provider makes
/// (e.g. 1024-d Azure vectors, now a 768-d Ollama model), embeddings are refused (FTS-only, with a clear message) unless
/// <see cref="EmbeddingOptions.Reembed"/> drops the old vectors so everything re-embeds at the new size.
/// </summary>
public static class EmbeddingDimensionGuard
{
    /// <returns>The provider to use (the same one, or <see cref="NullEmbeddingProvider"/> when refused) and a note for the log.</returns>
    public static (IEmbeddingProvider Provider, string? Note) Check(AgentDb db, IEmbeddingProvider provider, bool reembed)
    {
        if (!provider.IsAvailable || provider.Dimensions <= 0) return (provider, null);
        long other;
        try { other = db.ScalarLong("SELECT COUNT(*) FROM vectors WHERE dims <> $d", ("$d", provider.Dimensions)); }
        catch (Exception) { return (provider, null); } // no vectors table yet
        if (other == 0) return (provider, null);
        long dims = db.ScalarLong("SELECT dims FROM vectors WHERE dims <> $d LIMIT 1", ("$d", provider.Dimensions));
        if (!reembed)
            return (NullEmbeddingProvider.Instance,
                $"embeddings off: the db holds {other} vector(s) of {dims} dimensions but {provider.Model} makes {provider.Dimensions}; " +
                "one db never mixes dimensions. Set Agent:Embeddings:Reembed=true to drop the old vectors and re-embed, or switch back.");
        db.Exec("DELETE FROM vectors WHERE dims <> $d", ("$d", provider.Dimensions));
        try { db.Exec("DELETE FROM embed_cache WHERE dims <> $d", ("$d", provider.Dimensions)); } catch (Exception) { }
        return (provider, $"embeddings: dropped {other} vector(s) of {dims} dimensions; re-embedding with {provider.Model} ({provider.Dimensions})");
    }
}
