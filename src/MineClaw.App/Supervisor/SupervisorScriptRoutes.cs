using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Routines;
using MineClaw.Scripting;

namespace MineClaw.App.Supervisor;

/// <summary>A body that runs a script right now (the Live Hive tiles' "script" badge).</summary>
/// <param name="Owner">The owning hive's login for a hive's subhive body (null otherwise).</param>
public sealed record LiveScriptRow(string Body, string Short, string Script, string Status, int Line = 0, bool Paused = false, int? Version = null,
    string? Role = null, string? Owner = null);

/// <summary>What the script routes read: the saved library, one version, the live debugger snapshot of a body, who runs what.</summary>
public interface IScriptSupervisorData
{
    IReadOnlyList<ScriptLibraryEntry> Library();
    /// <summary>One version (null = the active one) of a saved script, or null.</summary>
    ScriptVersionDetail? Version(string name, int? version);
    /// <summary>The live debugger snapshot of the script <paramref name="body"/> runs (or its last run), null = no such body.</summary>
    Task<BodyScriptReply?> LiveAsync(string body, bool code, CancellationToken ct);
    IReadOnlyList<LiveScriptRow> Running();
}

/// <summary>The reply of <c>/api/bodies/{name}/script</c>: running or not, and the snapshot (the last run's when it ended).</summary>
public sealed record BodyScriptReply(string Body, bool Running, ScriptDebugSnapshot? Snapshot, string? Note = null);

/// <summary>
/// The read-only script routes (GET):
/// <list type="bullet">
/// <item><c>/api/scripts</c> → <c>{at, scripts:[ScriptLibraryEntry]}</c> (the saved library)</item>
/// <item><c>/api/scripts/{name}</c>, <c>/api/scripts/{name}/{version}</c> → ScriptVersionDetail (code, lint, params, history, runs)</item>
/// <item><c>/api/bodies/scripts</c> → <c>{at, bodies:[LiveScriptRow]}</c> (who runs a script: the tiles' badge)</item>
/// <item><c>/api/bodies/{name}/script?code=1</c> → BodyScriptReply (the live debugger snapshot; asking keeps the script's
/// ≈ 5 Hz sampling on for a few seconds, see <c>ScriptSession.WatchLease</c>)</item>
/// </list>
/// They carry source code and live variable values, so they are token-gated like the rest of the API but answer 403 (not 401)
/// without the token. Read-only: there is no pause/step/param route (TODO(debugger pause/resume): an admin write under
/// <c>/api/admin/scripts/{body}/pause|resume</c>, see <see cref="ScriptDebugSnapshot.Controls"/>).
/// </summary>
public sealed class SupervisorScriptRoutes(IScriptSupervisorData data, string? token, TimeProvider? time = null)
{
    private readonly byte[]? _token = string.IsNullOrEmpty(token) ? null : Encoding.UTF8.GetBytes(token);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static bool IsScriptPath(string path)
    {
        var p = path.TrimEnd('/');
        return p.Equals("/api/scripts", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/api/scripts/", StringComparison.OrdinalIgnoreCase)
            || (p.StartsWith("/api/bodies/", StringComparison.OrdinalIgnoreCase) && (p.EndsWith("/script", StringComparison.OrdinalIgnoreCase)
                || p.Equals("/api/bodies/scripts", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Null = not a script route.</summary>
    public async Task<SupervisorReply?> TryHandleAsync(SupervisorRequest r, CancellationToken ct = default)
    {
        var path = r.Path.TrimEnd('/');
        if (!IsScriptPath(path)) return null;
        if (!r.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !r.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            return Json(405, new { error = "read-only: GET only" }, new Dictionary<string, string> { ["Allow"] = "GET, HEAD" });
        if (!Authorized(r)) return Json(403, new { error = "token required (X-Supervisor-Token header or ?token=)" });
        try
        {
            if (path.Equals("/api/scripts", StringComparison.OrdinalIgnoreCase))
                return Json(200, new { at = _time.GetUtcNow(), scripts = data.Library() });
            if (path.Equals("/api/bodies/scripts", StringComparison.OrdinalIgnoreCase))
                return Json(200, new { at = _time.GetUtcNow(), bodies = data.Running() });
            if (path.StartsWith("/api/scripts/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = path["/api/scripts/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
                if (parts.Length is 0 or > 2) return Json(404, new { error = "not found" });
                int? v = null;
                if (parts.Length == 2)
                {
                    var vs = parts[1].TrimStart('v', 'V');
                    if (!int.TryParse(vs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vi) || vi < 1) return Json(400, new { error = "version must be a number (3 or v3)" });
                    v = vi;
                }
                return data.Version(parts[0], v) is { } d ? Json(200, d) : Json(404, new { error = v is { } x ? $"no v{x} of script '{parts[0]}'" : $"no saved script '{parts[0]}'" });
            }
            // /api/bodies/{name}/script
            var body = Uri.UnescapeDataString(path["/api/bodies/".Length..^"/script".Length]);
            if (body.Length == 0) return Json(404, new { error = "not found" });
            bool code = r.Query.TryGetValue("code", out var c) && c is "1" or "true";
            var reply = await data.LiveAsync(body, code, ct).ConfigureAwait(false);
            return reply is { } rep ? Json(200, rep) : Json(404, new { error = $"no body '{body}' online" });
        }
        catch (OperationCanceledException) { return Json(503, new { error = "cancelled" }); }
        catch (Exception ex) { return Json(500, new { error = ex.Message }); }
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
