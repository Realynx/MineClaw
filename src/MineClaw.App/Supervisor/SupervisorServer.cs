using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.App.Supervisor;

/// <summary>One HTTP request as the router sees it (the listener glue and the tests both build these).</summary>
/// <param name="Body">POST body (admin routes only; capped at <see cref="SupervisorAdminRoutes.MaxBodyBytes"/>).</param>
public sealed record SupervisorRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query, string? TokenHeader = null, string? Body = null);

public sealed record SupervisorReply(int Status, string ContentType, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// The hive-mind supervisor endpoint (read-only). Routes:
/// <list type="bullet">
/// <item><c>GET /</c> the UI; <c>GET /api/bots</c> live tiles; <c>GET /api/status?bot=</c> the TUI panels; <c>GET /api/events?since=</c> the feed;
/// <c>GET /api/locate?q=</c> a camera jump target</item>
/// <item><c>GET /view/{bot}?mode=sheet|brain|eye|top|iso|side|layers&amp;radius=</c>, <c>GET /view/hive</c>, <c>GET /camera?x&amp;y&amp;z&amp;yaw&amp;pitch&amp;dim&amp;w&amp;h</c>
/// (PNG; <c>meta=1</c> = the legend, token estimate and VisionRouter facts of the same cached render)</item>
/// </list>
/// Anything but GET/HEAD is 405, except the admin-write routes (<see cref="SupervisorAdminRoutes"/>, off by default). With
/// <see cref="SupervisorOptions.Token"/> set, every route except the page needs it.
/// </summary>
public sealed class SupervisorServer : IAsyncDisposable
{
    private readonly SupervisorOptions _o;
    private readonly ISupervisorData _data;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly RenderCache<ViewImage> _cache;
    private readonly SemaphoreSlim _connections;
    private readonly byte[]? _token;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SupervisorServer(SupervisorOptions o, ISupervisorData data, ILogger? log = null, TimeProvider? time = null, RenderGate? gate = null)
    {
        _o = o;
        _data = data;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _cache = new RenderCache<ViewImage>(_time);
        _connections = new SemaphoreSlim(Math.Max(1, o.MaxConnections));
        _token = string.IsNullOrEmpty(o.Token) ? null : Encoding.UTF8.GetBytes(o.Token);
        Gate = gate ?? new RenderGate(o.MinRenderIntervalMs, o.MaxQueuedRenders);
    }

    public RenderGate Gate { get; }
    /// <summary>The Live Hive's read-only <c>/api/world/*</c> routes (column index, chunk payloads, poses, overlays); null = off.</summary>
    public SupervisorWorldRoutes? World { get; set; }
    /// <summary>Admin writes (<c>/api/admin/*</c>, Supervisor:AllowAdminWrites + token header); null = every admin route is 403.</summary>
    public ISupervisorAdmin? Admin { get; set; }
    public string? Prefix { get; private set; }

    /// <summary>Starts listening on Bind:Port. Throws when the address can't be bound (the host logs and runs without it).</summary>
    public void Start()
    {
        var host = string.IsNullOrWhiteSpace(_o.Bind) ? "*" : _o.Bind.Trim();
        Prefix = $"http://{host}:{_o.Port}/";
        var l = new HttpListener();
        l.Prefixes.Add(Prefix);
        l.Start();
        _listener = l;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(l, _cts.Token));
        _log.LogInformation("supervisor: read-only viewer on {Prefix}{Auth}", Prefix, _token != null ? " (token required)" : "");
    }

    private async Task AcceptLoopAsync(HttpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested || !l.IsListening) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "supervisor: accept failed"); continue; }
            _ = Task.Run(() => ServeAsync(ctx, ct), ct);
        }
    }

    private async Task ServeAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var res = ctx.Response;
        try
        {
            SupervisorReply reply;
            if (!await _connections.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
                reply = Json(503, new { error = "busy" });
            else
                try
                {
                    var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var nv = ctx.Request.QueryString;
                    foreach (var k in nv.AllKeys) if (k != null && nv[k] is { } v) q[k] = v;
                    var body = await SupervisorAdminRoutes.ReadBodyAsync(ctx.Request, ct).ConfigureAwait(false);
                    var req = new SupervisorRequest(ctx.Request.HttpMethod, ctx.Request.Url?.AbsolutePath ?? "/", q, ctx.Request.Headers["X-Supervisor-Token"], body);
                    reply = await HandleAsync(req, ct).ConfigureAwait(false);
                }
                finally { _connections.Release(); }
            res.StatusCode = reply.Status;
            res.ContentType = reply.ContentType;
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["Referrer-Policy"] = "no-referrer";
            if (reply.Headers != null) foreach (var (k, v) in reply.Headers) res.Headers[k] = v;
            res.ContentLength64 = reply.Body.Length;
            if (!ctx.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                await res.OutputStream.WriteAsync(reply.Body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug(ex, "supervisor: request failed"); } // never log the URL: it may carry the token
        finally
        {
            try { res.Close(); } catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------ routing (no sockets: the tests call this)

    public async Task<SupervisorReply> HandleAsync(SupervisorRequest r, CancellationToken ct = default)
    {
        if (SupervisorAdminRoutes.IsAdminPath(r.Path)) return await SupervisorAdminRoutes.HandleAsync(r, _o, Admin, ct).ConfigureAwait(false);
        bool get = r.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || r.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        if (!get) return Json(405, new { error = "read-only: GET only" }, new Dictionary<string, string> { ["Allow"] = "GET, HEAD" });
        var path = r.Path.TrimEnd('/');
        if (path.Length == 0 || path == "/index.html") return new SupervisorReply(200, "text/html; charset=utf-8", Page());
        if (path == "/favicon.ico") return new SupervisorReply(204, "image/x-icon", []);
        if (!Authorized(r)) return Json(401, new { error = "token required (X-Supervisor-Token header or ?token=)" });
        if (World != null && await World.TryHandleAsync(r, path, ct).ConfigureAwait(false) is { } worldReply) return worldReply;
        try
        {
            switch (path)
            {
                case "/api/bots":
                    return Json(200, new { at = _time.GetUtcNow(), bots = _data.Bots() });
                case "/api/status":
                    return Json(200, _data.Status(Q(r, "bot")));
                case "/api/events":
                    return Json(200, Events(long.TryParse(Q(r, "since"), out var since) ? since : 0));
                case "/api/locate":
                    return Q(r, "q") is { Length: > 0 } query && _data.Locate(query) is { } hit ? Json(200, hit) : Json(404, new { error = "not found" });
                case "/view/hive":
                    {
                        var img = await _cache.GetAsync("hive", TimeSpan.FromMilliseconds(_o.HiveCacheMs), () => _data.HiveAsync(Gate, ct)).ConfigureAwait(false);
                        return Image(img, r);
                    }
                case "/camera":
                    {
                        if (CameraQueryOf(r) is not { } cq) return Json(400, new { error = "camera needs x, y, z (yaw, pitch, dim, w, h optional)" });
                        var key = string.Create(CultureInfo.InvariantCulture, $"cam:{cq.Dim}:{cq.X:0.0}:{cq.Y:0.0}:{cq.Z:0.0}:{cq.Yaw:0.0}:{cq.Pitch:0.0}:{cq.W}x{cq.H}");
                        var img = await _cache.GetAsync(key, TimeSpan.FromMilliseconds(_o.CacheMs), () => _data.CameraAsync(cq, Gate, ct)).ConfigureAwait(false);
                        return Image(img, r);
                    }
            }
            // Designs (blueprints): the list, one design's bill/eval as JSON, its preview PNG (meta=1 = the bill).
            if (path == "/api/blueprints") return Json(200, new { designs = _data.Designs() });
            if (path.StartsWith("/api/blueprints/", StringComparison.Ordinal))
                return long.TryParse(path["/api/blueprints/".Length..], out var bid) && _data.DesignBill(bid) is { } bill ? Json(200, bill) : Json(404, new { error = "no such design" });
            // Build plans: the slicer's steps, techniques, scaffolding and progress (HTML page; ?meta=1 = the data).
            if (path.StartsWith("/view/buildplan/", StringComparison.Ordinal) || path.StartsWith("/api/buildplans/", StringComparison.Ordinal))
            {
                var tail = path[(path.IndexOf('/', 1) + 1)..];
                tail = tail[(tail.IndexOf('/') + 1)..];
                if (!long.TryParse(tail, out var pid)) return Json(400, new { error = "design id?" });
                if (path.StartsWith("/api/", StringComparison.Ordinal) || Q(r, "meta") is "1" or "true")
                    return _data.BuildPlanData(pid) is { } data ? Json(200, data) : Json(404, new { error = "no build plan for that design" });
                return _data.BuildPlanPage(pid) is { } html
                    ? new SupervisorReply(200, "text/html; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(html), null)
                    : Json(404, new { error = "no build plan for that design" });
            }
            if (path.StartsWith("/view/blueprint/", StringComparison.Ordinal))
            {
                if (!long.TryParse(path["/view/blueprint/".Length..], out var bid)) return Json(400, new { error = "design id?" });
                if (Q(r, "meta") is "1" or "true") return _data.DesignBill(bid) is { } bill ? Json(200, bill) : Json(404, new { error = "no such design" });
                return _data.DesignPicture(bid) is { } pic ? Image(pic, r) : Json(404, new { error = "no preview for that design" });
            }
            if (path.StartsWith("/view/", StringComparison.Ordinal))
            {
                var bot = Uri.UnescapeDataString(path["/view/".Length..]);
                var mode = (Q(r, "mode") ?? "sheet").Trim().ToLowerInvariant();
                if (!Modes.Contains(mode)) return Json(400, new { error = $"unknown mode '{mode}' ({string.Join("|", Modes)})" });
                int radius = Math.Clamp(int.TryParse(Q(r, "radius"), out var rr) ? rr : _o.Radius, 3, 12);
                if (!_data.Bots().Any(b => b.Name.Equals(bot, StringComparison.OrdinalIgnoreCase) || b.Short.Equals(bot, StringComparison.OrdinalIgnoreCase)))
                    return Json(404, new { error = $"no bot '{bot}' online" });
                var key = $"view:{bot.ToLowerInvariant()}:{(mode == "brain" ? "sheet" : mode)}:{radius}";
                var img = await _cache.GetAsync(key, TimeSpan.FromMilliseconds(_o.CacheMs), async () =>
                    await _data.ViewAsync(bot, mode, radius, Gate, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(bot)).ConfigureAwait(false);
                return Image(img, r);
            }
            return Json(404, new { error = "not found" });
        }
        catch (RenderBusyException) { return Json(503, new { error = "renderer busy, retry" }, new Dictionary<string, string> { ["Retry-After"] = "1" }); }
        catch (KeyNotFoundException) { return Json(404, new { error = "not found" }); }
        catch (OperationCanceledException) { return Json(503, new { error = "cancelled" }); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "supervisor: {Path} failed", path);
            return Json(500, new { error = ex.Message });
        }
    }

    public static readonly IReadOnlyList<string> Modes = ["sheet", "brain", "eye", "top", "iso", "side", "layers"];

    private object Events(long since)
    {
        var list = _data.Events(since, 200);
        return new { events = list, last = list.Count > 0 ? list[^1].Id : since };
    }

    private bool Authorized(SupervisorRequest r)
    {
        if (_token == null) return true;
        var given = r.TokenHeader ?? Q(r, "token");
        if (string.IsNullOrEmpty(given)) return false;
        var b = Encoding.UTF8.GetBytes(given);
        return b.Length == _token.Length && CryptographicOperations.FixedTimeEquals(b, _token);
    }

    private static string? Q(SupervisorRequest r, string k) => r.Query.TryGetValue(k, out var v) ? v : null;

    private static CameraQuery? CameraQueryOf(SupervisorRequest r)
    {
        static double? D(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
        if (D(Q(r, "x")) is not { } x || D(Q(r, "y")) is not { } y || D(Q(r, "z")) is not { } z) return null;
        int dim = (int)(D(Q(r, "dim")) ?? 0) == -1 ? -1 : 0;
        return new CameraQuery(x, Math.Clamp(y, -16, 200), z, (float)(D(Q(r, "yaw")) ?? 0), (float)Math.Clamp(D(Q(r, "pitch")) ?? 20, -90, 90), dim,
            (int)(D(Q(r, "w")) ?? 480), (int)(D(Q(r, "h")) ?? 360));
    }

    private static SupervisorReply Image(ViewImage img, SupervisorRequest r)
    {
        if (Q(r, "meta") is "1" or "true")
            return Json(200, new
            {
                width = img.Width, height = img.Height, tokens = img.Tokens, bytes = img.Png.Length, legend = img.Legend, facts = img.Facts,
                seen = img.Seen, note = img.Note,
            });
        var h = new Dictionary<string, string>
        {
            ["X-Tokens"] = img.Tokens.ToString(CultureInfo.InvariantCulture),
            ["X-Width"] = img.Width.ToString(CultureInfo.InvariantCulture),
            ["X-Height"] = img.Height.ToString(CultureInfo.InvariantCulture),
        };
        if (img.Seen is { } seen) h["X-Seen"] = Ascii(seen);
        return new SupervisorReply(200, "image/png", img.Png, h);
    }

    private static string Ascii(string s) => new(s.Select(c => c is >= ' ' and <= '~' ? c : '?').Take(300).ToArray());

    private static SupervisorReply Json(int status, object value, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, "application/json; charset=utf-8", SupervisorJson.Bytes(value), headers);

    private static byte[]? _page;

    /// <summary>The single-file UI (embedded resource; no external scripts, works offline through the tunnel).</summary>
    public static byte[] Page()
    {
        if (_page != null) return _page;
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MineClaw.App.Supervisor.supervisor.html");
        if (s == null) return _page = Encoding.UTF8.GetBytes("<!doctype html><title>Hive supervisor</title><p>UI resource missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return _page = ms.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); _listener?.Close(); } catch (Exception) { }
        if (_loop != null) { try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { } }
        Gate.Dispose();
        (Admin as IDisposable)?.Dispose();
    }
}
