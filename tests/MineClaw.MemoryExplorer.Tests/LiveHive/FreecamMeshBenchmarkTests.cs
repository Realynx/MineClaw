using System.Diagnostics;
using MineClaw.Data;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.MemoryExplorer.LiveHive.Gpu;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>A deterministic overworld-like area for mesher benchmarks: stone with caves and ores to y 58, dirt, grass at 62,
/// trees, flowers, torches, a pond, a small house (door, glass, slabs, stairs, fence) — 5×5 columns, 128 or 256 high.</summary>
internal static class MeshFixture
{
    public static (byte[] Ids, byte[] Meta) Terrain(int cx, int cz, int height)
    {
        var ids = new byte[16 * 16 * height];
        var meta = new byte[ids.Length];
        var rng = new Random(cx * 7919 + cz * 104729 + 17);
        void Put(int x, int y, int z, int id, int m = 0)
        {
            if ((uint)x >= 16 || (uint)z >= 16 || (uint)y >= (uint)height) return;
            ids[ChunkColumn.Index(x, y, z)] = (byte)id;
            meta[ChunkColumn.Index(x, y, z)] = (byte)m;
        }
        for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
            {
                int wx = cx * 16 + x, wz = cz * 16 + z;
                int top = 60 + (int)(2 * Math.Sin(wx * 0.21) + 2 * Math.Cos(wz * 0.17));
                Put(x, 0, z, Blocks.Bedrock);
                for (int y = 1; y < top - 3; y++)
                {
                    bool cave = Math.Sin(wx * 0.31 + y * 0.4) * Math.Cos(wz * 0.27 - y * 0.3) > 0.55;
                    if (cave) continue;
                    int r = rng.Next(100);
                    Put(x, y, z, r < 2 ? Blocks.CoalOre : r < 3 ? Blocks.IronOre : r < 5 ? Blocks.Gravel : Blocks.Stone);
                }
                for (int y = top - 3; y < top; y++) Put(x, y, z, Blocks.Dirt);
                Put(x, top, z, Blocks.Grass);
                int f = rng.Next(40);
                if (f == 0) Put(x, top + 1, z, Blocks.TallGrass, 1);
                else if (f == 1) Put(x, top + 1, z, Blocks.Dandelion);
                else if (f == 2) Put(x, top + 1, z, Blocks.Torch, 5);
            }
        // A tree.
        int tx = 4 + rng.Next(8), tz = 4 + rng.Next(8);
        for (int y = 64; y < 69; y++) Put(tx, y, tz, Blocks.Log);
        for (int dx = -2; dx <= 2; dx++) for (int dz = -2; dz <= 2; dz++) for (int y = 67; y <= 69; y++)
            if ((dx != 0 || dz != 0 || y == 69) && Math.Abs(dx) + Math.Abs(dz) < 4) Put(tx + dx, y, tz + dz, Blocks.Leaves);
        // A pond.
        for (int x = 1; x < 4; x++) for (int z = 10; z < 14; z++) { Put(x, 61, z, Blocks.Water); Put(x, 62, z, Blocks.Water); Put(x, 63, z, 0); }
        // A small house: planks walls, glass, a door, a slab roof, stairs and a fence.
        for (int x = 9; x <= 13; x++) for (int z = 1; z <= 5; z++) for (int y = 64; y <= 67; y++)
        {
            bool wall = x is 9 or 13 || z is 1 or 5;
            if (!wall) Put(x, y, z, 0);
            else Put(x, y, z, y == 65 && (x == 11 || z == 3) ? Blocks.Glass : Blocks.Planks);
        }
        for (int x = 9; x <= 13; x++) for (int z = 1; z <= 5; z++) Put(x, 68, z, Blocks.Slab, 2);
        Put(11, 64, 1, Blocks.WoodenDoor, 1); Put(11, 65, 1, Blocks.WoodenDoor, 9);
        Put(11, 64, 0, Blocks.WoodenStairs, 2);
        for (int x = 8; x <= 14; x++) Put(x, 64, 7, Blocks.Fence);
        Put(10, 66, 4, Blocks.Torch, 4);
        return (ids, meta);
    }

    /// <summary>A cache holding the 5×5 columns around chunk (0, 0).</summary>
    public static HiveWorldCache Cache(int height = 128, Func<int, int, byte[], byte[], ColumnPayloadEntry>? entry = null)
    {
        var cache = new HiveWorldCache();
        var list = new List<ColumnPayloadEntry>();
        for (int cx = -2; cx <= 2; cx++)
            for (int cz = -2; cz <= 2; cz++)
            {
                var (ids, meta) = Terrain(cx, cz, height);
                list.Add(entry?.Invoke(cx, cz, ids, meta) ?? LiveHiveDecoderTests.Entry(cx, cz, new ChunkSnapshot(ids, meta)));
            }
        cache.Apply(ColumnPayload.Encode(0, list));
        return cache;
    }

