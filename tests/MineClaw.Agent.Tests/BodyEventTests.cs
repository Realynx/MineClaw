using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>One brain, many bodies, step 0: events and telemetry carry the body; the queue never merges two bodies' events.</summary>
public class BodyEventTests
{
    private static EventQueue Q() => new(new EventQueueOptions { CoalesceWindowMs = 30, MaxBatchDelayMs = 200 });

    [Fact]
    public void Body_is_null_by_default_and_unprefixed()
    {
        var e = GameEvent.TaskDone("#3 mine: 64 cobble");
        Assert.Null(e.Body);
        Assert.Equal("[task ok] #3 mine: 64 cobble", e.Format()); // Format is unchanged in step 0
    }

    [Fact]
    public void Same_event_from_two_bodies_stays_two_events()
    {
        var q = Q();
        q.Post(GameEvent.TaskDone("#1 mine: ok") with { Body = "maid_chan_67" });
        q.Post(GameEvent.TaskDone("#1 mine: ok") with { Body = "maid_chan_69" });
        q.Post(GameEvent.TaskDone("#1 mine: ok") with { Body = "maid_chan_67" }); // same body: merged (x2)
        var pending = q.Snapshot();
        Assert.Equal(2, pending.Count);
        Assert.Equal(2, pending.Single(p => p.Body == "maid_chan_67").Count);
        Assert.Equal(1, pending.Single(p => p.Body == "maid_chan_69").Count);
    }

    [Fact]
    public void Body_match_ignores_case_and_null_keeps_the_old_merge()
    {
        var q = Q();
        q.Post(GameEvent.Notice("chest full") with { Body = "MAID_CHAN_67" });
        q.Post(GameEvent.Notice("chest full") with { Body = "maid_chan_67" });
        q.Post(GameEvent.Notice("chest full"));
        q.Post(GameEvent.Notice("chest full"));
        var pending = q.Snapshot();
        Assert.Equal(2, pending.Count);
        Assert.All(pending, p => Assert.Equal(2, p.Count));
    }

    [Fact]
    public void Health_and_progress_keep_the_newest_per_body()
    {
        var q = Q();
        q.Post(GameEvent.LowHealth(6) with { Body = "maid_chan_67" });
        q.Post(GameEvent.LowHealth(5) with { Body = "maid_chan_69" });
        q.Post(GameEvent.LowHealth(4) with { Body = "maid_chan_67" });
        q.Post(GameEvent.Progress("mined 3/12") with { Body = "maid_chan_67" });
        q.Post(GameEvent.Progress("mined 9/12") with { Body = "maid_chan_69" });
        var pending = q.Snapshot();
        Assert.Equal(["hp 4/20", "hp 5/20"], pending.Where(p => p.Kind == EventKind.LowHealth).Select(p => p.Text).OrderBy(t => t));
        Assert.Equal(2, pending.Count(p => p.Kind == EventKind.TaskProgress));
    }

    [Fact]
    public void Repeat_drop_after_take_is_per_body()
    {
        var q = Q();
        q.Post(GameEvent.Notice("hungry", EventPriority.Normal) with { Body = "maid_chan_67" });
        Assert.NotNull(q.TryTakeBatch());
        // 67's repeat inside the dedupe window is dropped; the same line from 69 is news.
        Assert.False(q.Post(GameEvent.Notice("hungry", EventPriority.Normal) with { Body = "maid_chan_67" }));
        Assert.True(q.Post(GameEvent.Notice("hungry", EventPriority.Normal) with { Body = "maid_chan_69" }));
        // Health warnings: one per window per body.
        q.Post(GameEvent.LowHealth(6) with { Body = "maid_chan_67" });
        Assert.NotNull(q.TryTakeBatch());
        Assert.False(q.Post(GameEvent.LowHealth(5) with { Body = "maid_chan_67" }));
        Assert.True(q.Post(GameEvent.LowHealth(5) with { Body = "maid_chan_69" }));
    }

    [Fact]
    public void Chat_repeat_key_includes_the_body()
    {
        var a = GameEvent.Whisper("Fox", "come here") with { Body = "maid_chan_67" };
        var b = GameEvent.Whisper("Fox", "come here") with { Body = "maid_chan_69" };
        Assert.NotEqual(EventQueue.ChatKey(a), EventQueue.ChatKey(b));
        Assert.NotEqual(EventQueue.Key(a), EventQueue.Key(b));
        Assert.Equal(EventQueue.Key(a), EventQueue.Key(a with { Body = "Maid_Chan_67" }));
        Assert.NotEqual(EventQueue.RepeatKey(GameEvent.LowHealth(4) with { Body = "maid_chan_67" }), EventQueue.RepeatKey(GameEvent.LowHealth(4)));
    }

    [Fact]
    public void Telemetry_records_the_body_in_scope()
    {
        var tel = new PromptTelemetry(new AgentOptions { KnowledgeDirectory = null });
        var fake = new FakeLlm();
        fake.ThenText("a").ThenText("b");
        var llm = tel.Wrap(fake);
        string? body = null;
        tel.CurrentBody = () => body;
        llm.CompleteAsync(new LlmRequest { Tier = ModelTier.Main, Messages = [LlmMessage.User("hi")] }, default).GetAwaiter().GetResult();
        Assert.Null(tel.Last!.Body); // brain-level
        Assert.DoesNotContain("\"body\"", tel.Last.ToJsonLine());
        body = "maid_chan_67";
        llm.CompleteAsync(new LlmRequest { Tier = ModelTier.Worker, Messages = [LlmMessage.User("mine")] }, default).GetAwaiter().GetResult();
        Assert.Equal("maid_chan_67", tel.Last!.Body);
        Assert.Contains("\"body\":\"maid_chan_67\"", tel.Last.ToJsonLine());
        Assert.Equal("maid_chan_67", PromptUsageRecord.FromJsonLine(tel.Last.ToJsonLine())!.Body);
    }

    [Fact]
    public void Interrupt_cancels_only_the_events_body()
    {
        var (rt, _, _) = TestRuntime.Create();
        using var _r = rt;
        var other = new ActivityManager(rt.Tools, rt.Events);
        rt.Agent.BodyActivities = n => n == "maid_chan_67" ? other : null;
        Assert.Same(other, rt.Agent.ActivitiesFor("maid_chan_67"));
        Assert.Same(rt.Activities, rt.Agent.ActivitiesFor(null));
        Assert.Same(rt.Activities, rt.Agent.ActivitiesFor("nobody"));

        rt.Tools.Register("hold", "hold", ToolSchema.Empty, async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return ToolResult.Error("cancelled");
        }, longRunning: true);
        Assert.True(rt.Tools.TryGet("hold", out var hold));
        _ = rt.Activities.RunAsync([(hold!, ToolSchema.Empty)], TimeSpan.Zero, default);
        _ = other.RunAsync([(hold!, ToolSchema.Empty)], TimeSpan.Zero, default);
        SpinWait.SpinUntil(() => rt.Activities.IsBusy && other.IsBusy, 2000);

        // 67's danger interrupts 67 only; main keeps working.
        rt.Events.Post(GameEvent.LowHealth(3) with { Body = "maid_chan_67" });
        SpinWait.SpinUntil(() => !other.IsBusy, 2000);
        Assert.False(other.IsBusy);
        Assert.True(rt.Activities.IsBusy);
        rt.Activities.Cancel("stop");
    }
}
