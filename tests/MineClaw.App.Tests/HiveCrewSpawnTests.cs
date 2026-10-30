using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Hosting;
using MineClaw.Bot;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Client.Versioning;
using MineClaw.Data.Versioning;
using MineClaw.Hive;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Db;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace MineClaw.App.Tests;

/// <summary>
/// Fox 10-30: "it would be nice if it could spawn up its bodies with a script already made, so its bodies just go straight to work."
/// End to end with the real <see cref="CloneMode"/> as an in-process hive on fake servers, b1.8.1 (one login at a time) and 1.12.2:
/// <c>hive spawn brain=opus bodies=2 script=crew_job</c> gives ONE hive whose bodies run the saved crew script as they log in, the
/// coordinator started by C# with no brain turn; the brain's first call is the crew's result, which it reports. The hive's own
/// <c>hive spawn bodies=1 script=crew_job</c> brings a further body in straight onto the script.
/// </summary>
[Collection("Release1122World")]
public sealed class HiveCrewSpawnTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-hive-crew", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private static async Task<bool> Until(Func<bool> cond, int ms = 30000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    /// <summary>
    /// The hive's brain: every main-tier call is kept. On the crew's result it reports done (after <paramref name="more"/>: first a further
    /// body straight onto the script through its own hive tool, then done once that crew ends too).
    /// </summary>
    private sealed class CrewBrain(bool more) : ILlmClient
    {
        public readonly ConcurrentQueue<string> Calls = new();
        private int _step;

        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            if (r.Tier != ModelTier.Main || r.Purpose.StartsWith("quick_", StringComparison.Ordinal)) return Task.FromResult(Say(r));
            var text = string.Join("\n", r.Messages.SelectMany(m => m.Content).Select(c => c switch
            {
                LlmText t => t.Text, LlmToolResult tr => "[result] " + tr.Content, _ => "",
            }));
            Calls.Enqueue(text);
            if (r.Messages[^1].Content.Any(c => c is LlmToolResult)) return Task.FromResult(Say(r)); // the tool's answer: end the turn
            int crews = System.Text.RegularExpressions.Regex.Matches(text, @"\[crew crew_job done\]").Count;
            if (more && crews == 1 && Interlocked.CompareExchange(ref _step, 1, 0) == 0)
                return Task.FromResult(Use(r, "hive", new { op = "spawn", bodies = 1, script = "crew_job" }));
            if (crews >= (more ? 2 : 1) && Interlocked.Exchange(ref _step, 9) != 9)
                return Task.FromResult(Use(r, HiveTools.CloneTool, new { status = "done", text = "the crew is done" }));
            return Task.FromResult(Say(r));
        }

        private static LlmResponse Use(LlmRequest r, string tool, object input) =>
            new([new LlmToolUse("tu_" + Guid.NewGuid().ToString("N")[..6], tool, JsonSerializer.SerializeToElement(input))], StopReasons.ToolUse,
                new LlmUsage(100, 20, 0, 0), r.Model ?? "claude-opus-5-5");

        private static LlmResponse Say(LlmRequest r) => new([new LlmText("")], StopReasons.EndTurn, new LlmUsage(10, 1, 0, 0), r.Model ?? "claude-opus-5-5");
    }

    private const string CrewJob = """
        // purpose: each body marks the board, one result line
        function worker() { wait(0.2); crew.inc('n'); crew.set('by_' + me.body, 1); return 'worked' }
        function main() { return `crew: ${everyBody('worker', null, { wait: 60 }).msg}` }
        """;

    public static TheoryData<string, bool> Cases => new() { { "b1.8.1", false }, { "1.12.2", false }, { "b1.8.1", true } };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_hive_spawned_onto_a_crew_script_puts_its_bodies_to_work_with_no_brain_turn(string version, bool more)
    {
        var profile = version == "1.12.2" ? GameVersionProfiles.Java1122 : GameVersionProfiles.Beta181;
        using var _ = GameData.Override(profile.Data);
        Directory.CreateDirectory(_dir);
        var log = new List<string>();
        void Log(string l) { lock (log) log.Add(l); }
        var servers = new ConcurrentDictionary<string, FakeServer>(StringComparer.OrdinalIgnoreCase);
        FakeServer ServerFor(string name) => servers.GetOrAdd(name, _ => new FakeServer(release1122: version == "1.12.2"));
        var cfg = new HiveConfig
        {
            HeartbeatSeconds = 0.5, HeartbeatTimeoutSeconds = 6, StartTimeoutSeconds = 30, BackoffBaseSeconds = 60, MaxCrashes = 1,
            SnapshotSeconds = 0.3, LaunchGapSeconds = 0, DepositTimeoutSeconds = 3, FailureGraceSeconds = 0, Subhives = { ViewDistance = 3 },
        };
        var brain = new CrewBrain(more);
        AppSettings Bind()
        {
            var agent = MineClaw.Bot.Tests.TestBot.Options();
            agent.DataDirectory = _dir;
            agent.Rag.SeedFile = "";
            agent.Llm.ApiKey = "sk-ant-test";
            agent.Self.TrustedPlayers = ["poofimafox"];
            var hive = new HiveConfig { HeartbeatSeconds = 0.5, SnapshotSeconds = 0.3, DepositTimeoutSeconds = 3, FailureGraceSeconds = 0, LaunchGapSeconds = 0,
                Subhives = { ViewDistance = 3 } };
            return new AppSettings(new MinecraftSettings { Host = "127.0.0.1", Port = 1, Username = "maid_chan" }, agent, new BotOptions { GeneratorQueueSeconds = 0 },
                new HostSettings(), hive, new TelemetryOptions { Enabled = false }, new Supervisor.SupervisorOptions(),
                new MineClaw.Bot.Bodies.BodiesOptions { LaunchGapSeconds = 0, YieldCheckSeconds = 0, TickReportSeconds = 0, ShutdownRecallSeconds = 10 },
                Path.Combine(_dir, "logs"), Path.Combine(_dir, "logs"));
        }
        var settings = new InProcHiveSettings(AppCli.Parse([]), Bind, profile, Path.Combine(_dir, "logs"));
        await using var launcher = new InProcHiveLauncher(cfg, settings.For, profile, NullLoggerFactory.Instance)
        {
            ClientSetup = o => o with
            {
                TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero, RespawnDelay = TimeSpan.FromMilliseconds(10),
                ConnectionFactory = ServerFor(o.Username).CreateConnection, HostileAlertRadius = 0, AutoReconnect = false,
            },
            Llm = (_, _) => brain,
        };
        using var db = AgentDb.Open(Path.Combine(_dir, "mineclaw.db"));
        // The crew script was saved earlier by main's Opus brain (one memory: the hive runs it by name).
        new ScriptLibrary(new MemoryStore(db, null)).SaveVersion("crew_job", CrewJob, author: "main/claude-opus-5-5", lang: "js");
        var sup = new HiveSupervisor(cfg, "maid_chan", launcher, new ClaimBoard(TimeSpan.FromSeconds(60)),
            new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, cfg.Budget, () => (0m, 0m)),
            new ChatArbiter("maid_chan", "maid_chan", cfg.Clones()), new HiveStore(db, "maid_chan"),
            () => new SlotFacts(20, ["maid_chan", "poofimafox", .. servers.Where(s => s.Value.LoggedIn).Select(s => s.Key)]), ["poofimafox"]);
        try
        {
            var gate = new MineClaw.Agent.Chat.CommandGate(new MineClaw.Agent.Chat.CommandOptions(), () => ["poofimafox"]);
            var managed = new ManagedAccounts(null);
            int adds = 0;
            var whitelist = new WhitelistManager(managed, cfg.Accounts, "maid_chan", gate.ArmManagedWhitelist, async (line, _) =>
            {
                // A late join: every body after the first is whitelisted (so logs in) well after the first one's lane has ended.
                if (line.Contains(" add ", StringComparison.Ordinal) && Interlocked.Increment(ref adds) > 2) await Task.Delay(1500);
                bool ok = gate.AllowOutgoing(line);
                if (ok) Log("sent " + line);
                return ok;
            }, NullLogger.Instance);
            gate.ManagedAccount = whitelist.Accepts;
            var notices = new List<string>();
            var h = new HiveHierarchy(cfg.Accounts, "maid_chan", managed, whitelist, regulars: () => ["poofimafox"])
            {
                Bodies = new SupervisorBodies(sup), ProfileCheck = _ => null,
                Notice = e => { lock (notices) notices.Add(e.Text); },
                RunsInProcess = (i, p) => cfg.RunsInProcess(i, p), InProcRunning = sup.RunningInProcess,
                CrewPrep = (s, _) => (s, null), // main's prep: the saved crew script (HiveScriptHost.PrepareCrew in the App)
            };
            sup.Accounts = h;
            sup.Notice += e => { lock (notices) notices.Add(e.Text); };
            var bus = new MineClaw.Hive.Hierarchy.Bus.HiveBus("maid_chan") { MainHandler = h.HandleBusAsync };
            h.Bus = bus;
            sup.Bus = bus;
            SubhiveWiring.AttachMain(h, null, cfg, NullLogger.Instance);
            h.Resources = null;

            // ---- the brain's one call: a hive whose 2 bodies go straight to work on crew_job
            var spawned = h.Spawn(new SpawnRequest(Brain: "opus", Script: "crew_job", Bodies: 2));
            output.WriteLine(spawned);
            Assert.StartsWith("ok", spawned);
            Assert.Contains("ONE Opus hive whose brain will drive 2 subhive bodies", spawned);
            Assert.Contains("they go straight to work on crew_job as they arrive (no brain turn first", spawned);
            var hiveNode = h.Tree.All().Single(n => n.Kind == CloneKind.Hive);
            Assert.Equal("crew_job", sup.Slot(hiveNode.Account)!.Task!.Crew);
            Assert.Null(sup.Slot(hiveNode.Account)!.Task!.Script); // not a script-only clone: it keeps its brain and hosts its bodies

            // ---- both bodies log in (b1.8.1: one at a time) and each runs its lane; the crew's one result is the brain's first call
            string Board(string key) => db.Scalar("SELECT value FROM crew_board WHERE key = $k AND crew LIKE 'crew_job@%'", ("$k", key)) as string ?? "";
            bool worked = await Until(() => brain.Calls.Any(c => c.Contains("[crew crew_job done]")), 60000);
            if (!worked) output.WriteLine(string.Join(" / ", log) + " || " + string.Join(" || ", notices) + Environment.NewLine + HiveLog(hiveNode.Account));
            Assert.True(worked, "the crew's result reached the brain: " + h.Tree.Render());
            var bodies = h.Tree.All().Where(n => n.Kind == CloneKind.Subhive).Select(n => n.Account).ToList();
            Assert.True(bodies.Count >= 2, h.Tree.Render());
            var first = brain.Calls.First();
            output.WriteLine(first.Length > 1500 ? first[^1500..] : first);
            Assert.Contains("[crew crew_job done] crew: 2/2 bodies done", first); // no brain turn before the crew's end, the late body counted
            Assert.Contains("[hive task] crew crew_job", first);                 // the task rode along as context
            foreach (var b in bodies.Take(2)) Assert.Equal("1", Board("by_" + b[(b.LastIndexOf('_') + 1)..]));
            lock (log) Assert.All(bodies.Take(2), b => Assert.Contains($"sent /whitelist add {b}", log)); // a third (the hive tool's own, below) may be mid-allocation
            var hiveLog = HiveLog(hiveNode.Account);
            Assert.Contains("started over the bodies spawned for it (no brain turn)", hiveLog);

            if (more)
            {
                // ---- the hive's own hive tool: one more body straight onto the script, ONE crew over just it
                Assert.True(await Until(() => brain.Calls.Count(c => c.Contains("[crew crew_job done]")) >= 2 || h.Tree.All().Count(n => n.Kind == CloneKind.Subhive) == 3, 60000));
                var spawnResult = brain.Calls.FirstOrDefault(c => c.Contains("[result] ok: 1 subhive body"));
                Assert.True(spawnResult != null, "the hive tool's answer: " + string.Join(" ## ", brain.Calls.Select(c => c.Length > 300 ? c[^300..] : c)));
                Assert.Contains("they go straight to work on crew_job as they log in: ONE crew over just them", spawnResult);
                Assert.True(await Until(() => brain.Calls.Any(c => c.Contains("crew: 1/1 bodies done")), 60000), "the second crew's result");
                var third = h.Tree.All().Where(n => n.Kind == CloneKind.Subhive).Select(n => n.Account).Except(bodies.Take(2)).Single();
                Assert.Equal("1", db.Scalar("SELECT value FROM crew_board WHERE key = $k", ("$k", "by_" + third[(third.LastIndexOf('_') + 1)..])) as string);
            }

            // ---- the brain reports done: the hive's bodies deposit and log out first, then the hive
            Assert.True(await Until(() => { lock (notices) return notices.Any(n => n.Contains("done: the crew is done")); }, 30000), string.Join(" || ", notices));
            Assert.True(await Until(() => launcher.Find(hiveNode.Account)!.HasExited, 40000), "the hive logged out");
        }
        finally
        {
            try { await sup.RecallAllAsync("test over", TimeSpan.FromSeconds(10)); } catch (Exception) { }
            await sup.DisposeAsync();
            foreach (var s in servers.Values) s.Dispose();
        }
    }

    private string HiveLog(string clone)
    {
        var dir = Path.Combine(_dir, "logs", "hive", clone);
        return Directory.Exists(dir) ? string.Join("\n", Directory.GetFiles(dir, "mineclaw-*.log").Select(f =>
        {
            using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new StreamReader(s).ReadToEnd();
        })) : "";
    }
}
