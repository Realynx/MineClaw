using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// 2026-10-27 night log, B10: maid_chan_67 learned "protected_region center 0,66,-3 r16 (server undid dig at 13,70,2, 13
/// from spawn)" from ONE undone dig (a leaf of a crown main was chopping too), then refused to replant at 13,65,3. A region
/// now needs two independent undone edits, leaves and contested blocks never count, and a retraction elsewhere clears it.
/// </summary>
public class NightLogProtectionTests
{
    private static void Floor(FakeServer s)
    {
        for (int x = 2; x <= 14; x++)
        for (int z = 2; z <= 14; z++)
            s.Blocks[(x, 63, z)] = Blocks.Stone;
        s.SpawnAt = (4.5, 64, 8.5);
    }

    [Fact]
    public async Task One_undone_dig_near_spawn_does_not_block_a_later_edit()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            srv.BreakOnFinish = false;
            srv.Blocks[(10, 64, 10)] = Blocks.Cobblestone;
            srv.Blocks[(11, 64, 10)] = Blocks.Dirt;
        });
        await using var _ = c;
        using var __ = s;
        Assert.False((await c.DigBlockAsync(10, 64, 10)).Ok);
        Assert.Null(c.ProtectedRegion);
        Assert.False(c.EditBlocked(new BlockPos(11, 64, 10)));
        s.BreakOnFinish = true;
        Assert.True((await c.DigBlockAsync(11, 64, 10)).Ok); // tried, not refused up front as "spawn protection"
    }

    [Fact]
    public async Task Undone_leaf_digs_never_count()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            srv.BreakOnFinish = false;
            srv.Blocks[(10, 64, 10)] = Blocks.Leaves;
        });
        await using var _ = c;
        using var __ = s;
        for (int i = 0; i < 3; i++) Assert.False((await c.DigBlockAsync(10, 64, 10)).Ok);
        Assert.Null(c.ProtectedRegion);
    }

    [Fact]
    public async Task Undone_digs_next_to_another_hive_bot_are_a_race_not_protection()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            srv.BreakOnFinish = false;
            srv.Blocks[(10, 64, 10)] = Blocks.Cobblestone;
        });
        await using var _ = c;
        using var __ = s;
        c.EditContested = p => p.X == 10;
        Assert.False((await c.DigBlockAsync(10, 64, 10)).Ok);
        Assert.False((await c.DigBlockAsync(10, 64, 10)).Ok);
        Assert.Null(c.ProtectedRegion);
        c.EditContested = null;
        Assert.False((await c.DigBlockAsync(10, 64, 10)).Ok);
        Assert.False((await c.DigBlockAsync(10, 64, 10)).Ok);
        Assert.NotNull(c.ProtectedRegion);   // two independent, uncontested undone digs: learned
    }

    [Fact]
    public async Task A_region_retracted_elsewhere_is_forgotten_and_a_shared_one_can_be_adopted()
    {
        var (c, s) = await Harness.StartAsync(Floor);
        await using var _ = c;
        using var __ = s;
        var shared = new LearnedProtectedRegion(new BlockPos(4, 64, 8), 16, 0, 2, "learned by main", DateTime.UtcNow);
        Assert.True(c.AdoptProtectedRegion(shared));
        Assert.True(c.EditBlocked(new BlockPos(10, 64, 10)));
        Assert.False(c.AdoptProtectedRegion(shared)); // already holds one
        Assert.True(c.ForgetProtectedRegion("retracted by maid_chan's edit"));
        Assert.Null(c.ProtectedRegion);
        Assert.False(c.EditBlocked(new BlockPos(10, 64, 10)));
        Assert.False(c.ForgetProtectedRegion("again"));
    }
}
