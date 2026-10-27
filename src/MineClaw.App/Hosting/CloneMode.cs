using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Sources;
using MineClaw.App.Viewport;
using MineClaw.Bot;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Hive;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Db;
using MineClaw.Hive.Inventory;
using MineClaw.Hive.Ipc;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// <c>--clone &lt;name&gt; --parent-pipe &lt;pipe&gt;</c>: this process is one hive clone, started and supervised by the main
/// process. Same three-tier brain and game tools as main, logged in as its own whitelisted account, sharing the main db;
/// its own personality shard (scope = its username via <c>AgentOptions.Hive</c>), usage file, logs, sub-capped budget
/// authorised by main's ledger, remote claim board and chat arbiter. Headless; no RAPTOR maintenance (main only).
/// </summary>
public static class CloneMode
{
    /// <summary>Per-clone overrides, applied before the config check.</summary>
    public static (string LogDir, string TelemetryDir) Prepare(string clone, MinecraftSettings mc, AgentOptions agent, HiveConfig hive,
        string logDir, string telemetryDir)
    {
        var mainLogin = mc.Username;
        var mainDisplay = string.IsNullOrWhiteSpace(agent.Chat.DisplayName) ? mainLogin : agent.Chat.DisplayName;
        var num = ChatArbiter.NumberOf(clone) ?? clone;
        mc.Username = clone;
        agent.Chat.Username = clone;
        agent.Chat.DisplayName = string.Format(hive.CloneDisplayFormat, mainDisplay, num);
        agent.Chat.Aliases = [$"{mainDisplay} {num}"];
        agent.Hive.MainUsername = mainLogin;                  // personality: main's lines are a bot's, not a player's
        agent.Budget.UsageFile = $"usage-{clone}.json";        // own totals (main's ledger enforces the shared caps)
        // Sub-caps are optional: 0/unset = inherit main's hard caps (main's ledger still enforces the shared hive total).
        agent.Budget.DailyCapUsd = hive.Budget.CloneDailyCap(agent.Budget.DailyCapUsd);
        agent.Budget.MonthlyCapUsd = hive.Budget.CloneMonthlyCap(agent.Budget.MonthlyCapUsd);
        agent.Rag.Raptor.Maintenance = false;                  // background RAPTOR jobs run in the main process only
        agent.Rag.RestoreJournal = false;                      // the latest journal is main's, not this clone's
        agent.IdleCheckInMinutes = 0;                          // a clone wakes only for its name, its tasks and its own danger
        agent.PersonaExtra = (agent.PersonaExtra + $"\nYou are {agent.Chat.DisplayName} (login {clone}), one of {mainDisplay}'s hive clones, " +
            $"sent out for one task. {mainLogin} and the other maid_chan_* clones are your hive, not players: never chat with them; " +
            "report to the main bot with hive_report (done/failed ends your outing). Chat only when a player names you " +
            "(or your task says to greet/tell someone, then one short line); never mention the hive, clones or your task status in chat.").Trim();
        var dir = Path.Combine(logDir, "hive", clone);
        var tdir = Path.Combine(telemetryDir, "hive", clone);
        return (dir, tdir);
    }

