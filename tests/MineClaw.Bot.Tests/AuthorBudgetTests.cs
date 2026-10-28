using MineClaw.Agent.Llm;
using MineClaw.Bot.Blueprints;

namespace MineClaw.Bot.Tests;

/// <summary>The blueprint author is an authoring call: 100K output (from Agent:Llm:AuthorMaxTokens), a USD cap, $3 per design.</summary>
public class AuthorBudgetTests
{
    private sealed class Capture(bool cap = false) : ILlmClient
    {
        public LlmRequest? Last;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Last = request;
            if (cap) throw new LlmCostCapException(new LlmUsage(500, 120_000, 0, 0), request.Model!, 2.4m, request.MaxUsd ?? 0);
            return Task.FromResult(new LlmResponse([new LlmText("```\nblueprint x\n```")], StopReasons.EndTurn, new LlmUsage(1, 1, 0, 0), request.Model!));
        }
    }

    [Fact]
    public async Task Blueprint_author_gets_the_author_budget_and_a_cost_cap()
    {
        var o = new BlueprintOptions();
        Assert.Equal(100_000, o.AuthorMaxTokens);
        Assert.True(o.MaxUsdPerDesign >= 3.00m);
        var llm = new Capture();
        var turn = await new LlmBlueprintAuthor(llm, new LlmOptions(), null, o).WriteAsync([LlmMessage.User("a hut")], CancellationToken.None);
        Assert.Equal(100_000, llm.Last!.MaxTokens);
        Assert.Equal(3.00m, llm.Last.MaxUsd);
        Assert.NotNull(llm.Last.CostOf);
        Assert.Contains("blueprint x", turn.Text);

        // The Llm value caps (and defaults) the blueprint's.
        Assert.Equal(50_000, LlmBlueprintAuthor.MaxTokens(o, new LlmOptions { AuthorMaxTokens = 50_000 }));
        Assert.Equal(100_000, LlmBlueprintAuthor.MaxTokens(new BlueprintOptions { AuthorMaxTokens = 0 }, new LlmOptions()));
    }

    [Fact]
    public async Task A_capped_author_call_returns_its_partial_usage_cleanly()
    {
        var turn = await new LlmBlueprintAuthor(new Capture(cap: true), new LlmOptions(), null, new BlueprintOptions())
            .WriteAsync([LlmMessage.User("a castle")], CancellationToken.None);
        Assert.Equal("", turn.Text);
        Assert.Equal("cost_cap", turn.StopReason);
        Assert.Equal(120_000, turn.Usage.OutputTokens);
        Assert.Equal(2.4m, turn.Usd);
    }
}
