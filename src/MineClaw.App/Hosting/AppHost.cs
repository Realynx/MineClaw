using MineClaw.Agent;

using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Sources;
using MineClaw.App.Tui;
using MineClaw.App.Viewport;
using MineClaw.Bot;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Hive;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.App.Hosting;

/// <summary>
/// The real run: config → logging → key check → bot → TUI or headless → connect (retrying) → run until a signal, /quit,
/// q in the TUI or --minutes → graceful, time-boxed shutdown → exit code.
/// </summary>
public static partial class AppHost
{
    /// <summary>Hive seam: runs before the bot disconnects on shutdown (e.g. "deposit items before logout"), inside
    /// Host:PreLogoutSeconds of the shutdown budget. Null = nothing.</summary>
    public static Func<BotHost, CancellationToken, Task>? BeforeLogout { get; set; }

    public static async Task<int> RunAsync(AppCli cli)
    {
        var root = AppPaths.FindRoot();
        if (cli.CloneName != null) cli = cli with { Tui = TuiChoice.Off, NoKeyCheck = true }; // hive clone: headless, main checked the key
        if (Configure(cli, root) is not { } s) return ExitCodes.Config;

        // ------------------------------------------------------------ console mode + logging
        var (useTui, why) = TerminalDetect.Decide(cli.Tui, TerminalDetect.Current());
        var fileLog = new FileLoggerProvider(s.LogDir);
        var ring = new RingLoggerProvider();
        var format = AppLogging.ResolveFormat(s.Settings.Host.LogFormat, TerminalDetect.UnderJournal());
        AppLogging.SetConsoleLevel(s.Settings.Host.ConsoleLogLevel);
        var loggers = AppLogging.Create(useTui, format, fileLog, ring);
        var log = loggers.CreateLogger("MineClaw.App");
        using var signals = new ShutdownSignals(sig => log.LogInformation("{Signal}: shutting down", sig));
        // The web's Administration page (Supervisor/Admin): the config it edits on top of, and its graceful restart (systemd restarts us).
        if (cli.CloneName == null) Supervisor.Admin.AdminSettingsWiring.Configure(s.Config, s.Settings, signals.Request);
        log.LogInformation("MineClaw starting (root {Root}, data {Data}, log {Log}, console {Mode}: {Why})", root, s.Settings.Agent.DataDirectory, s.LogDir,
            useTui ? "tui" : "headless/" + format, why);
        using var minutesTimer = MinutesTimer(cli, signals);

        try { return await RunLoggedAsync(s, useTui, ring, loggers, log, signals); }
        finally
        {
            loggers.Dispose();
            fileLog.Dispose();
        }
    }

    /// <summary>The configuration a run starts from: the bound settings and this process's log/telemetry directories.</summary>
    private sealed record StartupConfig(AppCli Cli, string Root, IConfigurationRoot Config, AppSettings Settings, string LogDir, string TelemetryDir,
        string MainLogDir);

    /// <summary>Config → settings → a clone's own overrides → the config check → the directories; null = exit with a config error.</summary>
    private static StartupConfig? Configure(AppCli cli, string root)
    {
        if (BuildConfig(cli, root) is not { } config) return null;
        var settings = AppSettings.Bind(config, root);
        // Hive processes started from here read the same override file (the web's Administration page writes it).
        if (cli.CloneName == null && SettingsOverrideFile.Of(config) is { } overrideLayer)
            Environment.SetEnvironmentVariable(SettingsOverrideFile.EnvVar, overrideLayer.FilePath);
        var (logDir, telemetryDir) = (settings.LogDir, settings.TelemetryDir);
        var mainLogDir = logDir;
        if (cli.CloneName != null) (logDir, telemetryDir) = CloneMode.Prepare(cli.CloneName, settings.Mc, settings.Agent, settings.Hive, logDir, telemetryDir);
        if (!ConfigIsValid(settings.Mc, settings.Agent) || !CreateDirectories(settings.Agent.DataDirectory, logDir, telemetryDir)) return null;
        return new StartupConfig(cli, root, config, settings, logDir, telemetryDir, mainLogDir);
    }

