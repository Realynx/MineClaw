using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.ViewerBridge.Security;

namespace MineClaw.ViewerBridge.Supervisor;

/// <summary>
/// <c>/v1/sup/&lt;path&gt;</c> → supervisor <c>/api/&lt;path&gt;</c>: allow-listed exactly as <c>SUPERVISOR_PASSTHROUGH_ALLOW</c> in
/// @mineclaw/contracts (anything else 404), admin paths only with the <c>admin</c> role, Story Mode writes only with the
/// <c>story</c> or the <c>admin</c> role, the token added here, binary replies
/// streamed unbuffered, upstream errors mapped to <c>ApiError</c>. Three bot replies are adapted to the contracts:
/// <c>viewer/now</c> (<c>body.pos</c> "x,y,z" → [x,y,z]), <c>viewer/memory-activity</c> (<c>memory_id</c> → string) and
/// <c>viewer/chat</c> (<c>{accepted, id: feed seq}</c> → <c>{accepted, id: request_id, feed_seq}</c>).
/// </summary>
public static class SupervisorProxy
{
    public static readonly string[] AllowGet =
        ["bots", "hive/tree", "status", "events", "locate", "blueprints", "buildplans", "world/", "scripts", "bodies/", "brains/", "viewer/feed", "viewer/now",
         "viewer/memory-activity", "viewer/bodies/", "admin/rebuild/", "admin/audit", "designer/", "story"];
    public static readonly string[] AllowPost = ["viewer/chat", "admin/", "designer/", "story/", "bodies/"];
    public static readonly string[] AdminOnly = ["admin/"];
    /// <summary>POST paths that need the <c>story</c> or the <c>admin</c> role (contracts <c>storyWrite</c>).</summary>
    public static readonly string[] StoryWrite = ["story/", "bodies/"];
    /// <summary>Story directives to AI bodies: the bridge adds <c>player</c> and <c>allow_commands</c> from the identity, as for viewer chat.</summary>
    public const string StoryDirectives = "story/directives";

    /// <summary>Prefix match as the contract defines it: an entry ending in "/" is a prefix; any other entry matches itself and its sub-paths.</summary>
    public static bool Matches(string path, string entry) =>
        entry.EndsWith('/') ? path.StartsWith(entry, StringComparison.Ordinal) && path.Length > entry.Length
            : path == entry || path.StartsWith(entry + "/", StringComparison.Ordinal);

    public static bool Allowed(string method, string path) =>
        (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ? AllowGet : HttpMethods.IsPost(method) ? AllowPost : []).Any(e => Matches(path, e))
        && !(HttpMethods.IsPost(method) && path.StartsWith("bodies/", StringComparison.Ordinal) && !ControlPath(path));

    private static bool ControlPath(string path)
    {
        var p = path.Split('/');
        return p.Length == 4 && p[0] == "bodies" && p[1].Length > 0 && p[2] == "control"
            && p[3] is "acquire" or "input" or "action" or "release";
    }

    public static bool NeedsAdmin(string path) => AdminOnly.Any(e => Matches(path, e));

    public static bool NeedsStory(string method, string path) => HttpMethods.IsPost(method) && StoryWrite.Any(e => Matches(path, e));

    /// <summary>Story Mode writes: the <c>story</c> role, or <c>admin</c> (which holds every story grant).</summary>
    public static bool MayWriteStory(BridgeIdentity id) => id.Has("story") || id.IsAdmin;

