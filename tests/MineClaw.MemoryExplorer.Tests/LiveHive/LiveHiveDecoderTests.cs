using System.Buffers.Binary;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.World;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

public class LiveHiveDecoderTests
{
    internal static ChunkSnapshot Snap(params (int X, int Y, int Z, byte Id)[] blocks)
    {
        var ids = new byte[ChunkSnapshot.Volume];
        var meta = new byte[ChunkSnapshot.Volume];
        foreach (var (x, y, z, id) in blocks)
        {
            ids[ChunkColumn.Index(x, y, z)] = id;
            meta[ChunkColumn.Index(x, y, z)] = 3;
        }
        return new ChunkSnapshot(ids, meta);
    }

    internal static ColumnPayloadEntry Entry(int cx, int cz, ChunkSnapshot s, long seen = 1_700_000_000_000, string? by = "maid_chan", bool live = true) =>
        new(cx, cz, s.Hash, seen, by, live, s.Encode());

    [Fact]
    public void Payload_round_trips_every_field()
    {
        var a = Snap((1, 64, 2, 4));
        var b = Snap((15, 127, 15, 17), (0, 0, 0, 7));
        var bytes = ColumnPayload.Encode(-1, [Entry(3, -4, a), Entry(-100, 7, b, 42, "maid_chan_1337", false), new ColumnPayloadEntry(9, 9, 0, 0, null, false, [])]);
        var (dim, cols) = ColumnPayload.Decode(bytes);
        Assert.Equal(-1, dim);
        Assert.Equal(3, cols.Count);
        Assert.Equal((3, -4, a.Hash, 1_700_000_000_000L, "maid_chan", true), (cols[0].Cx, cols[0].Cz, cols[0].Hash, cols[0].SeenMs, cols[0].By, cols[0].Live));
        Assert.Equal((-100, 7, b.Hash, 42L, "maid_chan_1337", false), (cols[1].Cx, cols[1].Cz, cols[1].Hash, cols[1].SeenMs, cols[1].By, cols[1].Live));
        Assert.True(cols[2].Missing);
        Assert.Null(cols[2].By);
        var back = ChunkSnapshot.Decode(cols[1].Data);
        Assert.Equal(b.Hash, back.Hash);
        Assert.Equal(17, back.Ids[ChunkColumn.Index(15, 127, 15)]);
        Assert.Equal(3, back.Meta[ChunkColumn.Index(15, 127, 15)]);
    }

    [Fact]
    public void Bad_magic_version_truncation_and_trailing_bytes_are_rejected()
    {
        var good = ColumnPayload.Encode(0, [Entry(0, 0, Snap((1, 1, 1, 1)))]);
        Assert.Throws<InvalidDataException>(() => ColumnPayload.Decode([]));
        Assert.Throws<InvalidDataException>(() => ColumnPayload.Decode("<html>not found</html>"u8.ToArray()));

        var magic = (byte[])good.Clone();
        magic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => ColumnPayload.Decode(magic));

        var version = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(version.AsSpan(4), 3); // 2 is the light version now
        var ex = Assert.Throws<InvalidDataException>(() => ColumnPayload.Decode(version));
        Assert.Contains("version", ex.Message);

        Assert.Throws<InvalidDataException>(() => ColumnPayload.Decode(good.AsSpan(0, good.Length - 5)));
        Assert.Throws<InvalidDataException>(() => ColumnPayload.Decode([.. good, 0]));

        var count = (byte[])good.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(count.AsSpan(12), ColumnPayload.MaxColumns + 1);
        Assert.Throws<InvalidDataException>(() => ColumnPayload.Decode(count));

        var size = (byte[])good.Clone();
        int sizeAt = 16 + 4 + 4 + 8 + 8 + 1 + 1 + "maid_chan".Length;
        BinaryPrimitives.WriteInt32LittleEndian(size.AsSpan(sizeAt), -1);
        Assert.Throws<InvalidDataException>(() => ColumnPayload.Decode(size));
    }

    [Fact]
    public void A_bad_payload_applies_nothing()
    {
        var cache = new HiveWorldCache();
        var bytes = ColumnPayload.Encode(0, [Entry(0, 0, Snap((1, 1, 1, 1))), new ColumnPayloadEntry(1, 0, 5, 0, null, false, [1, 2, 3])]); // 2nd isn't deflate
        Assert.ThrowsAny<Exception>(() => cache.Apply(bytes));
        Assert.Equal(0, cache.Count);
        Assert.Throws<InvalidDataException>(() => cache.Apply("garbage-garbage-garbage"u8));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Server_json_parses_into_the_dtos()
    {
        var json = """
            {"at":1700000000000,"bots":[{"name":"maid_chan_67","short":"67","main":false,"dim":0,"x":6.5,"y":64,"z":8.5,"yaw":90,"pitch":12.5,
              "pose_age":1.2,"hp":17,"food":12,"job":"mining","held":"stone pickaxe","claims":["block 6,64,8"],"live":false,"age_seconds":0.8}],
             "entities":[{"kind":"player","name":"PoofImaFox","dim":0,"x":1,"y":65,"z":2}]}
            """u8.ToArray();
        var e = HiveJson.Parse<EntitiesDto>(json)!;
        var b = Assert.Single(e.Bots);
        Assert.Equal("67", b.Key);
        Assert.Equal(90f, b.Yaw);
        Assert.Equal(1.2, b.PoseAge);
        Assert.Equal("stone pickaxe", b.Held);
        Assert.Equal("block 6,64,8", Assert.Single(b.Claims!));
        Assert.Equal("PoofImaFox", Assert.Single(e.Entities).Name);

        var idx = HiveJson.Parse<IndexDto>("""{"dim":-1,"at":5,"columns":[{"cx":1,"cz":-2,"hash":-9223372036854775807,"seen":7,"by":"maid_chan","live":true}]}"""u8.ToArray())!;
        Assert.Equal(-9223372036854775807, idx.Columns[0].Hash);

        var ov = HiveJson.Parse<OverlaysDto>("""
            {"at":"2026-10-28T10:00:00+00:00","dim":0,"claims":[{"kind":"block","text":"block 4,64,6","min":[4,64,6],"max":[4,64,6],"owner":"maid_chan_67"}],
             "places":[],"points":[],"plans":[{"id":7,"name":"tower","status":"building","part":0,"blocks":[[5,65,5,4,0,0]]}],"dangers":[],"notes":[]}
            """u8.ToArray())!;
        Assert.Equal("maid_chan_67", ov.Claims[0].Owner);
        Assert.Equal([5, 65, 5, 4, 0, 0], ov.Plans[0].Blocks[0]);
    }
}
