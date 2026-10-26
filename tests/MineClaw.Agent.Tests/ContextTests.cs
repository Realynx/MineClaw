using System.Text.Json;
using MineClaw.Agent.Context;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

public class ContextTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private static Episode ToolEpisode(int n, int resultChars = 300)
    {
        var e = new Episode(ModelTier.Main, DateTimeOffset.UtcNow);
        e.Messages.Add(LlmMessage.User($"pos 0,64,0\n[chat] Alice: job {n}"));
        e.Messages.Add(LlmMessage.Assistant(new LlmThinking("hmm", "sig"), new LlmText("ok"), new LlmToolUse($"a{n}", "mine", J(new { block = "cobblestone" })),
            new LlmToolUse($"b{n}", "look", J(new { }))));
        e.Messages.Add(LlmMessage.User(new LlmToolResult($"a{n}", new string('r', resultChars)), new LlmToolResult($"b{n}", "grass")));
        e.Messages.Add(LlmMessage.Assistant($"done with job {n}"));
        return e;
    }

    private static void AssertPairsIntact(IReadOnlyList<LlmMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var uses = messages[i].ToolUses.Select(u => u.Id).ToList();
            if (uses.Count > 0)
            {
                Assert.True(i + 1 < messages.Count, "tool_use without following message");
                Assert.Equal(LlmRole.User, messages[i + 1].Role);
                Assert.Equal(uses.OrderBy(x => x), messages[i + 1].ToolResults.Select(r => r.ToolUseId).OrderBy(x => x));
            }
            var results = messages[i].ToolResults.Select(r => r.ToolUseId).ToList();
            if (results.Count > 0)
            {
                Assert.True(i > 0);
                Assert.True(results.All(id => messages[i - 1].ToolUses.Any(u => u.Id == id)), "orphan tool_result");
            }
            if (i > 0) Assert.NotEqual(messages[i - 1].Role, messages[i].Role);
        }
    }

    [Fact]
    public async Task Compaction_FoldsWholeEpisodes_KeepsPairsIntact()
    {
        var ctx = new ContextManager(new ContextOptions { HistoryTokenBudget = 400, KeepFullEpisodes = 1 });
        for (int i = 0; i < 8; i++) ctx.Commit(ToolEpisode(i));
        Assert.True(ctx.NeedsCompaction);

        string? prompt = null;
        int folded = await ctx.CompactAsync((p, _) => { prompt = p; return Task.FromResult("Alice gave jobs 0-5; all done"); }, default);

        Assert.True(folded > 0);
        Assert.False(ctx.NeedsCompaction);
        Assert.Equal("Alice gave jobs 0-5; all done", ctx.Journal);
        Assert.Contains("did mine(cobblestone)", prompt);
        Assert.Contains("[chat] Alice: job 0", prompt);

        var current = new List<LlmMessage> { LlmMessage.User("pos\n[chat] Alice: next") };
        var msgs = ctx.BuildMessages(ModelTier.Main, current);
        AssertPairsIntact(msgs);
        Assert.StartsWith("[journal]", ((LlmText)msgs[0].Content[0]).Text);
        Assert.Equal(LlmRole.User, msgs[^1].Role);
    }

    [Fact]
    public async Task Compaction_FallsBackToMechanicalDigest()
    {
        var ctx = new ContextManager(new ContextOptions { HistoryTokenBudget = 300, JournalMaxChars = 200 });
        for (int i = 0; i < 6; i++) ctx.Commit(ToolEpisode(i));
        await ctx.CompactAsync((_, _) => throw new HttpRequestException("down"), default);
        Assert.True(ctx.Journal.Length is > 0 and <= 200);
        AssertPairsIntact(ctx.BuildMessages(ModelTier.Main, []));
    }

    [Fact]
    public void Commit_StripsThinking_AndTruncatesOldResults()
    {
        var ctx = new ContextManager(new ContextOptions { KeepFullEpisodes = 1, OldToolResultChars = 50, HistoryTokenBudget = 100_000 });
        ctx.Commit(ToolEpisode(1));
        ctx.Commit(ToolEpisode(2));
        var eps = ctx.Episodes;
        Assert.DoesNotContain(eps.SelectMany(e => e.Messages).SelectMany(m => m.Content), b => b is LlmThinking);
        Assert.True(eps[0].Messages[2].ToolResults.First().Content.Length <= 50);  // old: truncated
        Assert.Equal(300, eps[1].Messages[2].ToolResults.First().Content.Length);  // newest: full
    }

    [Fact]
    public void Seal_AnswersDanglingToolUses_AndEndsWithAssistant()
    {
        var msgs = new List<LlmMessage>
        {
            LlmMessage.User("go"),
            LlmMessage.Assistant(new LlmToolUse("x", "mine", J(new { })), new LlmToolUse("y", "look", J(new { }))),
            LlmMessage.User(new LlmToolResult("x", "ok")),
            LlmMessage.Assistant(new LlmToolUse("z", "goto", J(new { }))),
        };
        ContextManager.Seal(msgs);
        AssertPairsIntact(msgs);
        Assert.Equal(LlmRole.Assistant, msgs[^1].Role);
        Assert.True(msgs[2].ToolResults.Single(r => r.ToolUseId == "y").IsError);
    }

    [Fact]
    public void CheapTier_GetsFlattenedDigest_NoToolBlocks()
    {
        var ctx = new ContextManager();
        ctx.Commit(ToolEpisode(1));
        var msgs = ctx.BuildMessages(ModelTier.Cheap, [LlmMessage.User("pos\n[chat] Bob: hi")]);
        var m = Assert.Single(msgs);
        Assert.DoesNotContain(m.Content, b => b is LlmToolUse or LlmToolResult);
        Assert.Contains("[recent]", m.Text);
        Assert.Contains("did mine", m.Text);
        Assert.Contains("[chat] Bob: hi", m.Text);
    }
}
