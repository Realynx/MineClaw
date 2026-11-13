using MineClaw.App.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Supervisor.Admin;

/// <summary>
/// The supervisor a fresh install runs in setup mode (<see cref="SetupMode"/>): no bot, no game client, just what the web panel needs
/// to finish the settings. The Administration routes (<c>/api/admin/settings*</c>, <c>/api/admin/ollama/*</c>, <c>/api/admin/restart*</c>)
/// with the same URLs, gate and JSON as a running bot; the reads the bridge's health probe and the shell poll (<c>/api/bots</c> = no
/// bots, <c>/api/status</c>, <c>/api/events</c>, <c>/api/hive/tree</c>); everything that needs a live bot is 503
/// <c>{error:"setup", message}</c>.
/// </summary>
public static class SetupSupervisor
{
    public const string Message = "the bot is in setup mode: finish the settings in Administration";

    /// <summary>The paths that answer in setup mode (besides them: 503).</summary>
    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal)
    {
        "", "/index.html", "/favicon.ico", "/api/bots", "/api/status", "/api/events", "/api/hive/tree",
    };

    public static bool Serves(string path) => AdminSettingsRoutes.IsSettingsPath(path) || Reads.Contains(path.TrimEnd('/'));

    /// <summary>A live-only route in setup mode.</summary>
    public static SupervisorReply Unavailable() =>
        new(503, "application/json; charset=utf-8", SupervisorJson.Bytes(new { error = "setup", message = Message }),
            new Dictionary<string, string> { ["Retry-After"] = "30" });

    /// <summary>
    /// The setup supervisor (not started): the Administration routes over the config's override file, <paramref name="restart"/> as
    /// the graceful restart (the host exits 0 and its service manager starts it again). Null when the override layer is off.
    /// </summary>
    public static SupervisorServer? Create(IConfigurationRoot config, AppSettings settings, Action<string> restart, ILogger log, IProviderTester? tester = null)
    {
        if (AdminSettingsWiring.SetupRoutes(config, settings, restart, log, tester) is not { } routes) return null;
        return new SupervisorServer(settings.Supervisor, new SetupSupervisorData(), log)
        {
            SetupOnly = true,
            Settings = routes,
            HiveTree = () => new { id = "main", name = routes.Service.Node.Name, kind = "main", state = "setup", children = Array.Empty<object>() },
        };
    }
}

/// <summary>Setup mode's supervisor data: no bots, no events; the status says why. The render routes never reach it (503 first).</summary>
internal sealed class SetupSupervisorData : ISupervisorData
{
    public IReadOnlyList<BotTile> Bots() => [];
    public Task<ViewImage?> ViewAsync(string bot, string mode, int radius, RenderGate gate, CancellationToken ct) => Task.FromResult<ViewImage?>(null);
    public Task<ViewImage> HiveAsync(RenderGate gate, CancellationToken ct) => throw new KeyNotFoundException("setup mode");
    public Task<ViewImage> CameraAsync(CameraQuery q, RenderGate gate, CancellationToken ct) => throw new KeyNotFoundException("setup mode");
    public object Status(string? selected) => new { setup = true, message = SetupSupervisor.Message };
    public IReadOnlyList<FeedEvent> Events(long since, int max) => [];
    public LocateResult? Locate(string query) => null;
}
