using System.Text.Json;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>How two overworld portals link, and what a nether route saves over walking. Teleporter.placeInExistingPortal looks for a
/// portal within 128 blocks (a square) of the scaled destination, IN the destination dimension (1.7.10 reference source; 1.12.2 is
/// the same), and only builds a new one when it finds none. From the overworld the destination is x/8, z/8: two overworld portals
/// less than 1024 blocks apart (in x or z) share one nether portal, and leaving it lands at the overworld portal nearest to it
/// (the first one built), so the second portal only ever leads home.</summary>
public sealed record PortalLinkPlan(BlockPos Home, BlockPos Far, double Distance, bool SharedNether, BlockPos NetherHome, BlockPos NetherFar,
    double NetherDistance, double WalkSeconds, double PortalSeconds)
{
    public bool TwoWay => !SharedNether;
    public bool PortalFaster => PortalSeconds < WalkSeconds;
}

public static class PortalLinks
{
    /// <summary>Teleporter.placeInExistingPortal: <c>short short1 = 128</c>, in the destination dimension's blocks.</summary>
    public const int NetherSearch = 128;

    /// <summary>Walking speed of a body over mixed ground including detours (4.3 m/s walking, 1.25x path length).</summary>
    public const double WalkMetresPerSecond = 4.3 / 1.25;

    /// <summary>One transit: walk into the frame, ~4 s portal timer, chunks arriving, a few steps out.</summary>
    public const double TransitSeconds = 12;

    /// <summary>The nether leg is a tunnel or a bridge, not a straight walk: 1.5x its length.</summary>
    public const double NetherDetour = 1.5;

    public static PortalLinkPlan Plan(BlockPos home, BlockPos far)
    {
        var nh = SkillState.Scale(home, toNether: true);
        var nf = SkillState.Scale(far, toNether: true);
        double d = Math.Sqrt(Math.Pow(far.X - home.X, 2) + Math.Pow(far.Z - home.Z, 2));
        double nd = Math.Sqrt(Math.Pow(nf.X - nh.X, 2) + Math.Pow(nf.Z - nh.Z, 2));
        bool shared = Math.Abs(nf.X - nh.X) <= NetherSearch && Math.Abs(nf.Z - nh.Z) <= NetherSearch;
        double walk = d / WalkMetresPerSecond;
        // Far -> home through the portals: in, nether leg (none when shared: the same nether portal), out.
        double portal = 2 * TransitSeconds + (shared ? 0 : nd * NetherDetour / (WalkMetresPerSecond * 1.25));
        return new PortalLinkPlan(home, far, d, shared, nh, nf, nd, walk, portal);
    }

    public static string Describe(PortalLinkPlan p)
    {
        string link = p.SharedNether
            ? $"{p.Distance:0} m apart is under 1024: both portals share one nether portal, so far -> home works and home -> far does not (walk out, ride home)"
            : $"{p.Distance:0} m apart: separate nether portals (home {p.NetherHome.X},{p.NetherHome.Z}, far {p.NetherFar.X},{p.NetherFar.Z}), {p.NetherDistance:0} m of nether between: two-way";
        return $"{link}. Walking {p.WalkSeconds / 60:0.0} min each way; far -> home by portal {p.PortalSeconds / 60:0.0} min" +
               (p.PortalFaster ? " (portal wins)" : " (just walk)");
    }
}

[Skill]
public sealed class PlanPortalsSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "plan_portals", Category = SkillCategory.Nether, Summary = "will a portal pair to a far spot link, and is it faster", Args = "to,from?",
        Schema = SkillJson.Schema("to:pos,from:pos?"), Estimate = TimeSpan.FromSeconds(1),
        Keywords = "plan portals link pair distance far sand source nether travel route worth",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos("to") is not { } far) return Task.FromResult(Fail("to required (x,y,z of the far spot)"));
        var home = args.Pos("from") ?? ctx.Me;
        if (ctx.InNether) return Task.FromResult(Fail("give overworld coordinates and run this in the overworld"));
        return Task.FromResult(Ok(PortalLinks.Describe(PortalLinks.Plan(home, far))));
    }
}
