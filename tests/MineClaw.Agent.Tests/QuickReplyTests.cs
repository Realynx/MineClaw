using System.Diagnostics;
using System.Text.Json;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Two-phase replies ("respond quickly and then respond conclusively"): streamed brain chat, the quick cheap-tier ack,
/// cheap-only small talk, the correction path and the first_chat / conclusive telemetry.
/// </summary>
public class QuickReplyTests
{
    private const string Ack = "on it master~";

    private static (AgentRuntime Rt, FakeLlm Llm, List<string> Sent) Create(Action<Brainstem.ReplyOptions>? reply = null)
    {
        var (rt, llm, sent) = TestRuntime.Create(o =>
        {
            o.Brainstem.Reply.AckAfterMs = 300;
            o.Brainstem.Reply.AckCallLeadMs = 100;
            reply?.Invoke(o.Brainstem.Reply);
        });
        llm.QuickHandler = (r, _) => Task.FromResult(Haiku(r.Purpose == BotAgent.AckPurpose ? Ack : """{"needs_brain":true,"say":"hm, lemme see"}"""));
        return (rt, llm, sent);
    }

    private static LlmResponse Haiku(string text) => new([new LlmText(text)], StopReasons.EndTurn, new LlmUsage(60, 6, 0, 0), "claude-haiku-5-5");

    private static LlmResponse Opus(LlmRequest r, string text, params LlmBlock[] more) =>
        new([new LlmText(text), .. more], more.Length > 0 ? StopReasons.ToolUse : StopReasons.EndTurn, new LlmUsage(100, 20, 1000, 0), "claude-opus-5-5");

    private static EventBatch Chat(string text, string player = "kiwi") => new([GameEvent.Chat(player, text)]);

    private static List<string> Snap(List<string> s) { lock (s) return s.ToList(); }

