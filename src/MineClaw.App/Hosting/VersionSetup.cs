using MineClaw.Client.Versioning;
using MineClaw.Protocol.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// The version decision of the composition root (docs/design/multi-version.md, "Version detection"): runs once, before
/// any client, body or clone is built. <c>Bot:Server:Version</c> (or <c>--server-version</c>): auto (default) | b1.8.1 |
/// 1.12.2. Auto pings <c>Minecraft:Host:Port</c>; an answer is remembered in <c>&lt;data&gt;/server-version.txt</c> as the
/// fallback for the next start while the server is down. Hive clones get the resolved id on their command line, so the
/// whole hive speaks one version and only main pings.
/// </summary>
public static class VersionSetup
{
    public const string ConfigKey = "Bot:Server:Version";
    public const string LastKnownFile = "server-version.txt";

    public sealed record Outcome(GameVersionProfile? Profile, VersionResolution? Resolution, string? Error);

    public static async Task<Outcome> ResolveAsync(IConfiguration config, MinecraftSettings mc, string dataDir, ILogger log, CancellationToken ct,
        Func<CancellationToken, Task<ServerPingResult>>? ping = null)
    {
        var configured = config[ConfigKey];
        double timeoutS = double.TryParse(config["Bot:Server:PingTimeoutSeconds"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 4;
        ping ??= c => ServerPing.PingAsync(mc.Host, mc.Port, TimeSpan.FromSeconds(timeoutS), c);
        var path = Path.Combine(dataDir, LastKnownFile);
        try
        {
            var res = await VersionResolver.ResolveAsync(configured, ping, ReadLastKnown(path), ct).ConfigureAwait(false);
            var profile = GameVersionProfiles.For(res.Version);
            if (res.Detected) WriteLastKnown(path, res.Version, log);
            log.LogInformation("game version {Version}: {How}", res.Version, res.How);
            if (!res.Detected && res.How.StartsWith("server didn't answer", StringComparison.Ordinal))
                log.LogWarning("{Server}:{Port} didn't answer the version ping; speaking {Version} (set {Key} to pin it)", mc.Host, mc.Port, res.Version.Id, ConfigKey);
            return new Outcome(profile, res, null);
        }
        catch (UnsupportedVersionException ex)
        {
            return new Outcome(null, null, ex.Message);
        }
    }

    internal static GameVersion? ReadLastKnown(string path)
    {
        try
        {
            return File.Exists(path) && GameVersion.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteLastKnown(string path, GameVersion v, ILogger log)
    {
        try
        {
            if (ReadLastKnown(path) != v) File.WriteAllText(path, v.Id + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug("can't remember the server version in {Path}: {Error}", path, ex.Message);
        }
    }
}
