using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 11-11: the owner's mine marks were not dug, and every idle miner said only "waiting: quarry …: stair step … needs a supported
/// scaffold" (docs/web/story-mode.md "Quarries", "Idle with work"). Two things at once. The quarry's next missing step had nothing to
/// click and no open side to scaffold from, so the stair stayed broken for good: now its scaffold goes over it, in its head room. And
/// the marks all waited for a tool (a soil rule asking for a shovel nobody had): that was said nowhere, so the quarry's hold read as
/// what kept the miners off them. A held quarry never keeps a miner from other mining work, and a removed mine design lets go of
/// everything it held.
/// </summary>
public sealed class StoryQuarryHoldTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Kind(JsonObject job) => job["kind"]!.GetValue<string>();
    private static string At(JsonObject job) => job["at"]?.GetValue<string>() ?? "";

    private static void Finish(StoryColony c, FakeStoryWorld w, JsonObject job)
    {
        var at = At(job).Split(',').Select(int.Parse).ToArray();
        w.Set(at[0], at[1], at[2], Kind(job) == "place" ? 4 : 0);
        c.Report(c.P(), job["id"]!.GetValue<string>(), "done", null, null);
    }

    /// <summary>
    /// A 6×6 quarry at 10..15 × 10..15, dug out from y 63 down to its floor at y 44, its stair whole but for steps 17 (10,46,13) and 18
    /// (10,45,12), a stray block high in the pit (13,56,13) that rewinds the working layer while its access still runs to the floor.
    /// Step 17 has nothing to click: the pit is open beside and under it, and the wall outside it is a pocket two high whose top cell is
    /// roofed (a scaffold there could never be dug back), as is the one beside the step's neighbour. The pit floor is 4 under step 16,
    /// a drop no pawn walks: the repair is done from step 16 or not at all.
    /// </summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, ZoneState Z) FloatingStep(Clock? clock = null, int pawns = 1, params Stack[] inv)
    {
        var (c, h, w) = StoryRig.Make(pawns, time: clock);
        c.DoneGrace = TimeSpan.Zero;
        foreach (var (pawn, i) in c.Pawns.Values.Select((v, i) => (v, i)))
            foreach (var type in WorkTypes.All) pawn.Priorities[type] = type == WorkTypes.Mine && i == 0 ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(10, 64, 9, inv: inv.Length > 0 ? inv : [new Stack(4, 0, 24)]);
        Assert.Null(c.ZoneOp("add", "mine", Area.Of(10, 63, 10, 15, 63, 15)).Error);
        var zone = c.Zones.Single();
        zone.QuarryTop = 63;
        var ring = StoryQuarry.Ring(zone.Cells)!;
        for (int x = 10; x <= 15; x++) for (int z = 10; z <= 15; z++) for (int y = 44; y <= 63; y++) w.Set(x, y, z, 0);
        for (int k = 0; k <= 19; k++)
        {
            var step = StoryQuarry.Step(ring, 63, k);
            w.Set(step.X, step.Y, step.Z, k is 17 or 18 ? 0 : 1);
        }
        w.Set(13, 56, 13, 4);
        foreach (var (y, z) in new[] { (45, 13), (46, 13), (45, 12), (46, 12) }) w.Set(9, y, z, 0);
        Assert.Equal(new P3(10, 47, 14), StoryQuarry.Step(ring, 63, 16));
        Assert.Equal(new P3(10, 46, 13), StoryQuarry.Step(ring, 63, 17));
        return (c, h, w, zone);
    }

    private static string QuarryStatus(StoryColony c) => c.State()["zones"]!.AsArray().Single()!["quarry"]!["status"]!.GetValue<string>();

    private static List<string> HeldUp(StoryColony c) => c.Alerts.Where(a => a.Text.Contains("is held up")).Select(a => a.Text).ToList();

    private static void EndHold(StoryColony c)
    {
        if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
    }

    /// <summary>The distance from the eyes of a pawn standing on <paramref name="feet"/> to a point (a face's centre).</summary>
    private static double FromEyes(P3 feet, double x, double y, double z) =>
        Math.Sqrt(Math.Pow(feet.X + 0.5 - x, 2) + Math.Pow(feet.Y + 1.62 - y, 2) + Math.Pow(feet.Z + 0.5 - z, 2));

    [Fact]
    public void A_floating_step_with_no_side_to_scaffold_from_is_placed_from_a_scaffold_over_it()
    {
        var (c, _, w, zone) = FloatingStep();
        // Before: "stair step 10,46,13 needs a supported scaffold", and nothing else, for good.
        var job = c.Next();
        Assert.Equal("place", Kind(job));
        Assert.Equal("10,46,13", At(job));
        Assert.Equal("mine", job["type"]!.GetValue<string>());
        // The scaffold goes in the step's head room, against step 16's side; the step under it, against the scaffold's underside.
        Assert.Equal("10,47,13", job["scaffold"]?["10,46,13"]?.GetValue<string>());
        Assert.Null(job["reach"]); // faces the pawn on step 16 reaches but doesn't see: clicked unseen, never the stationary reach
        Assert.Equal(48, job["quarry_access_y"]!.GetValue<int>()); // done from step 16
        // Both clicks are within a pawn's reach (4.25) from step 16. A column stood up from the floor under the step was not: the
        // floor's top is 5.6 under its eyes, and the pit a drop of 4 to walk into.
        var stand = new P3(10, 48, 14);
        Assert.True(FromEyes(stand, 10.5, 47.5, 14.0) < 4.25); // step 16's north face, for the scaffold
        Assert.True(FromEyes(stand, 10.5, 47.0, 13.5) < 4.25); // the scaffold's underside, for the step
        Assert.True(FromEyes(stand, 10.5, 44.0, 13.5) > 4.25); // the floor's top under the step
        Finish(c, w, job);

        var next = c.Next();
        Assert.Equal("place", Kind(next));
        Assert.Equal("10,45,12", At(next)); // step 18: a side scaffold on step 19's top, as ever
        Finish(c, w, next);
        Assert.Empty(zone.QuarryPlaced ?? []); // steps and their scaffolds are no record of their own
        Assert.Empty(HeldUp(c));
    }

    [Fact]
    public void A_free_side_still_takes_the_one_temporary_scaffold_before_the_head_room()
    {
        var (c, _, w, _) = FloatingStep();
        for (int y = 47; y <= 50; y++) w.Set(9, y, 13, 0); // the pocket outside opens to the sky: its cell is a scaffold spot again
        var job = c.Next();
        Assert.Equal("place", Kind(job));
        Assert.Equal("10,46,13", At(job));
        Assert.Equal("9,46,13", job["scaffold"]?["10,46,13"]?.GetValue<string>());
        Assert.True(job["reach"]!.GetValue<bool>());
    }

    [Fact]
    public void A_floating_steps_placement_that_keeps_failing_is_named_with_its_failure_and_held()
    {
        // Review 11-11: a support for the step placed under its own lease failed, rested and was counted there, never on the step: the
        // status said a bare "placing a step" and the stair held for good with no alert. The scaffold is the step job's own now.
        var clock = new Clock();
        var (c, _, _, _) = FloatingStep(clock);
        void Refuse()
        {
            var j = c.Next();
            Assert.Equal("place", Kind(j));
            Assert.Equal("10,46,13", At(j));
            c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", "no scaffold at 10,47,13 for 10,46,13: refused", null);
        }
        Refuse();
        string held = "stair broken at y 46: placing a step: resting after failures (no scaffold at 10,47,13 for 10,46,13: refused)";
        Assert.Contains(held, QuarryStatus(c));
        Assert.Empty(HeldUp(c)); // one refusal may be anything
        EndHold(c);
        clock.Now += TimeSpan.FromSeconds(31);
        Refuse();
        Assert.NotEqual("place", Kind(c.Next()));
        Assert.Contains(held, Assert.Single(HeldUp(c))); // twice: said
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.StartsWith("waiting: quarry", c.IdleReasonOf(c.P()));
    }

    [Fact]
    public void A_floating_step_with_no_free_cell_for_a_scaffold_is_said_on_the_quarry_and_to_the_pawn()
    {
        var clock = new Clock();
        var (c, h, _, _) = FloatingStep(clock, pawns: 2);
        h.Lives[c.P(1)] = FakeStoryHost.Live(10, 47, 13); // another body stands in the step's head room
        Assert.NotEqual("place", Kind(c.Next()));
        string why = "stair step 10,46,13 needs a supported scaffold: no free cell beside or over it";
        Assert.Contains($"stair broken at y 46: {why}", QuarryStatus(c));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.Equal($"waiting: quarry z1: {why}", c.IdleReasonOf(c.P()));
        Assert.Empty(HeldUp(c)); // a body in the way moves on by itself
        // It steps away: the step is placed from a scaffold over it, and the status no longer says it is held.
        h.Lives[c.P(1)] = FakeStoryHost.Live(20, 64, 20);
        EndHold(c);
        clock.Now += TimeSpan.FromSeconds(6);
        var job = c.Next();
        Assert.Equal("10,46,13", At(job));
        Assert.Equal("10,47,13", job["scaffold"]?["10,46,13"]?.GetValue<string>());
        Assert.DoesNotContain("needs a supported scaffold", QuarryStatus(c));
    }

    /// <summary>A quarry held for good (its floating step: no block to stand it on, none carried, none stored), and two owner's mine
    /// marks on a mound by it.</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) HeldQuarryAndMarks(int mound, Clock clock)
    {
        var (c, h, w, _) = FloatingStep(clock, 1, new Stack(274, 0, 1));
        h.Lives[c.P()] = FakeStoryHost.Bare(20, 64, 12, new Stack(274, 0, 1)); // a stone pickaxe and nothing else
        w.Set(22, 64, 12, mound);
        w.Set(22, 65, 12, mound);
        Assert.Null(c.Designate("add", "mine", null, [new P3(22, 64, 12), new P3(22, 65, 12)], null).Error);
        return (c, h, w);
    }

    [Fact]
    public void A_held_quarry_leaves_its_miners_to_the_owners_mine_marks()
    {
        var clock = new Clock();
        var (c, _, w) = HeldQuarryAndMarks(1, clock);
        var dig = c.Next();
        Assert.Equal("dig", Kind(dig));
        Assert.Equal("designation", c.CurrentOf(c.P())!.Source);
        Assert.Equal("22,65,12", At(dig)); // top-down
        Finish(c, w, dig);
        Assert.Equal("22,64,12", At(c.Next()));
    }

    [Fact]
    public void Marks_waiting_for_a_tool_are_named_ahead_of_a_held_quarry()
    {
        var clock = new Clock();
        var (c, _, _) = HeldQuarryAndMarks(3, clock); // a dirt mound
        c.Meta.Gear.AutoCraftTools = false;
        Assert.Null(c.MaterialRulesOp(new JsonObject
        {
            ["op"] = "set", ["rule"] = new JsonObject { ["material"] = "group:soil", ["tool"] = "shovel", ["tier"] = "wood", ["fallback"] = "plain" },
        }).Error);
        Assert.NotEqual("dig", Kind(c.Next()));
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        // Before: "waiting: quarry z1: stair step 10,46,13 needs a supported scaffold", the only reason (as live), as if the quarry
        // kept it off the marks.
        Assert.StartsWith("waiting: mine marks: 2 waiting for a tool (needs a wooden shovel", c.IdleReasonOf(c.P()));
        Assert.All(c.Designations, d => Assert.Equal("waiting", d.Status));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Removing_a_design_lets_go_of_its_pawns_and_cells(bool mine)
    {
        var (c, h, _) = StoryRig.Make();
        string work = mine ? WorkTypes.Mine : WorkTypes.Build;
        foreach (var type in WorkTypes.All) c.Pawns.Values.Single().Priorities[type] = type == work || type == WorkTypes.Mine ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(6, 64, 3, inv: new Stack(4, 0, 16));
        var item = mine
            ? new JsonObject
            {
                ["kind"] = "mine", ["name"] = "shaft",
                ["blocks"] = new JsonArray(new JsonArray(5, 63, 5), new JsonArray(5, 62, 5), new JsonArray(5, 61, 5)),
            }
            : new JsonObject { ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(5, 64, 5), new JsonArray(5, 65, 5)), ["material"] = "cobblestone" };
        var id = c.BuildsOp("add", new JsonArray(item), null).Result!["ids"]![0]!.GetValue<string>();
        Assert.Equal(mine ? "dig" : "place", Kind(c.Next()));
        Assert.Equal("ghost", c.CurrentOf(c.P())!.Source);

        Assert.Null(c.BuildsOp("remove", null, [id]).Error);
        // Before: a pawn placing a removed build's blocks went on with its batch (a mine design's digs only stopped as ore work).
        Assert.Null(c.CurrentOf(c.P()));
        Assert.Contains(c.P() + ": build removed", h.Interrupted);
        // A mark on its cell is open at once: nothing of the design holds the cell.
        Assert.Null(c.Designate("add", "mine", null, [new P3(5, 63, 5)], null).Error);
        var mark = c.Next();
        Assert.Equal("dig", Kind(mark));
        Assert.Equal("5,63,5", At(mark));
        Assert.Equal("designation", c.CurrentOf(c.P())!.Source);
    }
}
