using MineClaw.Agent;
using MineClaw.Agent.Telemetry;
using MineClaw.Bot;
using MineClaw.Client;
using MineClaw.Hive;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Db;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;
using MineClaw.World;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// Main-process hive wiring: claim board (hosted here), chat arbiter, budget ledger, shared-db hive store + data_version
/// watcher, the supervisor that runs clone processes, the merged <c>hive</c> tool, TUI sources for the clones, and the
/// shutdown recall. Created before the bot (the bot needs the claim view and hive names), attached after.
/// </summary>
public sealed class MainHive : IAsyncDisposable
{
    private readonly HiveConfig _cfg;
    private readonly string _main;
    private readonly ILogger _log;
    private readonly IReadOnlyList<string> _passThrough;
    private HiveSupervisor? _supervisor;
    private DataVersionWatcher? _watcher;

    private MainHive(HiveConfig cfg, string main, IReadOnlyList<string> passThrough, ILogger log)
    {
        _cfg = cfg;
        _main = main;
        _log = log;
        _passThrough = passThrough;
        Board = new ClaimBoard(TimeSpan.FromSeconds(cfg.ClaimTtlSeconds));
        Arbiter = new ChatArbiter(main, main, cfg.Clones(), sticky: TimeSpan.FromSeconds(cfg.StickySeconds));
    }

    public ClaimBoard Board { get; }
    public ChatArbiter Arbiter { get; }
    public IClaimBoard MainClaims => Board.ViewFor(_main);
    public IEnumerable<string> HiveBots() => [_main, .. _cfg.Clones()];
    public HiveSupervisor? Supervisor => _supervisor;

    /// <summary>Null when the hive is off or has no clones configured.</summary>
    public static MainHive? Create(HiveConfig cfg, string main, AppCli cli, AgentOptions agent, string logDir, ILogger log)
    {
        if (!cfg.Enabled || cfg.Clones().Count == 0) return null;
        var args = new List<string> { "--no-tui", "--no-key-check", "--data-dir", agent.DataDirectory, "--log-dir", logDir };
        if (cli.ConfigPath != null) args.AddRange(["--config", Path.GetFullPath(cli.ConfigPath)]);
        if (cli.LogFormat != null) args.AddRange(["--log-format", cli.LogFormat]);
        // Cached prefix (built with the bot): group asks get split between the bots.
        if (!agent.PersonaExtra.Contains(Agent.Prompts.HiveMainHint, StringComparison.Ordinal))
            agent.PersonaExtra = (agent.PersonaExtra + "\n" + Agent.Prompts.HiveMainHint).Trim();
        if (!agent.PersonaExtra.Contains(HiveTools.ScriptHint, StringComparison.Ordinal))
            agent.PersonaExtra = (agent.PersonaExtra + "\n" + HiveTools.ScriptHint).Trim();
        return new MainHive(cfg, main, args, log);
    }

