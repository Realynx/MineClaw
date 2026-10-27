using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Db;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;
using MineClaw.Hive.Testing;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 10-27 07:49/07:51 and 21:30: fresh clone maid_chan_420 spawned at -5.5,0.62,-8.5 (b1.8.1 EntityPlayer.java:36-42 puts a
/// new player in a random column ±10 around the world spawn at that column's top block, y≈0 in a skyblock void). The client
/// held still, but the brain tried to walk back, reported failed and logged out, and main logged it in there again. Now: one
/// /tp ask to the trusted players, main is told (relays it), the task waits for the landing, and the saved void spot gates the
/// next login until a player confirms.
/// </summary>
[Collection("processes")]
public class LiveSession5VoidSpawnTests : IAsyncDisposable
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

    private sealed class InProcLauncher(HiveConfig cfg) : IProcessLauncher
    {
        private int _ids = 5000;
        public readonly List<(string Clone, FakeBody Body, CloneHost Host)> Started = new();

        public ICloneProcess Start(string clone, string pipeName)
        {
            var kill = new CancellationTokenSource();
            var body = new FakeBody(clone, new FakePort()) { HoldingStill = true }; // just logged in: the client holds still
            var host = new CloneHost(clone, pipeName, cfg, body)
            {
                TrustedPlayers = () => ["poofimafox", "kiwi"],
                VoidHoldTimeout = TimeSpan.FromMinutes(5),
            };
            lock (Started) Started.Add((clone, body, host));
            return new FakeProcess(Interlocked.Increment(ref _ids), Task.Run(() => host.RunAsync(kill.Token)), kill);
        }

        public (string Clone, FakeBody Body, CloneHost Host)? Last(string clone) { lock (Started) return Started.LastOrDefault(s => s.Clone == clone); }
        public int Count(string clone) { lock (Started) return Started.Count(s => s.Clone == clone); }
    }

    private const string Clone = "maid_chan_420";
    private const string Sweep = "Every morning clear out the leftover mobs and put their drops in the chest by the house";

    private readonly string _dir;
    private readonly AgentDb _db;
    private readonly HiveStore _store;
    private readonly HiveConfig _cfg = TestHive.Config();
    private readonly InProcLauncher _launcher;
    private readonly HiveSupervisor _sup;
    private readonly List<string> _notices = new();

    public LiveSession5VoidSpawnTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mineclaw-hive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = AgentDb.Open(Path.Combine(_dir, "mineclaw.db"));
        _store = new HiveStore(_db, "maid_chan");
        _cfg.DepositTimeoutSeconds = 5;
        _launcher = new InProcLauncher(_cfg);
        var ledger = new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, _cfg.Budget, () => (0m, 0m));
        _sup = new HiveSupervisor(_cfg, "maid_chan", _launcher, new ClaimBoard(TimeSpan.FromSeconds(60)), ledger,
            new ChatArbiter("maid_chan", "maid_chan", _cfg.Clones()), _store, () => new SlotFacts(20, ["maid_chan", "PoofimaFox"]), ["PoofimaFox"]);
        _sup.Notice += e => { lock (_notices) _notices.Add(e.Text); };
    }

    private string Notices() { lock (_notices) return string.Join(" || ", _notices); }

    public async ValueTask DisposeAsync()
    {
        try { await _sup.RecallAllAsync("test over", TimeSpan.FromSeconds(10)); } catch (Exception) { }
        try { await _sup.DisposeAsync(); } catch (Exception) { } // a test may have disposed it already (main restart)
        _db.Dispose();
    }

    [Fact]
    public async Task A_clone_holding_over_the_void_asks_once_tells_main_waits_and_resumes_after_the_tp()
    {
        Assert.StartsWith("ok", _sup.Spawn(Sweep, "420", forPlayer: "PoofimaFox"));
        await Wait.Until(() => _sup.Slot(Clone)!.Connected, 20, "420 up");
        var (_, body, host) = _launcher.Last(Clone)!.Value;

        // 21:30:06 "teleported to -5.5,0.6,-8.5 with only void below; holding still" → the client's void-hold event.
        await host.OnVoidHoldAsync(-5.5, 0.594, -8.5);
        await host.OnVoidHoldAsync(-5.5, 0.594, -8.5); // the same hold again (a second 0x0D over the void): no second ask
        lock (body.Said)
        {
            var ask = Assert.Single(body.Said);
            Assert.Equal("poofimafox, kiwi: I spawned over the void at -6,0,-9, can someone /tp maid_chan_420 to me or to maid_chan?", ask);
        }
        await Wait.Until(() => Notices().Contains("[hive] 420 spawned over the void at -6,0,-9"), 10, "main told");
        Assert.Contains("/tp maid_chan_420 maid_chan", Notices());
        Assert.NotNull(_store.VoidSpawn(Clone)); // the saved login spot is marked for the next spawn

        // The task waits for the landing: no walk back from y 0, and "failed" doesn't end it or log out.
        await Task.Delay(300);
        lock (body.Calls) Assert.DoesNotContain(body.Calls, c => c.StartsWith("task ", StringComparison.Ordinal));
        var r = await host.ReportAsync("failed", "spawned over the void, no path up to the island");
        Assert.StartsWith("not ended", r);
        Assert.False(host.LoggingOut);

        // Fox: "/tp maid_chan_420 maid_chan" → the client lands on verified ground → the hold ends.
        body.HoldingStill = false;
        await host.OnVoidHoldEndedAsync(8.5, 64, -5.5);
        await Wait.Until(() => { lock (body.Calls) return body.Calls.Any(c => c.StartsWith("task " + Sweep, StringComparison.Ordinal)); }, 10, "task resumed");
        await Wait.Until(() => _store.VoidSpawn(Clone) == null, 10, "void mark cleared");
        await Wait.Until(() => Notices().Contains("landed on ground"), 10, "main told about the landing");
        lock (body.Said) Assert.Single(body.Said);

        // Item 10: 420's first login showed where fresh accounts land. 1337 never joined: the status says so and a login waits
        // for a player's confirmation ("nah we're good, ... all idle, nobody's stuck" was wrong).
        var list = _sup.List();
        Assert.Contains("1337 never joined; first login lands at void spawn -6,0,-9 and needs a /tp", list);
        Assert.DoesNotContain("1337 idle", list);
        var no = _sup.Spawn("help chop", "1337", forPlayer: "PoofimaFox");
        Assert.StartsWith("no: 1337 never joined: its first login lands at the void spawn -6,0,-9", no);
        Assert.Equal(0, _launcher.Count("maid_chan_1337"));
    }

    [Fact]
    public async Task Join_history_is_persisted_and_idle_means_logged_out_at_a_known_safe_spot()
    {
        _store.NoteJoined("maid_chan_67");
        _store.SaveGoodPosition("maid_chan_67", 9.5, 65, 0.5, 0);
        _store.SaveVitals("maid_chan_420", 20, 20, -5.5, 0.62, -8.5, 0); // 07:49: the only thing saved was the void spot
        _store.NoteFreshVoidSpawn("maid_chan_420", -5.5, 0.62, -8.5);

        // A main restart: a new supervisor over the same db file.
        await _sup.DisposeAsync();
        using var db2 = AgentDb.Open(Path.Combine(_dir, "mineclaw.db"));
        var store2 = new HiveStore(db2, "maid_chan");
        var ledger = new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, _cfg.Budget, () => (0m, 0m));
        await using var sup2 = new HiveSupervisor(_cfg, "maid_chan", new InProcLauncher(_cfg), new ClaimBoard(TimeSpan.FromSeconds(60)), ledger,
            new ChatArbiter("maid_chan", "maid_chan", _cfg.Clones()), store2, () => new SlotFacts(20, ["maid_chan", "PoofimaFox"]), ["PoofimaFox"]);

        var h67 = store2.History("maid_chan_67");
        Assert.True(h67.Joined);
        Assert.Equal((9.5, 65.0, 0.5, 0), (h67.Good!.Value.X, h67.Good.Value.Y, h67.Good.Value.Z, h67.Good.Value.Dimension));
        Assert.False(store2.History("maid_chan_1337").Joined);

        var list = sup2.List();
        Assert.Contains("67 idle (logged out safe at 9,65,0)", list);
        Assert.Contains("420 logged out over the void at -6,0,-9; next login lands there and needs a /tp", list);
        Assert.Contains("69 never joined; first login lands at void spawn -6,0,-9 and needs a /tp", list);
        Assert.Contains("1337 never joined; first login lands at void spawn -6,0,-9 and needs a /tp", list);
    }

    [Fact]
    public void Main_never_logs_a_clone_in_over_its_saved_void_spot_again_before_a_player_confirms()
    {
        // 07:49:41 420 logged out over the void; 07:51:54 main logged it straight back in at the same spot.
        _store.MarkVoidSpawn(Clone, -5.5, 0.62, -8.5);

        var no = _sup.Spawn("spar with 69", "420", forPlayer: "PoofimaFox");
        Assert.StartsWith("no:", no);
        Assert.Contains("over the void at -6,0,-9", no);
        Assert.Contains("/tp maid_chan_420 maid_chan", no);
        Assert.Equal(0, _launcher.Count(Clone));

        // Unnamed: another free clone takes it, never 420.
        Assert.StartsWith("ok", _sup.Spawn("chop the trees", forPlayer: "PoofimaFox"));
        Assert.Equal(0, _launcher.Count(Clone));

        // A non-trusted player's line changes nothing; a trusted player saying they'll /tp it lets the next login go ahead.
        _sup.NotePlayerChat("randomguy", "just tp 420 lol");
        Assert.StartsWith("no:", _sup.Spawn("spar with 69", "420", forPlayer: "PoofimaFox"));
        _sup.NotePlayerChat("PoofimaFox", "log 420 in, I'll tp it to you");
        Assert.StartsWith("ok", _sup.Spawn("spar with 69", "420", forPlayer: "PoofimaFox"));
        Assert.Equal(1, _launcher.Count(Clone));
    }

    [Fact]
    public async Task A_clone_that_logs_in_onto_ground_clears_an_old_void_mark()
    {
        _store.MarkVoidSpawn(Clone, -5.5, 0.62, -8.5);
        Assert.StartsWith("ok", _sup.Spawn(Sweep, "420", forPlayer: "PoofimaFox", options: new HiveSpawnOptions { ConfirmVoid = true }));
        await Wait.Until(() => _sup.Slot(Clone)!.Connected, 20, "420 up");
        var (_, body, host) = _launcher.Last(Clone)!.Value;
        host.Store = _store;
        body.HoldingStill = false; // someone built under the spot: ground verified right after login
        await Wait.Until(() => { lock (body.Calls) return body.Calls.Any(c => c.StartsWith("task ", StringComparison.Ordinal)); }, 10, "task started");
        await Wait.Until(() => _store.VoidSpawn(Clone) == null, 10, "void mark cleared");
    }
}
