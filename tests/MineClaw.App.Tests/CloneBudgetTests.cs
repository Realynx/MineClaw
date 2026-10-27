using MineClaw.Agent;
using MineClaw.App.Hosting;
using MineClaw.Hive;
using Microsoft.Extensions.Configuration;

namespace MineClaw.App.Tests;

/// <summary>Live session 2: Hive:Budget:CloneDailyCapUsd = 0 gave the clone Agent:Budget:DailyCapUsd = 0 → config check
/// failed (exit 2) → 3 crash-restarts → retired. 0/unset now means "no sub-cap: inherit main's hard caps".</summary>
public class CloneBudgetTests
{
    private static (MinecraftSettings Mc, AgentOptions Agent) Main()
    {
        var mc = new MinecraftSettings { Host = "10.1.2.69", Username = "maid_chan" };
        var a = new AgentOptions();
        a.Llm.ApiKey = "sk-ant-test";
        a.Chat.Username = "maid_chan";
        return (mc, a);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public void Clone_with_sub_cap_0_inherits_main_hard_caps_and_passes_the_config_check(int daily, int monthly)
    {
        var (mc, a) = Main();
        var hive = new HiveConfig { Budget = { CloneDailyCapUsd = daily, CloneMonthlyCapUsd = monthly } };
        CloneMode.Prepare("maid_chan_67", mc, a, hive, "logs", "logs");
        Assert.Equal(50m, a.Budget.DailyCapUsd);
        Assert.Equal(200m, a.Budget.MonthlyCapUsd);
        Assert.Empty(ConfigCheck.Validate(mc, a));
    }

    [Fact]
    public void Unset_sub_caps_default_to_inherit_and_a_set_sub_cap_still_applies()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Hive:Budget:GrantUsd"] = "0.2" }).Build();
        var hive = cfg.GetSection("Hive").Get<HiveConfig>()!;
        Assert.Equal(0m, hive.Budget.CloneDailyCapUsd);
        Assert.Equal(0m, hive.Budget.CloneMonthlyCapUsd);
        Assert.Equal(1.0, hive.Budget.RecallFraction); // no early recall: only the hard cap stops clones

        var (mc, a) = Main();
        CloneMode.Prepare("maid_chan_69", mc, a, hive, "logs", "logs");
        Assert.Equal(50m, a.Budget.DailyCapUsd);
        Assert.Equal(200m, a.Budget.MonthlyCapUsd);

        var (mc2, a2) = Main();
        CloneMode.Prepare("maid_chan_69", mc2, a2, new HiveConfig { Budget = { CloneDailyCapUsd = 2.5m } }, "logs", "logs");
        Assert.Equal(2.5m, a2.Budget.DailyCapUsd);
        Assert.Equal(200m, a2.Budget.MonthlyCapUsd);
    }
}
