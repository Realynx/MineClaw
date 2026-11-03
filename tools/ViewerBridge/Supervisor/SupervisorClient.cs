using System.Diagnostics;
using System.Net;

namespace MineClaw.ViewerBridge.Supervisor;

/// <summary>
/// The one HttpClient to the bot's Supervisor on loopback: a small connection pool (the supervisor serves at most 8 requests at
/// once, and the bot's own page needs some), the token in the <c>X-Supervisor-Token</c> header only (never <c>?token=</c>),
/// per-call timeouts. Never logs a URL with a query or the token.
/// </summary>
public sealed class SupervisorClient : IDisposable
{
    public const string TokenHeader = "X-Supervisor-Token";
    private readonly HttpClient _http;
    private readonly string? _token;
    private double _latencyMs = -1;

    public SupervisorClient(BridgeConfig config, HttpMessageHandler? handler = null)
    {
        _token = config.SupervisorToken;
        BaseUrl = config.SupervisorUrl;
        handler ??= new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Clamp(config.Bridge.SupervisorConnections, 1, 7),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false,
            AllowAutoRedirect = false,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = new Uri(BaseUrl + "/") };
    }

    public string BaseUrl { get; }
    public bool HasToken => !string.IsNullOrEmpty(_token);
    /// <summary>EWMA latency of answered calls (ms), -1 before the first.</summary>
    public double LatencyMs => _latencyMs;
    public DateTimeOffset? LastOk { get; private set; }
    public DateTimeOffset? LastFailure { get; private set; }

    /// <summary>
    /// Sends a request (path relative to the base, e.g. <c>api/status?bot=main</c>). The caller owns the response; with
    /// <paramref name="stream"/> the body is not buffered. The timeout covers the whole call when not streaming, and the
    /// headers when streaming (the caller's token bounds the body copy).
    /// </summary>
    /// <param name="headers">Extra request headers (the node proxy's signed identity: <c>X-MCX-User</c>, <c>X-MCX-Roles</c>).</param>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relative, HttpContent? content, TimeSpan timeout, bool stream, CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var req = new HttpRequestMessage(method, relative) { Content = content };
        if (_token is { Length: > 0 }) req.Headers.TryAddWithoutValidation(TokenHeader, _token);
        if (headers != null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        req.Headers.Accept.ParseAdd("application/json, image/png, application/octet-stream");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            var res = await _http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            var ms = sw.Elapsed.TotalMilliseconds;
            _latencyMs = _latencyMs < 0 ? ms : _latencyMs * 0.8 + ms * 0.2;
            LastOk = DateTimeOffset.UtcNow;
            return res;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LastFailure = DateTimeOffset.UtcNow;
            throw new SupervisorUnavailableException("the bot did not answer in time");
        }
        catch (HttpRequestException)
        {
            LastFailure = DateTimeOffset.UtcNow;
            throw new SupervisorUnavailableException("the bot is not reachable");
        }
    }

    /// <summary>A GET buffered fully: status + body bytes (+ the Retry-After header, if any).</summary>
    public async Task<(int Status, byte[] Body, string? ContentType, TimeSpan? RetryAfter)> GetAsync(string relative, TimeSpan timeout, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, relative, null, timeout, stream: false, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return ((int)res.StatusCode, body, res.Content.Headers.ContentType?.ToString(), res.Headers.RetryAfter?.Delta);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class SupervisorUnavailableException(string message) : Exception(message);
