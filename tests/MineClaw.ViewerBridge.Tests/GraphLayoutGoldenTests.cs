using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.ViewerBridge.Graph;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// Pins <see cref="GraphLayoutService.Layout"/> (WP07; <c>/v1/raptor/graph</c>) over the seeded trees of <see cref="GraphModelGoldenTests"/>:
/// every layout (icicle, radial, node-link, an unknown one), every colouring (category, time, cluster, an unknown one) and focus
/// (none, a leaf, a summary, the top, an id that isn't there). Each graph is dumped as JSON; the test keeps the dump's line count and
/// SHA-256 (WP07_GOLDEN_OUT writes the dump for a diff).
/// </summary>
public class GraphLayoutGoldenTests
{
    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (seed, leaves, layers, numLayers) in new[] { (1, 0, 0, 0), (2, 1, 0, 0), (3, 7, 2, 2), (4, 30, 3, 5), (7, 12, 1, 1), (6, 120, 4, 4) })
        {
            var m = GraphModel.FromSpecs(GraphModelGoldenTests.Specs(seed, leaves, layers), numLayers);
            var focuses = new List<int?> { null, 99999 };
            if (m.Nodes.Length > 0)
            {
                focuses.Add(m.Nodes[0].Index);
                focuses.Add(m.Nodes[^1].Index);
                focuses.Add(m.Nodes[m.Nodes.Length / 2].Index);
            }
            foreach (var layout in new[] { "icicle", "radial", "nodelink", "other" })
                foreach (var colorBy in new[] { "category", "time", "cluster", "other" })
                    foreach (var focus in focuses)
                    {
                        var g = GraphLayoutService.Layout(m, layout, colorBy, focus, "k" + seed);
                        sb.Append($"== seed {seed} {layout} {colorBy} focus {focus?.ToString() ?? "-"}\n").Append(JsonSerializer.Serialize(g)).Append('\n');
                    }
        }
        return sb.ToString();
    }

    [Fact]
    public void Layout_matches_the_golden_hash()
    {
        var dump = Dump();
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "graph-layout.txt"), dump);
        var lines = dump.Count(c => c == '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        Assert.Equal(GraphLayoutGoldenData.Expected, $"{lines} {hash}");
    }
}
