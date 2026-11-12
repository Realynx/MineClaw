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
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// The main bot's run (not a clone): build the client, hives and brain in a fixed order, start the supervisor and TUI, connect,
/// run until a stop is asked for, then the time-boxed shutdown.
/// </summary>
public static partial class AppHost
{
    /// <summary>What the main run built: the steps after construction (wiring, sources, run, shutdown) read it.</summary>
    private sealed record MainParts(MinecraftSettings Mc, HostSettings Host, HiveConfig Hive, ILogger Log, ShutdownSignals Signals,
        MinecraftClient Client, MinecraftGameClient Game, BotHost Bot, PromptTelemetry Telemetry, PausableAgent Agent, ConsoleCommands Commands,
        InProcessBotSource Source, InProcBodies? Inproc, MainHive? MainHive, InProcBodies? Subhives)
    {
        public AgentRuntime Rt => Bot.Runtime;
    }

    private static async Task<int> RunBotAsync(AppCli cli, MinecraftSettings mc, AgentOptions agentOptions, BotOptions botOptions, HostSettings host,
        HiveConfig hive, TelemetryOptions telemetryOptions, string telemetryDir, bool useTui, RingLoggerProvider ring, ILoggerFactory loggers,
        ILogger log, ShutdownSignals signals, string logDir, Supervisor.SupervisorOptions? supervisorOptions = null,
        MineClaw.Bot.Bodies.BodiesOptions? bodiesOptions = null, MineClaw.Client.Versioning.GameVersionProfile? profile = null,
        InProcHiveSettings? inProcHives = null)
    {
        profile ??= MineClaw.Client.Versioning.GameVersionProfiles.Beta181;
        var telemetry = new PromptTelemetry(agentOptions, telemetryOptions, telemetryOptions.Enabled ? telemetryDir : null);
        int seeded = telemetry.SeedFromLogs();
        ILlmClient llm = LlmClientFactory.Create(agentOptions.Llm); // nothing on Ollama = the AnthropicLlmClient, as before
        if (telemetryOptions.Enabled) llm = telemetry.Wrap(llm);

        var client = CreateMainClient(cli, mc, profile, loggers);
        var game = new MinecraftGameClient(client);
        Supervisor.BlockDeltaLog.Attach(client.World); // the browser viewer's live block feed (logs only while someone reads it)
        Supervisor.BodyInventories.Attach(mc.Username, client); // the viewer's body inventory panel (read only when asked)
        var (inproc, mainHive, subhives) = CreateHives(cli, mc, agentOptions, hive, bodiesOptions ?? new MineClaw.Bot.Bodies.BodiesOptions(), loggers,
            log, logDir, profile, inProcHives);
        var bot = BotHost.Create(game, agentOptions, botOptions, llm: llm, loggers: loggers, claims: inproc?.MainClaims ?? mainHive?.MainClaims,
            hiveBots: HiveBotsOf(inproc, mainHive, subhives));
        var rt = bot.Runtime;
        telemetry.Attach(rt);
        telemetry.CurrentBody = () => MineClaw.Bot.Bodies.BodyScope.Current?.Name; // the body a call worked for (null = brain-level)
        _ = rt.Rag.WarmUpAsync(signals.Token); // DNS + first TLS to the embedding provider now, not in the first turn
        LogRuntime(rt, telemetry, seeded, log);
        LogEvents(rt, client, mc, log);

        var agent = new PausableAgent(rt, log);
        var commands = new ConsoleCommands(game, rt, agent, log, signals.Source);
        var source = new InProcessBotSource(game, rt, telemetry, agent, commands, host.Tui.Viewport, signals.Token);
        var p = new MainParts(mc, host, hive, log, signals, client, game, bot, telemetry, agent, commands, source, inproc, mainHive, subhives);
        Supervisor.ViewerTaps.Attach(mc.Username, rt, client, mainHears: true); // the web viewer's feed + memory map (read only when it's on)
        mainHive?.Attach(bot, client, commands, agentOptions, subhives);
        inproc?.Attach(bot, client, signals.Token);
        subhives?.Attach(bot, client, signals.Token);
        WireScripts(p);

        // The TUI's per-bot sources (main in-process, clones over IPC); the supervisor viewer reads the same ones.
        var roster = RosterOf(p);
        var sources = roster.Names.Select(n => SourceFor(p, n)).ToList();
        var supervisorServer = StartSupervisor(supervisorOptions, bot, client, mc.Username, sources, mainHive, loggers.CreateLogger("MineClaw.Supervisor"),
            telemetryOptions.Enabled ? telemetry.Capture : null);
        mainHive?.Hierarchy?.ClaimsReady(); // the saved colonies (Story Mode) claimed their pawns: the startup reconcile may run

        var tuiTask = useTui
            ? new TuiApp(sources, roster, ring, host.Tui, w => signals.Request("tui " + w), log, cli.Bot - 1, ParseView(cli.ViewMode)).RunAsync(signals.Token)
            : Task.CompletedTask;

        // Console commands (/quit etc.) work from the start, also while still connecting or backing off.
        var token = signals.Token;
        if (!useTui && cli.StdinCommands) commands.StartStdin(token);
        await ConnectMainAsync(client, mc, log, token);
        var tasks = await RunUntilStopAsync(cli, p, token);

        await ShutdownAsync(p, bodiesOptions, useTui, tuiTask, supervisorServer, tasks);
        return ExitCodes.Ok;
    }

