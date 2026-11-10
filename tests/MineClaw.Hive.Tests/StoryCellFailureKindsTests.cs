using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Failures that are not the cell's do not count against it (docs/web/story-mode.md "Construction"). Fox: "there's some blocked blocks
/// on the storehouse build". Every text a pawn reported about a build cell that was not a known "not now" counted a strike, so a pawn
/// still taking its pillar down, short of dirt to climb with, standing in the cell, dead or hand-controlled blocked the cell after three;
/// a reach miss of one builder among farmers waited for three pawns that never came. One classifier now sorts a cell's report and a
/// build job's whole failure: not about the cell, not now, supply, this pawn's miss, or the cell's own; and every miss says why.
/// </summary>
public sealed class StoryCellFailureKindsTests
{
    private const string Cell = "4,64,12";
    private const string OutOfReach = "out of reach even from a pillar: a chest or workstation under the column";

    /// <summary>1.12.2, <paramref name="pawns"/> pawns that only build (unless <paramref name="builders"/> says fewer), carrying planks
    /// beside the site, and a one-cell planks build at <see cref="Cell"/> (<paramref name="far"/>: a second cell at 20,64,12, a batch of
    /// its own).</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, ManualTime T, GhostState G) Rig(int pawns = 1, int builders = -1, bool far = false)
    {
        var clock = new ManualTime();
        var (c, h, w) = StoryRig.Make(pawns, Release1122GameData.Instance, clock);
        for (int i = 0; i < pawns; i++)
        {
            bool builds = builders < 0 || i < builders;
            foreach (var t in WorkTypes.All) c.Pawns[c.P(i)].Priorities[t] = t == WorkTypes.Build && builds ? 1 : t == WorkTypes.Haul && !builds ? 1 : 0;
            h.Lives[c.P(i)] = FakeStoryHost.Live(0, 64, 4, inv: new Stack(5, 0, 64));
        }
        var item = new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(4, 64, 12)), ["material"] = new JsonArray(5, 0),
        };
        if (far) item["blocks"]!.AsArray().Add(new JsonArray(20, 64, 12));
        Assert.Null(c.BuildsOp("add", new JsonArray(item), null).Error);
        return (c, h, w, clock, c.Builds.Last().Ghosts.First(g => g.At == new P3(4, 64, 12)));
    }

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    /// <summary>Pawn <paramref name="i"/>'s next job; anything but a place job (idle, a walk) is ended at once so the next pick is fresh.</summary>
    private static JsonObject Pick(StoryColony c, int i = 0)
    {
        var j = c.Next(i);
        if (j["kind"]?.GetValue<string>() != "place" && j["id"] is not null) c.Report(c.P(i), Id(j), "done", null, null);
        return j;
    }

    /// <summary>Pawn <paramref name="i"/> is offered the cell now.</summary>
    private static bool Offered(StoryColony c, int i = 0, JsonObject? job = null)
    {
        var j = job ?? Pick(c, i);
        return j["kind"]?.GetValue<string>() == "place" && j["at"]?.GetValue<string>() == Cell;
    }

    /// <summary>The pawn's place job fails at the cell, as the pawn script reports it: that cell, then the batch's end.</summary>
    private static void CellFails(StoryColony c, JsonObject job, string why, int i = 0)
    {
        Assert.True(Offered(c, i, job));
        c.Report(c.P(i), Id(job), "progress", $"cell {Cell} failed: {why}", null);
        c.Report(c.P(i), Id(job), "done", "batch: 0/1 placed", null);
    }

    /// <summary>The place job fails as a whole (the body's action never ran, a single cell's placing).</summary>
    private static void JobFails(StoryColony c, JsonObject job, string why, int i = 0)
    {
        Assert.True(Offered(c, i, job));
        c.Report(c.P(i), Id(job), "failed", why, null);
    }

    /// <summary>The cell as a reconcile pass reopens a blocked one for a try (WP3): one more counted failure blocks it again at once.</summary>
    private static void OneFailureLeft(StoryColony c, FakeStoryWorld w, GhostState g)
    {
        g.State = "blocked";
        g.Reason = "server undid my placement at 4,64,12 (protected area?)";
        c.Reconcile(w);
        Assert.Equal("todo", g.State);
    }

    [Fact]
    public void An_out_of_reach_cell_rests_for_that_pawn_only_and_another_pawn_is_offered_it()
    {
        var (c, _, _, _, g) = Rig(pawns: 2);
        CellFails(c, Pick(c, 0), OutOfReach);
        Assert.Equal("todo", g.State);
        Assert.Equal(OutOfReach, g.Reason);
        Assert.False(Offered(c, 0)); // that pawn rests from this cell
        Assert.True(Offered(c, 1)); // the other is offered it at once
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("can't place at"));
    }

    [Fact]
    public void Out_of_reach_counts_once_every_builder_missed_it()
    {
        // Three pawns, Build on for two: the second miss is every builder's. Counting every online pawn, it never came.
        var (c, _, w, _, g) = Rig(pawns: 3, builders: 2);
        OneFailureLeft(c, w, g);
        CellFails(c, Pick(c, 0), OutOfReach);
        Assert.Equal("todo", g.State);
        Assert.Equal(OutOfReach, g.Reason);
        CellFails(c, Pick(c, 1), OutOfReach, i: 1);
        Assert.Equal("blocked", g.State);
        Assert.Contains(c.Alerts, a => a.Text.Contains("can't place at 4,64,12") && a.Text.Contains("chest or workstation"));
    }

    [Fact]
    public void No_blocks_to_climb_with_is_a_wait_with_a_reason_never_a_blocked_cell()
    {
        const string why = "out of reach: 6 block(s) needed, 0 on me";
        var (c, _, _, t, g) = Rig();
        for (int n = 0; n < 6; n++)
        {
            CellFails(c, Pick(c), why);
            Assert.Equal("todo", g.State);
            Assert.Equal(why, g.Reason);
            t.Now += TimeSpan.FromSeconds(20);
            Assert.False(Offered(c)); // the pawn waits its 30 s
            t.Now += TimeSpan.FromSeconds(11);
        }
        Assert.Single(c.Alerts, a => a.Text.Contains("pawns have no dirt or cobblestone to climb with: store some"));
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("can't place at"));
    }

    [Fact]
    public void Access_cleanup_pending_and_tool_trouble_never_count_against_the_cell()
    {
        var (c, _, w, t, g) = Rig();
        OneFailureLeft(c, w, g);
        string[] whys =
        [
            "access pillar cleanup pending; remaining work returned to the colony",
            "can't clear the cell 4,64,12: needs a pickaxe: stone would lose the drop",
        ];
        for (int n = 0; n < 4; n++)
        {
            var why = whys[n % 2];
            CellFails(c, Pick(c), why);
            Assert.Equal("todo", g.State);
            Assert.Equal(why, g.Reason);
            t.Now += TimeSpan.FromSeconds(16); // a moment's rest for everyone, no back-off
        }
        Assert.True(Offered(c));
    }

    [Fact]
    public void A_dead_pawn_or_a_hand_controlled_one_costs_the_cell_nothing()
    {
        var (c, _, w, t, g) = Rig();
        OneFailureLeft(c, w, g);
        foreach (var why in new[] { "dead", "human is controlling this pawn", "not in game", "build batch interrupted", "cancelled" })
        {
            JobFails(c, Pick(c), why);
            Assert.Equal("todo", g.State);
            Assert.Equal(why, g.Reason);
            t.Now += TimeSpan.FromSeconds(16); // no back-off either
        }
        Assert.True(Offered(c));
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("can't place at"));
    }

    [Fact]
    public void I_am_in_the_way_is_not_now()
    {
        const string why = "I'm in the way of 4,64,12";
        var (c, _, w, t, g) = Rig();
        OneFailureLeft(c, w, g);
        CellFails(c, Pick(c), why);
        Assert.Equal("todo", g.State);
        Assert.Equal(why, g.Reason);
        Assert.False(Offered(c));
        t.Now += TimeSpan.FromSeconds(16);
        Assert.True(Offered(c));
    }

    [Fact]
    public void A_server_refusal_with_a_body_in_the_cell_is_not_now()
    {
        const string why = "server undid my placement at 4,64,12 (protected area?)";
        var (c, _, w, t, g) = Rig();
        OneFailureLeft(c, w, g);
        var job = Pick(c);
        w.Ents.Add(new StoryEntity(7, "mob", "zombie", 4.6, 64, 12.2, Hostile: true)); // it walked in after the pick
        CellFails(c, job, why);
        Assert.Equal("todo", g.State);
        Assert.Equal(Refused, g.Reason);

        // With nobody in the cell the same refusal is the cell's own: its one failure left blocks it.
        w.Ents.Clear();
        t.Now += TimeSpan.FromSeconds(16);
        CellFails(c, Pick(c), why);
        Assert.Equal("blocked", g.State);
    }

    [Fact]
    public void A_cell_with_a_mob_in_it_is_not_offered()
    {
        var (c, h, w, _, g) = Rig(pawns: 2, builders: 1);
        w.Ents.Add(new StoryEntity(7, "mob", "cow", 4.5, 64, 12.5));
        Assert.False(Offered(c, 0));
        w.Ents.Clear();
        // Another pawn's body by its live position (the world's copy of it lags behind).
        h.Lives[c.P(1)] = FakeStoryHost.Live(4, 63, 12, inv: new Stack(5, 0, 64)); // its head is in the cell
        Assert.False(Offered(c, 0));
        h.Lives[c.P(1)] = FakeStoryHost.Live(0, 64, 0);
        Assert.True(Offered(c, 0));
        Assert.Equal("leased", g.State);
    }

    [Fact]
    public void A_miss_leaves_its_reason_on_the_cell()
    {
        // A build job's whole failure that is its pawn's own (no way there) used to rest the cell for that pawn with no reason at all.
        const string why = "no path to 4,64,12: unreachable even by pillaring/bridging";
        var (c, _, _, _, g) = Rig(pawns: 2);
        JobFails(c, Pick(c, 0), why);
        Assert.Equal("todo", g.State);
        Assert.Equal(why, g.Reason);
        Assert.False(Offered(c, 0));
        Assert.True(Offered(c, 1));
    }

    /// <summary>A miss's text stayed on the cell when another pawn took it, so the map drew it as failed ("resting after: …", counted in
    /// "Retry failed cells (N)") until it was built. Taken again, the cell rests no more; a counted failure's reason stays.</summary>
    [Fact]
    public void A_miss_reason_goes_when_the_cell_is_taken_again_and_a_counted_one_stays()
    {
        var (c, _, w, t, g) = Rig(pawns: 2);
        JobFails(c, Pick(c, 0), "human is controlling this pawn");
        Assert.Equal("human is controlling this pawn", g.Reason);
        t.Now += TimeSpan.FromSeconds(16);
        Assert.True(Offered(c, 1));
        Assert.Equal("leased", g.State);
        Assert.Null(g.Reason);

        var (c2, _, w2, t2, g2) = Rig();
        OneFailureLeft(c2, w2, g2); // a try: its reason counts against it
        Assert.True(Offered(c2));
        Assert.Equal("server undid my placement at 4,64,12 (protected area?)", g2.Reason);
    }

    /// <summary>Each pawn's miss of a build cell stayed in memory once the cell was built (the batch's end no longer clears it), and
    /// every retry and reopen walked them all.</summary>
    [Fact]
    public void A_pawns_misses_of_a_cell_end_when_it_is_built()
    {
        var (c, _, w, _, g) = Rig(pawns: 2);
        CellFails(c, Pick(c, 0), OutOfReach);
        var fails = (Dictionary<string, int>)typeof(StoryColony)
            .GetField("_pawnFails", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(c)!;
        var key = $"g:{c.Builds.Single().Id}|{Cell}|";
        Assert.Contains(fails.Keys, k => k.StartsWith(key, StringComparison.Ordinal));
        var job = Pick(c, 1);
        Assert.True(Offered(c, 1, job));
        w.Set(4, 64, 12, 5);
        c.Report(c.P(1), Id(job), "progress", $"cell {Cell} done", null);
        Assert.Equal("done", g.State);
        Assert.DoesNotContain(fails.Keys, k => k.StartsWith(key, StringComparison.Ordinal));
    }

    [Fact]
    public void A_first_cell_that_keeps_being_not_now_counts_on_the_fifth()
    {
        // The batch ends "done" every time; that used to clear its first cell's count of "not now"s, so the fifth never came.
        const string why = "couldn't get closer to 4,64,12";
        var (c, _, _, t, g) = Rig();
        for (int n = 1; n <= 4; n++)
        {
            CellFails(c, Pick(c), why);
            t.Now += TimeSpan.FromSeconds(16);
        }
        CellFails(c, Pick(c), why); // the fifth counts: the cell's first failure, a 30 s back-off
        Assert.Equal("todo", g.State);
        Assert.Equal(why, g.Reason);
        t.Now += TimeSpan.FromSeconds(16);
        Assert.False(Offered(c));
        t.Now += TimeSpan.FromSeconds(15);
        Assert.True(Offered(c));
    }

    /// <summary>The director's words for a block the server took back at <see cref="Cell"/>.</summary>
    private const string Refused = "the server refused the block at 4,64,12 (something in the cell, or a rule of that block)";

    /// <summary>The client guesses "(protected area?)" for every block the server takes back; the cell's reason and the build's said it
    /// for a chest beside a double chest and for a body in the cell too, pointing the owner at protection. The client's own text is
    /// pinned (LLM bots read it): the director says it in its own words.</summary>
    [Theory]
    [InlineData("server undid my placement at 4,64,12 (protected area?)")]
    [InlineData("stopped after 3 identical server refusals at 4,64,12; change the target/support before retrying")]
    public void A_server_refusal_is_said_in_the_directors_words(string why)
    {
        var (c, _, w, _, g) = Rig();
        OneFailureLeft(c, w, g);
        CellFails(c, Pick(c), why);
        Assert.Equal("blocked", g.State);
        Assert.Equal(Refused, g.Reason);
        Assert.Equal($"can't place at {Cell}: {Refused}", c.Builds.Single().Reason);
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("protected area"));
    }

    /// <summary>A build said "can't place at …" for its blocked cell until it turned active again (its other cells going in, after a
    /// material wait that said "need …"): then its reason was cleared while the cell stayed blocked, and the queue row said nothing.</summary>
    [Fact]
    public void A_build_keeps_saying_which_cell_it_cant_place_while_that_cell_stays_blocked()
    {
        var (c, h, w, t, g) = Rig(far: true);
        var b = c.Builds.Single();
        OneFailureLeft(c, w, g);
        CellFails(c, Pick(c), "server undid my placement at 4,64,12 (protected area?)");
        Assert.Equal("blocked", g.State);
        string said = $"can't place at {Cell}: {Refused}";
        Assert.Equal(said, b.Reason);

        // Out of planks: the build waits and says so.
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4);
        t.Now += TimeSpan.FromSeconds(16);
        Assert.NotEqual("place", Pick(c)["kind"]?.GetValue<string>());
        Assert.NotEqual("active", b.Status);
        Assert.DoesNotContain("can't place at", b.Reason ?? "");

        // Planks again: the other cell goes in, and the build says again which cell it can't place.
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: new Stack(5, 0, 64));
        var job = Pick(c);
        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.Equal("20,64,12", job["at"]!.GetValue<string>());
        Assert.Equal("active", b.Status);
        Assert.Equal(said, b.Reason);

        // The cell built (by hand): the reason goes with it.
        w.Set(4, 64, 12, 5);
        c.Reconcile(w);
        Assert.Equal("done", g.State);
        Assert.Null(b.Reason);
    }
}
