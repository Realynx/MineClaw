using System.Diagnostics;
using System.Reflection;
using MineClaw.Hive.Story;
using Xunit.Abstractions;

namespace MineClaw.Hive.Tests;

public sealed class StoryScaffoldAllocationTests(ITestOutputHelper output)
{
    // Bind once so the measured search is production code, without reflection allocation per call.
    private static readonly Func<StoryColony, BuildState, List<GhostState>, P3, (GhostState Seed, P3 Spot)?> Search =
        typeof(StoryColony).GetMethod("FloatingSeed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Func<StoryColony, BuildState, List<GhostState>, P3, (GhostState Seed, P3 Spot)?>>();

    private static readonly P3 Seed = new(0, 65, 0);
    private static readonly P3 Under = new(0, 64, 0);
    private static readonly P3 Side = new(1, 65, 0);

    private static (StoryColony C, FakeStoryWorld W, BuildState B) Rig()
    {
        var (c, _, w) = StoryRig.Make();
        var b = new BuildState { Id = "floating", Ghosts = [new GhostState { At = Seed, Id = 5 }] };
        c.Builds.Add(b);
        w.Set(2, 65, 0, 1); // the next candidate after the preferred under-cell also has an anchor
        return (c, w, b);
    }

    [Theory]
    [InlineData("done", "done", false, true)]
    [InlineData("paused", "done", false, true)]
    [InlineData("queued", "todo", true, true)]
    [InlineData("queued", "todo", false, false)]
    public void Every_build_cell_remains_reserved_even_when_it_is_not_pending_placement(string status, string state, bool remove, bool job)
    {
        var (c, _, b) = Rig();
        c.Builds.Add(new BuildState
        {
            Id = "reserved", Status = status,
            Ghosts = [new GhostState { At = Under, State = state, Remove = remove, Job = job }],
        });
        Assert.Equal(Side, Search(c, b, b.Ghosts, Seed)?.Spot);
    }

    [Fact]
    public void Ownership_edits_are_seen_on_the_very_next_search()
    {
        var (c, _, b) = Rig();
        Assert.Equal(Under, Search(c, b, b.Ghosts, Seed)?.Spot);
        var owner = new BuildState { Id = "owner", Status = "done", Ghosts = [new GhostState { At = Under, State = "done" }] };
        c.Builds.Add(owner);
        Assert.Equal(Side, Search(c, b, b.Ghosts, Seed)?.Spot);
        owner.Ghosts[0].At = Side;
        Assert.Equal(Under, Search(c, b, b.Ghosts, Seed)?.Spot);
        owner.Ghosts[0].At = Under;
        c.Builds.Remove(owner);
        Assert.Equal(Under, Search(c, b, b.Ghosts, Seed)?.Spot);
    }

    public static IEnumerable<object[]> Lids => StoryColony.LidBlocks.Select(id => new object[] { id });

    [Theory, MemberData(nameof(Lids))]
    public void Scaffolds_still_leave_container_lids_and_workstations_clear(int id)
    {
        var (c, w, b) = Rig();
        w.Set(Under.X, Under.Y - 1, Under.Z, id);
        Assert.Equal(Side, Search(c, b, b.Ghosts, Seed)?.Spot);
    }

    [Theory]
    [InlineData("unloaded")]
    [InlineData("occupied")]
    [InlineData("unsupported")]
    public void An_invalid_under_cell_is_skipped_without_changing_candidate_order(string why)
    {
        var (c, w, b) = Rig();
        if (why == "unloaded") w.Unloaded.Add((Under.X, Under.Z));
        if (why == "occupied") w.Set(Under.X, Under.Y, Under.Z, 1);
        if (why == "unsupported") w.Set(Under.X, Under.Y - 1, Under.Z, 0);
        Assert.Equal(Side, Search(c, b, b.Ghosts, Seed)?.Spot);
    }

    [Fact]
    public void Floating_platform_checks_build_occupancy_once_per_search()
    {
        var (c, _, w) = StoryRig.Make();
        var b = new BuildState { Id = "platform" };
        for (int z = 0; z < 8; z++)
            for (int x = 0; x < 8; x++)
                b.Ghosts.Add(new GhostState { At = new P3(x * 4, 100, z * 4), Id = 5 });
        c.Builds.Add(b);
        var finished = new BuildState { Id = "large-finished-build", Status = "done" };
        for (int i = 0; i < 4096; i++)
            finished.Ghosts.Add(new GhostState { At = new P3(1000 + i % 64, 70, i / 64), State = "done" });
        c.Builds.Add(finished);
        var expected = b.Ghosts[^1];
        w.Set(expected.At.X, expected.At.Y - 2, expected.At.Z, 1);

        for (int i = 0; i < 2; i++) Assert.Same(expected, Search(c, b, b.Ghosts, b.Ghosts[0].At)?.Seed);
        const int rounds = 8;
        var watch = new Stopwatch();
        long start = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        (GhostState Seed, P3 Spot)? result = null;
        for (int i = 0; i < rounds; i++) result = Search(c, b, b.Ghosts, b.Ghosts[0].At);
        watch.Stop();
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - start) / rounds;
        output.WriteLine($"64 candidates / 4,160 build cells: {bytes:N0} bytes/search; {watch.Elapsed.TotalMilliseconds / rounds:N3} ms/search");
        Assert.Same(expected, result?.Seed);
        Assert.Equal(expected.At.Add(0, -1, 0), result?.Spot);
        // The old implementation rebuilt a full HashSet for every rejected candidate (over 24 MB per search).
        Assert.True(bytes < 512_000, $"A scaffold search allocated {bytes:N0} bytes; build occupancy must be indexed once, not per candidate.");
    }
}
