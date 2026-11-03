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
         "viewer/memory-activity", "admin/rebuild/", "admin/audit", "designer/", "story"];
    public static readonly string[] AllowPost = ["viewer/chat", "admin/", "designer/", "story/"];
    public static readonly string[] AdminOnly = ["admin/"];
    /// <summary>POST paths that need the <c>story</c> or the <c>admin</c> role (contracts <c>storyWrite</c>).</summary>
    public static readonly string[] StoryWrite = ["story/"];
    /// <summary>Story directives to AI bodies: the bridge adds <c>player</c> and <c>allow_commands</c> from the identity, as for viewer chat.</summary>
    public const string StoryDirectives = "story/directives";

    /// <summary>Prefix match as the contract defines it: an entry ending in "/" is a prefix; any other entry matches itself and its sub-paths.</summary>
    public static bool Matches(string path, string entry) =>
        entry.EndsWith('/') ? path.StartsWith(entry, StringComparison.Ordinal) && path.Length > entry.Length
            : path == entry || path.StartsWith(entry + "/", StringComparison.Ordinal);

    public static bool Allowed(string method, string path) =>
        (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ? AllowGet : HttpMethods.IsPost(method) ? AllowPost : []).Any(e => Matches(path, e));

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
            string d;
            try { d = Uri.UnescapeDataString(s); }
            catch (UriFormatException) { return null; }
            if (d.Length == 0 || d is "." or ".." || d.Any(c => c is '/' or '\\' or '?' or '#' || char.IsControl(c))) return null;
            segs.Add(d);
        }
        return (string.Join('/', segs), query);
    }

    /// <summary>The upstream relative URL (each segment re-escaped).</summary>
    public static string Upstream(string path, string query) =>
        "api/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + (query.Length > 0 ? "?" + query : "");

    public static TimeSpan TimeoutFor(string path) => path switch
    {
        "world/chunks" => TimeSpan.FromSeconds(10),
        "world/entities" => TimeSpan.FromSeconds(3),
        "admin/shard/import" => TimeSpan.FromSeconds(180),
        _ when path.StartsWith("admin/", StringComparison.Ordinal) => TimeSpan.FromSeconds(60),
        _ when path.EndsWith("/vision/render", StringComparison.Ordinal) => TimeSpan.FromSeconds(12),
        _ when path.EndsWith(".png", StringComparison.Ordinal) => TimeSpan.FromSeconds(6),
        _ when path.StartsWith("bodies/", StringComparison.Ordinal) => TimeSpan.FromSeconds(4),
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
        if (!Allowed(method, s.Path)) { await ApiErrors.Write(ctx, 404, "not_found", "not found"); return; }
        if (NeedsAdmin(s.Path) && !id.IsAdmin) { await ApiErrors.Write(ctx, 403, "forbidden", "admin role required"); return; }
        if (NeedsStory(method, s.Path) && !MayWriteStory(id)) { await ApiErrors.Write(ctx, 403, "forbidden", "story role required"); return; }

        HttpContent? content = null;
        if (HttpMethods.IsPost(method))
        {
            // Admin and story writes: `who` is always the signed user, never the body's.
            var obj = ParseObject(ctx.BodyBytes());
            if (obj == null) { await ApiErrors.Write(ctx, 400, "bad_request", "body must be a JSON object"); return; }
            if (obj.ContainsKey("who")) { await ApiErrors.Write(ctx, 400, "bad_request", "who is set by the bridge"); return; }
            if (s.Path == StoryDirectives)
            {
                // Like viewer chat: the sender is the signed player, and commands only with the `commands` role.
                if (obj.ContainsKey("player") || obj.ContainsKey("allow_commands")) { await ApiErrors.Write(ctx, 400, "bad_request", "player and allow_commands are set by the bridge"); return; }
                if (id.Player.Length == 0) { await ApiErrors.Write(ctx, 403, "forbidden", "no Minecraft player is linked to this account"); return; }
                obj["player"] = id.Player;
                obj["allow_commands"] = id.Has("commands");
            }
            else obj["who"] = id.Who;
            content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
        }
        await ForwardAsync(ctx, sup, HttpMethods.IsHead(method) ? HttpMethod.Head : HttpMethods.IsPost(method) ? HttpMethod.Post : HttpMethod.Get, s.Path,
            Upstream(s.Path, s.Query), content, observe: s.Path == "world/index" && cache != null ? body => cache.ObserveIndex(s.Query, body) : null);
    }

    /// <param name="path">The decoded path after /api/ (timeouts, reply adapters).</param>
    /// <param name="relative">The upstream URL relative to the supervisor base.</param>
    /// <param name="observe">Sees a buffered JSON reply (the chunk cache reads index hashes); the bytes pass on unchanged.</param>
    public static async Task ForwardAsync(HttpContext ctx, SupervisorClient sup, HttpMethod method, string path, string relative, HttpContent? content,
        Func<byte[], byte[]?>? transform = null, Action<byte[]>? observe = null)
    {
        transform ??= TransformFor(path);
        if (observe != null && transform == null) transform = body => { observe(body); return null; };
        HttpResponseMessage res;
        try { res = await sup.SendAsync(method, relative, content, TimeoutFor(path), stream: transform == null, ctx.RequestAborted); }
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
            if (transform != null)
            {
                var body = await res.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                var outBody = transform(body) ?? body;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength = outBody.Length;
                await ctx.Response.Body.WriteAsync(outBody, ctx.RequestAborted);
                return;
            }
            if (res.Content.Headers.ContentLength is { } len) ctx.Response.ContentLength = len;
            try { await res.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException) { ctx.Abort(); }
        }
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
        string msg = "upstream error";
        long? retryMs = null;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (status == 400 && post && root.TryGetProperty("ok", out _))
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.Headers.CacheControl = "no-store";
                    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                    return;
                }
                if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) msg = Clip(e.GetString()!);
                if (root.TryGetProperty("retry_after_ms", out var ra) && ra.TryGetInt64(out var r)) retryMs = r;
            }
        }
        catch (JsonException) { }
        retryMs ??= res.Headers.RetryAfter?.Delta is { } d ? (long)d.TotalMilliseconds : null;
        switch (status)
        {
            case 400: await ApiErrors.Write(ctx, 400, "bad_request", msg); break;
            case 401: await ApiErrors.Write(ctx, 502, "upstream_error", "the bot refused the bridge (check Supervisor:Token)"); break;
            case 403: await ApiErrors.Write(ctx, 403, "forbidden", msg); break;
            case 404: await ApiErrors.Write(ctx, 404, "not_found", msg); break;
            case 405: await ApiErrors.Write(ctx, 404, "not_found", "not found"); break;
            case 413: await ApiErrors.Write(ctx, 413, "payload_too_large", msg); break;
            case 429: await ApiErrors.Write(ctx, 429, "rate_limited", msg, retryMs ?? 1000); break;
            case 503: await ApiErrors.Write(ctx, 503, msg.Contains("busy", StringComparison.OrdinalIgnoreCase) || retryMs != null ? "busy" : "upstream_unavailable", msg, retryMs ?? 1000); break;
            default: await ApiErrors.Write(ctx, 502, "upstream_error", "upstream error"); break;
        }
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
