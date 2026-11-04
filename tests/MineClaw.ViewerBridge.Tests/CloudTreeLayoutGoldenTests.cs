using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MineClaw.ViewerBridge.Graph;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// Pins <see cref="CloudTreeLayout.Fit"/> (WP07) over seeded random RAPTOR-like trees: soft extra parents, self parents, parents
/// that aren't reachable from a leaf, summaries without a layer, leaves without a seed, coincident seeds (the fixed-direction
/// push), an empty tree and a lone leaf. Every position is dumped round-trip; the test keeps the dump's line count and SHA-256
/// (WP07_GOLDEN_OUT writes the dump for a diff).
/// </summary>
public class CloudTreeLayoutGoldenTests
{
    private static (List<int> Leaves, CloudTreeInput Tree) Input(int seed, int leaves, int layers)
    {
        var rng = new Random(seed);
        var parents = new Dictionary<int, int[]>();
        var layer = new Dictionary<int, int>();
        var seeds = new Dictionary<int, (float X, float Y)>();
        var leafIds = new List<int>();
        int next = 0;
        var below = new List<int>();
        for (int i = 0; i < leaves; i++)
        {
            int id = next++;
            leafIds.Add(id);
            below.Add(id);
            switch (rng.Next(6))
            {
                case 0: break; // no seed: starts at the centre
                case 1: seeds[id] = (0.25f, 0.75f); break; // coincident with the other case-1 leaves
                default: seeds[id] = ((float)rng.NextDouble(), (float)rng.NextDouble()); break;
            }
        }
        var childParents = new Dictionary<int, List<int>>();
        for (int l = 1; l <= layers && below.Count > 1; l++)
        {
            int clusters = Math.Max(1, below.Count / 3);
            var ids = Enumerable.Range(0, clusters).Select(_ => next++).ToList();
            foreach (var s in ids)
                if (rng.Next(5) != 0) layer[s] = l; // some summaries have no layer (read as 1)
            foreach (var b in below)
            {
                var ps = childParents[b] = [ids[rng.Next(clusters)]];
                if (rng.Next(4) == 0) ps.Add(ids[rng.Next(clusters)]); // a soft extra parent (maybe the same one)
                if (rng.Next(13) == 0) ps.Add(b); // itself
                if (rng.Next(17) == 0) ps.Add(5000 + rng.Next(3)); // a parent with no parents of its own
            }
            below = ids;
        }
        if (layers > 0 && below.Count > 0) parents[below[0]] = [below[0]]; // a self loop at the top
        foreach (var (c, ps) in childParents) parents[c] = ps.ToArray();
        if (rng.Next(2) == 0 && next > 0) parents[7000] = [0]; // a node no leaf reaches
        // Shuffled leaf order: Fit keeps the order it's given.
        var order = leafIds.OrderBy(_ => rng.Next()).ToList();
        return (order, new CloudTreeInput(parents, layer, seeds));
    }

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (seed, leaves, layers, iterations) in new[] { (1, 0, 0, 10), (2, 1, 0, 10), (3, 2, 1, 5), (4, 9, 2, 30), (5, 40, 3, 60), (6, 150, 4, 300), (7, 25, 1, 1), (8, 60, 5, 0) })
        {
            var (leafIds, tree) = Input(seed, leaves, layers);
            var r = CloudTreeLayout.Fit(leafIds, tree, new CloudLayoutOptions { TreeIterations = iterations });
            sb.Append(CultureInfo.InvariantCulture, $"== seed {seed}: leaves {r.Leaves.Count} summaries {r.Summaries.Count} leafLength {r.LeafLength:R}\n");
            foreach (var (id, (x, y)) in r.Leaves) sb.Append(CultureInfo.InvariantCulture, $"L{id} {x:R} {y:R}\n");
            foreach (var (id, (x, y)) in r.Summaries) sb.Append(CultureInfo.InvariantCulture, $"S{id} {x:R} {y:R}\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void Fit_matches_the_golden_hash()
    {
        var dump = Dump();
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "cloud-tree.txt"), dump);
        var lines = dump.Count(c => c == '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        Assert.Equal(CloudTreeLayoutGoldenData.Expected, $"{lines} {hash}");
    }
}
