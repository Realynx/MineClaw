namespace MineClaw.World;

/// <summary>
/// Every foot cell the real search can walk to from a start, with its path cost (ticks): a Dijkstra flood over exactly the
/// moves <see cref="FindPath(IBlockAccess, BlockPos, Goal, PathOptions?, CancellationToken)"/> plans (doors, ladders, swims, drops,
/// the void rules), so "reachable" here means the follower can get there. The build planner picks stand cells from it.
/// </summary>
public sealed partial class Pathfinder
{
    private Dictionary<BlockPos, double>? _collect;

    /// <summary>Reachable foot cells from <paramref name="start"/> → cost in ticks (start = 0). Empty when the start isn't standable.
    /// Scaffolding/breaking are ignored (a flood plans no edits): the options' place/break flags are turned off.</summary>
    public static Dictionary<BlockPos, double> Explore(IBlockAccess world, BlockPos start, PathOptions? options = null, int maxNodes = 8000,
        CancellationToken ct = default)
    {
        var o = (options ?? PathOptions.Default) with
        {
            AllowPlace = false, AllowBreak = false, ScaffoldBudget = 0, AllowPartial = false, MaxNodes = maxNodes,
            Timeout = TimeSpan.FromSeconds(5),
        };
        var pf = new Pathfinder(world, o) { _collect = new Dictionary<BlockPos, double>() };
        pf.Search(start, FloodGoal.Instance, ct);
        return pf._collect;
    }

    private sealed class FloodGoal : Goal
    {
        public static readonly FloodGoal Instance = new();
        public override bool IsReached(int x, int y, int z) => false;
        public override double Distance(int x, int y, int z) => 0;
    }
}
