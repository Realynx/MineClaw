using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.ViewerBridge.Security;

namespace MineClaw.ViewerBridge.Supervisor;

/// <summary>
/// <c>/v1/admin/nodes…</c>: the Administration page's routes, per bot <b>node</b>. One node today, <c>local</c> (this bridge's
/// <see cref="SupervisorClient"/>); more nodes would each be a named supervisor URL + token in the bridge config, looked up here by
/// id (see docs/web/deploy.md "Bot settings from the web"). Every route needs the <c>admin</c> role in the signed identity; the
/// identity is forwarded to the bot as <c>X-MCX-User</c> / <c>X-MCX-Roles</c> (the bot refuses these routes without them), never in
/// the body. Bodies are passed through unchanged (they may hold an API key: never logged); the bot's per-key validation errors
/// become <c>ApiError.details</c> with the setting key as <c>path</c>.
/// </summary>
public static class SettingsProxy
{
    public const string LocalNode = "local";
    public const string UserHeader = "X-MCX-User", RolesHeader = "X-MCX-Roles";

    /// <summary>(method, path after the node) → (bot method, bot path, timeout). Anything else is 404.</summary>
    public static (HttpMethod Method, string Path, TimeSpan Timeout)? Route(string method, string rest) => (method.ToUpperInvariant(), rest) switch
    {
        ("GET", "settings") => (HttpMethod.Get, "api/admin/settings", TimeSpan.FromSeconds(10)),
        ("POST", "settings") => (HttpMethod.Put, "api/admin/settings", TimeSpan.FromSeconds(20)),
        ("POST", "settings/test") => (HttpMethod.Post, "api/admin/settings/test", TimeSpan.FromSeconds(40)),
        ("GET", "ollama/models") => (HttpMethod.Get, "api/admin/ollama/models", TimeSpan.FromSeconds(25)),
        ("GET", "restart/safety") => (HttpMethod.Get, "api/admin/restart/safety", TimeSpan.FromSeconds(8)),
        ("POST", "restart") => (HttpMethod.Post, "api/admin/restart", TimeSpan.FromSeconds(10)),
        ("GET", "audit") => (HttpMethod.Get, "api/admin/settings/audit", TimeSpan.FromSeconds(8)),
        _ => null,
    };

    /// <summary>The identity headers the bot checks (the signed user and roles).</summary>
    public static Dictionary<string, string> IdentityHeaders(BridgeIdentity id) => new()
    {
        [UserHeader] = id.User,
        [RolesHeader] = string.Join(',', id.Roles.Order(StringComparer.Ordinal)),
    };

    public static void Map(RouteGroupBuilder v1)
    {
        v1.MapGet("/admin/nodes", async (HttpContext ctx, SupervisorClient sup) =>
        {
            var id = ctx.Identity();
            if (!id.IsAdmin) return ApiErrors.Forbidden("admin role required");
            JsonObject node = new() { ["id"] = LocalNode, ["name"] = LocalNode };
            try
            {
                using var res = await sup.SendAsync(HttpMethod.Get, "api/admin/settings/node", null, TimeSpan.FromSeconds(4), false, ctx.RequestAborted, IdentityHeaders(id));
                if (res.IsSuccessStatusCode && JsonNode.Parse(await res.Content.ReadAsByteArrayAsync(ctx.RequestAborted)) is JsonObject n) node = n;
                node["reachable"] = true;
            }
            catch (SupervisorUnavailableException) { node["reachable"] = false; }
            catch (JsonException) { node["reachable"] = true; }
            node["id"] = LocalNode;
            node["supervisor_url"] = sup.BaseUrl;
            return Results.Text(new JsonObject { ["nodes"] = new JsonArray(node) }.ToJsonString(), "application/json; charset=utf-8");
        });

        v1.MapMethods("/admin/nodes/{node}/{**rest}", ["GET", "POST"], async (HttpContext ctx, string node, string rest, SupervisorClient sup) =>
        {
            await HandleAsync(ctx, node, rest, sup);
            return Results.Empty;
        });
    }

    public static async Task HandleAsync(HttpContext ctx, string node, string rest, SupervisorClient sup)
    {
        var id = ctx.Identity();
        if (!id.IsAdmin) { await ApiErrors.Write(ctx, 403, "forbidden", "admin role required"); return; }
        if (!node.Equals(LocalNode, StringComparison.Ordinal)) { await ApiErrors.Write(ctx, 404, "not_found", "no such node"); return; }
        if (Route(ctx.Request.Method, rest.TrimEnd('/')) is not { } route) { await ApiErrors.Write(ctx, 404, "not_found", "not found"); return; }
        HttpContent? content = null;
        if (route.Method != HttpMethod.Get)
        {
            var (body, bad) = WriteBody(ctx);
            if (bad != null) { await ApiErrors.Write(ctx, 400, "bad_request", bad); return; }
            content = body;
        }
        var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value! : "";
        HttpResponseMessage res;
        try { res = await sup.SendAsync(route.Method, route.Path + query, content, route.Timeout, false, ctx.RequestAborted, IdentityHeaders(id)); }
        catch (SupervisorUnavailableException ex) { await ApiErrors.Write(ctx, 503, "upstream_unavailable", ex.Message); return; }
        using (res) await ReplyAsync(ctx, res, rest == "settings" && route.Method == HttpMethod.Get ? sup.BaseUrl : null);
    }

