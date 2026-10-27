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

    [Fact] // Night review 10-27 B22: 07:52:36 main logged "today $2.980/50.00" while the ledger said "hive today $4.882/50.00 (clones $1.902)"
    public void B22_hive_main_summary_shows_hive_spend_against_the_cap_and_calls_per_tier()
    {
        var time = new ManualTime();
        var t = new UsageTracker(new BudgetOptions { DailyCapUsd = 50m, MonthlyCapUsd = 200m }, time: time, path: null)
        {
            Classify = m => new LlmOptions().TierOfModel(m),
        };
        t.Record("claude-sonnet-5-5", new LlmUsage(0, 298_000, 0, 0));          // $2.98 main
        for (int i = 0; i < 5; i++) t.Record("claude-haiku-5-5", new LlmUsage(0, 0, 0, 0)); // RAPTOR summaries
        t.ExternalSpend = () => (1.902m, 1.902m);

        var s = t.Summary();
        Assert.StartsWith("today $4.882/50.00 (main $2.980, clones $1.902, 6 calls", s);
        Assert.Contains("month $4.88/200.00", s);
        Assert.Contains("calls/h worker 1 memory 5", s);                       // never one "6 calls/h" for the brain
        Assert.Equal(5, t.CallsLastHourByTier["memory"]);
        Assert.Equal(6, t.CallsLastHour);

        t.ExternalSpend = null;                                                 // single bot / clone: own spend only
        Assert.StartsWith("today $2.980/50.00 (6 calls", t.Summary());
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
    public void Hard_caps_only_no_hourly_call_cap_even_when_configured()
    {
        // Live session 2: ~37 background RAPTOR summaries hit "hourly call limit" and paused the brain mid-conversation.
        var time = new ManualTime();
        var t = new UsageTracker(new BudgetOptions { MaxCallsPerHour = 3 }, time: time, path: null);
        var modes = new List<BudgetMode>();
        t.ModeChanged += (m, _) => modes.Add(m);
        for (int i = 0; i < 100; i++) t.Record("claude-haiku-5-5", new LlmUsage(10, 10, 0, 0));
        Assert.Equal(BudgetMode.Normal, t.Mode);
        Assert.Empty(modes);
    }

    [Fact]
    public void Default_budget_never_leaves_Normal_below_the_hard_cap()
    {
        var time = new ManualTime();
        var o = new BudgetOptions();
        Assert.Equal(50m, o.DailyCapUsd);
        Assert.Equal(200m, o.MonthlyCapUsd);
        var t = new UsageTracker(o, time: time, path: null);
        // ~$49.9 of opus spend today: still Normal (no soft fallback stage by default).
        while (t.Today.TotalUsd < 49.5m) t.Record("claude-opus-5-5", new LlmUsage(100_000, 10_000, 0, 0));
        Assert.Equal(BudgetMode.Normal, t.Mode);
        while (t.Today.TotalUsd < 50m) t.Record("claude-opus-5-5", new LlmUsage(100_000, 10_000, 0, 0));
        Assert.Equal(BudgetMode.Paused, t.Mode);
        Assert.Contains("daily budget", t.Reason);

        var zero = new UsageTracker(new BudgetOptions { DailyCapUsd = 10m, CheapOnlyFraction = 0 }, time: time, path: null);
        Assert.Equal(BudgetMode.Normal, zero.Mode); // 0 = no cheap-only stage, not "always cheap-only"
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
