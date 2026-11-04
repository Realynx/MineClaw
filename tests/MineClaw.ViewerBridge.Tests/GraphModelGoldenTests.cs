using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MineClaw.ViewerBridge.Graph;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// Pins <see cref="GraphModel.FromSpecs"/> (WP07) over seeded random RAPTOR-like trees: soft clustering (leaves under several
/// clusters), missing and self children, empty summaries, titles, kinds/sources for every category, seeded epoch-zero stamps and
/// equal timestamps. Every node field and the model's own are dumped; the test keeps the dump's line count and SHA-256
/// (WP07_GOLDEN_OUT writes the dump for a diff).
/// </summary>
public class GraphModelGoldenTests
{
    private static readonly (string? Kind, string? Source)[] Kinds =
    [
        (null, null), ("episode", null), ("activity", "minute"), ("fact", null), ("recipe", null), ("chat", "chat"), ("world", null), ("self", null),
        ("Fact", "seed"), (null, "chat"), ("lore", "wiki"),
    ];

    private static List<NodeSpec> Specs(int seed, int leaves, int layers)
    {
        var rng = new Random(seed);
        var specs = new List<NodeSpec>();
        int next = 0;
        var below = new List<int>();
        for (int i = 0; i < leaves; i++)
        {
            var (kind, source) = Kinds[rng.Next(Kinds.Length)];
            DateTimeOffset? at = rng.Next(5) switch
            {
                0 => null,
                1 => DateTimeOffset.UnixEpoch,
                2 => new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                _ => new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(rng.Next(10000)),
            };
            var text = rng.Next(6) == 0 ? new string('x', 300) + "\nmore" : $"leaf {next} line\r\nsecond";
            specs.Add(new NodeSpec(next, 0, [], text, rng.Next(50), rng.Next(7) == 0, kind, rng.Next(3) == 0 ? "tag" : null, source,
                rng.Next(3) == 0 ? (rng.Next(2) == 0 ? "  " : $"Title {next} " + new string('t', rng.Next(100))) : null, at));
            below.Add(next++);
        }
        for (int layer = 1; layer <= layers && below.Count > 1; layer++)
        {
            var now = new List<int>();
            int clusters = Math.Max(1, below.Count / 3);
            var groups = Enumerable.Range(0, clusters).Select(_ => new List<int>()).ToList();
            foreach (var b in below)
            {
                groups[rng.Next(clusters)].Add(b);
                if (rng.Next(4) == 0) groups[rng.Next(clusters)].Add(b); // soft clustering: a second cluster
            }
            foreach (var g in groups)
            {
                if (rng.Next(9) == 0) g.Add(9999); // a child that isn't in the tree
                if (rng.Next(11) == 0) g.Add(next); // itself
                specs.Add(new NodeSpec(next, layer, g.Distinct().ToArray(), $"summary {next}", rng.Next(200), rng.Next(5) == 0));
                now.Add(next++);
            }
            below = now;
        }
        // Shuffled input order: FromSpecs orders by itself.
        return specs.OrderBy(_ => rng.Next()).ToList();
    }

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (seed, leaves, layers, numLayers) in new[] { (1, 0, 0, 0), (2, 1, 0, 0), (3, 7, 2, 2), (4, 30, 3, 5), (5, 80, 4, 1), (6, 200, 5, 5), (7, 12, 1, 1) })
        {
            var m = GraphModel.FromSpecs(Specs(seed, leaves, layers), numLayers);
            sb.Append(CultureInfo.InvariantCulture, $"== seed {seed}: layers {m.NumLayers} top [{string.Join(",", m.TopLevel)}] counts [{string.Join(",", m.LayerCounts)}] time {m.MinTime}..{m.MaxTime} leaves {m.TotalLeaves}\n");
            foreach (var g in m.Nodes)
                sb.Append(CultureInfo.InvariantCulture,
                    $"{g.Slot} #{g.Index} L{g.Layer} p{g.Parent} root{g.Root} kids[{string.Join(",", g.Children)}] other[{string.Join(",", g.OtherParents)}] cc{g.ChildCount} " +
                    $"leaves{g.Leaves}/{g.LeafCount} tok{g.Tokens} dirty{g.Dirty} cat{g.Category} new{g.Newest} rank{g.TimeRank:R} title[{g.Title}] preview[{g.Preview}]\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void FromSpecs_matches_the_golden_hash()
    {
        var dump = Dump();
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "graph-model.txt"), dump);
        var lines = dump.Count(c => c == '\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        Assert.Equal(GraphModelGoldenData.Expected, $"{lines} {hash}");
    }
}
