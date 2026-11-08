using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryQuarryAccessTests
{
    private static readonly P3 Remnant = new(195, 56, 370);
    private static readonly P3 Obstruction = new(194, 46, 366);
    private static readonly P3 Missing20 = new(195, 43, 367), Missing21 = new(195, 42, 368);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(int pawns = 1)
    {
        var (c, h, w) = StoryRig.Make(pawns);
        c.DoneGrace = TimeSpan.Zero;
        foreach (var p in c.Pawns.Values)
        {
            foreach (var type in WorkTypes.All) p.Priorities[type] = type == WorkTypes.Mine ? 1 : 0;
            h.Lives[p.Name] = FakeStoryHost.Live(176, 64, 366, inv: new Stack(4, 0, 24));
        }
        c.ZoneOp("add", "mine", Area.Of(176, 63, 366, 195, 63, 375));
        var zone = c.Zones.Single();
        zone.QuarryTop = 63;
        zone.QuarryLayer = 29; // it had reached the floor before a high stray block appeared
        for (int x = 176; x <= 195; x++) for (int z = 366; z <= 375; z++) for (int y = 35; y <= 63; y++) w.Set(x, y, z, 0);
        var ring = StoryQuarry.Ring(zone.Cells)!;
        for (int k = 0; k <= 29; k++)
        {
            var at = StoryQuarry.Step(ring, 63, k);
            w.Set(at.X, at.Y, at.Z, 1);
        }
        w.Set(Remnant.X, Remnant.Y, Remnant.Z, 4);
        w.Set(Obstruction.X, Obstruction.Y, Obstruction.Z, 4);
        w.Set(Missing20.X, Missing20.Y, Missing20.Z, 0);
        w.Set(Missing21.X, Missing21.Y, Missing21.Z, 0);
        w.Set(196, 43, 367, 0); // missing20 has no adjacent face; the side/under scaffold has a wall anchor one block lower
        w.Set(196, 44, 367, 0); // live wall top is y42; the side support remains visible from above
        w.Set(196, 45, 367, 0);
        return (c, h, w);
    }

    private static string Kind(JsonObject job) => job["kind"]!.GetValue<string>();
    private static string At(JsonObject job) => job["at"]!.GetValue<string>();
    private static string[] Route(JsonObject job) => job["quarry_approach"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static void Finish(StoryColony c, FakeStoryWorld w, JsonObject job)
    {
        var at = At(job).Split(',').Select(int.Parse).ToArray();
        w.Set(at[0], at[1], at[2], Kind(job) == "place" ? 4 : 0);
        c.Report(c.P(), job["id"]!.GetValue<string>(), "done", null, null);
    }

    [Theory]
    [InlineData(2, 0, -1)] // north rim, descending east
    [InlineData(7, 1, 0)] // east rim, descending south
    [InlineData(12, 0, 1)] // south rim, descending west
    [InlineData(17, -1, 0)] // west rim, descending north
    public void Floating_stairs_use_an_exposed_outside_support_in_every_direction(int layer, int dx, int dz)
    {
        var (c, h, w) = StoryRig.Make();
        foreach (var type in WorkTypes.All) c.Pawns.Values.Single().Priorities[type] = type == WorkTypes.Mine ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(10, 64, 10, inv: new Stack(4, 0, 24));
        c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 15, 63, 15));
        var zone = c.Zones.Single();
        zone.QuarryTop = 63;
        var ring = StoryQuarry.Ring(zone.Cells)!;
        for (int x = 10; x <= 15; x++) for (int z = 10; z <= 15; z++) for (int y = 44; y <= 63; y++) w.Set(x, y, z, 0);
        for (int k = 0; k <= 19; k++)
        {
            var step = StoryQuarry.Step(ring, 63, k);
            w.Set(step.X, step.Y, step.Z, k == layer || k == layer + 1 ? 0 : 1);
        }
        w.Set(13, 56, 13, 4);
        var missing = StoryQuarry.Step(ring, 63, layer);
        var outside = missing.Add(dx, 0, dz);
        for (int y = outside.Y; y <= 66; y++) w.Set(outside.X, y, outside.Z, 0);
        var previous = StoryQuarry.Step(ring, 63, layer - 1);
        Assert.Equal(1, w.Block(previous.X, previous.Y, previous.Z).Id);
        Assert.Equal(0, w.Block(previous.X, previous.Y - 1, previous.Z).Id); // tempting but hidden support under the pawn's step

        var job = c.Next();
        Assert.Equal("place", Kind(job));
        Assert.Equal(missing.ToString(), At(job));
        Assert.Equal(outside.ToString(), job["scaffold"]?[missing.ToString()]?.GetValue<string>());
    }

    [Fact]
    public void A_high_remnant_repairs_deep_stair_headroom_and_floating_missing_steps_before_approaching_the_real_floor()
    {
        var (c, _, w) = Rig();
        var clear = c.Next();
        Assert.Equal("dig", Kind(clear));
        Assert.Equal(Obstruction.ToString(), At(clear));
        Assert.Equal(7, c.Zones.Single().QuarryLayer); // scan rewound to y56, but access still extends down to y35
        Assert.Equal("193,47,366", Route(clear)[^1]);
        Finish(c, w, clear);

        var first = c.Next();
        Assert.Equal("place", Kind(first));
        Assert.Equal(Missing20.ToString(), At(first));
        Assert.Equal("196,43,367", first["scaffold"]?[Missing20.ToString()]?.GetValue<string>());
        Assert.Equal("195,45,366", Route(first)[^1]); // the preceding intact step, never through the gap
        Finish(c, w, first);

        var second = c.Next();
        Assert.Equal("place", Kind(second));
        Assert.Equal(Missing21.ToString(), At(second));
        Assert.Equal("195,44,367", Route(second)[^1]);
        Finish(c, w, second);

        var dig = c.Next();
        Assert.Equal("dig", Kind(dig));
        Assert.Equal(Remnant.ToString(), At(dig));
        Assert.Equal("176,64,366", Route(dig)[0]);
        Assert.Equal("194,35,375", Route(dig)[^1]);
    }

    [Fact]
    public void A_seal_only_high_layer_also_repairs_the_deep_stair_before_entering_the_quarry()
    {
        var (c, _, w) = Rig();
        w.Set(Remnant.X, Remnant.Y, Remnant.Z, 9);
        var job = c.Next();
        Assert.Equal("dig", Kind(job));
        Assert.Equal(Obstruction.ToString(), At(job));
        Assert.Equal("193,47,366", Route(job)[^1]);
        Assert.Equal(7, c.Zones.Single().QuarryLayer);
    }

    [Fact]
    public void Access_does_not_look_through_an_intact_floor_to_repair_a_cave_stair_below_it()
    {
        var (c, _, w) = Rig();
        for (int x = 176; x <= 195; x++) for (int z = 366; z <= 375; z++) w.Set(x, 54, z, 1);
        var job = c.Next();
        Assert.Equal("dig", Kind(job));
        Assert.Equal(Remnant.ToString(), At(job));
        Assert.Equal("185,55,366", Route(job)[^1]);
        Assert.Equal(0, w.Block(Missing20.X, Missing20.Y, Missing20.Z).Id);
        Assert.Equal(4, w.Block(Obstruction.X, Obstruction.Y, Obstruction.Z).Id);
    }

    [Fact]
    public void Other_miners_wait_for_the_first_access_repair_instead_of_entering_through_the_gap()
    {
        var (c, _, _) = Rig(pawns: 2);
        Assert.Equal(Obstruction.ToString(), At(c.Next()));
        var other = c.Next(1);
        Assert.DoesNotContain(Kind(other), new[] { "dig", "place" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Access_clearance_leaves_containers_and_build_cells_alone(bool build)
    {
        var (c, _, w) = Rig();
        if (build) c.Builds.Add(new BuildState { Id = "keep", Kind = "block", Status = "done",
            Ghosts = { new GhostState { At = Obstruction, Id = 4 } } });
        else w.Set(Obstruction.X, Obstruction.Y, Obstruction.Z, 54);
        Assert.DoesNotContain(Kind(c.Next()), new[] { "dig", "place" });
        Assert.Equal(build ? 4 : 54, w.Block(Obstruction.X, Obstruction.Y, Obstruction.Z).Id);
    }

    [Theory]
    [InlineData(1, 0, 0, false)]
    [InlineData(1, 1, 0, true)]
    [InlineData(1, 0, 3, true)]
    [InlineData(0, 0, 1, false)]
    [InlineData(0, 0, 2, true)]
    public void A_floating_step_starts_only_with_two_real_blocks_for_step_and_temporary_support(int cobble, int dirt, int stored, bool ready)
    {
        var (c, h, w) = Rig();
        w.Set(Obstruction.X, Obstruction.Y, Obstruction.Z, 0);
        var inventory = new List<Stack>();
        if (cobble > 0) inventory.Add(new Stack(4, 0, cobble));
        if (dirt > 0) inventory.Add(new Stack(3, 0, dirt));
        h.Lives[c.P()] = FakeStoryHost.Live(176, 64, 366, inv: inventory.ToArray());
        if (stored > 0)
        {
            var chest = new P3(196, 64, 368);
            w.Set(chest.X, chest.Y, chest.Z, 54);
            c.StorageOp("set", [chest], null, null, null);
            c.AddContents(c.StorageAt(chest)!, new Stack(4, 0, stored));
        }
        var job = c.Next();
        if (!ready) { Assert.NotEqual("place", Kind(job)); return; }
        Assert.Equal("place", Kind(job));
        Assert.Equal(Missing20.ToString(), At(job));
        Assert.NotNull(job["scaffold"]?[Missing20.ToString()]);
        Assert.Equal(dirt > 0 ? "dirt" : "cobblestone", job["block"]!.GetValue<string>());
        if (stored > 0) Assert.Equal(Math.Min(2, stored), job["fetch"]!.AsArray().Sum(n => n!["n"]!.GetValue<int>()));
    }
}
