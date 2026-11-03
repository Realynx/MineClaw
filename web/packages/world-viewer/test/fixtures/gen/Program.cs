using System.Text.Json;
using MineClaw.World;

// Deterministic columns encoded by the production C# code; the JSON lists the payloads (base64) and what a decoder must read.

static (byte[] Ids, byte[] Meta) Column(int height, int seed)
{
    int v = 256 * height;
    var ids = new byte[v];
    var meta = new byte[v];
    for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
            for (int y = 0; y < height; y++)
            {
                int i = ChunkColumn.Index(x, y, z);
                int h = (x * 31 + z * 17 + y * 7 + seed * 13) % 23;
                ids[i] = (byte)(y < 40 + seed ? 1 + (h % 5) : h == 3 ? 17 + seed : 0);
                meta[i] = (byte)((x + y + z + seed) & 15);
            }
    return (ids, meta);
}

var samples = new (int X, int Y, int Z)[] { (0, 0, 0), (15, 127, 15), (3, 64, 9), (7, 39, 2), (8, 41, 8), (1, 100, 14) };
var samples256 = new (int X, int Y, int Z)[] { (0, 128, 0), (15, 255, 15), (5, 200, 6), (9, 129, 3) };

object Describe(int cx, int cz, ChunkSnapshot s, LightSnapshot? l, long hash)
{
    var pts = samples.Concat(s.Height > 128 ? samples256 : []).Select(p => new[]
    {
        p.X, p.Y, p.Z, s.Ids[ChunkColumn.Index(p.X, p.Y, p.Z)], s.Meta[ChunkColumn.Index(p.X, p.Y, p.Z)],
        l?.Sky(p.X, p.Y, p.Z) ?? -1, l?.Block(p.X, p.Y, p.Z) ?? -1,
    }).ToArray();
    return new
    {
        cx, cz, height = s.Height,
        block_hash = s.Hash.ToString(), light_hash = l?.Hash.ToString(), hash = hash.ToString(),
        full_sections = l?.FullSections ?? 0, biome_0_0 = l?.Biome(0, 0) ?? -1, biome_5_9 = l?.Biome(5, 9) ?? -1,
        samples = pts,
    };
}

var (ia, ma) = Column(128, 0);
var a = new ChunkSnapshot(ia, ma);
var la = LightSnapshot.From(128, (x, y, z) => y >= 60 ? 15 : y > 45 ? (x + z) % 16 : 0, (x, y, z) => (x * 3 + y + z) % 16 == 0 ? 14 : 0);
var (ib, mb) = Column(256, 1);
var b = new ChunkSnapshot(ib, mb);
var biomes = Enumerable.Range(0, 256).Select(i => (byte)(i % 40)).ToArray();
var lb = LightSnapshot.From(256, (x, y, z) => y >= 100 ? 15 : 0, (x, y, z) => 0, biomes);
var (id, md) = Column(128, 2);
var d = new ChunkSnapshot(id, md);

long ha = LightSnapshot.Combine(a.Hash, la.Hash), hb = LightSnapshot.Combine(b.Hash, lb.Hash);
var v2 = ColumnPayload.Encode(0, [
    new ColumnPayloadEntry(3, -2, ha, 1_700_000_000_123, "Alice", true, a.Encode(), la.Encode()),
    new ColumnPayloadEntry(-5, 7, hb, 1_700_000_000_456, "Bøb", true, b.Encode(), lb.Encode()),
    new ColumnPayloadEntry(0, 0, 0, 1_700_000_000_789, null, false, []),
    new ColumnPayloadEntry(1000000, -1000000, d.Hash, 1_600_000_000_000, "remembered", false, d.Encode()),
]);
var v1 = ColumnPayload.Encode(-1, [new ColumnPayloadEntry(-1, -1, a.Hash, 42, null, false, a.Encode())]);

var json = new
{
    generated_by = "web/packages/world-viewer/test/fixtures/gen (MineClaw.World ColumnPayload/ChunkSnapshot/LightSnapshot)",
    v2 = Convert.ToBase64String(v2),
    v1 = Convert.ToBase64String(v1),
    v2_columns = new object[]
    {
        Describe(3, -2, a, la, ha), Describe(-5, 7, b, lb, hb), new { cx = 0, cz = 0, missing = true }, Describe(1000000, -1000000, d, null, d.Hash),
    },
    v1_columns = new[] { Describe(-1, -1, a, null, a.Hash) },
};
var outPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "csharp-payload.json"));
if (args.Length > 0) outPath = args[0];
File.WriteAllText(outPath, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"wrote {outPath} ({v2.Length} + {v1.Length} payload bytes)");
