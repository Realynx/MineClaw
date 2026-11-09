using MineClaw.World;

namespace MineClaw.App.Supervisor;

public static class ChestStateMerge
{
    /// <summary>Complete current state, independent of the short animation event ring. Rejoining viewers
    /// see long-open lids; a close seen by any body supersedes an older open seen by another.</summary>
    public static IReadOnlyList<WorldChestState> Merge(IEnumerable<WorldState> worlds)
    {
        var states = new Dictionary<(int Dim, BlockPos Pos), ChestState>();
        foreach (var world in worlds)
        {
            int dim = world.Dimension;
            foreach (var state in world.ChestStates.Snapshot())
            {
                var p = state.Position;
                if (!world.IsLoaded(p.X, p.Z) || world.GetBlock(p) is not (54 or 146 or 130)) continue;
                var key = (dim, p);
                if (!states.TryGetValue(key, out var old) || state.At > old.At
                    || state.At == old.At && state.OpenCount < old.OpenCount) states[key] = state;
            }
        }
        return states.Where(kv => kv.Value.OpenCount > 0).OrderBy(kv => kv.Key.Dim).ThenBy(kv => kv.Key.Pos.X)
            .ThenBy(kv => kv.Key.Pos.Y).ThenBy(kv => kv.Key.Pos.Z).Select(kv => new WorldChestState(kv.Key.Dim,
            kv.Key.Pos.X, kv.Key.Pos.Y, kv.Key.Pos.Z, kv.Value.OpenCount)).ToArray();
    }
}
