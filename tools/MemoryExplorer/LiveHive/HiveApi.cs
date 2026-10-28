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

    /// <summary>Over a given client (tests: a fake handler).</summary>
    internal HiveApi(SupervisorEndpoint endpoint, HttpClient http)
    {
        Endpoint = endpoint;
        _http = http;
    }

    public SupervisorEndpoint Endpoint { get; }
    /// <summary>Smoothed round-trip in ms (−1 = none yet).</summary>
    public double LatencyMs { get { lock (_lock) return _latency; } }
    public long BytesIn;

    public bool BackingOff(string route)
    {
        lock (_lock) return _backoff.TryGetValue(route, out var until) && DateTime.UtcNow < until;
    }

    /// <summary>Per-request time limits by route (a hung request is cancelled and backed off, never awaited forever).</summary>
    public static TimeSpan TimeoutFor(string route) => route switch
    {
        "entities" => TimeSpan.FromSeconds(3),
        "chunks" => TimeSpan.FromSeconds(10),
        "view" => TimeSpan.FromSeconds(6),
        "script" or "scripts" => TimeSpan.FromSeconds(2.5), // the live script debugger polls at 4 Hz: a slow answer is skipped, not awaited
        _ => TimeSpan.FromSeconds(5),
    };

    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly Random _jitter = new();

    /// <summary>Consecutive failures of a route → exponential back-off with ±25 % jitter (0.5 s … 15 s; 404s 2 s … 30 s), so a
    /// broken route is retried slowly instead of every pass, and one success clears it.</summary>
    private TimeSpan Fail(string route, int status, TimeSpan? retryAfter)
    {
        lock (_lock)
        {
            int n = _failures.GetValueOrDefault(route) + 1;
            _failures[route] = n;
            // A 404 is usually an older server without the route (it climbs to 30 s), but can be one bot that just went offline
            // (/view/{bot}): start at 2 s so a one-off doesn't park the whole route.
            double baseS = status == 404 ? Math.Min(30, 2 * Math.Pow(2, n - 1)) : Math.Min(15, 0.5 * Math.Pow(2, n - 1));
            var wait = retryAfter ?? TimeSpan.FromSeconds(baseS * (0.75 + _jitter.NextDouble() * 0.5));
            _backoff[route] = DateTime.UtcNow + wait;
            return wait;
        }
    }

    private void Ok(string route)
    {
        lock (_lock) { _failures.Remove(route); _backoff.Remove(route); }
    }

    public async Task<byte[]> GetBytesAsync(string route, string pathAndQuery, CancellationToken ct, bool track = false)
    {
        if (BackingOff(route)) throw new HiveApiException(429, $"{route}: backing off");
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutFor(route));
        HttpResponseMessage res;
        byte[] body;
        try
        {
            res = await _http.GetAsync(pathAndQuery, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { Fail(route, 0, null); throw new HiveApiException(0, $"{route}: {ex.InnerException?.Message ?? ex.Message}"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Fail(route, 0, null); throw new HiveApiException(0, $"{route}: timed out"); }
        using (res)
        {
            try { body = await res.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false); }
            catch (HttpRequestException ex) { Fail(route, 0, null); throw new HiveApiException(0, $"{route}: {ex.InnerException?.Message ?? ex.Message}"); }
            catch (IOException ex) { Fail(route, 0, null); throw new HiveApiException(0, $"{route}: {ex.Message}"); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Fail(route, 0, null); throw new HiveApiException(0, $"{route}: timed out"); }
            Interlocked.Add(ref BytesIn, body.Length);
            if (track)
                lock (_lock) _latency = _latency < 0 ? sw.Elapsed.TotalMilliseconds : _latency * 0.8 + sw.Elapsed.TotalMilliseconds * 0.2;
            int code = (int)res.StatusCode;
            if (res.IsSuccessStatusCode) { Ok(route); return body; }
            TimeSpan? retry = res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                ? res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1) : null;
            // Auth problems are reported, not backed off (the fast loop shows them and retries every few seconds).
            if (code is not (401 or 403)) retry = Fail(route, code, retry);
            throw new HiveApiException(code, route + ": " + code switch
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
        try { return HiveJson.Parse<T>(body) ?? throw new HiveApiException(200, route + ": empty reply"); }
        catch (System.Text.Json.JsonException ex) { Fail(route, 200, null); throw new HiveApiException(200, $"{route}: bad reply ({ex.Message})"); }
    }

    private static string I(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public Task<EntitiesDto> EntitiesAsync(CancellationToken ct) => GetJsonAsync<EntitiesDto>("entities", "api/world/entities", ct, track: true);

    /// <summary>The entity delta stream: what changed after revision <paramref name="since"/> (0 = everything).</summary>
    public Task<EntitiesDto> EntitiesSinceAsync(long since, CancellationToken ct) =>
        GetJsonAsync<EntitiesDto>("entities", $"api/world/entities?since={Math.Max(0, since).ToString(CultureInfo.InvariantCulture)}", ct, track: true);

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
