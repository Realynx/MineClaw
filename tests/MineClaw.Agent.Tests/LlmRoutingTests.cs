using System.Text.Json;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using Microsoft.Extensions.Configuration;

namespace MineClaw.Agent.Tests;

/// <summary>Per-tier provider routing, profiles, pricing/budget and telemetry tags — and proof that empty config changes nothing.</summary>
public class LlmRoutingTests
{
    /// <summary>Records which "provider" got each request.</summary>
    private sealed class Tagged(string name, List<(string Provider, LlmRequest Request)> log) : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            lock (log) log.Add((name, request));
            return Task.FromResult(new LlmResponse([new LlmText("ok")], StopReasons.EndTurn, LlmUsage.Zero,
                request.Model ?? new LlmOptions().ModelFor(request.Tier)) { Provider = name });
        }
    }

    private static string RepoFile(string relative)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
        return Path.Combine(d!.FullName, relative);
    }

    [Fact]
    public void Empty_config_keeps_the_anthropic_path_untouched()
    {
        var o = new LlmOptions();
        Assert.IsType<AnthropicLlmClient>(LlmClientFactory.Create(o)); // the exact old client, unwrapped
        Assert.False(o.OllamaInUse);
        Assert.Empty(o.ValidateProviders());
        foreach (var t in new[] { ModelTier.Main, ModelTier.Worker, ModelTier.Cheap }) Assert.Equal(LlmProviders.Anthropic, o.ProviderFor(t));
        Assert.Null(ProfilePrompts.GuidanceLine(o)); // no API key here: no profile is usable, so no prompt line either

        // Prices of the Anthropic models and the expensive fallback are exactly as before.
        var prices = new PriceTable().RegisterProviders(o);
        Assert.Empty(prices.OllamaModels);
        var u = new LlmUsage(1_000_000, 1_000_000, 0, 0);
        Assert.Equal(24.00m, prices.Cost("claude-opus-5-5", u));
        Assert.Equal(30.00m, prices.Cost("some-typo-model", u));
        Assert.Equal(LlmProviders.Anthropic, prices.ProviderOf("claude-opus-5-5"));
    }

    [Fact]
    public void The_example_config_binds_with_ollama_empty_and_changes_nothing()
    {
        var cfg = new ConfigurationBuilder().AddJsonFile(RepoFile("src/MineClaw.App/appsettings.example.json")).Build();
        var a = new AgentOptions();
        cfg.GetSection("Agent").Bind(a);
        Assert.Equal("", a.Llm.Ollama.Endpoint);
        Assert.False(a.Llm.OllamaInUse);
        Assert.IsType<AnthropicLlmClient>(LlmClientFactory.Create(a.Llm));
        Assert.Empty(a.Llm.ValidateProviders());
        Assert.Equal("claude-opus-5-5", a.Llm.Profile("opus")!.Model); // built-ins survive binding the "local" entry
        Assert.Contains("local", a.Llm.Profiles.Keys);
        Assert.Contains("isn't configured", a.Llm.CheckProfile("local"));
        Assert.DoesNotContain("local", a.Llm.UsableProfiles());
        Assert.Equal("AzureOpenAI", a.Embeddings.Provider);
        Assert.Equal(0.13m, a.Embeddings.PricePerMTok);
    }

    [Fact]
    public async Task Each_tier_goes_to_its_provider_and_profiles_and_overrides_follow_the_model()
    {
        var o = new LlmOptions { MainProvider = "ollama", MainModel = "qwen3:8b", ApiKey = "sk-ant-test" };
        o.Ollama.Endpoint = "http://ollama.test:11434";
        o.Profiles["local"] = new LlmProfile { Provider = "ollama", Model = "llama3.2" };
        Assert.Empty(o.ValidateProviders());
        Assert.IsType<RoutingLlmClient>(LlmClientFactory.Create(o));

        var log = new List<(string Provider, LlmRequest Request)>();
        var router = new RoutingLlmClient(o, () => new Tagged("anthropic", log), () => new Tagged("ollama", log));
        await router.CompleteAsync(new LlmRequest { Tier = ModelTier.Main }, default);
        await router.CompleteAsync(new LlmRequest { Tier = ModelTier.Worker }, default);
        await router.CompleteAsync(new LlmRequest { Tier = ModelTier.Cheap }, default);
        await router.CompleteAsync(new LlmRequest { Tier = ModelTier.Main, Model = o.WorkerModel }, default);           // economy mode
        await router.CompleteAsync(new LlmRequest { Tier = ModelTier.Worker, Model = "llama3.2", Profile = "local" }, default); // delegate profile
        await router.CompleteAsync(new LlmRequest { Tier = ModelTier.Worker, Model = "claude-haiku-5-5", Profile = "haiku" }, default);
        Assert.Equal(["ollama", "anthropic", "anthropic", "anthropic", "ollama", "anthropic"], log.Select(l => l.Provider));
    }

    [Fact]
    public void Validation_catches_ollama_without_endpoint_and_bad_provider_names()
    {
        var o = new LlmOptions { WorkerProvider = "ollama", CheapProvider = "openai" };
        var p = o.ValidateProviders();
        Assert.Contains(p, x => x.Contains("WorkerProvider is ollama but Agent:Llm:Ollama:Endpoint is empty"));
        Assert.Contains(p, x => x.Contains("CheapProvider 'openai' is not a provider"));
        o.Ollama.Endpoint = "not a url";
        Assert.Contains(o.ValidateProviders(), x => x.Contains("not an absolute URL"));
    }

    [Fact]
    public void Profiles_are_checked_and_a_non_opus_brain_profile_loses_script_authorship()
    {
        var o = new LlmOptions { ApiKey = "sk-ant-test" };
        o.Profiles["local"] = new LlmProfile { Provider = "ollama", Model = "llama3.2" };
        o.Profiles["weird"] = new LlmProfile { Provider = "gemini", Model = "x" };
        Assert.Contains("no profile 'nope'", o.CheckProfile("nope"));
        Assert.Contains("needs Agent:Llm:Ollama:Endpoint", o.CheckProfile("local")); // unconfigured provider: never usable
        Assert.Contains("invalid provider", o.CheckProfile("weird"));
        Assert.Equal(["opus", "sonnet", "haiku"], o.UsableProfiles());
        Assert.Contains("unavailable", Disable(o));

        var brain = new LlmOptions { ApiKey = "sk-ant-test" };
        Assert.True(brain.BrainAuthorsScripts);
        Assert.Null(brain.ApplyBrainProfile("haiku"));
        Assert.Equal("claude-haiku-5-5", brain.ModelFor(ModelTier.Main));
        Assert.Equal("haiku", brain.MainProfile);
        Assert.False(brain.BrainAuthorsScripts);
        Assert.False(new Tools.ToolCallerInfo(ModelTier.Main, "claude-haiku-5-5").IsBrainModel(brain));
        Assert.Equal("haiku", brain.ProfileOfModel("claude-haiku-5-5"));

        var opus = new LlmOptions { ApiKey = "sk-ant-test" };
        Assert.Null(opus.ApplyBrainProfile("opus"));
        Assert.True(opus.BrainAuthorsScripts);
        Assert.Contains("no profile", new LlmOptions().ApplyBrainProfile("nope"));

        static string Disable(LlmOptions o)
        {
            o.DisableProfile("sonnet", "model missing");
            return o.CheckProfile("sonnet")!;
        }
    }

    [Fact]
    public void Ollama_calls_are_tracked_free_by_default_priced_when_configured_and_tagged_by_provider_and_profile()
    {
        var o = new LlmOptions { CheapProvider = "ollama", CheapModel = "llama3.2", ApiKey = "sk-ant-test" };
        o.Ollama.Endpoint = "http://ollama.test:11434";
        var prices = new PriceTable().RegisterProviders(o);
        var usage = new UsageTracker(new BudgetOptions { DailyCapUsd = 1 }, prices, path: null)
        {
            Classify = o.TierOfModel, ProfileOf = o.ProfileOfModel,
        };
        var big = new LlmUsage(5_000_000, 1_000_000, 0, 0);
        Assert.Equal(0m, usage.Record("llama3.2", big, ModelTier.Cheap));   // $0: no cap pressure
        Assert.Equal(BudgetMode.Normal, usage.Mode);
        decimal paid = usage.Record("claude-haiku-5-5", new LlmUsage(1_000_000, 0, 0, 0), ModelTier.Cheap);
        Assert.Equal(0.10m, paid);

        var today = usage.SnapshotTotals().Today;
        Assert.Equal(2, today.Calls);
        Assert.Equal(1, today.Providers!["ollama"].Calls);
        Assert.Equal(0m, today.Providers["ollama"].Usd);
        Assert.Equal(0.10m, today.Providers["anthropic"].Usd);
        Assert.Equal(1, today.Profiles!["haiku"].Calls);
        Assert.Contains("ollama 1c", usage.Summary());

        // A configured Ollama price counts toward the hard caps like any other spend.
        prices.Ollama = ModelPrice.Standard(0.20m, 0.40m);
        usage.Record("llama3.2", big, ModelTier.Cheap);
        Assert.Equal(BudgetMode.Paused, usage.Mode); // 5×0.20 + 1×0.40 = $1.40 ≥ $1 cap
    }

    [Fact]
    public async Task Telemetry_records_provider_and_profile()
    {
        var a = new AgentOptions();
        a.Llm.ApiKey = "sk-ant-test";
        var telemetry = new PromptTelemetry(a);
        var llm = telemetry.Wrap(new Tagged("anthropic", []));
        await llm.CompleteAsync(new LlmRequest { Tier = ModelTier.Worker, Model = "claude-haiku-5-5", Profile = "haiku", Purpose = "worker" }, default);
        Assert.Equal("haiku", telemetry.Last!.Profile);
        Assert.Equal("anthropic", telemetry.Last.Provider);
        await llm.CompleteAsync(new LlmRequest { Tier = ModelTier.Main, Purpose = "chat" }, default);
        Assert.Equal("opus", telemetry.Last!.Profile); // the default brain model matches the built-in opus profile
        var line = telemetry.Last.ToJsonLine();
        Assert.Contains("\"provider\":\"anthropic\"", line);
        Assert.Contains("\"profile\":\"opus\"", line);
    }

    [Fact]
    public async Task Delegate_with_a_profile_runs_the_worker_loop_on_that_model_and_rejects_bad_profiles()
    {
        var (rt, llm, _) = TestRuntime.Create(o => o.Llm.ApiKey = "sk-ant-test");
        var del = rt.Tools.Definitions(ModelTier.Main).Single(d => d.Name == "delegate");
        Assert.True(del.InputSchema.GetProperty("properties").TryGetProperty("profile", out _));
        Assert.Contains("pick the cheapest that fits", rt.Agent.SystemPrompt[0].Text);

        llm.Then(r => llm.Resp(r, StopReasons.EndTurn, new LlmText("ok: done")));
        var r = await rt.Tools.ExecuteAsync("delegate", JsonSerializer.SerializeToElement(new { task = "mine 4 cobble", done_when = "4 cobble", profile = "haiku" }), default);
        Assert.True(r.Ok, r.Text);
        var call = llm.Requests.Single();
        Assert.Equal(ModelTier.Worker, call.Tier);
        Assert.Equal("claude-haiku-5-5", call.Model);
        Assert.Equal("haiku", call.Profile);
        Assert.Equal(1, rt.Usage.SnapshotTotals().Today.Profiles!["haiku"].Calls); // budget sees the profile's spend

        var bad = await rt.Tools.ExecuteAsync("delegate", JsonSerializer.SerializeToElement(new { task = "x", done_when = "y", profile = "local" }), default);
        Assert.False(bad.Ok);
        Assert.Contains("no profile 'local'", bad.Text);
        Assert.Single(llm.Requests); // rejected before any call
    }

    [Fact]
    public void Without_a_usable_profile_the_delegate_schema_is_unchanged()
    {
        var (rt, _, _) = TestRuntime.Create();
        var del = rt.Tools.Definitions(ModelTier.Main).Single(d => d.Name == "delegate");
        Assert.False(del.InputSchema.GetProperty("properties").TryGetProperty("profile", out _));
        Assert.DoesNotContain("Model profiles", rt.Agent.SystemPrompt[0].Text);
    }
}
