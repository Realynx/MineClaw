using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 2026-10-27 15:55:37: <c>Main 4 call(s) $0.4194 [in 14 out 482 cr 113035 cw 77425]</c>. The usage JSONL showed both
/// cold calls reading exactly tools + system (8,978) with ~36K of history and no compaction; the latency JSONL showed the turn
/// was a simple chat line (low effort) right after planning turns (medium). The message cache is per effort variant, and the
/// last low call (15:52:13) was 3 min old but ~40 content blocks back (an 8-round medium task turn in between), past the API's
/// 20-block lookback, so the whole history was re-written, twice (the interrupted call and its restart).
/// </summary>
public class LiveSession4CacheTests
{
    private static (AgentRuntime Rt, FakeLlm Llm, ManualTime Time, ListLoggers Logs) Create()
    {
        var time = new ManualTime { Now = new DateTimeOffset(2026, 10, 27, 15, 52, 4, TimeSpan.Zero) };
        var options = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0 };
        options.Chat.Username = "maid_chan";
        options.Chat.DisplayName = "maid";
        options.Chat.Aliases = [];
        options.Chat.LineDelayMs = 0;
        options.Events.CoalesceWindowMs = 10;
        options.Events.MaxBatchDelayMs = 50;
        options.ChatDebounce.QuietMs = 10;
        options.ChatDebounce.MaxMs = 50;
        options.ChatDebounce.ReplyQuietMs = 0;
        options.ChatDebounce.RestartOnNewChat = false;
        options.Brainstem.Reply.AckAfterMs = 0;
        var llm = new FakeLlm();
        var logs = new ListLoggers();
        var rt = AgentRuntime.Create(options, (_, _) => Task.CompletedTask, llm, persist: false, time: time, loggers: logs);
        return (rt, llm, time, logs);
    }

    private static EventBatch Chat(AgentRuntime rt, string text, string player = "kiwi")
    {
        rt.Events.Post(GameEvent.Chat(player, text));
        return rt.Events.TryTakeBatch()!;
    }

    /// <summary>~36K tokens of history, like the live session (no compaction: the threshold is far above this in these tests).</summary>
    private static void FillHistory(AgentRuntime rt, int episodes, int chars = 4_000)
    {
        for (int i = 0; i < episodes; i++)
        {
            var e = new Episode(ModelTier.Main, DateTimeOffset.UtcNow);
            e.Messages.Add(LlmMessage.User($"[chat] kiwi: line {i} " + new string('x', chars)));
            e.Messages.Add(LlmMessage.Assistant("ok " + i));
            rt.Context.Commit(e);
        }
    }

    [Fact]
    public async Task Simple_chat_after_a_long_medium_task_turn_stays_on_the_warm_variant_instead_of_rewriting_history()
    {
        var (rt, llm, time, _) = Create();
        rt.Context.Options.CompactAtTokens = 1_000_000; // replay the live window: history big, no compaction
        FillHistory(rt, 30);
        llm.Fallback = r => llm.Resp(r, StopReasons.EndTurn, new LlmText("ok"));

        await rt.Agent.HandleBatchAsync(Chat(rt, "go back and mine cobble"), default);   // 15:52:04 simple → low
        Assert.Equal("low", llm.Requests[^1].Effort);
        time.Advance(TimeSpan.FromSeconds(10));
        await rt.Agent.HandleBatchAsync(Chat(rt, "until night, then come back and tell me what you found"), default); // planning → configured
        Assert.Null(llm.Requests[^1].Effort);
        // 15:52:42 the 8-round task turn (medium): ~40 blocks of history the low variant never saw.
        time.Advance(TimeSpan.FromSeconds(28));
        for (int i = 0; i < 7; i++) llm.ThenTool("no_such_tool", new { n = i }, id: $"tu_{i}");
        llm.ThenText("done");
        rt.Events.Post(GameEvent.TaskDone("delegate: still in the nether at 2,66,1"));
        await rt.Agent.HandleBatchAsync(rt.Events.TryTakeBatch()!, default);
        Assert.Null(llm.Requests[^1].Effort);
        time.Advance(TimeSpan.FromSeconds(120));
        await rt.Agent.HandleBatchAsync(Chat(rt, "come here"), default);    // 15:55:18 simple ("you're mining the floor / STOP")
        Assert.Null(llm.Requests[^1].Effort); // stayed on the warm variant (was "low" → 38K re-written twice)
    }

    [Fact]
    public async Task Simple_chat_still_switches_to_low_while_the_low_cache_is_warm()
    {
        var (rt, llm, time, _) = Create();
        rt.Context.Options.CompactAtTokens = 1_000_000;
        FillHistory(rt, 30);
        llm.Fallback = r => llm.Resp(r, StopReasons.EndTurn, new LlmText("ok"));
        await rt.Agent.HandleBatchAsync(Chat(rt, "go back and mine cobble"), default);
        time.Advance(TimeSpan.FromSeconds(10));
        await rt.Agent.HandleBatchAsync(Chat(rt, "until night, then come back and tell me what you found"), default);
        time.Advance(TimeSpan.FromSeconds(30));
        // 15:48:03 live: a few blocks after the last low call, the low cache was read (cr 31420): switching is free.
        await rt.Agent.HandleBatchAsync(Chat(rt, "breads on the floor"), default);
        Assert.Equal("low", llm.Requests[^1].Effort);
    }

    [Fact]
    public void Warmth_replays_the_live_cache_reads_by_ttl_and_block_lookback()
    {
        var w = new CacheWarmth();
        var t0 = new DateTimeOffset(2026, 10, 27, 15, 45, 33, TimeSpan.Zero);
        string low = CacheWarmth.Key("opus", "low", null), med = CacheWarmth.Key("opus", null, null);
        w.Note(low, t0, 300);                                       // 15:45:33 low ("she can try")
        w.Note(med, t0.AddSeconds(77), 308);                        // 15:46:50 medium
        Assert.Null(w.ColdReason(low, t0.AddSeconds(150), 312));    // 15:48:03 low read 31K: few blocks, 2.5 min
        w.Note(low, new DateTimeOffset(2026, 10, 27, 15, 52, 13, TimeSpan.Zero), 360);
        w.Note(med, new DateTimeOffset(2026, 10, 27, 15, 55, 8, TimeSpan.Zero), 402);
        var at = new DateTimeOffset(2026, 10, 27, 15, 55, 22, TimeSpan.Zero);
        var why = w.ColdReason(low, at, 404);                       // 15:55:22 low read only 8,978
        Assert.NotNull(why);
        Assert.Contains("44 blocks back", why);
        Assert.True(w.ShouldStay(low, downgrade: true, historyTokens: 36_000, at, 404));
        Assert.False(w.ShouldStay(low, downgrade: true, historyTokens: 3_000, at, 404)); // small history: a cold switch is cheap
        Assert.False(w.ShouldStay(med, downgrade: false, historyTokens: 36_000, at, 404));
    }

    [Fact]
    public async Task A_call_that_writes_over_30K_cache_tokens_logs_a_warning_with_its_cause()
    {
        var (rt, llm, time, logs) = Create();
        llm.UsagePerCall = new LlmUsage(4, 146, 8_978, 38_290);
        llm.Fallback = r => llm.Resp(r, StopReasons.EndTurn, new LlmText("ok"));
        await rt.Agent.HandleBatchAsync(Chat(rt, "come here"), default);
        var warn = logs.Lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Text).ToList();
        Assert.Contains(warn, l => l.Contains("cache write spike") && l.Contains("wrote 38290") && l.Contains("first call"));

        llm.UsagePerCall = new LlmUsage(4, 20, 47_000, 300);
        time.Advance(TimeSpan.FromSeconds(30));
        logs.Lines.Clear();
        await rt.Agent.HandleBatchAsync(Chat(rt, "ok thanks"), default);
        Assert.DoesNotContain(logs.Lines, l => l.Text.Contains("cache write spike"));
    }

    [Fact]
    public void Guard_names_the_variant_switch_ttl_and_compaction()
    {
        var now = new DateTimeOffset(2026, 10, 27, 15, 55, 22, TimeSpan.Zero);
        string low = CacheWarmth.Key("opus", "low", null), med = CacheWarmth.Key("opus", null, null);
        Assert.Contains("variant switched configured effort on opus -> low on opus",
            CacheWriteGuard.Cause(med, low, "last used 3m ago but 44 blocks back (the API looks back 20)", now.AddSeconds(-14), now, false));
        Assert.Contains("TTL expired: no call for 10 min", CacheWriteGuard.Cause(low, low, "last used 10m ago (5m TTL)", now.AddMinutes(-10), now, false));
        Assert.Contains("compacted", CacheWriteGuard.Cause(low, low, null, now, now, compacted: true));
        Assert.Null(CacheWriteGuard.Check(new LlmUsage(4, 10, 47_000, 30_000), "opus", 0, "x"));
        Assert.NotNull(CacheWriteGuard.Check(new LlmUsage(4, 10, 8_978, 30_001), "opus", 0, "x"));
    }

    [Fact]
    public void Compaction_thresholds_use_the_calibrated_token_scale()
    {
        var ctx = new ContextManager(new ContextOptions { CompactAtTokens = 30_000, CompactToTokens = 20_000, CheapCompactAtTokens = 1_000_000, CheapCompactToTokens = 1_000_000 });
        for (int i = 0; i < 12; i++)
        {
            var e = new Episode(ModelTier.Main, DateTimeOffset.UtcNow);
            e.Messages.Add(LlmMessage.User(new string('x', 7_000))); // ≈2,000 estimated tokens each: 24K estimated in all
            e.Messages.Add(LlmMessage.Assistant("ok"));
            ctx.Commit(e);
        }
        Assert.False(ctx.NeedsCompaction);
        ctx.TokenScale = 1.57; // live: 47,272 real / 30,193 estimated
        Assert.True(ctx.NeedsCompaction);
    }

    internal sealed class ListLoggers : ILoggerFactory
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new L(this);
        public void Dispose() { }

        private sealed class L(ListLoggers owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Lines) owner.Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
