using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MineClaw.Agent;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Raptor.Storage;
using MineClaw.ViewerBridge.Data;
using MineClaw.ViewerBridge.Graph;
using Xunit.Abstractions;

namespace MineClaw.ViewerBridge.Tests;

[CollectionDefinition(nameof(BridgeMemoryBudget), DisableParallelization = true)]
public sealed class BridgeMemoryBudget;

/// <summary>
/// The bridge's memory on a synthetic agent db of realistic size (20 000 leaf vectors of 384 dimensions under a two-layer RAPTOR
/// tree; the live db is 567 MB): what a bank retains, what one reload allocates the old way (a whole replacement bank per changed
/// doc, about once a minute live) and the new ways (vectors refreshed in place; a tree reload sharing the vector arrays), and the
/// memory cloud's refusal when it cannot afford a layout. Runs alone (allocation counters are process-wide). Numbers go to the test
/// output; the assertions keep generous margins.
/// </summary>
[Collection(nameof(BridgeMemoryBudget))]
public sealed class BridgeMemoryBudgetTests(ITestOutputHelper output)
{
    private const int Leaves = 20_000, Dims = 384, Fanout = 100;

    /// <summary>Docs, vectors, a two-layer tree (summaries of 100 leaves, one root) and its meta, written the way the store writes them.</summary>
    internal static void BuildSynthetic(string path, int leaves, int dims, int fanout, string model)
    {
        using var db = AgentDb.Open(path);
        var rng = new Random(11_10);
        float[] Unit()
        {
            var v = new float[dims];
            for (int i = 0; i < dims; i++) v[i] = (float)(rng.NextDouble() * 2 - 1);
            return MineClaw.Agent.Rag.VectorMath.Normalize(v);
        }
        string[] topics = ["cobblestone generator", "wheat farm", "oak sapling", "chest storage", "nether portal", "mob grinder"];
        int summaries = (leaves + fanout - 1) / fanout;
        db.Tx(() =>
        {
            for (int i = 0; i < leaves; i++)
            {
                db.Exec("""
                    INSERT INTO docs(id, ref, kind, title, body, hash, created, updated, level, raptor_idx, dirty)
                    VALUES ($id, $ref, 'episode', $title, $body, $h, 1, $u, 0, $idx, 0)
                    """, ("$id", i + 1L), ("$ref", $"ep:synthetic:{i}"), ("$title", $"episode {i}"),
                    ("$body", $"{topics[i % topics.Length]} event {i}: the colony worked near the island and noted it"), ("$h", $"h{i}"),
                    ("$u", 1_762_000_000_000L + i), ("$idx", i));
                db.Exec("INSERT INTO vectors(doc_id, model, dims, hash, vec) VALUES ($id, $m, $d, $h, $v)",
                    ("$id", i + 1L), ("$m", model), ("$d", dims), ("$h", $"h{i}"), ("$v", MineClaw.Agent.Rag.VectorMath.ToBlob(Unit())));
            }
            for (int s = 0; s <= summaries; s++)
            {
                bool root = s == summaries;
                long id = leaves + 1L + s;
                int idx = leaves + s;
                db.Exec("""
                    INSERT INTO docs(id, ref, kind, tag, title, body, hash, source, embed, created, updated, level, raptor_idx, dirty)
                    VALUES ($id, $ref, $k, $tag, $title, $body, $h, 'raptor', 0, 1, 1, $l, $idx, 0)
                    """, ("$id", id), ("$ref", AgentDbRaptorStore.SummaryRefPrefix + idx), ("$k", RagIndex.SummaryKind), ("$tag", root ? "L2" : "L1"),
                    ("$title", root ? "summary L2" : "summary L1"), ("$body", root ? "everything the colony did" : $"summary of {topics[s % topics.Length]} memories {s}"),
                    ("$h", $"s{s}"), ("$l", root ? 2 : 1), ("$idx", idx));
                db.Exec("INSERT INTO vectors(doc_id, model, dims, hash, vec) VALUES ($id, $m, $d, $h, $v)",
                    ("$id", id), ("$m", model), ("$d", dims), ("$h", $"s{s}"), ("$v", MineClaw.Agent.Rag.VectorMath.ToBlob(Unit())));
                if (root)
                    for (int c = 0; c < summaries; c++) db.Exec("INSERT INTO doc_edges(parent, child, weight) VALUES ($p, $c, 1.0)", ("$p", id), ("$c", leaves + 1L + c));
                else
                    for (int c = s * fanout; c < Math.Min(leaves, (s + 1) * fanout); c++)
                        db.Exec("INSERT INTO doc_edges(parent, child, weight) VALUES ($p, $c, 1.0)", ("$p", id), ("$c", c + 1L));
            }
            var meta = new RaptorTreeMeta(2, leaves + summaries + 1, dims, model, 1, DateTimeOffset.FromUnixTimeSeconds(1_762_000_000), leaves, 0, 0, 0.5, 0, 0, 0);
            db.SetMeta(AgentDbRaptorStore.MetaKey, JsonSerializer.Serialize(meta));
        });
    }

