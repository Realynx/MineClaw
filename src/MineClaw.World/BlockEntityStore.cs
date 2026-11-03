using System.Collections.Concurrent;
using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// The block entities this world's connection described (signs on both versions; 1.12.2 also beds, banners, skulls, flower pots,
/// spawners, chests… from chunk data and Update Block Entity), keyed by chunk column then position, for the browser viewer. The bot
/// never reads it (signs keep their own client cache). A column's entries go when it unloads. Thread-safe.
/// </summary>
public sealed class BlockEntityStore
{
    private readonly ConcurrentDictionary<(int Cx, int Cz), ConcurrentDictionary<BlockPos, BlockEntityInfo>> _columns = new();

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
        if (changed) Changed?.Invoke(pos);
    }

    public BlockEntityInfo? Get(BlockPos pos) =>
        _columns.TryGetValue((pos.X >> 4, pos.Z >> 4), out var col) && col.TryGetValue(pos, out var info) ? info : null;

    /// <summary>The column's block entities (a snapshot).</summary>
    public IReadOnlyList<(BlockPos Pos, BlockEntityInfo Info)> InColumn(int cx, int cz) =>
        _columns.TryGetValue((cx, cz), out var col) ? col.Select(kv => (kv.Key, kv.Value)).ToList() : [];

    /// <summary>The column left this world's view: its entries go (no events: the column event says it).</summary>
    public void DropColumn(int cx, int cz) => _columns.TryRemove((cx, cz), out _);

    // Records compare their lists by reference: lines and patterns by content.
    private static bool Same(BlockEntityInfo a, BlockEntityInfo b) =>
        a with { Lines = null, Patterns = null } == b with { Lines = null, Patterns = null }
        && (a.Lines ?? []).SequenceEqual(b.Lines ?? []) && (a.Patterns ?? []).SequenceEqual(b.Patterns ?? []);
}
