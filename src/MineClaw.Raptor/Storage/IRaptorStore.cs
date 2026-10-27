namespace MineClaw.Raptor.Storage;

/// <summary>
/// Persistence for one RAPTOR tree. Implementations must apply a change set atomically (all or nothing) and may
/// be called from any thread, one call at a time (RaptorIndex serializes writers).
/// </summary>
public interface IRaptorStore
{
    /// <summary>The stored tree, or null when nothing has been saved yet.</summary>
    Task<RaptorSnapshot?> LoadAsync(CancellationToken ct);

    Task ApplyAsync(RaptorChangeSet changes, CancellationToken ct);
}

/// <summary>Volatile store (tests, persist=false).</summary>
public sealed class InMemoryRaptorStore : IRaptorStore
{
    private readonly object _lock = new();
    private readonly SortedDictionary<int, RaptorNodeRecord> _nodes = new();
    private RaptorTreeMeta? _meta;

    public int ApplyCount { get; private set; }

    public Task<RaptorSnapshot?> LoadAsync(CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult(_meta == null ? null : new RaptorSnapshot(_meta, _nodes.Values.ToList()));
    }

    public Task ApplyAsync(RaptorChangeSet changes, CancellationToken ct)
    {
        lock (_lock)
        {
            Apply(_nodes, changes);
            _meta = changes.Meta;
            ApplyCount++;
        }
        return Task.CompletedTask;
    }

    internal static void Apply(SortedDictionary<int, RaptorNodeRecord> nodes, RaptorChangeSet changes)
    {
        if (changes.ReplaceAll) nodes.Clear();
        foreach (var d in changes.Deletes) nodes.Remove(d);
        foreach (var u in changes.Upserts) nodes[u.Index] = u;
    }
}
