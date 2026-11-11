using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MineClaw.Agent.Rag;
using MineClaw.ViewerBridge.Data;
using MineClaw.ViewerBridge.Graph;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// Live 11-10: the bridge rebuilt its whole bank (vectors, RAPTOR tree, docs) about once a minute, because the bot touches some
/// doc's <c>updated</c> all the time and <c>/v1/health</c> drove the checks; with the replaced bank kept 30 s it sat at its 600 MB cap.
/// Now a doc or vector change is refreshed in place, only a tree change opens a replacement, and the health probe drives nothing.
/// </summary>
public sealed class BankIncrementalReloadTests
{
    internal static Dictionary<long, float[]> StoredVectors(RagIndex rag)
    {
        var entries = (IDictionary)typeof(RagIndex).GetField("_vecs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rag)!;
        lock (typeof(RagIndex).GetField("_vecLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rag)!)
        {
            var result = new Dictionary<long, float[]>();
            var iterator = entries.GetEnumerator();
            while (iterator.MoveNext()) result.Add((long)iterator.Key, (float[])iterator.Value!.GetType().GetProperty("Vec")!.GetValue(iterator.Value)!);
            return result;
        }
    }

    private sealed class ReloadCounter : ILogger<BankHost>
    {
        private int _reloads, _refreshes;
        public int Reloads => Volatile.Read(ref _reloads);
        public int Refreshes => Volatile.Read(ref _refreshes);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            var text = format(state, error);
            if (text.StartsWith("reloaded the agent db", StringComparison.Ordinal)) Interlocked.Increment(ref _reloads);
            if (text.StartsWith("refreshed the agent db vectors in place", StringComparison.Ordinal)) Interlocked.Increment(ref _refreshes);
        }
    }

    private static async Task Until(Func<bool> done, string what)
    {
        var stop = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > stop) Assert.Fail("timed out waiting until " + what);
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task A_doc_change_is_refreshed_in_place_the_health_probe_drives_nothing_and_a_tree_change_replaces_the_bank()
    {
        using var fixture = await FixtureDb.CreateAsync();
        var provider = new HashEmbeddings();
        var log = new ReloadCounter();
        var config = BridgeConfig.ForTests(new BridgeOptions { DbPath = fixture.Path, ReloadCheckSeconds = 1, TreeReloadSeconds = 0 });
        using var host = new BankHost(config, log) { Embeddings = provider };
        var bank = await host.GetAsync(CancellationToken.None);
        var before = StoredVectors(bank.Rag);
        Assert.NotEmpty(before);

        // Docs outside the tree: one re-embedded, one whose vector is deleted, and a new one.
        long changedId, goneId, newId;
        var changed = provider.Embed("tall glass greenhouse with melons");
        var added = provider.Embed("a new episode about a lighthouse");
        using (var writer = AgentDb.Open(fixture.Path))
        {
            var outside = writer.Query("SELECT d.id FROM docs d JOIN vectors v ON v.doc_id = d.id WHERE d.raptor_idx IS NULL ORDER BY d.id", r => r.GetInt64(0));
            Assert.True(outside.Count >= 2, "the fixture has docs outside the tree");
            (changedId, goneId) = (outside[0], outside[^1]);
            writer.Exec("UPDATE docs SET hash = 'changed-vector', updated = updated + 1 WHERE id = $id", ("$id", changedId));
            writer.Exec("UPDATE vectors SET hash = 'changed-vector', vec = $v WHERE doc_id = $id", ("$id", changedId), ("$v", VectorMath.ToBlob(changed)));
            writer.Exec("DELETE FROM vectors WHERE doc_id = $id", ("$id", goneId));
            writer.Exec("""
                INSERT INTO docs(ref, kind, title, body, hash, created, updated) VALUES ('ep:new', 'episode', 'lighthouse', 'a new episode', 'new-hash', 1, 1)
                """);
            newId = writer.ScalarLong("SELECT id FROM docs WHERE ref = 'ep:new'");
            writer.Exec("INSERT INTO vectors(doc_id, model, dims, hash, vec) VALUES ($id, $m, $d, 'new-hash', $v)",
                ("$id", newId), ("$m", provider.Model), ("$d", provider.Dimensions), ("$v", VectorMath.ToBlob(added)));
        }
        await Task.Delay(1100);

        // The health probe opens the bank but starts no check.
        Assert.Same(bank, await host.GetAsync(CancellationToken.None, check: false));
        await Task.Delay(400);
        Assert.Equal(before.Count, StoredVectors(bank.Rag).Count);
        Assert.Equal(0, log.Refreshes);

        // A read route checks: the vectors are refreshed in the same bank.
        Assert.Same(bank, await host.GetAsync(CancellationToken.None));
        await Until(() => log.Refreshes == 1, "the vectors were refreshed");
        var after = StoredVectors(bank.Rag);
        Assert.Same(bank, await host.GetAsync(CancellationToken.None));
        Assert.Equal(0, log.Reloads);
        Assert.Equal(changed, after[changedId]);
        Assert.Equal(added, after[newId]);
        Assert.False(after.ContainsKey(goneId));
        foreach (var (id, vector) in before)
            if (id != changedId && id != goneId) Assert.Same(vector, after[id]); // untouched entries are not decoded again
        Assert.Equal(before.Count, after.Count); // one gone, one new

        // The tree changed: a replacement bank, whose tree shares the flat index's arrays.
        using (var writer = AgentDb.Open(fixture.Path))
            writer.Exec("UPDATE meta SET value = value || ' ' WHERE key = 'raptor_meta'");
        await Task.Delay(1100);
        MemoryBank latest = bank;
        await Until(() => !ReferenceEquals(latest = host.GetAsync(CancellationToken.None).GetAwaiter().GetResult(), bank), "the tree was reloaded");
        Assert.Equal(1, log.Reloads);
        var shared = StoredVectors(latest.Rag);
        Assert.Same(after[changedId], shared[changedId]);
        int nodes = latest.ReadTree(t =>
        {
            foreach (var n in t.AllNodes.Values)
                Assert.Same(shared[latest.DocOf(n.Index)?.Id ?? -1], n.Embedding);
            return t.Count;
        }, 0);
        Assert.Equal(bank.Raptor!.Index.NodeCount, nodes);
    }

    [Fact]
    public async Task A_tree_change_waits_for_the_tree_reload_interval_while_vector_changes_still_refresh()
    {
        using var fixture = await FixtureDb.CreateAsync();
        var provider = new HashEmbeddings();
        var log = new ReloadCounter();
        var config = BridgeConfig.ForTests(new BridgeOptions { DbPath = fixture.Path, ReloadCheckSeconds = 1, TreeReloadSeconds = 3600 });
        using var host = new BankHost(config, log) { Embeddings = provider };
        var bank = await host.GetAsync(CancellationToken.None);
        using (var writer = AgentDb.Open(fixture.Path))
        {
            writer.Exec("UPDATE meta SET value = value || ' ' WHERE key = 'raptor_meta'");
            writer.Exec("INSERT INTO docs(ref, kind, title, body, hash, created, updated) VALUES ('ep:late', 'episode', 'late', 'late episode', 'late-hash', 1, 1)");
            long id = writer.ScalarLong("SELECT id FROM docs WHERE ref = 'ep:late'");
            writer.Exec("INSERT INTO vectors(doc_id, model, dims, hash, vec) VALUES ($id, $m, $d, 'late-hash', $v)",
                ("$id", id), ("$m", provider.Model), ("$d", provider.Dimensions), ("$v", VectorMath.ToBlob(provider.Embed("late episode"))));
        }
        await Task.Delay(1100);
        await host.GetAsync(CancellationToken.None);
        await Until(() => log.Refreshes == 1, "the vectors were refreshed");
        await Task.Delay(300);
        Assert.Same(bank, await host.GetAsync(CancellationToken.None));
        Assert.Equal(0, log.Reloads);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_762_800_000);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_cloud_layout_refused_for_want_of_memory_is_not_retried_at_once_but_on_a_later_request()
    {
        var fixture = await FixtureDb.SharedAsync();
        var config = BridgeConfig.ForTests(new BridgeOptions { DbPath = fixture.Path, CloudThreads = 1 });
        var time = new ManualTime();
        long headroom = 0;
        var cloud = new MemoryCloudService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<MemoryCloudService>.Instance, time)
        {
            Headroom = () => Volatile.Read(ref headroom),
        };
        using var bank = await MemoryBank.OpenAsync(fixture.Path, new MineClaw.Agent.AgentOptions(), new HashEmbeddings());
        Assert.Equal("computing", cloud.Get(bank, CloudArrangements.Pca).Status);
        await cloud.Idle();
        var refused = cloud.Get(bank, CloudArrangements.Pca);
        Assert.Equal("error", refused.Status);
        Assert.Contains("short of memory", refused.Note);
        Assert.Equal("error", refused.Arrangements!.Single(a => a.Id == CloudArrangements.Pca).Status);
        // Memory came back, but the refusal holds until its retry time: no layout attempt on every poll.
        Volatile.Write(ref headroom, long.MaxValue / 2);
        Assert.Equal("error", cloud.Get(bank, CloudArrangements.Pca).Status);
        time.Now += MemoryCloudService.ShortOfMemoryRetry;
        Assert.Equal("computing", cloud.Get(bank, CloudArrangements.Pca).Status);
        await cloud.Idle();
        Assert.Equal("ready", cloud.Get(bank, CloudArrangements.Pca).Status);
    }
}
