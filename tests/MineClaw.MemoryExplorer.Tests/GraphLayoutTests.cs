using Avalonia;
using MineClaw.MemoryExplorer.Controls;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>RAPTOR graph: partition layout, TF-IDF topic labels, hit-testing under zoom and the leaf level of detail.</summary>
public sealed class GraphLayoutTests
{
    private static readonly string[] Topics =
    [
        "cobblestone generator lava water flowing cobble mining",
        "wheat farm hoe seeds bonemeal harvest crops",
        "oak tree sapling logs planks chop wood",
        "chest storage sorted items deposit withdraw",
    ];

    /// <summary>2 roots (L2) over 5 L1 clusters over 23 leaves, uneven fan-out, one soft-clustered leaf.</summary>
    private static GraphModel Sample(bool shuffle = false)
    {
        var specs = new List<NodeSpec>();
        int leaf = 0;
        int[] perCluster = [7, 2, 5, 6, 3];
        var l1 = new List<int>();
        for (int c = 0; c < perCluster.Length; c++)
        {
            var kids = new List<int>();
            for (int k = 0; k < perCluster[c]; k++)
            {
                var topic = Topics[c % Topics.Length];
                specs.Add(new NodeSpec(leaf, 0, [], $"{topic} note {k} by PoofimaFox", 20 + k, Kind: c == 3 ? "gamedata" : "episode",
                    Source: "episode", Title: $"leaf {leaf}", Updated: DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + leaf * 60)));
                kids.Add(leaf++);
            }
            int idx = 100 + c;
            if (c == 1) kids.Add(0); // soft clustering: leaf 0 also sits under cluster 1
            specs.Add(new NodeSpec(idx, 1, kids, $"summary of {Topics[c % Topics.Length]}", 80));
            l1.Add(idx);
        }
        specs.Add(new NodeSpec(200, 2, [l1[0], l1[1], l1[2]], "root one", 150, Dirty: true));
        specs.Add(new NodeSpec(201, 2, [l1[3], l1[4]], "root two", 120));
        if (shuffle) specs = specs.OrderBy(s => (s.Index * 7919) % 101).ToList();
        var m = GraphModel.FromSpecs(specs, 2);
        TopicLabeler.Label(m);
        return m;
    }

    [Fact]
    public void Partition_widths_are_proportional_to_leaf_counts_and_children_sum_to_the_parent()
    {
        var m = Sample();
        var L = m.Layout;
        Assert.Equal(23, L.TotalLeaves);
        Assert.Equal(3, L.Rows);
        foreach (var g in m.Nodes)
        {
            double w = L.X1[g.Slot] - L.X0[g.Slot];
            Assert.Equal(g.Leaves / 23.0, w, 9);
            Assert.Equal(m.NumLayers - g.Layer, L.Row[g.Slot]);
            if (g.Children.Length == 0) continue;
            Assert.Equal(g.Leaves, g.Children.Sum(c => m.Nodes[c].Leaves));
            Assert.Equal(w, g.Children.Sum(c => L.X1[c] - L.X0[c]), 9);
            // Nested: children tile the parent's band left to right with no gap.
            double at = L.X0[g.Slot];
            foreach (var c in g.Children.OrderBy(c => L.X0[c]))
            {
                Assert.Equal(at, L.X0[c], 9);
                at = L.X1[c];
            }
            Assert.Equal(L.X1[g.Slot], at, 9);
        }
        // Top level tiles [0, 1]; soft-clustered leaf 0 hangs under its most specific cluster only (101: 3 children vs 7).
        Assert.Equal(1.0, m.TopLevel.Sum(s => L.X1[s] - L.X0[s]), 9);
        var leaf0 = m.Nodes[m.Slot[0]];
        Assert.Equal(101, m.Nodes[leaf0.Parent].Index);
        Assert.Equal([100], leaf0.OtherParents.Select(p => m.Nodes[p].Index));
        Assert.Equal(6, m.Nodes[m.Slot[100]].LeafCount);
        Assert.Equal(7 + 2 + 5, m.Nodes[m.Slot[200]].Leaves);
    }

    [Fact]
    public void Soft_clustering_never_leaves_a_cluster_with_children_empty()
    {
        // Cluster 11 only holds leaves that cluster 10 (smaller) also holds: the repair pass gives it one of them.
        var specs = new List<NodeSpec>
        {
            new(0, 0, [], "a"), new(1, 0, [], "b"), new(2, 0, [], "c"),
            new(10, 1, [0, 1], "x"), new(11, 1, [0, 1, 2], "y"), new(12, 1, [2], "z"),
            new(20, 2, [10, 11, 12], "root"),
        };
        var m = GraphModel.FromSpecs(specs, 2);
        Assert.All(m.Nodes.Where(g => !g.IsLeaf), g => Assert.True(g.LeafCount > 0, $"#{g.Index} is empty"));
        Assert.Equal(3, m.Nodes[m.Slot[20]].LeafCount);
        Assert.Equal(1.0, m.Layout.X1[m.Slot[20]] - m.Layout.X0[m.Slot[20]], 9);
    }

    [Fact]
    public void Tf_idf_labels_are_deterministic_and_topical()
    {
        var a = Sample();
        var b = Sample(shuffle: true);
        foreach (var g in a.Nodes)
            Assert.Equal(g.Terms, b.Nodes[b.Slot[g.Index]].Terms);
        Assert.Equal(TopicLabeler.Compute(a), TopicLabeler.Compute(a));
        // Cluster 0 is about the cobblestone generator; the name every leaf shares never wins.
        var c0 = a.Nodes[a.Slot[100]].Terms;
        Assert.InRange(c0.Length, 2, 4);
        Assert.Contains(c0, t => t is "cobblestone" or "generator" or "lava" or "cobble" or "flowing" or "mining" or "water");
        Assert.All(a.Nodes, g => Assert.DoesNotContain("PoofimaFox", g.Terms));
    }

    [Fact]
    public void Hit_testing_returns_the_node_under_the_point_after_zoom()
    {
        var m = Sample();
        var L = m.Layout;
        var plot = new Rect(80, 10, 920, 600);
        int target = m.Slot[103]; // the L1 band with 6 leaves
        // Zoom 4× into the middle of that band.
        double mid = (L.X0[target] + L.X1[target]) / 2;
        var view = new ViewState(mid - 0.2, 0.25, 1, 0, 0);
        var vp = new GraphViewport(m, GraphLayoutKind.Icicle, plot, view);
        var (t, b) = vp.RowSpan(L.Row[target]);
        Assert.Equal(target, vp.HitTest(new Point(vp.ScreenX(mid), (t + b) / 2)));
        // Just inside the band's left edge and just outside it.
        Assert.Equal(target, vp.HitTest(new Point(vp.ScreenX(L.X0[target]) + 1, (t + b) / 2)));
        Assert.NotEqual(target, vp.HitTest(new Point(vp.ScreenX(L.X0[target]) - 1, (t + b) / 2)));
        // Above it, its root.
        var (rt, rb) = vp.RowSpan(0);
        Assert.Equal(m.Slot[201], vp.HitTest(new Point(vp.ScreenX(mid), (rt + rb) / 2)));
        // In the gutter: nothing.
        Assert.Equal(-1, vp.HitTest(new Point(20, (t + b) / 2)));

        // The sunburst maps the same partition to angles.
        var rv = new GraphViewport(m, GraphLayoutKind.Radial, plot, ViewState.Whole with { Scale = 1.5 });
        var (r0, r1) = rv.RingSpan(L.Row[target]);
        var p = GraphViewport.Polar(rv.Center, (r0 + r1) / 2, rv.Angle(mid));
        Assert.Equal(target, rv.HitTest(p));

        // Node-link: the node's circle.
        var nv = new GraphViewport(m, GraphLayoutKind.NodeLink, plot, view);
        Assert.Equal(target, nv.HitTest(nv.NodeCenter(target) + new Vector(1, 1)));
    }

    [Fact]
    public void Lod_hides_individual_leaves_below_the_pixel_threshold()
    {
        var m = Sample();
        var plot = new Rect(0, 0, 46, 400); // 2 px per leaf at full view
        var whole = new GraphViewport(m, GraphLayoutKind.Icicle, plot, ViewState.Whole);
        Assert.Equal(2, whole.PixelsPerLeaf, 9);
        Assert.False(whole.ShowLeaves);
        Assert.Equal(GraphLod.LeafStripFrac, whole.LeafFrac, 9);
        var (t, b) = whole.RowSpan(m.Layout.LeafRow);
        var (st, sb) = whole.RowSpan(0);
        Assert.True(b - t < (sb - st) * 0.5, "leaf row is a thin strip");
        // With leaves hidden the strip answers with the leaf's parent.
        int leaf = m.Slot[3];
        double x = whole.ScreenX((m.Layout.X0[leaf] + m.Layout.X1[leaf]) / 2);
        Assert.Equal(m.Nodes[leaf].Parent, whole.HitTest(new Point(x, (t + b) / 2)));

        var zoomed = new GraphViewport(m, GraphLayoutKind.Icicle, plot, new ViewState(0, 0.25, 1, 0, 0)); // 8 px per leaf
        Assert.True(zoomed.ShowLeaves);
        Assert.Equal(1, zoomed.LeafFrac, 9);
        var (zt, zb) = zoomed.RowSpan(m.Layout.LeafRow);
        Assert.Equal(leaf, zoomed.HitTest(new Point(zoomed.ScreenX((m.Layout.X0[leaf] + m.Layout.X1[leaf]) / 2), (zt + zb) / 2)));

        Assert.False(GraphLod.ShowLeaves(GraphLod.LeafDetailPx - 0.01));
        Assert.True(GraphLod.ShowLeaves(GraphLod.LeafDetailPx));
    }

    [Fact]
    public void Row_range_culls_to_the_visible_window()
    {
        var m = Sample();
        var L = m.Layout;
        int row = L.LeafRow;
        var (from, to) = L.RowRange(row, 0.5, 0.6);
        var visible = L.RowSlots[row][from..to];
        Assert.All(visible, s => Assert.True(L.X1[s] > 0.5 - 1e-9 && L.X0[s] <= 0.6));
        // Every band overlapping the window is included.
        for (int i = 0; i < L.RowSlots[row].Length; i++)
        {
            int s = L.RowSlots[row][i];
            if (L.X1[s] > 0.5 && L.X0[s] < 0.6) Assert.Contains(s, visible);
        }
    }

    [Fact]
    public void Categories_and_dirty_flags_carry_through()
    {
        var m = Sample();
        Assert.Equal(MemoryCategory.Recipe, m.Nodes[m.Slot[103]].Category); // gamedata cluster
        Assert.Equal(MemoryCategory.Episode, m.Nodes[m.Slot[200]].Category);
        Assert.True(m.Nodes[m.Slot[200]].Dirty);
        Assert.Equal(m.Nodes.Where(g => g.IsLeaf).Max(g => g.Newest), m.MaxTime);
        Assert.Equal(MemoryCategory.Activity, GraphModel.Categorize("episode", null, "activity"));
        Assert.Equal(MemoryCategory.World, GraphModel.Categorize("structure", null, "memory"));
    }
}
