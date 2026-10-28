using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;
using MineClaw.Hive.Testing;
using MineClaw.World.Claims;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Main's hive vision over the real clone IPC: "send me your view" (the clone flushes its world memory and answers with its
/// eye pose), clone positions/jobs from their snapshots, leases from the claim board.
/// </summary>
public class HiveVisionTests
{
    private sealed class Proc(int id, Task<int> run, CancellationTokenSource kill) : ICloneProcess
    {
        public int Id => id;
        public bool HasExited => run.IsCompleted;
        public int? ExitCode => run.IsCompletedSuccessfully ? run.Result : run.IsCompleted ? 1 : null;
        public async Task WaitForExitAsync(CancellationToken ct = default) { try { await run.WaitAsync(ct); } catch (Exception) when (!ct.IsCancellationRequested) { } }
        public void Kill() { try { kill.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose() { }
    }

    /// <summary>Clones run in-process on real pipes; each answers "view" with a fixed pose and counts its flushes.</summary>
    private sealed class Launcher(HiveConfig cfg) : IProcessLauncher
    {
        public int Flushes;
        /// <summary>The clone's vision history (what its models got): one sheet, answered over the vision IPC.</summary>
        public readonly MineClaw.Bot.Perception.Vision.VisionHistory Sheets = new(capacity: 5);

        public ICloneProcess Start(string clone, string pipeName)
        {
            var kill = new CancellationTokenSource();
            var body = new FakeBody(clone, new FakePort()) { TaskText = "mine the cobble gen" };
            var host = new CloneHost(clone, pipeName, cfg, body)
            {
                View = _ =>
                {
                    Interlocked.Increment(ref Flushes);
                    return Task.FromResult<CloneViewMsg?>(new CloneViewMsg(5.5, 64, 7.5, 180f, 25f, 0, 12));
                },
                VisionList = since => new VisionListMsg(Sheets.Capacity, Sheets.List(null, since).ToList()),
                VisionPng = id => Sheets.Png(id) is { } png ? new VisionPngMsg(id, png) : null,
            };
            return new Proc(Environment.TickCount, Task.Run(() => host.RunAsync(kill.Token)), kill);
        }
    }

    [Fact]
    public async Task Main_gets_a_clone_view_over_ipc_and_the_hive_from_snapshots_and_claims()
    {
        var cfg = TestHive.Config();
        cfg.LaunchGapSeconds = 0;
        cfg.DepositTimeoutSeconds = 5;
        var launcher = new Launcher(cfg);
        var board = new ClaimBoard(TimeSpan.FromSeconds(60));
        var ledger = new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, cfg.Budget, () => (0m, 0m));
        var sup = new HiveSupervisor(cfg, "maid_chan", launcher, board, ledger, new ChatArbiter("maid_chan", "maid_chan", cfg.Clones()), null,
            () => new SlotFacts(20, ["maid_chan", "PoofimaFox"]), ["PoofimaFox"]);
        try
        {
            TurnContext.Current = new TurnInfo("PoofimaFox", ["PoofimaFox"], false);
            try { Assert.StartsWith("ok", sup.Spawn("Mine the cobble generator at 4,64,6", "67")); }
            finally { TurnContext.Current = null; }
            await Wait.Until(() => sup.Slot("maid_chan_67") is { Connected: true, Snapshot.Pos: not null }, 20, "67 up with a snapshot");

            var vision = new HiveVisionSource(() => sup, board);
            var bot = Assert.Single(vision.Clones());
            Assert.Equal(("maid_chan_67", 1.5, 64.0, 1.5, "mine the cobble gen", 20), (bot.Name, bot.X, bot.Y, bot.Z, bot.Job, bot.Hp));

            var pose = await vision.ViewAsync("67", CancellationToken.None);
            Assert.NotNull(pose);
            Assert.Equal(("maid_chan_67", 5.5, 7.5, 180f, 0, 12), (pose!.Name, pose.X, pose.Z, pose.Yaw, pose.Dim, pose.Flushed));
            Assert.Equal(1, launcher.Flushes);                       // the clone flushed its world memory before answering
            Assert.Null(await vision.ViewAsync("1337", CancellationToken.None)); // not online: no view

            // "What the AI saw" over the vision / vision_png IPC: the clone's sheets and the exact PNG bytes.
            var png = MineClaw.Render.Png.Encode(new uint[24 * 16], 24, 16);
            var rec = launcher.Sheets.Add(png, 24, 16, new MineClaw.Bot.Perception.Vision.VisionSheetInfo(0, DateTimeOffset.UtcNow, "maid_chan_67", "sheet", "see",
                "direct", ["iso yaw=90"], [5, 64, 7], 6, null, null, 0, 0, 0, 0, "", "legend"));
            var list = await sup.RequestVisionListAsync("67", 0, TimeSpan.FromSeconds(3));
            var info = Assert.Single(list!.Sheets);
            Assert.Equal((rec.Id, rec.Sha256, "iso yaw=90", 5), (info.Id, info.Sha256, info.Views[0], list.Capacity));
            Assert.Empty((await sup.RequestVisionListAsync("67", rec.Id, TimeSpan.FromSeconds(3)))!.Sheets);
            Assert.Equal(png, (await sup.RequestVisionPngAsync("67", rec.Id, TimeSpan.FromSeconds(3)))!.Png);
            Assert.Null(await sup.RequestVisionPngAsync("67", rec.Id + 99, TimeSpan.FromSeconds(3)));
            Assert.Null(await sup.RequestVisionListAsync("1337", 0, TimeSpan.FromSeconds(1)));

            Assert.True(board.TryClaim("maid_chan_67", ClaimKey.Block(4, 64, 6)).Ok);
            var lease = Assert.Single(vision.Leases());
            Assert.Equal(("maid_chan_67", ClaimKey.Block(4, 64, 6).Id), (lease.Owner, lease.Key));
        }
        finally
        {
            try { await sup.RecallAllAsync("test over", TimeSpan.FromSeconds(10)); } catch (Exception) { }
            await sup.DisposeAsync();
        }
    }
}
