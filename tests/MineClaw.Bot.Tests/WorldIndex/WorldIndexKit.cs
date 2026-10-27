using MineClaw.Agent.Rag;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>Hand-advanced clock.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan d) => _now += d;
    public void Seconds(double s) => _now += TimeSpan.FromSeconds(s);
}

/// <summary>Counts transactions/calls on the way to the real store.</summary>
public sealed class CountingSink(IObservationSink inner) : IObservationSink
{
    public int UpsertCalls, Upserted, RemoveCalls;
    public List<Observation> Last { get; } = new();
    public int UpsertMany(IReadOnlyList<Observation> batch)
    {
        UpsertCalls++;
        Upserted += batch.Count;
        Last.AddRange(batch);
        return inner.UpsertMany(batch);
    }
    public bool Remove(string kind, string key) { RemoveCalls++; return inner.Remove(kind, key); }
    public IReadOnlyList<ObservationRow> Near(string? kind, double x, double y, double z, double radius, int limit) => inner.Near(kind, x, y, z, radius, limit);
}

/// <summary>A synthetic world + a real SQLite observation store in a temp file.</summary>
public sealed class IndexFixture : IDisposable
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "mineclaw-wi-" + Guid.NewGuid().ToString("N")[..8]);
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    public WorldState World { get; } = new();
    public AgentDb Db { get; }
    public RagIndex Rag { get; }
    public ObservationStore Store { get; }
    public CountingSink Sink { get; }
    public ManualIndexFeed Feed { get; } = new();
    public WorldIndexOptions Options { get; } = new() { ClusterMinWriteSeconds = 0, ContainerMinWriteSeconds = 0, MaxColumnsPerPass = 64, ScanBudgetMs = 1000 };
    private WorldIndexer? _indexer;

    public IndexFixture(int chunkRadius = 2, bool loadAir = true)
    {
        Directory.CreateDirectory(Dir);
        Db = AgentDb.Open(Path.Combine(Dir, "test.db"));
        Rag = new RagIndex(Db, time: Clock, backgroundEmbedding: false);
        Store = new ObservationStore(Rag, Clock);
        Sink = new CountingSink(new ObservationStoreSink(Store));
        if (loadAir)
        {
            int size = chunkRadius * 32;
            World.ApplyChunkRegion(-chunkRadius * 16, 0, -chunkRadius * 16, size, 128, size, new byte[size * 128 * size], null, null, null);
        }
        World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
    }

    public WorldIndexer Indexer => _indexer ??= new WorldIndexer(World, Sink, Feed, Options, "maid_chan", Clock).Attach();

    public void Set(int x, int y, int z, int id, int meta = 0) => World.SetBlock(x, y, z, id, meta);

    public void Fill(int x0, int y0, int z0, int x1, int y1, int z1, int id)
    {
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
            World.SetBlock(x, y, z, id, 0);
    }

    /// <summary>Starter skyblock island: 7x7 dirt with grass top at y 60..63 centred on 0,0.</summary>
    public void Island(int cx = 0, int cz = 0, int r = 3, int top = 63)
    {
        Fill(cx - r, top - 3, cz - r, cx + r, top - 1, cz + r, Blocks.Dirt);
        Fill(cx - r, top, cz - r, cx + r, top, cz + r, Blocks.Grass);
    }

    /// <summary>Lava | gap | water channel on top of the island: the gap at (x+1, y, z) makes cobble.</summary>
    public BlockPos Generator(int x, int y, int z)
    {
        Fill(x - 1, y, z - 1, x + 3, y, z + 1, Blocks.Cobblestone); // rim
        Set(x, y, z, Blocks.Lava);
        Set(x + 1, y, z, Blocks.Cobblestone);
        Set(x + 2, y, z, Blocks.Water);
        return new BlockPos(x + 1, y, z);
    }

    public void Pump() => Indexer.PumpAll();

    public ObservationRow? Row(string kind, string key) => Store.Get(kind, key);

    public void Dispose()
    {
        _indexer?.Dispose();
        Rag.Dispose();
        Db.Dispose();
        try { Directory.Delete(Dir, true); } catch { }
    }
}
