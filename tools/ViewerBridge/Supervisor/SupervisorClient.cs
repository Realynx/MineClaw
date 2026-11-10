using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;

namespace MineClaw.ViewerBridge.Supervisor;

/// <summary>
/// Bounded HTTP pools to the bot's Supervisor on loopback: a reserved lane for live motion and health; from 6 connections an
/// interactive lane (story commands, one chest, a body's inventory, recipes); at 7 a lane for the story reads that wait on the
/// colony lock; the remaining connections for ordinary requests. The total stays within SupervisorConnections (the supervisor
/// serves at most 8 requests at once). The token is sent only in <c>X-Supervisor-Token</c>, never a URL. Never logs queries or the token.
/// </summary>
public sealed class SupervisorClient : IDisposable
{
    public const string TokenHeader = "X-Supervisor-Token";
    private readonly HttpClient _http;
    private readonly HttpClient _live;
    private readonly HttpClient _interactive;
    private readonly HttpClient _storyRead;
    private readonly string? _token;
    private double _latencyMs = -1;

    public SupervisorClient(BridgeConfig config, HttpMessageHandler? handler = null)
    {
        _token = config.SupervisorToken;
        BaseUrl = config.SupervisorUrl;
        var lanes = Lanes(config.Bridge.SupervisorConnections);
        bool shared = lanes.Total == 1 || handler != null;
        _http = Client(handler ?? Handler(shared ? lanes.Total : lanes.Ordinary));
        _live = shared ? _http : Client(Handler(lanes.Live));
        _interactive = shared || lanes.Interactive == 0 ? _http : Client(Handler(lanes.Interactive));
        _storyRead = shared || lanes.StoryRead == 0 ? _http : Client(Handler(lanes.StoryRead));
        _commands = new SemaphoreSlim(Math.Max(2, lanes.Interactive));
    }

    /// <summary>Story commands on the wire at once, at most the interactive lane's connections: a command waits here, where the
    /// wait is seen, never unseen in the connection pool.</summary>
    private readonly SemaphoreSlim _commands;

    /// <summary>
    /// The most a story command waits for its turn before it is answered busy without being sent (a definite refusal: the bot
    /// never saw it). The rest of <see cref="SupervisorProxy.StoryCommandTimeout"/> covers the bot's own bounded wait.
    /// </summary>
    public TimeSpan CommandQueueBudget { get; set; } = TimeSpan.FromSeconds(4);

    internal const string CommandQueueBusy = "busy: colony commands before this one are still waiting; it was not sent";

    /// <summary><c>POST api/story/colonies/{id}/{action}</c>.</summary>
    internal static bool IsStoryCommand(HttpMethod method, string relative)
    {
        if (method != HttpMethod.Post) return false;
        var p = Split(relative).Path.Split('/');
        return p.Length == 5 && p[0] == "api" && p[1] == "story" && p[2] == "colonies" && p[3].Length > 0 && p[4].Length > 0;
    }

    /// <summary>The connections of each lane for a configured total (clamped to 1..7). Up to 5 as before the interactive lane:
    /// 1 shared, 2..3 one live, 4..5 two live. 6: live 2, interactive 2, ordinary 2. 7: live 2, interactive 2, story-read 1, ordinary 2.
    /// A lane of 0 sends its requests on the ordinary lane.</summary>
    internal readonly record struct LaneSplit(int Live, int Interactive, int StoryRead, int Ordinary)
    {
        public int Total => Live + Interactive + StoryRead + Ordinary;
    }

    internal static LaneSplit Lanes(int configured)
    {
        int t = Math.Clamp(configured, 1, 7);
        return t switch
        {
            1 => new(0, 0, 0, 1),
            < 4 => new(1, 0, 0, t - 1),
            < 6 => new(2, 0, 0, t - 2),
            6 => new(2, 2, 0, 2),
            _ => new(2, 2, 1, 2),
        };
    }

    private static SocketsHttpHandler Handler(int connections) => new()
    {
        MaxConnectionsPerServer = connections,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AutomaticDecompression = DecompressionMethods.None,
        UseProxy = false,
        AllowAutoRedirect = false,
    };

