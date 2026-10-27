namespace MineClaw.World.Tests;

using static MineClaw.World.Tests.TestBlocks;

/// <summary>
/// Item 11 (live 10-27): working the wheat field must not trample it. mc-dev Entity.move calls Block.b(…, entity) on each step
/// of a non-sneaking walker and BlockSoil.b turns farmland to dirt 1 in 4: paths go around fields, and any step on farmland
/// (or a jump onto it) is sneaked.
/// </summary>
public class FarmlandPathTests
{
    private const int Farmland = 60;
    private static readonly PathOptions Strict = new() { AllowPartial = false, AllowParkour = false };

    [Fact]
    public void Path_goes_around_a_field_when_it_can()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, -2, 8, 63, 2, Cobble);
        w.Fill(3, 63, -2, 5, 63, 1, Farmland); // the field; z=2 stays a cobble path around it
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(8, 64, 0), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.DoesNotContain(r.Steps, s => w.GetBlock(s.X, s.Y - 1, s.Z) == Farmland);
    }

    [Fact]
    public void Steps_onto_or_across_farmland_are_sneaked_and_never_plain_jumps()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 8, 63, 0, Cobble);
        w.Fill(3, 63, 0, 5, 63, 0, Farmland); // a 1-wide strip: the only way across is over the field
        w.Fill(7, 64, 0, 8, 64, 0, Cobble);   // a step up after it
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(8, 65, 0), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        var onField = r.Steps.Where(s => w.GetBlock(s.X, s.Y - 1, s.Z) == Farmland).ToList();
        Assert.NotEmpty(onField);
        Assert.All(onField, s => Assert.True(s.Sneak, $"step {s.Pos} on farmland not sneaked"));
        // Leaving the field is sneaked too (the first step off it still treads the farmland).
        int last = r.Steps.ToList().FindLastIndex(s => w.GetBlock(s.X, s.Y - 1, s.Z) == Farmland);
        Assert.True(r.Steps[last + 1].Sneak, "step off the field not sneaked");
    }
}
