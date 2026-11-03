using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.App.Hosting;
using MineClaw.Bot;
using Microsoft.Extensions.Configuration;

namespace MineClaw.App.Tests;

public sealed class OfflineConfigTests
{
    [Fact]
    public void ExampleAcceptsMockWithoutKeysButMixedAnthropicStillNeedsOne()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "offline-config.json")).Build();
        var options = config.GetSection("Agent").Get<AgentOptions>()!;
        options.Llm.MockScriptPath = Path.Combine(AppContext.BaseDirectory, "offline-brain.json");
        var mc = config.GetSection("Minecraft").Get<MinecraftSettings>()!;
        Assert.Empty(ConfigCheck.Validate(mc, options));
        Assert.True(options.Llm.MockInUse);
        Assert.False(options.Llm.TiersUseAnthropic);
        Assert.Empty(KeyCheck.AnthropicModels(options.Llm));
        Assert.Empty(KeyCheck.OllamaModels(options.Llm));
        Assert.False(options.Llm.OllamaInUse);
        Assert.False(options.Rag.Enabled);
        Assert.False(config.GetSection("Bot").Get<BotOptions>()!.Vision.AllowDownload);
        options.Llm.WorkerProvider = LlmProviders.Anthropic;
        Assert.True(options.Llm.TiersUseAnthropic);
        Assert.Single(KeyCheck.AnthropicModels(options.Llm));
        Assert.Contains(ConfigCheck.Validate(mc, options), p => p.Contains("ApiKey is missing"));
        options.Llm.WorkerProvider = LlmProviders.Mock;
        options.Llm.Profiles["offline"] = new() { Provider = "mock", Model = "mock-profile" };
        Assert.Null(options.Llm.CheckProfile("offline"));
    }
}
