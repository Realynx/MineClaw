using System.Reflection;
using Xunit.Abstractions;

namespace MineClaw.World.Tests;

public sealed class PathTransitAllocationTests(ITestOutputHelper output)
{
    private delegate bool Probe(int x, int z, int tx, int tz, int y, double floor, out List<DoorToggle>? toggles);

    private static Probe Create(WorldState world, bool canOpen)
    {
        var pathfinder = Activator.CreateInstance(typeof(Pathfinder), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [world, PathOptions.Default with { AllowOpenDoors = canOpen }], culture: null)!;
        return typeof(Pathfinder).GetMethod("TransitOk", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Probe>(pathfinder);
    }

    [Fact]
    public void Ordinary_transit_checks_do_not_allocate_door_probe_closures()
    {
        var world = TestWorld.Create();
        var probe = Create(world, canOpen: true);
        _ = probe(-1, 0, 0, 0, 64, 64, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool clear = true;
        for (int i = 0; i < 10_000; i++) clear &= probe(-1, 0, 0, 0, 64, 64, out var toggles) && toggles is null;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"10,000 ordinary transit checks: {allocated:N0} bytes");
        Assert.True(clear);
        Assert.True(allocated < 1024, $"Ordinary transit allocated {allocated:N0} bytes; only actual door probes should capture a sweep.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Door_probes_restore_the_temporary_open_state_between_checks(bool canOpen)
    {
        var world = TestWorld.Create();
        world.SetBlock(0, 64, 0, 64, 0);
        world.SetBlock(0, 65, 0, 64, 8);
        var probe = Create(world, canOpen);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(canOpen, probe(-1, 0, 0, 0, 64, 64, out var toggles));
            if (canOpen) Assert.Equal(new BlockPos(0, 64, 0), Assert.Single(toggles!).Pos);
            else Assert.Null(toggles);
            Assert.Equal(0, world.GetMeta(0, 64, 0));
            Assert.Equal(8, world.GetMeta(0, 65, 0));
        }
    }
}
