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
    /// <summary>An <see cref="Overlay"/> reads through to the plan world it was made from (which must not change meanwhile).</summary>
    private PlanWorld? _base;

    public IBlockAccess Inner => inner;

    public int GetBlock(int x, int y, int z) => _set.TryGetValue(new BlockPos(x, y, z), out var v) ? v.Id : _base != null ? _base.GetBlock(x, y, z) : inner.GetBlock(x, y, z);
    public int GetMeta(int x, int y, int z) => _set.TryGetValue(new BlockPos(x, y, z), out var v) ? v.Meta : _base != null ? _base.GetMeta(x, y, z) : inner.GetMeta(x, y, z);
    public bool IsLoaded(int x, int z) => inner.IsLoaded(x, z);
    public BlockInfo Info(int id) => inner.Info(id);
    public MineClaw.World.Versioning.IPhysicsRules Physics => inner.Physics;
    public int WorldHeight => inner.WorldHeight;

    public int Get(BlockPos p) => GetBlock(p.X, p.Y, p.Z);
    public void Set(BlockPos p, int id, int meta = 0) => _set[p] = (id, meta);

    /// <summary>A copy to try something on (a candidate placement's wall-in check) without touching this one.</summary>
    public PlanWorld Fork()
    {
        var f = new PlanWorld(inner) { _base = _base };
        foreach (var kv in _set) f._set[kv.Key] = kv.Value;
        return f;
    }

    /// <summary>
    /// A cheap fork for a question asked at once (a candidate placement's wall-in check): changes go to the overlay only and every
    /// other cell reads through to this world, so nothing is copied (a <see cref="Fork"/> copies every step so far: quadratic over
    /// a 43,680-block build). Only while this world does not change.
    /// </summary>
    public PlanWorld Overlay() => new(inner) { _base = this };
}
