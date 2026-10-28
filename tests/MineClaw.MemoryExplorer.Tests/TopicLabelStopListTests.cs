using MineClaw.MemoryExplorer.Controls;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>The graph's topic labels name topics (cobble gen, tree farm, Fox), never generic action verbs or log words ("broke").</summary>
public sealed class TopicLabelStopListTests
{
    private static GraphModel Model()
    {
        var specs = new List<NodeSpec>();
        string[][] clusters =
        [
            ["maid_chan broke cobblestone at the cobble gen (10-28 01:56-01:56)", "maid_chan_67 broke cobblestone at the cobble gen (10-28 00:31-00:31)",
             "maid_chan broke 3 cobblestone at the cobble gen (10-28 02:01-02:01)", "maid_chan_69 picked up cobblestone at the cobble gen"],
            ["maid_chan_67 broke log at the tree farm (10-28 00:37-00:37)", "PoofimaFox picked up 4 sapling at the tree farm",
             "maid_chan placed sapling at the tree farm", "maid_chan_69 broke log at the tree farm (10-28 00:40-00:41)"],
            ["PoofimaFox fought a zombie (1 hit taken) at the unknown build", "PoofimaFox placed ~16 cobblestone at the carousel",
             "PoofimaFox picked up torch at the carousel", "PoofimaFox opened the chest at the carousel"],
        ];
        int leaf = 0;
        var l1 = new List<int>();
        for (int c = 0; c < clusters.Length; c++)
        {
            var kids = new List<int>();
            foreach (var text in clusters[c])
            {
                specs.Add(new NodeSpec(leaf, 0, [], text, 20, Kind: "episode", Source: "activity", Title: "activity",
                    Updated: DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + leaf * 60)));
                kids.Add(leaf++);
            }
            specs.Add(new NodeSpec(100 + c, 1, kids, "players broke and picked up blocks", 40));
            l1.Add(100 + c);
        }
        specs.Add(new NodeSpec(200, 2, l1, "root", 60));
        var m = GraphModel.FromSpecs(specs, 2);
        TopicLabeler.Label(m);
        return m;
    }

    [Theory]
    [InlineData("broke")]
    [InlineData("picked")]
    [InlineData("placed")]
    [InlineData("up")]
    [InlineData("at")]
    [InlineData("unknown")]
    [InlineData("mined")]
    [InlineData("chopped")]
    public void Action_verbs_and_log_words_are_stop_words(string word) => Assert.True(TopicLabeler.IsStopWord(word));

    [Theory]
    [InlineData("cobble")]
    [InlineData("gen")]
    [InlineData("carousel")]
    [InlineData("fox")]
    public void Topic_words_are_not_stop_words(string word) => Assert.False(TopicLabeler.IsStopWord(word));

    [Fact]
    public void Labels_name_the_topics_not_the_verbs()
    {
        var m = Model();
        var all = m.Nodes.SelectMany(n => n.Terms).Select(t => t.ToLowerInvariant()).ToList();
        foreach (var bad in new[] { "broke", "picked", "placed", "unknown", "activity", "taken" }) Assert.DoesNotContain(bad, all);
        var gen = m.Nodes.Single(n => n.Layer == 1 && n.Children.Length == 4 && n.Text.Contains("players") && m.Nodes[n.Children[0]].Text.Contains("cobble gen"));
        Assert.Contains(gen.Terms, t => t is "gen" or "cobblestone" or "cobble");
        var farm = m.Nodes.Single(n => n.Layer == 1 && m.Nodes[n.Children[0]].Text.Contains("tree farm"));
        Assert.Contains(farm.Terms, t => t is "farm" or "tree" or "log" or "sapling");
        var carousel = m.Nodes.Single(n => n.Layer == 1 && m.Nodes[n.Children[0]].Text.Contains("zombie"));
        Assert.Contains("carousel", carousel.Terms);
    }
}
