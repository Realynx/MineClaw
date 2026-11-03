namespace MineClaw.Bot.Tests;

public class ToolSchemaValidationTests
{
    [Fact]
    public async Task Script_validates_params_and_keeps_default_and_binding_operations()
    {
        var (host, _) = TestBot.Create();
        using (host)
        {
            var invalid = await host.Run("script", new { op = "status", @params = new[] { "wrong" } });
            Assert.False(invalid.Ok);
            Assert.Contains("$.params", invalid.Text);
            Assert.True((await host.Run("script", new { })).Ok);
            var bindings = await host.Run("script", new { op = "bindings" });
            Assert.True(bindings.Ok, bindings.Text);
        }
    }
}
