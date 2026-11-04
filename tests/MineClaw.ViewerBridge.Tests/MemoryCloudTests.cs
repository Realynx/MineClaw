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

    private static CloudLayoutOptions Fast => new() { NNeighbors = 10, Arrangement = CloudArrangements.Umap };

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

    // ---------------- distance-preserving arrangements ----------------

    private const int BigDims = 1024;

    /// <summary>
    /// Four Gaussian clusters on the 1024-d unit sphere with known centre geometry: cos(c0, c1) = 0.8, cos(c0, c2) = 0.3, c3 orthogonal
    /// to all (cosine distances 0.2, 0.7, 1.0 from c0). Within a cluster cos ≈ 0.9. Leaves under two layer-1 summaries per cluster,
    /// one layer-2 summary per pair of clusters, one root.
    /// </summary>
    private static (List<CloudLeaf> Leaves, Dictionary<int, int> Truth, float[][] Centres, CloudTreeInput Tree) Known(int per = 60, long seed = 3,
        int firstIndex = 0, long insertedSeq = 0, int[]? only = null)
    {
        var rng = new Rng(seed);
        var basis = new float[4][];
        var brng = new Rng(1234); // the geometry is fixed whatever the sample seed
        for (int k = 0; k < 4; k++)
        {
            var v = Enumerable.Range(0, BigDims).Select(_ => (float)brng.NextGaussian()).ToArray();
            for (int p = 0; p < k; p++)
            {
                float dot = VectorMath.Dot(v, basis[p]);
                for (int d = 0; d < BigDims; d++) v[d] -= dot * basis[p][d];
            }
            basis[k] = VectorMath.Normalized(v);
        }
        float[] Mix(params (float W, int K)[] parts) => VectorMath.Normalized(Enumerable.Range(0, BigDims).Select(d => parts.Sum(p => p.W * basis[p.K][d])).ToArray());
        var centres = new[] { basis[0], Mix((0.8f, 0), (0.6f, 1)), Mix((0.3f, 0), (MathF.Sqrt(1 - 0.09f), 2)), basis[3] };
        var leaves = new List<CloudLeaf>();
        var truth = new Dictionary<int, int>();
        var parents = new Dictionary<int, int[]>();
        var layer = new Dictionary<int, int>();
        int idx = firstIndex;
        foreach (var c in only ?? [0, 1, 2, 3])
            for (int i = 0; i < per; i++)
            {
                var v = centres[c].Select(x => x + 0.0104f * (float)rng.NextGaussian()).ToArray();
                truth[idx] = c;
                parents[idx] = [10_000 + c * 2 + i % 2];
                layer[idx] = 0;
                leaves.Add(new CloudLeaf(idx++, v, insertedSeq == 0 ? 0 : insertedSeq + i));
            }
        for (int c = 0; c < 4; c++)
        {
            for (int h = 0; h < 2; h++) { parents[10_000 + c * 2 + h] = [20_000 + c / 2]; layer[10_000 + c * 2 + h] = 1; }
            parents[20_000 + c / 2] = [30_000];
            layer[20_000 + c / 2] = 2;
        }
        parents[30_000] = [];
        layer[30_000] = 3;
        return (leaves, truth, centres, new CloudTreeInput(parents, layer, new Dictionary<int, (float X, float Y)>()));
    }

    private static (double X, double Y) Centroid(IReadOnlyDictionary<int, (float X, float Y)> pos, Dictionary<int, int> truth, int c)
    {
        var pts = truth.Where(t => t.Value == c).Select(t => pos[t.Key]).ToList();
        return (pts.Average(p => p.X), pts.Average(p => p.Y));
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static CloudLayoutOptions Arr(string a) => new() { Arrangement = a, NNeighbors = 10 };

    [Fact]
    public void Mds_draws_embedding_distances_on_known_geometry()
    {
        var (leaves, truth, _, _) = Known();
        var r = CloudLayout.Compute(1, leaves, null, Arr(CloudArrangements.Mds));
        var f = CloudFidelity.Measure(leaves, r.Positions, 7, maxPairs: 50_000);
        output.WriteLine($"mds: r={f.Pearson} rho={f.Spearman} stress={f.Stress} unit={f.UnitDistance} fit {r.Anchors.FitMs:0} ms ({r.Anchors.FitIterations} SMACOF iterations)");
        Assert.True(f.Pearson >= 0.95, $"pearson {f.Pearson}");
        Assert.True(f.Spearman >= 0.8, $"spearman {f.Spearman}");
        Assert.True(f.Stress <= 0.2, $"stress {f.Stress}");
        Assert.True(f.Pairs > 20_000);

        // Gaps are true: c1 (0.2 away) drawn nearer c0 than c2 (0.7), c2 nearer than c3 (1.0), each about the right size on the scale.
        var c = Enumerable.Range(0, 4).Select(k => Centroid(r.Positions, truth, k)).ToArray();
        double d01 = Dist(c[0], c[1]), d02 = Dist(c[0], c[2]), d03 = Dist(c[0], c[3]);
        Assert.True(d01 < d02 && d02 < d03, $"{d01} {d02} {d03}");
        Assert.InRange(d01 * f.UnitDistance, 0.12, 0.3);
        Assert.InRange(d02 * f.UnitDistance, 0.55, 0.85);
        Assert.InRange(d03 * f.UnitDistance, 0.8, 1.2);

        // Every arrangement is measured the same way; UMAP is not asserted to keep distances (it does not).
        var u = CloudLayout.Compute(1, leaves, null, Arr(CloudArrangements.Umap));
        output.WriteLine($"umap: {CloudFidelity.Measure(leaves, u.Positions, 7)}");
    }

    [Fact]
    public async Task Each_arrangement_is_deterministic()
    {
        var (leaves, _, _, tree0) = Known(per: 30);
        var mds = CloudLayout.Compute(4, leaves, null, Arr(CloudArrangements.Mds));
        var tree = tree0 with { Seed = mds.Positions };
        foreach (var a in CloudArrangements.Ids)
        {
            var x = CloudLayout.Compute(4, leaves, null, Arr(a), default, tree);
            var y = CloudLayout.Compute(4, leaves, null, Arr(a), default, tree);
            Assert.Equal(leaves.Count, x.Positions.Count);
            foreach (var l in leaves) Assert.Equal(x.Positions[l.Index], y.Positions[l.Index]);
            foreach (var (px, py) in x.Positions.Values)
            {
                Assert.InRange(px, 0f, 1f);
                Assert.InRange(py, 0f, 1f);
            }
            Assert.Equal(CloudFidelity.Measure(leaves, x.Positions, 1), CloudFidelity.Measure(leaves, y.Positions, 1) with { });
        }
        // The thread count does not change the MDS picture (one row per task, fixed summation order).
        var wide = await new BoundedRunner(4).Run(() => CloudLayout.Compute(4, leaves, null, Arr(CloudArrangements.Mds)));
        foreach (var l in leaves) Assert.Equal(mds.Positions[l.Index], wide.Positions[l.Index]);
    }

    [Fact]
    public void Mds_places_new_memories_by_their_distances_without_moving_anything()
    {
        var (build, truth, _, _) = Known(per: 40);
        var o = Arr(CloudArrangements.Mds);
        var first = CloudLayout.Compute(9, build, null, o);
        var (fresh, _, _, _) = Known(per: 5, seed: 77, firstIndex: 5000, insertedSeq: 1, only: [2]);
        var all = build.Concat(fresh).ToList();
        var second = CloudLayout.Compute(9, all, first.Anchors, o);
        Assert.False(second.Full);
        Assert.Equal(5, second.PlacedNow);
        foreach (var l in build) Assert.Equal(first.Positions[l.Index], second.Positions[l.Index]);
        foreach (var f in fresh) Assert.Equal(2, truth[Nearest(second.Positions, f.Index, truth.Keys)]);
        var restart = CloudLayout.Compute(9, all, null, o);
        foreach (var l in all) Assert.Equal(second.Positions[l.Index], restart.Positions[l.Index]);
        // Sampled anchors: the rest minimise their own stress and still land by their cluster, distances intact.
        var sampled = CloudLayout.Compute(9, build, null, o with { MaxMdsPoints = 100 });
        Assert.Equal(100, sampled.Anchors.Index.Length);
        Assert.Equal(build.Count - 100, sampled.PlacedNow);
        Assert.True(CloudFidelity.Measure(build, sampled.Positions, 3).Pearson >= 0.9);
    }

    [Fact]
    public void Pca_reports_variance_explained_and_projects_new_memories_exactly()
    {
        var (leaves, _, _, _) = Known(per: 40);
        var r = CloudLayout.Compute(2, leaves, null, Arr(CloudArrangements.Pca));
        var ve = r.Anchors.VarianceExplained!;
        output.WriteLine($"pca variance explained {string.Join(", ", ve)}; {CloudFidelity.Measure(leaves, r.Positions, 1)}");
        Assert.Equal(2, ve.Length);
        Assert.True(ve[0] >= ve[1] && ve[1] > 0.05, string.Join(",", ve));
        Assert.True(ve[0] + ve[1] <= 1.0001);
        // Three of the four centres span 3 of 4 directions: the top two axes hold most of the variance.
        Assert.True(ve[0] + ve[1] > 0.5, string.Join(",", ve));
        var (fresh, _, _, _) = Known(per: 3, seed: 5, firstIndex: 900, insertedSeq: 1, only: [3]);
        var next = CloudLayout.Compute(2, leaves.Concat(fresh).ToList(), r.Anchors, Arr(CloudArrangements.Pca));
        Assert.Equal(3, next.PlacedNow);
        foreach (var l in leaves) Assert.Equal(r.Positions[l.Index], next.Positions[l.Index]);
    }

    [Fact]
    public void The_tree_arrangement_keeps_children_near_their_parents()
    {
        var (leaves, _, _, tree0) = Known(per: 40);
        var mds = CloudLayout.Compute(3, leaves, null, Arr(CloudArrangements.Mds));
        var tree = tree0 with { Seed = mds.Positions };
        var r = CloudLayout.Compute(3, leaves, null, Arr(CloudArrangements.Tree), default, tree);
        var sxy = r.Anchors.SummaryXy!;
        double childParent = leaves.Average(l => Dist(r.Positions[l.Index], sxy[tree.Parents[l.Index][0]]));
        var rng = new Rng(1);
        double random = Enumerable.Range(0, 2000).Average(_ => Dist(r.Positions[leaves[rng.NextInt(leaves.Count)].Index], r.Positions[leaves[rng.NextInt(leaves.Count)].Index]));
        double l1ToL2 = Enumerable.Range(0, 8).Average(s => Dist(sxy[10_000 + s], sxy[tree.Parents[10_000 + s][0]]));
        output.WriteLine($"tree: leaf→parent {childParent:0.000}, L1→L2 {l1ToL2:0.000}, random pair {random:0.000}");
        Assert.True(childParent < 0.2 * random, $"{childParent} vs {random}");
        Assert.True(l1ToL2 < 0.6 * random, $"{l1ToL2} vs {random}");
        // Summaries come out of the layout (not the children's mean) and the DTO carries them.
        Assert.Equal(11, sxy.Count);
        // A memory inserted later sits next to its parent.
        var (fresh, _, _, _) = Known(per: 2, seed: 8, firstIndex: 7000, insertedSeq: 1, only: [1]);
        var parents = tree.Parents.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var f in fresh) parents[f.Index] = [10_002];
        var next = CloudLayout.Compute(3, leaves.Concat(fresh).ToList(), r.Anchors, Arr(CloudArrangements.Tree), default, tree with { Parents = parents });
        Assert.Equal(2, next.PlacedNow);
        foreach (var f in fresh) Assert.True(Dist(next.Positions[f.Index], sxy[10_002]) < 3 * childParent + 0.01);
    }

    [Fact]
    public async Task Every_arrangement_is_served_with_its_fidelity()
    {
        var fx = await FixtureDb.SharedAsync();
        await using var h = await BridgeHost.StartAsync(fx.Path, new HashEmbeddings());
        var svc = h.Services.GetRequiredService<MemoryCloudService>();
        var first = JsonDocument.Parse((await h.GetAsync("/v1/memory-activity/cloud", roles: "viewer")).Body).RootElement;
        Assert.Equal("mds", first.GetProperty("arrangement").GetString());
        foreach (var a in CloudArrangements.Ids) await h.GetAsync($"/v1/memory-activity/cloud?arrangement={a}", roles: "viewer");
        await svc.Idle();
        var xy = new Dictionary<string, string>();
        foreach (var a in CloudArrangements.Ids)
        {
            var r = JsonDocument.Parse((await h.GetAsync($"/v1/memory-activity/cloud?arrangement={a}", roles: "viewer")).Body).RootElement;
            Assert.Equal("ready", r.GetProperty("status").GetString());
            Assert.Equal(a, r.GetProperty("arrangement").GetString());
            var f = r.GetProperty("fidelity");
            Assert.InRange(f.GetProperty("pearson").GetDouble(), -1, 1);
            Assert.InRange(f.GetProperty("spearman").GetDouble(), -1, 1);
            Assert.True(f.GetProperty("stress").GetDouble() >= 0);
            Assert.True(f.GetProperty("unit_distance").GetDouble() > 0);
            Assert.Equal("cosine", f.GetProperty("metric").GetString());
            Assert.Equal(a == "pca", f.TryGetProperty("variance_explained", out var ve) && ve.ValueKind == JsonValueKind.Array);
            var arr = r.GetProperty("arrangements").EnumerateArray().ToList();
            Assert.Equal(CloudArrangements.Ids, arr.Select(x => x.GetProperty("id").GetString()).ToArray());
            Assert.All(arr, x => Assert.Equal("ready", x.GetProperty("status").GetString()));
            Assert.Equal("Distances (MDS)", arr.Single(x => x.GetProperty("id").GetString() == "mds").GetProperty("label").GetString());
            xy[a] = r.GetProperty("points").GetProperty("xy").GetRawText();
            output.WriteLine($"{a}: {f.GetRawText()}");
        }
        Assert.NotEqual(xy["mds"], xy["umap"]);
        Assert.NotEqual(xy["mds"], xy["pca"]);
        Assert.Equal(400, (await h.GetAsync("/v1/memory-activity/cloud?arrangement=tsne", roles: "viewer")).Status);
    }

    [Fact]
    public async Task The_distance_route_answers_the_cosine_distance_for_viewers()
    {
        var fx = await FixtureDb.SharedAsync();
        await using var h = await BridgeHost.StartAsync(fx.Path, new HashEmbeddings());
        var cloud = JsonDocument.Parse((await h.GetAsync("/v1/raptor/graph")).Body).RootElement;
        var leaves = cloud.GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("layer").GetInt32() == 0).Select(n => n.GetProperty("id").GetInt32()).Take(2).ToArray();
        var (s, body) = await h.GetAsync($"/v1/memory-activity/cloud/distance?a={leaves[0]}&b={leaves[1]}", roles: "viewer");
        Assert.Equal(200, s);
        var d = JsonDocument.Parse(body).RootElement;
        Assert.Equal("cosine", d.GetProperty("metric").GetString());
        double dist = d.GetProperty("distance").GetDouble();
        Assert.InRange(dist, 0, 2);
        Assert.Equal(1 - dist, d.GetProperty("similarity").GetDouble(), 4);
        Assert.DoesNotContain("embedding", body); // no vectors on the wire
        var back = JsonDocument.Parse((await h.GetAsync($"/v1/memory-activity/cloud/distance?a={leaves[1]}&b={leaves[0]}", roles: "viewer")).Body).RootElement;
        Assert.Equal(dist, back.GetProperty("distance").GetDouble());
        var self = JsonDocument.Parse((await h.GetAsync($"/v1/memory-activity/cloud/distance?a={leaves[0]}&b={leaves[0]}", roles: "viewer")).Body).RootElement;
        Assert.Equal(0, self.GetProperty("distance").GetDouble(), 4);
        Assert.Equal(404, (await h.GetAsync($"/v1/memory-activity/cloud/distance?a={leaves[0]}&b=99999999", roles: "viewer")).Status);
        Assert.Equal(400, (await h.GetAsync("/v1/memory-activity/cloud/distance?a=1", roles: "viewer")).Status);
        Assert.Equal(400, (await h.GetAsync("/v1/memory-activity/cloud/distance?a=x&b=1", roles: "viewer")).Status);
        Assert.Equal(403, (await h.GetAsync($"/v1/memory-activity/cloud/distance?a={leaves[0]}&b={leaves[1]}", roles: "chat")).Status);
    }

    /// <summary>Timing of every arrangement on synthetic 1024-d sets of real size (2.5k) and future size (20k): set MINECLAW_CLOUD_BENCH=1.</summary>
    [Fact]
    public void Bench_arrangements_on_synthetic_sets_when_asked()
    {
        if (Environment.GetEnvironmentVariable("MINECLAW_CLOUD_BENCH") != "1") { output.WriteLine("skipped: MINECLAW_CLOUD_BENCH not set"); return; }
        foreach (int n in new[] { 2500, 20_000 })
        {
            // 13 topic clusters with sub-topics, cos within ≈ 0.6–0.8 and between ≈ 0.2–0.4 (text-embedding-like spreads).
            var rng = new Rng(n);
            var topics = Enumerable.Range(0, 13).Select(_ => VectorMath.Normalized(Enumerable.Range(0, BigDims).Select(_ => (float)rng.NextGaussian()).ToArray())).ToArray();
            var shared = VectorMath.Normalized(Enumerable.Range(0, BigDims).Select(_ => (float)rng.NextGaussian()).ToArray());
            var leaves = new List<CloudLeaf>(n);
            var parents = new Dictionary<int, int[]>();
            var layer = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                int t = i % 13, sub = i % 91;
                var v = new float[BigDims];
                for (int d = 0; d < BigDims; d++) v[d] = 0.5f * shared[d] + 0.6f * topics[t][d] + 0.025f * (float)rng.NextGaussian() + 0.01f * ((sub * 7 + d) % 13 - 6);
                leaves.Add(new CloudLeaf(i, v, 0));
                parents[i] = [100_000 + sub];
                layer[i] = 0;
            }
            for (int s = 0; s < 91; s++) { parents[100_000 + s] = [200_000 + s % 13]; layer[100_000 + s] = 1; }
            for (int t = 0; t < 13; t++) { parents[200_000 + t] = []; layer[200_000 + t] = 2; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var mds = CloudLayout.Compute(1, leaves, null, new CloudLayoutOptions());
            var fm = CloudFidelity.Measure(leaves, mds.Positions, 1);
            output.WriteLine($"{n} mds: {sw.ElapsedMilliseconds} ms (fit {mds.Anchors.FitMs:0} ms on {mds.Anchors.Index.Length}, {mds.Anchors.FitIterations} it), {fm}");
            sw.Restart();
            var pca = CloudLayout.Compute(1, leaves, null, new CloudLayoutOptions { Arrangement = CloudArrangements.Pca });
            output.WriteLine($"{n} pca: {sw.ElapsedMilliseconds} ms, {CloudFidelity.Measure(leaves, pca.Positions, 1)}");
            sw.Restart();
            var tree = CloudLayout.Compute(1, leaves, null, new CloudLayoutOptions { Arrangement = CloudArrangements.Tree }, default, new CloudTreeInput(parents, layer, mds.Positions));
            output.WriteLine($"{n} tree: {sw.ElapsedMilliseconds} ms, {CloudFidelity.Measure(leaves, tree.Positions, 1)}");
            sw.Restart();
            var umap = CloudLayout.Compute(1, leaves, null, new CloudLayoutOptions { Arrangement = CloudArrangements.Umap });
            output.WriteLine($"{n} umap: {sw.ElapsedMilliseconds} ms, {CloudFidelity.Measure(leaves, umap.Positions, 1)}");
        }
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
        foreach (var a in CloudArrangements.Ids)
        {
            sw.Restart();
            await h.GetAsync($"/v1/memory-activity/cloud?arrangement={a}", roles: "viewer");
            await h.Services.GetRequiredService<MemoryCloudService>().Idle();
            var x = JsonDocument.Parse((await h.GetAsync($"/v1/memory-activity/cloud?arrangement={a}", roles: "viewer")).Body).RootElement;
            output.WriteLine($"{a}: {sw.ElapsedMilliseconds} ms (compute {x.GetProperty("compute_ms").GetDouble()} ms, anchors {x.GetProperty("anchors").GetInt32()}), " +
                $"fidelity {x.GetProperty("fidelity").GetRawText()}");
        }
    }
}
