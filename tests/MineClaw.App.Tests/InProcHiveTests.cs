using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
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
using MineClaw.Hive.Clone;
using MineClaw.Hive.Db;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace MineClaw.App.Tests;

/// <summary>
/// Fox 10-30: "consolidate everything into the same process and use threads instead … so that we can still go back to the beta
/// version and work the same". A hive-kind clone is the real <see cref="CloneMode"/> wiring (its own brain loop on a fake model, its
/// own client on a fake server, the shared db) run by <see cref="InProcHiveLauncher"/> inside the test process, supervised by the
/// real <see cref="HiveSupervisor"/> over the in-memory link: spawn, task, result, the logout gate, the watchdog and containment, on
/// b1.8.1 and on 1.12.2 alike.
/// </summary>
[Collection("Release1122World")]
public sealed class InProcHiveTests(ITestOutputHelper output) : IDisposable
{
    private const string Clone = "maid_chan_101";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-inproc-hive", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private static async Task<bool> Until(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    /// <summary>The hive's brain: on its "[hive task]" turn it calls <c>hive_report done</c> (or hangs, or ignores cancellation).</summary>
    private sealed class FakeBrain(string mode) : ILlmClient
    {
        private int _reported;
        public readonly ConcurrentQueue<string> Tasks = new();

