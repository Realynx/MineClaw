using MineClaw.Data;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.MemoryExplorer.LiveHive.Gpu;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>The freecam's light comes from the hive (synced nibbles, delta-keyed), follows the world time, and its block models
/// follow the hive's version.</summary>
[Collection("avalonia")]
public class FreecamLightSyncTests
{
    private static ChunkSnapshot Floor(int height = 128)
    {
        var ids = new byte[16 * 16 * height];
        for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) ids[ChunkColumn.Index(x, 20, z)] = (byte)Blocks.Stone;
        return new ChunkSnapshot(ids, new byte[ids.Length]);
    }

    private static ColumnPayloadEntry Lit(int cx, int cz, ChunkSnapshot s, Func<int, int, int, int> block)
    {
        var light = LightSnapshot.From(s.Height, (_, y, _) => y > 20 ? 15 : 0, block);
        return new ColumnPayloadEntry(cx, cz, LightSnapshot.Combine(s.Hash, light.Hash), 1, "maid_chan", true, s.Encode(), light.Encode());
    }

    [Fact]
    public void Voxels_read_the_synced_light_and_fall_back_to_the_estimate_without_it()
    {
        var cache = new HiveWorldCache();
        cache.Apply(ColumnPayload.Encode(0, [Lit(0, 0, Floor(), (x, y, z) => x == 3 && y == 21 && z == 3 ? 14 : 0), LiveHiveDecoderTests.Entry(1, 0, Floor())]));
        var v = new HiveVoxels(cache);
        v.Prepare(0, 8, 8, 2, DateTimeOffset.UtcNow);
        Assert.Equal(14, v.BlockLight(3, 21, 3));
        Assert.Equal(15, v.SkyLight(3, 21, 3));
        Assert.Equal(0, v.SkyLight(3, 10, 3));    // under the floor: dark, not "unknown"
        Assert.Equal(-1, v.SkyLight(19, 10, 3));  // the column without light: the old estimate
        var hood = new ColumnNeighbourhood();
        hood.Set(cache, 0, 0, 0);
        Assert.Equal(14, hood.BlockLight(3, 21, 3));
        Assert.Equal(0, hood.SkyLight(3, 10, 3));
    }

    [Fact]
    public void A_light_only_change_rebuilds_its_sections_and_the_neighbours_across()
    {
        var cache = new HiveWorldCache();
        var floor = Floor();
        cache.Apply(ColumnPayload.Encode(0, [Lit(0, 0, floor, (_, _, _) => 0)]));
        var store = new SectionStore(cache) { Log = [] };
        while (store.Update(0, 0, 0, 2) > 0) { }
        store.Log!.Clear();
        // The same blocks; a torch's light reaches y 21..24 (section 1) only.
        cache.Apply(ColumnPayload.Encode(0, [Lit(0, 0, floor, (x, y, z) => y is > 20 and < 25 && x < 4 ? 10 : 0)]));
        while (store.Update(0, 0, 0, 2) > 0) { }
        Assert.Contains(new SectionKey(0, 1, 0), store.Log);
        Assert.DoesNotContain(store.Log, k => k.Sy > 2);
        Assert.Equal(0, SectionStore.LightDiff(cache.Get(0, 0, 0)!, cache.Get(0, 0, 0)!));
    }

    [Fact]
    public void Night_darkens_the_mesh_and_a_new_light_step_rebuilds_everything()
    {
        var cache = new HiveWorldCache();
        cache.Apply(ColumnPayload.Encode(0, [Lit(0, 0, Floor(), (_, _, _) => 0)]));
        var hood = new ColumnNeighbourhood();
        hood.Set(cache, 0, 0, 0);
        var m = new SectionMesher { Textured = false };
        m.Build(hood, 0, 1, 0);
        byte day = m.Solid.V.Take(m.Solid.Count).Max(v => v.Light);
        m.SkySubtracted = Lighting.SkylightSubtracted(18000);
        m.Build(hood, 0, 1, 0);
        byte night = m.Solid.V.Take(m.Solid.Count).Max(v => v.Light);
        Assert.Equal(255, day);
        Assert.True(night < day / 2, $"night {night}, day {day}");

        var store = new SectionStore(cache) { Log = [] };
        while (store.Update(0, 0, 0, 2) > 0) { }
        store.Log!.Clear();
        store.SkySubtracted = 4;
        while (store.Update(0, 0, 0, 2) > 0) { }
        Assert.Contains(new SectionKey(0, 1, 0), store.Log);
        store.Log.Clear();
        store.SkySubtracted = 4;
        Assert.Equal(0, store.Update(0, 0, 0, 2));

        // The world time comes with the index and runs on at 20 ticks a second.
        Assert.Null(cache.TimeNow(0));
        cache.SetWorldInfo(0, "1.12.2", 13000, nowMs: 1000);
        Assert.Equal(13020, cache.TimeNow(0, nowMs: 2000));
        Assert.Null(cache.TimeNow(-1, nowMs: 2000));
        Assert.Equal("1.12.2", cache.GameVersion);
    }

    [Fact]
    public void The_hives_version_picks_the_block_models()
    {
        var cache = new HiveWorldCache();
        Assert.Null(cache.DetectedVersion);
        var loads = new List<string>();
        var b181 = new TextureAtlas(16, new uint[256 * 256], null, null);
        var r1122 = new TextureAtlas(16, new uint[256 * 256], null, null);
        using var gate = new ManualResetEventSlim(false);
        var atlas = new VersionedAtlas(() => cache.DetectedVersion, v =>
        {
            lock (loads) loads.Add(v);
            if (v == "1.12.2") { gate.Wait(TimeSpan.FromSeconds(10)); return r1122; }
            return b181;
        }, "b1.8.1");
        atlas.Start(atlas.Version).Wait();
        Assert.Same(b181, atlas.Current);
        // A 256-high column (1.12.2's world) before any supervisor said: the models follow.
        cache.Apply(ColumnPayload.Encode(0, [LiveHiveDecoderTests.Entry(0, 0, Floor(256))]));
        Assert.Equal("1.12.2", cache.DetectedVersion);
        Assert.Same(b181, atlas.Current);          // the old one while the jar is read
        gate.Set();
        atlas.Start("1.12.2").Wait();
        Assert.Same(r1122, atlas.Current);
        Assert.Equal(["b1.8.1", "1.12.2"], loads);
        // The hive's own word wins over the guess.
        cache.SetWorldInfo(0, "b1.8.1", null);
        Assert.Equal("b1.8.1", cache.DetectedVersion);

        var dto = HiveJson.Parse<IndexDto>("""{"dim":0,"at":5,"columns":[],"version":"1.12.2","time":6000}"""u8.ToArray())!;
        Assert.Equal("1.12.2", dto.Version);
        Assert.Equal(6000, dto.Time);
        Assert.Null(HiveJson.Parse<IndexDto>("""{"dim":0,"at":5,"columns":[]}"""u8.ToArray())!.Version); // an older supervisor
    }
}
