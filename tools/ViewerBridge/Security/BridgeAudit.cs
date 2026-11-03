using System.Text.Json;

namespace MineClaw.ViewerBridge.Security;

/// <summary>
/// The bridge's own write trail: one JSONL line per non-GET request (who, player, roles, method, path, status, time). Bodies are
/// never logged (chat text, shard bytes); the bot keeps its own admin audit of what it applied.
/// </summary>
public sealed class BridgeAudit(BridgeConfig config, TimeProvider time, ILogger<BridgeAudit> log)
{
    private readonly object _lock = new();
    private bool _warned;

    public sealed record Line(DateTimeOffset At, string User, string Player, string Roles, string Method, string Path, int Status, double Ms, int BodyBytes, string? RequestId);

    public void Write(BridgeIdentity id, string method, string path, int status, TimeSpan elapsed, int bodyBytes)
    {
        var line = new Line(time.GetUtcNow(), id.User, id.Player, string.Join(',', id.Roles.Order(StringComparer.Ordinal)), method, path, status,
            Math.Round(elapsed.TotalMilliseconds, 1), bodyBytes, id.RequestId);
        log.LogInformation("write {Method} {Path} by {User} → {Status}", method, path, id.User, status);
        try
        {
            var file = config.AuditLog;
            lock (_lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                File.AppendAllText(file, JsonSerializer.Serialize(line, BridgeJson.Options) + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_warned) { _warned = true; log.LogWarning("bridge audit log not writable: {Message}", ex.Message); }
        }
    }
}
