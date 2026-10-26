using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

public class BudgetTests
{
    [Fact]
    public void Cost_UsesPriceTable_IncludingCache()
    {
        var p = new PriceTable();
        // Sonnet 5.5: 1M in $2, 1M out $10, 1M cache read $0.20, 1M 5m write $2.50, 1M 1h write $4
        var u = new LlmUsage(1_000_000, 1_000_000, 1_000_000, 2_000_000, CacheWrite1hTokens: 1_000_000);
        Assert.Equal(2m + 10m + 0.2m + 2.5m + 4m, p.Cost("claude-sonnet-5-5", u));
        Assert.Equal(0.1m + 0.5m, p.Cost("claude-haiku-5-5", new LlmUsage(1_000_000, 1_000_000, 0, 0)));
        Assert.Equal(5m, p.Cost("some-unknown-model", new LlmUsage(1_000_000, 0, 0, 0))); // conservative fallback
    }

    [Fact]
    public void Caps_DriveModes_AndFireNotices()
    {
        var time = new ManualTime();
        var t = new UsageTracker(new BudgetOptions { DailyCapUsd = 1m, MonthlyCapUsd = 10m, CheapOnlyFraction = 0.8 }, time: time, path: null);
        var changes = new List<BudgetMode>();
        t.ModeChanged += (m, _) => changes.Add(m);

        Assert.Equal(BudgetMode.Normal, t.CheckMode());
        t.Record("claude-sonnet-5-5", new LlmUsage(0, 85_000, 0, 0)); // $0.85
        Assert.Equal(BudgetMode.CheapOnly, t.Mode);
        t.Record("claude-sonnet-5-5", new LlmUsage(0, 20_000, 0, 0)); // $1.05
        Assert.Equal(BudgetMode.Paused, t.Mode);
        Assert.Contains("daily", t.Reason);

        time.Advance(TimeSpan.FromDays(1)); // new day → back to normal
        Assert.Equal(BudgetMode.Normal, t.CheckMode());
        Assert.Equal([BudgetMode.CheapOnly, BudgetMode.Paused, BudgetMode.Normal], changes);
        Assert.Equal(1.05m, t.ThisMonth.Usd);
    }

    [Fact]
    public void MonthlyCap_Pauses()
    {
        var t = new UsageTracker(new BudgetOptions { DailyCapUsd = 100m, MonthlyCapUsd = 1m }, time: new ManualTime(), path: null);
        t.Record("claude-sonnet-5-5", new LlmUsage(0, 100_000, 0, 0));
        Assert.Equal(BudgetMode.Paused, t.Mode);
        Assert.Contains("monthly", t.Reason);
    }

    [Fact]
    public void HourlyCallCap_PausesUntilWindowPasses()
    {
        var time = new ManualTime();
        var t = new UsageTracker(new BudgetOptions { MaxCallsPerHour = 3 }, time: time, path: null);
        for (int i = 0; i < 3; i++) t.Record("claude-haiku-5-5", new LlmUsage(10, 10, 0, 0));
        Assert.Equal(BudgetMode.Paused, t.Mode);
        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(BudgetMode.Normal, t.Mode);
    }

    [Fact]
    public void Totals_PersistAcrossInstances()
    {
        using var dir = new TempDir();
        var time = new ManualTime();
        var path = dir.File("usage.json");
        var a = new UsageTracker(new BudgetOptions(), time: time, path: path);
        a.Record("claude-haiku-5-5", new LlmUsage(1000, 100, 5000, 0));
        a.Record("claude-haiku-5-5", new LlmUsage(1000, 100, 5000, 0));

        var b = new UsageTracker(new BudgetOptions(), time: time, path: path);
        Assert.Equal(2, b.Today.Calls);
        Assert.Equal(a.Today.Usd, b.Today.Usd);
        Assert.Equal(10_000, b.ThisMonth.CacheReadTokens);
    }
}