    private static MinecraftClient CreateMainClient(AppCli cli, MinecraftSettings mc, MineClaw.Client.Versioning.GameVersionProfile profile, ILoggerFactory loggers)
    {
        var clientLog = loggers.CreateLogger("MineClaw.Client");
        return new MinecraftClient(mc.WithPlayers(new ClientOptions
        {
            Host = mc.Host, Port = mc.Port, Username = mc.Username, Profile = profile,
            AutoReconnect = true, AutoRespawn = true, RecordDirectory = cli.Record,
            DuplicateLoginBackoff = TimeSpan.FromSeconds(Math.Max(0, mc.DuplicateLoginBackoffSeconds)),
            // Main is op (the empty state: main whitelisted + opped): on 1.12.2 its commands skip the spam spacing.
            CommandInterval = profile.Version == MineClaw.Protocol.Versioning.GameVersion.Release1122 ? TimeSpan.FromMilliseconds(150) : null,
            Log = m => clientLog.LogDebug("{Msg}", m),
        }));
    }

    /// <summary>
    /// One brain, many bodies (step 2): Bodies:Mode=inproc runs the clone accounts as bodies in this process (no MainHive); the
    /// default "hive" keeps one process per clone, plus main's own subhive bodies (accounts from the allocator).
    /// </summary>
    private static (InProcBodies? Inproc, MainHive? MainHive, InProcBodies? Subhives) CreateHives(AppCli cli, MinecraftSettings mc, AgentOptions agentOptions,
        HiveConfig hive, MineClaw.Bot.Bodies.BodiesOptions bodiesOptions, ILoggerFactory loggers, ILogger log, string logDir,
        MineClaw.Client.Versioning.GameVersionProfile profile, InProcHiveSettings? inProcHives)
    {
        var inproc = InProcBodies.Create(hive, bodiesOptions, mc, agentOptions, loggers, profile);
        if (inproc != null) return (inproc, null, null);
        var mainHive = MainHive.Create(hive, mc.Username, cli, agentOptions, logDir, loggers.CreateLogger("MineClaw.Hive"), inProcHives, loggers);
        // Hive mode: main's own subhive bodies (one brain, many bodies next to the clone processes; accounts from the allocator).
        var subhives = mainHive != null
            ? InProcBodies.CreateSubhives(hive, bodiesOptions, mc, agentOptions, loggers, profile, mainHive.Board)
            : null;
        return (null, mainHive, subhives);
    }

    /// <summary>The hive's bot names (dynamic accounts): the in-process bodies, or the clones plus main's subhive bodies.</summary>
    private static Func<IEnumerable<string>>? HiveBotsOf(InProcBodies? inproc, MainHive? mainHive, InProcBodies? subhives)
    {
        if (inproc != null) return inproc.HiveBots;
        if (mainHive == null) return null;
        return () => [.. mainHive.HiveBots(), .. subhives?.Registry?.Accounts ?? []];
    }

