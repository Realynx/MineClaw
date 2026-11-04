using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineClaw.Bot.Blueprints.Mine;

/// <summary>One generated feature of the mine terrain: an ore or a stone pocket (dirt, gravel, granite…).</summary>
/// <param name="Veins">Attempts per 16×16 chunk.</param>
/// <param name="Size">Steps of one vein's random walk (the vein size).</param>
/// <param name="Shape">"uniform" (min..max) or "triangle" (center ± spread: most at the center).</param>
/// <param name="Best">The level a strip mine for it goes to.</param>
public sealed record OreBand(string Id, string Name, int Block, int Meta, int Min, int Max, int Veins, int Size, string Shape, int Center, int Spread,
    int Best, string? Color, string? Note);

/// <summary>A version's table: the ores, the pockets, the bedrock layer (floor..top) and the lava-lake level.</summary>
public sealed record OreVersion(string Version, IReadOnlyList<OreBand> Ores, IReadOnlyList<OreBand> Pockets, int BedrockFloor, int BedrockTop, int Lava);

/// <summary>
/// The ore heights of each game version (docs/web/api.md "Mine designs"): the Design Studio's terrain generator
/// (<see cref="MineTerrain"/>) and the ore-height guide read the same data file,
/// <c>web/apps/web/src/features/studio/mine/ore-heights.json</c> (embedded here), with its public sources noted in it.
/// </summary>
public static class OreTable
{
    private sealed record Feature(string Id, string Name, int Block, int Meta, int Min, int Max, int Veins, int Size, string? Shape, int? Center, int? Spread,
        int? Best, string? Color, string? Note);
    private sealed record Bedrock(int Floor, int Top);
    private sealed record Ver(Bedrock Bedrock, int Lava, List<Feature> Ores, List<Feature> Pockets);
    private sealed record File(string About, List<string> Sources, Dictionary<string, Ver> Versions);

    private static readonly Lazy<(IReadOnlyDictionary<string, OreVersion> Versions, IReadOnlyList<string> Sources)> Data = new(Load);

    /// <summary>The file's public sources (shown with the guide).</summary>
    public static IReadOnlyList<string> Sources => Data.Value.Sources;

    /// <summary>The table of a version ("b1.8.1" / "1.12.2"; anything else: the server's).</summary>
    public static OreVersion For(string? version) =>
        Data.Value.Versions[DesignVersions.Normalize(version) ?? DesignVersions.Normalize(DesignVersions.Server) ?? DesignVersions.Beta];

    private static (IReadOnlyDictionary<string, OreVersion>, IReadOnlyList<string>) Load()
    {
        using var s = typeof(OreTable).Assembly.GetManifestResourceStream("mine.ore-heights.json")
                      ?? throw new InvalidOperationException("mine.ore-heights.json is not embedded");
        var f = JsonSerializer.Deserialize<File>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                ?? throw new InvalidOperationException("ore-heights.json is empty");
        static OreBand Band(Feature x) => new(x.Id, x.Name, x.Block, x.Meta, x.Min, x.Max, x.Veins, x.Size, x.Shape ?? "uniform", x.Center ?? 0, x.Spread ?? 0,
            x.Best ?? (x.Min + x.Max) / 2, x.Color, x.Note);
        var map = f.Versions.ToDictionary(kv => kv.Key, kv => new OreVersion(kv.Key, kv.Value.Ores.Select(Band).ToList(), kv.Value.Pockets.Select(Band).ToList(),
            kv.Value.Bedrock.Floor, kv.Value.Bedrock.Top, kv.Value.Lava));
        return (map, f.Sources);
    }

    /// <summary>The ore at a block state, or null.</summary>
    public static OreBand? OreOf(string? version, int id, int meta) => For(version).Ores.FirstOrDefault(o => o.Block == id && (o.Meta == meta || o.Meta == 0));

    /// <summary>"coal y0-127 (best 48), iron y0-63 (16), … ; lava lakes y≤10, bedrock y0-4": the compact line the AI designer reads.</summary>
    public static string Compact(string? version)
    {
        var v = For(version);
        var sb = new StringBuilder();
        sb.Append(string.Join(", ", v.Ores.Where(o => o.Veins > 0).Select(o =>
            string.Create(CultureInfo.InvariantCulture, $"{o.Id} y{o.Min}-{o.Max}{(o.Shape == "triangle" ? $" peak {o.Center}" : "")} best {o.Best}"))));
        sb.Append(CultureInfo.InvariantCulture, $"; lava lakes y≤{v.Lava}; bedrock y{v.BedrockFloor}-{v.BedrockTop}");
        return sb.ToString();
    }
}