    /// <summary>
    /// The raw <c>/v1/sup/…</c> target split into a decoded, validated path (segments joined with "/", no "."/".." or encoded
    /// separators) and the raw query (forwarded unchanged); null = not a safe path.
    /// </summary>
    public static (string Path, string Query)? Split(string rawTarget, string prefix = "/v1/sup/")
    {
        if (!rawTarget.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = rawTarget[prefix.Length..];
        int q = rest.IndexOf('?');
        var rawPath = q >= 0 ? rest[..q] : rest;
        var query = q >= 0 ? rest[(q + 1)..] : "";
        if (rawPath.Length == 0 || rawPath.Length > 512) return null;
        var segs = new List<string>();
        foreach (var s in rawPath.Split('/'))
        {
            if (Segment(s) is not { } d) return null;
            segs.Add(d);
        }
        return (string.Join('/', segs), query);
    }

    /// <summary>One path segment decoded; null when it doesn't decode, is empty, "." or "..", or holds a separator or control char.</summary>
    private static string? Segment(string s)
    {
        string d;
        try { d = Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return null; }
        return d.Length == 0 || d is "." or ".." || d.Any(c => c is '/' or '\\' or '?' or '#' || char.IsControl(c)) ? null : d;
    }

    /// <summary>The upstream relative URL (each segment re-escaped).</summary>
    public static string Upstream(string path, string query) =>
        "api/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + (query.Length > 0 ? "?" + query : "");

    public static TimeSpan TimeoutFor(string path) => path switch
    {
        "world/chunks" => TimeSpan.FromSeconds(10),
        "world/entities" => TimeSpan.FromSeconds(3),
        "admin/shard/import" => TimeSpan.FromSeconds(180),
        _ when ControlPath(path) && path.EndsWith("/acquire", StringComparison.Ordinal) => TimeSpan.FromSeconds(15),
        _ when path.StartsWith("admin/", StringComparison.Ordinal) => TimeSpan.FromSeconds(60),
        _ when path.EndsWith("/vision/render", StringComparison.Ordinal) => TimeSpan.FromSeconds(12),
        _ when path.EndsWith(".png", StringComparison.Ordinal) => TimeSpan.FromSeconds(6),
        _ when path.StartsWith("bodies/", StringComparison.Ordinal) || path.StartsWith("viewer/bodies/", StringComparison.Ordinal) => TimeSpan.FromSeconds(4),
        _ => TimeSpan.FromSeconds(8),
    };

    /// <summary>Headers a binary/JSON reply carries through (api.md §1).</summary>
    public static readonly string[] PassHeaders = ["ETag", "X-Sha256", "X-Tokens", "X-Width", "X-Height", "X-Seen", "Retry-After", "Content-Disposition"];

    public static async Task HandleAsync(HttpContext ctx, SupervisorClient sup, ChunkCache? cache = null)
    {
        var id = ctx.Identity();
        var split = Split(BridgeAuthMiddleware.RawTarget(ctx));
        if (split is not { } s) { await ApiErrors.Write(ctx, 404, "not_found", "not found"); return; }
        var method = ctx.Request.Method;
        if (Refusal(id, method, s.Path) is { } refused) { await ApiErrors.Write(ctx, refused.Status, refused.Code, refused.Message); return; }
        HttpContent? content = null;
        if (HttpMethods.IsPost(method))
        {
            var (body, bad) = PostBody(ctx, id, s.Path);
            if (bad is { } b) { await ApiErrors.Write(ctx, b.Status, b.Code, b.Message); return; }
            content = body;
        }
        await ForwardAsync(ctx, sup, MethodOf(method), s.Path,
            Upstream(s.Path, s.Query), content, observe: s.Path == "world/index" && cache != null ? body => cache.ObserveIndex(s.Query, body) : null,
            headers: ControlPath(s.Path) ? SettingsProxy.IdentityHeaders(id) : null);
    }

    internal static HttpMethod MethodOf(string method) => HttpMethods.IsHead(method) ? HttpMethod.Head : HttpMethods.IsPost(method) ? HttpMethod.Post : HttpMethod.Get;

    /// <summary>A route this method may not reach (404), or a role it lacks (admin, story: 403); null = allowed.</summary>
    private static (int Status, string Code, string Message)? Refusal(BridgeIdentity id, string method, string path)
    {
        if (!Allowed(method, path)) return (404, "not_found", "not found");
        if (NeedsAdmin(path) && !id.IsAdmin) return (403, "forbidden", "admin role required");
        if (NeedsStory(method, path) && !MayWriteStory(id)) return (403, "forbidden", "story role required");
        return null;
    }

    /// <summary>
    /// Admin and story writes: <c>who</c> is always the signed user, never the body's. Story directives are like viewer chat: the sender is
    /// the signed player, and commands only with the <c>commands</c> role. The body to send, or the refusal.
    /// </summary>
    private static (HttpContent? Body, (int Status, string Code, string Message)? Error) PostBody(HttpContext ctx, BridgeIdentity id, string path)
    {
        var obj = ParseObject(ctx.BodyBytes());
        if (obj == null) return (null, (400, "bad_request", "body must be a JSON object"));
        if (obj.ContainsKey("who")) return (null, (400, "bad_request", "who is set by the bridge"));
        if (path == StoryDirectives)
        {
            if (obj.ContainsKey("player") || obj.ContainsKey("allow_commands")) return (null, (400, "bad_request", "player and allow_commands are set by the bridge"));
            if (id.Player.Length == 0) return (null, (403, "forbidden", "no Minecraft player is linked to this account"));
            obj["player"] = id.Player;
            obj["allow_commands"] = id.Has("commands");
        }
        else obj["who"] = id.Who;
        return (new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json"), null);
    }

    /// <param name="path">The decoded path after /api/ (timeouts, reply adapters).</param>
    /// <param name="relative">The upstream URL relative to the supervisor base.</param>
    /// <param name="observe">Sees a buffered JSON reply (the chunk cache reads index hashes); the bytes pass on unchanged.</param>
    public static async Task ForwardAsync(HttpContext ctx, SupervisorClient sup, HttpMethod method, string path, string relative, HttpContent? content,
        Func<byte[], byte[]?>? transform = null, Action<byte[]>? observe = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        transform ??= TransformFor(path);
        if (observe != null && transform == null) transform = body => { observe(body); return null; };
        HttpResponseMessage res;
        try { res = await sup.SendAsync(method, relative, content, TimeoutFor(path), stream: transform == null, ctx.RequestAborted, headers); }
        catch (SupervisorUnavailableException ex) { await ApiErrors.Write(ctx, 503, "upstream_unavailable", ex.Message); return; }
        using (res)
        {
            int status = (int)res.StatusCode;
            if (status is < 200 or > 299)
            {
                await MapErrorAsync(ctx, res, path, HttpMethods.IsPost(ctx.Request.Method));
                return;
            }
            ctx.Response.StatusCode = status;
            CopyHeaders(res, ctx.Response);
            if (transform != null) await WriteTransformedAsync(ctx, res, transform);
            else await StreamAsync(ctx, res);
        }
    }

    /// <summary>The buffered JSON reply through <paramref name="transform"/> (null = the bytes unchanged).</summary>
    private static async Task WriteTransformedAsync(HttpContext ctx, HttpResponseMessage res, Func<byte[], byte[]?> transform)
    {
        var body = await res.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
        var outBody = transform(body) ?? body;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength = outBody.Length;
        await ctx.Response.Body.WriteAsync(outBody, ctx.RequestAborted);
    }

    /// <summary>The reply streamed through; a broken stream aborts the response.</summary>
    private static async Task StreamAsync(HttpContext ctx, HttpResponseMessage res)
    {
        if (res.Content.Headers.ContentLength is { } len) ctx.Response.ContentLength = len;
        try { await res.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException) { ctx.Abort(); }
    }

    public static void CopyHeaders(HttpResponseMessage res, HttpResponse to)
    {
        if (res.Content.Headers.ContentType is { } ct) to.ContentType = ct.ToString();
        foreach (var h in PassHeaders)
        {
            if (res.Headers.TryGetValues(h, out var v) || res.Content.Headers.TryGetValues(h, out v)) to.Headers[h] = string.Join(",", v);
        }
        to.Headers.CacheControl = "no-store";
    }

    /// <summary>Upstream non-2xx → ApiError. A store refusal (400 with a result body carrying <c>ok</c>) on an admin write passes through unchanged.</summary>
    public static async Task MapErrorAsync(HttpContext ctx, HttpResponseMessage res, string path, bool post)
    {
        int status = (int)res.StatusCode;
        var bytes = await res.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
        var (passThrough, msg, retryMs) = ReadUpstreamError(bytes, status, post);
        if (status == 409 && post && ControlPath(path))
        {
            await ApiErrors.Write(ctx, 409, "conflict", msg);
            return;
        }
        if (passThrough)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
            return;
        }
        retryMs ??= res.Headers.RetryAfter?.Delta is { } d ? (long)d.TotalMilliseconds : null;
        await WriteMappedAsync(ctx, status, msg, retryMs);
    }

    /// <summary>The upstream error body: a store refusal to pass through (400 + <c>ok</c> on a write), else its message and retry hint.</summary>
    private static (bool PassThrough, string Message, long? RetryMs) ReadUpstreamError(byte[] bytes, int status, bool post)
    {
        string msg = "upstream error";
        long? retryMs = null;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (false, msg, retryMs);
            if (status == 400 && post && root.TryGetProperty("ok", out _)) return (true, msg, retryMs);
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) msg = Clip(e.GetString()!);
            if (root.TryGetProperty("retry_after_ms", out var ra) && ra.TryGetInt64(out var r)) retryMs = r;
        }
        catch (JsonException) { }
        return (false, msg, retryMs);
    }

    private static Task WriteMappedAsync(HttpContext ctx, int status, string msg, long? retryMs) => status switch
    {
        400 => ApiErrors.Write(ctx, 400, "bad_request", msg),
        401 => ApiErrors.Write(ctx, 502, "upstream_error", "the bot refused the bridge (check Supervisor:Token)"),
        403 => ApiErrors.Write(ctx, 403, "forbidden", msg),
        404 => ApiErrors.Write(ctx, 404, "not_found", msg),
        405 => ApiErrors.Write(ctx, 404, "not_found", "not found"),
        413 => ApiErrors.Write(ctx, 413, "payload_too_large", msg),
        429 or 503 => WriteRetryableAsync(ctx, status, msg, retryMs),
        _ => ApiErrors.Write(ctx, 502, "upstream_error", "upstream error"),
    };

    /// <summary>429 = rate limited; 503 = busy when it says so or gives a retry hint, else unavailable. Both retry in a second by default.</summary>
    private static Task WriteRetryableAsync(HttpContext ctx, int status, string msg, long? retryMs)
    {
        if (status == 429) return ApiErrors.Write(ctx, 429, "rate_limited", msg, retryMs ?? 1000);
        var code = msg.Contains("busy", StringComparison.OrdinalIgnoreCase) || retryMs != null ? "busy" : "upstream_unavailable";
        return ApiErrors.Write(ctx, 503, code, msg, retryMs ?? 1000);
    }

    private static string Clip(string s)
    {
        s = s.ReplaceLineEndings(" ");
        // Never echo anything URL- or token-like from upstream.
        if (s.Contains("http://", StringComparison.OrdinalIgnoreCase) || s.Contains("https://", StringComparison.OrdinalIgnoreCase) || s.Contains("token=", StringComparison.OrdinalIgnoreCase))
            return "upstream error";
        return s.Length > 200 ? s[..200] : s;
    }

    public static JsonObject? ParseObject(byte[] body)
    {
        if (body.Length == 0) return new JsonObject();
        try { return JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static Func<byte[], byte[]?>? TransformFor(string path) => path switch
    {
        "viewer/now" => NowTransform,
        "viewer/memory-activity" => MemoryActivityTransform,
        _ => null,
    };

    /// <summary><c>body.pos</c> "x,y,z" → [x, y, z] (the contract's canonical form).</summary>
    public static byte[]? NowTransform(byte[] body)
    {
        if (JsonNodeOrNull(body) is not JsonObject o) return null;
        FixNow(o);
        return Encoding.UTF8.GetBytes(o.ToJsonString());
    }

    public static void FixNow(JsonObject o)
    {
        if (o["body"] is JsonObject b && b["pos"] is JsonValue v && v.TryGetValue<string>(out var s))
        {
            var parts = s.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 3 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                b["pos"] = new JsonArray(parts.Select(p => (JsonNode)JsonValue.Create(double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture))).ToArray());
            else b.Remove("pos");
        }
    }

    /// <summary><c>memory_id</c> numbers → strings (contracts MemoryAccessEvent / InContextMemory).</summary>
    public static byte[]? MemoryActivityTransform(byte[] body)
    {
        if (JsonNodeOrNull(body) is not JsonObject o) return null;
        FixMemoryActivity(o);
        return Encoding.UTF8.GetBytes(o.ToJsonString());
    }

    public static void FixMemoryActivity(JsonObject o)
    {
        foreach (var key in new[] { "events", "in_context" })
            if (o[key] is JsonArray arr)
                foreach (var item in arr.OfType<JsonObject>())
                    if (item["memory_id"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
                        item["memory_id"] = v.ToJsonString();
    }

    private static JsonNode? JsonNodeOrNull(byte[] body)
    {
        try { return JsonNode.Parse(body); }
        catch (JsonException) { return null; }
    }
}