    public static async Task<int> RunAsync(AppCli cli, MinecraftSettings mc, AgentOptions agentOptions, BotOptions botOptions, HostSettings host,
        HiveConfig hive, TelemetryOptions telemetryOptions, string telemetryDir, ILoggerFactory loggers, ILogger log, ShutdownSignals signals)
    {
        var clone = cli.CloneName!;
        var host0 = new CloneHost(clone, cli.ParentPipe!, hive, log: loggers.CreateLogger("MineClaw.Hive"));
        // Never unsupervised: the parent pipe comes first, before the shared db is opened or anything logs in.
        var welcome = await host0.ConnectParentAsync(TimeSpan.FromSeconds(10), signals.Token);
        if (welcome == null) return CloneExitCodes.NoParent;

        var telemetry = new PromptTelemetry(agentOptions, telemetryOptions, telemetryOptions.Enabled ? telemetryDir : null);
        ILlmClient llm = new AnthropicLlmClient(agentOptions.Llm);
        if (telemetryOptions.Enabled) llm = telemetry.Wrap(llm);
        var gated = new GatedLlmClient(llm, agentOptions.Prices, hive.Budget, () => host0.Link);
        gated.Denied += why => { log.LogWarning("hive budget refused a call: {Why}; logging out", why); _ = host0.LogoutAsync("budget", planned: true); };

        var clientLog = loggers.CreateLogger("MineClaw.Client");
        var client = new MinecraftClient(new ClientOptions
        {
            Host = mc.Host, Port = mc.Port, Username = clone,
            AutoReconnect = true,
            AutoRespawn = !hive.OnCloneDeath.Equals("logout", StringComparison.OrdinalIgnoreCase),
            Log = m => clientLog.LogDebug("{Msg}", m),
        });
        var game = new MinecraftGameClient(client);
        var bot = BotHost.Create(game, agentOptions, botOptions, llm: gated, loggers: loggers, claims: host0.Claims,
            hiveBots: () => [host0.Main ?? "", .. hive.Clones()]);
        var rt = bot.Runtime;
        telemetry.Attach(rt);
        game.ReservedItems = new HashSet<int>(); // a clone keeps nothing back: everything goes into storage before logout

        // Identity: the identity row in the shared db is main's; a clone has its own fixed chat name and can't rename it.
        rt.Chat.SetIdentity(agentOptions.Chat.DisplayName, agentOptions.Chat.Aliases);
        rt.Tools.Remove(MineClaw.Agent.Memory.IdentityTools.ToolName);
        // Hears only what's for it (its name, its IPC tasks, its own danger), speaks only when addressed, never narrates the
        // hive; main is the single chat_log writer for public chat. Muted narration goes to main as task progress.
        ClonePolicy.Apply(rt.Chat, rt.Events, host0.Arbiter, rt.Agent,
            muted: text => _ = host0.ReportAsync("progress", text.Length > 160 ? text[..160] : text));
        rt.Chat.Accepted += m => { if (m.Player != null) host0.ChatAccepted(m.Player); };
        HiveTools.RegisterClone(rt.Tools, host0);

        var store = new HiveStore(rt.Db, clone);
        host0.Store = store;
        host0.RecordExperience = (key, text) =>
        {
            if (!store.RecordOnce("death", key, clone, text)) return false;
            rt.Rag.Upsert(new RagDoc("hive:" + key, "hive", $"{clone} died", text) { Source = "hive" });
            return true;
        };
        using var watcher = new DataVersionWatcher(rt.Db, () => { rt.Memory.Reload(); rt.Rag.ReloadVectors(); },
            slowRefresh: rt.Raptor is { } rap ? () => _ = rap.LoadAsync() : null);

        string lastDanger = "died";
        DateTimeOffset lastDangerAt = default;
        client.Danger += d => { lastDanger = d.Message; lastDangerAt = DateTimeOffset.UtcNow; };
        client.ChatReceived += line => log.LogInformation("chat: {Line}", ChatText.StripColours(line));
        client.Kicked += r => log.LogWarning("kicked: {Reason}", r);

        var agent = new PausableAgent(rt, log);
        var commands = new ConsoleCommands(game, rt, agent, log, signals.Source);
        var source = new InProcessBotSource(game, rt, telemetry, agent, commands, host.Tui.Viewport, signals.Token);
        var body = new GameCloneBody(client, game, rt, agent, commands, source, telemetry, new GameInventoryPort(game, rt.Observations, rt.Memory), log,
            () => DateTimeOffset.UtcNow - lastDangerAt < TimeSpan.FromSeconds(15) ? lastDanger : "died");
        host0.Body = body;
        log.LogInformation("clone {Clone} starting (pipe {Pipe}, scope {Scope})", clone, cli.ParentPipe, agentOptions.Hive.ScopeFor(clone));

        int code = await host0.RunAsync(signals.Token, welcome);

        log.LogInformation("clone {Clone} exiting ({Code}): {Reason}; {Usage}", clone, code, host0.LogoutReason ?? "-", rt.Usage.Summary());
        try { await agent.StopAsync(); } catch (Exception) { }
        source.Dispose();
        bot.Dispose();
        try { await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        rt.Dispose();
        telemetry.Dispose();
        return code;
    }

    /// <summary>The real clone body: client + brain + console + telemetry of this process.</summary>
    private sealed class GameCloneBody(MinecraftClient client, IGameClient game, AgentRuntime rt, PausableAgent agent, ConsoleCommands commands,
        InProcessBotSource source, PromptTelemetry telemetry, IInventoryPort inventory, ILogger log, Func<string> deathCause) : ICloneBody
    {
        private bool _wired;

        public string Name => game.Username;
        public bool IsPlaying => game.IsPlaying;
        public IInventoryPort Inventory => inventory;
        public BotSnapshot Snapshot(bool full) => source.Snapshot(full);

        public event Action<PromptUsageRecord>? UsageRecorded
        {
            add => telemetry.Recorded += value;
            remove => telemetry.Recorded -= value;
        }

        private Action<string>? _died;
        public event Action<string>? Died
        {
            add { _died += value; if (!_wired) { _wired = true; client.Died += () => _died?.Invoke(deathCause()); } }
            remove => _died -= value;
        }

        public async Task ConnectAsync(CancellationToken ct)
        {
            var delay = TimeSpan.FromSeconds(3);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    log.LogInformation("connecting to {Host}:{Port} as {User}", client.Options.Host, client.Options.Port, client.Options.Username);
                    await client.ConnectAsync(ct);
                    log.LogInformation("in game: {Status}", client.DescribeStatus());
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    log.LogWarning("connect failed: {Error}; retrying in {Delay}s", ex.Message, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
                }
            }
        }

        public void StartAgent(CancellationToken ct) => agent.Start(ct);

        public async Task StopAgentAsync()
        {
            rt.Activities.Cancel("logout");
            await agent.StopAsync();
            try { await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        }

        public void AssignTask(TaskMsg task)
        {
            var text = (task.Restarted ? "(restarted after a crash: check your inventory first) " : "") +
                $"[hive task] {task.Text} — take what you need from storage first; when finished call hive_report done (or failed).";
            rt.Events.Post(GameEvent.Notice(text, EventPriority.Normal));
        }

        public async Task<bool> ExecuteAsync(string line, CancellationToken ct) { await commands.ExecuteAsync(line, ct); return true; }

        public void InjectChat(string raw) => rt.Chat.OnServerChat(raw, test: true);

        public Task DisconnectAsync(string reason) => client.DisconnectAsync(reason);
    }
}
