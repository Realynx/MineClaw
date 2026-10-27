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
        var ctx = new ContextManager(new ContextOptions { CompactAtTokens = 400, CompactToTokens = 200, KeepFullEpisodes = 1 });
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
        var ctx = new ContextManager(new ContextOptions { CompactAtTokens = 300, CompactToTokens = 150, JournalMaxChars = 200 });
        for (int i = 0; i < 6; i++) ctx.Commit(ToolEpisode(i));
        await ctx.CompactAsync((_, _) => throw new HttpRequestException("down"), default);
        Assert.True(ctx.Journal.Length is > 0 and <= 200);
        AssertPairsIntact(ctx.BuildMessages(ModelTier.Main, []));
    }

    [Fact]
    public async Task Commit_IsAppendOnly_TruncationOnlyAtCompaction()
    {
        var opts = new ContextOptions { KeepFullEpisodes = 1, OldToolResultChars = 50, CompactAtTokens = 100_000, CompactToTokens = 100_000 };
        var ctx = new ContextManager(opts);
        for (int i = 1; i <= 3; i++) ctx.Commit(ToolEpisode(i));
        var eps = ctx.Episodes;
        Assert.DoesNotContain(eps.SelectMany(e => e.Messages).SelectMany(m => m.Content), b => b is LlmThinking);
        Assert.All(eps, e => Assert.Equal(300, e.Messages[2].ToolResults.First().Content.Length)); // history untouched turn to turn
        Assert.Equal([0L, 1L, 2L], eps.Select(e => e.Seq));

        // One compaction: fold exactly the oldest episode, then (only now) truncate older survivors.
        opts.CompactToTokens = ctx.EstimateTokens() - eps[0].MainTokens;
        Assert.Equal(1, await ctx.CompactAsync((_, _) => Task.FromResult("j"), default));
        eps = ctx.Episodes;
        Assert.Equal(2, eps.Count);
        Assert.True(eps[0].Messages[2].ToolResults.First().Content.Length <= 50);  // older survivor: truncated
        Assert.Equal(300, eps[1].Messages[2].ToolResults.First().Content.Length);  // newest: full
        Assert.Equal(1, ctx.OldestSeq);
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
    public void CheapTier_GetsAppendOnlyTextTranscript_NoToolBlocks()
    {
        var ctx = new ContextManager();
        ctx.Commit(ToolEpisode(1));
        var msgs = ctx.BuildMessages(ModelTier.Cheap, [LlmMessage.User("pos\n[chat] Bob: hi")]);
        Assert.Equal(3, msgs.Count);
        Assert.DoesNotContain(msgs.SelectMany(m => m.Content), b => b is LlmToolUse or LlmToolResult);
        Assert.Contains("[chat] Alice: job 1", msgs[0].Text);
        Assert.Contains("[did] mine(cobblestone) = rrr", msgs[0].Text);
        Assert.DoesNotContain("pos 0,64,0", msgs[0].Text);          // state header is per-turn only
        Assert.Equal("ok done with job 1", msgs[1].Text);
        Assert.Contains("[chat] Bob: hi", msgs[2].Text);

        // Next turn: the earlier messages are identical (cache prefix holds).
        ctx.Commit(ToolEpisode(2));
        var next = ctx.BuildMessages(ModelTier.Cheap, [LlmMessage.User("pos\n[chat] Bob: again")]);
        Assert.Equal(msgs[0].Text, next[0].Text);
        Assert.Equal(msgs[1].Text, next[1].Text);
    }
}
