using System.Net;
using System.Text;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.App.Hosting;

namespace MineClaw.App.Tests;

/// <summary>Config validation and startup checks for the Ollama backend; empty config = the old checks exactly.</summary>
public class OllamaConfigTests
{
    private static (MinecraftSettings Mc, AgentOptions Agent) Main()
    {
        var mc = new MinecraftSettings { Host = "10.1.2.69", Username = "maid_chan" };
        var a = new AgentOptions();
        a.Llm.ApiKey = "sk-ant-test";
        a.Chat.Username = "maid_chan";
        return (mc, a);
    }

    [Fact]
    public void Default_config_passes_and_checks_the_same_anthropic_models()
    {
        var (mc, a) = Main();
        Assert.Empty(ConfigCheck.Validate(mc, a));
        Assert.Equal(["claude-haiku-5-5", "claude-opus-5-5", "claude-sonnet-5-5"], KeyCheck.AnthropicModels(a.Llm));
        Assert.Empty(KeyCheck.OllamaModels(a.Llm));
        a.Llm.ApiKey = null;
        Assert.Contains(ConfigCheck.Validate(mc, a), p => p.Contains("Agent:Llm:ApiKey is missing"));
    }

    [Fact]
    public void An_ollama_tier_without_an_endpoint_is_a_config_error()
    {
        var (mc, a) = Main();
        a.Llm.MainProvider = "ollama";
        a.Llm.MainModel = "qwen3:8b";
        var p = ConfigCheck.Validate(mc, a);
        Assert.Contains(p, x => x.Contains("Agent:Llm:MainProvider is ollama but Agent:Llm:Ollama:Endpoint is empty"));
        Assert.DoesNotContain(p, x => x.Contains("no price for model qwen3")); // Ollama models are priced from Prices:Ollama

        a.Llm.Ollama.Endpoint = "http://localhost:11434";
        Assert.Empty(ConfigCheck.Validate(mc, a));
        Assert.Equal(["claude-haiku-5-5", "claude-sonnet-5-5"], KeyCheck.AnthropicModels(a.Llm));
    }

    [Fact]
    public void All_tiers_on_ollama_need_no_anthropic_key()
    {
        var (mc, a) = Main();
        a.Llm.ApiKey = null;
        a.Llm.MainProvider = a.Llm.WorkerProvider = a.Llm.CheapProvider = "ollama";
        a.Llm.Ollama.Endpoint = "http://localhost:11434";
        Assert.Empty(ConfigCheck.Validate(mc, a));
        Assert.Empty(KeyCheck.AnthropicModels(a.Llm));
        Assert.Equal(3, KeyCheck.OllamaModels(a.Llm).Count);
        Assert.Empty(a.Llm.UsableProfiles()); // anthropic profiles need a key: never offered
    }

    [Fact]
    public void Ollama_embeddings_need_a_model_and_an_endpoint()
    {
        var (mc, a) = Main();
        a.Embeddings.Provider = "ollama";
        var p = ConfigCheck.Validate(mc, a);
        Assert.Contains(p, x => x.Contains("Agent:Embeddings:Model is empty"));
        Assert.Contains(p, x => x.Contains("neither Agent:Embeddings:Endpoint nor Agent:Llm:Ollama:Endpoint"));
        a.Embeddings.Model = "nomic-embed-text";
        a.Embeddings.Dimensions = 768;
        a.Llm.Ollama.Endpoint = "http://localhost:11434";
        Assert.Empty(ConfigCheck.Validate(mc, a));
    }

    [Fact]
    public async Task Startup_exits_2_when_a_tier_selects_ollama_without_an_endpoint()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mineclaw-ollama-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """
            { "Minecraft": { "Host": "localhost", "Username": "maid_chan" },
              "Agent": { "Chat": { "Username": "maid_chan" }, "Llm": { "ApiKey": "sk-ant-test", "WorkerProvider": "ollama", "Ollama": { "Endpoint": "" } } },
              "Hive": { "Enabled": false } }
            """);
        try
        {
            var code = await AppHost.RunAsync(AppCli.Parse(["--config", file, "--no-tui", "--no-key-check"]));
            Assert.Equal(ExitCodes.Config, code);
        }
        finally { File.Delete(file); }
    }

    private sealed class Server(string[] tags, string[] caps) : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Paths.Add(r.RequestUri!.AbsolutePath);
            object body = r.RequestUri.AbsolutePath.EndsWith("/api/tags") ? new { models = tags.Select(t => new { name = t }) } : new { capabilities = caps };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Startup_check_asks_tags_and_show_only_when_a_tier_uses_ollama()
    {
        var o = new LlmOptions();
        var idle = new Server([], []);
        Assert.True((await KeyCheck.RunOllamaAsync(o, new HttpClient(idle))).Ok);
        Assert.Empty(idle.Paths); // nothing on Ollama: no request at all

        o.WorkerProvider = "ollama";
        o.WorkerModel = "qwen3:8b";
        o.Ollama.Endpoint = "http://localhost:11434";
        var good = new Server(["qwen3:8b"], ["completion", "tools"]);
        var ok = await KeyCheck.RunOllamaAsync(o, new HttpClient(good));
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal(["/api/tags", "/api/show"], good.Paths);

        var missing = await KeyCheck.RunOllamaAsync(o, new HttpClient(new Server(["llama3.2:latest"], ["tools"])));
        Assert.False(missing.Ok);
        Assert.Contains("qwen3:8b", missing.Message);

        var toolless = await KeyCheck.RunOllamaAsync(o, new HttpClient(new Server(["qwen3:8b"], ["completion"])));
        Assert.False(toolless.Ok);
        Assert.Contains("can't call tools", toolless.Message);
    }

    [Fact]
    public async Task A_tool_less_ollama_profile_is_disabled_with_its_reason()
    {
        var o = new LlmOptions();
        o.Ollama.Endpoint = "http://localhost:11434";
        o.Profiles["local"] = new LlmProfile { Provider = "ollama", Model = "tinyllama" };
        Assert.Null(o.CheckProfile("local"));
        var notes = await KeyCheck.CheckOllamaProfilesAsync(o, new HttpClient(new Server(["tinyllama:latest"], ["completion"])));
        Assert.Single(notes);
        Assert.Contains("can't call tools", o.CheckProfile("local"));
    }
}