    /// <summary>The same area lit by the world's light engine and synced with its light (the live path: payload v2).</summary>
    public static HiveWorldCache CacheLit(int height = 128)
    {
        var w = new WorldState(height: height) { Dimension = 0 };
        for (int cx = -2; cx <= 2; cx++)
            for (int cz = -2; cz <= 2; cz++)
            {
                var (ids, meta) = Terrain(cx, cz, height);
                // Terrain is in the flat column layout; the region wants (x · 16 + z) · height + y.
                var rid = new byte[ids.Length]; var rmeta = new byte[ids.Length];
                for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) for (int y = 0; y < height; y++)
                {
                    rid[(x * 16 + z) * height + y] = ids[ChunkColumn.Index(x, y, z)];
                    rmeta[(x * 16 + z) * height + y] = meta[ChunkColumn.Index(x, y, z)];
                }
                w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, height, 16, rid, rmeta, null, null);
            }
        var list = new List<ColumnPayloadEntry>();
        for (int cx = -2; cx <= 2; cx++)
            for (int cz = -2; cz <= 2; cz++)
            {
                var col = w.GetColumn(cx, cz)!;
                var snap = ChunkSnapshot.Capture(col);
                var light = LightSnapshot.Capture(col);
                list.Add(new ColumnPayloadEntry(cx, cz, LightSnapshot.Combine(snap.Hash, light.Hash), 1, "maid_chan", true, snap.Encode(), light.Encode()));
            }
        var cache = new HiveWorldCache();
        cache.Apply(ColumnPayload.Encode(0, list));
        return cache;
    }
}

/// <summary>The freecam's mesh build time (the mesh worker's cost per section; the frame budget is untouched by it).</summary>
[Collection("avalonia")]
public class FreecamMeshBenchmarkTests(ITestOutputHelper output)
{
    public static (double MsPerSection, int Sections, int Quads) Measure(HiveWorldCache cache, SectionMesher m, int rounds = 8)
    {
        var hood = new ColumnNeighbourhood();
        int sections = 0, quads = 0;
        double best = double.MaxValue;
        for (int round = 0; round < rounds + 1; round++)
        {
            sections = quads = 0;
            var sw = Stopwatch.StartNew();
            for (int cx = -1; cx <= 1; cx++)
                for (int cz = -1; cz <= 1; cz++)
                {
                    hood.Set(cache, 0, cx, cz);
                    var c = hood.Centre!;
                    for (int sy = 0; sy < c.Snap.Height / 16; sy++)
                    {
                        if (!SectionMesher.NonEmpty(c.Snap, sy)) continue;
                        m.Build(hood, cx, sy, cz);
                        sections++;
                        quads += m.Solid.Quads + m.Translucent.Quads;
                    }
                }
            double ms = sw.Elapsed.TotalMilliseconds / Math.Max(1, sections);
            if (round > 0) best = Math.Min(best, ms); // round 0 warms the JIT and caches
        }
        return (best, sections, quads);
    }

    [Fact]
    public void Mesh_build_time_per_section()
    {
        // Before the light sync (10-29): 1.73 ms/section b1.8.1, 0.77 ms/section 1.12.2 models, flat 0.75 shade everywhere.
        var cache = MeshFixture.CacheLit();
        foreach (bool smooth in new[] { false, true })
        {
            var (ms, n, q) = Measure(cache, new SectionMesher { Textured = true, Smooth = smooth });
            output.WriteLine($"b1.8.1 textured, synced light, {(smooth ? "smooth" : "flat")}: {ms:0.000} ms/section over {n} sections, {q} quads");
            Assert.True(n > 20 && q > 1000, $"{n} sections, {q} quads");
            Assert.True(ms < 25, $"{ms:0.0} ms per section"); // generous: CI machines vary; the live number is logged above
        }
        if (Jar1122FactAttribute.Atlas.Value?.Models is { } models)
        {
            var big = MeshFixture.CacheLit(256);
            foreach (bool smooth in new[] { false, true })
            {
                var (ms2, n2, q2) = Measure(big, new SectionMesher { Textured = true, Models = models, Smooth = smooth });
                output.WriteLine($"1.12.2 models, synced light, {(smooth ? "smooth" : "flat")}: {ms2:0.000} ms/section over {n2} sections, {q2} quads");
                Assert.True(ms2 < 25, $"{ms2:0.0} ms per section");
            }
        }
    }
}
