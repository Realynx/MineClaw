using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryBuildFrontierTests
{
    private static readonly P3 Lower = new(4, 64, 8);
    private static readonly P3 Upper = new(12, 69, 8);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, BuildState B) Rig(string lowerState, bool supported = true)
    {
        var (c, h, w) = StoryRig.Make(1, Release1122GameData.Instance);
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(12, 69, 5, inv: new Stack(54, 0, 8));
        var b = new BuildState
        {
            Id = "independent-floor", Kind = "block", Label = "Storehouse",
            Ghosts =
            [
                new GhostState { At = Lower, Id = 98, Item = "stonebrick", State = lowerState,
                    Reason = lowerState == "blocked" ? "out of reach: no path to block" : null },
                new GhostState { At = Upper, Id = 54, Item = "chest" },
            ],
        };
        c.Builds.Add(b);
        if (supported)
            for (int x = 10; x <= 14; x++)
                for (int z = 5; z <= 10; z++) w.Set(x, 68, z, 5);
        return (c, h, w, b);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("leased")]
    [InlineData("todo")]
    public void A_supported_upper_chest_does_not_wait_for_an_unrelated_lower_cell(string lowerState)
    {
        var (c, _, _, b) = Rig(lowerState);
        // "todo" has no stonebrick stock; "blocked" cannot currently be reached; "leased" belongs to another builder.
        // None is a dependency of the chest on its already-built upper floor.
        var job = c.Next();
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.Equal(Upper.ToString(), job["at"]!.GetValue<string>());
        Assert.Equal("chest", job["want"]!.GetValue<string>());
        Assert.Equal(lowerState, b.Ghosts[0].State);
        Assert.Equal("leased", b.Ghosts[1].State);
    }

    [Fact]
    public void An_upper_cell_without_actual_support_waits_instead_of_receiving_a_failing_job()
    {
        var (c, _, _, b) = Rig("blocked", supported: false);
        Assert.NotEqual("place", c.Next()["kind"]?.GetValue<string>());
        Assert.Equal("todo", b.Ghosts[1].State);
    }

    [Fact]
    public void Ready_lower_placements_remain_preferred_even_when_the_pawn_is_next_to_the_upper_floor()
    {
        var (c, h, _, _) = Rig("todo");
        h.Lives[c.P()] = FakeStoryHost.Live(12, 69, 5, inv: [new Stack(54, 0, 8), new Stack(98, 0, 8)]);
        var job = c.Next();
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.Equal(Lower.ToString(), job["at"]!.GetValue<string>());
    }

    [Fact]
    public void A_pure_demolition_plan_still_works_top_down()
    {
        var (c, _, w, b) = Rig("todo");
        b.Kind = "remove";
        foreach (var g in b.Ghosts) { g.Remove = true; g.Item = null; g.Id = 1; w.Set(g.At.X, g.At.Y, g.At.Z, 1); }
        var job = c.Next();
        Assert.Equal("dig", job["kind"]!.GetValue<string>());
        Assert.Equal(Upper.ToString(), job["at"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    public void A_chest_can_use_another_chest_as_its_only_support_in_modern_but_not_beta(bool modern, int meta)
    {
        var (c, h, w) = StoryRig.Make(1, modern ? Release1122GameData.Instance : Beta181GameData.Instance);
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(12, 64, 5, inv: new Stack(54, 0, 8));
        var at = new P3(12, 65, 8);
        w.Set(12, 64, 8, 54, 2);
        var chest = new GhostState { At = at, Id = 54, Meta = meta, Item = "chest" };
        c.Builds.Add(new BuildState
        {
            Id = "stacked-chests", Kind = "block", Label = "Storehouse",
            // Keep this cell above the unfinished layer so a scaffold cannot mask a missing support check.
            Ghosts = [new GhostState { At = Lower, Id = 98, Item = "stonebrick", State = "blocked" }, chest],
        });

        var job = c.Next();
        if (modern)
        {
            Assert.Equal("place", job["kind"]!.GetValue<string>());
            Assert.Equal(at.ToString(), job["at"]!.GetValue<string>());
            Assert.Equal("chest", job["want"]!.GetValue<string>());
            Assert.Equal("leased", chest.State);
        }
        else
        {
            Assert.NotEqual("place", job["kind"]?.GetValue<string>());
            Assert.Equal("todo", chest.State);
        }
    }
}
