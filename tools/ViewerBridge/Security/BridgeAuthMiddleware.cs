using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http.Features;

namespace MineClaw.ViewerBridge.Security;

/// <summary>
/// Every request: loopback source only (403), body read up to the route's cap (413), HMAC verified over the raw request target
/// and the exact body bytes (401), timestamp within ±30 s (401), nonce unseen in the last 5 min (401). The verified identity
/// (<see cref="BridgeIdentity"/>) is put on the context; the endpoint reads the buffered body. Every non-GET is audited.
/// Failures are generic: no detail about which check failed reaches the caller (the bridge log says, without secrets).
/// </summary>
public sealed class BridgeAuthMiddleware(RequestDelegate next, BridgeConfig config, NonceCache nonces, BridgeAudit audit, TimeProvider time, ILogger<BridgeAuthMiddleware> log)
{
    public const string IdentityKey = "mcx.identity";
    public const string BodyKey = "mcx.body";
    public const long JsonBodyLimit = 256 * 1024;
    /// <summary>A saved designer camera PNG and bounded mine-component metadata.</summary>
    public const long DesignerSaveBodyLimit = 8L * 1024 * 1024;
    /// <summary>A 48 MB shard plus multipart framing.</summary>
    public const long UploadBodyLimit = 50L * 1024 * 1024;

    private static readonly string[] UploadPaths = ["/v1/shards/import/preview", "/v1/sup/admin/shard/import"];

    public static long BodyLimitFor(string path) =>
        UploadPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)) ? UploadBodyLimit :
        IsDesignerSave(path) ? DesignerSaveBodyLimit : JsonBodyLimit;

    private static bool IsDesignerSave(string path)
    {
        const string prefix = "/v1/sup/designer/sessions/", suffix = "/save";
        return path.Length > prefix.Length + suffix.Length && path.StartsWith(prefix, StringComparison.Ordinal) &&
               path.EndsWith(suffix, StringComparison.Ordinal) &&
               long.TryParse(path.AsSpan(prefix.Length, path.Length - prefix.Length - suffix.Length), NumberStyles.None,
                   CultureInfo.InvariantCulture, out long id) && id > 0;
    }

    public static bool IsLoopback(IPAddress? ip)
    {
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var started = time.GetTimestamp();
        var key = config.Bridge.Key;
        if (Gate(ctx, key) is { } refused)
        {
            await ApiErrors.Write(ctx, refused.Status, refused.Code, refused.Message);
            return;
        }
        var body = await ReadLimitedBodyAsync(ctx);
        if (body == null)
        {
            await ApiErrors.Write(ctx, 413, "payload_too_large", "body too large");
            return;
        }
        var h = ctx.Request.Headers;
        string user = h[BridgeSigning.UserHeader].ToString(), player = h[BridgeSigning.PlayerHeader].ToString(), roles = h[BridgeSigning.RolesHeader].ToString();
        if (Unauthorized(ctx, body, key!, user, player, roles) is { } why)
        {
            log.LogWarning("unauthorized request ({Why})", why);
            await ApiErrors.Write(ctx, 401, "unauthorized", "unauthorized");
            return;
        }

        var id = new BridgeIdentity(user, player, BridgeIdentity.ParseRoles(roles), h[BridgeSigning.RequestIdHeader].ToString() is { Length: > 0 and <= 128 } rid ? rid : null);
        ctx.Items[IdentityKey] = id;
        ctx.Items[BodyKey] = body;
        ctx.Request.Body = new MemoryStream(body, writable: false);
        try { await next(ctx); }
        finally
        {
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
                audit.Write(id, ctx.Request.Method, ctx.Request.Path.Value ?? "", ctx.Response.StatusCode, time.GetElapsedTime(started), body.Length);
        }
    }

    /// <summary>The checks before the body is read: loopback only, a configured key. Null = go on.</summary>
    private (int Status, string Code, string Message)? Gate(HttpContext ctx, string? key)
    {
        if (!IsLoopback(ctx.Connection.RemoteIpAddress))
        {
            log.LogWarning("refused a non-loopback request");
            return (403, "forbidden", "forbidden");
        }
        return string.IsNullOrEmpty(key) ? (503, "upstream_unavailable", "bridge key not configured") : null;
    }

    /// <summary>The body, up to the route's cap; null = too large (by its declared length or as read).</summary>
    private async Task<byte[]?> ReadLimitedBodyAsync(HttpContext ctx)
    {
        long limit = BodyLimitFor(ctx.Request.Path.Value ?? "");
        if (ctx.Request.ContentLength is { } cl && cl > limit) return null;
        try { return await ReadBodyAsync(ctx.Request.Body, limit, ctx.RequestAborted); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Why the signed request is refused (timestamp, skew, nonce shape, identity headers, signature, replay); null = verified.</summary>
    private string? Unauthorized(HttpContext ctx, byte[] body, string key, string user, string player, string roles)
    {
        var h = ctx.Request.Headers;
        string ts = h[BridgeSigning.TsHeader].ToString(), nonce = h[BridgeSigning.NonceHeader].ToString(), sig = h[BridgeSigning.SigHeader].ToString();
        if (!long.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out var tsv)) return "bad timestamp";
        if (Math.Abs(time.GetUtcNow().ToUnixTimeSeconds() - tsv) > BridgeSigning.MaxSkewSeconds) return "clock skew";
        if (!BridgeSigning.NonceShape().IsMatch(nonce)) return "bad nonce";
        if (HasLineBreak(user) || HasLineBreak(player) || HasLineBreak(roles)) return "bad identity";
        var signing = BridgeSigning.SigningString(ctx.Request.Method, RawTarget(ctx), ts, nonce, BridgeSigning.Sha256Hex(body), user, player, roles);
        if (!BridgeSigning.SignatureMatches(key, signing, sig)) return "bad signature";
        return nonces.TryAdd(nonce) ? null : "replayed nonce";
    }

    private static bool HasLineBreak(string s) => s.Contains('\r') || s.Contains('\n');

    /// <summary>The request target exactly as received (path + "?" + raw query), never re-encoded.</summary>
    public static string RawTarget(HttpContext ctx)
    {
        var raw = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(raw) && raw.StartsWith('/')) return raw;
        return ctx.Request.PathBase.Add(ctx.Request.Path).ToUriComponent() + ctx.Request.QueryString.ToUriComponent();
    }

    private static async Task<byte[]> ReadBodyAsync(Stream s, long limit, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        while (true)
        {
            int n = await s.ReadAsync(buf, ct);
            if (n == 0) break;
            if (ms.Length + n > limit) throw new InvalidDataException("body over the limit");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }
}

