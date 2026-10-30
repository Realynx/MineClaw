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
/// Fox 10-30: "consolidate everything into the same process and use threads instead" (25 clone processes of ~200 MB each thrashed
/// the 4 GB CT). The link between main and a hive in main's process is a pair of in-memory streams carrying the same frames as the
/// pipe, so the supervisor and <see cref="CloneHost"/> run the whole protocol unchanged: hello/welcome, task, result, recall,
/// deposit, bye, heartbeats, the bus token.
/// </summary>
[Collection("processes")]
public class InProcTransportTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-hive-tests", Guid.NewGuid().ToString("N"));
    private AgentDb? _db;
    private HiveSupervisor? _sup;

    public async ValueTask DisposeAsync()
    {
        if (_sup != null)
        {
            try { await _sup.RecallAllAsync("test over", TimeSpan.FromSeconds(10)); } catch (Exception) { }
            await _sup.DisposeAsync();
        }
        _db?.Dispose();
    }

    [Fact]
    public async Task A_memory_duplex_carries_bytes_both_ways_and_a_dispose_is_the_end_of_stream_on_the_other_side()
    {
        var (a, b) = MemoryDuplexStream.CreatePair();
        a.Write([1, 2, 3]);
        await b.WriteAsync(new byte[] { 9 });
        var buf = new byte[8];
        Assert.Equal(3, b.Read(buf, 0, 8));
        Assert.Equal(new byte[] { 1, 2, 3 }, buf[..3]);
        Assert.Equal(1, await a.ReadAsync(buf.AsMemory(0, 8)));
        Assert.Equal(9, buf[0]);

        // A blocked reader (the link's dedicated thread) wakes up with 0 when the other end goes.
        var reader = Task.Run(() => b.Read(buf, 0, 8));
        await Task.Delay(50);
        Assert.False(reader.IsCompleted);
        a.Dispose();
        Assert.Equal(0, await reader.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Throws<IOException>(() => b.Write([1]));
    }

    [Fact]
    public async Task Two_hive_links_over_memory_answer_requests_beat_and_close_together()
    {
        var (a, b) = MemoryDuplexStream.CreatePair();
        await using var main = new HiveLink(a, "main");
        await using var clone = new HiveLink(b, "clone") { Handler = (m, _) => Task.FromResult<object?>(m.T == "echo" ? m.As<string>() + "!" : null) };
        string? closed = null;
        clone.Closed += r => closed = r;
        main.Start();
        clone.Start();
        main.StartHeartbeat(TimeSpan.FromMilliseconds(50));
        clone.StartHeartbeat(TimeSpan.FromMilliseconds(50));

        var reply = await main.RequestAsync<string>("echo", "hi", TimeSpan.FromSeconds(5));
        Assert.Equal("hi!", reply);
        Assert.True(await main.ProbeAsync(TimeSpan.FromSeconds(5)));
        await Wait.Until(() => clone.LastReceived > DateTimeOffset.UtcNow.AddSeconds(-1) && main.HeartbeatsSent > 2, 5, "heartbeats");

        await main.DisposeAsync();
        await Wait.Until(() => closed != null, 5, "the clone's side closed");
        Assert.False(clone.IsOpen);
    }

    [Fact]
    public async Task The_memory_transport_meets_only_at_a_listened_address_and_a_failed_launch_ends_the_clone_side()
    {
        var t = new MemoryTransport();
        Assert.Null(await t.ConnectAsync("mem:nobody:x", TimeSpan.FromMilliseconds(100), CancellationToken.None));

        var addr = t.NewAddress("maid_chan_101");
        Assert.StartsWith("mem:maid_chan_101:", addr);
        Assert.NotEqual(addr, t.NewAddress("maid_chan_101")); // unique per launch
        var ep = t.Listen(addr);
        var accept = ep.AcceptAsync(CancellationToken.None);
        var clientSide = await t.ConnectAsync(addr, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(clientSide);
        var mainSide = await accept.WaitAsync(TimeSpan.FromSeconds(1));
        clientSide!.Write([7]);
        var one = new byte[1];
        Assert.Equal(1, mainSide.Read(one, 0, 1));
        Assert.Equal(0, t.Listening);
        Assert.Null(await t.ConnectAsync(addr, TimeSpan.FromMilliseconds(50), CancellationToken.None)); // one connection per launch

        // Main gives up on a launch (timeout, exit before connecting): an offered stream that wasn't taken is closed.
        var addr2 = t.NewAddress("maid_chan_102");
        var ep2 = t.Listen(addr2);
        var late = await t.ConnectAsync(addr2, TimeSpan.FromSeconds(1), CancellationToken.None);
        ep2.Dispose();
        Assert.Equal(0, late!.Read(one, 0, 1));
        // A taken stream belongs to the link (the endpoint is never disposed after a successful accept).
        clientSide.Write([8]);
        Assert.Equal(1, mainSide.Read(one, 0, 1));
    }

    /// <summary>A hive "process" that is a <see cref="CloneHost"/> task in this process, linked over main's memory transport.</summary>
    private sealed class InProcLauncher(HiveConfig cfg) : IProcessLauncher
    {
        public readonly MemoryTransport Memory = new();
        public readonly List<(string Clone, FakeBody Body, CloneHost Host)> Started = new();
        private int _ids;

        public IHiveTransport TransportFor(string clone) => Memory;

        public ICloneProcess Start(string clone, string pipeName)
        {
            Assert.StartsWith("mem:", pipeName); // never a named pipe
            var body = new FakeBody(clone, new FakePort(stacks: 2, chestSlots: 27));
            body.OnTask = async t => { if (t.Text.Contains("done")) await Started.Single(s => s.Clone == clone).Host.ReportAsync("done", "did it"); };
            var host = new CloneHost(clone, pipeName, cfg, body, transport: Memory);
            lock (Started) Started.Add((clone, body, host));
            var kill = new CancellationTokenSource();
            return new Hive(Interlocked.Increment(ref _ids), Task.Run(() => host.RunAsync(kill.Token)), kill);
        }

        private sealed class Hive(int id, Task<int> run, CancellationTokenSource kill) : ICloneProcess
        {
            public int Id => id;
            public bool InProcess => true;
            public bool HasExited => run.IsCompleted;
            public int? ExitCode => run.IsCompletedSuccessfully ? run.Result : run.IsCompleted ? 1 : null;
            public async Task WaitForExitAsync(CancellationToken ct = default) { try { await run.WaitAsync(ct); } catch (Exception) when (!ct.IsCancellationRequested) { } }
            public void Kill() { try { kill.Cancel(); } catch (ObjectDisposedException) { } }
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task A_hive_in_mains_process_runs_the_whole_supervised_lifecycle_over_the_memory_link()
    {
        Directory.CreateDirectory(_dir);
        _db = AgentDb.Open(Path.Combine(_dir, "mineclaw.db"));
        var cfg = TestHive.Config();
        cfg.DepositTimeoutSeconds = 2;
        cfg.FailureGraceSeconds = 0;
        var launcher = new InProcLauncher(cfg);
        var notices = new List<string>();
        var ledger = new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, cfg.Budget, () => (0m, 0m));
        _sup = new HiveSupervisor(cfg, "maid_chan", launcher, new ClaimBoard(TimeSpan.FromSeconds(60)), ledger,
            new ChatArbiter("maid_chan", "maid_chan", cfg.Clones()), new HiveStore(_db, "maid_chan"),
            () => new SlotFacts(20, ["maid_chan", "PoofimaFox"]), ["PoofimaFox"]);
        _sup.Notice += e => { lock (notices) notices.Add(e.Text); };
        string Notices() { lock (notices) return string.Join(" || ", notices); }

        Assert.StartsWith("ok", _sup.Spawn("wait here", "420", forPlayer: "PoofimaFox"));
        await Wait.Until(() => _sup.Slot("maid_chan_420")!.Connected, 20, "420 up");
        Assert.Equal(1, _sup.RunningInProcess());
        var (_, body, host) = launcher.Started.Single();
        await Wait.Until(() => _sup.Slot("maid_chan_420")!.Snapshot?.Task == "fake", 10, "snapshots stream over memory");
        Assert.True(host.LastStreamed > DateTimeOffset.UtcNow.AddSeconds(-5)); // the in-process watchdog's "its loops run"

        // A new task over the link, the result back to main's brain.
        Assert.StartsWith("ok", _sup.Spawn("get it done", "420", forPlayer: "PoofimaFox"));
        await Wait.Until(() => Notices().Contains("[hive] 420 done: did it"), 15, "the result reaches main");

        // Done ends the outing (or a release does): the logout gate (deposit everything first), the bye, the link closes.
        if (!host.LoggingOut) _sup.Recall("420", "released");
        await body.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, body.ItemsAtDisconnect);
        await Wait.Until(() => _sup.Slot("maid_chan_420")!.State == CloneState.Idle, 15, "idle");
        Assert.True(_sup.Slot("maid_chan_420")!.ByeReceived);
        Assert.Equal(0, _sup.RunningInProcess());
        Assert.Equal(0, launcher.Memory.Listening);
        Assert.DoesNotContain("lost", Notices());
    }

    [Fact]
    public void Isolation_is_inproc_by_default_process_on_request_per_profile_and_per_spawn()
    {
        var cfg = new HiveConfig();
        Assert.True(cfg.RunsInProcess());
        Assert.True(cfg.RunsInProcess(profile: "deepseek"));
        cfg.ProcessProfiles = ["deepseek"];
        Assert.False(cfg.RunsInProcess(profile: "deepseek"));
        Assert.True(cfg.RunsInProcess(profile: "opus"));
        Assert.True(cfg.RunsInProcess("inproc", "deepseek")); // a spawn's own override wins
        cfg.Isolation = "process";
        Assert.False(cfg.RunsInProcess());
        Assert.True(cfg.RunsInProcess(isolation: "inproc"));
        Assert.False(cfg.RunsInProcess(isolation: "process"));
    }

    [Fact]
    public void The_ceiling_prices_a_hive_in_mains_process_as_a_brain_and_a_client_and_keeps_the_managed_heap()
    {
        var cfg = new HiveResourceConfig { LimitMb = 4096, MainProcessMb = 650, ReserveMb = 400, HiveProcessMb = 380, InProcHiveMb = 140, BodyMb = 40 };
        var use = new ResourceUse(HiveProcesses: 4, Bodies: 0, InProcHives: 4);
        var c = new ResourceCeiling(cfg, () => use, () => new MemoryProbe(4096, 100, "test"));
        Assert.Equal(650 + 4 * 140, c.Snapshot().Used);
        // 4096 - 400 - 1210 = 2486 free: 17 more in-process hives fit, only 6 more hive processes would.
        Assert.True(c.CanAdd(17, 0, inProcess: true).Ok);
        Assert.False(c.CanAdd(18, 0, inProcess: true).Ok);
        Assert.True(c.CanAdd(6, 0).Ok);
        var no = c.CanAdd(7, 0);
        Assert.False(no.Ok);
        Assert.Contains("a hive process", no.Why);
        // Mixed: two of the four are processes.
        use = new ResourceUse(4, 0, 2);
        Assert.Equal(650 + 2 * 380 + 2 * 140, c.Snapshot().Used);

        // Main's heap (GCHeapHardLimit) is shared by every in-process hive: 700 MB limit, 300 used, 512 kept → refused for a hive.
        var heap = new ResourceCeiling(cfg, () => new ResourceUse(0, 0), () => new MemoryProbe(16384, 100, "test"))
        {
            Heap = () => new MemoryProbe(700, 300, "managed heap"),
        };
        var refused = heap.CanAdd(1, 0, inProcess: true);
        Assert.False(refused.Ok);
        Assert.Contains("managed heap", refused.Why);
        Assert.True(heap.CanAdd(1, 0).Ok);           // a process of its own has its own heap
        Assert.Equal(0, heap.BodiesThatFit(1, inProcess: true));
    }
}
