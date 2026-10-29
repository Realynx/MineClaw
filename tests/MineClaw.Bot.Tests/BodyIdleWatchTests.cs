using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Bodies;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-29, Fox: "I see a lot of standing around agents" (by 113): bodies whose chop came back "no tree within 24" stood there
/// and the hive's brain never heard. Idle bodies are now counted with why, and the brain gets ONE wake per idle stretch.
/// </summary>
public class BodyIdleWatchTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Bodies_idle_30s_wake_the_hive_once_with_the_count_and_the_reason_and_again_only_after_working()
    {
        var tools = new ToolRegistry(400);
        var events = new EventQueue();
        tools.Register("chop_tree", "chop", ToolSchema.Empty,
            (_, _) => Task.FromResult(ToolResult.Error("exhausted: no tree within 24; nearest beyond: oak 200,64,300 (40 m E)")));
        Assert.True(tools.TryGet("chop_tree", out var chop));
        var mc = new FakeGameClient { Username = "maid_chan_420" };
        var reg = new BodyRegistry(new Body(mc.Username, isMain: true, mc, mc, new ActivityManager(tools, events)));
        var fakes = new Dictionary<string, FakeGameClient>();
        foreach (var n in new[] { "maid_chan_101", "maid_chan_102", "maid_chan_113" })
        {
            var c = new FakeGameClient { Username = n };
            fakes[n] = c;
            reg.Add(new Body(n, isMain: false, c, c, new ActivityManager(tools, events)));
        }
        var clock = new Clock(DateTimeOffset.Parse("2026-10-29T14:42:00Z"));
        var watch = new BodyIdleWatch(reg, events, clock);
        int Wakes() => events.Snapshot().Count(e => e.MustWake && e.Text.StartsWith("[bodies]", StringComparison.Ordinal));

        watch.Tick();                                       // all three start an idle stretch now
        var body113 = reg.All.First(b => b.Name == "maid_chan_113");
        await body113.Activities.RunAsync([(chop!, JsonDocument.Parse("{}").RootElement)], TimeSpan.FromSeconds(5), CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(20);
        watch.Tick();
        Assert.Equal(0, Wakes());                           // not yet 30 s

        clock.Now += TimeSpan.FromSeconds(11);
        watch.Tick();
        Assert.Equal(1, Wakes());
        var wake = events.Snapshot().Single(e => e.MustWake && e.Text.StartsWith("[bodies]", StringComparison.Ordinal));
        Assert.Contains("3 of 3 bodies idle", wake.Text);
        Assert.Contains("101, 102, 113", wake.Text);
        Assert.Contains("exhausted: no tree within 24; nearest beyond: oak 200,64,300", wake.Text);
        Assert.Contains("idle 3 (101,102,113)", watch.Summary());

        clock.Now += TimeSpan.FromSeconds(30);
        watch.Tick();
        Assert.Equal(1, Wakes());                           // the same stretch: never twice

        fakes["maid_chan_102"].Safety = true;               // 102 works for a while (a reflex here)
        watch.Tick();
        fakes["maid_chan_102"].Safety = false;
        watch.Tick();
        clock.Now += TimeSpan.FromMinutes(2);
        watch.Tick();
        Assert.Equal(2, Wakes());                           // idle again after working: one new wake, for 102 only
        Assert.Contains("(102)", events.Snapshot().Where(e => e.MustWake).Last().Text);
    }
}
