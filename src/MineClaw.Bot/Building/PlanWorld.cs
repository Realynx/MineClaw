using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>
/// The planner's simulated world: the real (remembered) world with the plan's steps so far applied on top, so stand
/// selection, the pathfinder flood and the swim simulation all see what will stand when each step runs.
/// </summary>
public sealed class PlanWorld(IBlockAccess inner) : IBlockAccess
{
    private readonly Dictionary<BlockPos, (int Id, int Meta)> _set = new();

    public IBlockAccess Inner => inner;

    public int GetBlock(int x, int y, int z) => _set.TryGetValue(new BlockPos(x, y, z), out var v) ? v.Id : inner.GetBlock(x, y, z);
    public int GetMeta(int x, int y, int z) => _set.TryGetValue(new BlockPos(x, y, z), out var v) ? v.Meta : inner.GetMeta(x, y, z);
    public bool IsLoaded(int x, int z) => inner.IsLoaded(x, z);
    public BlockInfo Info(int id) => inner.Info(id);
    public MineClaw.World.Versioning.IPhysicsRules Physics => inner.Physics;
    public int WorldHeight => inner.WorldHeight;

    public int Get(BlockPos p) => GetBlock(p.X, p.Y, p.Z);
    public void Set(BlockPos p, int id, int meta = 0) => _set[p] = (id, meta);
    public void Revert(BlockPos p) => _set.Remove(p);

    /// <summary>A copy to try something on (a candidate placement's wall-in check) without touching this one.</summary>
    public PlanWorld Fork()
    {
        var f = new PlanWorld(inner);
        foreach (var kv in _set) f._set[kv.Key] = kv.Value;
        return f;
    }
}
