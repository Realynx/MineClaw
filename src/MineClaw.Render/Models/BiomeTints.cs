using System.Text.Json;

namespace MineClaw.Render.Models;

/// <summary>
/// 1.12.2 biome colours from minecraft-data's <c>tints.json</c> (grass, foliage and water per biome name; the fixed spruce,
/// birch and lily-pad colours) with <c>biomes.json</c>'s id → name. The world model keeps no chunk biomes yet, so the
/// renderer uses <see cref="DefaultBiome"/> (plains: grass 0x91BD59, foliage 0x77AB2F) everywhere.
/// </summary>
public static class BiomeTints
{
    public const int DefaultBiome = 1; // plains
    /// <summary>ColorizerFoliage.getFoliageColorPine / Birch, BlockColors' lily pad (in a world).</summary>
    public const uint Spruce = 0x619961, Birch = 0x80A755, LilyPad = 0x208030;

    private sealed record Tables(Dictionary<int, string> Names, Dictionary<string, uint> Grass, Dictionary<string, uint> Foliage, Dictionary<string, uint> Water,
        uint GrassDefault, uint FoliageDefault, uint WaterDefault);

    private static readonly Lazy<Tables> T = new(Load);

    public static uint Grass(int biome = DefaultBiome) => Lookup(T.Value.Grass, biome, T.Value.GrassDefault);
    public static uint Foliage(int biome = DefaultBiome) => Lookup(T.Value.Foliage, biome, T.Value.FoliageDefault);
    public static uint Water(int biome = DefaultBiome) => Lookup(T.Value.Water, biome, T.Value.WaterDefault);
    public static string? BiomeName(int biome) => T.Value.Names.GetValueOrDefault(biome);

    private static uint Lookup(Dictionary<string, uint> d, int biome, uint fallback) =>
        T.Value.Names.TryGetValue(biome, out var n) && d.TryGetValue(n, out var c) ? c : fallback;

    private static Tables Load()
    {
        var asm = typeof(BiomeTints).Assembly;
        var names = new Dictionary<int, string>();
        using (var s = asm.GetManifestResourceStream("mcdata.1.12.2.biomes.json"))
            if (s != null)
                using (var doc = JsonDocument.Parse(s))
                    foreach (var b in doc.RootElement.EnumerateArray()) names[b.GetProperty("id").GetInt32()] = b.GetProperty("name").GetString()!;
        Dictionary<string, uint> grass = [], foliage = [], water = [];
        uint gd = 0x91BD59, fd = 0x77AB2F, wd = 0x3F76E4;
        using (var s = asm.GetManifestResourceStream("mcdata.1.12.2.tints.json"))
            if (s != null)
                using (var doc = JsonDocument.Parse(s))
                {
                    uint Read(string kind, Dictionary<string, uint> into, uint def)
                    {
                        if (!doc.RootElement.TryGetProperty(kind, out var k)) return def;
                        foreach (var d in k.GetProperty("data").EnumerateArray())
                        {
                            uint c = (uint)d.GetProperty("color").GetInt64() & 0xffffff;
                            foreach (var key in d.GetProperty("keys").EnumerateArray()) into[key.GetString()!] = c;
                        }
                        return k.TryGetProperty("default", out var dv) ? (uint)dv.GetInt64() & 0xffffff : def;
                    }
                    gd = Read("grass", grass, gd);
                    fd = Read("foliage", foliage, fd);
                    wd = Read("water", water, wd);
                }
        // The "default" entries are a fallback for unknown biomes; plains carries the familiar colours.
        return new Tables(names, grass, foliage, water, grass.GetValueOrDefault("plains", gd), foliage.GetValueOrDefault("plains", fd), wd);
    }
}
