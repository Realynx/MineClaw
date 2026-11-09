using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MineClaw.App.Supervisor;

/// <summary>Authenticated human control for one pawn. Reads remain on the existing viewer routes;
/// all lease mutations require the bridge's user identity and Story permission.</summary>
public sealed partial class SupervisorPawnControlRoutes(SupervisorOptions options, PawnControlSessions sessions) : IDisposable
{
    private static readonly JsonSerializerOptions Json = SupervisorJson.Options;
    private static readonly HashSet<string> Actions = ["attack", "dig", "cancel", "release", "use", "inventory", "inventory_click", "close_inventory", "drop"];
    public static bool IsControlPath(string path) => path.StartsWith("/api/bodies/", StringComparison.OrdinalIgnoreCase)
        && path.Contains("/control/", StringComparison.OrdinalIgnoreCase);

    public static async Task<string?> ReadBodyAsync(HttpListenerRequest request, SupervisorOptions options, CancellationToken ct)
    {
        if (request.HttpMethod != "POST" || !IsControlPath(request.Url?.AbsolutePath ?? "") || !request.HasEntityBody
            || !SupervisorStoryRoutes.Writes(options) || !TokenOk(request.Headers["X-Supervisor-Token"], options.Token)) return null;
        return await SupervisorAdminRoutes.ReadCappedAsync(request, 4096, 4096, ct).ConfigureAwait(false);
    }

    public async Task<SupervisorReply?> TryHandleAsync(SupervisorRequest request, CancellationToken ct = default)
    {
        if (!IsControlPath(request.Path)) return null;
        if (!SupervisorStoryRoutes.Writes(options)) return Reply(403, new { error = "Story control is disabled." });
        if (request.Method != "POST") return Reply(405, new { error = "POST required." });
        if (!TokenOk(request.TokenHeader, options.Token)) return Reply(403, new { error = "Supervisor token required." });
        var owner = Header(request, "X-MCX-User");
        var roles = (Header(request, "X-MCX-Roles") ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128 || !roles.Any(r => r is "story" or "admin"))
            return Reply(403, new { error = "Authenticated Story permission required." });
        try
        {
            var parts = request.Path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 || parts[3] != "control") return Reply(404, new { error = "Unknown control route." });
            var body = Uri.UnescapeDataString(parts[2]);
            if (body.Length is 0 or > 128 || body.Contains('/')) throw new PawnControlException(400, "Invalid body name.");
            if (request.Body is not { Length: > 0 and <= 4096 }) throw new PawnControlException(400, "A control request body is required (maximum 4096 characters).");
            using var doc = JsonDocument.Parse(request.Body);
            var json = doc.RootElement;
            if (json.ValueKind != JsonValueKind.Object) throw new PawnControlException(400, "Control request must be an object.");
            if (parts[4] == "acquire") return Reply(200, await sessions.AcquireAsync(body, owner, Text(json, "request_id")).WaitAsync(ct).ConfigureAwait(false));
            string session = Text(json, "session");
            switch (parts[4])
            {
                case "input":
                    RequireSequence(json);
                    var input = json.Deserialize<PawnControlInput>(Json) ?? throw new PawnControlException(400, "Invalid input.");
                    ValidateInput(input);
                    return Reply(200, sessions.Input(body, owner, session, input));
                case "action":
                    RequireSequence(json);
                    var action = json.Deserialize<PawnControlAction>(Json) ?? throw new PawnControlException(400, "Invalid action.");
                    ValidateAction(action);
                    return Reply(200, await sessions.AcknowledgeActionAsync(body, owner, session, action, ct).ConfigureAwait(false));
                case "release": return Reply(200, new { ok = true, state = sessions.Release(body, owner, session) });
                default: return Reply(404, new { error = "Unknown control route." });
            }
        }
        catch (PawnControlException ex) { return Reply(ex.Status, new { error = ex.Message }); }
        catch (JsonException) { return Reply(400, new { error = "Invalid control request." }); }
        catch (OperationCanceledException) { return Reply(503, new { error = "Control request cancelled." }); }
    }

    private static void ValidateInput(PawnControlInput input)
    {
        if (input.Seq < 0 || !float.IsFinite(input.Forward) || input.Forward is < -1 or > 1
            || !float.IsFinite(input.Strafe) || input.Strafe is < -1 or > 1 || !float.IsFinite(input.Yaw) || Math.Abs(input.Yaw) > 1_000_000
            || !float.IsFinite(input.Pitch) || input.Pitch is < -90 or > 90 || input.Hotbar is < 0 or > 8)
            throw new PawnControlException(400, "Invalid movement, look, sequence, or hotbar input.");
    }

    private static void RequireSequence(JsonElement json)
    {
        if (!json.TryGetProperty("seq", out var seq) || seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt64(out var n) || n < 0)
            throw new PawnControlException(400, "A nonnegative integer sequence is required.");
    }

    private static void ValidateAction(PawnControlAction action)
    {
        if (action.Seq < 0 || !Actions.Contains(action.Kind) || action.Face is < 0 or > 5 || action.EntityId is <= 0
            || action.Button is < 0 or > 1 || action.WindowId is < 0 or > 127
            || action.Slot is { } slot && slot != -999 && slot is < 0 or > 127
            || action.Target is { } target && (Math.Abs((long)target.X) > 30_000_000 || Math.Abs((long)target.Z) > 30_000_000 || target.Y is < -64 or > 4096))
            throw new PawnControlException(400, "Invalid control action.");
        if (action.Kind == "dig" && action.Target == null) throw new PawnControlException(400, "Dig needs a target block.");
        if (action.Kind == "inventory_click" && (action.Slot == null || action.WindowId == null)) throw new PawnControlException(400, "Inventory clicks need the current windowId and slot.");
    }

    private static string Text(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 128 } s
        ? s : throw new PawnControlException(400, key + " is required (1–128 characters).");
    private static string? Header(SupervisorRequest request, string key) => request.Headers?.FirstOrDefault(h => h.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    private static bool TokenOk(string? given, string? token) => !string.IsNullOrEmpty(given) && !string.IsNullOrEmpty(token)
        && given.Length == token.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token));
    private static SupervisorReply Reply(int status, object value) => new(status, "application/json; charset=utf-8", SupervisorJson.Bytes(value));
    public void Dispose() => sessions.Dispose();
}
