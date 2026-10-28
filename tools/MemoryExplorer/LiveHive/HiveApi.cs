using System.Diagnostics;
using System.Globalization;
using System.Net;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.LiveHive;

public enum LinkState { Disconnected, Connecting, Connected, Unauthorized, Error }

/// <summary>A failed call: the HTTP status (0 = no answer) and, for 429/503, how long to back off.</summary>
public sealed class HiveApiException(int status, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public int Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// The Live Hive's HTTP client for the Supervisor (GET only). The token rides in the header via <see cref="SupervisorEndpoint"/>
/// and is never read here. Tracks the round-trip latency (EWMA) and per-route back-off after 429/503 (Retry-After).
/// </summary>
public sealed class HiveApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, DateTime> _backoff = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private double _latency = -1;

    public HiveApi(SupervisorEndpoint endpoint)
    {
        Endpoint = endpoint;
        _http = endpoint.CreateClient(TimeSpan.FromSeconds(6));
    }

    public SupervisorEndpoint Endpoint { get; }
    /// <summary>Smoothed round-trip in ms (−1 = none yet).</summary>
    public double LatencyMs { get { lock (_lock) return _latency; } }
    public long BytesIn;

    public bool BackingOff(string route)
    {
        lock (_lock) return _backoff.TryGetValue(route, out var until) && DateTime.UtcNow < until;
    }

    public async Task<byte[]> GetBytesAsync(string route, string pathAndQuery, CancellationToken ct, bool track = false)
    {
        if (BackingOff(route)) throw new HiveApiException(429, "backing off");
        var sw = Stopwatch.StartNew();
        HttpResponseMessage res;
        try { res = await _http.GetAsync(pathAndQuery, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new HiveApiException(0, ex.InnerException?.Message ?? ex.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new HiveApiException(0, "timed out"); }
        using (res)
        {
            var body = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            Interlocked.Add(ref BytesIn, body.Length);
            if (track)
                lock (_lock) _latency = _latency < 0 ? sw.Elapsed.TotalMilliseconds : _latency * 0.8 + sw.Elapsed.TotalMilliseconds * 0.2;
            int code = (int)res.StatusCode;
            if (res.IsSuccessStatusCode) return body;
            TimeSpan? retry = null;
            if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                retry = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                lock (_lock) _backoff[route] = DateTime.UtcNow + retry.Value;
            }
            throw new HiveApiException(code, code switch
            {
                401 or 403 => "unauthorized: token missing or wrong",
                404 => "not found (is the server new enough for the Live Hive routes?)",
                429 => "rate limited",
                503 => "server busy",
                _ => $"HTTP {code}",
            }, retry);
        }
    }

    public async Task<T> GetJsonAsync<T>(string route, string pathAndQuery, CancellationToken ct, bool track = false)
    {
        var body = await GetBytesAsync(route, pathAndQuery, ct, track).ConfigureAwait(false);
        return HiveJson.Parse<T>(body) ?? throw new HiveApiException(200, "empty reply");
    }

    private static string I(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public Task<EntitiesDto> EntitiesAsync(CancellationToken ct) => GetJsonAsync<EntitiesDto>("entities", "api/world/entities", ct, track: true);

    public Task<IndexDto> IndexAsync(int dim, int x, int z, int r, CancellationToken ct) =>
        GetJsonAsync<IndexDto>("index", $"api/world/index?dim={dim}&x={x}&z={z}&r={r}", ct);

    public Task<byte[]> ChunksAsync(int dim, IEnumerable<(int Cx, int Cz)> cols, CancellationToken ct) =>
        GetBytesAsync("chunks", $"api/world/chunks?dim={dim}&cols={string.Join(';', cols.Select(c => $"{c.Cx},{c.Cz}"))}", ct);

    public Task<OverlaysDto> OverlaysAsync(int dim, CancellationToken ct) => GetJsonAsync<OverlaysDto>("overlays", $"api/world/overlays?dim={dim}", ct);

    /// <summary>A bot's server-rendered view PNG (iso/top/side/...; the server caches each ~1 s).</summary>
    public Task<byte[]> ViewPngAsync(string bot, string mode, CancellationToken ct) =>
        GetBytesAsync("view", $"view/{Uri.EscapeDataString(bot)}?mode={Uri.EscapeDataString(mode)}", ct);

    public async Task<LocateDto?> LocateAsync(string q, CancellationToken ct)
    {
        try { return await GetJsonAsync<LocateDto>("locate", $"api/locate?q={Uri.EscapeDataString(q)}", ct).ConfigureAwait(false); }
        catch (HiveApiException e) when (e.Status == 404) { return null; }
    }

    public static string Coords(double x, double y, double z) => $"{I(x)},{I(y)},{I(z)}";

    public void Dispose() => _http.Dispose();
}