        public async Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            var text = string.Join("\n", r.Messages.SelectMany(m => m.Content).OfType<LlmText>().Select(t => t.Text));
            if (mode != "idle" && r.Tier == ModelTier.Main && !r.Purpose.StartsWith("quick_", StringComparison.Ordinal)
                && text.Contains("[hive task]", StringComparison.Ordinal) && Interlocked.Exchange(ref _reported, 1) == 0)
            {
                Tasks.Enqueue(text);
                switch (mode)
                {
                    case "hang": await Task.Delay(Timeout.Infinite, ct); break;          // a call that never answers (honours cancel)
                    case "stuck": await Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None); break; // ignores cancellation
                }
                return new LlmResponse([new LlmToolUse("tu_1", HiveTools.CloneTool,
                    JsonSerializer.SerializeToElement(new { status = "done", text = "chopped 3 logs" }))], StopReasons.ToolUse, new LlmUsage(100, 20, 0, 0),
                    r.Model ?? "claude-opus-5-5");
            }
            return new LlmResponse([new LlmText("")], StopReasons.EndTurn, new LlmUsage(10, 1, 0, 0), r.Model ?? "claude-opus-5-5");
        }
    }

    private sealed class Main : IAsyncDisposable
    {
        public required HiveSupervisor Sup { get; init; }
        public required InProcHiveLauncher Launcher { get; init; }
        public required AgentDb Db { get; init; }
        public required FakeServer Server { get; init; }
        public readonly List<string> Notices = new();
        public readonly List<string> Watchdog = new();
        public string Text() { lock (Notices) return string.Join(" || ", Notices); }

        public async ValueTask DisposeAsync()
        {
            try { await Sup.RecallAllAsync("test over", TimeSpan.FromSeconds(10)); } catch (Exception) { }
            await Sup.DisposeAsync();
            await Launcher.DisposeAsync();
            Server.Dispose();
            Db.Dispose();
        }
    }

    private async Task<Main> StartMain(GameVersionProfile profile, string brain, Action<HiveConfig>? tune = null, Func<string, AgentOptions, ILlmClient>? llm = null)
    {
        Directory.CreateDirectory(_dir);
        var cfg = new HiveConfig
        {
            CloneUsernames = [Clone], HeartbeatSeconds = 0.5, HeartbeatTimeoutSeconds = 6, StartTimeoutSeconds = 30, BackoffBaseSeconds = 60,
            MaxCrashes = 1, SnapshotSeconds = 0.3, LaunchGapSeconds = 0, DepositTimeoutSeconds = 3, FailureGraceSeconds = 0,
        };
        tune?.Invoke(cfg);
        var server = new FakeServer(release1122: profile.Version == MineClaw.Protocol.Versioning.GameVersion.Release1122);
        var fake = new FakeBrain(brain);
        // What the App binds per hive from the config: fresh objects each time, prepared like a clone process.
        AppSettings Bind()
        {
            var agent = MineClaw.Bot.Tests.TestBot.Options();
            agent.DataDirectory = _dir;
            agent.Rag.SeedFile = "";
            agent.Llm.ApiKey = "sk-ant-test";
            agent.Self.TrustedPlayers = ["poofimafox"];
            var hive = new HiveConfig { HeartbeatSeconds = cfg.HeartbeatSeconds, SnapshotSeconds = cfg.SnapshotSeconds, DepositTimeoutSeconds = cfg.DepositTimeoutSeconds,
                FailureGraceSeconds = 0, InProc = cfg.InProc };
            return new AppSettings(new MinecraftSettings { Host = "127.0.0.1", Port = 1, Username = "maid_chan" }, agent, new BotOptions { GeneratorQueueSeconds = 0 },
                new HostSettings(), hive, new TelemetryOptions { Enabled = true }, new Supervisor.SupervisorOptions(),
                new MineClaw.Bot.Bodies.BodiesOptions { LaunchGapSeconds = 0, YieldCheckSeconds = 0, TickReportSeconds = 0 },
                Path.Combine(_dir, "logs"), Path.Combine(_dir, "logs"));
        }
        var settings = new InProcHiveSettings(AppCli.Parse([]), Bind, profile, Path.Combine(_dir, "logs"));
        var launcher = new InProcHiveLauncher(cfg, settings.For, profile, NullLoggerFactory.Instance)
        {
            ClientSetup = o => o with
            {
                TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero, RespawnDelay = TimeSpan.FromMilliseconds(10),
                ConnectionFactory = server.CreateConnection, HostileAlertRadius = 0, AutoReconnect = false,
            },
            Llm = llm ?? ((_, _) => fake),
        };
        var db = AgentDb.Open(Path.Combine(_dir, "mineclaw.db"));
        var ledger = new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, cfg.Budget, () => (0m, 0m));
        var sup = new HiveSupervisor(cfg, "maid_chan", launcher, new ClaimBoard(TimeSpan.FromSeconds(60)), ledger,
            new ChatArbiter("maid_chan", "maid_chan", cfg.Clones()), new HiveStore(db, "maid_chan"),
            () => new SlotFacts(20, ["maid_chan", "poofimafox"]), ["poofimafox"]);
        var main = new Main { Sup = sup, Launcher = launcher, Db = db, Server = server };
        sup.Notice += e => { lock (main.Notices) main.Notices.Add(e.Text); };
        launcher.Notice = (c, why) => { lock (main.Watchdog) main.Watchdog.Add($"{c}: {why}"); };
        return await Task.FromResult(main);
    }

    private static IDisposable Version(GameVersionProfile p) => GameData.Override(p.Data);

    public static TheoryData<string> Versions => new() { "b1.8.1", "1.12.2" };

    private static GameVersionProfile Profile(string v) => v == "1.12.2" ? GameVersionProfiles.Java1122 : GameVersionProfiles.Beta181;

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task A_hive_in_mains_process_logs_in_takes_its_task_reports_and_logs_out_through_the_gate(string version)
    {
        var profile = Profile(version);
        using var _ = Version(profile);
        await using var main = await StartMain(profile, "done");

        Assert.StartsWith("ok", main.Sup.Spawn("chop the three logs by the chest", "101", forPlayer: "poofimafox"));
        Assert.True(await Until(() => main.Sup.Slot(Clone)!.Connected), "hello over the memory link: " + main.Text());
        var hive = main.Launcher.Find(Clone)!;
        Assert.True(hive.InProcess);
        Assert.Equal(1, main.Sup.RunningInProcess());
        Assert.True(await Until(() => main.Server.LoggedIn), "its own login on the server");

        // Its brain took the task and reported; main's brain hears the result; done ends the outing through the logout gate.
        bool done = await Until(() => main.Text().Contains("[hive] 101 done: chopped 3 logs"));
        if (!done) output.WriteLine(main.Text() + Environment.NewLine + HiveLog());
        Assert.True(done, "result: " + main.Text());
        Assert.True(await Until(() => main.Sup.Slot(Clone)!.State == CloneState.Idle && hive.HasExited), "logged out: " + main.Text());
        Assert.True(main.Sup.Slot(Clone)!.ByeReceived);
        Assert.Equal(0, hive.ExitCode);
        Assert.DoesNotContain("lost", main.Text());
        Assert.Empty(main.Watchdog);

        // One version per process, and the hive played it: its log says so; its own log directory, separate from main's.
        var log = HiveLog();
        output.WriteLine(log.Length > 3000 ? log[^3000..] : log);
        Assert.Contains($"hive {Clone} starting in main's process", log);
        Assert.Contains($"{profile.Data.VersionId}", log);
        Assert.Contains("deposit before logout", log);
        Assert.Equal(0, main.Launcher.Transport.Listening);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task An_inproc_hive_hosts_its_own_bodies_over_the_memory_bus_and_a_release_logs_them_out_first(string version)
    {
        var profile = Profile(version);
        using var _ = Version(profile);
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
            Llm = (_, _) => new FakeBrain("idle"),
        };
        using var db = AgentDb.Open(Path.Combine(_dir, "mineclaw.db"));
        var sup = new HiveSupervisor(cfg, "maid_chan", launcher, new ClaimBoard(TimeSpan.FromSeconds(60)),
            new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, cfg.Budget, () => (0m, 0m)),
            new ChatArbiter("maid_chan", "maid_chan", cfg.Clones()), new HiveStore(db, "maid_chan"),
            () => new SlotFacts(20, ["maid_chan", "poofimafox", .. servers.Where(s => s.Value.LoggedIn).Select(s => s.Key)]), ["poofimafox"]);
        try
        {
            // ---- main's hierarchy: the allocator, the whitelist through the command gate's scoped exception, the bus
            var gate = new MineClaw.Agent.Chat.CommandGate(new MineClaw.Agent.Chat.CommandOptions(), () => ["poofimafox"]);
            var managed = new MineClaw.Hive.Hierarchy.ManagedAccounts(null);
            var whitelist = new MineClaw.Hive.Hierarchy.WhitelistManager(managed, cfg.Accounts, "maid_chan", gate.ArmManagedWhitelist, (line, _) =>
            {
                bool ok = gate.AllowOutgoing(line);
                if (ok) Log("sent " + line);
                return Task.FromResult(ok);
            }, NullLogger.Instance);
            gate.ManagedAccount = whitelist.Accepts;
            var notices = new List<string>();
            var h = new MineClaw.Hive.Hierarchy.HiveHierarchy(cfg.Accounts, "maid_chan", managed, whitelist, regulars: () => ["poofimafox"])
            {
                Bodies = new MineClaw.Hive.Hierarchy.SupervisorBodies(sup), ProfileCheck = _ => null,
                Notice = e => { lock (notices) notices.Add(e.Text); },
                RunsInProcess = (i, p) => cfg.RunsInProcess(i, p), InProcRunning = sup.RunningInProcess,
            };
            sup.Accounts = h;
            var bus = new MineClaw.Hive.Hierarchy.Bus.HiveBus("maid_chan") { MainHandler = h.HandleBusAsync };
            h.Bus = bus;
            sup.Bus = bus;
            SubhiveWiring.AttachMain(h, null, cfg, NullLogger.Instance);
            h.Resources = null; // the test machine's memory isn't the CT's

            // ---- "spawn a hive with 2 agents": ONE hive in this process whose brain drives 2 bodies of its own
            var spawned = h.Spawn(new MineClaw.Hive.Hierarchy.SpawnRequest(Kind: "hive", Brain: "opus", Task: "stand by", Bodies: 2));
            output.WriteLine(spawned);
            Assert.StartsWith("ok", spawned);
            var hiveNode = h.Tree.All().Single(n => n.Kind == MineClaw.Hive.Hierarchy.CloneKind.Hive);
            Assert.True(await Until(() => sup.Slot(hiveNode.Account)?.Connected == true), "the hive up");
            var started = launcher.Find(hiveNode.Account);
            if (started == null || started.HasExited) output.WriteLine(HiveLog(hiveNode.Account));
            Assert.True(started is { InProcess: true, HasExited: false }, "running in this process");
            List<string> Bodies() => h.Tree.All().Where(n => n.Kind == MineClaw.Hive.Hierarchy.CloneKind.Subhive).Select(n => n.Account).ToList();
            bool online = await Until(() => Bodies().Count == 2 && Bodies().All(b => servers.TryGetValue(b, out var s) && s.LoggedIn), 30000);
            if (!online) output.WriteLine(string.Join(" / ", log) + " || " + string.Join(" || ", notices) + Environment.NewLine + HiveLog(hiveNode.Account));
            Assert.True(online, "2 bodies in game: " + h.Tree.Render());
            Assert.All(Bodies(), b => Assert.Equal(hiveNode.Account, h.Tree.Get(b)!.Owner));
            lock (log) Assert.All(Bodies(), b => Assert.Contains($"sent /whitelist add {b}", log)); // main whitelisted each, over the memory bus
            Assert.Equal(1, sup.RunningInProcess()); // the bodies live in the hive, not as hives of their own

            // ---- release the hive: its bodies deposit and log out first, main takes each off the whitelist, then the hive goes
            var bodies = Bodies();
            var rel = h.Release(HiveNodeShort(hiveNode.Account));
            output.WriteLine(rel);
            Assert.True(await Until(() => h.Tree.Count == 0, 40000), "released: " + h.Tree.Render());
            Assert.True(await Until(() => bodies.All(b => { lock (log) return log.Contains($"sent /whitelist remove {b}"); })), "bodies off the whitelist");
            Assert.True(await Until(() => sup.Slot(hiveNode.Account)!.State == CloneState.Idle && launcher.Find(hiveNode.Account)!.HasExited), "the hive logged out");
            Assert.True(sup.Slot(hiveNode.Account)!.ByeReceived);
            var hiveLog = HiveLog(hiveNode.Account);
            Assert.All(bodies, b => Assert.Contains($"bodies: {b}: deposit before logout", hiveLog));
        }
        finally
        {
            try { await sup.RecallAllAsync("test over", TimeSpan.FromSeconds(10)); } catch (Exception) { }
            await sup.DisposeAsync();
            foreach (var s in servers.Values) s.Dispose();
        }
    }

    private static string HiveNodeShort(string account) => account[(account.LastIndexOf('_') + 1)..];

    private string HiveLog(string clone = Clone)
    {
        var dir = Path.Combine(_dir, "logs", "hive", clone);
        return Directory.Exists(dir) ? string.Join("\n", Directory.GetFiles(dir, "mineclaw-*.log").Select(f =>
        {
            using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new StreamReader(s).ReadToEnd();
        })) : "";
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task The_watchdog_stops_a_hung_hive_through_the_logout_gate_and_tells_main(string version)
    {
        var profile = Profile(version);
        using var _ = Version(profile);
        await using var main = await StartMain(profile, "hang", c => { c.InProc.HangSeconds = 1.5; c.InProc.CheckSeconds = 0.2; c.InProc.StopGraceSeconds = 30; });

        Assert.StartsWith("ok", main.Sup.Spawn("chop the three logs by the chest", "101", forPlayer: "poofimafox"));
        Assert.True(await Until(() => main.Sup.Slot(Clone)!.Connected), "up");
        var hive = main.Launcher.Find(Clone)!;
        Assert.True(await Until(() => { lock (main.Watchdog) return main.Watchdog.Count > 0; }), "the watchdog fired: " + main.Text());
        lock (main.Watchdog) Assert.Contains("hung: a brain turn", main.Watchdog[0]);
        Assert.True(await Until(() => hive.HasExited && main.Sup.Slot(Clone)!.State is CloneState.Idle or CloneState.Retired), "stopped: " + main.Text());
        Assert.True(main.Sup.Slot(Clone)!.ByeReceived, "a graceful stop: the logout gate ran (" + main.Text() + ")");
        Assert.Contains("watchdog: hung", HiveLog());
        Assert.False(hive.Abandoned);
    }

    [Fact]
    public async Task A_hive_that_wont_stop_is_killed_then_abandoned_and_main_lives_on()
    {
        var profile = GameVersionProfiles.Beta181;
        using var _ = Version(profile);
        await using var main = await StartMain(profile, "stuck", c =>
        {
            c.InProc.HangSeconds = 1; c.InProc.CheckSeconds = 0.2; c.InProc.StopGraceSeconds = 1; c.InProc.AbandonSeconds = 1;
        });
        Assert.StartsWith("ok", main.Sup.Spawn("chop the three logs by the chest", "101", forPlayer: "poofimafox"));
        var hive = await Until(() => main.Launcher.Find(Clone) != null) ? main.Launcher.Find(Clone)! : throw new Xunit.Sdk.XunitException("no hive");
        Assert.True(await Until(() => hive.HasExited, 30000), "exited: " + main.Text());
        Assert.NotNull(hive.KillReason);
        Assert.Contains("watchdog", hive.KillReason);
        Assert.True(await Until(() => main.Text().Contains("[hive] 101 lost"), 10000), "reported lost: " + main.Text());
        // Main's process (this test) is fine and the slot is usable again (retired after MaxCrashes=1 here, never a crash loop).
        Assert.True(main.Sup.Slot(Clone)!.State is CloneState.Idle or CloneState.Retired);
    }

    [Fact]
    public async Task One_hives_exception_is_its_own_crash_never_mains()
    {
        var profile = GameVersionProfiles.Beta181;
        using var _ = Version(profile);
        await using var main = await StartMain(profile, "done", llm: (_, _) => throw new InvalidOperationException("model profile blew up"));
        Assert.StartsWith("ok", main.Sup.Spawn("chop the three logs by the chest", "101", forPlayer: "poofimafox"));
        Assert.True(await Until(() => main.Text().Contains("[hive] 101 lost"), 20000), "lost: " + main.Text());
        var hive = main.Launcher.Find(Clone)!;
        Assert.True(await Until(() => hive.HasExited), "exited");
        Assert.Equal(1, hive.ExitCode);
        Assert.Contains("model profile blew up", HiveLog()); // the crash is in its own log; this process goes on
        Assert.True(await Until(() => main.Sup.Slot(Clone)!.State is CloneState.Idle or CloneState.Retired));
    }

    /// <summary>Records launches without running anything (the storm check needs no game).</summary>
    private sealed class CountingLauncher : IProcessLauncher
    {
        public int Starts;
        public ICloneProcess Start(string clone, string pipeName) { Interlocked.Increment(ref Starts); return new Never(); }
        private sealed class Never : ICloneProcess
        {
            private readonly TaskCompletionSource _gone = new();
            public int Id => 1;
            public bool HasExited => _gone.Task.IsCompleted;
            public int? ExitCode => HasExited ? 0 : null;
            public Task WaitForExitAsync(CancellationToken ct = default) => _gone.Task.WaitAsync(ct);
            public void Kill() => _gone.TrySetResult();
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task A_build_resumed_by_csharp_after_a_restart_never_logs_clones_in_only_a_model_call_does()
    {
        Directory.CreateDirectory(_dir);
        using var db = AgentDb.Open(Path.Combine(_dir, "mineclaw.db"));
        var cfg = new HiveConfig { CloneUsernames = [Clone, "maid_chan_102"], LaunchGapSeconds = 0 };
        var launcher = new CountingLauncher();
        await using var sup = new HiveSupervisor(cfg, "maid_chan", launcher, new ClaimBoard(TimeSpan.FromSeconds(60)),
            new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, cfg.Budget, () => (0m, 0m)),
            new ChatArbiter("maid_chan", "maid_chan", cfg.Clones()), new HiveStore(db, "maid_chan"),
            () => new SlotFacts(20, ["maid_chan", "poofimafox"]), ["poofimafox"]);
        sup.ScriptPrep = (_, code) => ("build_lane", null, code); // main's lint + save of the lane script
        var jobs = new HiveBuildJobs(sup, cfg.Clones());

        // C# resuming a stored build (main's standing order at startup): no caller → offline clones are not candidates, not started.
        Assert.Null(MineClaw.Agent.Tools.ToolCaller.Current);
        Assert.Empty(jobs.Candidates(["101", "102"]));
        Assert.Contains("isn't in game", jobs.StartLane("101", 1, 0));
        await Task.Delay(200);
        Assert.Equal(0, launcher.Starts);

        // The brain naming them now: they are asked (and logged in).
        using (MineClaw.Agent.Tools.ToolCaller.Push(ModelTier.Main, "claude-opus-5-5"))
        {
            Assert.Equal(2, jobs.Candidates(["101", "102"]).Count);
            Assert.Null(jobs.StartLane("101", 1, 0));
        }
        Assert.True(await Until(() => Volatile.Read(ref launcher.Starts) == 1, 5000), "one launch");
    }

    [Fact]
    public async Task A_hive_never_starts_on_another_version_than_the_process_plays()
    {
        using var _ = GameData.Override(GameVersionProfiles.Beta181.Data);
        await using var launcher = new InProcHiveLauncher(new HiveConfig(), _ => throw new InvalidOperationException("not reached"), GameVersionProfiles.Java1122,
            NullLoggerFactory.Instance);
        var ex = Assert.Throws<InvalidOperationException>(() => launcher.Start(Clone, "mem:x"));
        Assert.Contains("one version per process", ex.Message);
    }
}
