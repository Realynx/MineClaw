using System.Text.Json;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

public class CompactionSummaryTests
{
    private const string OriginalJournal = "Alice still needs a bridge; do not spend her diamonds.";
    private const string RejectedText = "Incomplete checkpoint: all promises fulfilled";
    private const string MechanicalLabel = "[mechanical fallback; lossy]";

    public static IEnumerable<object[]> RejectedSummaries()
    {
        foreach (bool hardLimit in new[] { false, true })
        {
            foreach (string stop in new[] { StopReasons.MaxTokens, StopReasons.Refusal, StopReasons.PauseTurn, StopReasons.ToolUse, "unknown" })
                yield return [stop, RejectedText, false, hardLimit];
            yield return [StopReasons.EndTurn, "   ", false, hardLimit];
            yield return [StopReasons.EndTurn, RejectedText, true, hardLimit];
            yield return [StopReasons.MaxTokens, RejectedText, true, hardLimit];
        }
    }

    [Theory]
    [MemberData(nameof(RejectedSummaries))]
    public async Task RejectedSummary_RetainsHistoryWhenItFits_OtherwiseUsesLabelledDigest(
        string stop, string text, bool hasTool, bool hardLimit)
    {
        var (rt, llm, _) = TestRuntime.Create(o =>
        {
            o.Context.CompactAtTokens = 1;
            o.Context.CompactToTokens = 1;
            o.Context.JournalMaxChars = 200;
        });
        using (rt)
        {
            Seed(rt.Context);
            var original = rt.Context.Episodes.ToArray();
            int compacted = 0;
            rt.Context.Compacted += (_, _) => compacted++;
            llm.Fallback = r =>
            {
                if (r.Purpose != "compact") return llm.Resp(r, StopReasons.EndTurn, new LlmText("turn complete"));
                // Tighten the history ceiling at the summary boundary, after request admission.
                if (hardLimit) rt.Context.Options.MaxTokens = 1;
                LlmBlock[] blocks = hasTool
                    ? [new LlmText(text), new LlmToolUse("unexpected", "look", JsonSerializer.SerializeToElement(new { }))]
                    : [new LlmText(text)];
                return llm.Resp(r, stop, blocks);
            };

            await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("step")]), default);

            var request = Assert.Single(llm.Requests, r => r.Purpose == "compact");
            Assert.Equal(500, request.MaxTokens);
            Assert.Equal(ModelTier.Cheap, request.Tier);
            Assert.False(request.CacheHistory);
            Assert.Empty(request.Tools);
            Assert.Equal(1, rt.Usage.Today.Tier(ModelTier.Cheap).Calls);
            Assert.DoesNotContain(RejectedText, rt.Context.Journal);
            if (hardLimit)
            {
                Assert.Equal(1, compacted);
                Assert.Single(rt.Context.Episodes);
                Assert.StartsWith(MechanicalLabel, rt.Context.Journal);
                Assert.True(rt.Context.Journal.Length <= 200);
                Assert.Contains("did mine", rt.Context.Journal);
            }
            else
            {
                Assert.Equal(0, compacted);
                Assert.Equal(OriginalJournal, rt.Context.Journal);
                Assert.Equal(4, rt.Context.Episodes.Count);
                Assert.Equal(original, rt.Context.Episodes.Take(3));
                Assert.All(original, e => Assert.Equal(300, e.Messages[2].ToolResults.Single().Content.Length));
            }
            AssertPairs(rt.Context.BuildMessages(ModelTier.Main, []));

            // A completed summary still follows the original success path, without a higher output cap.
            rt.Context.Options.MaxTokens = 100_000;
            llm.Fallback = r => llm.Resp(r, StopReasons.EndTurn,
                new LlmText(r.Purpose == "compact" ? "Complete journal: bridge still owed to Alice." : "turn complete"));
            await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("next step")]), default);
            Assert.Equal("Complete journal: bridge still owed to Alice.", rt.Context.Journal);
            Assert.Single(rt.Context.Episodes);
            Assert.Equal(hardLimit ? 2 : 1, compacted);
            Assert.All(llm.Requests.Where(r => r.Purpose == "compact"), r => Assert.Equal(500, r.MaxTokens));
            AssertPairs(rt.Context.BuildMessages(ModelTier.Main, []));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryCancellation_DoesNotCommitCheckpointOrFoldEpisodes(bool providerThrows)
    {
        var (rt, llm, _) = TestRuntime.Create(o =>
        {
            o.Context.CompactAtTokens = 1;
            o.Context.CompactToTokens = 1;
        });
        using (rt)
        using (var cts = new CancellationTokenSource())
        {
            Seed(rt.Context);
            var original = rt.Context.Episodes.ToArray();
            int compacted = 0;
            rt.Context.Compacted += (_, _) => compacted++;
            llm.ThenText("turn complete").ThenAsync((r, ct) =>
            {
                Assert.Equal("compact", r.Purpose);
                if (providerThrows) throw new OperationCanceledException("provider cancelled");
                cts.Cancel();
                return Task.FromResult(llm.Resp(r, StopReasons.MaxTokens, new LlmText(RejectedText)));
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("step")]), cts.Token));

            Assert.Equal(OriginalJournal, rt.Context.Journal);
            Assert.Equal(4, rt.Context.Episodes.Count);
            Assert.Equal(original, rt.Context.Episodes.Take(3));
            Assert.All(original, e => Assert.Equal(300, e.Messages[2].ToolResults.Single().Content.Length));
            Assert.Equal(0, compacted);
        }
    }

    private static void Seed(ContextManager context)
    {
        context.SetJournal(OriginalJournal);
        for (int i = 0; i < 3; i++)
        {
            var e = new Episode(ModelTier.Main, DateTimeOffset.UtcNow);
            e.Messages.Add(LlmMessage.User($"pos 0,64,0\n[chat] Alice: bridge job {i}"));
            e.Messages.Add(LlmMessage.Assistant(new LlmToolUse($"mine{i}", "mine", JsonSerializer.SerializeToElement(new { block = "stone" }))));
            e.Messages.Add(LlmMessage.User(new LlmToolResult($"mine{i}", new string('r', 300))));
            e.Messages.Add(LlmMessage.Assistant($"collected stone for bridge {i}"));
            context.Commit(e);
        }
    }

    private static void AssertPairs(IReadOnlyList<LlmMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].ToolUses.Any())
            {
                Assert.True(i + 1 < messages.Count);
                Assert.Equal(messages[i].ToolUses.Select(u => u.Id), messages[i + 1].ToolResults.Select(r => r.ToolUseId));
            }
            foreach (var result in messages[i].ToolResults)
            {
                Assert.True(i > 0);
                Assert.Contains(messages[i - 1].ToolUses, u => u.Id == result.ToolUseId);
            }
        }
    }
}
