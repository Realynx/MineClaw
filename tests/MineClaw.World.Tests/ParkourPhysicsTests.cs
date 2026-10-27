using MineClaw.World;
using Xunit.Abstractions;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Gap-jump physics facts and the max-gap table (b1.8.1 movement: sprint 0.2806 b/t on ground, jump 0.42, sprint-jump
/// boost 0.2 along the yaw, air accel 0.02 (+30 % sprinting), air drag 0.91).
/// </summary>
public class ParkourPhysicsTests(ITestOutputHelper output)
{
    /// <summary>Start platform x = −4..0 (top y = 63), a gap of <paramref name="gap"/> air columns, then a landing block
    /// whose top is at 64 + <paramref name="dy"/> (over a floor 4 below the start for dy ≥ −3, or the void).</summary>
    internal static WorldState GapWorld(int gap, int dy, bool voidBelow = false, int landingId = Stone, int landingMeta = 0)
    {
        var w = TestWorld.Create();
        w.Fill(-4, 60, -1, 0, 63, 1, Dirt);
        int lx = gap + 1;
        if (landingId == Slab) { w.Fill(lx, 60, -1, lx + 3, 62 + dy, 1, Dirt); w.Fill(lx, 63 + dy, -1, lx + 3, 63 + dy, 1, Slab); }
        else w.Fill(lx, 60, -1, lx + 3, 63 + dy, 1, landingId, landingMeta);
        if (!voidBelow) w.Fill(1, Math.Min(61, 62 + dy), -1, gap, Math.Min(61, 62 + dy), 1, Stone); // shallow pit 1 below the landing: a miss is a small fall
        return w;
    }

    /// <summary>Largest gap a schedule can clear at all (perfect timing, no perturbation), and the planner's robust max.</summary>
    private static (int Perfect, int Robust) MaxGap(int dy, bool sprint, int runCells = 3, bool slab = false, bool overVoid = false)
    {
        int perfect = 0, robust = 0;
        for (int gap = 1; gap <= 6; gap++)
        {
            var w = GapWorld(gap, dy, voidBelow: overVoid, landingId: slab ? Slab : Stone);
            double toFloor = 64 + dy + (slab ? -0.5 : 0);
            var from = new BlockPos(-runCells, 64, 0);
            var to = new BlockPos(gap + 1, 64 + dy, 0);
            if (Parkour.Plan(w, from, 64, to, toFloor, sprint, overVoid, 4.3, 3, out _) is not null) robust = gap;
            float yaw = -90;
            for (int t = 1; t < 30; t++)
            {
                var o = Parkour.Simulate(w, Parkour.StandingAt(new Vec3d(-runCells + 0.5, 64, 0.5), yaw),
                    new ParkourSchedule(yaw, sprint, t, to.X + 0.5, 0.5), 0, to, toFloor, 10);
                if (o.Ok) { perfect = gap; break; }
            }
        }
        return (perfect, robust);
    }

    [Fact]
    public void SprintSpeeds_MatchVanilla()
    {
        var w = TestWorld.Create();
        w.Fill(-20, 63, -2, 20, 63, 2, Stone);
        var s = TestWorld.Standing(-15.5, 64, 0.5);
        TestWorld.Run(w, s, new MoveInput(Forward: 1, Sprint: true, Yaw: -90), 30);
        Assert.Equal(0.2806, s.Velocity.HorizontalLength / 0.546, 3); // post-friction velocity / 0.546 = per-tick move
        // A sprint-jump adds 0.2 along the yaw on the takeoff tick (EntityLiving.S(), at() = sprinting).
        var a = s.Clone();
        var b = s.Clone();
        PlayerPhysics.Tick(w, a, new MoveInput(Forward: 1, Sprint: true, Jump: true, Yaw: -90));
        PlayerPhysics.Tick(w, b, new MoveInput(Forward: 1, Sprint: false, Jump: true, Yaw: -90));
        Assert.True(a.Position.X - s.Position.X > b.Position.X - s.Position.X + 0.2);
        Assert.Equal(0.42 - 0.08, a.Velocity.Y / 0.98 + 0, 2);
    }

    [Fact]
    public void MaxGapTable()
    {
        output.WriteLine("dy | sprint perfect/robust/void-strict | walk perfect/robust/void-strict   (gap = air columns between the blocks, 3-cell run-up)");
        var rows = new Dictionary<int, ((int Perfect, int Robust) S, (int Perfect, int Robust) W)>();
        foreach (int dy in new[] { 1, 0, -1, -2, -3 })
        {
            var sp = MaxGap(dy, true);
            var wk = MaxGap(dy, false);
            var sv = MaxGap(dy, true, overVoid: true);
            var wv = MaxGap(dy, false, overVoid: true);
            rows[dy] = (sp, wk);
            output.WriteLine($"{dy,+3} | {sp.Perfect}/{sp.Robust}/{sv.Robust} | {wk.Perfect}/{wk.Robust}/{wv.Robust}  air travel sprint {Parkour.AirTravel(dy, true):0.00} walk {Parkour.AirTravel(dy, false):0.00}");
        }
        var slab = MaxGap(0, true, slab: true);
        output.WriteLine($"slab landing (top 63.5, rise -0.5) sprint {slab.Perfect}/{slab.Robust}");
        for (int r = 0; r <= 2; r++)
        {
            var sr = MaxGap(0, true, runCells: r);
            output.WriteLine($"flat, run-up {r} cells: sprint {sr.Perfect}/{sr.Robust}");
        }
        Assert.True(rows[0].S.Robust >= 3, "sprint-jump must clear a 3-block flat gap robustly");
        Assert.True(rows[0].S.Robust > rows[0].W.Robust, "sprinting must reach further");
        Assert.True(rows[1].S.Robust >= 1);
        Assert.True(rows[-3].S.Robust >= rows[0].S.Robust);
    }
}
