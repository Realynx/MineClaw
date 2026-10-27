using MineClaw.Agent.Rag;
using MineClaw.Client;
using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

/// <summary>
/// What the indexer needs from the client beyond <see cref="WorldState"/>. The real client exposes sign texts as a
/// polled dictionary and keeps the opened container's position private, so this is a stub-able seam: when the Client
/// grows <c>SignUpdated</c> / <c>ContainerOpened(BlockPos, Window)</c> events, implement them here and nothing else changes.
/// </summary>
public interface IWorldIndexFeed
{
    /// <summary>Sign texts seen so far (position → 4 lines). Polled once per pass.</summary>
    IReadOnlyDictionary<BlockPos, string[]> Signs { get; }

    /// <summary>Block position of the container the bot opened last, when the client exposes it (else null and the
    /// indexer resolves it by raycast from the bot's look direction).</summary>
    BlockPos? LastOpenedContainer { get; }
}

/// <summary>Feed over the real <see cref="MinecraftClient"/>.</summary>
public sealed class ClientIndexFeed(MinecraftClient client) : IWorldIndexFeed
{
    public IReadOnlyDictionary<BlockPos, string[]> Signs => client.Signs;
    // TODO(client): MinecraftClient._lastContainerPos is private; expose it (or a ContainerOpened event) to skip the raycast.
    public BlockPos? LastOpenedContainer => null;
}

/// <summary>Settable feed for tests and hosts without a real client.</summary>
public sealed class ManualIndexFeed : IWorldIndexFeed
{
    public System.Collections.Concurrent.ConcurrentDictionary<BlockPos, string[]> SignTexts { get; } = new();
    public IReadOnlyDictionary<BlockPos, string[]> Signs => SignTexts;
    public BlockPos? LastOpenedContainer { get; set; }
}

/// <summary>Where observations go (the Agent's <see cref="ObservationStore"/>; tests wrap it to count transactions).</summary>
public interface IObservationSink
{
    int UpsertMany(IReadOnlyList<Observation> batch);
    bool Remove(string kind, string key);
    /// <summary>Live rows (any source) whose box is within <paramref name="radius"/> of a point.</summary>
    IReadOnlyList<ObservationRow> Near(string? kind, double x, double y, double z, double radius, int limit);
}

public sealed class ObservationStoreSink(ObservationStore store) : IObservationSink
{
    public ObservationStore Store { get; } = store;
    public int UpsertMany(IReadOnlyList<Observation> batch) => batch.Count == 0 ? 0 : Store.UpsertMany(batch);
    public bool Remove(string kind, string key) => Store.Remove(kind, key);
    public IReadOnlyList<ObservationRow> Near(string? kind, double x, double y, double z, double radius, int limit) =>
        Store.Nearest(kind, x, y, z, limit, radius).Select(t => t.Row).ToList();
}
