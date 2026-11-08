using System.Diagnostics;
using MineClaw.Data;
using MineClaw.World.Versioning;
using Xunit.Abstractions;

namespace MineClaw.World.Tests;

public sealed class PathAllocationTests(ITestOutputHelper output)
{
    private sealed class Terrain(string shape) : IBlockAccess
    {
        public int WorldHeight => 256;
        public IPhysicsRules Physics => Release1122PhysicsRules.Instance;
        public BlockInfo Info(int id) => TestBlocks.Get(id);
        public bool IsLoaded(int x, int z) => x is >= 0 and < 32 && z is >= 0 and < 32;
        public int GetMeta(int x, int y, int z) => 0;
        public int GetBlock(int x, int y, int z)
        {
            if (!IsLoaded(x, z) || y < 0) return 0;
            if (shape == "dig") return y <= 66 && !(x is >= 1 and <= 3 && z is >= 1 and <= 3 && y >= 64) ? 1 : 0;
            if (shape == "gaps" && x % 8 is 4 or 5) return y <= 61 ? 1 : 0;
            return y <= 63 ? 1 : 0;
        }
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("unreachable")]
    [InlineData("dig")]
    [InlineData("gaps")]
    public void Representative_search_allocations(string shape)
    {
        var world = new Terrain(shape);
        var options = new PathOptions { AllowBreak = shape == "dig", AllowOpenDoors = true,
            AllowPartial = false, MaxNodes = shape == "gaps" ? 256 : 2048, Timeout = TimeSpan.FromSeconds(30) };
        var start = new BlockPos(2, 64, 2);
        var goal = new GoalBlock(shape == "dig" ? 12 : 29, shape == "unreachable" ? 80 : 64, 29);
        _ = Pathfinder.FindPath(world, start, goal, options); // JIT and thread-local collision buffers do not belong to the sample.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        var result = Pathfinder.FindPath(world, start, goal, options);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{shape}: {allocated:N0} bytes, {timer.Elapsed.TotalMilliseconds:0.0} ms, {result.NodesExplored} nodes, {result.Status}, {result.Steps.Count} steps");
        Assert.NotEqual(PathStatus.InvalidStart, result.Status);
        Assert.True(result.NodesExplored > 0);
        if (shape == "gaps")
        {
            Assert.Equal(256, result.NodesExplored); // Compare a fixed amount of search work, not a time-budget sample.
            Assert.True(allocated < 40_000_000, $"Gap search allocated {allocated:N0} bytes; pooled snapshots and collision scratch should stay below 40 MB.");
        }
    }

    [Fact]
    public void Repeated_gap_simulation_allocations()
    {
        var world = ParkourPhysicsTests.GapWorld(2, 0);
        var from = new BlockPos(-3, 64, 0);
        var to = new BlockPos(3, 64, 0);
        _ = Parkour.Plan(world, from, 64, to, 64, true, false, 4.3, 3, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        const int count = 20;
        for (int i = 0; i < count; i++) Assert.NotNull(Parkour.Plan(world, from, 64, to, 64, true, false, 4.3, 3, out _));
        long perPlan = (GC.GetAllocatedBytesForCurrentThread() - before) / count;
        output.WriteLine($"gap plan: {perPlan:N0} bytes/plan");
        Assert.True(perPlan < 20_000, $"A robust gap plan allocated {perPlan:N0} bytes; temporary world and contact buffers should be reused.");
    }
}
