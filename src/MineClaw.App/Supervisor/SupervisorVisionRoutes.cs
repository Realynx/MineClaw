using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Bot.Perception.Vision;

namespace MineClaw.App.Supervisor;

/// <summary>A body's vision history: the sheets its models received, newest first (no PNG bytes: those have their own route).</summary>
public sealed record VisionListReply(string Body, int Capacity, IReadOnlyList<VisionSheetInfo> Sheets, string? Note = null);

/// <summary>What the vision routes read: a body's captured sheets, one sheet's exact PNG, and an on-demand render of a see input.</summary>
public interface IVisionSupervisorData
{
    /// <summary>Null = no such body online.</summary>
    Task<VisionListReply?> ListAsync(string body, long since, CancellationToken ct);
    /// <summary>The exact bytes the model got (null = unknown body or the sheet fell out of the ring).</summary>
    Task<byte[]?> PngAsync(string body, long id, CancellationToken ct);
    /// <summary>Renders a <c>see</c> tool input for that body (no model, no budget, not recorded). Null = no such body.</summary>
    Task<ViewImage?> RenderAsync(string body, string seeJson, RenderGate gate, CancellationToken ct);
}

/// <summary>
/// The read-only vision routes (GET; the Live Hive's "what the AI saw" and compose panel):
/// <list type="bullet">
/// <item><c>/api/bodies/{name}/vision[?since=id]</c> → VisionListReply (meta of the last sheets: request, size, tokens, sha256,
/// legend, the perception answer)</item>
/// <item><c>/api/bodies/{name}/vision/{id}</c> → one VisionSheetInfo; <c>/api/bodies/{name}/vision/{id}.png</c> → the exact PNG
/// the model received (<c>X-Sha256</c>, <c>X-Tokens</c>)</item>
/// <item><c>/api/bodies/{name}/vision/render?see={json}[&amp;meta=1]</c> → a fresh render of a see input on the server (roles and
/// marks included: what the AI would get), through the CPU-capped render gate</item>
/// </list>
/// Pictures carry what the bots see (coords, bases), so they are token-gated and answer 403 (not 401) without the token, like
/// the script routes. Clones answer over the <c>vision</c>/<c>vision_png</c> IPC requests.
/// </summary>
public sealed class SupervisorVisionRoutes(IVisionSupervisorData data, string? token, RenderGate gate, TimeProvider? time = null)
{
    private readonly byte[]? _token = string.IsNullOrEmpty(token) ? null : Encoding.UTF8.GetBytes(token);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static bool IsVisionPath(string path)
    {
        var p = path.TrimEnd('/');
        if (!p.StartsWith("/api/bodies/", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = p["/api/bodies/".Length..];
        int slash = rest.IndexOf('/');
        return slash > 0 && (rest[(slash + 1)..].Equals("vision", StringComparison.OrdinalIgnoreCase)
            || rest[(slash + 1)..].StartsWith("vision/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Null = not a vision route.</summary>
    public async Task<SupervisorReply?> TryHandleAsync(SupervisorRequest r, CancellationToken ct = default)
    {
        var path = r.Path.TrimEnd('/');
        if (!IsVisionPath(path)) return null;
        if (!r.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !r.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            return Json(405, new { error = "read-only: GET only" }, new Dictionary<string, string> { ["Allow"] = "GET, HEAD" });
        if (!Authorized(r)) return Json(403, new { error = "token required (X-Supervisor-Token header or ?token=)" });
        var rest = path["/api/bodies/".Length..];
        int slash = rest.IndexOf('/');
        var body = Uri.UnescapeDataString(rest[..slash]);
        var tail = rest[(slash + 1)..]["vision".Length..].TrimStart('/');
        try
        {
            if (tail.Length == 0) return await ListAsync(r, body, ct).ConfigureAwait(false);
            if (tail.Equals("render", StringComparison.OrdinalIgnoreCase)) return await RenderAsync(r, body, ct).ConfigureAwait(false);
            return await SheetAsync(body, tail, ct).ConfigureAwait(false);
        }
        catch (RenderBusyException) { return Json(503, new { error = "renderer busy, retry" }, new Dictionary<string, string> { ["Retry-After"] = "1" }); }
        catch (OperationCanceledException) { return Json(503, new { error = "cancelled" }); }
        catch (Exception ex) { return Json(500, new { error = ex.Message }); }
    }

    /// <summary>GET /api/bodies/{body}/vision[?since=]: the body's kept sheets.</summary>
    private async Task<SupervisorReply> ListAsync(SupervisorRequest r, string body, CancellationToken ct)
    {
        long since = long.TryParse(Q(r, "since"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var list = await data.ListAsync(body, since, ct).ConfigureAwait(false);
        return list is { } l ? Json(200, new { at = _time.GetUtcNow(), body = l.Body, capacity = l.Capacity, note = l.Note, sheets = l.Sheets })
            : Json(404, new { error = $"no body '{body}' online" });
    }

    /// <summary>GET /api/bodies/{body}/vision/render?see=&amp;meta=: a fresh render (the PNG, or its numbers with meta).</summary>
    private async Task<SupervisorReply> RenderAsync(SupervisorRequest r, string body, CancellationToken ct)
    {
        var see = Q(r, "see") ?? "{}";
        try { using var _ = JsonDocument.Parse(see); }
        catch (JsonException) { return Json(400, new { error = "see must be the see tool's input JSON, e.g. {\"views\":[\"iso yaw=200\"]}" }); }
        var img = await data.RenderAsync(body, see, gate, ct).ConfigureAwait(false);
        if (img == null) return Json(404, new { error = $"no body '{body}' online" });
        if (img.Png.Length == 0) return Json(400, new { error = img.Note ?? "nothing rendered" });
        if (Q(r, "meta") is "1" or "true")
            return Json(200, new { width = img.Width, height = img.Height, tokens = img.Tokens, bytes = img.Png.Length, legend = img.Legend, note = img.Note });
        return Png(img.Png, img.Width, img.Height, img.Tokens);
    }

    /// <summary>GET /api/bodies/{body}/vision/{id}[.png]: one kept sheet's info, or its PNG.</summary>
    private async Task<SupervisorReply> SheetAsync(string body, string tail, CancellationToken ct)
    {
        bool png = tail.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        if (!long.TryParse(png ? tail[..^4] : tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return Json(404, new { error = "not found" });
        if (png)
        {
            var bytes = await data.PngAsync(body, id, ct).ConfigureAwait(false);
            if (bytes == null) return Json(404, new { error = $"sheet {id} is no longer kept for '{body}'" });
            var (w, h) = Size(bytes);
            return Png(bytes, w, h, (w * h + 749) / 750);
        }
        var all = await data.ListAsync(body, 0, ct).ConfigureAwait(false);
        if (all == null) return Json(404, new { error = $"no body '{body}' online" });
        return all.Sheets.FirstOrDefault(x => x.Id == id) is { } info ? Json(200, info) : Json(404, new { error = $"sheet {id} is no longer kept for '{body}'" });
    }

    /// <summary>Width/height from the PNG's IHDR (no decode).</summary>
    public static (int W, int H) Size(byte[] png) =>
        png.Length >= 24 ? (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))) : (0, 0);

    private static SupervisorReply Png(byte[] png, int w, int h, int tokens) => new(200, "image/png", png, new Dictionary<string, string>
    {
        ["X-Sha256"] = VisionHistory.Hash(png),
        ["X-Tokens"] = tokens.ToString(CultureInfo.InvariantCulture),
        ["X-Width"] = w.ToString(CultureInfo.InvariantCulture),
        ["X-Height"] = h.ToString(CultureInfo.InvariantCulture),
    });

    private static string? Q(SupervisorRequest r, string k) => r.Query.TryGetValue(k, out var v) ? v : null;

    private bool Authorized(SupervisorRequest r)
    {
        if (_token == null) return true;
        var given = r.TokenHeader ?? Q(r, "token");
        if (string.IsNullOrEmpty(given)) return false;
        var b = Encoding.UTF8.GetBytes(given);
        return b.Length == _token.Length && CryptographicOperations.FixedTimeEquals(b, _token);
    }

    private static SupervisorReply Json(int status, object value, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, "application/json; charset=utf-8", SupervisorJson.Bytes(value), headers);
}
