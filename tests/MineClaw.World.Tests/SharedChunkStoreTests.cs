using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Chunks;
using Xunit;

namespace MineClaw.World.Tests;

/// <summary>Lazily allocated 16³ sections in <see cref="ChunkColumn"/> and the per-process <see cref="SharedChunkStore"/>.</summary>
public class SharedChunkStoreTests
{
    // ------------------------------------------------------------------ lazy sections

    [Fact]
    public void Unallocated_sections_read_as_the_defaults_and_cost_nothing()
    {
        var w = new WorldState(height: 256);
        w.LoadColumn(0, 0); // no data yet: the light engine leaves it alone
        var c = w.GetColumn(0, 0)!;
        Assert.Equal(0, c.AllocatedSections);
        Assert.Equal(16, c.SectionCount);
        Assert.Equal(0, c.GetBlock(3, 200, 4));
        Assert.Equal(0, c.GetMeta(3, 200, 4));
        Assert.Equal(0, c.GetBlockLight(3, 200, 4));
        Assert.Equal(15, c.GetSkyLight(3, 200, 4));
        Assert.Equal(-1, c.HighestNonAir(3, 4));
        w.SetBlock(3, 200, 4, 0, 0); // a default write allocates nothing
        Assert.Equal(0, c.AllocatedSections);
        w.SetBlock(3, 200, 4, Blocks.Wool, 14); // ids + meta of section 12
        Assert.Equal(2, c.AllocatedSections);
        Assert.Equal((Blocks.Wool, 14), (c.GetBlock(3, 200, 4), c.GetMeta(3, 200, 4)));
        Assert.Equal(0, c.GetBlock(3, 199, 4));
        Assert.Equal(15, c.GetSkyLight(3, 200, 4));
        Assert.Equal(200, c.HighestNonAir(3, 4));
        w.SetBlock(3, 200, 4, 0, 0); // back to air: the section may stay
        Assert.Equal(0, c.GetBlock(3, 200, 4));
        Assert.Equal(-1, c.HighestNonAir(3, 4));
    }

    [Fact]
    public void A_region_allocates_only_its_non_default_sections()
    {
        var w = new WorldState(height: 256);
        var (ids, meta, sky) = Terrain(64);
        w.ApplyChunkRegion(0, 0, 0, 16, 256, 16, ids, meta, new byte[ids.Length], sky);
        var c = w.GetColumn(0, 0)!;
        // ids and sky light (0 up to the grass at y 64) of sections 0..4, meta only where set (section 1), no block light.
        Assert.Equal(5 + 1 + 5, c.AllocatedSections);
        Assert.Equal(Blocks.Grass, w.GetBlock(5, 64, 5));
        Assert.Equal(Blocks.Stone, w.GetBlock(5, 10, 5));
        Assert.Equal(1, w.GetMeta(2, 20, 2));
        Assert.Equal(15, w.GetSkyLight(5, 65, 5));
        Assert.Equal(0, w.GetSkyLight(5, 63, 5));
        Assert.Equal(64, c.HighestNonAir(5, 5));
        Assert.True(c.ApproximateBytes < 12 * ChunkColumn.SectionCells);
    }

    [Fact]
    public void The_flat_copies_keep_the_index_layout()
    {
        var w = new WorldState(height: 256);
        var (ids, meta, sky) = Terrain(64);
        w.ApplyChunkRegion(0, 0, 0, 16, 256, 16, ids, meta, null, sky);
        w.SetBlock(7, 200, 9, Blocks.Wool, 5);
        var c = w.GetColumn(0, 0)!;
        var flatIds = c.IdData.ToArray();
        var flatMeta = c.MetaData.ToArray();
        Assert.Equal(c.CellCount, flatIds.Length);
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        for (int y = 0; y < 256; y++)
        {
            Assert.Equal(c.GetBlock(x, y, z), flatIds[ChunkColumn.Index(x, y, z)]);
            Assert.Equal(c.GetMeta(x, y, z), flatMeta[ChunkColumn.Index(x, y, z)]);
        }
        var snap = ChunkSnapshot.Capture(c);
        Assert.Equal(ChunkSnapshot.ComputeHash(flatIds, flatMeta), snap.Hash);
        Assert.Equal(snap.Hash, c.ContentHash());
        var empty = new WorldState();
        empty.LoadColumn(0, 0); // 128 high, all air
        Assert.Equal(ChunkSnapshot.Capture(empty.GetColumn(0, 0)!).Hash, empty.GetColumn(0, 0)!.ContentHash());
        Assert.Equal(Blocks.Wool, snap.Ids[ChunkColumn.Index(7, 200, 9)]);
    }

    [Fact]
    public void A_column_lit_without_light_arrays_keeps_the_open_sky_unallocated()
    {
        var w = new WorldState(height: 256);
        var (ids, meta, _) = Terrain(64);
        w.ApplyChunkRegion(0, 0, 0, 16, 256, 16, ids, meta, null, null); // initLighting
        var c = w.GetColumn(0, 0)!;
        Assert.Equal(15, w.GetSkyLight(3, 65, 3));
        Assert.Equal(0, w.GetSkyLight(3, 63, 3));
        Assert.Equal(5 + 1 + 5, c.AllocatedSections); // the block light pass wrote zeros only
    }