    /// <summary>With logging up: the provider checks, the game version, then the clone's or main's run.</summary>
    private static async Task<int> RunLoggedAsync(StartupConfig s, bool useTui, RingLoggerProvider ring, ILoggerFactory loggers, ILogger log,
        ShutdownSignals signals)
    {
        var (cli, root, settings) = (s.Cli, s.Root, s.Settings);
        var (mc, agentOptions, botOptions, host, hive) = (settings.Mc, settings.Agent, settings.Bot, settings.Host, settings.Hive);
        var (telemetryOptions, supervisorOptions, bodiesOptions) = (settings.Telemetry, settings.Supervisor, settings.Bodies);
        if (agentOptions.Llm.MockInUse)
            log.LogWarning("MOCK LLM provider enabled: deterministic offline script replies, not a real language model");
        // -------------------------------------------------------- key check (models API: free)
        if (!cli.NoKeyCheck && await CheckKeysAsync(agentOptions.Llm, useTui, log, signals.Token) is { } keyFailed) return keyFailed;
        if (cli.CloneName == null) WarnIfBrainIsNotOpus(agentOptions.Llm, log);
        // The game version, once, before any client graph exists (docs/design/multi-version.md): config, else a server ping.
        var version = await VersionSetup.ResolveAsync(s.Config, mc, agentOptions.DataDirectory, log, signals.Token);
        if (version.Profile is not { } profile) return VersionFailed(version.Error, useTui, log, signals.Token);
        cli = cli with { ServerVersion = profile.Version.Id }; // hive clones inherit the resolved version (MainHive pass-through)
        // b1.8.1's server throttles joins from one IP < ~4 s apart: one login at a time there; 1.12.2 logs bodies in in batches.
        // The db's packs decide skyblock vs a normal world for the whole process (WorldMode): this process's own runtime sets it.
        settings.ApplyVersion(profile, appliesWorldMode: true);
        if (cli.CloneName != null)
            return await CloneMode.RunAsync(cli, mc, agentOptions, botOptions, host, hive, telemetryOptions, s.TelemetryDir, loggers, log, signals, profile,
                bodiesOptions);
        // Hives in this process (Hive:Isolation=inproc) bind their own copy of every option, as a clone process would.
        var hives = new InProcHiveSettings(cli, () => AppSettings.Bind(AppConfig.Build(cli, root), root), profile, s.MainLogDir);
        return await RunBotAsync(cli, mc, agentOptions, botOptions, host, hive, telemetryOptions, s.TelemetryDir, useTui, ring, loggers, log, signals,
            s.MainLogDir, supervisorOptions, bodiesOptions, profile, hives);
    }

    /// <summary>The configuration, or null after telling the console why it can't be read.</summary>
    private static IConfigurationRoot? BuildConfig(AppCli cli, string root)
    {
        try { return AppConfig.Build(cli, root); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MineClaw: can't read the configuration: {ex.Message}");
            return null;
        }
    }

    /// <summary>False after listing on the console what the config needs.</summary>
    private static bool ConfigIsValid(MinecraftSettings mc, AgentOptions agentOptions)
    {
        var problems = ConfigCheck.Validate(mc, agentOptions);
        if (problems.Count == 0) return true;
        Console.Error.WriteLine("MineClaw can't start, the config needs attention:");
        foreach (var p in problems) Console.Error.WriteLine("  - " + p);
        Console.Error.WriteLine("Edit appsettings.json (copy appsettings.example.json), pass --config <file>, or set env vars like MINECLAW_Agent__Llm__ApiKey.");
        return false;
    }

