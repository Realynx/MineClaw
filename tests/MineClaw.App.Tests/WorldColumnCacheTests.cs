using MineClaw.App.Supervisor;
using MineClaw.World;

namespace MineClaw.App.Tests;

public sealed class WorldColumnCacheTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static WorldState World(int id = 4)
    {
        var world = new WorldState { Dimension = 0 };
        world.Light.Enabled = false;
        world.ApplyChunkRegion(0, 64, 0, 1, 1, 1, [(byte)id], [0], [0], [15]);
        return world;
    }

    private static long Hash(WorldColumnSource source, bool light = true) => source.Index(0, 0, 0, 0, 0, 1, light).Single().Hash;

    [Fact]
    public void Unchanged_columns_are_not_rehashed_after_a_quiet_period_but_new_versions_are_seen_immediately()
    {
        var clock = new Clock();
        var world = World();
        var source = new WorldColumnSource(() => [new("main", "main", true, world)], () => null, clock);
        long plain = Hash(source, false), lit = Hash(source);
        Assert.Equal(2, source.HashComputations);
        clock.Now += TimeSpan.FromHours(2);
        Assert.Equal(plain, Hash(source, false));
        Assert.Equal(lit, Hash(source));
        Assert.Equal(2, source.HashComputations); // a large static view must not periodically walk every column's bytes again
        world.SetBlock(0, 64, 0, 5, 1);
        Assert.NotEqual(plain, Hash(source, false));
        Assert.NotEqual(lit, Hash(source));
        Assert.Equal(4, source.HashComputations);
    }

    [Theory]
    [InlineData("block")]
    [InlineData("metadata")]
    [InlineData("bulk")]
    [InlineData("light")]
    [InlineData("biomes")]
    public void Every_world_mutation_invalidates_the_index_and_payload_without_waiting_for_a_ttl(string mutation)
    {
        var world = World();
        var source = new WorldColumnSource(() => [new("main", "main", true, world)], () => null);
        long before = Hash(source);
        var first = source.Columns(0, [(0, 0)], light: true).Single();
        Assert.Equal(before, first.Hash);
        switch (mutation)
        {
            case "block": world.SetBlock(0, 64, 0, 5, 0); break;
            case "metadata": world.SetBlock(0, 64, 0, 4, 1); break;
            case "bulk": world.ApplyChunkRegion(0, 64, 0, 1, 2, 1, [4, 3], [0, 0], [0, 0], [15, 15]); break;
            case "light": world.ApplyChunkRegion(0, 64, 0, 1, 1, 1, [4], [0], [7], [11]); break;
            case "biomes": world.SetBiomes(0, 0, Enumerable.Repeat((byte)2, 256).ToArray()); break;
        }
        long after = Hash(source);
        Assert.NotEqual(before, after);
        var payload = source.Columns(0, [(0, 0)], light: true).Single();
        Assert.Equal(after, payload.Hash);
        Assert.Equal(after, LightSnapshot.Combine(ChunkSnapshot.Decode(payload.Data).Hash, LightSnapshot.Decode(payload.Light!).Hash));
        Assert.Equal(2, source.HashComputations);
    }

    [Fact]
    public void Wide_indexes_keep_main_first_precedence_and_reselect_after_unload_reload_or_dimension_change()
    {
        var main = World(4);
        var other = World(5);
        other.ApplyChunkRegion(16, 64, 16, 1, 1, 1, [3], [0], [0], [15]);
        var source = new WorldColumnSource(() => [new("main", "main", true, main), new("pawn", "pawn", false, other)], () => null);
        WorldColumnInfo AtOrigin() => source.Index(0, -16, -16, 16, 16, 1089, light: true).Single(c => c.Cx == 0 && c.Cz == 0);
        var first = AtOrigin();
        Assert.Equal("main", first.By);
        Assert.Equal(2, source.Index(0, -16, -16, 16, 16, 1089).Count);
        main.UnloadColumn(0, 0);
        var fallback = AtOrigin();
        Assert.Equal("pawn", fallback.By);
        Assert.NotEqual(first.Hash, fallback.Hash);
        main.ApplyChunkRegion(0, 64, 0, 1, 1, 1, [2], [0], [0], [15]);
        var reloaded = AtOrigin();
        Assert.Equal("main", reloaded.By);
        Assert.NotEqual(first.Hash, reloaded.Hash);
        Assert.Equal(reloaded.Hash, source.Columns(0, [(0, 0)], light: true).Single().Hash);
        main.Dimension = -1;
        Assert.Equal("pawn", AtOrigin().By);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Small_views_do_not_scan_the_whole_hive_when_the_main_has_few_or_no_loaded_columns(bool mainLoaded)
    {
        var main = mainLoaded ? World(4) : new WorldState { Dimension = 0 };
        var first = World(5);
        var second = World(3);
        for (int cx = 3; cx < 67; cx++)
        {
            first.ApplyChunkRegion(cx * 16, 64, 0, 1, 1, 1, [5], [0], [0], [15]);
            second.ApplyChunkRegion(cx * 16, 64, 16, 1, 1, 1, [3], [0], [0], [15]);
        }
        var source = new WorldColumnSource(() => [new("main", "main", true, main),
            new("first", "first", false, first), new("second", "second", false, second)], () => null);
        var small = Assert.Single(source.Index(0, -1, -1, 1, 1, 9));
        Assert.Equal(mainLoaded ? "main" : "first", small.By);
        Assert.Equal(0, source.MergedColumnVisits); // nine cells must not enumerate every distant pawn column

        var wide = source.Index(0, -1, -1, 67, 2, 276);
        Assert.Equal(129, wide.Count);
        Assert.Equal(small.Hash, wide.Single(c => c.Cx == 0 && c.Cz == 0).Hash);
        Assert.Equal(130 + (mainLoaded ? 1 : 0), source.MergedColumnVisits); // a wide view still uses the bounded merge
    }
}
