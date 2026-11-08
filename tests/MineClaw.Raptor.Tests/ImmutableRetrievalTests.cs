using MineClaw.Raptor.Model;
using MineClaw.Raptor.Numerics;
using Xunit.Abstractions;

namespace MineClaw.Raptor.Tests;

public class ImmutableRetrievalTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(48, 32)]
    [InlineData(2300, 512)]
    public void Reused_rows_match_copied_rows_for_serial_and_parallel_rankings(int leaves, int dimensions)
    {
        var tree = MakeTree(leaves, dimensions);
        var copied = new RaptorRetriever(tree, new RaptorOptions());
        var reused = new RaptorRetriever(tree, new RaptorOptions { ReuseImmutableEmbeddings = true });
        var queries = new RaptorQuery[]
        {
            new() { MaxTokens = 100_000 },
            new() { TopK = 7, MaxTokens = 100_000 },
            new() { MaxTokens = 37 },
            new() { IncludePinned = true, TopK = 12 },
            new() { Filter = new RaptorFilter { Kinds = ["ore"] }, MaxTokens = 100_000 },
            new() { Collapsed = false, TopK = 3 },
            new() { Collapsed = false, IncludePinned = true, Filter = new RaptorFilter { Kinds = ["ore"] } },
        };
        foreach (var queryVector in new[] { tree[0].Embedding, tree[17].Embedding, new float[dimensions] })
            foreach (var query in queries)
            {
                var expected = copied.Retrieve(queryVector, query);
                var actual = reused.Retrieve(queryVector, query);
                Assert.Equal(expected.Nodes, actual.Nodes);
                Assert.Equal(expected.Context, actual.Context);
                Assert.Equal(expected.Tokens, actual.Tokens);
                Assert.Equal(expected.Collapsed, actual.Collapsed);
            }
    }

    [Fact]
    public void Default_retriever_keeps_copy_until_refresh_if_public_array_is_modified()
    {
        var tree = Pair();
        var retriever = new RaptorRetriever(tree, new RaptorOptions());
        var query = new RaptorQuery { TopK = 1 };
        Assert.Equal(0, retriever.Retrieve([1, 0], query).Nodes.Single().Index);
        tree[0].Embedding[0] = -1;
        Assert.Equal(0, retriever.Retrieve([1, 0], query).Nodes.Single().Index);
        retriever.Refresh();
        Assert.Equal(1, retriever.Retrieve([1, 0], query).Nodes.Single().Index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replacing_node_vector_is_visible_only_after_refresh(bool reuse)
    {
        var tree = Pair();
        var retriever = new RaptorRetriever(tree, new RaptorOptions { ReuseImmutableEmbeddings = reuse });
        var query = new RaptorQuery { TopK = 1 };
        tree[0].Embedding = [-1, 0];
        Assert.Equal(0, retriever.Retrieve([1, 0], query).Nodes.Single().Index);
        retriever.Refresh();
        Assert.Equal(1, retriever.Retrieve([1, 0], query).Nodes.Single().Index);
    }

    [Fact]
    public void Read_only_refresh_avoids_tree_sized_embedding_copy()
    {
        const int leaves = 12_072, dimensions = 1024;
        var tree = MakeTree(leaves, dimensions);
        // Warm the refresh paths before measuring allocations on the calling thread.
        _ = new RaptorRetriever(Pair(), new RaptorOptions());
        _ = new RaptorRetriever(Pair(), new RaptorOptions { ReuseImmutableEmbeddings = true });
        long before = GC.GetAllocatedBytesForCurrentThread();
        var copied = new RaptorRetriever(tree, new RaptorOptions());
        long copiedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var reused = new RaptorRetriever(tree, new RaptorOptions { ReuseImmutableEmbeddings = true });
        long reusedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{tree.Count} x {dimensions}: copied {copiedBytes:N0} B; reused {reusedBytes:N0} B");
        Assert.True(copiedBytes >= (long)tree.Count * dimensions * sizeof(float));
        Assert.True(reusedBytes < 1_000_000, $"Read-only refresh allocated {reusedBytes:N0} bytes.");
        Assert.True(reusedBytes < copiedBytes / 20);
        GC.KeepAlive(copied);
        GC.KeepAlive(reused);
    }

    private static RaptorTree Pair()
    {
        var tree = new RaptorTree { Dimensions = 2 };
        tree.Add(new RaptorNode(0, 0, "first", [1, 0], 1));
        tree.Add(new RaptorNode(1, 0, "second", [0, 1], 1));
        return tree;
    }

    private static RaptorTree MakeTree(int leaves, int dimensions)
    {
        const int groupSize = 32;
        var tree = new RaptorTree { Dimensions = dimensions, NumLayers = 1 };
        var random = new Random(934);
        for (int i = 0; i < leaves; i++)
        {
            var vector = new float[dimensions];
            for (int d = 0; d < dimensions; d++) vector[d] = (float)(random.NextDouble() * 2 - 1);
            VectorMath.Normalize(vector);
            tree.Add(new RaptorNode(i, 0, $"leaf {i}", vector, 1 + i % 7)
            {
                Key = $"leaf:{i}", Kind = i % 3 == 0 ? "ore" : "fact", Pinned = i % 29 == 0,
            });
        }
        for (int first = 0; first < leaves; first += groupSize)
        {
            int parent = leaves + first / groupSize;
            tree.Add(new RaptorNode(parent, 1, $"summary {parent}", tree[first].Embedding, 11));
            for (int child = first; child < Math.Min(leaves, first + groupSize); child++) tree.Link(parent, child);
        }
        return tree;
    }
}