public static class BridgeContext
{
    public static BridgeIdentity Identity(this HttpContext ctx) =>
        ctx.Items.TryGetValue(BridgeAuthMiddleware.IdentityKey, out var v) && v is BridgeIdentity id ? id : BridgeIdentity.Anonymous;

    public static byte[] BodyBytes(this HttpContext ctx) =>
        ctx.Items.TryGetValue(BridgeAuthMiddleware.BodyKey, out var v) && v is byte[] b ? b : [];
}

/// <summary>Writes <c>ApiError {error, code?, retry_after_ms?}</c> (snake_case; Nest camel-cases it).</summary>
public static class ApiErrors
{
    public sealed record ApiError(string Error, string? Code = null, long? RetryAfterMs = null);

    public static async Task Write(HttpContext ctx, int status, string code, string message, long? retryAfterMs = null)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        if (retryAfterMs is { } ra) ctx.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(ra / 1000.0)).ToString(CultureInfo.InvariantCulture);
        await ctx.Response.Body.WriteAsync(BridgeJson.Bytes(new ApiError(message, code, retryAfterMs)));
    }

    public static IResult Result(int status, string code, string message, long? retryAfterMs = null) =>
        new ErrorResult(status, code, message, retryAfterMs);

    public static IResult NotFound(string what = "not found") => Result(404, "not_found", what);
    public static IResult BadRequest(string what) => Result(400, "bad_request", what);
    public static IResult Forbidden(string what = "forbidden") => Result(403, "forbidden", what);

    private sealed class ErrorResult(int status, string code, string message, long? retry) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => Write(httpContext, status, code, message, retry);
    }
}
