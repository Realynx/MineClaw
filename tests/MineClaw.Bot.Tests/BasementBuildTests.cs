using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// A design that digs into the ground (<c>ground N</c>): a site puts its layer N one above the ground block and the layers under
/// it into the terrain; its forced air there is dug before anything is placed below it, and a design without <c>ground</c>
/// places exactly as before.
/// </summary>
public class BasementBuildTests
{
    private const string Pit = "size 3x3x3\nground 3\npalette C=cobblestone\nlayer 0\nCCC\nCCC\nCCC\nlayer 1-2\n...\n...\n...\n";

    /// <summary>A dirt island: ground block y 63, dirt down to 56, the body standing on it south of the middle.</summary>
    private static WorldState Ground(int r = 6)
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -r; x <= r; x++) for (int z = -r; z <= r; z++) for (int y = 56; y <= 63; y++) w.SetBlock(x, y, z, Blocks.Dirt, 0);
        w.Player.ServerTeleport(new Vec3d(0.5, 64, r - 0.5), 64 + 1.62, 0, 0, true);
        return w;
    }

    [Fact]
    public void Ground_is_parsed_and_a_site_puts_the_dug_layers_under_its_surface()
    {
        var (bp, issues) = BlueprintParser.Parse(Pit);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        Assert.Equal(3, bp.Ground);
        Assert.Equal(new BlockPos(-1, 61, -1), bp.OriginOn(new BlockPos(0, 63, 0)));
        var (flat, _) = BlueprintParser.Parse("size 3x1x3\npalette C=cobblestone\nlayer 0\nCCC\nCCC\nCCC\n");
        Assert.Equal(0, flat.Ground);
        Assert.Equal(new BlockPos(-1, 64, -1), flat.OriginOn(new BlockPos(0, 63, 0))); // as before: one above the ground block
        Assert.Contains(BlueprintParser.Parse("size 3x2x3\nground 5\npalette C=cobblestone\nlayer 0\nCCC\n").Issues, i => i.Code == "ground");
        // An unsited design that digs: drawn on a ground at y 63 (never under the world's floor).
        var rec = new BlueprintRecord { Dsl = Pit };
        Assert.Equal(63, BlueprintService.UnsitedGround(rec, bp).Y);
        Assert.Equal(0, BlueprintService.UnsitedGround(rec, flat).Y);
        // A turned build keeps its ground.
        var (plan, _) = BlueprintNormalizer.Normalize(bp, bp.OriginOn(new BlockPos(0, 63, 0)));
        Assert.Equal(3, PlanTransform.Apply(plan!, new BuildTransform(Quarters: 1)).Blueprint.Ground);
    }

    [Fact]
    public void The_pit_is_dug_top_down_before_its_floor_goes_in()
    {
        var w = Ground();
        var (bp, _) = BlueprintParser.Parse(Pit);
        var (plan, issues) = BlueprintNormalizer.Normalize(bp, bp.OriginOn(new BlockPos(0, 63, 0)));
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        Assert.Equal(18, plan!.Air.Count); // y 62..63 under the surface
        Assert.All(plan.Blocks.Keys, p => Assert.Equal(61, p.Y));
        var built = BuildPlanner.Plan(plan, w.PlanningView, Pathfinder.StartCellFor(w.PlanningView, w.Player.Position)!.Value, null,
            new BuildPlannerOptions { GateBlocks = 0 });
        var steps = built.Steps;
        var digs = steps.Select((s, i) => (s, i)).Where(x => x.s.Action == StepAction.Clear && plan.Air.Contains(x.s.Target)).ToList();
        Assert.Equal(18, digs.Count);
        Assert.Equal(digs.Select(d => d.s.Target.Y).OrderByDescending(y => y), digs.Select(d => d.s.Target.Y)); // top-down
        int lastDig = digs.Max(d => d.i);
        var places = steps.Select((s, i) => (s, i)).Where(x => x.s.Action == StepAction.Place).ToList();
        Assert.NotEmpty(places);
        Assert.All(places, p => Assert.True(p.i > lastDig, $"{p.s} placed before the pit was dug"));
    }
}