    private HttpClient Client(HttpMessageHandler handler) =>
        new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = new Uri(BaseUrl + "/") };

    /// <summary>Only quick live reads use the reserve; chunk, index, render and command traffic cannot queue in front of them.</summary>
    internal static bool IsLiveRead(HttpMethod method, string relative)
    {
        if (method != HttpMethod.Get) return false;
        int query = relative.IndexOf('?');
        var path = (query < 0 ? relative : relative[..query]).TrimStart('/');
        return path is "api/world/entities" or "api/world/blocks" or "api/status";
    }

    /// <summary>Human movement and release must not wait behind chunk downloads or expensive inspections.</summary>
    internal static bool IsControlInput(HttpMethod method, string relative)
    {
        if (method != HttpMethod.Post) return false;
        var p = relative.TrimStart('/').Split('/');
        return p.Length == 5 && p[0] == "api" && p[1] == "bodies" && p[2].Length > 0 && p[3] == "control" && p[4] is "input" or "release";
    }

    /// <summary>
    /// What the owner waits on after a click: a story command (<c>POST api/story/colonies/{id}/{action}</c>), one chest
    /// (<c>GET api/world/containers</c> with <c>x=</c>; the box read with <c>x0=</c> is a badge poll and stays ordinary), a body's
    /// inventory and the recipes. None of them may queue behind chunk downloads or the bridge's own pollers.
    /// </summary>
    internal static bool IsInteractive(HttpMethod method, string relative)
    {
        var (path, query) = Split(relative);
        var p = path.Split('/');
        if (method == HttpMethod.Post) return IsStoryCommand(method, relative);
        if (method != HttpMethod.Get) return false;
        if (path == "api/world/containers") return HasKey(query, "x") && !HasKey(query, "x0");
        return path == "api/story/recipes"
            || p.Length == 5 && p[0] == "api" && p[1] == "viewer" && p[2] == "bodies" && p[3].Length > 0 && p[4] == "inventory";
    }

    /// <summary>The story reads that wait on the colony lock: the list (<c>GET api/story</c>) and a colony's state, delta and
    /// Architect (<c>GET api/story/colonies/{id}[/…]</c>). On a lane of their own, nothing on the ordinary lane waits on that lock.</summary>
    internal static bool IsStoryRead(HttpMethod method, string relative)
    {
        if (method != HttpMethod.Get) return false;
        var (path, _) = Split(relative);
        if (path == "api/story") return true;
        var p = path.Split('/');
        return p.Length is 4 or 5 && p[0] == "api" && p[1] == "story" && p[2] == "colonies" && p[3].Length > 0 && p[^1].Length > 0;
    }

    private static (string Path, string Query) Split(string relative)
    {
        int q = relative.IndexOf('?');
        return ((q < 0 ? relative : relative[..q]).TrimStart('/'), q < 0 ? "" : relative[(q + 1)..]);
    }

    private static bool HasKey(string query, string key)
    {
        foreach (var part in query.Split('&'))
        {
            int eq = part.IndexOf('=');
            if ((eq < 0 ? part : part[..eq]) == key) return true;
        }
        return false;
    }

    public string BaseUrl { get; }
    public bool HasToken => !string.IsNullOrEmpty(_token);
    /// <summary>EWMA latency of answered calls (ms), -1 before the first.</summary>
    public double LatencyMs => _latencyMs;
    public DateTimeOffset? LastOk { get; private set; }
    public DateTimeOffset? LastFailure { get; private set; }

    /// <summary>A persistent authenticated channel bypasses the bounded request pools; no per-frame HTTP queue.</summary>
    internal async Task<ClientWebSocket> ConnectSocketAsync(string relative, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (_token is { Length: > 0 }) socket.Options.SetRequestHeader(TokenHeader, _token);
        foreach (var (key, value) in headers) socket.Options.SetRequestHeader(key, value);
        var url = new UriBuilder(new Uri(new Uri(BaseUrl + "/"), relative)) { Scheme = BaseUrl.StartsWith("https:", StringComparison.Ordinal) ? "wss" : "ws" };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try { await socket.ConnectAsync(url.Uri, timeout.Token).ConfigureAwait(false); return socket; }
        catch { socket.Dispose(); throw; }
    }

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
        var gate = IsStoryCommand(method, relative) ? _commands : null;
        if (gate != null) await EnterAsync(gate, cts.Token, ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            var http = IsLiveRead(method, relative) || IsControlInput(method, relative) ? _live
                : IsInteractive(method, relative) ? _interactive
                : IsStoryRead(method, relative) ? _storyRead
                : _http;
            var res = await http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
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
        finally { gate?.Release(); }
    }

    /// <summary>A story command's turn within <see cref="CommandQueueBudget"/> (and the call's own timeout); else busy, unsent.</summary>
    private async Task EnterAsync(SemaphoreSlim gate, CancellationToken timeout, CancellationToken caller)
    {
        bool entered;
        try { entered = await gate.WaitAsync(CommandQueueBudget, timeout).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { entered = false; }
        if (!entered) throw new SupervisorBusyException(CommandQueueBusy);
    }

    /// <summary>A GET buffered fully: status + body bytes (+ the Retry-After header, if any).</summary>
    public async Task<(int Status, byte[] Body, string? ContentType, TimeSpan? RetryAfter)> GetAsync(string relative, TimeSpan timeout, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, relative, null, timeout, stream: false, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return ((int)res.StatusCode, body, res.Content.Headers.ContentType?.ToString(), res.Headers.RetryAfter?.Delta);
    }

    public void Dispose()
    {
        foreach (var lane in new[] { _live, _interactive, _storyRead }.Distinct())
            if (!ReferenceEquals(lane, _http)) lane.Dispose();
        _http.Dispose();
    }
}

public class SupervisorUnavailableException(string message) : Exception(message);

/// <summary>The request was never sent (it waited too long for its turn): nothing reached the bot, so nothing was applied.</summary>
public sealed class SupervisorBusyException(string message) : SupervisorUnavailableException(message);
