namespace MineClaw.Protocol.Versioning;

/// <summary>Which version this process speaks and why (logged once at startup).</summary>
/// <param name="Detected">True when the server's ping named it (not config, not a fallback).</param>
public sealed record VersionResolution(GameVersion Version, string How, bool Detected, ServerPingResult? Ping = null);

/// <summary>The server speaks a version MineClaw has no name for (or config names one it doesn't know).</summary>
public sealed class UnsupportedVersionException(string message) : Exception(message);

/// <summary>
/// Resolves the game version once, before the client graph is built (docs/design/multi-version.md, "Version detection"):
/// an explicit <c>Bot:Server:Version</c> wins without a ping; <c>auto</c> (the default) pings the server; a server that
/// answers neither ping (down, firewalled) falls back to the last version detected for it, else b1.8.1, so a bot started
/// while the server is offline still starts and retries its connect exactly as before.
/// </summary>
public static class VersionResolver
{
    public static async Task<VersionResolution> ResolveAsync(string? configured, Func<CancellationToken, Task<ServerPingResult>> ping,
        GameVersion? lastKnown = null, CancellationToken ct = default)
    {
        if (!GameVersion.TryParse(configured, out var explicitVersion))
            throw new UnsupportedVersionException(
                $"Bot:Server:Version '{configured}' is not a version MineClaw knows (auto | {string.Join(" | ", GameVersion.Known.Select(k => k.Id))})");
        if (explicitVersion is not null) return new VersionResolution(explicitVersion, "configured (Bot:Server:Version)", Detected: false);

        ServerPingResult result;
        try
        {
            result = await ping(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnanswered(ex, ct))
        {
            return Fallback(ex, lastKnown);
        }
        var v = result.Version ?? throw Unsupported(result);
        return new VersionResolution(v, "detected by " + result.Describe(), Detected: true, result);
    }

    /// <summary>The ping failed the way an offline server does (not a cancellation the caller asked for).</summary>
    private static bool IsUnanswered(Exception ex, CancellationToken ct) =>
        ex is ServerPingException or IOException or TimeoutException or System.Net.Sockets.SocketException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    /// <summary>No answer: the last version detected for this server, else b1.8.1.</summary>
    private static VersionResolution Fallback(Exception ex, GameVersion? lastKnown)
    {
        var fallback = lastKnown ?? GameVersion.Beta181;
        return new VersionResolution(fallback,
            $"server didn't answer the ping ({ex.Message}); using {(lastKnown is null ? "the default" : "the last detected version")} {fallback.Id}",
            Detected: false);
    }

    private static UnsupportedVersionException Unsupported(ServerPingResult result) => new(
        $"the server is {result.VersionName ?? "an unknown version"}{(result.Protocol is { } p ? $" (protocol {p})" : "")}; " +
        $"MineClaw speaks {string.Join(", ", GameVersion.Known.Select(k => k.ToString()))}. Set Bot:Server:Version to force one.");
}
