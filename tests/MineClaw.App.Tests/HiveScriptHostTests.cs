using System.Collections.Concurrent;
using MineClaw.Agent;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.App.Hosting;
using MineClaw.Bot;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Game;
using MineClaw.Bot.Tests;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Hive;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace MineClaw.App.Tests;

/// <summary>
/// A script-only hive end to end in main's process (Fox, 10-29: "just one script to control all the bodies in the Hive"):
/// <c>hive spawn kind=script bodies=3 script=hive_chop</c> allocates three accounts, whitelists each before its login, logs them in
/// a launch gap apart on their own (fake) servers, and runs ONE bodiless coordinator with no brain whose <c>bodies()</c> are exactly
/// those three as they arrive: the first is done before the last logs in, and the last still gets its lane. When the script ends,
/// every body deposits, logs out and leaves the whitelist; main's brain hears one line. Main's own crews never see those bodies.
/// </summary>
[Collection("Release1122World")]
public sealed class HiveScriptHostTests(ITestOutputHelper output)
{
    private static readonly string[] BodyNames = ["maid_chan_101", "maid_chan_102", "maid_chan_103"];
    private static readonly Dictionary<string, (int X, int Y, int Z)> Log = new()
    {
        ["maid_chan_101"] = (9, 64, 9), ["maid_chan_102"] = (7, 64, 9), ["maid_chan_103"] = (9, 64, 7),
    };

    private const string HiveChop = """
        // purpose: every body chops a free log near it (one body per log); one result line
        function chopper() {
          const r = forEach(find('log', { r: 8 }), t => dig(t), { budget: 1 })
          crew.inc('logs', r.n)
          return `${me.body} ${r.n}`
        }
        function main() {
          const first = bodies().length
          const r = everyBody('chopper', null, { wait: 30 })
          return `hive chop: ${crew.get('logs')} logs, ${r.msg} | bodies ${bodies().join(',')} | at start ${first}`
        }
        """;

    private static FakeServer Server(string? account)
    {
        var s = new FakeServer();
        if (account != null && Log.TryGetValue(account, out var t)) s.Blocks[(t.X, t.Y, t.Z)] = Blocks.Log; // each body's own world: one log
        s.SpawnAt = (8.5, 64, 8.5);
        return s;
    }

    private static ClientOptions Fast(ClientOptions o, FakeServer s) => o with
    {
        TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero, RespawnDelay = TimeSpan.FromMilliseconds(10),
        ConnectionFactory = s.CreateClientConnection, HostileAlertRadius = 0, AutoReconnect = false,
    };

