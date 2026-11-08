using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MineClaw.Agent.Rag;
using MineClaw.ViewerBridge.Data;
using MineClaw.ViewerBridge.Graph;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Reflection;

namespace MineClaw.ViewerBridge.Tests;

public sealed class MemoryBankReloadTests
{
    private static Dictionary<long, float[]> StoredVectors(RagIndex rag)
    {
        var entries = (IDictionary)typeof(RagIndex).GetField("_vecs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rag)!;
        var result = new Dictionary<long, float[]>();
        var iterator = entries.GetEnumerator();
        while (iterator.MoveNext()) result.Add((long)iterator.Key, (float[])iterator.Value!.GetType().GetProperty("Vec")!.GetValue(iterator.Value)!);
        return result;
    }

    private sealed class NamedEmbeddings(string model, int dimensions) : IEmbeddingProvider
    {
        public string Model => model;
        public int Dimensions => dimensions;
        public bool IsAvailable => true;
        public Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct) =>
            new HashEmbeddings(dimensions).EmbedAsync(inputs, ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Replacement_does_not_reuse_vectors_from_a_different_model_or_dimension(bool changeModel)
    {
        using var fixture = await FixtureDb.CreateAsync();
        var config = BridgeConfig.ForTests(new BridgeOptions { DbPath = fixture.Path });
        using var original = await MemoryBank.OpenAsync(fixture.Path, config.Agent, new HashEmbeddings());
        var before = StoredVectors(original.Rag);
        var provider = new NamedEmbeddings(changeModel ? "other-model" : "fake-hash", changeModel ? 48 : 24);
        var expected = Enumerable.Repeat(0.125f, provider.Dimensions).ToArray();
        using (var writer = AgentDb.Open(fixture.Path))
            writer.Exec("UPDATE vectors SET model = $m, dims = $d, vec = $v", ("$m", provider.Model), ("$d", provider.Dimensions), ("$v", VectorMath.ToBlob(expected)));
        using var replacement = await MemoryBank.OpenAsync(fixture.Path, config.Agent, provider, previous: original);
        var after = StoredVectors(replacement.Rag);
        Assert.Equal(before.Count, after.Count);
        foreach (var (id, vector) in after) { Assert.NotSame(before[id], vector); Assert.Equal(expected, vector); }
    }

    [Fact]
    public async Task Replacement_reuses_only_unchanged_vectors_and_remains_independent_after_old_bank_disposal()
    {
        using var fixture = await FixtureDb.CreateAsync();
        var config = BridgeConfig.ForTests(new BridgeOptions { DbPath = fixture.Path });
        var provider = new HashEmbeddings();
        var original = await MemoryBank.OpenAsync(fixture.Path, config.Agent, provider);
        var before = StoredVectors(original.Rag);
        var changedId = before.Keys.First();
        var changed = provider.Embed("new flowers roses garden");
        using (var writer = AgentDb.Open(fixture.Path))
        {
            writer.Exec("UPDATE docs SET hash = 'changed-vector' WHERE id = $id", ("$id", changedId));
            writer.Exec("UPDATE vectors SET hash = 'changed-vector', vec = $v WHERE doc_id = $id",
                ("$id", changedId), ("$v", VectorMath.ToBlob(changed)));
        }
        using var replacement = await MemoryBank.OpenAsync(fixture.Path, config.Agent, provider, previous: original);
        using var independent = await MemoryBank.OpenAsync(fixture.Path, config.Agent, provider);
        var after = StoredVectors(replacement.Rag);
        Assert.Equal(before.Count, after.Count);
        foreach (var (id, vector) in after)
            if (id == changedId) { Assert.NotSame(before[id], vector); Assert.Equal(changed, vector); }
            else Assert.Same(before[id], vector);
        original.Dispose();
        var query = new RagQuery { Text = "cobblestone farm", Limit = 10 };
        var qv = provider.Embed(query.Text);
        Assert.Equal(independent.Rag.Search(query, qv).Select(h => (h.Ref, h.Cosine)), replacement.Rag.Search(query, qv).Select(h => (h.Ref, h.Cosine)));
        Assert.True(replacement.Db.ScalarLong("SELECT count(*) FROM docs") > 0);
        Assert.Empty(replacement.ReadTree(t => t.Validate(), ["missing tree"]));
    }

    [Fact]
    public async Task Completed_cloud_layout_does_not_keep_a_retired_bank_alive()
    {
        using var fixture = await FixtureDb.CreateAsync();
        var config = BridgeConfig.ForTests(new BridgeOptions { DbPath = fixture.Path, CloudThreads = 1 });
        var service = new MemoryCloudService(config, NullLogger<MemoryCloudService>.Instance, TimeProvider.System);
        var oldBank = CacheCloud(service, fixture.Path, config);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(oldBank.IsAlive, "An idle cloud service must retain its layout data, not a whole retired bank.");
        GC.KeepAlive(service);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CacheCloud(MemoryCloudService service, string path, BridgeConfig config)
    {
        using var bank = MemoryBank.OpenAsync(path, config.Agent, new HashEmbeddings()).GetAwaiter().GetResult();
        service.Get(bank, CloudArrangements.Pca);
        service.Idle().GetAwaiter().GetResult();
        Assert.Equal("ready", service.Get(bank, CloudArrangements.Pca).Status);
        return new WeakReference(bank);
    }

    private sealed class ReloadLog : ILogger<BankHost>
    {
        private int _reloads;
        public int Reloads => Volatile.Read(ref _reloads);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (format(state, error).StartsWith("reloaded the agent db", StringComparison.Ordinal)) Interlocked.Increment(ref _reloads);
        }
    }

    /// <summary>Pause replacement loading before vectors are allocated; the current bank's read routes remain usable.</summary>
    private sealed class PausedEmbeddings : IEmbeddingProvider, IDisposable
    {
        private readonly HashEmbeddings _inner = new();
        private readonly ManualResetEventSlim _continue = new(true);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Model => _inner.Model;
        public int Dimensions => _inner.Dimensions;
        public bool IsAvailable
        {
            get
            {
                if (!_continue.IsSet) { Entered.TrySetResult(); _continue.Wait(TimeSpan.FromSeconds(10)); }
                return true;
            }
        }
        public void Pause() => _continue.Reset();
        public void Resume() => _continue.Set();
        public Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct) => _inner.EmbedAsync(inputs, ct);
        public void Dispose() { _continue.Set(); _continue.Dispose(); }
    }

    [Fact]
    public async Task Concurrent_requests_share_one_reload_and_old_readers_remain_usable()
    {
        using var fixture = await FixtureDb.CreateAsync();
        using var embeddings = new PausedEmbeddings();
        var log = new ReloadLog();
        var config = BridgeConfig.ForTests(new BridgeOptions { DbPath = fixture.Path, ReloadCheckSeconds = 1 });
        using var host = new BankHost(config, log) { Embeddings = embeddings };
        using var original = await host.GetAsync(CancellationToken.None);
        var originalNodes = original.Raptor!.Index.NodeCount;
        using (var writer = AgentDb.Open(fixture.Path))
            writer.Exec("UPDATE docs SET updated = (SELECT max(updated) + 10000 FROM docs) WHERE id = (SELECT min(id) FROM docs)");
        await Task.Delay(1100);
        embeddings.Pause();
        try
        {
            Assert.Same(original, await host.GetAsync(CancellationToken.None));
            await embeddings.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var readers = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => host.GetAsync(CancellationToken.None))));
            Assert.All(readers, bank => Assert.Same(original, bank));
            Assert.True(original.Db.ScalarLong("SELECT count(*) FROM docs") > 0);
            Assert.Equal(originalNodes, original.Raptor.Index.NodeCount);
        }
        finally { embeddings.Resume(); }
        var stop = DateTime.UtcNow.AddSeconds(5);
        MemoryBank latest;
        do
        {
            latest = await host.GetAsync(CancellationToken.None);
            if (!ReferenceEquals(latest, original)) break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < stop);
        Assert.NotSame(original, latest);
        Assert.Equal(1, log.Reloads);
        Assert.Equal(originalNodes, latest.Raptor!.Index.NodeCount);
        Assert.Equal(original.Rag.VectorCount, latest.Rag.VectorCount);
        // A caller holding the old snapshot across the atomic swap still has a live database and tree.
        Assert.True(original.Db.ScalarLong("SELECT count(*) FROM docs") > 0);
        Assert.Empty(original.ReadTree(t => t.Validate(), ["missing tree"]));
        Assert.Empty(latest.ReadTree(t => t.Validate(), ["missing tree"]));
    }
}
