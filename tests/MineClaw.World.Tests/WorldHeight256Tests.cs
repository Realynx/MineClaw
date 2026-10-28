using MineClaw.Data;
using MineClaw.World;
using Xunit;

namespace MineClaw.World.Tests;

/// <summary>
/// Multi-version P1: 256-high (1.12.2) columns behind the same world model, with b1.8.1's 128-high layout untouched (the
/// upper half sits above the lower one, so every index below y 128 is the b1.8.1 one).
/// </summary>
public class WorldHeight256Tests
{
    [Fact]
    public void The_lower_half_index_is_the_b181_index()
    {
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        for (int y = 0; y < 128; y++)
            Assert.Equal((x * 16 + z) * 128 + y, ChunkColumn.Index(x, y, z));
        Assert.Equal(32768, ChunkColumn.Index(0, 128, 0));
        Assert.Equal(65535, ChunkColumn.Index(15, 255, 15));
        Assert.Equal(128, new WorldState().WorldHeight);
        Assert.Equal(32768, new ChunkColumn(0, 0).CellCount);
        Assert.Equal(65536, new ChunkColumn(0, 0, 256).CellCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldState(height: 200));
    }

    /// <summary>An empty 256-high world of 5×5 columns lit like a fresh chunk (no light arrays → initLighting).</summary>
    private static WorldState Empty256()
    {
        var w = new WorldState(height: 256);
        const int n = 80;
        w.ApplyChunkRegion(-32, 0, -32, n, 256, n, new byte[n * 256 * n], null, null, null);
        w.Time = 6000;
        return w;
    }

    [Fact]
    public void Blocks_above_128_are_stored_read_and_scanned()
    {
        var w = Empty256();
        Assert.True(w.SetBlock(3, 200, 4, Blocks.Stone, 0));
        Assert.True(w.SetBlock(3, 255, 4, Blocks.Wool, 14));
        Assert.False(w.SetBlock(3, 256, 4, Blocks.Stone, 0));
        Assert.Equal(Blocks.Stone, w.GetBlock(3, 200, 4));
        Assert.Equal(14, w.GetMeta(3, 255, 4));
        Assert.Equal(255, w.HighestBlock(3, 4));
        Assert.Equal(200, w.HighestSolidBelow(3, 254, 4));
        Assert.Equal([(200, Blocks.Stone, 0), (255, Blocks.Wool, 14)], w.ScanColumn(3, 4));
        Assert.Single(w.FindBlocks(new Vec3d(3, 199, 4), 3, Blocks.Stone));
        Assert.False(Safety.IsOverVoid(w, 3, 210, 4)); // the 1.12.2 height reaches the safety scan
        Assert.True(Safety.IsOverVoid(w, 5, 210, 5));
        // The same calls on a b1.8.1 world stop at 127.
        var old = new WorldState();
        old.ApplyChunkRegion(0, 0, 0, 16, 128, 16, new byte[16 * 128 * 16], null, null, null);
        Assert.False(old.SetBlock(3, 200, 4, Blocks.Stone, 0));
    }

