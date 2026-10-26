using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tests;

public class EventQueueTests
{
    private static EventQueue Q(int coalesceMs = 30, int maxDelayMs = 200) =>
        new(new EventQueueOptions { CoalesceWindowMs = coalesceMs, MaxBatchDelayMs = maxDelayMs });

    [Fact]
    public void IdenticalPendingEvents_MergeWithCount()
    {
        var q = Q();
        q.Post(GameEvent.Chat("Alice", "clawd?"));
        q.Post(GameEvent.Chat("Alice", "clawd?"));
        q.Post(GameEvent.Chat("Alice", "clawd?"));
        var e = Assert.Single(q.Snapshot());
        Assert.Equal(3, e.Count);
        Assert.EndsWith("(x3)", e.Format());
    }

    [Fact]
    public void BackgroundOnly_DoesNotWake()
    {
        var q = Q();
        q.Post(GameEvent.Chat("Alice", "bob look at this", addressed: false));
        q.Post(GameEvent.Progress("mined 3/12"));
        Assert.False(q.HasWaking);
        Assert.Null(q.TryTakeBatch());
    }

    [Fact]
    public void IdleTick_DroppedWhenOtherEventsPending_AndRemovedByWakingEvent()
    {
        var q = Q();
        q.Post(GameEvent.Idle());
        q.Post(GameEvent.Chat("Alice", "hi clawd"));
        Assert.DoesNotContain(q.Snapshot(), e => e.Kind == EventKind.IdleTick);
        Assert.False(q.Post(GameEvent.Idle()));
    }

    [Fact]
    public void ProgressAndHealth_KeepOnlyLatest()
    {
        var q = Q();
        q.Post(GameEvent.Progress("1/10"));
        q.Post(GameEvent.Progress("5/10"));
        q.Post(GameEvent.LowHealth(8));
        q.Post(GameEvent.LowHealth(6));
        var snap = q.Snapshot();
        Assert.Equal("5/10", snap.Single(e => e.Kind == EventKind.TaskProgress).Text);
        Assert.Equal("hp 6/20", snap.Single(e => e.Kind == EventKind.LowHealth).Text);
    }

    [Fact]
    public void LowValueRepeat_AfterTake_IsDropped_ButCriticalIsNot()
    {
        var q = Q();
        q.Post(GameEvent.LowHealth(6));
        Assert.NotNull(q.TryTakeBatch());
        Assert.False(q.Post(GameEvent.LowHealth(5)));      // within dedupe window
        Assert.True(q.Post(GameEvent.Died("fell into the void")));
        Assert.True(q.Post(GameEvent.Chat("Alice", "u ok?"))); // chat is never dropped as repeat
    }

    [Fact]
    public void BackgroundChat_IsCapped()
    {
        var q = new EventQueue(new EventQueueOptions { MaxBackgroundChat = 3 });
        for (int i = 0; i < 10; i++) q.Post(GameEvent.Chat("Bob", "msg " + i, addressed: false));
        var bg = q.Snapshot();
        Assert.Equal(3, bg.Count);
        Assert.Equal("msg 9", bg[^1].Text);
    }

    [Fact]
    public async Task ChatBurst_CoalescesIntoOneBatch_AndFormatMerges()
    {
        var q = Q(coalesceMs: 80, maxDelayMs: 1000);
        var wait = q.WaitBatchAsync(new CancellationTokenSource(5000).Token);
        q.Post(GameEvent.Chat("Alice", "clawd"));
        await Task.Delay(20);
        q.Post(GameEvent.Chat("Alice", "can you"));
        await Task.Delay(20);
        q.Post(GameEvent.Chat("Alice", "get wood"));
        var batch = await wait;
        Assert.Equal(3, batch.Events.Count);
        Assert.Equal("[chat] Alice: clawd / can you / get wood", batch.Format());
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public async Task HighPriority_SkipsDebounce()
    {
        var q = Q(coalesceMs: 5000, maxDelayMs: 10000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var wait = q.WaitBatchAsync(new CancellationTokenSource(5000).Token);
        q.Post(GameEvent.VoidFall(-5));
        var batch = await wait;
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.True(batch.HasDanger);
    }

    [Fact]
    public void Overflow_DropsLowestPriorityOldest()
    {
        var q = new EventQueue(new EventQueueOptions { MaxPending = 3, MaxBackgroundChat = 10 });
        q.Post(GameEvent.Chat("Bob", "bg1", addressed: false));
        q.Post(GameEvent.Chat("Alice", "hi"));
        q.Post(GameEvent.TaskDone("done"));
        q.Post(GameEvent.Died());
        Assert.DoesNotContain(q.Snapshot(), e => e.Text == "bg1");
        Assert.Equal(3, q.Count);
    }

    [Fact]
    public void WhisperOnlyFrom_And_IsConversational()
    {
        var b = new EventBatch([GameEvent.Whisper("Alice", "psst"), GameEvent.Chat("Bob", "x", addressed: false)]);
        Assert.Equal("Alice", b.WhisperOnlyFrom);
        Assert.True(b.IsConversational);
        Assert.False(new EventBatch([GameEvent.TaskFail("x")]).IsConversational);
    }
}