    private static async Task<bool> Until(Func<bool> cond, int ms = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            await Task.Delay(10);
        }
        return cond();
    }

    private static string AllUserText(LlmRequest r) =>
        string.Join("\n", r.Messages.Where(m => m.Role == LlmRole.User).SelectMany(m => m.Content.OfType<LlmText>()).Select(t => t.Text));

    private static string LastUserText(LlmRequest r) => string.Concat(r.Messages[^1].Content.OfType<LlmText>().Select(t => t.Text));

    private static int Acks(FakeLlm llm) { lock (llm.QuickRequests) return llm.QuickRequests.Count(q => q.Purpose == BotAgent.AckPurpose); }

    // ------------------------------------------------------------------ quick ack

    [Fact]
    public async Task Ack_fires_when_the_brain_is_slow_then_the_conclusion_follows()
    {
        var (rt, llm, sent) = Create();
        llm.ThenAsync(async (r, _) =>
        {
            Assert.True(await Until(() => Snap(sent).Contains(Ack))); // the ack lands while Opus is still thinking
            return Opus(r, "the chest by the gen has 12 cobble for you master");
        });

        var outcome = await rt.Agent.HandleBatchAsync(Chat("clawd mine some cobble for me"), default);

        Assert.Equal([Ack, "the chest by the gen has 12 cobble for you master"], Snap(sent));
        Assert.Equal(1, Acks(llm));
        var ack = llm.QuickRequests.Single();
        Assert.Equal(ModelTier.Cheap, ack.Tier);
        Assert.Contains("clawd mine some cobble", LastUserText(ack));
        Assert.True(ack.System.Sum(s => s.Text.Length) < 1500); // a tiny prompt: persona + the line
        Assert.Equal(Ack, outcome.Said[0]);
    }

    [Fact]
    public async Task No_ack_when_the_brain_streams_a_line_in_time()
    {
        var (rt, llm, sent) = Create();
        llm.ThenAsync(async (r, _) =>
        {
            Assert.NotNull(r.OnTextDelta);
            r.OnTextDelta!("hai robin, lemme look! ");
            await Task.Delay(700); // well past the ack deadline
            r.OnTextDelta!("the gen is busy rn");
            return Opus(r, "hai robin, lemme look! the gen is busy rn");
        });

        await rt.Agent.HandleBatchAsync(Chat("clawd mine some cobble for me"), default);
        await Task.Delay(200);

        Assert.Equal(0, Acks(llm));
        Assert.Equal(["hai robin, lemme look!", "the gen is busy rn"], Snap(sent));
    }

    [Fact]
    public async Task One_ack_per_message_and_the_next_round_sees_the_note()
    {
        var (rt, llm, sent) = Create();
        rt.Tools.Register("count_blocks", "Count blocks.", ToolSchema.Create().Str("block").Build(),
            (input, _) => Task.FromResult(ToolResult.Success("12 cobblestone")));
        llm.ThenAsync(async (r, _) =>
        {
            Assert.True(await Until(() => Snap(sent).Contains(Ack)));
            return new LlmResponse([new LlmToolUse("tu_1", "count_blocks", JsonSerializer.SerializeToElement(new { block = "cobblestone" }))],
                StopReasons.ToolUse, new LlmUsage(100, 20, 1000, 0), "claude-opus-5-5");
        });
        llm.ThenAsync(async (r, _) =>
        {
            await Task.Delay(700); // slow again: still no second ack
            return Opus(r, "you have 12 cobble in total master");
        });

        await rt.Agent.HandleBatchAsync(Chat("clawd how much cobble do we have"), default);

        Assert.Equal(1, Acks(llm));
        Assert.Equal(1, Snap(sent).Count(l => l == Ack));
        var note = string.Format(BotAgent.AckNoteFormat, Ack);
        Assert.DoesNotContain(note, AllUserText(llm.Requests[0]));
        Assert.Contains(note, LastUserText(llm.Requests[1])); // newest user message = uncached tail
        Assert.DoesNotContain(note, string.Concat(llm.Requests[1].Messages[0].Content.OfType<LlmText>().Select(t => t.Text)));
        Assert.Equal([Ack, "you have 12 cobble in total master"], Snap(sent));
    }

    [Fact]
    public async Task Ack_failing_the_lint_means_silence()
    {
        var (rt, llm, sent) = Create();
        llm.QuickHandler = (_, _) => Task.FromResult(Haiku("yes! got 12 cobble at 4,64,6"));
        llm.ThenAsync(async (r, _) => { await Task.Delay(800); return Opus(r, "omw to the gen"); });

        await rt.Agent.HandleBatchAsync(Chat("clawd mine some cobble"), default);

        Assert.Equal(1, Acks(llm));
        Assert.Equal(["omw to the gen"], Snap(sent));
    }

    [Fact]
    public async Task No_ack_for_a_reflex_or_when_paused_off()
    {
        var (rt, llm, sent) = Create(r => r.QuickAck = false);
        llm.ThenAsync(async (r, _) => { await Task.Delay(600); return Opus(r, "ok!"); });
        await rt.Agent.HandleBatchAsync(Chat("clawd mine some cobble"), default);
        Assert.Equal(0, Acks(llm));

        var (rt2, llm2, _) = Create();
        llm2.ThenAsync(async (r, _) => { await Task.Delay(600); return Opus(r, "(silent)"); });
        await rt2.Agent.HandleBatchAsync(Chat("stop"), default); // a reflex shape: the body already answered
        Assert.Equal(0, Acks(llm2));
    }

    [Theory]
    [InlineData("on it master~")]
    [InlineData("omw~")]
    [InlineData("which chest?")]
    [InlineData("hm, lemme see")]
    [InlineData("ooh lemme check uwu")]
    public void Ack_lint_accepts_short_noncommittal_lines(string ack) => Assert.Equal(ack, QuickReply.LintAck(ack));

    [Theory]
    [InlineData("got 12 cobble!")]                       // digits / counts
    [InlineData("its at 4 64 6")]                       // coordinates
    [InlineData("yes master")]                          // yes/no commitment
    [InlineData("nope, can't")]
    [InlineData("done already~")]                       // claims
    [InlineData("i'll bring a stack")]                  // promise
    [InlineData("there's a chest by the gen")]          // a fact
    [InlineData("ooh lemme check this out real quick")] // more than 5 words
    [InlineData("hmmmmmm lemme seeeee what we gottt")]  // more than 30 chars
    [InlineData("/tell kiwi hi")]
    [InlineData("")]
    public void Ack_lint_rejects_facts_numbers_claims_and_long_lines(string ack) => Assert.Null(QuickReply.LintAck(ack));

    // ------------------------------------------------------------------ streamed chat

    [Fact]
    public async Task Streamed_lines_go_out_before_the_turn_ends_in_order_and_never_twice()
    {
        var (rt, llm, sent) = Create(r => r.QuickAck = false);
        bool early = false, secondEarly = false;
        const string second = "the left chest by the gen has what you need, i'll grab it for you.";
        llm.ThenAsync(async (r, _) =>
        {
            r.OnTextDelta!("omw ");
            r.OnTextDelta!("master~ ");
            early = await Until(() => Snap(sent).Contains("omw master~"));
            foreach (var w in second.Split(' ')) r.OnTextDelta!(w + " ");
            secondEarly = await Until(() => Snap(sent).Contains(second)); // a sentence end after 60+ chars flushes too
            return Opus(r, "omw master~ " + second);
        });

        await rt.Agent.HandleBatchAsync(Chat("clawd bring me the stuff from the chest"), default);

        Assert.True(early);
        Assert.True(secondEarly);
        Assert.Equal(["omw master~", second], Snap(sent)); // in order, and the final message sent nothing again
    }

    [Fact]
    public async Task Streamed_lines_keep_the_filters_meta_and_repeats()
    {
        var (rt, llm, sent) = Create(r => r.QuickAck = false);
        llm.ThenAsync((r, _) =>
        {
            r.OnTextDelta!("Delegating the craft.\n");
            r.OnTextDelta!("okie robin, crafting your door now~\n");
            r.OnTextDelta!("Okie robin, crafting the door now~\n");
            return Task.FromResult(Opus(r, "Delegating the craft.\nokie robin, crafting your door now~\nOkie robin, crafting the door now~"));
        });

        await rt.Agent.HandleBatchAsync(Chat("clawd make me a door"), default);

        Assert.Equal(["okie robin, crafting your door now~"], Snap(sent));
    }

    [Fact]
    public async Task A_streamed_call_is_not_cut_for_new_chat()
    {
        var (rt, llm, sent) = Create(r => r.QuickAck = false);
        bool cancelled = false;
        llm.ThenAsync(async (r, ct) =>
        {
            Assert.True(r.Interruptible);
            r.OnTextDelta!("hai! ");
            Assert.True(await Until(() => Snap(sent).Contains("hai!")));
            rt.Events.Post(GameEvent.Chat("kiwi", "also bring wood"));
            await Task.Delay(200, CancellationToken.None);
            cancelled = ct.IsCancellationRequested;
            r.OnTextDelta!("lemme see");
            return Opus(r, "hai! lemme see");
        });

        await rt.Agent.HandleBatchAsync(Chat("clawd mine the gen"), default);

        Assert.False(cancelled);
        Assert.Single(llm.Requests); // no restart: the new line waits for the next turn / round boundary
        Assert.Equal(["hai!", "lemme see"], Snap(sent));
    }

    [Fact]
    public async Task A_call_cut_before_any_line_went_out_sends_nothing_of_it()
    {
        var (rt, llm, sent) = Create(r => r.QuickAck = false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        llm.ThenAsync(async (r, ct) =>
        {
            r.OnTextDelta!("partial wor");
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw new LlmInterruptedException(new LlmUsage(10, 3, 0, 0), "claude-opus-5-5"); }
            throw new InvalidOperationException("unreachable");
        });
        llm.ThenAsync((r, _) => Task.FromResult(Opus(r, "fresh answer for both lines")));

        var turn = rt.Agent.HandleBatchAsync(Chat("clawd mine the gen"), default);
        await started.Task;
        rt.Events.Post(GameEvent.Chat("kiwi", "actually the left cell"));
        await turn;

        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(["fresh answer for both lines"], Snap(sent));
    }

    // ------------------------------------------------------------------ cheap-only small talk

    [Fact]
    public async Task Small_talk_is_answered_once_by_the_cheap_tier_without_opus()
    {
        var (rt, llm, sent) = Create();
        llm.QuickHandler = (_, _) => Task.FromResult(Haiku("""{"needs_brain":false,"say":"haiii robin~"}"""));

        var outcome = await rt.Agent.HandleBatchAsync(Chat("hi clawd"), default);

        Assert.Empty(llm.Requests); // no Opus call
        Assert.Equal(ModelTier.Cheap, outcome.Tier);
        Assert.Equal(["haiii robin~"], Snap(sent));
        var q = Assert.Single(llm.QuickRequests);
        Assert.Equal(BotAgent.QuickReplyPurpose, q.Purpose);
        Assert.Equal(ModelTier.Cheap, q.Tier);
        // The exchange lands in history/episodes so the brain knows what was said.
        var ep = rt.Context.Episodes.Last();
        Assert.Contains(BotAgent.QuickReplyTag, ep.Messages[0].Text);
        Assert.Equal("haiii robin~", ep.Messages[1].Text);
        Assert.Equal((1, 0), rt.Latency.CheapCounts());
    }

    [Fact]
    public async Task Cheap_tier_saying_needs_brain_hands_its_ack_to_the_brain_turn()
    {
        var (rt, llm, sent) = Create(r => r.AckAfterMs = 200);
        llm.QuickHandler = (_, _) => Task.FromResult(Haiku("""{"needs_brain":true,"say":"ooh which one?"}"""));
        llm.ThenAsync(async (r, _) =>
        {
            Assert.True(await Until(() => Snap(sent).Contains("ooh which one?")));
            return Opus(r, "you're the cutest robin on the island");
        });

        await rt.Agent.HandleBatchAsync(Chat("clawd good girl"), default);

        Assert.Single(llm.QuickRequests); // the same single cheap call wrote the ack
        Assert.Equal(["ooh which one?", "you're the cutest robin on the island"], Snap(sent));
    }

    [Fact]
    public async Task Cheap_final_failing_the_lint_goes_to_the_brain()
    {
        var (rt, llm, sent) = Create();
        llm.QuickHandler = (_, _) => Task.FromResult(Haiku("""{"needs_brain":false,"say":"hai! i'll bring you 5 logs"}"""));
        llm.ThenText("haii robin");

        await rt.Agent.HandleBatchAsync(Chat("hey clawd"), default);

        Assert.Single(llm.Requests);
        Assert.Equal(["haii robin"], Snap(sent));
    }

    [Fact]
    public async Task An_action_request_gets_a_short_ack_then_the_opus_conclusion()
    {
        var (rt, llm, sent) = Create();
        llm.ThenAsync(async (r, _) =>
        {
            Assert.True(await Until(() => Snap(sent).Count > 0));
            return Opus(r, "found a bucket in the west chest, bringing it to you master");
        });

        await rt.Agent.HandleBatchAsync(Chat("clawd go bring me a bucket"), default);

        var lines = Snap(sent);
        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5);
        Assert.True(lines[0].Length <= 30);
        Assert.DoesNotContain(llm.QuickRequests, q => q.Purpose == BotAgent.QuickReplyPurpose); // never triaged as small talk
        Assert.Single(llm.Requests);
    }

    [Theory]
    [InlineData("where is the chest?")]
    [InlineData("do you remember the house")]
    [InlineData("what do we have in the chest")]
    [InlineData("how many cobble do you have")]
    [InlineData("hi, can you get me wood")]
    [InlineData("thanks, now mine the gen")]
    [InlineData("ok")]
    [InlineData("yes")]
    [InlineData("come here")]
    [InlineData("hi 67")]
    public void World_memory_and_action_lines_never_go_cheap_only(string line) =>
        Assert.Equal(QuickKind.Brain, QuickReply.Classify([line], ["Clawd_bot", "Clawd"]));

    [Theory]
    [InlineData("hi clawd", QuickKind.SmallTalk)]
    [InlineData("MAID CHAN", QuickKind.SmallTalk)]
    [InlineData("good girls", QuickKind.SmallTalk)]
    [InlineData("thanks!! <3", QuickKind.SmallTalk)]
    [InlineData("lmao", QuickKind.SmallTalk)]
    [InlineData("hey clawd, how are you?", QuickKind.Status)]
    [InlineData("what are you doing", QuickKind.Status)]
    public void Safe_small_talk_is_a_cheap_candidate(string line, QuickKind kind) =>
        Assert.Equal(kind, QuickReply.Classify([line], ["Clawd_bot", "Clawd", "maid chan"]));

    [Fact]
    public async Task A_memory_question_in_a_run_never_goes_cheap_only()
    {
        var (rt, llm, sent) = Create(r => r.QuickAck = false);
        llm.ThenText("the house is the big plank one by the gen");

        await rt.Agent.HandleBatchAsync(Chat("clawd do you remember where the house is?"), default);

        Assert.Empty(llm.QuickRequests);
        Assert.Single(llm.Requests);
    }

    [Fact]
    public async Task A_correction_after_a_cheap_reply_escalates_and_counts_a_miss()
    {
        var (rt, llm, sent) = Create(r => r.QuickAck = false);
        llm.QuickHandler = (_, _) => Task.FromResult(Haiku("""{"needs_brain":false,"say":"haii, just chilling~"}"""));
        await rt.Agent.HandleBatchAsync(Chat("what are you doing clawd"), default);
        Assert.Empty(llm.Requests);

        llm.ThenText("oh! sorry robin, omw to the chest");
        await rt.Agent.HandleBatchAsync(Chat("no, i meant what are you doing with the chest"), default);

        Assert.Single(llm.Requests);
        Assert.Contains("missed", LastUserText(llm.Requests[0]));
        Assert.Contains("haii, just chilling~", string.Join(" | ", llm.Requests[0].Messages.Select(m => m.Text))); // the cheap exchange is in the brain's history
        Assert.Equal((1, 1), rt.Latency.CheapCounts());
        Assert.Contains("1 miss", rt.Latency.Summary().Line());
    }

    // ------------------------------------------------------------------ telemetry

    [Fact]
    public async Task First_chat_and_conclusive_are_measured_separately()
    {
        var (rt, llm, sent) = Create();
        var records = new List<Telemetry.LatencyRecord>();
        rt.Latency.Recorded += records.Add;
        llm.ThenAsync(async (r, _) =>
        {
            Assert.True(await Until(() => Snap(sent).Contains(Ack)));
            await Task.Delay(150);
            return Opus(r, "the gen has plenty, mining now master");
        });

        rt.Events.Post(GameEvent.Chat("kiwi", "clawd mine some cobble"));
        var batch = await rt.Events.WaitBatchAsync(default);
        await rt.Agent.HandleBatchAsync(batch, default);

        var rec = Assert.Single(records);
        Assert.True(rec.Ack);
        Assert.NotNull(rec.FirstChatMs);
        Assert.NotNull(rec.ConclusiveMs);
        Assert.True(rec.FirstChatMs < rec.ConclusiveMs);
        Assert.Contains("chat ", rt.Latency.Summary().Line());
        Assert.Contains("first_chat_ms", Telemetry.LatencyJsonlWriter.Serialize(rec));
        Assert.Contains("conclusive_ms", Telemetry.LatencyJsonlWriter.Serialize(rec));
    }
}
