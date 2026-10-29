using MineClaw.Data;
using MineClaw.World;
using Xunit;

namespace MineClaw.World.Tests;

/// <summary>The Live Hive light sync: a live column's light nibbles + biomes, compact per section, delta-keyed, wire version 2.</summary>
public class LightSnapshotTests
{
    private static WorldState World(int height = 128)
    {
        var w = new WorldState(height: height) { Dimension = 0 };
        var ids = new byte[16 * height * 16];
        for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) for (int y = 0; y < 60; y++) ids[(x * 16 + z) * height + y] = (byte)Blocks.Stone;
        w.ApplyChunkRegion(0, 0, 0, 16, height, 16, ids, null, null, null);
        return w;
    }

    [Fact]
    public void Capture_matches_the_columns_light_cell_for_cell_and_stays_small()
    {
        foreach (int h in new[] { 128, 256 })
        {
            var w = World(h);
            Assert.True(w.SetBlock(4, 60, 4, Blocks.Torch, 5));
            var col = w.GetColumn(0, 0)!;
            var l = LightSnapshot.Capture(col);
            Assert.Equal(h, l.Height);
            for (int x = 0; x < 16; x++)
                for (int z = 0; z < 16; z++)
                    for (int y = 0; y < h; y++)
                    {
                        Assert.Equal(col.GetSkyLight(x, y, z), l.Sky(x, y, z));
                        Assert.Equal(col.GetBlockLight(x, y, z), l.Block(x, y, z));
                    }
            Assert.InRange(l.FullSections, 1, 3);             // the surface sections; buried and open-sky ones are one byte
            var back = LightSnapshot.Decode(l.Encode());
            Assert.Equal(l.Hash, back.Hash);
            Assert.Equal(l.Hash, LightSnapshot.HashOf(col)); // the index's allocation-free hash is the same key
            Assert.Equal(l.Block(4, 61, 4), back.Block(4, 61, 4));
            Assert.True(l.Encode().Length < 2048, $"{l.Encode().Length} bytes");
        }
    }

    [Fact]
    public void A_light_change_changes_the_hash_and_only_its_sections_differ()
    {
        var w = World();
        var a = LightSnapshot.Capture(w.GetColumn(0, 0)!);
        Assert.Equal(a.Hash, LightSnapshot.Capture(w.GetColumn(0, 0)!).Hash);
        Assert.True(w.SetBlock(8, 100, 8, Blocks.Glowstone, 0));   // in the air: section 6 only
        var b = LightSnapshot.Capture(w.GetColumn(0, 0)!);
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.False(a.SameSection(b, 6));
        Assert.True(a.SameSection(b, 0));
        Assert.True(a.SameSection(b, 2));
    }

    [Fact]
    public void Payload_v2_carries_light_and_v1_stays_for_old_viewers()
    {
        var w = World();
        var col = w.GetColumn(0, 0)!;
        var snap = ChunkSnapshot.Capture(col);
        var l = LightSnapshot.Capture(col);
        var v1 = ColumnPayload.Encode(0, [new ColumnPayloadEntry(0, 0, snap.Hash, 1, "a", true, snap.Encode())]);
        Assert.Equal(ColumnPayload.Version, BitConverter.ToUInt16(v1, 4));
        Assert.Null(ColumnPayload.Decode(v1).Columns[0].Light);

        long key = LightSnapshot.Combine(snap.Hash, l.Hash);
        var v2 = ColumnPayload.Encode(0, [new ColumnPayloadEntry(0, 0, key, 1, "a", true, snap.Encode(), l.Encode()),
            new ColumnPayloadEntry(1, 0, 7, 1, "b", false, snap.Encode())]); // a remembered column: no light
        Assert.Equal(ColumnPayload.VersionLight, BitConverter.ToUInt16(v2, 4));
        var (_, cols) = ColumnPayload.Decode(v2);
        Assert.Equal(key, cols[0].Hash);
        Assert.Equal(l.Hash, LightSnapshot.Decode(cols[0].Light!).Hash);
        Assert.Null(cols[1].Light);
        Assert.Throws<InvalidDataException>(() => ColumnPayload.Decode(v2.AsSpan(0, v2.Length - 1)));
        Assert.Throws<InvalidDataException>(() => LightSnapshot.Decode([1, 2, 3]));
    }

    [Fact]
    public void Biomes_ride_along_and_join_the_hash()
    {
        var a = LightSnapshot.From(128, (_, y, _) => y > 64 ? 15 : 0, (_, _, _) => 0, Enumerable.Repeat((byte)1, 256).ToArray());
        var b = LightSnapshot.From(128, (_, y, _) => y > 64 ? 15 : 0, (_, _, _) => 0, Enumerable.Repeat((byte)4, 256).ToArray());
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.Equal(4, LightSnapshot.Decode(b.Encode()).Biome(3, 9));
        Assert.Equal(-1, LightSnapshot.From(128, (_, _, _) => 15, (_, _, _) => 0).Biome(0, 0));
    }
}