    /// <summary>The data, log and telemetry directories; false after telling the console why one can't be made.</summary>
    private static bool CreateDirectories(string dataDir, string logDir, string telemetryDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(logDir);
            Directory.CreateDirectory(telemetryDir);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"MineClaw: can't create data/log directories: {ex.Message}");
            return false;
        }
    }

    /// <summary><c>--minutes</c>: a one-shot timer that asks for the shutdown; null without the flag.</summary>
    private static Timer? MinutesTimer(AppCli cli, ShutdownSignals signals) =>
        cli.Minutes is { } minutes ? new Timer(_ => signals.Request("--minutes elapsed"), null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan) : null;

    /// <summary>
    /// The startup provider checks: the Anthropic key (only the tiers that run there), the Ollama server (only when a tier uses
    /// it), then the Ollama profiles. Null = go on; otherwise the exit code.
    /// </summary>
    private static async Task<int?> CheckKeysAsync(LlmOptions llm, bool useTui, ILogger log, CancellationToken ct)
    {
        // Anthropic: only the tiers that run there (all three by default = the old check, unchanged).
        var anthropicModels = KeyCheck.AnthropicModels(llm);
        if (anthropicModels.Count > 0)
        {
            var check = await KeyCheck.RunAsync(llm.ApiKey!, anthropicModels, ct);
            if (!check.Ok) return CheckFailed("Anthropic API", check, ExitCodes.Auth, useTui, log, ct);
            log.LogInformation("Anthropic key ok: {Models}", check.Message);
        }
        // Ollama: only when a tier uses it (GET /api/tags + /api/show: the models exist and can call tools).
        if (llm.TiersUseOllama)
        {
            var oc = await KeyCheck.RunOllamaAsync(llm, ct: ct);
            if (!oc.Ok) return CheckFailed("Ollama", oc, ExitCodes.Config, useTui, log, ct);
            log.LogInformation("Ollama ok ({Endpoint}): {Models}", llm.Ollama.Endpoint, oc.Message);
        }
        if (llm.Ollama.IsConfigured)
            foreach (var note in await KeyCheck.CheckOllamaProfilesAsync(llm, ct: ct)) log.LogWarning("{Note}", note);
        return null;
    }

    /// <summary>A failed provider check: logged (and on the console under the TUI); a shutdown wins, then transient = try again later.</summary>
    private static int CheckFailed(string what, (bool Ok, string Message, bool Transient) check, int rejected, bool useTui, ILogger log, CancellationToken ct)
    {
        log.LogCritical("{What} check failed: {Message}", what, check.Message);
        if (useTui) Console.Error.WriteLine(what + " check failed: " + check.Message);
        return ct.IsCancellationRequested ? ExitCodes.Ok : check.Transient ? ExitCodes.TempFail : rejected;
    }

    /// <summary>Script authorship is keyed to the brain tier: a brain that isn't Opus 5.5 still writes Lua, so say so loudly.</summary>
    private static void WarnIfBrainIsNotOpus(LlmOptions llm, ILogger log)
    {
        if (LlmOptions.IsOpus55(llm.MainModel) && llm.ProviderFor(ModelTier.Main) == LlmProviders.Anthropic) return;
        log.LogWarning("brain tier runs {Provider}/{Model}, not Opus 5.5: it still writes Lua scripts (authorship belongs to the brain tier)",
            llm.ProviderFor(ModelTier.Main), llm.MainModel);
    }

    /// <summary>No game version could be picked: a shutdown ends cleanly, anything else is a config error.</summary>
    private static int VersionFailed(string? error, bool useTui, ILogger log, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return ExitCodes.Ok;
        log.LogCritical("can't pick a game version: {Error}", error);
        if (useTui) Console.Error.WriteLine("MineClaw: can't pick a game version: " + error);
        return ExitCodes.Config;
    }

    internal static ViewMode? ParseView(string? v) => TuiApp.ParseView(v);

    /// <summary>
    /// The read-only hive-mind supervisor viewer (Supervisor:Enabled): every bot's live view, the free camera and the TUI panels
    /// over HTTP, for an SSH tunnel. Renders run on one capped background thread; a bind failure only logs (the bot runs on).
    /// </summary>
    private static Supervisor.SupervisorServer? StartSupervisor(Supervisor.SupervisorOptions? o, BotHost bot, MinecraftClient client, string main,
        IReadOnlyList<IBotTelemetrySource> sources, MainHive? mainHive, ILogger log, BrainContextCapture? capture = null)
    {
        if (o is not { Enabled: true }) return null;
        if (bot.Vision?.Hub is not { } hub)
        {
            log.LogWarning("supervisor: Bot:Vision is off, so there are no views to serve; not starting");
            return null;
        }
        var feed = new Supervisor.SupervisorFeed();
        client.Danger += d => feed.Add("danger", $"{main}: {d.Kind}: {d.Message}", "warn");
        client.Died += () => feed.Add("death", $"{main} died", "warn");
        client.Kicked += r => feed.Add("conn", $"{main} kicked: {r}", "warn");
        client.Disconnected += r => feed.Add("conn", $"{main} disconnected: {r}", "warn");
        client.Spawned += () => feed.Add("conn", $"{main} joined");
        client.ServerMoved += (a, b) => feed.Add("move", $"{main} moved by the server {a} -> {b}");
        client.PlayerJoined += n => feed.Add("players", $"{n} joined");
        client.PlayerLeft += n => feed.Add("players", $"{n} left");
        if (bot.Bodies is { HasLifecycle: true } bodies) // inproc: bodies spawning/leaving and their notices
            bodies.Changed += (b, added) => feed.Add("hive", $"{b.Short} {(added ? "spawning" : "logged out")} ({b.State})");
        if (mainHive?.Supervisor is { } sup)
            sup.Notice += e => feed.Add("hive", e.Format(), e.Priority >= Agent.Events.EventPriority.Normal ? "warn" : "info");
        Supervisor.BlockDeltaLog.Process = new Supervisor.BlockDeltaLog(); // /api/world/blocks: every attached world of this process
        // Every body this process controls (main, hives, their subhive bodies, nodes still spawning) for every list below.
        var allBodies = Supervisor.HiveBodyList.For(hub, bot, main, () => mainHive?.Supervisor, () => mainHive?.Hierarchy?.Tree);
        var data = new Supervisor.LiveSupervisorData(hub, () => client.World, main, sources, () => mainHive?.Supervisor, feed, o, log)
        {
            Blueprints = () => bot.Blueprints?.Service, // /view/blueprint/{id}, /api/blueprints: designs + preview + bill
            Bodies = allBodies,
        };
        var scriptData = new Supervisor.LiveScriptData(bot, main, () => mainHive?.Supervisor, log) { Bodies = allBodies };
        var server = new Supervisor.SupervisorServer(o, data, log)
        {
            World = Supervisor.LiveSupervisorWorld.Routes(hub, bot, () => client.World, main, o, log, allBodies), // Live Hive: /api/world/*
            Admin = Supervisor.SupervisorAdminService.Create(o, bot.Runtime, main, log), // /api/admin/*: only with AllowAdminWrites + Token
            // /api/scripts*, /api/bodies/{name}/script: the saved library and the live script debugger (read-only, token or 403)
            Scripts = new Supervisor.SupervisorScriptRoutes(scriptData, o.Token),
        };
        // /api/admin/settings*, /api/admin/ollama/*, /api/admin/restart*: the web's Administration page (AllowAdminWrites + token + admin identity)
        server.Settings = Supervisor.Admin.AdminSettingsWiring.Routes(o, bot, client, allBodies, main, log);
        // /api/bodies/{name}/vision*: the sheets each body's models got (clones over IPC) and on-demand renders (token or 403)
        server.HiveTree = () => mainHive?.Hierarchy?.Snapshot(); // the Explorer's Live Hive tree (owner → children, kind, brain)
        server.Vision = new Supervisor.SupervisorVisionRoutes(new Supervisor.LiveVisionData(hub, bot, main, () => mainHive?.Supervisor, log), o.Token, server.Gate);
        // /api/bodies/{name}/controller, /api/brains/{id}/context|turn/{n}: what drives a body and its brain's live context (token or 403)
        capture?.AddSecrets([o.Token]);
        var controller = new Supervisor.SupervisorControllerData(bot, main, capture, () => mainHive?.Supervisor, () => mainHive?.Hierarchy?.Tree, log);
        server.Controller = new Supervisor.SupervisorControllerRoutes(controller, o.Token);
        // /api/viewer/*: the web viewer (chat as a trusted player, the live feed, "now", memory activity); Viewer:Enabled + token header
        if (o.Viewer.Enabled)
        {
            var viewerFeed = new Supervisor.ViewerFeed();
            var memory = new Supervisor.ViewerMemoryActivity();
            Supervisor.ViewerTaps.Enable(viewerFeed, memory);
            var viewer = new Supervisor.LiveViewerData(bot, main, sources, () => mainHive?.Supervisor, controller, scriptData, log) { Bodies = allBodies };
            server.Viewer = new Supervisor.SupervisorViewerRoutes(o, viewer, viewerFeed, memory);
            // /api/designer/*: the Design Studio (a sandbox canvas co-designed with a model of the user's choice, saved as designs)
            if (o.Viewer.Designer.Enabled)
                server.Designer = new Supervisor.DesignerRoutes(o, () => bot.Blueprints?.Service, new MineClaw.Bot.Blueprints.DesignerStore(bot.Runtime.Db),
                    bot.Runtime.Llm, bot.Runtime.Options.Llm, bot.Runtime.Usage, () => main) { ServerSeed = () => client.World.Seed };
            // /api/story/*: Story Mode (colonies of script-only pawns, docs/web/story-mode.md); the viewer's gate, AllowStory for writes
            server.Story = Supervisor.StoryWiring.Attach(o, bot, client, mainHive?.Hierarchy, viewer, viewerFeed, allBodies, log);
        }
        try
        {
            server.Start();
            return server;
        }
        catch (Exception ex)
        {
            log.LogWarning("supervisor: can't listen on {Bind}:{Port} ({Error}); running without it", o.Bind, o.Port, ex.Message);
            _ = server.DisposeAsync();
            return null;
        }
    }

    // ================================================================ demo

    /// <summary><c>--demo</c>: fake bots in a fake world, real TUI. No config, server or API key needed.</summary>
    public static async Task<int> RunDemoAsync(AppCli cli)
    {
        var root = AppPaths.FindRoot();
        var host = new HostSettings();
        var hive = new HiveConfig { CloneUsernames = ["maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"] };
        string main = "maid_chan";
        try
        {
            var config = AppConfig.Build(cli, root);
            config.GetSection("Host").Bind(host);
            config.GetSection("Hive").Bind(hive);
            main = config["Minecraft:Username"] is { Length: > 0 } u ? u : main;
        }
        catch (Exception) { /* demo works without config */ }

        var roster = new HiveRoster([main, .. hive.CloneUsernames.Take(hive.MaxClones > 0 ? hive.MaxClones : 4)]);
        using var demo = new DemoHive(roster.Names, Math.Min(cli.DemoBots, roster.Names.Count), host.Tui.Viewport);
        var ring = new RingLoggerProvider();
        ring.Add(new LogLine(DateTimeOffset.Now, LogLevel.Information, "MineClaw.App", "demo mode: fake bots, no server, no API calls"));

        var (useTui, why) = TerminalDetect.Decide(cli.Tui, TerminalDetect.Current());
        if (cli.Frame is { } size || !useTui)
        {
            for (int i = 0; i < 64; i++) demo.Tick();
            var (w, h) = cli.Frame ?? (120, 40);
            var app = new TuiApp(demo.Sources, roster, ring, host.Tui, _ => { }, NullLogger.Instance, cli.Bot - 1, ParseView(cli.ViewMode));
            // --frame: ANSI 24-bit colour (NO_COLOR=1 for plain text); the headless fallback prints plain text.
            bool ansi = cli.Frame != null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
            Console.Out.WriteLine(app.RenderOnce(w, h, ansi));
            if (cli.Frame == null) Console.Error.WriteLine($"(demo: printed one frame because {why}; run in a terminal for the live TUI)");
            return ExitCodes.Ok;
        }

        using var signals = new ShutdownSignals();
        using var minutesTimer = cli.Minutes is { } minutes ? new Timer(_ => signals.Request("--minutes elapsed"), null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan) : null;
        demo.Start();
        var tui = new TuiApp(demo.Sources, roster, ring, host.Tui, w => signals.Request("tui " + w), NullLogger.Instance, cli.Bot - 1, ParseView(cli.ViewMode));
        await tui.RunAsync(signals.Token);
        return ExitCodes.Ok;
    }
}
