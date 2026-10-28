using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Telemetry;

namespace MineClaw.App.Supervisor;

/// <summary>What the controller routes read (the live hive: <see cref="SupervisorControllerData"/>; tests use a fake).</summary>
public interface IControllerSupervisorData
{
    /// <summary>What drives the body (null = no such body).</summary>
    Task<BodyControllerInfo?> ControllerAsync(string body, CancellationToken ct);
    /// <summary>A brain's context page since a version (null = no such brain or it didn't answer).</summary>
    Task<BrainContextPage?> ContextAsync(string brain, long since, string? body, CancellationToken ct);
    /// <summary>One turn in full; <c>Found</c> false = no such brain (a null turn with Found = the turn left the ring).</summary>
    Task<(bool Found, BrainTurnDetail? Turn)> TurnAsync(string brain, long turn, string? body, CancellationToken ct);
}

/// <summary>
/// The read-only Controller routes (GET), for the Explorer's Controller panel:
/// <list type="bullet">
/// <item><c>/api/bodies/{name}/controller</c> → <see cref="BodyControllerInfo"/> (kind main | hive | subhive | script, owner, brain id and
/// model, the panel's tabs and links)</item>
/// <item><c>/api/brains/{id}/context?since=&amp;body=</c> → <see cref="BrainContextPage"/> (turn summaries changed after the version
/// <c>since</c>, the live context window when it changed, evicted turn ids; <c>body</c> = the subhive filter)</item>
/// <item><c>/api/brains/{id}/turn/{n}?body=</c> → <see cref="BrainTurnDetail"/> (every call: what it added, the response with thinking
/// and tool calls, usage, cost, latency; linked sub-entries)</item>
/// </list>
/// They carry the brain's prompts, memory and thinking, so they need the token like the script routes and answer 403 without it.
/// Everything is redacted by the capture (<see cref="BrainContextCapture.Redact"/>); API keys and headers are never captured.
/// </summary>
public sealed class SupervisorControllerRoutes(IControllerSupervisorData data, string? token, TimeProvider? time = null)
{
    private readonly byte[]? _token = string.IsNullOrEmpty(token) ? null : Encoding.UTF8.GetBytes(token);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static bool IsControllerPath(string path)
    {
        var p = path.TrimEnd('/');
        return p.StartsWith("/api/brains/", StringComparison.OrdinalIgnoreCase)
            || (p.StartsWith("/api/bodies/", StringComparison.OrdinalIgnoreCase) && p.EndsWith("/controller", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Null = not a controller route.</summary>
    public async Task<SupervisorReply?> TryHandleAsync(SupervisorRequest r, CancellationToken ct = default)
    {
        var path = r.Path.TrimEnd('/');
        if (!IsControllerPath(path)) return null;
        if (!r.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !r.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            return Json(405, new { error = "read-only: GET only" }, new Dictionary<string, string> { ["Allow"] = "GET, HEAD" });
        if (!Authorized(r)) return Json(403, new { error = "token required (X-Supervisor-Token header or ?token=)" });
        try
        {
            string? body = r.Query.TryGetValue("body", out var b) && b.Trim().Length > 0 ? b.Trim() : null;
            if (path.StartsWith("/api/bodies/", StringComparison.OrdinalIgnoreCase))
            {
                var name = Uri.UnescapeDataString(path["/api/bodies/".Length..^"/controller".Length]);
                if (name.Length == 0) return Json(404, new { error = "not found" });
                return await data.ControllerAsync(name, ct).ConfigureAwait(false) is { } info ? Json(200, info) : Json(404, new { error = $"no body '{name}' online" });
            }
            var parts = path["/api/brains/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            if (parts.Length == 2 && parts[1].Equals("context", StringComparison.OrdinalIgnoreCase))
            {
                long since = r.Query.TryGetValue("since", out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv) ? Math.Max(0, sv) : 0;
                return await data.ContextAsync(parts[0], since, body, ct).ConfigureAwait(false) is { } page
                    ? Json(200, page) : Json(404, new { error = $"no brain '{parts[0]}' (offline, or it didn't answer)" });
            }
            if (parts.Length == 3 && parts[1].Equals("turn", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1) return Json(400, new { error = "turn must be a number" });
                var (found, turn) = await data.TurnAsync(parts[0], n, body, ct).ConfigureAwait(false);
                if (!found) return Json(404, new { error = $"no brain '{parts[0]}' (offline, or it didn't answer)" });
                return turn is { } t ? Json(200, t) : Json(404, new { error = $"turn {n} is no longer kept (the ring holds the last turns only)" });
            }
            return Json(404, new { error = "not found" });
        }
        catch (OperationCanceledException) { return Json(503, new { error = "cancelled" }); }
        catch (Exception ex) { return Json(500, new { error = ex.Message, at = _time.GetUtcNow() }); }
    }

    private bool Authorized(SupervisorRequest r)
    {
        if (_token == null) return true;
        var given = r.TokenHeader ?? (r.Query.TryGetValue("token", out var q) ? q : null);
        if (string.IsNullOrEmpty(given)) return false;
        var b = Encoding.UTF8.GetBytes(given);
        return b.Length == _token.Length && CryptographicOperations.FixedTimeEquals(b, _token);
    }

    private static SupervisorReply Json(int status, object value, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, "application/json; charset=utf-8", SupervisorJson.Bytes(value), headers);
}
