using MineClaw.Data;

namespace MineClaw.World;

/// <summary>A few hypothetical block changes layered over a world (what-if checks: "if I place/dig this...").</summary>
public sealed class BlockOverlay(IBlockAccess inner) : IBlockAccess
{
    private readonly Dictionary<BlockPos, (int Id, int Meta)> _mods = new();

    public IBlockAccess Inner { get; } = inner;

    public BlockOverlay Set(BlockPos p, int id, int meta = 0)
    {
        _mods[p] = (id, meta);
        return this;
    }

    public int GetBlock(int x, int y, int z) =>
        _mods.Count > 0 && _mods.TryGetValue(new BlockPos(x, y, z), out var m) ? m.Id : Inner.GetBlock(x, y, z);

    public int GetMeta(int x, int y, int z) =>
        _mods.Count > 0 && _mods.TryGetValue(new BlockPos(x, y, z), out var m) ? m.Meta : Inner.GetMeta(x, y, z);

    public bool IsLoaded(int x, int z) => Inner.IsLoaded(x, z);

    public BlockInfo Info(int id) => Inner.Info(id);
}
