using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

public sealed class StoryTimeRangeTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData(0, 12000, 11999, false)]
    [InlineData(0, 12000, 12000, true)]
    [InlineData(0, 12000, 18000, true)]
    [InlineData(18000, 6000, 23999, false)]
    [InlineData(18000, 6000, 24000, false)]
    [InlineData(18000, 6000, 30000, true)]
    public async Task Clock_runs_inside_the_arc_and_returns_to_start_at_or_past_the_end(int start, int end, long tick, bool reset)
    {
        var sent = new List<string>();
        using var control = new StoryTimeControl(() => [new("main", true, true)], (_, line, _) =>
        { sent.Add(line); return Task.FromResult<string?>(null); });
        Assert.True(await control.SetRangeAsync(new(start, end)));
        await control.TickAsync(tick);
        Assert.Equal(reset ? [$"/time set {start}"] : Array.Empty<string>(), sent);
    }

    [Fact]
    public async Task Range_survives_reload_and_disabling_it_works_without_an_op()
    {
        using var db = AgentDb.Open(null);
        var store = new StoryTimeRangeStore(db);
        using (var first = new StoryTimeControl(() => [new("main", true, true)], (_, _, _) => Task.FromResult<string?>(null), store: store))
            Assert.True(await first.SetRangeAsync(new(1000, 11000)));
        var sends = 0;
        using var reloaded = new StoryTimeControl(() => [], (_, _, _) => { sends++; return Task.FromResult<string?>(null); }, store: store);
        Assert.Equal(new StoryTimeRange(1000, 11000), reloaded.Range);
        await reloaded.TickAsync(18000);
        Assert.Equal(0, sends);
        Assert.True(await reloaded.SetRangeAsync(null));
        Assert.Null(store.Load());
        await reloaded.TickAsync(18000);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task Failed_resets_retry_with_the_existing_rate_limit_and_stop_after_disable()
    {
        var clock = new Clock();
        var calls = 0;
        using var control = new StoryTimeControl(() => [new("main", true, true)], (_, _, _) =>
        { calls++; return Task.FromResult<string?>(calls == 1 ? "disconnected" : null); }, clock);
        await control.SetRangeAsync(new(0, 12000));
        await control.TickAsync(12020);
        await control.TickAsync(12020);
        Assert.Equal(1, calls);
        clock.Now += StoryTimeControl.MinGap;
        await control.TickAsync(12040);
        Assert.Equal(2, calls);
        await control.SetRangeAsync(null);
        clock.Now += StoryTimeControl.MinGap;
        await control.TickAsync(12060);
        Assert.Equal(2, calls);
    }
}
