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

/// <summary>
/// Where observations go (the Agent's <see cref="ObservationStore"/>; tests wrap it to count transactions). The indexer
/// stamps every <see cref="Observation.Dimension"/> and calls the dimension overloads; the old two-argument forms remain
/// for dimension-unaware sinks (they default to the overworld-only behaviour of v4).
/// </summary>
public interface IObservationSink
{
    int UpsertMany(IReadOnlyList<Observation> batch);
    bool Remove(string kind, string key);
    /// <summary>Live rows (any source) whose box is within <paramref name="radius"/> of a point.</summary>
    IReadOnlyList<ObservationRow> Near(string? kind, double x, double y, double z, double radius, int limit);

    bool Remove(string kind, string key, int dimension) => Remove(kind, key);
    IReadOnlyList<ObservationRow> Near(string? kind, double x, double y, double z, double radius, int limit, int dimension) =>
        Near(kind, x, y, z, radius, limit);
    /// <summary>The bot went through a portal: entry and exit as one linked pair.</summary>
    void LinkPortals(PortalEnd from, PortalEnd to) { }
}

public sealed class ObservationStoreSink(ObservationStore store) : IObservationSink
{
    public ObservationStore Store { get; } = store;
    public int UpsertMany(IReadOnlyList<Observation> batch) => batch.Count == 0 ? 0 : Store.UpsertMany(batch);
    public bool Remove(string kind, string key) => Store.Remove(kind, key);
    public bool Remove(string kind, string key, int dimension) => Store.Remove(kind, key, dimension: dimension);
    public IReadOnlyList<ObservationRow> Near(string? kind, double x, double y, double z, double radius, int limit) =>
        Store.Nearest(kind, x, y, z, limit, radius).Select(t => t.Row).ToList();
    public IReadOnlyList<ObservationRow> Near(string? kind, double x, double y, double z, double radius, int limit, int dimension) =>
        Store.Nearest(kind, x, y, z, limit, radius, dimension).Select(t => t.Row).ToList();
    public void LinkPortals(PortalEnd from, PortalEnd to) => Store.Portals.Link(from, to);
}

/// <summary>
/// Which dimension the bot is in (0 overworld, -1 nether), for the memory side. Consumers (indexer, activity tracker) also
/// poll <see cref="Dimension"/> at the start of every pass, so a source without the event is still correct.
/// <para>TODO(client): the Client has no dimension event yet. Wanted on <c>MinecraftClient</c> (raised on the event
/// dispatcher after the 0x09 world reset, only when the dimension actually changed):
/// <c>public event Action&lt;int, int&gt;? DimensionChanged; // (from, to)</c> — then implement
/// <see cref="ClientDimensionSource"/> by forwarding it.</para>
/// </summary>
public interface IDimensionSource
{
    int Dimension { get; }
    /// <summary>(from, to); may be raised on any thread.</summary>
    event Action<int, int>? DimensionChanged;
}

/// <summary>Polled source over <see cref="WorldState.Dimension"/> (set by the client on login / 0x09); never raises the event.</summary>
public sealed class WorldDimensionSource(WorldState world) : IDimensionSource
{
    public int Dimension => world.Dimension;
#pragma warning disable CS0067 // stub until the client raises DimensionChanged
    public event Action<int, int>? DimensionChanged;
#pragma warning restore CS0067
}

/// <summary>Real client: today the same polled view (see the TODO on <see cref="IDimensionSource"/>).</summary>
public sealed class ClientDimensionSource(MinecraftClient client) : IDimensionSource
{
    public int Dimension => client.World.Dimension;
#pragma warning disable CS0067
    public event Action<int, int>? DimensionChanged; // TODO(client): add { client.DimensionChanged += value; remove ... }
#pragma warning restore CS0067
}
