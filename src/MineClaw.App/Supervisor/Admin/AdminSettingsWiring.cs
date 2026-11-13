using System.Diagnostics;
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
        if (_ctx is not { } c) return null;
        var probe = new LiveRestartProbe(() => client.World, () => client.IsPlaying,
            () => bodies.Rows().Select(r => new SafetyBody(r.Name, r.Online, r.Dim, r.X, r.Y, r.Z, r.HasPos)));
        return Build(c.Config, c.Settings, o, bot.Runtime.Options, probe, main, bot.Runtime.Options.GameVersion, c.Restart, log);
    }

    /// <summary>
    /// Setup mode (<see cref="SetupMode"/>): the same routes without a bot or a game client. A restart is always safe (nobody is in
    /// game) and the node has no game version yet. Null when the override layer is off.
    /// </summary>
    public static AdminSettingsRoutes? SetupRoutes(IConfigurationRoot config, AppSettings settings, Action<string> restart, ILogger log,
        IProviderTester? tester = null) =>
        Build(config, settings, settings.Supervisor, settings.Agent, new SetupRestartProbe(),
            string.IsNullOrWhiteSpace(settings.Mc.Username) ? "main" : settings.Mc.Username, null, restart, log, tester);

    /// <summary>The service over the override file, with the running options objects live settings are written to.</summary>
    private static AdminSettingsRoutes? Build(IConfigurationRoot config, AppSettings s, SupervisorOptions o, MineClaw.Agent.AgentOptions agent,
        IRestartSafetyProbe probe, string main, string? gameVersion, Action<string> restart, ILogger log, IProviderTester? tester = null)
    {
        if (SettingsOverrideFile.Of(config) is not { } layer) return null;
        var store = new SettingsOverrideStore(layer.FilePath);
        var live = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent"] = agent, ["Supervisor"] = o, ["Host"] = s.Host, ["Hive"] = s.Hive, ["Telemetry"] = s.Telemetry,
            ["Minecraft"] = s.Mc, ["Bodies"] = s.Bodies,
        };
        DateTimeOffset? started = null;
        try { started = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero); } catch (Exception) { }
        var node = new AdminNodeInfo("local", main, AppVersion.Informational, started, gameVersion);
        var svc = new AdminSettingsService(config, store, live, node, probe, tester ?? new LiveProviderTester(), restart, log);
        log.LogInformation("supervisor: administration routes on (override file {Path})", layer.FilePath);
        return new AdminSettingsRoutes(o, svc, log);
    }
}

/// <summary>Setup mode's restart check: no body is in game, so a restart is always safe.</summary>
public sealed class SetupRestartProbe(TimeProvider? time = null) : IRestartSafetyProbe
{
    public RestartSafetyResult Check() => new(true, (time ?? TimeProvider.System).GetUtcNow(), null, null, [], []);
}
