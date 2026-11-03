using System.Diagnostics;
using System.Reflection;
using MineClaw.App.Hosting;
using MineClaw.Bot;
using MineClaw.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Supervisor.Admin;

/// <summary>
/// Hands the Administration routes what only the host knows: the layered config (with its override layer), the bound settings
/// main runs with (the live objects live settings are written to) and the graceful-restart hook (systemd starts the bot again).
/// </summary>
public static class AdminSettingsWiring
{
    private sealed record Context(IConfigurationRoot Config, AppSettings Settings, Action<string> Restart);

    private static Context? _ctx;

    /// <summary>Main's host, once: the config it started from, its settings and how to ask for a graceful restart.</summary>
    public static void Configure(IConfigurationRoot config, AppSettings settings, Action<string> restart) => _ctx = new Context(config, settings, restart);

    /// <summary>The routes for this process's supervisor; null when the host didn't configure them or the override layer is off.</summary>
    public static AdminSettingsRoutes? Routes(SupervisorOptions o, BotHost bot, MinecraftClient client, HiveBodyList bodies, string main, ILogger log)
    {
        if (_ctx is not { } c || SettingsOverrideFile.Of(c.Config) is not { } layer) return null;
        var store = new SettingsOverrideStore(layer.FilePath);
        var s = c.Settings;
        var live = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent"] = bot.Runtime.Options, ["Supervisor"] = o, ["Host"] = s.Host, ["Hive"] = s.Hive, ["Telemetry"] = s.Telemetry,
            ["Minecraft"] = s.Mc, ["Bodies"] = s.Bodies,
        };
        var probe = new LiveRestartProbe(() => client.World, () => client.IsPlaying,
            () => bodies.Rows().Select(r => new SafetyBody(r.Name, r.Online, r.Dim, r.X, r.Y, r.Z, r.HasPos)));
        DateTimeOffset? started = null;
        try { started = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero); } catch (Exception) { }
        var version = typeof(AdminSettingsWiring).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var node = new AdminNodeInfo("local", main, version, started, bot.Runtime.Options.GameVersion);
        var svc = new AdminSettingsService(c.Config, store, live, node, probe, new LiveProviderTester(), c.Restart, log);
        log.LogInformation("supervisor: administration routes on (override file {Path})", layer.FilePath);
        return new AdminSettingsRoutes(o, svc, log);
    }
}
