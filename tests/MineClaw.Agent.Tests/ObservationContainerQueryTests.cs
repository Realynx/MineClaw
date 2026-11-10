using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Tests;

/// <summary>
/// <see cref="ObservationStore.ContainersIn"/>: the viewer's container reads (a chest click, the map's badge box) as one indexed
/// statement that brings each container's items along, where they scanned every observation and then read the items row by row.
/// </summary>
public sealed class ObservationContainerQueryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Kinds = ["chest", "furnace"];
    private readonly AgentDb _db = AgentDb.Open(null);
    private readonly RagIndex _rag;
    private readonly ObservationStore _obs;

    public ObservationContainerQueryTests()
    {
        _rag = new RagIndex(_db, backgroundEmbedding: false);
        _obs = new ObservationStore(_rag);
    }

    public void Dispose()
    {
        _rag.Dispose();
        _db.Dispose();
    }

    private void Container(string kind, int x, int y, int z, int dim = 0, IReadOnlyList<ItemCount>? items = null, bool looked = true) =>
        _obs.Upsert(new Observation(kind, ObservationStore.KeyFor(x, y, z), kind)
        {
            Bounds = Box.Point(x, y, z),
            Contents = looked ? items ?? [] : null,
            ContentsAt = looked ? T0 : null,
            ContentsBy = looked ? "maid_chan" : null,
            Dimension = dim,
        });

    [Fact]
    public void Looked_containers_of_the_kinds_in_the_region_come_with_their_items_biggest_first()
    {
        Container("chest", 1, 64, 1, items: [new ItemCount(4, "cobblestone", 12), new ItemCount(265, "iron_ingot", 40)]);
        Container("furnace", 3, 64, 1, items: [new ItemCount(15, "iron_ore", 2)]);
        Container("chest", 5, 64, 1); // looked into and empty: a known empty chest
        Container("chest", 7, 64, 1, looked: false); // nobody looked inside
        Container("hopper", 9, 64, 1, items: [new ItemCount(4, "cobblestone", 1)]); // not an asked kind
        Container("chest", 1, 64, 1, dim: -1, items: [new ItemCount(87, "netherrack", 5)]); // the nether
        Container("chest", 40, 64, 40, items: [new ItemCount(4, "cobblestone", 1)]); // outside
        Container("chest", 11, 64, 1, items: [new ItemCount(4, "cobblestone", 3)]);
        Assert.True(_obs.Remove("chest", ObservationStore.KeyFor(11, 64, 1), dimension: 0)); // gone

        var found = _obs.ContainersIn(Kinds, new Box(0, 0, 0, 15, 127, 15), 0, 100).ToDictionary(t => t.Row.Key, t => t.Items);

        Assert.Equal(new[] { "1,64,1", "3,64,1", "5,64,1" }, found.Keys.Order());
        Assert.Equal(new[] { ("iron_ingot", 40), ("cobblestone", 12) }, found["1,64,1"].Select(i => (i.Name, i.Count)));
        Assert.Equal(("iron_ore", 2), (Assert.Single(found["3,64,1"]).Name, found["3,64,1"][0].Count));
        Assert.Empty(found["5,64,1"]);
        // The same items the per-row read gives.
        var row = _obs.Get("chest", "1,64,1", dimension: 0)!;
        Assert.Equal(_obs.Contents(row.Id), found["1,64,1"]);
    }

    [Fact]
    public void The_limit_counts_containers_not_items()
    {
        for (int x = 0; x < 6; x++) Container("chest", x * 2, 64, 0, items: [new ItemCount(4, "cobblestone", 1), new ItemCount(3, "dirt", 2)]);
        var some = _obs.ContainersIn(Kinds, new Box(0, 0, 0, 15, 127, 15), 0, 4);
        Assert.Equal(4, some.Count);
        Assert.All(some, t => Assert.Equal(2, t.Items.Count));
        Assert.Empty(_obs.ContainersIn([], new Box(0, 0, 0, 15, 127, 15), 0, 4));
    }

    [Fact]
    public void The_statement_is_served_by_the_dimension_and_kind_index()
    {
        var plan = string.Join("\n", _db.Query("EXPLAIN QUERY PLAN " + ObservationStore.ContainersInSql(Kinds.Length), r => r.GetString(3),
            ("$dim", 0), ("$n", 10), ("$x0", 0.0), ("$x1", 1.0), ("$y0", 0.0), ("$y1", 1.0), ("$z0", 0.0), ("$z1", 1.0), ("$k0", "chest"), ("$k1", "furnace")));
        Assert.Contains("USING INDEX observations_dim", plan);
        Assert.DoesNotContain("SCAN observations", plan);
    }
}
