using System.Text.Json;
using MineClaw.App.Supervisor;
using MineClaw.World;
using MineClaw.World.Chunks;

namespace MineClaw.App.Tests;

/// <summary>
/// The browser viewer's live block feed (<see cref="BlockDeltaLog"/>, <c>/api/world/blocks</c>): it downloads each column once and
/// then patches single blocks, so every change must arrive once, in rev order, and a client that fell behind must be told to
/// refetch by index hash.
/// </summary>
public class BlockDeltaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static void Load(WorldState w, int cx, int cz, int sizeChunks = 1, byte fill = 0)
    {
        int h = w.WorldHeight, n = 16 * sizeChunks;
        var ids = new byte[n * h * n];
        if (fill != 0) for (int i = 0; i < ids.Length; i += 3) ids[i] = fill; // a real-looking column: thousands of blocks
        w.ApplyChunkRegion(cx * 16, 0, cz * 16, n, h, n, ids, null, null, null);
    }

    private static (BlockDeltaLog Log, ManualClock Clock, long Rev) Started(int capacity = 20_000)
    {
        var clock = new ManualClock(T0);
        var log = new BlockDeltaLog(capacity, clock);
        var first = log.Since(0, null, 100); // the first read turns logging on (a new client always starts full)
        Assert.True(first.Full);
        return (log, clock, first.Rev);
    }

    private static List<int[]> Blocks(BlockDeltaLog.Page p, int dim = 0) => p.Blocks.TryGetValue(dim.ToString(), out var l) ? l : [];

    [Fact]
    public void A_block_change_appears_once_with_its_rev()
    {
        var (log, clock, r0) = Started();
        var w = new WorldState { Dimension = 0 };
        log.Watch(w);
        Load(w, 0, 0);
        var loaded = log.Since(r0, null, 100);
        Assert.False(loaded.Full);
        Assert.Empty(loaded.Blocks);
        var col = Assert.Single(loaded.Columns);
        Assert.Equal((0, 0, 0, BlockDeltaLog.Loaded, r0 + 1), (col.Dim, col.Cx, col.Cz, col.Kind, col.Rev));

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(w.SetBlock(3, 70, 3, MineClaw.Data.Blocks.Stone, 0));
        Assert.True(w.SetBlock(3, 70, 3, MineClaw.Data.Blocks.Stone, 0)); // nothing changed: no event, no entry
        var p = log.Since(loaded.Rev, null, 100);
        Assert.Equal(new[] { 3, 70, 3, MineClaw.Data.Blocks.Stone, 0 }, Assert.Single(Blocks(p)));
        Assert.Equal(loaded.Rev + 1, p.Rev);
        var again = log.Since(p.Rev, null, 100);
        Assert.False(again.Full);
        Assert.Empty(again.Blocks);
        Assert.Equal(p.Rev, again.Rev);
    }

    [Fact]
    public void Bodies_sharing_a_column_log_one_entry_per_change_and_unload_only_when_the_last_lets_go()
    {
        var (log, clock, r0) = Started();
        var store = new SharedChunkStore();
        var a = new WorldState(chunkStore: store) { Dimension = 0 };
        var b = new WorldState(chunkStore: store) { Dimension = 0 };
        log.Watch(a);
        log.Watch(b);
        Load(a, 2, 3);
        Load(b, 2, 3); // the same server data over the second connection
        Assert.True(a.SetBlock(40, 64, 50, MineClaw.Data.Blocks.Cobblestone, 0));
        Assert.True(b.SetBlock(40, 64, 50, MineClaw.Data.Blocks.Cobblestone, 0)); // b's copy of the same packet
        var p = log.Since(r0, null, 100);
        Assert.Single(p.Columns);
        Assert.Single(Blocks(p));

        clock.Now += TimeSpan.FromSeconds(5); // a real later change of the same cell is news again
        Assert.True(a.SetBlock(40, 64, 50, 0, 0));
        Assert.True(b.SetBlock(40, 64, 50, 0, 0));
        var p2 = log.Since(p.Rev, null, 100);
        Assert.Equal(new[] { 40, 64, 50, 0, 0 }, Assert.Single(Blocks(p2)));

        a.UnloadColumn(2, 3); // b still holds it: not the hive's unload
        Assert.Empty(log.Since(p2.Rev, null, 100).Columns);
        b.UnloadColumn(2, 3);
        var gone = Assert.Single(log.Since(p2.Rev, null, 100).Columns);
        Assert.Equal((2, 3, BlockDeltaLog.Unloaded), (gone.Cx, gone.Cz, gone.Kind));
    }

    [Fact]
    public void A_bulk_chunk_load_is_one_column_event_per_column_on_both_versions()
    {
        var (log, _, r0) = Started();
        var beta = new WorldState { Dimension = 0 };
        log.Watch(beta);
        beta.LoadColumn(5, 5);                        // b1.8.1 pre-chunk: an empty column, nothing to fetch yet
        Assert.Empty(log.Since(r0, null, 100).Columns);
        Load(beta, 5, 5, fill: 1);                     // its data: one "loaded", no block entries
        Load(beta, 6, 6, sizeChunks: 2, fill: 3);      // a 2x2-column region
        var p = log.Since(r0, null, 10_000);
        Assert.Empty(p.Blocks);
        Assert.Equal(new[] { (5, 5), (6, 6), (6, 7), (7, 6), (7, 7) }, p.Columns.Select(c => (c.Cx, c.Cz)).OrderBy(c => c));

        var release = new WorldState(height: 256) { Dimension = -1 };
        log.Watch(release);
        Load(release, 0, 0, fill: 87);                 // a 1.12.2 chunk (256 high), in the nether
        var q = log.Since(p.Rev, null, 10_000);
        Assert.Empty(q.Blocks);
        Assert.Equal((-1, 0, 0), (q.Columns.Single().Dim, q.Columns.Single().Cx, q.Columns.Single().Cz));
        Assert.True(release.SetBlock(1, 200, 1, MineClaw.Data.Blocks.Netherrack, 0)); // above b1.8.1's 128
        Assert.Equal(new[] { 1, 200, 1, MineClaw.Data.Blocks.Netherrack, 0 }, Assert.Single(Blocks(log.Since(q.Rev, -1, 100), -1)));
        Assert.Empty(log.Since(q.Rev, 0, 100).Blocks); // the dim filter
    }

    [Fact]
    public void Since_pages_forward_and_falls_back_to_full_when_too_far_behind()
    {
        var (log, clock, r0) = Started(capacity: 16);
        var w = new WorldState { Dimension = 0 };
        log.Watch(w);
        Load(w, 0, 0);
        for (int i = 0; i < 10; i++) w.SetBlock(i, 64, 0, MineClaw.Data.Blocks.Stone, 0);
        var page = log.Since(r0, null, 4);
        Assert.True(page.More);
        Assert.Single(page.Columns);
        Assert.Equal(3, Blocks(page).Count);
        var rest = log.Since(page.Rev, null, 100);
        Assert.False(rest.More);
        Assert.Equal(Enumerable.Range(3, 7), Blocks(rest).Select(b => b[0]));

        for (int i = 0; i < 20; i++) Assert.True(w.SetBlock(i % 16, 65, i / 16, MineClaw.Data.Blocks.Dirt, 0)); // the 16-entry ring overflows
        var behind = log.Since(rest.Rev, null, 100);
        Assert.True(behind.Full);
        Assert.Empty(behind.Blocks);
        Assert.Empty(behind.Columns);
        Assert.Equal(log.Revision, behind.Rev);
        Assert.False(log.Since(behind.Rev - 3, null, 100).Full); // still in the ring
        Assert.True(log.Since(behind.Rev + 50, null, 100).Full); // a rev from before a restart

        // Nobody read for a while: nothing was logged, so a rev from before the gap must refetch.
        long current = log.Since(log.Revision, null, 100).Rev;
        clock.Now += TimeSpan.FromMinutes(5);
        w.SetBlock(1, 66, 1, MineClaw.Data.Blocks.Stone, 0);
        Assert.True(log.Since(current, null, 100).Full);
    }

    [Fact]
    public async Task The_route_caps_a_page_and_says_more()
    {
        var (log, _, r0) = Started();
        var w = new WorldState { Dimension = 0 };
        log.Watch(w);
        Load(w, 0, 0, sizeChunks: 5);
        int n = 0;
        for (int x = 0; x < 80 && n < 4100; x++)
        for (int z = 0; z < 80 && n < 4100; z++, n++)
            w.SetBlock(x, 70, z, MineClaw.Data.Blocks.Stone, 0);
        await using var s = new SupervisorServer(new SupervisorOptions { Token = "s3cret" }, new FakeSupervisorData())
        {
            World = new SupervisorWorldRoutes(new FakeSupervisorWorld()) { Blocks = log },
        };
        SupervisorRequest Get(long since) => new("GET", "/api/world/blocks", new Dictionary<string, string> { ["since"] = since.ToString() }, "s3cret");
        var first = JsonDocument.Parse((await s.HandleAsync(Get(r0))).Text).RootElement;
        Assert.True(first.GetProperty("more").GetBoolean());
        Assert.False(first.GetProperty("full").GetBoolean());
        int got = first.GetProperty("blocks").GetProperty("0").GetArrayLength() + first.GetProperty("columns").GetArrayLength();
        Assert.Equal(SupervisorWorldRoutes.MaxBlockEntries, got);
        var second = JsonDocument.Parse((await s.HandleAsync(Get(first.GetProperty("rev").GetInt64()))).Text).RootElement;
        Assert.False(second.GetProperty("more").GetBoolean());
        Assert.Equal(4100 + 25 - SupervisorWorldRoutes.MaxBlockEntries, second.GetProperty("blocks").GetProperty("0").GetArrayLength());
        Assert.Equal(log.Revision, second.GetProperty("rev").GetInt64());
        var stale = JsonDocument.Parse((await s.HandleAsync(Get(0))).Text).RootElement;
        Assert.True(stale.GetProperty("full").GetBoolean());
        Assert.Equal(401, (await s.HandleAsync(new SupervisorRequest("GET", "/api/world/blocks", new Dictionary<string, string>(), null))).Status);
    }

    [Fact]
    public void The_index_hash_changes_with_a_block_at_once_not_after_its_cache()
    {
        var log = new BlockDeltaLog();
        var previous = BlockDeltaLog.Process;
        BlockDeltaLog.Process = log;
        try
        {
            log.Since(0, null, 1);
            var w = new WorldState { Dimension = 0 };
            log.Watch(w);
            Load(w, 0, 0, fill: 1);
            var src = new WorldColumnSource(() => [new LiveBody("maid_chan", "main", true, w)], () => null) { LiveHashTtl = TimeSpan.FromMinutes(1) };
            long before = src.Index(0, 0, 0, 0, 0, 10)[0].Hash;
            Assert.Equal(before, src.Index(0, 0, 0, 0, 0, 10)[0].Hash); // cached
            Thread.Sleep(5);
            Assert.True(w.SetBlock(2, 100, 2, MineClaw.Data.Blocks.Glass, 0));
            Assert.NotEqual(before, src.Index(0, 0, 0, 0, 0, 10)[0].Hash);
            Assert.True(w.SetBlock(2, 100, 2, MineClaw.Data.Blocks.Glass, 3)); // meta alone counts too
            Thread.Sleep(5);
            long withMeta = src.Index(0, 0, 0, 0, 0, 10)[0].Hash;
            Assert.NotEqual(before, withMeta);
        }
        finally { BlockDeltaLog.Process = previous; }
    }
}
