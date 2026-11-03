namespace MineClaw.World;

/// <summary>1.12.2 biome ids (the bytes of a full chunk's biome array) and the names a skill or a script asks for.</summary>
public static class BiomeNames
{
    private static readonly Dictionary<int, string> Table = new()
    {
        [0] = "ocean", [1] = "plains", [2] = "desert", [3] = "extreme hills", [4] = "forest", [5] = "taiga", [6] = "swampland",
        [7] = "river", [8] = "nether", [9] = "the end", [10] = "frozen ocean", [11] = "frozen river", [12] = "ice plains",
        [13] = "ice mountains", [14] = "mushroom island", [15] = "mushroom island shore", [16] = "beach", [17] = "desert hills",
        [18] = "forest hills", [19] = "taiga hills", [20] = "extreme hills edge", [21] = "jungle", [22] = "jungle hills",
        [23] = "jungle edge", [24] = "deep ocean", [25] = "stone beach", [26] = "cold beach", [27] = "birch forest",
        [28] = "birch forest hills", [29] = "roofed forest", [30] = "cold taiga", [31] = "cold taiga hills", [32] = "mega taiga",
        [33] = "mega taiga hills", [34] = "extreme hills+", [35] = "savanna", [36] = "savanna plateau", [37] = "mesa",
        [38] = "mesa plateau f", [39] = "mesa plateau", [127] = "void", [129] = "sunflower plains", [130] = "desert m",
        [131] = "extreme hills m", [132] = "flower forest", [133] = "taiga m", [134] = "swampland m", [140] = "ice plains spikes",
        [149] = "jungle m", [151] = "jungle edge m", [155] = "birch forest m", [156] = "birch forest hills m",
        [157] = "roofed forest m", [158] = "cold taiga m", [160] = "mega spruce taiga", [161] = "redwood taiga hills m",
        [162] = "extreme hills+ m", [163] = "savanna m", [164] = "savanna plateau m", [165] = "mesa (bryce)",
        [166] = "mesa plateau f m", [167] = "mesa plateau m",
    };

    /// <summary>What a request for a kind of ground means: "sand" is every biome whose surface is sand (a desert, a beach), "mesa"
    /// and "badlands" are the red-sand ones. Stone and cold beaches have no sand surface.</summary>
    private static readonly Dictionary<string, int[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sand"] = [2, 17, 130, 16],
        ["desert"] = [2, 17, 130],
        ["beach"] = [16],
        ["beaches"] = [16],
        ["mesa"] = [37, 38, 39, 165, 166, 167],
        ["badlands"] = [37, 38, 39, 165, 166, 167],
        ["red sand"] = [37, 38, 39, 165, 166, 167],
        ["ocean"] = [0, 24],
        ["hills"] = [3, 13, 17, 18, 19, 22, 34],
    };

    public static string Name(int id) => Table.TryGetValue(id, out var n) ? n : id < 0 ? "unknown" : $"biome {id}";

    /// <summary>The biome ids a query means ("desert", "sand", "Cold Beach", "2"), or empty when it names none.</summary>
    public static IReadOnlySet<int> Resolve(string? query)
    {
        var q = (query ?? "").Trim().Replace('_', ' ').ToLowerInvariant();
        if (q.Length == 0) return new HashSet<int>();
        if (Aliases.TryGetValue(q, out var a)) return new HashSet<int>(a);
        if (int.TryParse(q, out var id)) return new HashSet<int> { id };
        var exact = Table.Where(kv => kv.Value == q).Select(kv => kv.Key).ToHashSet();
        return exact.Count > 0 ? exact : Table.Where(kv => kv.Value.Contains(q)).Select(kv => kv.Key).ToHashSet();
    }
}