    private static void LogRuntime(AgentRuntime rt, PromptTelemetry telemetry, int seeded, ILogger log)
    {
        log.LogInformation("telemetry: {Dir} ({Seeded} records seeded)", telemetry.Directory ?? "off", seeded);
        var toolChars = rt.Tools.Definitions().Sum(d => d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length);
        log.LogInformation("{Tools} tools (~{Tok} tokens of definitions), knowledge {K} chars{Trunc}", rt.Tools.All.Count, (int)Math.Ceiling(toolChars / 3.5),
            rt.KnowledgeChars, rt.KnowledgeTruncated ? " (TRUNCATED)" : "");
    }

    /// <summary>The main log's lines for episodes, budget modes and the client's connection, chat and danger events.</summary>
    private static void LogEvents(AgentRuntime rt, MinecraftClient client, MinecraftSettings mc, ILogger log)
    {
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
    }

    /// <summary>
    /// Scripts: several bodies get a crew port (in-process lanes, or hive jobs over the shared db board); then the one-time
    /// Lua → JS migration of saved scripts runs in the background (main only; Scripting:MigrateLua).
    /// </summary>
    private static void WireScripts(MainParts p)
    {
        if (MineClaw.Bot.GameTools.ScriptTools.Of(p.Rt) is not { } scripts) return;
        if (p.Inproc != null && p.Bot.Bodies is { } registry)
            scripts.Crew = new MineClaw.Bot.Bodies.BodyCrewPort(registry, p.Rt.Tools, new MineClaw.Scripting.Crew.InMemoryCrewBoard());
        else if (p.MainHive?.Supervisor is { } supervisor)
            WireHiveCrew(p, scripts, supervisor);
        var log = p.Log;
        _ = scripts.StartLuaMigrationAsync(p.Signals.Token).ContinueWith(t =>
        {
            if (t.Exception is { } ex) log.LogWarning(ex.GetBaseException(), "lua migration stopped");
        }, TaskScheduler.Default);
    }

    /// <summary>Hive mode: clone processes are lanes over the db board; main's subhive bodies are crew lanes in this process.</summary>
    private static void WireHiveCrew(MainParts p, MineClaw.Bot.GameTools.ScriptBridge scripts, MineClaw.Hive.Supervisor.HiveSupervisor supervisor)
    {
        var board = new MineClaw.Bot.Bodies.DbCrewBoard(p.Rt.Memory.Db);
        MineClaw.Scripting.Crew.ICrewPort hivePort = new MineClaw.Hive.Crew.HiveCrewPort(supervisor, board);
        // Main's subhive bodies are crew lanes in this process; clone processes stay lanes over the db board.
        if (p.Subhives == null || p.Bot.Bodies is not { } reg)
        {
            scripts.Crew = hivePort;
            return;
        }
        var own = new MineClaw.Bot.Bodies.BodyCrewPort(reg, p.Rt.Tools, board);
        scripts.Crew = new CompositeCrewPort(board, own, hivePort);
        // Hive scripts (hive spawn kind=script bodies=N): one bodiless coordinator over its own bodies, no brain.
        if (p.MainHive!.Hierarchy is { } hierarchy) SubhiveWiring.AttachScriptHives(hierarchy, scripts, own, reg, p.Rt.Tools, board, p.Log);
    }

    private static HiveRoster RosterOf(MainParts p) =>
        p.Inproc != null ? new HiveRoster([p.Mc.Username, .. p.Inproc.Accounts]) : new HiveRoster([p.Mc.Username, .. p.Hive.Clones()]);

    /// <summary>One bot's telemetry source: main in-process, an in-process body, a clone over IPC, or offline.</summary>
    private static IBotTelemetrySource SourceFor(MainParts p, string name)
    {
        if (string.Equals(name, p.Mc.Username, StringComparison.OrdinalIgnoreCase)) return p.Source;
        if (p.Inproc != null)
            return new BodyBotSource(name, () => p.Bot.Bodies?.Find(name),
                b => new InProcessBotSource(b.Adapter, p.Rt, p.Telemetry, p.Agent, p.Commands, p.Host.Tui.Viewport, p.Signals.Token) { Activities = b.Activities });
        return p.MainHive?.SourceFor(name) ?? new OfflineBotSource(name);
    }

