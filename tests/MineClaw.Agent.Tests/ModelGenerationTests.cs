using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>User rule (2026-10-28): nothing runs on a Claude model older than the 5.5 generation.</summary>
public class ModelGenerationTests
{
    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-sonnet-4-5", false)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-opus-5", false)]
    [InlineData("claude-sonnet-5", false)]
    public void Only_the_5_5_generation_counts_as_current(string model, bool current) =>
        Assert.Equal(current, ModelProfiles.IsCurrentGeneration(model));

    [Fact]
    public void Defaults_are_current_generation_and_pass_validation()
    {
        var o = new LlmOptions();
        Assert.Empty(o.ValidateProviders());
        Assert.True(ModelProfiles.IsCurrentGeneration(o.MainModel));
        Assert.True(ModelProfiles.IsCurrentGeneration(o.WorkerModel));
        Assert.True(ModelProfiles.IsCurrentGeneration(o.CheapModel));
    }

    [Fact]
    public void An_older_anthropic_model_is_a_config_problem()
    {
        var o = new LlmOptions { WorkerModel = "claude-sonnet-4-5" };
        Assert.Contains(o.ValidateProviders(), p => p.Contains("WorkerModel") && p.Contains("5.5 generation"));
    }

    [Fact]
    public void Thinking_off_shapes_match_the_5_5_models()
    {
        Assert.Equal(ModelProfiles.ThinkingOffStyle.BetweenTools, ModelProfiles.OffStyle("claude-sonnet-5-5"));
        Assert.Equal(ModelProfiles.ThinkingOffStyle.Omit, ModelProfiles.OffStyle("claude-opus-5-5"));
        Assert.Equal(ModelProfiles.ThinkingOffStyle.Disabled, ModelProfiles.OffStyle("claude-haiku-5-5"));
    }
}
