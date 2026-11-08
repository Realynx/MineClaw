using MineClaw.Data;

namespace MineClaw.Bot.Tests;

public sealed class SmeltInputVariantToolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Explicit_input_variant_reaches_the_client_and_limits_the_default_count(int meta)
    {
        var (host, client) = TestBot.Create();
        using var _ = host;
        client.Give(9, Items.RawFish, 12, 1 - meta);
        client.Give(10, Items.RawFish, 3, meta);
        var result = await host.RunAs(null, "smelt", new { item = "raw_fish", input_meta = meta });
        Assert.True(result.Ok, result.Text);
        Assert.Equal(meta, client.LastSmeltInputMeta);
        Assert.Contains(client.CallList, call => call.StartsWith("smelt 349 3 "));
    }

    [Fact]
    public async Task Old_smelt_calls_keep_the_unspecified_variant_default()
    {
        var (host, client) = TestBot.Create();
        using var _ = host;
        var result = await host.RunAs(null, "smelt", new { item = "cobblestone", count = 2 });
        Assert.True(result.Ok, result.Text);
        Assert.Null(client.LastSmeltInputMeta);
    }
}