    /// <summary>Connect (retry with backoff until it works or we're told to stop; the client reconnects by itself afterwards).</summary>
    private static async Task ConnectMainAsync(MinecraftClient client, MinecraftSettings mc, ILogger log, CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(3);
        while (!token.IsCancellationRequested)
        {
            try
            {
                log.LogInformation("connecting to {Host}:{Port} as {User}", mc.Host, mc.Port, mc.Username);
                await client.ConnectAsync(token);
                await client.WaitForJoinStateAsync(TimeSpan.FromSeconds(3), token); // no default 20/20 or fake void in the line
                log.LogInformation("in game: {Status}", client.DescribeStatus());
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogWarning("connect failed: {Error}; retrying in {Delay}s", ex.Message, delay.TotalSeconds);
                try { await Task.Delay(delay, token); } catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }

    /// <summary>In game: the brain, the optional void watch and script, then wait for the stop. Returns the background tasks.</summary>
    private static async Task<List<Task>> RunUntilStopAsync(AppCli cli, MainParts p, CancellationToken token)
    {
        var tasks = new List<Task>();
        if (token.IsCancellationRequested) return tasks;
        p.Agent.Start(token);
        if (cli.VoidAbort) tasks.Add(VoidWatch.RunAsync(p.Game, p.Log, p.Signals.Source));
        if (cli.Script != null) tasks.Add(p.Commands.RunScriptAsync(cli.Script, token));
        try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
        return tasks;
    }

    // ------------------------------------------------------------ graceful shutdown (time-boxed)

    private static async Task ShutdownAsync(MainParts p, MineClaw.Bot.Bodies.BodiesOptions? bodiesOptions, bool useTui, Task tuiTask,
        Supervisor.SupervisorServer? supervisorServer, List<Task> tasks)
    {
        var budget = ShutdownBudget(p);
        var seq = new ShutdownSequence(budget, p.Log);
        p.Log.LogInformation("stopping ({Why}, budget {S:0}s): {Usage}", p.Signals.Reason ?? "requested", budget.TotalSeconds, p.Rt.Usage.Summary());
        await seq.StepAsync("tui", TimeSpan.FromSeconds(2), _ => tuiTask);
        if (supervisorServer != null) await seq.StepAsync("supervisor", TimeSpan.FromSeconds(3), _ => supervisorServer.DisposeAsync().AsTask());
        if (useTui) Console.Out.WriteLine("MineClaw: shutting down...");
        await RecallHivesAsync(p, bodiesOptions, seq);
        await StopBotAsync(p, seq, tasks);
        await DisposeHivesAsync(p, seq);
        seq.Final("db", p.Rt.Dispose);
        seq.Final("telemetry", p.Telemetry.Dispose);
        p.Log.LogInformation("bye{Late}", seq.TimedOut.Count > 0 ? " (timed out: " + string.Join(", ", seq.TimedOut) + ")" : "");
        if (useTui) Console.Out.WriteLine("MineClaw: bye");
    }

    /// <summary>Hive: clones need time to deposit; stay under systemd's TimeoutStopSec=90 (deploy/mineclaw.service).</summary>
    private static TimeSpan ShutdownBudget(MainParts p)
    {
        var budget = TimeSpan.FromSeconds(Math.Clamp(p.Host.ShutdownTimeoutSeconds, 3, 600));
        if (!HasBodiesOut(p)) return budget;
        return TimeSpan.FromSeconds(Math.Max(budget.TotalSeconds, Math.Min(85, p.Hive.ShutdownRecallSeconds + 25)));
    }

    /// <summary>A clone slot that isn't idle or retired, or an in-process body other than main.</summary>
    private static bool HasBodiesOut(MainParts p) =>
        p.MainHive?.Supervisor?.Slots.Any(s => s.State != MineClaw.Hive.Supervisor.CloneState.Idle && s.State != MineClaw.Hive.Supervisor.CloneState.Retired) == true
        || p.Bot.Bodies?.All.Any(b => !b.IsMain) == true;

    /// <summary>Hive first: clones deposit everything and log out while main is still online (handoff receiver), then main stops.</summary>
    private static async Task RecallHivesAsync(MainParts p, MineClaw.Bot.Bodies.BodiesOptions? bodiesOptions, ShutdownSequence seq)
    {
        // A deploy or restart gives no account up: the bodies logging out now stay on the whitelist (add once, keep).
        p.MainHive?.Hierarchy?.Stopping();
        if (p.Inproc is { } inproc)
            await seq.StepAsync("bodies recall", TimeSpan.FromSeconds(bodiesOptions!.ShutdownRecallSeconds), _ =>
                inproc.RecallAllAsync("main shutting down", TimeSpan.FromSeconds(Math.Max(5, Math.Min(bodiesOptions.ShutdownRecallSeconds, seq.Remaining.TotalSeconds) - 1))));
        if (p.MainHive is { } mainHive)
        {
            var hive = p.Hive;
            var subhives = p.Subhives;
            await seq.StepAsync("hive recall", TimeSpan.FromSeconds(hive.ShutdownRecallSeconds), _ =>
            {
                var each = TimeSpan.FromSeconds(Math.Max(5, Math.Min(hive.ShutdownRecallSeconds, seq.Remaining.TotalSeconds) - 1));
                // Clone processes and main's own subhive bodies put everything away in parallel while main is still online.
                return Task.WhenAll(mainHive.RecallAllAsync("main shutting down", each), subhives?.RecallAllAsync("main shutting down", each) ?? Task.CompletedTask);
            });
        }
    }

    /// <summary>Main itself: actions, brain, the pre-logout hook, disconnect, background tasks, then the bot and client objects.</summary>
    private static async Task StopBotAsync(MainParts p, ShutdownSequence seq, List<Task> tasks)
    {
        var (rt, client, bot) = (p.Rt, p.Client, p.Bot);
        await seq.StepAsync("stop actions", TimeSpan.FromSeconds(5), ct =>
        {
            rt.Activities.Cancel("shutdown");
            return client.StopAsync(ct);
        });
        await seq.StepAsync("agent", TimeSpan.FromSeconds(10), _ => p.Agent.StopAsync());
        if (BeforeLogout is { } before && p.Host.PreLogoutSeconds > 0 && client.IsPlaying)
            await seq.StepAsync("pre-logout", TimeSpan.FromSeconds(p.Host.PreLogoutSeconds), ct => before(bot, ct));
        await seq.StepAsync("disconnect", TimeSpan.FromSeconds(5), _ => client.DisconnectAsync("Bye!"));
        await seq.StepAsync("background tasks", TimeSpan.FromSeconds(3), _ => Task.WhenAll(tasks));
        seq.Final("bot", () => { p.Source.Dispose(); bot.Dispose(); });
        await seq.StepAsync("client", TimeSpan.FromSeconds(3), _ => client.DisposeAsync().AsTask());
    }

    /// <summary>In-process hives the recall left running are killed here and their teardown (db connections) awaited, bounded.</summary>
    private static async Task DisposeHivesAsync(MainParts p, ShutdownSequence seq)
    {
        // Every body's last world-memory write, before any teardown that may outlast its step (and so the db closing under it).
        if (p.Inproc != null || p.Subhives != null)
            await seq.StepAsync("world memory", TimeSpan.FromSeconds(10), _ => Task.Run(() =>
            {
                p.Inproc?.FlushWorldMemory();
                p.Subhives?.FlushWorldMemory();
            }));
        if (p.MainHive is { } mainHive) await seq.StepAsync("hive", TimeSpan.FromSeconds(mainHive.InProc != null ? 12 : 3), _ => mainHive.DisposeAsync().AsTask());
        if (p.Inproc is { } inproc) await seq.StepAsync("bodies", TimeSpan.FromSeconds(5), _ => inproc.DisposeAsync().AsTask());
        if (p.Subhives is { } subhives) await seq.StepAsync("subhive bodies", TimeSpan.FromSeconds(5), _ => subhives.DisposeAsync().AsTask());
    }
}