    public void Attach(BotHost bot, MinecraftClient client, ConsoleCommands commands, AgentOptions agent)
    {
        var rt = bot.Runtime;
        var chat = rt.Chat;
        Arbiter.Bind(chat); // ownership, thread leases (who asked owns the reply), never eating a clone's reply
        void Names() => Arbiter.SetLive(Arbiter.Live, [chat.DisplayName, .. chat.Aliases, chat.Username]);
        Names();
        chat.IdentityChanged += Names;

        var store = new HiveStore(rt.Db, _main);
        _watcher = new DataVersionWatcher(rt.Db, () => { rt.Memory.Reload(); rt.Rag.ReloadVectors(); });

        var ledger = new BudgetLedger(agent.Budget, _cfg.Budget, () =>
        {
            var (d, m) = rt.Usage.SnapshotTotals();
            return (d.TotalUsd, m.TotalUsd);
        }, path: Path.Combine(agent.DataDirectory, "hive-ledger.json"));
        rt.Usage.ExternalSpend = ledger.ClonesSpend; // caps are shared by the whole hive

        var regulars = _cfg.RegularPlayers.Length > 0 ? _cfg.RegularPlayers : agent.Self.TrustedPlayers;
        var launcher = new ProcessLauncher(extraArgs: _passThrough, log: _log);
        _supervisor = new HiveSupervisor(_cfg, _main, launcher, Board, ledger, Arbiter, store,
            () => new SlotFacts(client.World.MaxPlayers, client.World.Entities.OnlinePlayers), regulars, _log)
        {
            Handoff = new Receiver(client),
        };
        _supervisor.Notice += e => rt.Events.Post(e);
        // A clone's result is reportable only after its result event arrives: drop pending-result claims from main's chat.
        var sup = _supervisor;
        rt.Agent.SpeechGate ??= (_, text, _) => sup.GuardSpeech(text);
        // Uncached tail, only while clones are online: who's out and idle, plus a split reminder on a group ask.
        rt.Agent.TurnHint = batch =>
        {
            var asks = batch.Waking.Where(e => e.Kind is Agent.Events.EventKind.Chat or Agent.Events.EventKind.Whisper && e.Player != null)
                .Select(e => new Agent.Chat.ChatMessage(Agent.Chat.ChatKind.Public, e.Player!, e.Text, e.Text)).ToList();
            // A line naming several bots (night review B2): the named clones answer their part, main coordinates.
            var named = asks.Select(Arbiter.Route).Where(r => r.Group && r.Addressed != null)
                .SelectMany(r => r.Addressed!).Where(n => !n.Equals(_main, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return _supervisor.HintLine(groupAsk: asks.Any(Arbiter.IsGroupAsk), named: named);
        };
        rt.Usage.ModeChanged += (mode, reason) =>
        {
            if (mode != Agent.Budget.BudgetMode.Normal) _supervisor.RecallBudget($"budget mode {mode}: {reason}");
        };
        commands.Injected = raw => _ = _supervisor.InjectAsync(raw);
        // Model profiles for clone brains; script spawns (lint + save on main, the clone runs it by name, brain asleep).
        _supervisor.ProfileCheck = name => agent.Llm.CheckProfile(name);
        if (MineClaw.Bot.GameTools.ScriptTools.Of(rt) is { } bridge)
            _supervisor.ScriptPrep = (script, code) => bridge.PrepareForHive(script, code, Agent.Tools.ToolCaller.Current?.Tag);
        _supervisor.ResetClone = clone =>
        {
            if (rt.Memory.GetStandingOrder(clone) is { Player: CloneScriptRunner.OrderPlayer }) rt.Memory.ClearStandingOrder(clone, "new hive task");
        };
        HiveTools.RegisterMain(rt.Tools, _supervisor, agent.Llm);
        _log.LogInformation("hive: {N} clone(s) configured ({Names}); regulars {Regulars}; {Budget}", _cfg.Clones().Count,
            string.Join(", ", _cfg.Clones()), string.Join(", ", regulars), ledger.Summary());
    }

    public IBotTelemetrySource? SourceFor(string name) =>
        _supervisor?.Slot(name) is { } slot ? new RemoteBotSource(slot) : null;

    public Task RecallAllAsync(string reason, TimeSpan timeout) =>
        _supervisor?.RecallAllAsync(reason, timeout) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        if (_supervisor != null) await _supervisor.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Item handoff: the main bot's feet + counting what it picks up (0x16 Collect Item for its own entity).</summary>
    private sealed class Receiver : IHandoffReceiver
    {
        private readonly MinecraftClient _client;
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

        public Receiver(MinecraftClient client)
        {
            _client = client;
            client.ItemCollected += (collector, _, stack) =>
            {
                if (collector != _client.World.Player.EntityId) return;
                lock (_counts) foreach (var k in _counts.Keys.ToList()) _counts[k] += Math.Max(1, (int)(stack?.Count ?? 1));
            };
        }

        public Vec3d? Position => _client.IsPlaying ? _client.World.Player.Position : null;

        public void BeginCounting(string from) { lock (_counts) _counts[from] = 0; }
        public int Collected(string from) { lock (_counts) return _counts.GetValueOrDefault(from); }
        /// <summary>Night review B24: main stands still (its job paused, not aborted) while a clone tosses at its feet.</summary>
        public IDisposable? HoldStill(TimeSpan max) => _client.IsPlaying ? _client.HoldStill(max) : null;
    }
}
