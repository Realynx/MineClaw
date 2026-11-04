using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.ViewerBridge.Graph;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// Pins <see cref="MemoryCloudService.Assemble"/> (WP07): the whole memory cloud wire shape over the seeded trees of
/// <see cref="GraphModelGoldenTests"/>, with some leaves unplaced, inserted leaves, soft-clustered parents and (for the tree
/// arrangement) some summaries laid out themselves. The test keeps the JSON dump's line count and SHA-256 (WP07_GOLDEN_OUT writes
/// the dump for a diff).
/// </summary>
public class MemoryCloudAssembleGoldenTests
{
    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (seed, leafCount, layers, numLayers) in new[] { (1, 0, 0, 0), (2, 1, 0, 0), (3, 7, 2, 2), (4, 30, 3, 5), (5, 80, 4, 1), (6, 200, 5, 5), (7, 12, 1, 1), (8, 60, 3, 3) })
        {
            var specs = GraphModelGoldenTests.Specs(seed, leafCount, layers);
            var g = GraphModel.FromSpecs(specs, numLayers);
            var rng = new Random(seed * 31);
            var leaves = specs.Where(s => s.Layer == 0).OrderBy(s => s.Index)
                .Select(s => new CloudLeaf(s.Index, [1, 0], rng.Next(4) == 0 ? rng.Next(1, 9) : 0)).ToList();
            leaves.Add(new CloudLeaf(5000 + seed, [0, 1], 0)); // a leaf the graph doesn't know
            var positions = new Dictionary<int, (float X, float Y)>();
            foreach (var l in leaves)
                if (rng.Next(7) != 0) positions[l.Index] = ((float)rng.NextDouble(), (float)rng.NextDouble());
            var parents = specs.SelectMany(s => s.Children.Select(c => (c, s.Index))).GroupBy(x => x.c)
                .ToDictionary(x => x.Key, x => x.Select(y => y.Index).ToArray());
            var own = specs.Where(s => s.Layer > 0 && rng.Next(3) == 0).ToDictionary(s => s.Index, _ => ((float)rng.NextDouble(), (float)rng.NextDouble()));
            foreach (var withOwn in new[] { false, true })
            {
                var dto = MemoryCloudService.Assemble(g, leaves, parents, positions, $"key{seed}", $"v{seed}", withOwn ? own : null);
                sb.Append("== seed ").Append(seed).Append(withOwn ? " own" : "").Append('\n');
                sb.Append(JsonSerializer.Serialize(dto)).Append('\n');
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void Assemble_matches_the_golden_hash()
    {
        var dump = Dump();
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "memory-cloud-assemble.txt"), dump);
        var lines = dump.Count(c => c == '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        Assert.Equal(MemoryCloudAssembleGoldenData.Expected, $"{lines} {hash}");
    }
}
