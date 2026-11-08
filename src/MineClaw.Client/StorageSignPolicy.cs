using System.Collections.Frozen;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>A published, immutable view of one chest's physical labels. The predicate captures copied rules, never a colony.</summary>
public sealed class StorageSignPolicy
{
    internal FrozenSet<BlockPos> Chests { get; }
    internal FrozenDictionary<BlockPos, string[]> Signs { get; }
    private readonly Func<int, int, bool> _accepts;

    public StorageSignPolicy(IEnumerable<BlockPos> chests, IEnumerable<(BlockPos At, IReadOnlyList<string> Lines)> signs,
        Func<int, int, bool> accepts)
    {
        Chests = chests.ToFrozenSet();
        Signs = signs.ToFrozenDictionary(s => s.At, s => s.Lines.ToArray());
        _accepts = accepts;
    }

    internal bool Accepts(int id, int meta) => _accepts(id, meta);
}
