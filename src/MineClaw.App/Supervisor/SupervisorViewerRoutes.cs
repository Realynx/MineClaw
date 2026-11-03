using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Telemetry;

namespace MineClaw.App.Supervisor;

/// <summary>
/// "Supervisor:Viewer": the web viewer's routes (<see cref="SupervisorViewerRoutes"/>). Off by default; needs
/// <see cref="SupervisorOptions.Token"/>, sent in the X-Supervisor-Token header only.
/// </summary>
public sealed class SupervisorViewerOptions
{
    /// <summary>Off = every <c>/api/viewer/*</c> route is 404.</summary>
    public bool Enabled { get; set; }
    /// <summary>A viewer line may arm the bot's command request grant (when its request also sets <c>allow_commands</c>);
    /// audited as "player (viewer)". Off = viewer lines never reach the command gate.</summary>
    public bool AllowCommands { get; set; }
    /// <summary>Viewer lines per player per minute (their own bucket: in-game chat is never demoted by them); more = 429.</summary>
    public int PerMinute { get; set; } = 12;
    /// <summary>The Design Studio (<c>/api/designer/*</c>, <see cref="DesignerRoutes"/>): on with the viewer unless turned off here.</summary>
    public MineClaw.Bot.Blueprints.DesignerOptions Designer { get; set; } = new();
    /// <summary>Story Mode writes (<c>POST /api/story/*</c>, docs/web/story-mode.md); null = on whenever the viewer is.</summary>
    public bool? AllowStory { get; set; }
    /// <summary>
    /// Accounts the config says are op (Story Mode's time control). 1.12.2 servers tell each account its op level, which wins; b1.8.1
    /// servers never do, so there an account is op only when it is listed here.
    /// </summary>
    public string[] OpAccounts { get; set; } = [];
}

/// <summary>"What is it doing right now" for one bot (<c>/api/viewer/now</c>); every part is null when unknown.</summary>
public sealed record ViewerNow(string Bot, DateTimeOffset At, string? BrainState, ViewerInFlight? InFlight, ViewerThought? Thought, ViewerTool? Tool,
    string? Task, ViewerScript? Script, ViewerBody? Body, string? Kind = null, string? Owner = null);
public sealed record ViewerInFlight(string Purpose, string Model, DateTimeOffset Since);
public sealed record ViewerThought(string Text, string Thinking, DateTimeOffset At);
public sealed record ViewerTool(string Name, string ArgsShort, DateTimeOffset At);
public sealed record ViewerScript(string Name, int? Version, string Status, int Line, bool Paused);
/// <param name="Pos">"x,y,z" (block coordinates).</param>
public sealed record ViewerBody(int Hp, int Food, string? Pos, int Dim, string? Held);

/// <summary>What the viewer routes need from the running bot (the live one: <see cref="LiveViewerData"/>; tests fake it).</summary>
public interface IViewerData
{
    /// <summary>Agent:Self:TrustedPlayers (the only players a viewer line may speak as).</summary>
    IReadOnlyCollection<string> TrustedPlayers { get; }
    /// <summary>A bot, clone or body name (login, short or number) → its login; null = no such bot.</summary>
    string? Resolve(string bot);
    /// <summary>Hands the line to main's router and, in a hive, to every hive process (only the target's router takes it).</summary>
    Task<bool> SendAsync(RemoteChat chat, CancellationToken ct);
    Task<ViewerNow?> NowAsync(string bot, CancellationToken ct);
    /// <summary>The brains whose context can be read (main and the hive brains).</summary>
    IReadOnlyList<string> Brains();
    /// <summary>A brain's full capture page (null = no capture or no answer).</summary>
    Task<BrainContextPage?> ContextAsync(string brain, CancellationToken ct);
    /// <summary>Doc ids → kind and RAPTOR node right now (missing = no such doc).</summary>
    IReadOnlyDictionary<long, MemoryDocInfo> Docs(IReadOnlyCollection<long> ids);
}

