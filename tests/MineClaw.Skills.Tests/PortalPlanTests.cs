using MineClaw.World;

namespace MineClaw.Skills.Tests;

public class PortalPlanTests
{
    [Theory]
    [InlineData(0, 0, 1000, 0, true)]       // 125 nether blocks apart: inside the 128 search square
    [InlineData(0, 0, 1024, 0, true)]       // exactly 128
    [InlineData(0, 0, 1032, 0, false)]      // 129: separate portals
    [InlineData(0, 0, 900, 1500, false)]    // far in z only
    [InlineData(-500, -500, 500, 500, true)]
    [InlineData(-3000, 0, 3000, 0, false)]
    public void Two_overworld_portals_share_a_nether_portal_within_128_nether_blocks_of_each_other(int hx, int hz, int fx, int fz, bool shared) =>
        Assert.Equal(shared, PortalLinks.Plan(new BlockPos(hx, 64, hz), new BlockPos(fx, 64, fz)).SharedNether);

    [Fact]
    public void A_shared_pair_leads_home_only_and_a_separate_pair_goes_both_ways()
    {
        var near = PortalLinks.Plan(new BlockPos(0, 64, 0), new BlockPos(600, 64, 0));
        Assert.False(near.TwoWay);
        Assert.Equal(2 * PortalLinks.TransitSeconds, near.PortalSeconds, 0.1);   // in and out: no nether walk when it is the same nether portal
        Assert.Contains("far -> home works and home -> far does not", PortalLinks.Describe(near));
        var far = PortalLinks.Plan(new BlockPos(0, 64, 0), new BlockPos(4000, 64, 0));
        Assert.True(far.TwoWay);
        Assert.Equal(new BlockPos(500, 64, 0), far.NetherFar);          // 4000 / 8
        Assert.Equal(500, far.NetherDistance, 0.5);
        Assert.Contains("two-way", PortalLinks.Describe(far));
    }

    [Fact]
    public void The_portal_only_beats_walking_once_the_trip_is_long_enough()
    {
        Assert.False(PortalLinks.Plan(new BlockPos(0, 64, 0), new BlockPos(60, 64, 0)).PortalFaster, "60 m: walk");
        Assert.True(PortalLinks.Plan(new BlockPos(0, 64, 0), new BlockPos(600, 64, 0)).PortalFaster, "shared nether portal: 24 s of portals vs 3 min of walking");
        var p = PortalLinks.Plan(new BlockPos(0, 64, 0), new BlockPos(3000, 64, 0));
        Assert.True(p.PortalFaster);
        Assert.True(p.WalkSeconds > 10 * 60, "3000 m on foot is over 14 minutes");
        Assert.InRange(p.PortalSeconds, 100, 300);
    }

    [Fact]
    public async Task The_skill_answers_from_where_I_stand_unless_given_a_start()
    {
        var g = LiquidTests.Island();
        var r = await g.Run("plan_portals", new { to = "1500,64,0" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("separate nether portals", r.Text);
        Assert.Contains("two-way", r.Text);
        Assert.Contains("portal wins", r.Text);
        Assert.Contains("under 1024", (await g.Run("plan_portals", new { to = "1500,64,0", from = "900,64,0" })).Text);
        Assert.False((await g.Run("plan_portals")).Ok);
    }
}