    [Fact]
    public void Light_engine_runs_on_256_high_columns()
    {
        var w = Empty256();
        // Open sky from the top of the world: every cell sees the sky.
        Assert.Equal(15, w.LightAt(new BlockPos(0, 255, 0)).Sky);
        Assert.Equal(15, w.LightAt(new BlockPos(0, 140, 0)).Sky);
        Assert.Equal(0, w.Light.Height(0, 0));
        // A roof at y 200: the height map moves above 128 and the cells below darken by distance from the edge.
        for (int x = -8; x <= 8; x++)
        for (int z = -8; z <= 8; z++)
            w.SetBlock(x, 200, z, Blocks.Stone, 0);
        Assert.Equal(201, w.Light.Height(0, 0));
        Assert.True(w.Light.CanSeeSky(0, 201, 0));
        Assert.False(w.Light.CanSeeSky(0, 150, 0));
        Assert.Equal(15, w.LightAt(new BlockPos(0, 201, 0)).Sky);
        int under = w.LightAt(new BlockPos(0, 199, 0)).Sky;
        Assert.InRange(under, 1, 14); // lit sideways from the roof's edge (8 blocks away), not by open sky
        Assert.Equal(15, w.LightAt(new BlockPos(12, 150, 0)).Sky); // outside the roof
        // A torch at y 180 lights its neighbourhood across the 128 half boundary of nothing (pure upper half) …
        w.SetBlock(0, 180, 0, Blocks.Torch, 5);
        Assert.Equal(14, w.LightAt(new BlockPos(0, 180, 0)).Block);
        Assert.Equal(13, w.LightAt(new BlockPos(0, 181, 0)).Block);
        Assert.Equal(10, w.LightAt(new BlockPos(2, 182, 0)).Block);
        // … and one at y 127 lights y 128 above it (the boundary between the halves).
        w.SetBlock(20, 127, 20, Blocks.Torch, 5);
        Assert.Equal(13, w.LightAt(new BlockPos(20, 128, 20)).Block);
        Assert.Equal(12, w.LightAt(new BlockPos(20, 129, 20)).Block);
        Assert.Equal(13, w.LightAt(new BlockPos(20, 126, 20)).Block);
        w.SetBlock(0, 180, 0, 0, 0);
        Assert.Equal(0, w.LightAt(new BlockPos(0, 181, 0)).Block);
    }

    [Fact]
    public void A_server_lit_256_column_keeps_its_light_and_gets_a_height_map()
    {
        var w = new WorldState(height: 256);
        var ids = new byte[16 * 256 * 16];
        var block = new byte[ids.Length];
        var sky = new byte[ids.Length];
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        {
            int b = (x * 16 + z) * 256;
            for (int y = 0; y <= 150; y++) ids[b + y] = (byte)Blocks.Stone;
            for (int y = 151; y < 256; y++) sky[b + y] = 15;
            block[b + 100] = 7;
        }
        w.ApplyChunkRegion(0, 0, 0, 16, 256, 16, ids, null, block, sky);
        Assert.Equal(151, w.Light.Height(5, 5));
        Assert.Equal(15, w.LightAt(new BlockPos(5, 151, 5)).Sky);
        Assert.Equal(0, w.LightAt(new BlockPos(5, 140, 5)).Sky);
        Assert.Equal(7, w.LightAt(new BlockPos(5, 100, 5)).Block);
        Assert.Equal(Blocks.Stone, w.GetBlock(5, 150, 5));
        Assert.Equal(0, w.GetBlock(5, 151, 5));
    }

    [Fact]
    public void Snapshots_of_256_columns_round_trip()
    {
        var w = Empty256();
        w.SetBlock(1, 5, 1, Blocks.Dirt, 0);
        w.SetBlock(1, 250, 1, Blocks.Wool, 3);
        var col = w.GetColumn(0, 0)!;
        var snap = ChunkSnapshot.Capture(col);
        Assert.Equal(256, snap.Height);
        var back = ChunkSnapshot.Decode(snap.Encode());
        Assert.Equal(snap.Hash, back.Hash);
        Assert.Equal(256, back.Height);
        var rem = new RememberedColumn(0, 0, 0, DateTimeOffset.UtcNow, back);
        Assert.Equal(Blocks.Wool, rem.GetBlock(1, 250, 1));
        Assert.Equal(3, rem.GetMeta(1, 250, 1));
        Assert.Equal(Blocks.Dirt, rem.GetBlock(1, 5, 1));
        Assert.Contains("15:35=1", back.Summary());
        // b1.8.1 snapshots are unchanged: 128 high, same bytes.
        var old = new WorldState();
        old.ApplyChunkRegion(0, 0, 0, 16, 128, 16, new byte[16 * 128 * 16], null, null, null);
        old.SetBlock(1, 5, 1, Blocks.Dirt, 0);
        var s128 = ChunkSnapshot.Capture(old.GetColumn(0, 0)!);
        Assert.Equal(128, s128.Height);
        Assert.Equal(ChunkSnapshot.Volume, s128.Ids.Length);
        Assert.Equal(128, ChunkSnapshot.Decode(s128.Encode()).Height);
        Assert.Throws<InvalidDataException>(() => ChunkSnapshot.Decode([1, 2, 3]));
    }
}