/// <summary>
/// The web viewer's routes (behind its NestJS proxy, never the browser directly):
/// <list type="bullet">
/// <item><c>POST /api/viewer/chat</c> <c>{bot, text, player, allow_commands, request_id}</c> → <c>{accepted, id}</c>: a trusted player's line
/// to one bot, delivered through <c>ChatRouter.OnRemoteChat</c> (addressed to that bot, real speech, not test, the player not marked
/// online). The reply goes to in-game chat as always; the viewer sees it in the feed. <c>id</c> = the feed seq of the viewer line.</item>
/// <item><c>GET /api/viewer/feed?since=&amp;max=</c> → <c>{lines:[ViewerLine], last}</c> (oldest first; pass <c>last</c> back as <c>since</c>)</item>
/// <item><c>GET /api/viewer/now?bot=</c> → <see cref="ViewerNow"/></item>
/// <item><c>GET /api/viewer/memory-activity?since=&amp;brain=</c> → <c>{events:[ViewerMemoryEvent], in_context:[ViewerInContext], last}</c></item>
/// <item><c>GET /api/viewer/bodies/{name}/inventory</c> → <see cref="BodyInventory"/> (any in-process body: main, hives, subhive bodies, pawns;
/// <c>online:false</c> = its last known inventory); 404 <c>{error, online:false}</c> when the body is unknown or never seen here.</item>
/// </list>
/// 404 unless <see cref="SupervisorViewerOptions.Enabled"/>; 403 without a configured token or without it in the X-Supervisor-Token
/// header (never <c>?token=</c>; constant-time compare). JSON is snake_case with nulls written out.
/// </summary>
public sealed class SupervisorViewerRoutes
{
    public const string Prefix = "/api/viewer";
    public const int MaxText = 256;
    public const int MaxBodyBytes = 4096;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    private static readonly Regex RequestId = new(@"^[A-Za-z0-9_.:\-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex InventoryPath = new(@"^/bodies/(?<name>[^/]{1,96})/inventory$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BodyName = new(@"^[A-Za-z0-9_]{1,32}$", RegexOptions.Compiled);

    private readonly SupervisorOptions _o;
    private readonly IViewerData _data;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _rate = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public SupervisorViewerRoutes(SupervisorOptions o, IViewerData data, ViewerFeed feed, ViewerMemoryActivity memory, TimeProvider? time = null)
    {
        _o = o;
        _data = data;
        Feed = feed;
        Memory = memory;
        _time = time ?? TimeProvider.System;
    }

    public ViewerFeed Feed { get; }
    public ViewerMemoryActivity Memory { get; }
    /// <summary>The bodies' inventories (<c>/bodies/{name}/inventory</c>); null = <see cref="BodyInventories.Process"/>.</summary>
    public BodyInventories? Inventories { get; init; }

    public static bool IsViewerPath(string path)
    {
        var p = path.TrimEnd('/');
        return p.Equals(Prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The body of a POST to a viewer route (null otherwise), read up to <see cref="MaxBodyBytes"/>, only with the right header.</summary>
    public static async Task<string?> ReadBodyAsync(HttpListenerRequest req, SupervisorOptions o, CancellationToken ct)
    {
        if (!req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) || !IsViewerPath(req.Url?.AbsolutePath ?? "/") || !req.HasEntityBody) return null;
        if (!o.Viewer.Enabled || !TokenOk(req.Headers["X-Supervisor-Token"], o.Token)) return null;
        if (req.ContentLength64 > MaxBodyBytes) return SupervisorAdminRoutes.TooLarge;
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        while (true)
        {
            int r = await req.InputStream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (r == 0) break;
            ms.Write(buf, 0, r);
            if (ms.Length > MaxBodyBytes) return SupervisorAdminRoutes.TooLarge;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static bool TokenOk(string? given, string? token)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(given)) return false;
        var a = Encoding.UTF8.GetBytes(given);
        var b = Encoding.UTF8.GetBytes(token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Null = not a viewer route.</summary>
    public async Task<SupervisorReply?> TryHandleAsync(SupervisorRequest r, CancellationToken ct = default)
    {
        var path = r.Path.TrimEnd('/');
        if (!IsViewerPath(path)) return null;
        if (!_o.Viewer.Enabled) return Reply(404, new { error = "not found" });
        if (string.IsNullOrEmpty(_o.Token)) return Reply(403, new { error = "the viewer needs Supervisor:Token" });
        if (!TokenOk(r.TokenHeader, _o.Token)) return Reply(403, new { error = "X-Supervisor-Token header required" });
        bool post = r.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        bool get = r.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || r.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (InventoryPath.Match(path[Prefix.Length..]) is { Success: true } inv)
                return get ? Inventory(inv.Groups["name"].Value) : NotAllowed("GET, HEAD");
            switch (path[Prefix.Length..].ToLowerInvariant())
            {
                case "/chat":
                    return post ? await ChatAsync(r, ct).ConfigureAwait(false) : NotAllowed("POST");
                case "/feed":
                    if (!get) return NotAllowed("GET, HEAD");
                    {
                        long since = Long(r, "since") ?? 0;
                        var lines = Feed.Since(since, (int)Math.Clamp(Long(r, "max") ?? 200, 1, 500));
                        return Reply(200, new { lines, last = lines.Count > 0 ? lines[^1].Seq : Math.Min(since, Feed.Last) });
                    }
                case "/now":
                    if (!get) return NotAllowed("GET, HEAD");
                    {
                        if (Q(r, "bot") is not { Length: > 0 } bot || _data.Resolve(bot) is not { } login) return Reply(404, new { error = "no such bot" });
                        return await _data.NowAsync(login, ct).ConfigureAwait(false) is { } now ? Reply(200, now) : Reply(404, new { error = "no such bot" });
                    }
                case "/memory-activity":
                    return get ? await MemoryActivityAsync(r, ct).ConfigureAwait(false) : NotAllowed("GET, HEAD");
            }
            return Reply(404, new { error = "not found" });
        }
        catch (OperationCanceledException) { return Reply(503, new { error = "cancelled" }); }
    }

    // ------------------------------------------------------------------ chat

    private sealed record ChatBody(string? Bot, string? Text, string? Player, bool AllowCommands, string? RequestId);

    private async Task<SupervisorReply> ChatAsync(SupervisorRequest r, CancellationToken ct)
    {
        if (r.Body == SupervisorAdminRoutes.TooLarge) return Reply(413, new { error = $"body over {MaxBodyBytes} bytes" });
        ChatBody? body;
        try { body = string.IsNullOrWhiteSpace(r.Body) ? null : JsonSerializer.Deserialize<ChatBody>(r.Body, Json); }
        catch (JsonException) { return Reply(400, new { error = "body must be JSON {bot, text, player, allow_commands, request_id}" }); }
        if (body == null) return Reply(400, new { error = "body must be JSON {bot, text, player, allow_commands, request_id}" });
        var text = body.Text?.Trim() ?? "";
        if (text.Length == 0) return Reply(400, new { error = "text is empty" });
        if (text.Length > MaxText) return Reply(400, new { error = $"text over {MaxText} chars" });
        if (text.Any(char.IsControl)) return Reply(400, new { error = "text must be one line (no control characters)" });
        if (body.RequestId is { } rid && !RequestId.IsMatch(rid)) return Reply(400, new { error = "request_id: 1-64 of A-Z a-z 0-9 _ . : -" });
        // The player is who the viewer's own login says it is (the proxy's principal), and must be a trusted player here.
        var player = _data.TrustedPlayers.FirstOrDefault(p => p.Equals(body.Player?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (player == null) return Reply(403, new { error = "player is not trusted (Agent:Self:TrustedPlayers)" });
        if (string.IsNullOrWhiteSpace(body.Bot) || _data.Resolve(body.Bot.Trim()) is not { } target) return Reply(404, new { error = "no such bot" });
        if (!Allow(player, out var retry))
            return Reply(429, new { error = $"over {Math.Max(1, _o.Viewer.PerMinute)} viewer lines a minute", retry_after_ms = (long)retry.TotalMilliseconds },
                new Dictionary<string, string> { ["Retry-After"] = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture) });

        var chat = new RemoteChat(player, text, "viewer", target, body.AllowCommands && _o.Viewer.AllowCommands, body.RequestId);
        long id = Feed.Add(target, "viewer", player, text, "viewer", body.RequestId); // before delivery: the reply's lines come after it
        bool accepted = await _data.SendAsync(chat, ct).ConfigureAwait(false);
        return Reply(200, new { accepted, id });
    }

    // Sliding one-minute window per player.
    private bool Allow(string player, out TimeSpan retry)
    {
        var now = _time.GetUtcNow();
        int max = Math.Max(1, _o.Viewer.PerMinute);
        lock (_lock)
        {
            if (!_rate.TryGetValue(player, out var q)) _rate[player] = q = new Queue<DateTimeOffset>();
            while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromMinutes(1)) q.Dequeue();
            if (q.Count >= max)
            {
                retry = q.Peek() + TimeSpan.FromMinutes(1) - now;
                return false;
            }
            q.Enqueue(now);
            retry = TimeSpan.Zero;
            return true;
        }
    }

    // ------------------------------------------------------------------ inventory

    /// <summary>A body by login or short name ("main", "102"); read-only, one snapshot (shared by viewers within half a second).</summary>
    private SupervisorReply Inventory(string raw)
    {
        string name;
        try { name = Uri.UnescapeDataString(raw).Trim(); }
        catch (UriFormatException) { return Reply(404, new { error = "no such body", online = false }); }
        if (!BodyName.IsMatch(name)) return Reply(404, new { error = "no such body", online = false });
        var reg = Inventories ?? BodyInventories.Process;
        var login = _data.Resolve(name);
        var inv = (login != null ? reg.Get(login) : null) ?? reg.Get(name);
        if (inv != null) return Reply(200, inv);
        return Reply(404, new { error = login != null ? "no inventory known for this body" : "no such body", online = false });
    }

    // ------------------------------------------------------------------ memory activity

    private async Task<SupervisorReply> MemoryActivityAsync(SupervisorRequest r, CancellationToken ct)
    {
        long since = Long(r, "since") ?? 0;
        var brain = Q(r, "brain") is { Length: > 0 } b ? b.Trim() : null;
        var events = Memory.Since(since, brain, 500);
        List<string> brains = brain != null ? [brain] : _data.Brains().Concat(events.Select(e => e.Brain)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pages = new Dictionary<string, BrainContextPage?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in brains) pages[name] = await _data.ContextAsync(name, ct).ConfigureAwait(false);
        var inContext = brains.SelectMany(n => Memory.InContext(n, pages[n], _data.Docs)).ToList();
        // RAPTOR nodes and turns are resolved now (a rebuild renumbers nodes; the turn ring is the capture's).
        var docs = events.Count > 0 ? _data.Docs(events.Select(e => e.MemoryId).Distinct().ToList()) : new Dictionary<long, MemoryDocInfo>();
        var list = events.Select(e => e with
        {
            RaptorNodeId = docs.TryGetValue(e.MemoryId, out var d) ? d.RaptorNode : null,
            Turn = pages.TryGetValue(e.Brain, out var p) ? ViewerMemoryActivity.TurnOf(e.At, p) : null,
        }).ToList();
        return Reply(200, new { events = list, in_context = inContext, last = list.Count > 0 ? list[^1].Seq : Math.Min(since, Memory.Last) });
    }

    // ------------------------------------------------------------------ helpers

    private static string? Q(SupervisorRequest r, string k) => r.Query.TryGetValue(k, out var v) ? v : null;

    private static long? Long(SupervisorRequest r, string k) =>
        long.TryParse(Q(r, k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static SupervisorReply NotAllowed(string allow) =>
        Reply(405, new { error = $"{allow} only" }, new Dictionary<string, string> { ["Allow"] = allow });

    private static SupervisorReply Reply(int status, object value, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Json), headers);
}