    private static void Collect()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    /// <summary>A bank with what opening it retained and allocated. A class, so the bank can be released (<see cref="Release"/>); opened
    /// synchronously, because a (Debug) async state machine keeps its locals alive and would count one bank into the next measurement.</summary>
    private sealed class Measured(MemoryBank bank, long retained, long allocated)
    {
        public MemoryBank Bank { get; private set; } = bank;
        public long Retained => retained;
        public long Allocated => allocated;
        public void Release() { Bank.Dispose(); Bank = null!; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Measured Open(string path, IEmbeddingProvider provider, bool share, MemoryBank? previous = null)
    {
        Collect();
        long before = GC.GetTotalMemory(true), allocated = GC.GetTotalAllocatedBytes(precise: true);
        var bank = MemoryBank.OpenAsync(path, new AgentOptions(), provider, previous: previous, shareTreeVectors: share).GetAwaiter().GetResult();
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        Collect();
        return new Measured(bank, GC.GetTotalMemory(true) - before, allocated);
    }

    [Fact]
    public async Task Bank_memory_and_reload_cost_on_a_realistic_db()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-bridge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "synthetic.db");
        try
        {
            var provider = new HashEmbeddings(Dims);
            var sw = Stopwatch.StartNew();
            BuildSynthetic(path, Leaves, Dims, Fanout, provider.Model);
            output.WriteLine($"synthetic db: {Leaves} leaves x {Dims} dims, {MemoryBank.FileSize(path) / (1024 * 1024)} MB on disk, built in {sw.ElapsedMilliseconds} ms");
            long payload = (Leaves + Leaves / Fanout + 1L) * Dims * sizeof(float);

            // 1. Retained by one bank: the tree used to decode a second copy of every vector the flat index already holds.
            var unshared = Open(path, provider, share: false);
            Assert.True(unshared.Bank.HasTree, unshared.Bank.TreeNote);
            Assert.Equal(Leaves + Leaves / Fanout + 1, unshared.Bank.Raptor!.Index.NodeCount);
            unshared.Release();
            var opened = Open(path, provider, share: true);
            var bank = opened.Bank;
            output.WriteLine($"vector payload {Mb(payload)}; bank retains {Mb(unshared.Retained)} before, {Mb(opened.Retained)} now " +
                $"(saved {Mb(unshared.Retained - opened.Retained)}); opening it allocates {Mb(unshared.Allocated)} before, {Mb(opened.Allocated)} now");
            Assert.True(unshared.Retained - opened.Retained >= payload * 8 / 10, "the tree shares the flat index's vector arrays");

            // 2. One reload. Before: any changed doc opened a whole replacement bank (vectors reused, the tree decoded again), about
            // once a minute live, with the replaced bank alive for 30 s more.
            var oldWay = Open(path, provider, share: false, previous: bank);
            oldWay.Release();
            // Now, a doc or vector change: the vectors are refreshed in place (one re-embedded doc, one new one).
            using (var writer = AgentDb.Open(path))
            {
                writer.Exec("UPDATE docs SET hash = 'h0b', updated = updated + 5 WHERE id = 1");
                writer.Exec("UPDATE vectors SET hash = 'h0b', vec = $v WHERE doc_id = 1", ("$v", MineClaw.Agent.Rag.VectorMath.ToBlob(provider.Embed("re-embedded"))));
                writer.Exec("INSERT INTO docs(ref, kind, title, body, hash, created, updated) VALUES ('ep:new', 'episode', 'new', 'a new memory', 'hn', 1, 2)");
                writer.Exec("INSERT INTO vectors(doc_id, model, dims, hash, vec) SELECT id, $m, $d, 'hn', $v FROM docs WHERE ref = 'ep:new'",
                    ("$m", provider.Model), ("$d", Dims), ("$v", MineClaw.Agent.Rag.VectorMath.ToBlob(provider.Embed("a new memory"))));
            }
            Collect();
            long refreshAllocated = GC.GetTotalAllocatedBytes(precise: true);
            var signature = BankHost.Signatures(bank.Db);
            var (changed, removed) = bank.Rag.RefreshVectors();
            refreshAllocated = GC.GetTotalAllocatedBytes(precise: true) - refreshAllocated;
            Assert.Equal((2, 0), (changed, removed));
            Assert.NotEmpty(signature.Docs);
            // A tree change: a replacement bank whose tree shares the arrays.
            var treeWay = Open(path, provider, share: true, previous: bank);
            treeWay.Release();
            output.WriteLine($"one reload allocates: old {Mb(oldWay.Allocated)} (a whole bank per changed doc); now {Mb(refreshAllocated)} for a doc change, " +
                $"{Mb(treeWay.Allocated)} for a tree change");
            output.WriteLine($"a replacement bank adds beside the live one (both alive for the 30 s grace): old {Mb(oldWay.Retained)}, now {Mb(treeWay.Retained)}");
            Assert.True(refreshAllocated * 20 < oldWay.Allocated, $"an in-place refresh ({refreshAllocated} B) costs a small fraction of a reload ({oldWay.Allocated} B)");
            Assert.True(oldWay.Allocated - treeWay.Allocated >= payload * 7 / 10, "a tree reload no longer decodes the vectors again");
            Assert.True(oldWay.Retained - treeWay.Retained >= payload * 7 / 10, "a replacement bank no longer holds its own copy of the tree's vectors");

            // 3. The memory cloud refuses a layout it cannot afford instead of throwing OutOfMemoryException into the rest of the process.
            var cloud = new MemoryCloudService(BridgeConfig.ForTests(new BridgeOptions { DbPath = path }), NullLogger<MemoryCloudService>.Instance, TimeProvider.System)
            {
                Headroom = () => 64L * 1024 * 1024,
            };
            long need = MemoryCloudService.EstimateBytes(CloudArrangements.Mds, Leaves, Dims, cloud.OptionsFor(CloudArrangements.Mds));
            output.WriteLine($"MDS layout estimate for {Leaves} leaves: {Mb(need)} (+{Mb(MemoryCloudService.SafetyBytes)} kept free)");
            Assert.Equal("computing", cloud.Get(bank, CloudArrangements.Mds).Status);
            await cloud.Idle();
            var refused = cloud.Get(bank, CloudArrangements.Mds);
            Assert.Equal("error", refused.Status);
            Assert.Contains("short of memory", refused.Note);
            opened.Release();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
