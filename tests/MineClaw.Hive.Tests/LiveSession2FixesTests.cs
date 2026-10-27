using MineClaw.Agent.Budget;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Inventory;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Testing;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Hive.Tests;

/// <summary>Live session 2: deposit-before-logout on fresh state (item 17) and name casing in the hive (item 11).</summary>
public class LiveSession2FixesTests
{
    private static ItemMsg Sticks(int n) => new(280, 0, n, "stick");

    [Fact]
    public async Task Deposit_never_trusts_a_stale_empty_snapshot()
    {
        // 67 at 01:05:50: the local copy said empty while the worker said "4 sticks left". The deposit settles first.
        var port = new FakePort(0, chestSlots: 4);
        port.Hidden.Add(Sticks(4));
        var r = await new DepositProcedure(port, new ClaimBoard().ViewFor("maid_chan_67"), "x") { RetryDelay = TimeSpan.FromMilliseconds(5) }
            .RunAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(r.Empty, r.Text);
        Assert.Equal(1, port.Deposits);
        Assert.Equal(4, r.Deposited);
        Assert.Contains("deposited 4", r.Text);
        Assert.True(port.Settles >= 2); // before the first read and again before declaring empty after the deposit
    }

    [Fact]
    public async Task Deposit_rechecks_fresh_state_after_a_rejected_click()
    {
        var port = new FakePort(1, chestSlots: 8);
        port.RejectedOnDeposit.Add(Sticks(4)); // predicted moved, but the server bounced it back
        var r = await new DepositProcedure(port, new ClaimBoard().ViewFor("maid_chan_67"), "x") { RetryDelay = TimeSpan.FromMilliseconds(5) }
            .RunAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(r.Empty, r.Text);
        Assert.Equal(2, port.Deposits); // the bounced sticks went in on the second pass
        Assert.Empty(port.Items());
    }

    [Fact]
    public void Ledger_merges_clone_names_case_insensitively_across_a_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc-ledger-{Guid.NewGuid():N}.json");
        try
        {
            var caps = new BudgetOptions { DailyCapUsd = 50, MonthlyCapUsd = 200 };
            var cfg = new HiveBudgetConfig { CloneDailyCapUsd = 50, RecallAtFraction = 1 };
            var a = new BudgetLedger(caps, cfg, () => (0m, 0m), path: path);
            a.Record("Maid_Chan_67", 1m);
            var b = new BudgetLedger(caps, cfg, () => (0m, 0m), path: path); // JSON replaced the dictionary
            b.Record("maid_chan_67", 2m);
            Assert.Equal(3m, b.CloneSpend("MAID_CHAN_67").Today);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public void Claims_ignore_owner_casing()
    {
        var board = new ClaimBoard();
        var key = ClaimKey.Block(new BlockPos(4, 64, 6));
        Assert.True(board.TryClaim("maid_chan_67", key).Ok);
        Assert.True(board.TryClaim("Maid_Chan_67", key).Ok);            // same bot renewing, not a conflict
        Assert.True(board.IsFree("MAID_CHAN_67", key));
        Assert.False(board.IsFree("maid_chan", key));
        Assert.True(board.Release("MAID_chan_67", key));
        Assert.True(board.IsFree("maid_chan", key));
    }
}
