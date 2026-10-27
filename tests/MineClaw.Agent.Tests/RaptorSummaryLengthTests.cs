using MineClaw.Agent.Rag.Raptor;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Night review 10-27 B23: 178 of 305 RAPTOR summaries stopped at max_tokens = 150 (summarization_length 100 × 1.5), so the
/// tree held trimmed or cut-off sentences. The headroom is now 2.5 (250 tokens) and the trim keeps quoted/bracketed sentence
/// ends and never leaves half a word behind.
/// </summary>
public class RaptorSummaryLengthTests
{
    [Fact]
    public void Headroom_lets_a_typical_haiku_summary_finish()
    {
        var s = new HaikuSummarizer(new FakeLlm(), new MineClaw.Agent.Llm.LlmOptions());
        Assert.True(100 * s.MaxTokensHeadroom >= 250);
    }

    [Theory]
    [InlineData("The wheat farm at -1,64,-22 has melon stems. Fox said \"no wheat here.\" The next", "The wheat farm at -1,64,-22 has melon stems. Fox said \"no wheat here.\"")]
    [InlineData("Chest 14,64,-5 holds the spare picks (two stone pickaxes.) Then 69 craf", "Chest 14,64,-5 holds the spare picks (two stone pickaxes.)")]
    [InlineData("Cobblestone makes furnaces and tools. This one is cut", "Cobblestone makes furnaces and tools.")]
    [InlineData("maid chan mines the dual cobble generator cell at 6,64,8 standing at 7,63,8 and stores cobble in the chest by the do", "maid chan mines the dual cobble generator cell at 6,64,8 standing at 7,63,8 and stores cobble in the chest by the…")]
    public void Trim_keeps_whole_sentences_and_whole_words(string cut, string want) =>
        Assert.Equal(want, HaikuSummarizer.TrimToSentence(cut));
}