    private static async Task<bool> Until(Func<bool> cond, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(10); }
        return cond();
    }

    /// <summary>Main's brain: silent (a hive script needs none).</summary>
    private sealed class QuietLlm : ILlmClient
    {
        public int Calls;
        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            if (r.Tier == ModelTier.Main && !r.Purpose.StartsWith("quick_", StringComparison.Ordinal)) Interlocked.Increment(ref Calls);
            return Task.FromResult(new LlmResponse([new LlmText("")], StopReasons.EndTurn, new LlmUsage(10, 1, 0, 0), r.Model ?? "claude-opus-5-5"));
        }
    }

    /// <summary>No clone processes in this test: main's process hosts every body.</summary>
    private sealed class NoClones : IHiveBodies
    {
        public string Spawn(string task, string? name, bool handover, HiveSpawnOptions options) => "error: no clone processes here";
        public string Recall(string name, string reason) => "no: not out";
        public Task WaitGoneAsync(string name, TimeSpan timeout) => Task.CompletedTask;
        public IReadOnlyCollection<string> Running() => [];
        public SlotFacts Slots() => new(20, ["maid_chan", "poofimafox"]);
        public int Pending() => 0;
        public string List() => "";
    }

    private sealed class ListLogger(List<string> lines) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            lock (lines) lines.Add(formatter(state, exception) + (exception != null ? " !! " + exception.GetType().Name + ": " + exception.Message : ""));
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task A_script_only_hive_drives_its_bodies_with_one_coordinator_late_logins_included_and_releases_them_at_the_end()
    {
        var log = new List<string>();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new ListLogger(log)));
        var servers = new ConcurrentDictionary<string, FakeServer>(StringComparer.OrdinalIgnoreCase);
        var mainServer = servers.GetOrAdd("maid_chan", _ => Server(null));

        // ---- main: allocator, whitelist (the command gate's scoped exception), the tree
        var cfg = new HiveConfig { Subhives = { ViewDistance = 3 } };
        var gate = new CommandGate(new CommandOptions(), () => ["poofimafox"]);
        var managed = new ManagedAccounts(null);
        var whitelist = new WhitelistManager(managed, cfg.Accounts, "maid_chan", gate.ArmManagedWhitelist, (line, _) =>
        {
            bool ok = gate.AllowOutgoing(line);
            if (ok) lock (log) log.Add("sent " + line);
            return Task.FromResult(ok);
        }, loggers.CreateLogger("whitelist"));
        gate.ManagedAccount = whitelist.Accepts;
        var h = new HiveHierarchy(cfg.Accounts, "maid_chan", managed, whitelist, regulars: () => ["poofimafox"], log: loggers.CreateLogger("hierarchy"))
        {
            Bodies = new NoClones(),
        };
        var notices = new List<GameEvent>();
        h.Notice = e => { lock (notices) notices.Add(e); };

        var agent = TestBot.Options();
        agent.Llm.ApiKey = "sk-ant-test";
        var mc = new MinecraftSettings { Host = "127.0.0.1", Port = 1, Username = "maid_chan" };
        var board = new MineClaw.Hive.Claims.ClaimBoard(TimeSpan.FromSeconds(60));
        await using var host = InProcBodies.CreateSubhives(cfg, new BodiesOptions { LaunchGapSeconds = 1.5, LaunchBatch = 1, YieldCheckSeconds = 0, TickReportSeconds = 0 },
            mc, agent, loggers, null, board)!;
        var mainClient = new MinecraftClient(Fast(new ClientOptions { Username = "maid_chan" }, mainServer));
        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await mainClient.ConnectAsync(cts.Token);
            Assert.True(await Until(() => mainClient.World.Player.OnGround));
            var llm = new QuietLlm();
            using var bot = BotHost.Create(new MinecraftGameClient(mainClient), agent, null, llm, loggers, persist: false,
                hiveBots: () => ["maid_chan", .. host.Registry?.Accounts ?? []]);
            var rt = bot.Runtime;
            host.Attach(bot, mainClient, CancellationToken.None);
            SubhiveWiring.AttachMain(h, host, cfg, loggers.CreateLogger("main"));
            h.Resources = null; // the test machine's memory isn't the CT's
            var setup = host.ClientSetup!;
            host.ClientSetup = o => Fast(setup(o), servers.GetOrAdd(o.Username, Server));
            var scripts = MineClaw.Bot.GameTools.ScriptTools.Of(rt)!;
            var crewBoard = new DbCrewBoard(rt.Memory.Db);
            var own = new BodyCrewPort(bot.Bodies!, rt.Tools, crewBoard);
            scripts.Crew = own;
            SubhiveWiring.AttachScriptHives(h, scripts, own, bot.Bodies!, rt.Tools, crewBoard, loggers.CreateLogger("hive"));
            // The Opus brain saved it earlier; this spawn only names it.
            var saved = await bot.Run("script", new { op = "save", name = "hive_chop", code = HiveChop });
            Assert.True(saved.Ok, saved.Text);

            // ---- the spawn: three bodies, ONE script, no brain
            var r = h.Spawn(new SpawnRequest(Kind: "script", Script: "hive_chop", Bodies: 3));
            output.WriteLine(r);
            Assert.StartsWith("ok: hive script hive_chop: ONE script drives 3 bodies (101-103), no brain", r);
            Assert.Equal(["hive_chop"], h.HiveScripts.Keys);
            Assert.All(BodyNames, n => Assert.Equal("hive_chop", h.Tree.Get(n)!.Crew));

            // While they work, main's own crews don't see them (the hive script owns them).
            Assert.True(await Until(() => bot.Bodies!.Find(BodyNames[0]) is { State: BodyState.Online }), "first body online");
            Assert.Empty(own.Bodies());

            // ---- its end: one line to main's brain; every body deposits, logs out, leaves the whitelist
            Assert.True(await Until(() => { lock (notices) return notices.Count > 0; }, 40000), "hive script end: " + string.Join(" / ", Snapshot(log)));
            var note = notices.Single();
            output.WriteLine(note.Text);
            Assert.StartsWith("[hive] hive script hive_chop done: ", note.Text);
            Assert.Contains("hive chop: 3 logs, 3/3 bodies done", note.Text);
            Assert.Contains("| bodies 101,102,103", note.Text); // bodies() = exactly its own, main not among them
            foreach (var n in BodyNames)
                Assert.Equal([Log[n]], servers[n].Dug.Select(d => (d.X, d.Y, d.Z)).Distinct().ToList()); // each on its own connection
            Assert.Empty(mainServer.Dug); // the coordinator has no body; main's never moved
            // Every body was a late login: none was in game when the coordinator started, and each still got its lane.
            Assert.Contains("| at start 0", note.Text);
            Assert.True(await Until(() => h.Tree.Count == 0, 30000), "released: " + h.Tree.Render());
            Assert.True(await Until(() => BodyNames.All(n => Snapshot(log).Contains($"sent /whitelist remove {n}"))));
            var lines = Snapshot(log);
            foreach (var n in BodyNames)
            {
                Assert.True(lines.IndexOf($"sent /whitelist add {n}") is >= 0 and var i && i < lines.FindIndex(l => l.Contains($"connecting {n}")), n + " whitelisted first");
                int deposit = lines.FindIndex(l => l.StartsWith($"bodies: {n}: deposit before logout"));
                Assert.True(deposit >= 0 && deposit < lines.IndexOf($"sent /whitelist remove {n}"), n + " deposits before it leaves the whitelist");
            }
            Assert.Empty(h.HiveScripts);
            Assert.All(managed.All(), a => Assert.False(a.Active));
        }
        finally
        {
            await mainClient.DisposeAsync();
            foreach (var srv in servers.Values) srv.Dispose();
        }
    }

    private static List<string> Snapshot(List<string> log) { lock (log) return log.ToList(); }
}
