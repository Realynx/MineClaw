using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>Fox 11-03: "+ and − buttons to add or remove pawns in Story Mode." (docs/web/story-mode.md "Adding and removing pawns").</summary>
public sealed class StoryRosterTests
{
    [Fact]
    public void Adding_pawns_spawns_what_the_seats_allow_and_the_rest_wait_for_a_seat()
    {
        var (c, h, _) = StoryRig.Make(pawns: 2);
        var colonies = new StoryColonies(h);
        var (res, err) = colonies.AddPawns(c, 2);
        Assert.Null(err);
        Assert.Equal(4, c.Pawns.Count);
        Assert.Equal(4, c.Meta.Wanted);
        Assert.Equal(4, res!["pawns"]!.AsArray().Count);
        Assert.Empty(res["pending"]!.AsArray());

        // Only one seat free beyond the regulars': one comes now, two wait (never the regulars' seats).
        h.SeatsNow = (3, 2);
        (res, err) = colonies.AddPawns(c, 3);
        Assert.Null(err);
        Assert.Equal(5, c.Pawns.Count);
        Assert.Equal(7, c.Meta.Wanted);
        Assert.Equal(2, Assert.Single(res!["pending"]!.AsArray())!["count"]!.GetValue<int>());
        Assert.Contains("server full", res["blocked"]![0]!["reason"]!.GetValue<string>());
        Assert.Equal(2, c.State()["seats"]!["pending"]!.GetValue<int>());

        // No upper limit (Fox 11-05): more than the old 12 is fine, they wait for seats like the rest.
        (res, err) = colonies.AddPawns(c, 6);
        Assert.Null(err);
        Assert.Equal(13, res!["wanted"]!.GetValue<int>());
        Assert.NotNull(colonies.AddPawns(c, 0).Error);
        c.Meta.Active = false;
        Assert.Contains("released", colonies.AddPawns(c, 1).Error);
    }

    [Fact]
    public void A_removed_pawn_gives_back_its_claims_and_bed_stores_its_things_then_leaves()
    {
        var (c, h, w) = StoryRig.Make(pawns: 2);
        var colonies = new StoryColonies(h);
        var gone = c.P(1);
        w.Tree(6, 0);
        c.Designate("add", "chop", Area.Of(0, 60, -2, 10, 80, 2), null, null);
        foreach (var t in WorkTypes.All) c.Pawns[c.P(0)].Priorities[t] = 0;
        Assert.Equal("chop", c.Next(1)["kind"]!.GetValue<string>());
        Assert.Contains(c.Leases(), l => l.Value == gone);
        w.Set(3, 64, 3, 26, 0); w.Set(3, 64, 4, 26, 8);
        c.BedsOp("assign", new P3(3, 64, 3), gone);
        c.StorageOp("set", [new P3(0, 64, 4)], null, null, null);
        h.Lives[gone] = FakeStoryHost.Live(0, 64, 0, inv: new Stack(17, 0, 12));

        var (res, err, _) = colonies.RemovePawn(c, gone);
        Assert.Null(err);
        Assert.Equal(1, c.Meta.Wanted);
        Assert.Equal(gone, res!["leaving"]![0]!.GetValue<string>());
        Assert.DoesNotContain(c.Leases(), l => l.Value == gone);           // the tree is free for the others
        Assert.Empty(c.Meta.Beds);                                          // its bed too
        Assert.Contains(h.Interrupted, i => i.StartsWith(gone));
        Assert.True(c.State()["pawns"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == gone)!["leaving"]!.GetValue<bool>());

        // It stores its logs, then its script ends, then it is released and gone.
        var store = c.Next(1);
        Assert.Equal("0,64,4", store["stash"]![0]!["to"]!.GetValue<string>());
        h.Lives[gone] = FakeStoryHost.Live(0, 64, 3);
        c.Report(gone, store["id"]!.GetValue<string>(), "done", null, null);
        var end = c.Next(1);
        Assert.Equal("end", end["kind"]!.GetValue<string>());
        c.Report(gone, end["id"]!.GetValue<string>(), "done", null, null);
        c.Tick();
        Assert.Equal([gone], h.ReleasedPawns);
        Assert.False(c.Pawns.ContainsKey(gone));
        Assert.Contains(c.Alerts, a => a.Text.Contains("left the colony"));
        foreach (var t in WorkTypes.All) c.Pawns[c.P(0)].Priorities[t] = 3;
        Assert.Equal("chop", c.Next(0)["kind"]!.GetValue<string>()); // the tree it gave back

        // The last one stays: release the colony instead.
        var (_, last, notFound) = colonies.RemovePawn(c, c.P(0));
        Assert.Contains("last pawn", last);
        Assert.False(notFound);
        Assert.True(colonies.RemovePawn(c, "nobody").NotFound);
    }

    [Fact]
    public void An_offline_pawn_leaves_at_once()
    {
        var (c, h, _) = StoryRig.Make(pawns: 3);
        var colonies = new StoryColonies(h);
        var p = c.P(2);
        h.Lives[p] = PawnLive.Offline;
        Assert.Null(colonies.RemovePawn(c, p).Error);
        c.Tick();
        Assert.Equal([p], h.ReleasedPawns);
        Assert.Equal(2, c.Pawns.Count);
        Assert.Equal(2, c.Meta.Wanted);
    }
}
