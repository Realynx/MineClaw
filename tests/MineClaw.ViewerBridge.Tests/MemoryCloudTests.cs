using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using MineClaw.Raptor.Numerics;
using MineClaw.ViewerBridge.Graph;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>The memory cloud: deterministic UMAP layout, incremental placement of new memories, the bot's clusters, the route.</summary>
public class MemoryCloudTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Dims = 32;

    /// <summary><paramref name="clusters"/> well-separated Gaussian blobs on the unit sphere, <paramref name="per"/> points each.</summary>
    private static (List<CloudLeaf> Leaves, Dictionary<int, int> Truth, float[][] Centres) Blobs(int clusters, int per, long seed = 7, int firstIndex = 0, long insertedSeq = 0,
        float[][]? centres = null)
    {
        var rng = new Rng(seed);
        centres ??= Enumerable.Range(0, clusters).Select(_ => VectorMath.Normalized(Enumerable.Range(0, Dims).Select(_ => (float)rng.NextGaussian()).ToArray())).ToArray();
        var leaves = new List<CloudLeaf>();
        var truth = new Dictionary<int, int>();
        int idx = firstIndex;
        for (int c = 0; c < clusters; c++)
            for (int i = 0; i < per; i++)
            {
                var v = centres[c].Select(x => x + 0.08f * (float)rng.NextGaussian()).ToArray();
                truth[idx] = c;
                leaves.Add(new CloudLeaf(idx++, v, insertedSeq == 0 ? 0 : insertedSeq + i));
            }
        return (leaves, truth, centres);
    }

    private static CloudLayoutOptions Fast => new() { NNeighbors = 10 };

    private static int Nearest(IReadOnlyDictionary<int, (float X, float Y)> pos, int self, IEnumerable<int> among)
    {
        var p = pos[self];
        return among.Where(i => i != self).OrderBy(i => Math.Pow(pos[i].X - p.X, 2) + Math.Pow(pos[i].Y - p.Y, 2)).ThenBy(i => i).First();
    }

    [Fact]
    public void The_layout_is_deterministic_and_keeps_neighbourhoods()
    {
        var (leaves, truth, _) = Blobs(5, 40);
        var a = CloudLayout.Compute(42, leaves, null, Fast);
        var b = CloudLayout.Compute(42, leaves, null, Fast);
        Assert.True(a.Full);
        Assert.Equal(leaves.Count, a.Positions.Count);
        foreach (var l in leaves) Assert.Equal(a.Positions[l.Index], b.Positions[l.Index]);
        foreach (var (x, y) in a.Positions.Values)
        {
            Assert.InRange(x, 0f, 1f);
            Assert.InRange(y, 0f, 1f);
        }
        // Each point's nearest 2-D neighbour shares its blob.
        int same = leaves.Count(l => truth[Nearest(a.Positions, l.Index, truth.Keys)] == truth[l.Index]);
        Assert.True(same >= leaves.Count * 0.95, $"{same}/{leaves.Count} nearest neighbours in the same blob");
    }

    [Fact]
    public void New_memories_are_placed_near_their_neighbours_without_moving_anything()
    {
        var (build, truth, centres) = Blobs(4, 40);
        var first = CloudLayout.Compute(9, build, null, Fast);
        var before = build.ToDictionary(l => l.Index, l => first.Positions[l.Index]);

        // Five new memories that belong to blob 2, inserted after the build.
        var (fresh, _, _) = Blobs(1, 5, seed: 99, firstIndex: 1000, insertedSeq: 1, centres: [centres[2]]);
        var all = build.Concat(fresh).ToList();
        var second = CloudLayout.Compute(9, all, first.Anchors, Fast);
        Assert.False(second.Full);
        Assert.Equal(5, second.PlacedNow);
        foreach (var l in build) Assert.Equal(before[l.Index], second.Positions[l.Index]);
        foreach (var f in fresh) Assert.Equal(2, truth[Nearest(second.Positions, f.Index, truth.Keys)]);

        // A restart computes the same picture at once (placement depends on the build's anchors only).
        var restart = CloudLayout.Compute(9, all, null, Fast);
        foreach (var l in all) Assert.Equal(second.Positions[l.Index], restart.Positions[l.Index]);

        // Another refresh with nothing new places nothing; a re-embedded memory is placed again.
        Assert.Equal(0, CloudLayout.Compute(9, all, second.Anchors, Fast).PlacedNow);
        var moved = all.Select(l => l.Index == 1000 ? l with { Vector = centres[0].ToArray() } : l).ToList();
        var third = CloudLayout.Compute(9, moved, second.Anchors, Fast);
        Assert.Equal(1, third.PlacedNow);
        Assert.Equal(0, truth[Nearest(third.Positions, 1000, truth.Keys)]);

        // A new RAPTOR build lays everything out again.
        Assert.True(CloudLayout.Compute(10, all, third.Anchors, Fast).Full);
    }

    [Fact]
    public void Big_builds_run_umap_on_a_sample_and_place_the_rest()
    {
        var (leaves, truth, _) = Blobs(3, 50);
        var r = CloudLayout.Compute(1, leaves, null, Fast with { MaxUmapPoints = 60 });
        Assert.Equal(60, r.Anchors.Index.Length);
        Assert.Equal(leaves.Count - 60, r.PlacedNow);
        int same = leaves.Count(l => truth[Nearest(r.Positions, l.Index, truth.Keys)] == truth[l.Index]);
        Assert.True(same >= leaves.Count * 0.9, $"{same}/{leaves.Count}");
    }

    [Fact]
    public void Leaves_take_the_cluster_of_their_ancestor_at_the_cluster_layer()
    {
        // 2 roots (L2) over 6 L1 clusters over 36 leaves: L2 has too few nodes, so the clusters are the six L1 summaries.
        var specs = new List<NodeSpec>();
        string[] topics = ["cobble generator lava", "wheat farm seeds", "oak tree logs", "chest storage items", "nether portal obsidian", "mob grinder bones"];
        for (int c = 0; c < 6; c++)
            for (int i = 0; i < 6; i++)
                specs.Add(new NodeSpec(c * 6 + i, 0, [], $"{topics[c]} memory {i}", Kind: "episode", Title: $"{topics[c]} #{i}"));
        for (int c = 0; c < 6; c++) specs.Add(new NodeSpec(100 + c, 1, Enumerable.Range(c * 6, 6).ToArray(), $"summary about {topics[c]}"));
        specs.Add(new NodeSpec(200, 2, [100, 101, 102], "summary of the overworld"));
        specs.Add(new NodeSpec(201, 2, [103, 104, 105], "summary of the rest"));
        var g = GraphModel.FromSpecs(specs, 2);
        TopicLabeler.Label(g);
        Assert.Equal(1, MemoryCloudService.ClusterLayer(g));

        var leaves = Enumerable.Range(0, 36).Select(i => new CloudLeaf(i, [1, 0], 0)).ToList();
        var pos = leaves.ToDictionary(l => l.Index, l => ((float)(l.Index / 6) / 6 + 0.05f, 0.5f));
        var parents = specs.SelectMany(s => s.Children.Select(c => (c, s.Index))).GroupBy(x => x.c).ToDictionary(x => x.Key, x => x.Select(y => y.Index).ToArray());
        var dto = MemoryCloudService.Assemble(g, leaves, parents, pos, "k", "v");
        Assert.Equal(6, dto.Clusters.Count);
        Assert.All(dto.Clusters, c => Assert.Equal(6, c.Size));
        for (int i = 0; i < 36; i++)
        {
            var cluster = dto.Clusters[dto.Points.Cluster[i]];
            Assert.Equal(100 + dto.Points.Id[i] / 6, cluster.Node);
        }
        Assert.Contains("cobble", dto.Clusters.Single(c => c.Node == 100).Label);
        // Summaries sit at their children's mean; the roots too.
        int s100 = Array.IndexOf(dto.Summaries.Id, 100), s200 = Array.IndexOf(dto.Summaries.Id, 200);
        Assert.Equal(0.05f, dto.Summaries.Xy[2 * s100], 3);
        Assert.Equal(1, dto.Summaries.Layer[s100]);
        Assert.Equal(2, dto.Summaries.Layer[s200]);
        Assert.Equal(-1, dto.Summaries.Cluster[s200]);
        Assert.Equal([100], dto.Points.Parents[0]);
    }

    [Fact]
    public async Task The_bounded_runner_caps_parallel_loops()
    {
        int live = 0, peak = 0;
        var runner = new BoundedRunner(2);
        await runner.Run(() =>
        {
            Parallel.For(0, 64, new ParallelOptions { TaskScheduler = TaskScheduler.Current }, _ =>
            {
                int now = Interlocked.Increment(ref live);
                int p;
                while ((p = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, p) != p) { }
                Thread.Sleep(5);
                Interlocked.Decrement(ref live);
            });
            return 0;
        });
        Assert.InRange(peak, 1, 2);
    }

    [Fact]
    public async Task The_route_answers_at_once_then_serves_the_cached_layout()
    {
        var fx = await FixtureDb.SharedAsync();
        await using var h = await BridgeHost.StartAsync(fx.Path, new HashEmbeddings());
        var (s, body) = await h.GetAsync("/v1/memory-activity/cloud", roles: "viewer");
        Assert.Equal(200, s);
        var first = JsonDocument.Parse(body).RootElement;
        Assert.Contains(first.GetProperty("status").GetString(), new[] { "computing", "ready" });

        await h.Services.GetRequiredService<MemoryCloudService>().Idle();
        var ready = JsonDocument.Parse((await h.GetAsync("/v1/memory-activity/cloud", roles: "viewer")).Body).RootElement;
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        var pts = ready.GetProperty("points");
        int n = pts.GetProperty("id").GetArrayLength();
        Assert.True(n > 20, $"{n} points");
        Assert.Equal(2 * n, pts.GetProperty("xy").GetArrayLength());
        int clusters = ready.GetProperty("clusters").GetArrayLength();
        Assert.True(clusters >= 1);
        foreach (var c in pts.GetProperty("cluster").EnumerateArray()) Assert.InRange(c.GetInt32(), -1, clusters - 1);
        foreach (var v in pts.GetProperty("xy").EnumerateArray()) Assert.InRange(v.GetDouble(), 0, 1);
        var graph = JsonDocument.Parse((await h.GetAsync("/v1/raptor/graph")).Body).RootElement;
        Assert.Equal(graph.GetProperty("state_key").GetString(), ready.GetProperty("state_key").GetString());

        var again = JsonDocument.Parse((await h.GetAsync("/v1/memory-activity/cloud", roles: "viewer")).Body).RootElement;
        Assert.Equal(ready.GetProperty("version").GetString(), again.GetProperty("version").GetString());
        Assert.Equal(pts.GetProperty("xy").GetRawText(), again.GetProperty("points").GetProperty("xy").GetRawText());

        // The RAPTOR tree page's route is gone (its UI moved into Memory activity).
        Assert.Equal(404, (await h.GetAsync("/v1/raptor/tree")).Status);
    }

    /// <summary>Timing on a real agent db (a copy; opened read-only): set MINECLAW_CLOUD_BENCH_DB to its path.</summary>
    [Fact]
    public async Task Bench_on_a_real_db_when_given()
    {
        var path = Environment.GetEnvironmentVariable("MINECLAW_CLOUD_BENCH_DB");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { output.WriteLine("skipped: MINECLAW_CLOUD_BENCH_DB not set"); return; }
        await using var h = await BridgeHost.StartAsync(path);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (s, first) = await h.GetAsync("/v1/memory-activity/cloud", roles: "viewer");
        output.WriteLine($"first answer {s} in {sw.ElapsedMilliseconds} ms: {first[..Math.Min(120, first.Length)]}");
        await h.Services.GetRequiredService<MemoryCloudService>().Idle();
        var (_, body) = await h.GetAsync("/v1/memory-activity/cloud", roles: "viewer");
        var r = JsonDocument.Parse(body).RootElement;
        output.WriteLine($"ready after {sw.ElapsedMilliseconds} ms: {r.GetProperty("points").GetProperty("id").GetArrayLength()} points, " +
            $"{r.GetProperty("clusters").GetArrayLength()} clusters at L{r.GetProperty("cluster_layer").GetInt32()}, anchors {r.GetProperty("anchors").GetInt32()}, " +
            $"compute {r.GetProperty("compute_ms").GetDouble()} ms, {body.Length / 1024} KiB");
        foreach (var c in r.GetProperty("clusters").EnumerateArray().Take(16))
            output.WriteLine($"  {c.GetProperty("size").GetInt32(),5}  {c.GetProperty("label").GetString()}");
    }
}
