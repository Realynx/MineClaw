using MineClaw.Agent.Budget;
using MineClaw.Hive;
using MineClaw.Hive.Budget;

namespace MineClaw.Hive.Tests;

/// <summary>Live session 2: clone sub-caps 0/unset = inherit main's hard caps; nothing throttles before the hard cap.</summary>
public class CloneBudgetLedgerTests
{
    private static readonly BudgetOptions HardCaps = new() { DailyCapUsd = 50, MonthlyCapUsd = 200 };

    [Fact]
    public void Default_hive_budget_has_no_clone_sub_caps_and_no_early_recall()
    {
        var o = new HiveBudgetConfig();
        Assert.Equal(0m, o.CloneDailyCapUsd);
        Assert.Equal(0m, o.CloneMonthlyCapUsd);
        Assert.Equal(1.0, o.RecallFraction);
        Assert.Equal(50m, o.CloneDailyCap(50m));
        Assert.Equal(200m, o.CloneMonthlyCap(200m));
        Assert.Equal(1.0, new HiveBudgetConfig { RecallAtFraction = 0 }.RecallFraction);
    }

    [Fact]
    public void Clone_with_sub_cap_0_spends_up_to_the_shared_hard_cap()
    {
        decimal main = 20m;
        var l = new BudgetLedger(HardCaps, new HiveBudgetConfig(), () => (main, main));
        for (int i = 0; i < 40; i++) // a clone well past the old $30 monthly / $1.50 daily sub-caps
        {
            var g = l.Authorize("maid_chan_67", 0.5m);
            Assert.True(g.Ok, g.Reason);
            l.Record("maid_chan_67", g.GrantUsd);
        }
        Assert.Equal(BudgetMode.Normal, l.HiveMode());          // $20 main + ~$20 clone of $50: no soft stage
        Assert.False(l.AtRecallLine(out var why), why);
        main = 49.9m;
        Assert.False(l.Authorize("maid_chan_67", 0.5m).Ok);    // the hard cap itself still stops everyone
    }

    [Fact]
    public void Monthly_sub_cap_0_is_not_reported_as_a_cap()
    {
        var l = new BudgetLedger(HardCaps, new HiveBudgetConfig { CloneMonthlyCapUsd = 0 }, () => (0m, 0m));
        l.Record("maid_chan_67", 31m);
        var g = l.Authorize("maid_chan_67", 0.1m);
        Assert.True(g.Ok, g.Reason);
    }
}
