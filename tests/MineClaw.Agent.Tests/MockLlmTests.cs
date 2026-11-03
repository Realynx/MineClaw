using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

public sealed class MockLlmTests
{
    [Fact]
    public async Task FactoryRoutesEveryMockTierAndMatchesOnlyNewestUserText()
    {
        var options = new LlmOptions
        {
            MainProvider = "mock", WorkerProvider = "mock", CheapProvider = "mock",
            MainModel = "mock-main", WorkerModel = "mock-worker", CheapModel = "mock-memory",
            MockScriptPath = Path.Combine(AppContext.BaseDirectory, "offline-brain.json"),
        };
        var client = LlmClientFactory.Create(options);
        using var dispose = client as IDisposable;
        foreach (var tier in new[] { ModelTier.Main, ModelTier.Worker, ModelTier.Cheap })
        {
            var reply = await client.CompleteAsync(new() { Tier = tier, Messages = [LlmMessage.User("COME HERE please")] }, default);
            Assert.Equal("mock", reply.Provider);
            Assert.Equal(options.ModelFor(tier), reply.Model);
            Assert.Equal(LlmUsage.Zero, reply.Usage);
            Assert.Equal("On my way.", reply.Text);
            Assert.Equal(StopReasons.ToolUse, reply.StopReason);
            var tool = Assert.Single(reply.ToolUses);
            Assert.Equal("do", tool.Name);
            Assert.Equal("goto", tool.Input.GetProperty("action").GetString());
            Assert.Equal("tester", tool.Input.GetProperty("args").GetProperty("to").GetString());
            var next = await client.CompleteAsync(new() { Tier = tier, Messages = [LlmMessage.User("come here"),
                LlmMessage.Assistant(tool), LlmMessage.User(new LlmToolResult(tool.Id, "come here succeeded"))] }, default);
            Assert.Empty(next.ToolUses);
            Assert.Equal(StopReasons.EndTurn, next.StopReason);
            Assert.Equal("Offline and ready.", next.Text);
        }
        var newest = await client.CompleteAsync(new() { Messages = [LlmMessage.User("come here"),
            LlmMessage.Assistant("hello"), LlmMessage.User("nothing matches")] }, default);
        Assert.Equal("Offline and ready.", newest.Text);
        Assert.Empty(newest.ToolUses);
        var textOnly = await client.CompleteAsync(new() { Messages = [LlmMessage.User("hello")] }, default);
        Assert.Equal("Hello from the offline sandbox!", textOnly.Text);
        Assert.Empty(textOnly.ToolUses);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync(new(), cancelled.Token));
        Assert.False(new LlmOptions().MockInUse);
    }
}
