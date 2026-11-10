using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AdminSettingsRoutes = MineClaw.App.Supervisor.Admin.AdminSettingsRoutes;

namespace MineClaw.App.Supervisor;

/// <summary>One HTTP request as the router sees it (the listener glue and the tests both build these).</summary>
/// <param name="Body">POST body (admin routes only; capped at <see cref="SupervisorAdminRoutes.MaxBodyBytes"/>).</param>
/// <param name="Headers">The bridge's identity headers (<c>X-MCX-User</c>, <c>X-MCX-Roles</c>) for the routes that need them; null = none.</param>
public sealed record SupervisorRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query, string? TokenHeader = null, string? Body = null,
    IReadOnlyDictionary<string, string>? Headers = null);

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
    /// <summary>The read-only script routes (<c>/api/scripts*</c>, <c>/api/bodies/{name}/script</c>; 403 without the token); null = off.</summary>
    public SupervisorScriptRoutes? Scripts { get; set; }
    /// <summary>The read-only vision routes (<c>/api/bodies/{name}/vision*</c>: what the AI saw, on-demand renders; 403 without the token); null = off.</summary>
    public SupervisorVisionRoutes? Vision { get; set; }
    /// <summary>The read-only controller routes (<c>/api/bodies/{name}/controller</c>, <c>/api/brains/{id}/context|turn/{n}</c>: what drives a
    /// body, its brain's live context and thinking; 403 without the token); null = off.</summary>
    public SupervisorControllerRoutes? Controller { get; set; }
    public SupervisorPawnControlRoutes? PawnControl { get; set; }
    /// <summary>The web viewer's routes (<c>/api/viewer/*</c>, Supervisor:Viewer:Enabled + token header; the only non-admin POST); null = 404.</summary>
    public SupervisorViewerRoutes? Viewer { get; set; }
    /// <summary>The Design Studio's routes (<c>/api/designer/*</c>: sandbox canvas sessions with an AI, saved as designs); null = 404.</summary>
    public DesignerRoutes? Designer { get; set; }
    /// <summary>Story Mode (<c>/api/story/*</c>, the viewer's gate + AllowStory for writes); null = 404.</summary>
    public SupervisorStoryRoutes? Story { get; set; }
    /// <summary>The Administration page's routes (<c>/api/admin/settings*</c>, <c>/api/admin/ollama/*</c>, <c>/api/admin/restart*</c>); null = 404.</summary>
    public AdminSettingsRoutes? Settings { get; set; }
    /// <summary><c>/api/hive/tree</c>: the hive's ownership tree (main → children: kind, brain, state, task); null = main alone.</summary>
    public Func<object?>? HiveTree { get; set; }
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
        _perfTimer = new Timer(_ =>
        {
            try { _log.LogInformation("supervisor: {Perf}", Perf.Summary()); }
            catch (Exception ex) { _log.LogDebug(ex, "supervisor: perf line failed"); }
        }, null, PerfEvery, PerfEvery);
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

    /// <summary>Request timings and the process's CPU, pool, GC and A* figures, logged once a minute (<see cref="SupervisorPerf"/>).</summary>
    public SupervisorPerf Perf { get; } = new();

    /// <summary>How often the perf line is logged while the listener runs.</summary>
    public static readonly TimeSpan PerfEvery = TimeSpan.FromMinutes(1);

    private Timer? _perfTimer;

    /// <summary>At most one world-socket-ended line per this long; the ends in between are counted into the next line.</summary>
    internal static readonly TimeSpan WorldSocketEndLogEvery = TimeSpan.FromSeconds(10);
    private long _worldEndLoggedAt = long.MinValue / 2;
    private int _worldEndsQuiet;

    /// <summary>A world socket that ended other than by its client closing (or shutdown) is one Information line: the bridge's
    /// motion stream stopped and reconnects, which the viewer shows as stalled bodies. Debug alone left no trace in the log.</summary>
    private void LogWorldSocketEnd(WorldSocketEnded ended)
    {
        if (ended.End is WorldSocketEnd.ClientClosed or WorldSocketEnd.Shutdown) return;
        if (!WorldSocketEndDue(ref _worldEndLoggedAt, Environment.TickCount64)) { Interlocked.Increment(ref _worldEndsQuiet); return; }
        int quiet = Interlocked.Exchange(ref _worldEndsQuiet, 0);
        _log.LogInformation("supervisor: world socket ended: {End} {Detail}{Quiet}", ended.End, ended.Detail ?? "",
            quiet > 0 ? $" ({quiet} more since the last line)" : "");
    }

    /// <summary>Whether a line is due at <paramref name="nowMs"/> (claims the slot when it is).</summary>
    internal static bool WorldSocketEndDue(ref long lastMs, long nowMs)
    {
        long last = Interlocked.Read(ref lastMs);
        return nowMs - last >= (long)WorldSocketEndLogEvery.TotalMilliseconds && Interlocked.CompareExchange(ref lastMs, nowMs, last) == last;
    }

    private async Task ServeAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var res = ctx.Response;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (ctx.Request.IsWebSocketRequest && ctx.Request.Url?.AbsolutePath == "/api/world/socket" && World is { } world)
            {
                var request = await RequestOfAsync(ctx.Request, ct).ConfigureAwait(false);
                if (!Authorized(request with { Query = new Dictionary<string, string>() })) await WriteAsync(ctx, Json(401, new { error = "token required" }), ct).ConfigureAwait(false);
                else
                {
                    using var socket = (await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
                    LogWorldSocketEnd(await world.SocketAsync(socket, request, ct, Perf).ConfigureAwait(false));
                }
                return;
            }
            if (ctx.Request.IsWebSocketRequest && SupervisorPawnControlRoutes.IsSocketPath(ctx.Request.Url?.AbsolutePath ?? "") && PawnControl is { } control)
            {
                var request = await RequestOfAsync(ctx.Request, ct).ConfigureAwait(false);
                if (control.SocketAuthorization(request) is { } refused) await WriteAsync(ctx, refused, ct).ConfigureAwait(false);
                else
                {
                    using var socket = (await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
                    await control.SocketAsync(socket, request, ct, Perf).ConfigureAwait(false);
                }
                return; // A persistent socket never occupies a short-lived HTTP connection slot.
            }
            var reply = await AnswerAsync(ctx.Request, ct).ConfigureAwait(false);
            // The time a caller waited for the answer (a free slot included), by route; never the query (it may carry the token).
            Perf.Record(ctx.Request.Url?.AbsolutePath ?? "/", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await WriteAsync(ctx, reply, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug(ex, "supervisor: request failed"); } // never log the URL: it may carry the token
        finally
        {
            try { res.Close(); } catch (Exception) { }
        }
    }

    /// <summary>The reply, once a connection slot is free (503 busy after two seconds).</summary>
    private async Task<SupervisorReply> AnswerAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (!await _connections.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false)) return Json(503, new { error = "busy" });
        try
        {
            return await HandleAsync(await RequestOfAsync(request, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        finally { _connections.Release(); }
    }

    private async Task<SupervisorRequest> RequestOfAsync(HttpListenerRequest request, CancellationToken ct)
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nv = request.QueryString;
        foreach (var k in nv.AllKeys) if (k != null && nv[k] is { } v) q[k] = v;
        var body = await BodyOfAsync(request, ct).ConfigureAwait(false);
        return new SupervisorRequest(request.HttpMethod, request.Url?.AbsolutePath ?? "/", q, request.Headers["X-Supervisor-Token"], body, IdentityOf(request));
    }

    /// <summary>The body of a write route (each route set reads only its own POST/PUT bodies, under its own cap); null = none.</summary>
    private async Task<string?> BodyOfAsync(HttpListenerRequest request, CancellationToken ct) =>
        await AdminSettingsRoutes.ReadBodyAsync(request, _o, ct).ConfigureAwait(false)
        ?? await SupervisorAdminRoutes.ReadBodyAsync(request, ct, _o).ConfigureAwait(false)
        ?? await SupervisorViewerRoutes.ReadBodyAsync(request, _o, ct).ConfigureAwait(false)
        ?? await DesignerRoutes.ReadBodyAsync(request, _o, ct).ConfigureAwait(false)
        ?? await SupervisorStoryRoutes.ReadBodyAsync(request, _o, ct).ConfigureAwait(false)
        ?? await SupervisorPawnControlRoutes.ReadBodyAsync(request, _o, ct).ConfigureAwait(false);

    /// <summary>The bridge's identity headers (user, roles), or null without them.</summary>
    private static Dictionary<string, string>? IdentityOf(HttpListenerRequest request)
    {
        Dictionary<string, string>? identity = null;
        foreach (var h in new[] { AdminSettingsRoutes.UserHeader, AdminSettingsRoutes.RolesHeader })
            if (request.Headers[h] is { Length: > 0 } hv) (identity ??= new(StringComparer.OrdinalIgnoreCase))[h] = hv;
        return identity;
    }

    private static async Task WriteAsync(HttpListenerContext ctx, SupervisorReply reply, CancellationToken ct)
    {
        var res = ctx.Response;
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

    // ------------------------------------------------------------------ routing (no sockets: the tests call this)

    public async Task<SupervisorReply> HandleAsync(SupervisorRequest r, CancellationToken ct = default)
    {
        if (await GatedRoutesAsync(r, ct).ConfigureAwait(false) is { } gated) return gated;
        if (!IsGet(r.Method)) return Json(405, new { error = "read-only: GET only" }, new Dictionary<string, string> { ["Allow"] = "GET, HEAD" });
        var path = r.Path.TrimEnd('/');
        if (StaticReply(path) is { } page) return page;
        if (await OpenRoutesAsync(r, ct).ConfigureAwait(false) is { } open) return open;
        if (!Authorized(r)) return Json(401, new { error = "token required (X-Supervisor-Token header or ?token=)" });
        return await TokenRoutesAsync(r, path, ct).ConfigureAwait(false);
    }

    private static bool IsGet(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    /// <summary>The routes behind the token: the Live Hive's world routes, then the hive's own (errors mapped to statuses).</summary>
    private async Task<SupervisorReply> TokenRoutesAsync(SupervisorRequest r, string path, CancellationToken ct)
    {
        if (World != null && await World.TryHandleAsync(r, path, ct).ConfigureAwait(false) is { } worldReply) return worldReply;
        try
        {
            return await ExactRouteAsync(r, path, ct).ConfigureAwait(false) ?? await PrefixRouteAsync(r, path, ct).ConfigureAwait(false);
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

    private static SupervisorReply NotFound() => Json(404, new { error = "not found" });

    private static async Task<SupervisorReply> OrNotFound(Task<SupervisorReply?>? reply) =>
        reply == null ? NotFound() : await reply.ConfigureAwait(false) ?? NotFound();

    /// <summary>The route sets with their own gates (any method): settings, admin, viewer, designer, story; null = none of them.</summary>
    private async Task<SupervisorReply?> GatedRoutesAsync(SupervisorRequest r, CancellationToken ct)
    {
        if (SupervisorPawnControlRoutes.IsControlPath(r.Path)) return await OrNotFound(PawnControl?.TryHandleAsync(r, ct)).ConfigureAwait(false);
        if (AdminSettingsRoutes.IsSettingsPath(r.Path)) // AllowAdminWrites + the token header + the bridge's admin identity
            return Settings != null ? await Settings.HandleAsync(r, ct).ConfigureAwait(false) : NotFound();
        if (SupervisorAdminRoutes.IsAdminPath(r.Path)) return await SupervisorAdminRoutes.HandleAsync(r, _o, Admin, ct).ConfigureAwait(false);
        if (SupervisorViewerRoutes.IsViewerPath(r.Path)) // own gate: Supervisor:Viewer:Enabled (else 404) + the token header (else 403)
            return await OrNotFound(Viewer?.TryHandleAsync(r, ct)).ConfigureAwait(false);
        if (DesignerRoutes.IsDesignerPath(r.Path)) // the Design Studio: Viewer:Enabled + Viewer:Designer:Enabled (else 404) + the token header
            return await OrNotFound(Designer?.TryHandleAsync(r, ct)).ConfigureAwait(false);
        if (SupervisorStoryRoutes.IsStoryPath(r.Path)) // the viewer's gate (404/403) + AllowStory for writes
            return await OrNotFound(Story?.TryHandleAsync(r, ct)).ConfigureAwait(false);
        return null;
    }

    /// <summary>The page and the favicon (no token).</summary>
    private static SupervisorReply? StaticReply(string path)
    {
        if (path.Length == 0 || path == "/index.html") return new SupervisorReply(200, "text/html; charset=utf-8", Page());
        if (path == "/favicon.ico") return new SupervisorReply(204, "image/x-icon", []);
        return null;
    }

    /// <summary>The read-only route sets that check the token themselves (scripts, vision, controller); null = none of them.</summary>
    private async Task<SupervisorReply?> OpenRoutesAsync(SupervisorRequest r, CancellationToken ct)
    {
        if (Scripts != null && await Scripts.TryHandleAsync(r, ct).ConfigureAwait(false) is { } scriptReply) return scriptReply;
        if (Vision != null && await Vision.TryHandleAsync(r, ct).ConfigureAwait(false) is { } visionReply) return visionReply;
        if (Controller != null && await Controller.TryHandleAsync(r, ct).ConfigureAwait(false) is { } controllerReply) return controllerReply;
        return null;
    }

    /// <summary>The routes matched by their whole path; null = none.</summary>
    private async Task<SupervisorReply?> ExactRouteAsync(SupervisorRequest r, string path, CancellationToken ct)
    {
        switch (path)
        {
            case "/api/bots":
                return Json(200, new { at = _time.GetUtcNow(), bots = _data.Bots() });
            case "/api/hive/tree":
                return Json(200, HiveTreeOrMain());
            case "/api/status":
                return Json(200, _data.Status(Q(r, "bot")));
            case "/api/events":
                return Json(200, Events(long.TryParse(Q(r, "since"), out var since) ? since : 0));
            case "/api/locate":
                return Locate(r);
            case "/view/hive":
                return Image(await _cache.GetAsync("hive", TimeSpan.FromMilliseconds(_o.HiveCacheMs), () => _data.HiveAsync(Gate, ct)).ConfigureAwait(false), r);
            case "/camera":
                return await CameraAsync(r, ct).ConfigureAwait(false);
            // Designs (blueprints): the list.
            case "/api/blueprints":
                return Json(200, new { designs = _data.Designs() });
            default:
                return null;
        }
    }

    private object HiveTreeOrMain() =>
        HiveTree?.Invoke() ?? new { id = "main", name = "main", kind = "main", state = "running", children = Array.Empty<object>() };

    private SupervisorReply Locate(SupervisorRequest r) =>
        Q(r, "q") is { Length: > 0 } query && _data.Locate(query) is { } hit ? Json(200, hit) : NotFound();

    private async Task<SupervisorReply> CameraAsync(SupervisorRequest r, CancellationToken ct)
    {
        if (CameraQueryOf(r) is not { } cq) return Json(400, new { error = "camera needs x, y, z (yaw, pitch, dim, w, h optional)" });
        var key = string.Create(CultureInfo.InvariantCulture, $"cam:{cq.Dim}:{cq.X:0.0}:{cq.Y:0.0}:{cq.Z:0.0}:{cq.Yaw:0.0}:{cq.Pitch:0.0}:{cq.W}x{cq.H}");
        var img = await _cache.GetAsync(key, TimeSpan.FromMilliseconds(_o.CacheMs), () => _data.CameraAsync(cq, Gate, ct)).ConfigureAwait(false);
        return Image(img, r);
    }

    /// <summary>The routes matched by a path prefix (a design's bill and preview, build plans, a bot's view); 404 otherwise.</summary>
    private async Task<SupervisorReply> PrefixRouteAsync(SupervisorRequest r, string path, CancellationToken ct)
    {
        // One design's bill/eval as JSON.
        if (path.StartsWith("/api/blueprints/", StringComparison.Ordinal)) return DesignBill(path["/api/blueprints/".Length..]);
        // Build plans: the slicer's steps, techniques, scaffolding and progress (HTML page; ?meta=1 = the data).
        if (path.StartsWith("/view/buildplan/", StringComparison.Ordinal) || path.StartsWith("/api/buildplans/", StringComparison.Ordinal)) return BuildPlan(r, path);
        // A design's preview PNG (meta=1 = the bill).
        if (path.StartsWith("/view/blueprint/", StringComparison.Ordinal)) return DesignPreview(r, path["/view/blueprint/".Length..]);
        if (path.StartsWith("/view/", StringComparison.Ordinal)) return await ViewAsync(r, Uri.UnescapeDataString(path["/view/".Length..]), ct).ConfigureAwait(false);
        return NotFound();
    }

    private static bool Meta(SupervisorRequest r) => Q(r, "meta") is "1" or "true";

    private SupervisorReply DesignBill(string id) =>
        long.TryParse(id, out var bid) && _data.DesignBill(bid) is { } bill ? Json(200, bill) : Json(404, new { error = "no such design" });

    private SupervisorReply BuildPlan(SupervisorRequest r, string path)
    {
        var tail = path[(path.IndexOf('/', 1) + 1)..];
        tail = tail[(tail.IndexOf('/') + 1)..];
        if (!long.TryParse(tail, out var pid)) return Json(400, new { error = "design id?" });
        if (path.StartsWith("/api/", StringComparison.Ordinal) || Meta(r))
            return _data.BuildPlanData(pid) is { } data ? Json(200, data) : Json(404, new { error = "no build plan for that design" });
        return _data.BuildPlanPage(pid) is { } html
            ? new SupervisorReply(200, "text/html; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(html), null)
            : Json(404, new { error = "no build plan for that design" });
    }

    private SupervisorReply DesignPreview(SupervisorRequest r, string id)
    {
        if (!long.TryParse(id, out var bid)) return Json(400, new { error = "design id?" });
        if (Meta(r)) return _data.DesignBill(bid) is { } bill ? Json(200, bill) : Json(404, new { error = "no such design" });
        return _data.DesignPicture(bid) is { } pic ? Image(pic, r) : Json(404, new { error = "no preview for that design" });
    }

    private async Task<SupervisorReply> ViewAsync(SupervisorRequest r, string bot, CancellationToken ct)
    {
        var mode = (Q(r, "mode") ?? "sheet").Trim().ToLowerInvariant();
        if (!Modes.Contains(mode)) return Json(400, new { error = $"unknown mode '{mode}' ({string.Join("|", Modes)})" });
        int radius = Math.Clamp(int.TryParse(Q(r, "radius"), out var rr) ? rr : _o.Radius, 3, 12);
        if (!_data.Bots().Any(b => b.Name.Equals(bot, StringComparison.OrdinalIgnoreCase) || b.Short.Equals(bot, StringComparison.OrdinalIgnoreCase)))
            return Json(404, new { error = $"no bot '{bot}' online" });
        var xray = XRayOf(Q(r, "xray"));
        var key = $"view:{bot.ToLowerInvariant()}:{(mode == "brain" ? "sheet" : mode)}:{radius}{(xray == MineClaw.Render.XRay.Auto ? "" : ":" + xray)}";
        var img = await _cache.GetAsync(key, TimeSpan.FromMilliseconds(_o.CacheMs), async () =>
            await _data.ViewAsync(bot, mode, radius, xray, Gate, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(bot)).ConfigureAwait(false);
        return Image(img, r);
    }

    /// <summary>xray=on|off: the Explorer's x-ray toggle for the outside cameras (iso, orbit, high); default auto, the AI's rule.</summary>
    private static MineClaw.Render.XRay XRayOf(string? q) => (q ?? "").Trim().ToLowerInvariant() switch
    {
        "on" or "1" or "true" => MineClaw.Render.XRay.On,
        "off" or "0" or "false" => MineClaw.Render.XRay.Off,
        _ => MineClaw.Render.XRay.Auto,
    };

    public static readonly IReadOnlyList<string> Modes = ["sheet", "brain", "eye", "iso", "shoulder", "orbit", "high", "top", "side", "layers"];

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
        if (_perfTimer != null) await _perfTimer.DisposeAsync().ConfigureAwait(false);
        try { _listener?.Stop(); _listener?.Close(); } catch (Exception) { }
        if (_loop != null) { try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { } }
        Gate.Dispose();
        (Admin as IDisposable)?.Dispose();
        PawnControl?.Dispose(); // releases human inputs and resumes each paused pawn before the colony stops
        Story?.Colonies.Dispose(); // the colony tick stops; each colony saves
        Story?.StopTimeControl();
    }
}
