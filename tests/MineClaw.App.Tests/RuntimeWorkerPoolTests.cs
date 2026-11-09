using MineClaw.App.Hosting;

namespace MineClaw.App.Tests;

public sealed class RuntimeWorkerPoolTests
{
    [Theory]
    [InlineData(1, 1, 32767, 4)]
    [InlineData(2, 2, 32767, 8)]
    [InlineData(8, 8, 32767, 32)]
    [InlineData(16, 16, 32767, 64)]
    [InlineData(32, 32, 32767, 64)]
    [InlineData(int.MaxValue, 16, 32767, 64)]
    [InlineData(16, 96, 32767, 96)] // preserve a higher runtime/operator minimum
    [InlineData(16, 16, 24, 24)]    // do not defeat an explicitly restricted maximum
    [InlineData(16, 16, 16, 16)]
    public void Worker_headroom_is_cpu_bounded_and_respects_existing_limits(int processors, int minimum, int maximum, int expected)
    {
        var selected = RuntimeWorkerPool.Minimums(processors, minimum, 19, maximum);
        Assert.Equal(expected, selected.Workers);
        Assert.Equal(19, selected.Io);
    }

    [Fact]
    public void Reapplying_the_policy_keeps_the_same_limits()
    {
        var once = RuntimeWorkerPool.Minimums(16, 16, 7, 32767);
        var twice = RuntimeWorkerPool.Minimums(16, once.Workers, once.Io, 32767);
        Assert.Equal(once, twice);
    }
}
