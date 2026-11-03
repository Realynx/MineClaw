using MineClaw.World;

namespace MineClaw.World.Tests;

/// <summary>The biome memory behind find_biome / scout_biome: what chunks said when they loaded, kept after they unload.</summary>
public class BiomeMemoryTests
{
    private const int Plains = 1, Desert = 2, Beach = 16, DesertHills = 17;

    private static byte[] Chunk(int biome, int other = 0, int otherColumns = 0)
    {
        var b = new byte[256];
        Array.Fill(b, (byte)biome);
        for (int i = 0; i < otherColumns; i++) b[i] = (byte)other;
        return b;
    }

    private static HashSet<int> Ids(params int[] ids) => ids.ToHashSet();

    [Fact]
    public void A_chunk_keeps_its_most_common_biome_and_how_much_of_it()
    {
        var m = new BiomeMemory();
        m.Record(0, 3, -2, Chunk(Desert, Plains, 100));   // 156 desert columns, 100 plains
        Assert.Equal(Desert, m.At(0, 3 * 16 + 5, -2 * 16 + 9));
        Assert.Equal(-1, m.At(0, 0, 0));                   // never seen
        Assert.Equal(-1, m.At(-1, 3 * 16, -2 * 16));      // another dimension
        var p = Assert.Single(m.Patches(0, 0, 0, Ids(Desert)));
        Assert.Equal(156, p.Columns);
        Assert.Empty(m.Patches(0, 0, 0, Ids(Desert), minColumns: 157));   // a chunk that is mostly something else is not a hit
    }

    [Fact]
    public void A_patch_is_the_chunks_joined_edge_to_edge_and_the_nearest_patch_comes_first()
    {
        var m = new BiomeMemory();
        // Near patch: an L of 3 desert chunks (a diagonal neighbour is not joined). Far patch: 2 chunks.
        foreach (var (cx, cz) in new[] { (10, 0), (11, 0), (11, 1) }) m.Record(0, cx, cz, Chunk(Desert));
        m.Record(0, 12, 2, Chunk(Desert));                       // touches (11,1) only at a corner: its own patch
        foreach (var (cx, cz) in new[] { (-40, 5), (-39, 5) }) m.Record(0, cx, cz, Chunk(DesertHills));
        m.Record(0, 10, 1, Chunk(Plains));                       // other biomes in between change nothing
        var patches = m.Patches(0, 0, 0, Ids(Desert, DesertHills));
        Assert.Equal(3, patches.Count);
        Assert.Equal([3, 1, 2], patches.Select(p => p.Chunks));
        var near = patches[0];
        Assert.Equal((10, 0), (near.ChunkX, near.ChunkZ));       // the member nearest to the asker
        Assert.Equal(3 * 256, near.Columns);
        Assert.Equal((160, 0, 191, 31), (near.MinX, near.MinZ, near.MaxX, near.MaxZ));
        Assert.True(patches[0].Distance < patches[1].Distance && patches[1].Distance < patches[2].Distance);
    }

    [Fact]
    public void Patches_are_measured_from_the_asker_not_from_the_origin()
    {
        var m = new BiomeMemory();
        m.Record(0, 100, 100, Chunk(Desert));
        m.Record(0, -100, -100, Chunk(Desert));
        Assert.Equal((100, 100), Near(m, 1500, 1500));
        Assert.Equal((-100, -100), Near(m, -1500, -1500));
    }

    private static (int, int) Near(BiomeMemory m, double x, double z)
    {
        var p = m.Patches(0, x, z, Ids(Desert))[0];
        return (p.ChunkX, p.ChunkZ);
    }

    [Fact]
    public void Biomes_survive_the_chunk_unloading_and_a_respawn_clear()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(5 * 16, 0, 5 * 16, 16, 128, 16, new byte[16 * 128 * 16], null, null, null);   // the chunk's blocks
        w.SetBiomes(5, 5, Chunk(Desert));
        Assert.Equal(Desert, w.GetBiome(5 * 16, 5 * 16));
        w.UnloadColumn(5, 5);
        Assert.Equal(-1, w.GetBiome(5 * 16, 5 * 16));            // the live view is gone ...
        Assert.Equal(Desert, w.BiomeMemory.At(0, 5 * 16, 5 * 16)); // ... the memory is not
        w.Clear();
        Assert.Single(w.BiomeMemory.Patches(0, 0, 0, Ids(Desert)));
    }

    [Fact]
    public void Each_dimension_has_its_own_chunks()
    {
        var w = new WorldState();
        w.SetBiomes(1, 1, Chunk(Plains));
        w.Dimension = -1;
        w.SetBiomes(1, 1, Chunk(8));
        Assert.Equal(Plains, w.BiomeMemory.At(0, 16, 16));
        Assert.Equal(8, w.BiomeMemory.At(-1, 16, 16));
    }

    [Theory]
    [InlineData("desert", new[] { 2, 17, 130 })]
    [InlineData("Desert", new[] { 2, 17, 130 })]
    [InlineData("sand", new[] { 2, 16, 17, 130 })]
    [InlineData("beach", new[] { 16 })]               // not stone or cold beach: they have no sand surface
    [InlineData("badlands", new[] { 37, 38, 39, 165, 166, 167 })]
    [InlineData("cold_beach", new[] { 26 })]
    [InlineData("jungle hills", new[] { 22 })]
    [InlineData("2", new[] { 2 })]
    [InlineData("atlantis", new int[0])]
    public void Biome_names_resolve_to_the_ids_a_request_means(string query, int[] expected) =>
        Assert.Equal(expected.OrderBy(i => i), BiomeNames.Resolve(query).OrderBy(i => i));

    /// <summary>The ids and the sand-bearing groups agree with the 1.12.2 data the project ships (minecraft-data biomes.json).</summary>
    [Fact]
    public void Biome_ids_and_the_sand_groups_match_the_shipped_1122_data()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MineClaw.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "third_party", "minecraft-data", "pc", "1.12.2", "biomes.json")));
        var byCategory = doc.RootElement.EnumerateArray().Where(b => b.GetProperty("dimension").GetString() == "overworld")
            .GroupBy(b => b.GetProperty("category").GetString()!, b => b.GetProperty("id").GetInt32()).ToDictionary(g => g.Key, g => g.OrderBy(i => i).ToList());
        foreach (var b in doc.RootElement.EnumerateArray())
            Assert.NotEqual("unknown", BiomeNames.Name(b.GetProperty("id").GetInt32()));            // every shipped biome has a name here
        Assert.Equal(byCategory["desert"], BiomeNames.Resolve("desert").OrderBy(i => i));          // desert, desert hills, desert lakes
        Assert.Equal(byCategory["mesa"], BiomeNames.Resolve("badlands").OrderBy(i => i));          // all six badlands
        Assert.Contains(16, byCategory["beach"]);
        Assert.Equal(byCategory["desert"].Append(16).OrderBy(i => i), BiomeNames.Resolve("sand").OrderBy(i => i));
    }

    [Fact]
    public void Bodies_on_a_shared_store_share_what_they_have_seen()
    {
        var store = new MineClaw.World.Chunks.SharedChunkStore(128);
        var main = new WorldState(chunkStore: store);
        var scout = new WorldState(chunkStore: store);
        scout.SetBiomes(30, 4, Chunk(Desert));
        Assert.Equal(Desert, main.BiomeMemory.At(0, 30 * 16, 4 * 16));       // the scout's walk is the brain body's knowledge
        Assert.Equal(-1, new WorldState().BiomeMemory.At(0, 30 * 16, 4 * 16)); // a world with no store keeps its own
    }
}
