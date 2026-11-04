using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins <see cref="RagIndex.Search"/> (WP06 refactor): over a seeded index on a fixed clock (FTS and LIKE fallback), the exact
/// hits, order and scores (lexical, cosine, distance, snippet) for text, spatial, semantic, augmented, strict, dimension,
/// kind/tag and exclude queries.
/// </summary>
public class RagSearchGoldenTests
{
    private const string Expected = "8BFA76FD4946A60C939B9AF411CB9EEB7B087A4565A7B7B33D9109378C7B097A";

    private static (AgentDb Db, RagIndex Idx, FakeEmbedder Emb) Seed(bool noFts)
    {
        var time = new ManualTime();
        var emb = new FakeEmbedder();
        var db = AgentDb.Open(null, disableFts: noFts);
        var idx = new RagIndex(db, new RagOptions(), emb, null, time: time, backgroundEmbedding: false);
        var docs = new[]
        {
            new RagDoc("n1", "note", "ingots", "iron ingots stacked by the furnace"),
            new RagDoc("n2", "note", "storage", "metal storage chest holds metal") { Tag = "chests" },
            new RagDoc("n3", "note", "trees", "birch tree farm on the east side") { Weight = 1.5 },
            new RagDoc("n4", "note", "ingots", "ingots ingots ingots wanted by bob") { Tag = "bob" },
            new RagDoc("w1", "waypoint", "cobble gen", "the cobble generator by the tree") { Bounds = new Box(4, 64, 6, 6, 65, 8) },
            new RagDoc("w2", "waypoint", "nether portal", "obsidian portal frame") { Bounds = new Box(10, 64, 10, 12, 68, 10) },
            new RagDoc("w3", "waypoint", "nether fort", "nether fortress bridge") { Bounds = new Box(5, 64, 5, 7, 66, 7), Dimension = -1 },
            new RagDoc("e1", "episode", "turn", "fox asked for cobble; mined 12 cobblestone at the gen"),
            new RagDoc("e2", "episode", "turn", "kiwi asked about the iron farm") { Bounds = new Box(4, 64, 6, 4, 64, 6), Dimension = -1 },
            new RagDoc("s1", "structure", "house", "plank house with a glass roof near the gen") { Bounds = new Box(0, 64, 0, 8, 70, 8) },
            new RagDoc("c1", "chat", "chat", "cobble cobble cobble"),
            new RagDoc("r1", "rule", "rule", "never mine the gen floor") { Weight = 0.5 },
        };
        foreach (var d in docs)
        {
            idx.Upsert(d);
            time.Advance(TimeSpan.FromHours(7));
        }
        idx.FlushEmbeddingsAsync().GetAwaiter().GetResult();
        time.Advance(TimeSpan.FromHours(3));
        return (db, idx, emb);
    }

    private static float[] Vec(FakeEmbedder emb, string text) => emb.EmbedAsync([text], default).GetAwaiter().GetResult().Vectors[0];

    private static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static string Render(List<RagHit> hits) => string.Join("\n", hits.Select(h =>
        $"  {h.Ref}|{h.Kind}|{h.Tag}|{F(h.Score)}|{F(h.Lexical)}|{F(h.Cosine)}|{(h.Distance is { } d ? F(d) : "-")}|{h.Snippet}|{h.Updated:O}|{h.Dimension}"));

    private static IEnumerable<(string Name, Func<RagIndex, FakeEmbedder, List<RagHit>> Run)> Queries()
    {
        yield return ("text", (i, _) => i.Search(new RagQuery { Text = "ingots", Limit = 10 }));
        yield return ("text_vec", (i, e) => i.Search(new RagQuery { Text = "ingots", Limit = 10 }, Vec(e, "ingots")));
        yield return ("cobble", (i, e) => i.Search(new RagQuery { Text = "cobble gen", Limit = 10 }, Vec(e, "cobble gen")));
        yield return ("strict", (i, e) => i.Search(new RagQuery { Text = "cobble generator tree", Strict = true, Limit = 10 }, Vec(e, "cobble generator tree")));
        yield return ("near", (i, _) => i.Search(new RagQuery { Near = (5, 64, 7), Radius = 12, Limit = 10 }));
        yield return ("near_text", (i, _) => i.Search(new RagQuery { Text = "portal", Near = (5, 64, 7), Radius = 8, Limit = 10 }));
        yield return ("near_kinds", (i, _) => i.Search(new RagQuery { Text = "house", Near = (5, 64, 7), NearKinds = ["structure"], Limit = 10 }));
        yield return ("zero_radius", (i, _) => i.Search(new RagQuery { Text = "gen", Near = (5, 64, 7), Radius = 0, Limit = 10 }));
        yield return ("dim_nether", (i, e) => i.Search(new RagQuery { Text = "nether iron farm", Dimension = -1, Near = (6, 64, 6), Limit = 10 }, Vec(e, "nether iron farm")));
        yield return ("dim_over", (i, e) => i.Search(new RagQuery { Text = "nether iron farm", Dimension = 0, Near = (6, 64, 6), Limit = 10 }, Vec(e, "nether iron farm")));
        yield return ("dim_any", (i, _) => i.Search(new RagQuery { Text = "nether", Dimension = WorldDim.Any, Limit = 10 }));
        yield return ("kinds", (i, _) => i.Search(new RagQuery { Text = "cobble", Kinds = ["episode", "chat"], Limit = 10 }));
        yield return ("tags", (i, _) => i.Search(new RagQuery { Text = "ingots metal", Tags = ["bob", "chests"], Limit = 10 }));
        yield return ("exclude", (i, _) => i.Search(new RagQuery { Text = "ingots", Exclude = id => id % 2 == 1, Limit = 10 }));
        yield return ("limit", (i, _) => i.Search(new RagQuery { Text = "the", Limit = 0 }));
        yield return ("ignore", (i, _) => i.Search(new RagQuery { Text = "bob ingots", IgnoreWords = ["bob"], Limit = 10 }));
        yield return ("nothing", (i, _) => i.Search(new RagQuery { Text = "zzzz", Limit = 10 }));
        yield return ("empty", (i, _) => i.Search(new RagQuery { Limit = 10 }));
        yield return ("empty_vec", (i, _) => i.Search(new RagQuery { Text = "ingots", Limit = 10 }, []));
        yield return ("augment", (i, e) =>
        {
            var ids = i.Search(new RagQuery { Text = "ingots tree gen house", Limit = 20 }).Select(h => h.Id).OrderBy(x => x).ToList();
            var aug = new SearchAugment(
            [
                new RankedList("raptor", [(ids[^1], 0.9), (ids[0], 0.2), (ids[^1], 0.95), (ids[1], 0.4)]),
                new RankedList("empty", []),
                new RankedList("second", [(ids[1], 0.1), (999, 0.99)]),
            ], SkipFlatVector: id => id == ids[0]);
            return i.Search(new RagQuery { Text = "metal", Limit = 10 }, Vec(e, "metal"), aug);
        });
    }

    [Fact]
    public void Search_results_stay_the_same()
    {
        var sb = new StringBuilder();
        foreach (var noFts in new[] { false, true })
        {
            var (db, idx, emb) = Seed(noFts);
            using (db)
            using (idx)
                foreach (var (name, run) in Queries())
                    sb.Append("== ").Append(noFts ? "like " : "fts ").Append(name).Append('\n').Append(Render(run(idx, emb))).Append('\n');
        }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "rag-search.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
