using MineClaw.Agent.Telemetry;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Supervisor;
using MineClaw.Hive.Testing;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Hive.Tests;

/// <summary>Real child processes (the CloneStub exe: real CloneHost + IPC + shared SQLite, fake game and LLM).</summary>
[Collection("processes")]
public class HiveProcessTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Kill9_mid_db_write_keeps_db_intact_main_unaffected_claims_released_inventory_stranded()
    {
        await using var h = new TestHive(mode: "dbwrite");
        Assert.StartsWith("ok", h.Supervisor.Spawn("write lots", "67"));
        var slot = h.Supervisor.Slot("maid_chan_67")!;
        await Wait.Until(() => h.Board.CountOf("maid_chan_67") == 1, what: "the clone's claim");
        await Wait.Until(() => slot.Inventory is { Count: 2 }, what: "inventory snapshot");
        await Wait.Until(() => h.Db.ScalarLong("SELECT count(*) FROM chat_log WHERE player = 'maid_chan_67'") > 2000, 60, "db writes");
        Assert.True(h.Db.ScalarLong("SELECT count(*) FROM chat_log WHERE clone_id = 'maid_chan_67'") > 0); // rows tagged with the writer

        slot.Process!.Kill(); // kill -9 in the middle of its write loop
        await Wait.Until(() => slot.State == CloneState.Backoff, what: "crash handling");

        Assert.Equal("ok", h.Db.Scalar("PRAGMA integrity_check"));
        Assert.Equal(0, h.Board.CountOf("maid_chan_67"));                       // claims released at once
        Assert.True(h.Board.TryClaim("maid_chan_69", ClaimKey.Block(5, 64, 5)).Ok);
        var stranded = h.Store.Stranded("maid_chan_67");
        Assert.NotNull(stranded);
        Assert.Equal(37, stranded!.Count);
        Assert.Contains("stranded", h.NoticeText());
        Assert.Contains("restarting", h.NoticeText());
        // Main is unaffected: its db connection keeps working and the supervisor still answers.
        h.Db.Exec("INSERT INTO notes(subject, text, created) VALUES ('main', 'still fine', 1)");
        Assert.Equal("maid_chan", h.Db.Scalar("SELECT clone_id FROM notes WHERE text = 'still fine'"));
        Assert.Contains("67 restarting", h.Supervisor.List());
        output.WriteLine(h.NoticeText());
    }

    [Fact]
    public async Task Crash_loop_backs_off_then_retires()
    {
        var cfg = TestHive.Config();
        cfg.BackoffBaseSeconds = 0.4;
        cfg.MaxCrashes = 3;
        await using var h = new TestHive(cfg, mode: "crash");
        Assert.StartsWith("ok", h.Supervisor.Spawn("crash please", "69"));
        var slot = h.Supervisor.Slot("maid_chan_69")!;
        await Wait.Until(() => slot.State == CloneState.Retired, 90, "retirement");
        var c = slot.Crashes;
        Assert.Equal(3, c.Count);
        Assert.True((c[1] - c[0]).TotalSeconds >= 0.4, $"first backoff {(c[1] - c[0]).TotalSeconds:0.00}s");
        Assert.True((c[2] - c[1]).TotalSeconds >= 0.8, $"second backoff {(c[2] - c[1]).TotalSeconds:0.00}s");
        Assert.Contains("retired after 3 crashes", h.NoticeText());
        Assert.StartsWith("no: 69 is retired", h.Supervisor.Spawn("again", "69"));
        output.WriteLine(h.NoticeText());
    }

    [Fact]
    public async Task Losing_the_main_pipe_makes_the_clone_deposit_and_log_out()
    {
        var pipe = PipeNames.For("maid_chan_67");
        await using var server = PipeNames.Server(pipe);
        var (p, lines) = Stub.StartRaw("--clone", "maid_chan_67", "--parent-pipe", pipe, "--mode", "normal", "--stacks", "3", "--chest", "10");
        try
        {
            await server.WaitForConnectionAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            var link = new HiveLink(server, "test-main");
            var hello = new TaskCompletionSource();
            link.Handler = (m, _) =>
            {
                if (m.T != Msg.Hello) return Task.FromResult<object?>(null);
                hello.TrySetResult();
                return Task.FromResult<object?>(new WelcomeMsg("maid_chan", new TaskMsg("t1", "idle"), new RosterMsg("maid_chan", ["maid-chan"], [], new()), new ClaimsMsg([]), false, 0));
            };
            link.Start();
            await hello.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(800);
            await link.DisposeAsync(); // the main process "dies": pipe gone
            await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            string all; lock (lines) all = string.Join("\n", lines);
            output.WriteLine(all);
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("LOGOUT reason=main process lost empty=True left=0 disconnect=0", all);
        }
        finally { if (!p.HasExited) p.Kill(true); }
    }

    [Fact]
    public async Task Snapshots_stream_into_the_tui_source_claims_conflict_across_processes_and_recall_logs_out()
    {
        await using var h = new TestHive(mode: "normal", stubArgs: ["--stacks", "4", "--chest", "27"]);
        Assert.True(h.Board.TryClaim("maid_chan", ClaimKey.Block(9, 64, 9)).Ok); // main holds the block the clone will want
        var slot = h.Supervisor.Slot("maid_chan_67")!;
        var src = new RemoteBotSource(slot);
        var usage = new List<PromptUsageRecord>();
        src.UsageRecorded += r => { lock (usage) usage.Add(r); };
        Assert.Equal(BotStatus.Offline, src.Snapshot(true).Status);

        Assert.StartsWith("ok", h.Supervisor.Spawn("claim: the block at 9,64,9", "67"));
        await Wait.Until(() => src.Snapshot(true) is { Status: BotStatus.Online, Pos: not null, Viewport: not null }, what: "a streamed snapshot");
        var full = src.Snapshot(true);
        Assert.Equal("maid_chan_67", full.Name);
        Assert.Null(src.Snapshot(false).Viewport);          // strip form for the bot list
        await Wait.Until(() => { lock (usage) return usage.Count > 0; }, what: "usage record");
        await Wait.Until(() => h.NoticeText().Contains("claim refused by maid_chan"), what: "claim conflict");
        Assert.Equal(0.3, slot.Progress, 3);
        Assert.True(await src.SendAsync("/status", CancellationToken.None)); // TUI console line reaches the clone

        Assert.StartsWith("ok: recalling 67", h.Supervisor.Recall("67"));
        await Wait.Until(() => slot.State == CloneState.Idle, 30, "logout");
        Assert.Contains("67 logged out (recalled)", h.NoticeText());
        Assert.Null(h.Store.Stranded("maid_chan_67"));
        Assert.Empty(h.Store.Inventory("maid_chan_67"));  // deposited: ledger shows an empty inventory
        Assert.Equal(BotStatus.Offline, src.Snapshot(true).Status);
        output.WriteLine(h.NoticeText());
    }

    [Fact]
    public async Task Budget_ledger_caps_a_clone_and_budget_drop_recalls_clones_furthest_from_done_first()
    {
        var cfg = TestHive.Config();
        cfg.Budget.CloneDailyCapUsd = 0.35m;
        cfg.Budget.GrantUsd = 0.10m;
        await using var h = new TestHive(cfg, mode: "normal");
        // 1) Per-clone sub-cap enforced over IPC: $0.10 calls, refused at the 4th, the clone deposits and logs out.
        Assert.StartsWith("ok", h.Supervisor.Spawn("spend money", "1337"));
        var spender = h.Supervisor.Slot("maid_chan_1337")!;
        await Wait.Until(() => spender.State == CloneState.Idle, 60, "budget logout");
        Assert.Equal(0.30m, h.Ledger.CloneSpend("maid_chan_1337").Today);
        Assert.Contains("1337 logged out", h.NoticeText());

        // 2) Hive budget drop: clones are recalled, furthest from finishing first.
        Assert.StartsWith("ok", h.Supervisor.Spawn("claim: something", "67"));   // reports 30% progress
        Assert.StartsWith("ok", h.Supervisor.Spawn("just stand there", "69"));   // 0%
        await Wait.Until(() => h.Supervisor.Slot("maid_chan_67")!.Progress > 0, what: "progress");
        await Wait.Until(() => h.Supervisor.Slot("maid_chan_69")!.Connected, what: "69 up");
        Assert.Equal(["maid_chan_69", "maid_chan_67"], h.Supervisor.BudgetRecallOrder());
        h.MainToday = 5.5m; // past the recall line (0.5 × $10)
        Assert.StartsWith("no: budget", h.Supervisor.Spawn("one more", "420"));
        await Wait.Until(() => h.Supervisor.Slots.All(s => s.State is CloneState.Idle or CloneState.Retired), 30, "budget recall");
        Assert.Contains("budget: recalling 69, 67", h.NoticeText());
        output.WriteLine(h.NoticeText());
    }

    [Fact]
    public async Task In_game_death_is_reported_recorded_once_and_releases_claims_without_touching_main()
    {
        await using var h = new TestHive(mode: "normal", stubArgs: ["--stacks", "2", "--chest", "27"]);
        Assert.StartsWith("ok", h.Supervisor.Spawn("claim: then die", "67"));
        var slot = h.Supervisor.Slot("maid_chan_67")!;
        await Wait.Until(() => slot.Deaths == 1, what: "death report");
        await Wait.Until(() => h.Store.CountEvents("death", "maid_chan_67") == 1, what: "shared death record");
        Assert.Equal(0, h.Board.CountOf("maid_chan_67"));
        Assert.DoesNotContain("died", h.NoticeText());           // main's context untouched (no event)
        Assert.Contains("died x1", h.Supervisor.List());
        Assert.Equal(CloneState.Running, slot.State);             // OnCloneDeath = respawn: keeps going
        await h.Supervisor.RecallAllAsync("test over", TimeSpan.FromSeconds(20));
        Assert.Equal(1, h.Store.CountEvents("death", "maid_chan_67"));
    }

    [Fact]
    public async Task Slot_policy_blocks_spawns_that_would_take_a_regulars_slot()
    {
        await using var h = new TestHive(mode: "normal");
        lock (h.Online) { h.Online.Clear(); h.Online.AddRange(["maid_chan", "stranger1", "stranger2", "stranger3"]); }
        // 7 slots: 4 online, Fox + Robin offline → 3 free, 2 reserved → exactly one clone.
        Assert.StartsWith("ok", h.Supervisor.Spawn("a", "67"));
        Assert.StartsWith("no: only 2 slot(s) free", h.Supervisor.Spawn("b", "69"));
        await h.Supervisor.RecallAllAsync("test over", TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Deposit_before_logout_blocks_until_the_inventory_is_empty()
    {
        var pipe = PipeNames.For("maid_chan_420");
        await using var server = PipeNames.Server(pipe);
        var cfg = TestHive.Config();
        cfg.DepositTimeoutSeconds = 30;
        var port = new FakePort(stacks: 5, chestSlots: 0);   // storage full / none yet
        var body = new FakeBody("maid_chan_420", port);
        var host = new CloneHost("maid_chan_420", pipe, cfg, body);
        var run = Task.Run(() => host.RunAsync(CancellationToken.None));
        await server.WaitForConnectionAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        var link = new HiveLink(server, "test-main");
        ByeMsg? bye = null;
        link.Handler = (m, _) => m.T switch
        {
            Msg.Hello => Task.FromResult<object?>(new WelcomeMsg("maid_chan", null, new RosterMsg("maid_chan", [], [], new()), new ClaimsMsg([]), false, 0)),
            Msg.Bye => Task.FromResult<object?>(bye = m.As<ByeMsg>()),
            Msg.Claim => Task.FromResult<object?>(new ClaimReplyMsg(true, "maid_chan_420", DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds())),
            _ => Task.FromResult<object?>(null),
        };
        link.Start();
        await Wait.Until(() => body.Calls.Contains("start"), what: "clone running");

        await link.SendAsync(Msg.Recall, new RecallMsg("recalled"));
        await Task.Delay(2500);
        Assert.False(body.Disconnected.Task.IsCompleted, "logged out while still holding items");
        Assert.Equal(5, port.Items().Count);
        Assert.True(host.LoggingOut);

        port.AddChest(new BlockPos(2, 64, 2), 27);            // storage space appears
        await body.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, body.ItemsAtDisconnect);               // verified empty before the logout
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        await Wait.Until(() => bye != null, 5, "bye");
        Assert.True(bye!.Deposited);
        Assert.Equal("recalled", bye.Reason);
        await link.DisposeAsync();
    }
}

[CollectionDefinition("processes", DisableParallelization = true)]
public class ProcessCollection;
