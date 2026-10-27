using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Db;
using MineClaw.Hive.Inventory;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;
using MineClaw.Hive.Testing;
using MineClaw.World;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 10-27 21:45-21:53: a failed task logged the clone out at once (1337 at 21:45:37, 420 at 21:53:12, 4 s after a
/// transient gen stall), and main's retry was then blocked as "420 already refused mining". Now a failure leaves the clone
/// online, idle and safe, for a grace period (main gets a wake), every logout puts the WHOLE inventory away first (staying
/// online if it truly can't), and only state-based refusals (hp/food/tools) block a retry.
/// </summary>
[Collection("processes")]
public class LiveSession5HiveLifecycleTests : IAsyncDisposable
{
    private sealed class FakeProcess(int id, Task<int> run, CancellationTokenSource kill) : ICloneProcess
    {
        public int Id => id;
        public bool HasExited => run.IsCompleted;
        public int? ExitCode => run.IsCompletedSuccessfully ? run.Result : run.IsCompleted ? 1 : null;
        public async Task WaitForExitAsync(CancellationToken ct = default) { try { await run.WaitAsync(ct); } catch (Exception) when (!ct.IsCancellationRequested) { } }
        public void Kill() { try { kill.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose() { }
    }

    private sealed class InProcLauncher(HiveConfig cfg, Func<FakePort> port) : IProcessLauncher
    {
        private int _ids = 6000;
        public readonly List<(string Clone, FakeBody Body, CloneHost Host)> Started = new();

        public ICloneProcess Start(string clone, string pipeName)
        {
            var kill = new CancellationTokenSource();
            var body = new FakeBody(clone, port());
            var host = new CloneHost(clone, pipeName, cfg, body);
            lock (Started) Started.Add((clone, body, host));
            return new FakeProcess(Interlocked.Increment(ref _ids), Task.Run(() => host.RunAsync(kill.Token)), kill);
        }

        public (string Clone, FakeBody Body, CloneHost Host) Last(string clone) { lock (Started) return Started.Last(s => s.Clone == clone); }
    }

    /// <summary>420's result at 21:53:08 (main log line 5614, as main saw it).</summary>
    private const string GenStalled = "Loadout got the stone pickaxe, but the cobble gen stalled: 4,64,6 turned to water after one dig and 5,64,7 gave " +
        "0 cobble from 8 digs. Nothing mined or deposited. Gen needs repair (lava/water layout)";
    private const string Cobble = "Mine about 50 cobblestone at the cobble generator near 4,64,6 and deposit it in the chest at 14,64,-5";

    private readonly string _dir;
    private readonly AgentDb _db;
    private readonly HiveConfig _cfg = TestHive.Config();
    private InProcLauncher _launcher = null!;
    private HiveSupervisor _sup = null!;
    private readonly List<string> _notices = new();

    public LiveSession5HiveLifecycleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mineclaw-hive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = AgentDb.Open(Path.Combine(_dir, "mineclaw.db"));
        _cfg.DepositTimeoutSeconds = 2;
    }

    private void Start(double graceSeconds, Func<FakePort>? port = null)
    {
        _cfg.FailureGraceSeconds = graceSeconds;
        _launcher = new InProcLauncher(_cfg, port ?? (() => new FakePort(stacks: 2, chestSlots: 27)));
        var ledger = new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, _cfg.Budget, () => (0m, 0m));
        _sup = new HiveSupervisor(_cfg, "maid_chan", _launcher, new ClaimBoard(TimeSpan.FromSeconds(60)), ledger,
            new ChatArbiter("maid_chan", "maid_chan", _cfg.Clones()), new HiveStore(_db, "maid_chan"),
            () => new SlotFacts(20, ["maid_chan", "PoofimaFox"]), ["PoofimaFox"]);
        _sup.Notice += e => { lock (_notices) _notices.Add(e.Text); };
    }

    private string Notices() { lock (_notices) return string.Join(" || ", _notices); }

    public async ValueTask DisposeAsync()
    {
        if (_sup != null)
        {
            try { await _sup.RecallAllAsync("test over", TimeSpan.FromSeconds(10)); } catch (Exception) { }
            await _sup.DisposeAsync();
        }
        _db.Dispose();
    }

    [Fact]
    public async Task A_failed_task_leaves_the_clone_online_awaiting_orders_and_a_new_assignment_runs()
    {
        Start(graceSeconds: 60);
        Assert.StartsWith("ok", _sup.Spawn(Cobble, "420", forPlayer: "PoofimaFox"));
        await Wait.Until(() => _sup.Slot("maid_chan_420")!.Connected, 20, "420 up");
        var (_, body, host) = _launcher.Last("maid_chan_420");

        var r = await host.ReportAsync("failed", GenStalled);
        Assert.StartsWith("ok: reported to main; you stay online", r);
        await Wait.Until(() => Notices().Contains("[hive] 420 failed: Loadout got the stone pickaxe"), 10, "main woken");
        Assert.Contains("still online, awaiting orders", Notices());
        await Task.Delay(500);
        Assert.False(host.LoggingOut);
        Assert.False(body.Disconnected.Task.IsCompleted);

        // 21:53:36: main's retry on another cell is NOT blocked as a refusal (a transient world condition, not 420's state).
        var again = _sup.Spawn("Mine cobble ONLY at gen cell 6,64,8 with harvest_generator at=\"6,64,8\", then deposit", "420", forPlayer: "PoofimaFox");
        Assert.StartsWith("ok: 420 got the new task", again);
        await Wait.Until(() => { lock (body.Calls) return body.Calls.Any(c => c.Contains("6,64,8", StringComparison.Ordinal)); }, 10, "new task runs");
        Assert.False(host.LoggingOut);
    }

    [Fact]
    public async Task With_no_new_orders_the_grace_period_ends_in_a_deposit_then_the_logout()
    {
        Start(graceSeconds: 1);
        Assert.StartsWith("ok", _sup.Spawn(Cobble, "1337", forPlayer: "PoofimaFox"));
        await Wait.Until(() => _sup.Slot("maid_chan_1337")!.Connected, 20, "1337 up");
        var (_, body, host) = _launcher.Last("maid_chan_1337");
        await host.ReportAsync("failed", "harvest_generator failed 2x the same way: generator: dug 32/50 at 4,64,6 (have 25 cobblestone); " +
            "4,64,6 isn't regenerating cobble for me (yield 78 %: 25 cobblestone from 32 digs), need the right cell");
        await body.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("task failed (no new orders)", host.LogoutReason);
        Assert.True(host.LastDeposit!.Empty, host.LastDeposit.Text);
        Assert.Equal(0, body.ItemsAtDisconnect); // everything put away before the logout
    }

    [Fact]
    public async Task A_clone_that_cannot_put_its_items_away_stays_online_and_asks_main_instead_of_logging_out_with_them()
    {
        Start(graceSeconds: 0, port: () => new FakePort(stacks: 3)); // no chest at all, main can't take items, no chest item
        Assert.StartsWith("ok", _sup.Spawn(Cobble, "1337", forPlayer: "PoofimaFox"));
        await Wait.Until(() => _sup.Slot("maid_chan_1337")!.Connected, 20, "1337 up");
        var (_, body, host) = _launcher.Last("maid_chan_1337");

        await host.ReportAsync("failed", "no free cell");
        await Wait.Until(() => Notices().Contains("[hive] 1337 needs input"), 15, "main asked");
        Assert.Contains("couldn't put away 48 item(s)", Notices());
        Assert.False(body.Disconnected.Task.IsCompleted);
        Assert.False(host.LoggingOut); // a later recall can retry the deposit
        Assert.Equal(3, body.Port.Items().Count);
    }

    [Fact]
    public void Deposit_ignores_low_hp_but_never_runs_over_the_void()
    {
        // GameInventoryPort.CanActSafely had "hp > 6": 67 at hp 7 / 1337 at low hp waited out the deposit deadline. The rule
        // lives in one place; the procedure keeps trying whenever the port says it can act.
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MineClaw.Hive", "Inventory", "GameInventoryPort.cs"));
        var body = src[src.IndexOf("public bool CanActSafely", StringComparison.Ordinal)..];
        body = body[..body.IndexOf('}', body.IndexOf("return", StringComparison.Ordinal))];
        Assert.DoesNotContain("Health", body);
        Assert.Contains("IsPlayerOverVoid", body);
    }

    [Fact]
    public void Only_state_based_failures_block_a_retry_and_a_player_reasking_clears_them()
    {
        Assert.Equal(AssignFailureMemory.FailureKind.Transient, AssignFailureMemory.Classify(GenStalled));
        Assert.Equal(AssignFailureMemory.FailureKind.Transient, AssignFailureMemory.Classify("4,64,6 isn't regenerating cobble for me, need the right cell"));
        Assert.Equal(AssignFailureMemory.FailureKind.Transient, AssignFailureMemory.Classify("6,64,8 unreachable from stand 3,64,4; another miner is on 5,64,7"));
        Assert.Equal(AssignFailureMemory.FailureKind.State, AssignFailureMemory.Classify("can't fight: hp 7, food 0, no food anywhere"));
        Assert.Equal(AssignFailureMemory.FailureKind.State, AssignFailureMemory.Classify("no pickaxe in my inventory or storage"));

        var m = new AssignFailureMemory();
        var v = new CloneVitals(20, 20, [], null, null, null, 0, DateTimeOffset.UtcNow);
        m.Record("maid_chan_420", TaskIntent.Mine, GenStalled, v, false);
        Assert.Null(m.Blocking("maid_chan_420", TaskIntent.Mine, v, false));

        var weak = v with { Hp = 7, Food = 0 };
        m.Record("maid_chan_67", TaskIntent.Fight, "can't fight: hp 7, food 0, no food anywhere", weak, false);
        Assert.NotNull(m.Blocking("maid_chan_67", TaskIntent.Fight, weak, false));
        Assert.Equal(0, m.OnPlayerChat("she can try"));                                   // no new information, no name
        Assert.Equal(1, m.OnPlayerChat("have 67 fight the mobs", n => n == "maid_chan_67")); // re-asked by name
        Assert.Null(m.Blocking("maid_chan_67", TaskIntent.Fight, weak, false));
    }

    [Fact] // item 17 (a): 21:57:44 "can you get 420 to harvest the trees properly?" was classified as Mine
    public void Intents_are_specific_a_tree_job_is_not_mining_and_a_gen_failure_never_blocks_it()
    {
        const string trees = "Harvest the trees at the tree farm (9,65,0 / 13,65,0) with a stone axe: chop every log, replant saplings, deposit logs in chest 14,64,-5";
        const string gen = "Mine cobblestone ONLY at gen cell 4,64,6 (next to lava). Use harvest_generator with at=\"4,64,6\", count 40, with a stone pickaxe";
        Assert.Equal(TaskIntent.Chop, CloneFitness.IntentOf(trees));
        Assert.Equal(TaskIntent.Mine, CloneFitness.IntentOf(gen));
        Assert.Equal(TaskIntent.Farm, CloneFitness.IntentOf("harvest the wheat and replant seeds"));
        Assert.Equal(TaskIntent.Deliver, CloneFitness.IntentOf("bring Fox 5 bread"));
        Assert.Equal(TaskIntent.Fight, CloneFitness.IntentOf("kill the mobs in the mob chamber"));

        var m = new AssignFailureMemory();
        var v = new CloneVitals(20, 20, [], null, null, null, 0, DateTimeOffset.UtcNow);
        m.Record("maid_chan_420", TaskIntent.Mine, "no pickaxe in my inventory or storage", v, false); // even a state refusal
        Assert.NotNull(m.Blocking("maid_chan_420", TaskIntent.Mine, v, false));
        Assert.Null(m.Blocking("maid_chan_420", CloneFitness.IntentOf(trees), v, false));
    }

    [Fact] // item 17 (b): "picked maid_chan_67 for Mine (69 hp 20 food 20 REFUSED; 1337 hp 20 food 20 REFUSED; 67 hp 7)"
    public void Transient_gen_failures_never_mark_healthy_clones_refused_but_67s_hp_refusal_stands()
    {
        Start(graceSeconds: 60);
        var healthy = new CloneVitals(20, 20, null, null, null, null);
        _sup.Refusals.Record("maid_chan_69", TaskIntent.Mine, GenStalled, healthy, false);
        _sup.Refusals.Record("maid_chan_1337", TaskIntent.Mine, "harvest_generator failed 2x the same way: generator: dug 32/50 at 4,64,6 (have 25 " +
            "cobblestone); 4,64,6 isn't regenerating cobble for me (yield 78 %: 25 cobblestone from 32 digs), need the right cell", healthy, false);
        // 21:44:43, 67 at hp 7: a state-based refusal (it says "stalled" too, but its own hp/food is the reason).
        const string hurt = "Cobble run stalled: HP 7/20 with no food, tools refuse mining below HP 10. Mined 1 cobble, deposited 0. Need food or HP regen before retrying.";
        Assert.Equal(AssignFailureMemory.FailureKind.State, AssignFailureMemory.Classify(hurt));
        var weak = new CloneVitals(7, 10, null, null, null, null);
        _sup.Refusals.Record("maid_chan_67", TaskIntent.Mine, hurt, weak, false);

        Assert.Null(_sup.Refusals.Blocking("maid_chan_69", TaskIntent.Mine, healthy, false));
        Assert.Null(_sup.Refusals.Blocking("maid_chan_1337", TaskIntent.Mine, healthy, false));
        Assert.NotNull(_sup.Refusals.Blocking("maid_chan_67", TaskIntent.Mine, weak, false));
        Assert.Equal(1, _sup.Refusals.Count);
    }

    [Fact] // item 17 (c): a player's direct request for a specific clone always overrides the memory
    public void A_players_direct_request_for_a_clone_clears_its_refusals()
    {
        Start(graceSeconds: 60);
        var v = new CloneVitals(20, 20, [], null, null, null);
        _sup.Refusals.Record("maid_chan_420", TaskIntent.Chop, "no axe in my inventory or storage", v, false);
        Assert.NotNull(_sup.Refusals.Blocking("maid_chan_420", TaskIntent.Chop, v, false));
        _sup.NotePlayerChat("PoofimaFox", "can you get 420 to harvest the trees properly?");
        Assert.Null(_sup.Refusals.Blocking("maid_chan_420", TaskIntent.Chop, v, false));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
