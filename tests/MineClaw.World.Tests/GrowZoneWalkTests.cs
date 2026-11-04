using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Fox 11-04: "The pawns need to crouch-walk through growing zones, so they don't crush plants or tilled ground" and "If the ground in a
/// grow zone isn't planted or tilled (for whatever reason), they don't have to crouch-walk — better for speed". The zone
/// (<see cref="PathOptions.GrowZone"/>) only filters; the live blocks decide which moves are sneaked.
/// </summary>
public class GrowZoneWalkTests
{
    private const int Farmland = 60, Wheat = 59, Sapling = 6;
    private static readonly PathOptions Strict = new() { AllowPartial = false, AllowParkour = false };

    public static readonly TheoryData<bool> Versions = new() { false, true };

    private static WorldState NewWorld(bool v1122) => v1122 ? Physics1122Tests.World() : TestWorld.Create();

    /// <summary>A 1-wide corridor (walls on both sides) along x at feet 64 through a zone x 0..9: x 0..4 tilled and sown, x 5..9 bare.</summary>
    private static WorldState HalfTilled(bool v1122)
    {
        var w = NewWorld(v1122);
        w.Fill(-6, 60, -1, 16, 63, 1, Dirt);
        w.Fill(-6, 64, -1, 16, 65, -1, Cobble);
        w.Fill(-6, 64, 1, 16, 65, 1, Cobble);
        w.Fill(0, 63, 0, 4, 63, 0, Farmland);
        w.Fill(0, 64, 0, 4, 64, 0, Wheat);
        return w;
    }

    private static bool Zone(int x, int y, int z) => x is >= 0 and <= 9 && z is >= -1 and <= 1 && y is >= 62 and <= 66;

    [Theory, MemberData(nameof(Versions))]
    public void Across_a_half_tilled_zone_only_the_tilled_half_is_sneaked(bool v1122)
    {
        var w = HalfTilled(v1122);
        var r = Pathfinder.FindPath(w, new BlockPos(-4, 64, 0), new GoalBlock(14, 64, 0), Strict with { GrowZone = Zone });
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.DoesNotContain(r.Steps, s => s.Kind is MoveKind.Jump or MoveKind.Drop or MoveKind.JumpGap or MoveKind.SprintJump);
        foreach (var s in r.Steps)
        {
            // Onto the field, across it, off it, and the bare cell beside its last crop: sneaked. The bare half and outside: walked.
            bool careful = s.X is >= 0 and <= 6;
            Assert.True(s.Sneak == careful, $"step {s} sneak={s.Sneak}, expected {careful}");
        }
    }

    [Theory, MemberData(nameof(Versions))]
    public void Without_a_zone_only_farmland_is_sneaked(bool v1122)
    {
        var w = HalfTilled(v1122);
        w.Fill(7, 64, 0, 7, 64, 0, Sapling); // a sapling outside any zone: walked through
        var r = Pathfinder.FindPath(w, new BlockPos(-4, 64, 0), new GoalBlock(14, 64, 0), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.All(r.Steps.Where(s => s.X >= 6), s => Assert.False(s.Sneak, $"step {s} sneaked"));
    }

    [Theory, MemberData(nameof(Versions))]
    public void Saplings_in_a_zone_are_passed_sneaking_and_a_harvested_zone_is_walked(bool v1122)
    {
        var w = HalfTilled(v1122);
        w.Fill(0, 63, 0, 4, 64, 0, Air);
        w.Fill(0, 63, 0, 4, 63, 0, Grass);
        w.Set(7, 64, 0, Sapling);
        var r = Pathfinder.FindPath(w, new BlockPos(-4, 64, 0), new GoalBlock(14, 64, 0), Strict with { GrowZone = Zone });
        Assert.Equal(PathStatus.Found, r.Status);
        foreach (var s in r.Steps)
        {
            bool careful = s.X is >= 6 and <= 9; // into the sapling's neighbour, its cell, past it and off
            Assert.True(s.Sneak == careful, $"step {s} sneak={s.Sneak}, expected {careful}");
        }
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_field_with_an_aisle_is_crossed_on_the_aisle(bool v1122)
    {
        var w = NewWorld(v1122);
        w.Fill(-2, 60, -4, 12, 63, 4, Dirt);
        w.Fill(0, 63, -4, 10, 63, 4, Farmland);
        w.Fill(0, 64, -4, 10, 64, 4, Wheat);
        w.Fill(0, 63, 1, 10, 63, 1, Grass); // an aisle along x at z 1 (a grass row: nothing sown)
        w.Fill(0, 64, 1, 10, 64, 1, Air);
        var r = Pathfinder.FindPath(w, new BlockPos(-2, 64, 1), new GoalBlock(12, 64, 1),
            Strict with { GrowZone = (x, y, z) => x is >= 0 and <= 10 && z is >= -4 and <= 4 });
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.DoesNotContain(r.Steps, s => w.GetBlock(s.X, s.Y, s.Z) == Farmland || w.GetBlock(s.X, s.Y - 1, s.Z) == Farmland);
        Assert.All(r.Steps.Where(s => s.X is >= 0 and <= 10), s => Assert.True(s.Sneak, $"aisle step {s} beside the wheat not sneaked"));
    }
}