    /// <summary>A write's JSON body (who is the signed user, set upstream from the identity headers); why not otherwise.</summary>
    private static (HttpContent? Body, string? Error) WriteBody(HttpContext ctx)
    {
        var obj = SupervisorProxy.ParseObject(ctx.BodyBytes());
        if (obj == null) return (null, "body must be a JSON object");
        if (obj.ContainsKey("who")) return (null, "who is set by the bridge");
        return (new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json"), null);
    }

    /// <summary>The supervisor's answer: a success as JSON (the settings view stamped with the node when <paramref name="stampFrom"/> is
    /// given), an error mapped.</summary>
    private static async Task ReplyAsync(HttpContext ctx, HttpResponseMessage res, string? stampFrom)
    {
        var body = await res.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
        int status = (int)res.StatusCode;
        if (status is < 200 or >= 300)
        {
            await WriteErrorAsync(ctx, status, body, res.Headers.RetryAfter?.Delta);
            return;
        }
        if (stampFrom != null) body = StampNode(body, stampFrom);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.ContentLength = body.Length;
        await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
    }

    /// <summary>The settings view names its node as the bridge knows it (id, URL).</summary>
    public static byte[] StampNode(byte[] body, string supervisorUrl)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject o) return body;
            var n = o["node"] as JsonObject ?? [];
            n["id"] = LocalNode;
            n["supervisor_url"] = supervisorUrl;
            n["reachable"] = true;
            o["node"] = n;
            return Encoding.UTF8.GetBytes(o.ToJsonString());
        }
        catch (JsonException) { return body; }
    }

    /// <summary>The bot's error → ApiError: per-key validation errors as <c>details</c>, 409 (restart refused) as a conflict.</summary>
    public static async Task WriteErrorAsync(HttpContext ctx, int status, byte[] body, TimeSpan? retryAfter)
    {
        var (msg, details, retryMs) = ReadUpstreamError(body, retryAfter is { } d ? (long)d.TotalMilliseconds : null);
        var (outStatus, code) = MapStatus(status);
        if (status == 401) msg = "the bot refused the bridge (check Supervisor:Token)";
        if (outStatus == 502 && status != 401) msg = "upstream error";
        ctx.Response.StatusCode = outStatus;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        var err = new JsonObject { ["error"] = msg, ["code"] = code };
        if (outStatus is 429 or 503 && retryMs is { } rm)
        {
            ctx.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(rm / 1000.0)).ToString(CultureInfo.InvariantCulture);
            err["retry_after_ms"] = rm;
        }
        if (details is { Count: > 0 }) err["details"] = details;
        await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(err.ToJsonString()), ctx.RequestAborted);
    }

    /// <summary>The bot's error body: its message (clipped), per-key errors, and a retry hint that beats the Retry-After header.</summary>
    private static (string Message, JsonArray? Details, long? RetryMs) ReadUpstreamError(byte[] body, long? retryMs)
    {
        string msg = "upstream error";
        JsonArray? details = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject o)
            {
                if (o["error"] is JsonValue e && e.TryGetValue<string>(out var s)) msg = Clip(s);
                if (o["errors"] is JsonArray errs) details = Details(errs);
                if (o["retry_after_ms"] is JsonValue ra && ra.TryGetValue<long>(out var r)) retryMs = r;
            }
        }
        catch (JsonException) { }
        return (msg, details, retryMs);
    }

    /// <summary>Per-key validation errors (at most 50) as ApiError details: path and message (clipped; "invalid" without one).</summary>
    private static JsonArray Details(JsonArray errs) =>
        new(errs.OfType<JsonObject>().Take(50).Select(x => (JsonNode)new JsonObject
        {
            ["path"] = x["key"] is JsonValue k && k.TryGetValue<string>(out var ks) ? ks : "",
            ["message"] = x["message"] is JsonValue m && m.TryGetValue<string>(out var ms) ? Clip(ms) : "invalid",
        }).ToArray());

    /// <summary>The bot's status → the bridge's status and ApiError code (a refused bridge token is the bridge's 502).</summary>
    private static (int Status, string Code) MapStatus(int status) => status switch
    {
        400 => (400, "bad_request"),
        401 => (502, "upstream_error"),
        403 => (403, "forbidden"),
        404 => (404, "not_found"),
        409 => (409, "conflict"),
        413 => (413, "payload_too_large"),
        429 => (429, "rate_limited"),
        503 => (503, "upstream_unavailable"),
        _ => (502, "upstream_error"),
    };

    private static string Clip(string s)
    {
        s = s.ReplaceLineEndings(" ");
        if (s.Contains("http://", StringComparison.OrdinalIgnoreCase) || s.Contains("https://", StringComparison.OrdinalIgnoreCase) || s.Contains("token=", StringComparison.OrdinalIgnoreCase))
            return "upstream error";
        return s.Length > 300 ? s[..300] : s;
    }
}