    /// <summary>A 16×256×16 region: stone to y 63 (granite at y 20), grass at y 64, air above; sky 0 below the ground.</summary>
    private static (byte[] Ids, byte[] Meta, byte[] Sky) Terrain(int ground)
    {
        var ids = new byte[16 * 256 * 16];
        var meta = new byte[ids.Length];
        var sky = new byte[ids.Length];
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        for (int y = 0; y < 256; y++)
        {
            int i = (x * 16 + z) * 256 + y;
            ids[i] = (byte)(y < ground ? Blocks.Stone : y == ground ? Blocks.Grass : 0);
            if (y == 20) meta[i] = 1;
            sky[i] = (byte)(y > ground ? 15 : 0);
        }
        return (ids, meta, sky);
    }

    // ------------------------------------------------------------------ the shared store

    private static WorldState Body(SharedChunkStore store) => new(height: 256, chunkStore: store);

    private static void Load(WorldState w, int cx, int cz)
    {
        var (ids, meta, sky) = Terrain(64);
        w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 256, 16, ids, meta, new byte[ids.Length], sky);
    }

    [Fact]
    public void Bodies_share_one_column_per_key_with_their_own_views()
    {
        var store = new SharedChunkStore();
        var a = Body(store);
        var b = Body(store);
        var loadedA = new List<(int, int)>();
        var loadedB = new List<(int, int)>();
        a.ColumnLoaded += (x, z) => loadedA.Add((x, z));
        b.ColumnLoaded += (x, z) => loadedB.Add((x, z));
        Load(a, 0, 0);
        Load(a, 1, 0);
        Load(b, 0, 0);
        Assert.Same(a.GetColumn(0, 0), b.GetColumn(0, 0));
        Assert.Equal(2, store.ColumnCount);
        Assert.Equal(2, a.LoadedColumnCount);
        Assert.Equal(1, b.LoadedColumnCount);
        Assert.Null(b.GetColumn(1, 0));
        Assert.False(b.IsLoaded(20, 5));
        Assert.True(a.IsLoaded(20, 5));
        Assert.Equal([(0, 0), (1, 0)], loadedA);
        Assert.Equal([(0, 0)], loadedB);
        var stats = store.Stats();
        Assert.Equal((2, 3, 2), (stats.Columns, stats.References, stats.Views));
        Assert.True(stats.ApproximateBytes > 0);
        Assert.Equal(256, store.Height);

        // One body unloads: the other still has it; the last release drops it.
        var unloadedB = new List<(int, int)>();
        b.ColumnUnloaded += (x, z) => unloadedB.Add((x, z));
        a.UnloadColumn(0, 0);
        Assert.Null(a.GetColumn(0, 0));
        Assert.NotNull(b.GetColumn(0, 0));
        Assert.Empty(unloadedB);
        Assert.Equal(2, store.ColumnCount);
        b.UnloadColumn(0, 0);
        Assert.Equal([(0, 0)], unloadedB);
        Assert.Equal(1, store.ColumnCount);
        a.Clear(); // respawn / dimension change
        Assert.Equal(0, store.ColumnCount);
        Assert.Equal(0, store.Stats().References);
    }

    [Fact]
    public void A_released_column_is_never_handed_out_again()
    {
        var store = new SharedChunkStore(256);
        var a = Body(store);
        Load(a, 0, 0);
        var first = a.GetColumn(0, 0);
        a.SetBlock(1, 100, 1, Blocks.Wool, 0);
        a.UnloadColumn(0, 0);
        a.LoadColumn(0, 0);
        Assert.NotSame(first, a.GetColumn(0, 0));
        Assert.Equal(0, a.GetBlock(1, 100, 1));
    }

    [Fact]
    public void Dimensions_are_separate_keys()
    {
        var store = new SharedChunkStore();
        var a = Body(store);
        var b = Body(store);
        b.Dimension = -1;
        Load(a, 0, 0);
        Load(b, 0, 0);
        Assert.NotSame(a.GetColumn(0, 0), b.GetColumn(0, 0));
        Assert.Equal(-1, b.GetColumn(0, 0)!.Dimension);
        Assert.Equal(2, store.ColumnCount);
    }

    [Fact]
    public void Dispose_gives_the_columns_back_silently()
    {
        var store = new SharedChunkStore();
        var a = Body(store);
        Load(a, 0, 0);
        Load(a, 0, 1);
        int unloads = 0;
        a.ColumnUnloaded += (_, _) => unloads++;
        a.Dispose();
        Assert.Equal(0, store.ColumnCount);
        Assert.Equal(0, a.LoadedColumnCount);
        Assert.Equal(0, unloads);
        // A private world has nothing to give back: Dispose leaves it readable.
        var p = new WorldState(height: 256);
        Load(p, 0, 0);
        p.Dispose();
        Assert.Equal(Blocks.Stone, p.GetBlock(1, 1, 1));
    }

    [Fact]
    public void The_height_is_one_per_store()
    {
        var store = new SharedChunkStore();
        _ = Body(store);
        Assert.Throws<ArgumentException>(() => new WorldState(height: 128, chunkStore: store));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SharedChunkStore(200));
    }

    [Fact]
    public void Each_body_is_told_a_change_once_with_the_old_block_it_saw()
    {
        var store = new SharedChunkStore();
        var a = Body(store);
        var b = Body(store);
        Load(a, 0, 0);
        Load(b, 0, 0);
        var seenA = new List<BlockChange>();
        var seenB = new List<BlockChange>();
        a.BlockChanged += seenA.Add;
        b.BlockChanged += seenB.Add;
        var p = new BlockPos(3, 63, 3);

        // Both connections get the same block change: A applies it first, B's copy finds it done but is still told stone → air.
        a.SetBlock(3, 63, 3, 0, 0);
        Assert.Equal(0, b.GetBlock(3, 63, 3)); // B reads the shared column at once
        Assert.Empty(seenB);                  // but hears of it from its own connection
        b.SetBlock(3, 63, 3, 0, 0);
        Assert.Equal([new BlockChange(p, Blocks.Stone, 0, 0, 0)], seenA);
        Assert.Equal([new BlockChange(p, Blocks.Stone, 0, 0, 0)], seenB);

        // An identical third copy says nothing to anyone.
        b.SetBlock(3, 63, 3, 0, 0);
        a.SetBlock(3, 63, 3, 0, 0);
        Assert.Single(seenA);
        Assert.Single(seenB);

        // Two changes in a row reach B in order, each with the old block as B saw it.
        a.SetBlock(3, 63, 3, Blocks.Dirt, 0);
        a.SetBlock(3, 63, 3, Blocks.Wool, 3);
        b.SetBlock(3, 63, 3, Blocks.Dirt, 0);
        b.SetBlock(3, 63, 3, Blocks.Wool, 3);
        Assert.Equal([new BlockChange(p, 0, 0, Blocks.Dirt, 0), new BlockChange(p, Blocks.Dirt, 0, Blocks.Wool, 3)], seenB.Skip(1));
        Assert.Equal(3, seenA.Count);
    }

    [Fact]
    public void A_reverted_prediction_of_another_body_tells_nothing()
    {
        var store = new SharedChunkStore();
        var a = Body(store);
        var b = Body(store);
        Load(a, 0, 0);
        Load(b, 0, 0);
        var seenB = new List<BlockChange>();
        b.BlockChanged += seenB.Add;
        a.SetBlock(4, 63, 4, 0, 0);              // A predicts a dig
        a.SetBlock(4, 63, 4, Blocks.Stone, 0);   // its server undoes it
        b.SetBlock(4, 63, 4, Blocks.Stone, 0);   // B's server only ever confirms stone
        Assert.Empty(seenB);
        // A later real change still reports what B saw.
        a.SetBlock(4, 63, 4, Blocks.Wool, 0);
        b.SetBlock(4, 63, 4, Blocks.Wool, 0);
        Assert.Equal([new BlockChange(new BlockPos(4, 63, 4), Blocks.Stone, 0, Blocks.Wool, 0)], seenB);
    }

    [Fact]
    public void Chunk_data_of_its_own_resets_what_a_body_was_told()
    {
        var store = new SharedChunkStore();
        var a = Body(store);
        var b = Body(store);
        Load(a, 0, 0);
        Load(b, 0, 0);
        var seenB = new List<BlockChange>();
        b.BlockChanged += seenB.Add;
        a.SetBlock(5, 63, 5, 0, 0);
        Load(b, 0, 0); // B's server resends the column (stone there): B has seen its data
        a.SetBlock(5, 63, 5, Blocks.Dirt, 0);
        b.SetBlock(5, 63, 5, Blocks.Dirt, 0);
        Assert.Equal([new BlockChange(new BlockPos(5, 63, 5), Blocks.Stone, 0, Blocks.Dirt, 0)], seenB);
    }

    [Fact]
    public void Light_is_updated_once_in_the_shared_column()
    {
        var store = new SharedChunkStore();
        var a = Body(store);
        var b = Body(store);
        for (int cx = -2; cx <= 2; cx++)
        for (int cz = -2; cz <= 2; cz++)
        {
            Load(a, cx, cz);
            Load(b, cx, cz);
        }
        a.SetBlock(8, 65, 8, Blocks.Torch, 5);
        long before = b.Light.Updates;
        b.SetBlock(8, 65, 8, Blocks.Torch, 5); // B's copy: the light is already right, no relight
        Assert.Equal(before, b.Light.Updates);
        Assert.Equal(14, b.GetBlockLight(8, 65, 8));
        Assert.Equal(13, b.GetBlockLight(9, 65, 8));
    }
}
