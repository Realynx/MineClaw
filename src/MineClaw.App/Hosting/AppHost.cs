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
public static class AppHost
{
    /// <summary>Hive seam: runs before the bot disconnects on shutdown (e.g. "deposit items before logout"), inside
    /// Host:PreLogoutSeconds of the shutdown budget. Null = nothing.</summary>
    public static Func<BotHost, CancellationToken, Task>? BeforeLogout { get; set; }

    public static async Task<int> RunAsync(AppCli cli)
    {
        var root = AppPaths.FindRoot();
        if (cli.CloneName != null) cli = cli with { Tui = TuiChoice.Off, NoKeyCheck = true }; // hive clone: headless, main checked the key

        // ------------------------------------------------------------ config
        IConfigurationRoot config;
        try { config = AppConfig.Build(cli, root); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MineClaw: can't read the configuration: {ex.Message}");
            return ExitCodes.Config;
        }
        var mc = config.GetSection("Minecraft").Get<MinecraftSettings>() ?? new MinecraftSettings();
        var agentOptions = new AgentOptions();
        config.GetSection("Agent").Bind(agentOptions);
        var botOptions = new BotOptions();
        config.GetSection("Bot").Bind(botOptions);
        var host = config.GetSection("Host").Get<HostSettings>() ?? new HostSettings();
        var hive = config.GetSection("Hive").Get<HiveConfig>() ?? new HiveConfig();
        config.GetSection("Hive").Bind(agentOptions.Hive); // personality scope: main | clone username (Self shards)
        var telemetryOptions = config.GetSection("Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
        var supervisorOptions = config.GetSection("Supervisor").Get<Supervisor.SupervisorOptions>() ?? new Supervisor.SupervisorOptions();
        var bodiesOptions = config.GetSection("Bodies").Get<MineClaw.Bot.Bodies.BodiesOptions>() ?? new MineClaw.Bot.Bodies.BodiesOptions();
        if (string.IsNullOrWhiteSpace(agentOptions.Llm.ApiKey) || agentOptions.Llm.ApiKey.EndsWith("..."))
            agentOptions.Llm.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        agentOptions.DataDirectory = AppPaths.Under(root, agentOptions.DataDirectory);
        if (agentOptions.KnowledgeDirectory != null) agentOptions.KnowledgeDirectory = AppPaths.UnderOrBeside(root, agentOptions.KnowledgeDirectory);
        var logDir = AppPaths.Under(root, host.LogDirectory);
        var telemetryDir = string.IsNullOrWhiteSpace(telemetryOptions.Directory) ? logDir : AppPaths.Under(root, telemetryOptions.Directory);
        var mainLogDir = logDir;
        if (cli.CloneName != null) (logDir, telemetryDir) = CloneMode.Prepare(cli.CloneName, mc, agentOptions, hive, logDir, telemetryDir);

        var problems = ConfigCheck.Validate(mc, agentOptions);
        if (problems.Count > 0)
        {
            Console.Error.WriteLine("MineClaw can't start, the config needs attention:");
            foreach (var p in problems) Console.Error.WriteLine("  - " + p);
            Console.Error.WriteLine("Edit appsettings.json (copy appsettings.example.json), pass --config <file>, or set env vars like MINECLAW_Agent__Llm__ApiKey.");
            return ExitCodes.Config;
        }
        try
        {
            Directory.CreateDirectory(agentOptions.DataDirectory);
            Directory.CreateDirectory(logDir);
            Directory.CreateDirectory(telemetryDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"MineClaw: can't create data/log directories: {ex.Message}");
            return ExitCodes.Config;
        }

        // ------------------------------------------------------------ console mode + logging
        var (useTui, why) = TerminalDetect.Decide(cli.Tui, TerminalDetect.Current());
        var fileLog = new FileLoggerProvider(logDir);
        var ring = new RingLoggerProvider();
        var format = AppLogging.ResolveFormat(host.LogFormat, TerminalDetect.UnderJournal());
        var loggers = AppLogging.Create(useTui, format, fileLog, ring);
        var log = loggers.CreateLogger("MineClaw.App");
        using var signals = new ShutdownSignals(sig => log.LogInformation("{Signal}: shutting down", sig));
        log.LogInformation("MineClaw starting (root {Root}, data {Data}, log {Log}, console {Mode}: {Why})", root, agentOptions.DataDirectory, logDir,
            useTui ? "tui" : "headless/" + format, why);
        using var minutesTimer = cli.Minutes is { } minutes ? new Timer(_ => signals.Request("--minutes elapsed"), null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan) : null;

        try
        {
            // -------------------------------------------------------- key check (models API: free)
            if (!cli.NoKeyCheck)
            {
                // Anthropic: only the tiers that run there (all three by default = the old check, unchanged).
                var anthropicModels = KeyCheck.AnthropicModels(agentOptions.Llm);
                if (anthropicModels.Count > 0)
                {
                    var check = await KeyCheck.RunAsync(agentOptions.Llm.ApiKey!, anthropicModels, signals.Token);
                    if (!check.Ok)
                    {
                        log.LogCritical("Anthropic API check failed: {Message}", check.Message);
                        if (useTui) Console.Error.WriteLine("Anthropic API check failed: " + check.Message);
                        return signals.Token.IsCancellationRequested ? ExitCodes.Ok : check.Transient ? ExitCodes.TempFail : ExitCodes.Auth;
                    }
                    log.LogInformation("Anthropic key ok: {Models}", check.Message);
                }
                // Ollama: only when a tier uses it (GET /api/tags + /api/show: the models exist and can call tools).
                if (agentOptions.Llm.TiersUseOllama)
                {
                    var oc = await KeyCheck.RunOllamaAsync(agentOptions.Llm, ct: signals.Token);
                    if (!oc.Ok)
                    {
                        log.LogCritical("Ollama check failed: {Message}", oc.Message);
                        if (useTui) Console.Error.WriteLine("Ollama check failed: " + oc.Message);
                        return signals.Token.IsCancellationRequested ? ExitCodes.Ok : oc.Transient ? ExitCodes.TempFail : ExitCodes.Config;
                    }
                    log.LogInformation("Ollama ok ({Endpoint}): {Models}", agentOptions.Llm.Ollama.Endpoint, oc.Message);
                }
                if (agentOptions.Llm.Ollama.IsConfigured)
                    foreach (var note in await KeyCheck.CheckOllamaProfilesAsync(agentOptions.Llm, ct: signals.Token)) log.LogWarning("{Note}", note);
            }
            // Script authorship is keyed to the brain tier: a brain that isn't Opus 5.5 still writes Lua, so say so loudly.
            if (cli.CloneName == null && !(LlmOptions.IsOpus55(agentOptions.Llm.MainModel) && agentOptions.Llm.ProviderFor(ModelTier.Main) == LlmProviders.Anthropic))
                log.LogWarning("brain tier runs {Provider}/{Model}, not Opus 5.5: it still writes Lua scripts (authorship belongs to the brain tier)",
                    agentOptions.Llm.ProviderFor(ModelTier.Main), agentOptions.Llm.MainModel);
            if (cli.CloneName != null)
                return await CloneMode.RunAsync(cli, mc, agentOptions, botOptions, host, hive, telemetryOptions, telemetryDir, loggers, log, signals);
            return await RunBotAsync(cli, mc, agentOptions, botOptions, host, hive, telemetryOptions, telemetryDir, useTui, ring, loggers, log, signals, mainLogDir,
                supervisorOptions, bodiesOptions);
        }
        finally
        {
            loggers.Dispose();
            fileLog.Dispose();
        }
    }

    private static async Task<int> RunBotAsync(AppCli cli, MinecraftSettings mc, AgentOptions agentOptions, BotOptions botOptions, HostSettings host,
        HiveConfig hive, TelemetryOptions telemetryOptions, string telemetryDir, bool useTui, RingLoggerProvider ring, ILoggerFactory loggers,
        ILogger log, ShutdownSignals signals, string logDir, Supervisor.SupervisorOptions? supervisorOptions = null,
        MineClaw.Bot.Bodies.BodiesOptions? bodiesOptions = null)
    {
        var telemetry = new PromptTelemetry(agentOptions, telemetryOptions, telemetryOptions.Enabled ? telemetryDir : null);
        int seeded = telemetry.SeedFromLogs();
        ILlmClient llm = LlmClientFactory.Create(agentOptions.Llm); // nothing on Ollama = the AnthropicLlmClient, as before
        if (telemetryOptions.Enabled) llm = telemetry.Wrap(llm);

        var clientLog = loggers.CreateLogger("MineClaw.Client");
        var client = new MinecraftClient(new ClientOptions
        {
            Host = mc.Host, Port = mc.Port, Username = mc.Username,
            AutoReconnect = true, AutoRespawn = true, RecordDirectory = cli.Record,
            DuplicateLoginBackoff = TimeSpan.FromSeconds(Math.Max(0, mc.DuplicateLoginBackoffSeconds)),
            Log = m => clientLog.LogDebug("{Msg}", m),
        });
        var game = new MinecraftGameClient(client);
        // One brain, many bodies (step 2): Bodies:Mode=inproc runs the clone accounts as bodies in this process (no MainHive);
        // the default "hive" keeps one process per clone.
        var inproc = InProcBodies.Create(hive, bodiesOptions ?? new MineClaw.Bot.Bodies.BodiesOptions(), mc, agentOptions, loggers);
        var mainHive = inproc == null ? MainHive.Create(hive, mc.Username, cli, agentOptions, logDir, loggers.CreateLogger("MineClaw.Hive")) : null;
        var bot = BotHost.Create(game, agentOptions, botOptions, llm: llm, loggers: loggers, claims: inproc?.MainClaims ?? mainHive?.MainClaims,
            hiveBots: inproc != null ? inproc.HiveBots : mainHive != null ? mainHive.HiveBots : null);
        var rt = bot.Runtime;
        telemetry.Attach(rt);
        telemetry.CurrentBody = () => MineClaw.Bot.Bodies.BodyScope.Current?.Name; // the body a call worked for (null = brain-level)
        _ = rt.Rag.WarmUpAsync(signals.Token); // DNS + first TLS to the embedding provider now, not in the first turn
        log.LogInformation("telemetry: {Dir} ({Seeded} records seeded)", telemetry.Directory ?? "off", seeded);
        var toolChars = rt.Tools.Definitions().Sum(d => d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length);
        log.LogInformation("{Tools} tools (~{Tok} tokens of definitions), knowledge {K} chars{Trunc}", rt.Tools.All.Count, (int)Math.Ceiling(toolChars / 3.5),
            rt.KnowledgeChars, rt.KnowledgeTruncated ? " (TRUNCATED)" : "");
        rt.Agent.EpisodeCompleted += o => log.LogInformation("episode: {Tier} {Calls} call(s) ${Cost:0.0000} [{Usage}] said: {Said}{Skip}",
            o.Tier?.ToString() ?? "-", o.Calls, o.CostUsd, o.Usage, string.Join(" | ", o.Said), o.Skipped != null ? " skipped: " + o.Skipped : "");
        rt.Usage.ModeChanged += (mode, reason) => log.LogWarning("budget mode {Mode}: {Why}", mode, reason);
        client.ChatReceived += line => log.LogInformation("chat: {Line}", ChatText.StripColours(line));
        client.Kicked += r => log.LogWarning("kicked: {Reason}", r);
        client.Kicked += r =>
        {
            var wait = MinecraftClient.FirstReconnectDelay(r, client.Options);
            if (wait > client.Options.ReconnectInitialDelay)
                log.LogWarning("another session is using {User}; backing off {S:0}s before reconnecting", mc.Username, wait.TotalSeconds);
        };
        client.Spawned += () => log.LogInformation("joined {Host}:{Port} as {User}", mc.Host, mc.Port, mc.Username);
        client.Disconnected += r => log.LogWarning("disconnected: {Reason}", r);
        client.Danger += d => log.LogWarning("danger {Kind}: {Msg}", d.Kind, d.Message);

        var agent = new PausableAgent(rt, log);
        var commands = new ConsoleCommands(game, rt, agent, log, signals.Source);
        var source = new InProcessBotSource(game, rt, telemetry, agent, commands, host.Tui.Viewport, signals.Token);
        mainHive?.Attach(bot, client, commands, agentOptions);
        inproc?.Attach(bot, client, signals.Token);

        // The TUI's per-bot sources (main in-process, clones over IPC); the supervisor viewer reads the same ones.
        var roster = new HiveRoster([mc.Username, .. hive.Clones()]);
        if (inproc != null) roster = new HiveRoster([mc.Username, .. inproc.Accounts]);
        var sources = roster.Names.Select(n => string.Equals(n, mc.Username, StringComparison.OrdinalIgnoreCase)
            ? (IBotTelemetrySource)source
            : inproc != null ? new BodyBotSource(n, () => bot.Bodies?.Find(n),
                b => new InProcessBotSource(b.Adapter, rt, telemetry, agent, commands, host.Tui.Viewport, signals.Token) { Activities = b.Activities })
            : mainHive?.SourceFor(n) ?? new OfflineBotSource(n)).ToList();
        var supervisorServer = StartSupervisor(supervisorOptions, bot, client, mc.Username, sources, mainHive, loggers.CreateLogger("MineClaw.Supervisor"));

        Task tuiTask = Task.CompletedTask;
        if (useTui)
        {
            var tui = new TuiApp(sources, roster, ring, host.Tui, w => signals.Request("tui " + w), log, cli.Bot - 1, ParseView(cli.ViewMode));
            tuiTask = tui.RunAsync(signals.Token);
        }

        // Console commands (/quit etc.) work from the start, also while still connecting or backing off.
        var token = signals.Token;
        if (!useTui && cli.StdinCommands) commands.StartStdin(token);

        // Connect (retry with backoff until it works or we're told to stop; the client reconnects by itself afterwards).
        var delay = TimeSpan.FromSeconds(3);
        while (!token.IsCancellationRequested)
        {
            try
            {
                log.LogInformation("connecting to {Host}:{Port} as {User}", mc.Host, mc.Port, mc.Username);
                await client.ConnectAsync(token);
                await client.WaitForJoinStateAsync(TimeSpan.FromSeconds(3), token); // no default 20/20 or fake void in the line
                log.LogInformation("in game: {Status}", client.DescribeStatus());
                break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning("connect failed: {Error}; retrying in {Delay}s", ex.Message, delay.TotalSeconds);
                try { await Task.Delay(delay, token); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }

        var tasks = new List<Task>();
        if (!token.IsCancellationRequested)
        {
            agent.Start(token);
            if (cli.VoidAbort) tasks.Add(VoidWatch.RunAsync(game, log, signals.Source));
            if (cli.Script != null) tasks.Add(commands.RunScriptAsync(cli.Script, token));
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
        }

        // ------------------------------------------------------------ graceful shutdown (time-boxed)
        var budget = TimeSpan.FromSeconds(Math.Clamp(host.ShutdownTimeoutSeconds, 3, 600));
        // Hive: clones need time to deposit; stay under systemd's TimeoutStopSec=90 (deploy/mineclaw.service).
        if (mainHive?.Supervisor?.Slots.Any(s => s.State != MineClaw.Hive.Supervisor.CloneState.Idle && s.State != MineClaw.Hive.Supervisor.CloneState.Retired) == true
            || bot.Bodies?.All.Any(b => !b.IsMain) == true)
            budget = TimeSpan.FromSeconds(Math.Max(budget.TotalSeconds, Math.Min(85, hive.ShutdownRecallSeconds + 25)));
        var seq = new ShutdownSequence(budget, log);
        log.LogInformation("stopping ({Why}, budget {S:0}s): {Usage}", signals.Reason ?? "requested", budget.TotalSeconds, rt.Usage.Summary());
        await seq.StepAsync("tui", TimeSpan.FromSeconds(2), _ => tuiTask);
        if (supervisorServer != null) await seq.StepAsync("supervisor", TimeSpan.FromSeconds(3), _ => supervisorServer.DisposeAsync().AsTask());
        if (useTui) Console.Out.WriteLine("MineClaw: shutting down...");
        // Hive first: clones deposit everything and log out while main is still online (handoff receiver), then main stops.
        if (inproc != null)
            await seq.StepAsync("bodies recall", TimeSpan.FromSeconds(bodiesOptions!.ShutdownRecallSeconds), _ =>
                inproc.RecallAllAsync("main shutting down", TimeSpan.FromSeconds(Math.Max(5, Math.Min(bodiesOptions.ShutdownRecallSeconds, seq.Remaining.TotalSeconds) - 1))));
        if (mainHive != null)
            await seq.StepAsync("hive recall", TimeSpan.FromSeconds(hive.ShutdownRecallSeconds), _ =>
                mainHive.RecallAllAsync("main shutting down", TimeSpan.FromSeconds(Math.Max(5, Math.Min(hive.ShutdownRecallSeconds, seq.Remaining.TotalSeconds) - 1))));
        await seq.StepAsync("stop actions", TimeSpan.FromSeconds(5), ct =>
        {
            rt.Activities.Cancel("shutdown");
            return client.StopAsync(ct);
        });
        await seq.StepAsync("agent", TimeSpan.FromSeconds(10), _ => agent.StopAsync());
        if (BeforeLogout is { } before && host.PreLogoutSeconds > 0 && client.IsPlaying)
            await seq.StepAsync("pre-logout", TimeSpan.FromSeconds(host.PreLogoutSeconds), ct => before(bot, ct));
        await seq.StepAsync("disconnect", TimeSpan.FromSeconds(5), _ => client.DisconnectAsync("Bye!"));
        await seq.StepAsync("background tasks", TimeSpan.FromSeconds(3), _ => Task.WhenAll(tasks));
        seq.Final("bot", () => { source.Dispose(); bot.Dispose(); });
        await seq.StepAsync("client", TimeSpan.FromSeconds(3), _ => client.DisposeAsync().AsTask());
        if (mainHive != null) await seq.StepAsync("hive", TimeSpan.FromSeconds(3), _ => mainHive.DisposeAsync().AsTask());
        if (inproc != null) await seq.StepAsync("bodies", TimeSpan.FromSeconds(5), _ => inproc.DisposeAsync().AsTask());
        seq.Final("db", rt.Dispose);
        seq.Final("telemetry", telemetry.Dispose);
        log.LogInformation("bye{Late}", seq.TimedOut.Count > 0 ? " (timed out: " + string.Join(", ", seq.TimedOut) + ")" : "");
        if (useTui) Console.Out.WriteLine("MineClaw: bye");
        return ExitCodes.Ok;
    }

    internal static ViewMode? ParseView(string? v) => TuiApp.ParseView(v);

    /// <summary>
    /// The read-only hive-mind supervisor viewer (Supervisor:Enabled): every bot's live view, the free camera and the TUI panels
    /// over HTTP, for an SSH tunnel. Renders run on one capped background thread; a bind failure only logs (the bot runs on).
    /// </summary>
    private static Supervisor.SupervisorServer? StartSupervisor(Supervisor.SupervisorOptions? o, BotHost bot, MinecraftClient client, string main,
        IReadOnlyList<IBotTelemetrySource> sources, MainHive? mainHive, ILogger log)
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
        var data = new Supervisor.LiveSupervisorData(hub, () => client.World, main, sources, () => mainHive?.Supervisor, feed, o, log)
        {
            Blueprints = () => bot.Blueprints?.Service, // /view/blueprint/{id}, /api/blueprints: designs + preview + bill
        };
        var server = new Supervisor.SupervisorServer(o, data, log)
        {
            World = Supervisor.LiveSupervisorWorld.Routes(hub, bot, () => client.World, main, o, log), // Live Hive: /api/world/*
            Admin = Supervisor.SupervisorAdminService.Create(o, bot.Runtime, main, log), // /api/admin/*: only with AllowAdminWrites + Token
        };
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

        var roster = new HiveRoster([main, .. hive.CloneUsernames.Take(Math.Max(0, hive.MaxClones))]);
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
