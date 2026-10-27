using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MineClaw.Agent.Rag;

public sealed record EmbeddingResponse(IReadOnlyList<float[]> Vectors, long Tokens);

/// <summary>Turns text into vectors. Must be safe to call concurrently; may throw on failure (callers degrade to FTS-only).</summary>
public interface IEmbeddingProvider
{
    /// <summary>Model id used in cache keys (changing it re-embeds everything once).</summary>
    string Model { get; }
    int Dimensions { get; }
    /// <summary>False when unconfigured: the index runs FTS-only and never calls <see cref="EmbedAsync"/>.</summary>
    bool IsAvailable { get; }
    Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct);
}

public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public static NullEmbeddingProvider Instance { get; } = new();
    public string Model => "none";
    public int Dimensions => 0;
    public bool IsAvailable => false;
    public Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct) =>
        throw new InvalidOperationException("embeddings not configured");
}

/// <summary>
/// Azure OpenAI / AI Foundry embeddings over plain HttpClient:
/// <c>POST {Endpoint}/openai/deployments/{Deployment}/embeddings?api-version={ApiVersion}</c>, header <c>api-key</c>,
/// body <c>{"input":[...],"dimensions":N}</c>. Retries 429/5xx with backoff (honours Retry-After).
/// </summary>
public sealed class AzureOpenAIEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly EmbeddingOptions _o;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _uri;

    public AzureOpenAIEmbeddingProvider(EmbeddingOptions options, HttpClient? http = null)
    {
        _o = options;
        _ownsHttp = http == null;
        _http = http ?? new HttpClient();
        if (_ownsHttp) _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
        _uri = BuildUri(options);
    }

    public static Uri BuildUri(EmbeddingOptions o)
    {
        var endpoint = o.Endpoint.Trim().TrimEnd('/');
        if (endpoint.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)) endpoint = endpoint[..^"/openai".Length];
        return new Uri($"{endpoint}/openai/deployments/{Uri.EscapeDataString(o.Deployment.Trim())}/embeddings?api-version={Uri.EscapeDataString(o.ApiVersion.Trim())}");
    }

    public string Model => $"{_o.Deployment}";
    public int Dimensions => _o.Dimensions;
    public bool IsAvailable => _o.IsConfigured;

    public async Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0) return new EmbeddingResponse([], 0);
        var body = JsonSerializer.Serialize(new
        {
            input = inputs.Select(s => s.Length > _o.MaxInputChars ? s[.._o.MaxInputChars] : s).ToArray(),
            dimensions = _o.Dimensions,
        });

        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("api-key", _o.ApiKey);
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < _o.MaxRetries)
            {
                await Task.Delay(Backoff(attempt, null), ct).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < _o.MaxRetries)
            {
                await Task.Delay(Backoff(attempt, null), ct).ConfigureAwait(false); // HttpClient timeout
                continue;
            }

            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                    return Parse(doc.RootElement, inputs.Count);
                }
                bool retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                if (retryable && attempt < _o.MaxRetries)
                {
                    await Task.Delay(Backoff(attempt, resp.Headers.RetryAfter), ct).ConfigureAwait(false);
                    continue;
                }
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"embeddings HTTP {(int)resp.StatusCode}: {Tools.TextUtil.Truncate(Tools.TextUtil.OneLine(text), 200)}",
                    null, resp.StatusCode);
            }
        }
    }

    internal static EmbeddingResponse Parse(JsonElement root, int expected)
    {
        var vectors = new float[expected][];
        foreach (var item in root.GetProperty("data").EnumerateArray())
        {
            int index = item.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
            var emb = item.GetProperty("embedding");
            var v = new float[emb.GetArrayLength()];
            int i = 0;
            foreach (var x in emb.EnumerateArray()) v[i++] = x.GetSingle();
            if (index >= 0 && index < expected) vectors[index] = v;
        }
        if (vectors.Any(v => v == null)) throw new InvalidOperationException("embeddings response missing items");
        long tokens = 0;
        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("total_tokens", out var t)) tokens = t.GetInt64();
            else if (u.TryGetProperty("prompt_tokens", out var p)) tokens = p.GetInt64();
        }
        return new EmbeddingResponse(vectors, tokens);
    }

    private static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } d) return d > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : d;
        return TimeSpan.FromMilliseconds(Math.Min(8000, 500 * Math.Pow(2, attempt)) + Random.Shared.Next(0, 250));
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>Allocation-light vector helpers (vectors are stored L2-normalized, so cosine = dot).</summary>
public static class VectorMath
{
    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        if (sum <= 0) return v;
        float inv = (float)(1.0 / Math.Sqrt(sum));
        var r = new float[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = v[i] * inv;
        return r;
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = Math.Min(a.Length, b.Length);
        float sum = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..n]);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..n]);
            for (int k = 0; k < va.Length; k++) acc += va[k] * vb[k];
            sum = Vector.Dot(acc, Vector<float>.One);
            i = va.Length * Vector<float>.Count;
        }
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    public static byte[] ToBlob(float[] v) => MemoryMarshal.AsBytes(v.AsSpan()).ToArray();

    public static float[] FromBlob(byte[] blob) => MemoryMarshal.Cast<byte, float>(blob.AsSpan()).ToArray();

    public static string Hash(string text)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), h);
        return Convert.ToHexString(h[..16]);
    }
}
