using System.Collections.Concurrent;
using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// The block entities this world's connection described (signs on both versions; 1.12.2 also beds, banners, skulls, flower pots,
/// spawners, chests… from chunk data and Update Block Entity), keyed by chunk column then position, for the browser viewer and
/// observed sign rules. A column's entries go when it unloads. Thread-safe.
/// </summary>
public sealed class BlockEntityStore
{
    private readonly ConcurrentDictionary<(int Cx, int Cz), ConcurrentDictionary<BlockPos, BlockEntityInfo>> _columns = new();
    private readonly ConcurrentDictionary<BlockPos, (IReadOnlyList<string> Lines, long Revision)> _signs = new();
    private static long _observationRevision;

    /// <summary>A block entity was set or removed (the position): the viewer refetches its column's block entities.</summary>
    public event Action<BlockPos>? Changed;

    /// <summary>Sets (or with null removes) the block entity at <paramref name="pos"/>; raises <see cref="Changed"/> when it differs.</summary>
    public void Set(BlockPos pos, BlockEntityInfo? info)
    {
        var key = (pos.X >> 4, pos.Z >> 4);
        bool changed;
        if (info is null)
            changed = _columns.TryGetValue(key, out var col) && col.TryRemove(pos, out _);
        else
        {
            var col = _columns.GetOrAdd(key, _ => new ConcurrentDictionary<BlockPos, BlockEntityInfo>());
            changed = !col.TryGetValue(pos, out var had) || !Same(had, info);
            col[pos] = info;
        }
        if (info is { Kind: "sign", Lines: { } lines })
            _signs[pos] = (lines, Interlocked.Increment(ref _observationRevision));
        else _signs.TryRemove(pos, out _);
        if (changed) Changed?.Invoke(pos);
    }

    public BlockEntityInfo? Get(BlockPos pos) =>
        _columns.TryGetValue((pos.X >> 4, pos.Z >> 4), out var col) && col.TryGetValue(pos, out var info) ? info : null;

    /// <summary>The column's block entities (a snapshot).</summary>
    public IReadOnlyList<(BlockPos Pos, BlockEntityInfo Info)> InColumn(int cx, int cz) =>
        _columns.TryGetValue((cx, cz), out var col) ? col.Select(kv => (kv.Key, kv.Value)).ToList() : [];

    /// <summary>The column left this world's view: its entries go (no events: the column event says it).</summary>
    public void DropColumn(int cx, int cz)
    {
        _columns.TryRemove((cx, cz), out _);
        foreach (var pos in _signs.Keys)
            if (pos.X >> 4 == cx && pos.Z >> 4 == cz) _signs.TryRemove(pos, out _);
    }

    /// <summary>Sign text received from this connection. Revisions order observations across body caches in this process;
    /// a caller must still check that the column is loaded and the block remains a sign.</summary>
    public IReadOnlyList<(BlockPos Pos, IReadOnlyList<string> Lines, long Revision)> SignObservations() =>
        _signs.Select(s => (s.Key, s.Value.Lines, s.Value.Revision)).ToArray();

    // Records compare their lists by reference: lines and patterns by content.
    private static bool Same(BlockEntityInfo a, BlockEntityInfo b) =>
        a with { Lines = null, Patterns = null } == b with { Lines = null, Patterns = null }
        && (a.Lines ?? []).SequenceEqual(b.Lines ?? []) && (a.Patterns ?? []).SequenceEqual(b.Patterns ?? []);
}
