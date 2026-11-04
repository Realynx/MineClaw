using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>A body's recent "no route" answers aren't searched again until something near changes or 15 s pass.</summary>
public sealed class NoRouteCacheTests
{
    [Fact]
    public void The_same_failed_search_is_answered_from_the_cache_until_a_block_changes_near_it_or_it_expires()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var cache = new NoRouteCache(() => now);
        var start = new BlockPos(0, 64, 0);
        var goal = new GoalBlock(218, 64, 400);
        var key = NoRouteCache.Key(start, goal, new GotoOptions());
        Assert.Null(cache.Get(key));
        cache.Put(key, start, goal, "no path to the chest");
        Assert.Equal("no path to the chest", cache.Get(key));
        Assert.Equal("no path to the chest", cache.Get(key));
        Assert.Equal(2, cache.Hits);
        Assert.Null(cache.Get(NoRouteCache.Key(new BlockPos(1, 64, 0), goal, new GotoOptions()))); // another start: searched
        Assert.Null(cache.Get(NoRouteCache.Key(start, goal, new GotoOptions { AllowPlace = true }))); // more freedom: searched
        cache.Changed(new BlockPos(100, 64, 100)); // far from both ends: kept
        Assert.NotNull(cache.Get(key));
        cache.Changed(new BlockPos(217, 64, 401)); // a door opened by the chest: searched again
        Assert.Null(cache.Get(key));
        cache.Put(key, start, goal, "no path");
        now += NoRouteCache.Ttl + TimeSpan.FromSeconds(1);
        Assert.Null(cache.Get(key)); // expired
    }
}
