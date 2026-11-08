using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryPreservedCleanupTests
{
    private static readonly P3 Foundation = new(5, 62, 5), Stray = new(12, 64, 12);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, ManualTime T) Rig(bool modern)
    {
        var time = new ManualTime();
        var (c, h, w) = StoryRig.Make(1, modern ? Release1122GameData.Instance : Beta181GameData.Instance, time);
        foreach (var work in WorkTypes.All) c.Pawns.Values.Single().Priorities[work] = work == WorkTypes.Build ? 1 : 0;
        w.Set(Foundation.X, Foundation.Y, Foundation.Z, 3);
        w.Set(Foundation.X, 63, Foundation.Z, 60, 7);
        w.Set(4, 62, 5, 0);
        w.Set(4, 63, 5, 0); // exposed farm edge, accessible from beside its foundation
        w.Set(Stray.X, Stray.Y, Stray.Z, 4);
        return (c, h, w, time);
    }

    private static void PreserveFarm(StoryColony c)
    {
        var added = c.ZoneOp("add", "farm", Area.Of(5, 63, 5, 5, 63, 5), null,
            grow: new JsonObject { ["plant"] = "wheat" });
        Assert.Null(added.Error);
        Assert.Null(c.ZoneOp("preserve", null, null, added.Result!["id"]!.GetValue<string>(),
            grow: new JsonObject { ["below"] = 1 }).Error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Preserved_farm_foundations_are_not_adopted_or_swept_but_real_strays_still_are(bool modern, bool bodyLedger)
    {
        var (c, h, _, time) = Rig(modern);
        var pawn = c.P();
        PreserveFarm(c);
        h.Placed.Add((Foundation, 3, pawn));
        h.Placed.Add((Stray, 4, pawn));
        if (bodyLedger) h.Temps[pawn] = [(Foundation, 3), (Stray, 4)];
        h.Lives[pawn] = PawnLive.Offline;
        time.Now += TimeSpan.FromSeconds(50);
        c.Tick();
        Assert.DoesNotContain(c.Designations, d => d.At == Foundation);
        Assert.DoesNotContain(c.Meta.TempBlocks ?? [], t => t.At == Foundation);
        Assert.Contains(c.Designations, d => d.At == Stray && d.Auto);
        if (bodyLedger) Assert.DoesNotContain(h.Temps[pawn], t => t.At == Foundation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_cleanup_of_a_preserved_foundation_is_cancelled_and_only_its_automatic_mark_is_forgotten(bool modern)
    {
        var (c, h, w, time) = Rig(modern);
        var pawn = c.P();
        PreserveFarm(c);
        c.Tick();
        // Persisted state from the old stray sweep: the preserve snapshot predates the mistaken cleanup.
        h.Temps[pawn] = [(Foundation, 3)];
        h.Placed.Add((Foundation, 3, pawn));
        c.Temporary(pawn, Foundation.ToString(), true, "stray");
        Assert.Null(c.Designate("add", "mine", null, [Foundation], "all").Error);
        var cleanup = Assert.Single(c.Designations, d => d.At == Foundation);
        cleanup.Kind = "deconstruct"; // the legacy stray registry bypassed the manual deconstruct material filter
        cleanup.Auto = true;
        Assert.Single(c.Meta.TempBlocks!).Designation = cleanup.Id;
        h.Lives[pawn] = FakeStoryHost.Live(4, 62, 5);
        var job = c.Next();
        Assert.Equal("dig", job["kind"]!.GetValue<string>());
        Assert.Equal(Foundation.ToString(), job["at"]!.GetValue<string>());

        time.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        Assert.Null(c.CurrentOf(pawn));
        Assert.Contains(h.Interrupted, text => text.Contains("temporary cleanup is now preserved"));
        Assert.False(c.Report(pawn, job["id"]!.GetValue<string>(), "done", null, null));
        Assert.DoesNotContain(c.Designations, d => d.Id == cleanup.Id);
        Assert.DoesNotContain(c.Meta.TempBlocks ?? [], t => t.At == Foundation);
        Assert.Empty(h.Temps[pawn]);
        Assert.Empty(c.Leases());
        Assert.Equal(3, w.Block(Foundation.X, Foundation.Y, Foundation.Z).Id);
        time.Now += TimeSpan.FromMinutes(11);
        c.Tick();
        Assert.DoesNotContain(c.Designations, d => d.At == Foundation);
    }

    [Fact]
    public void A_manual_mark_reused_by_the_temp_registry_remains_when_the_foundation_is_preserved()
    {
        var (c, h, _, time) = Rig(modern: true);
        var pawn = c.P();
        PreserveFarm(c);
        c.Tick();
        c.Designate("add", "mine", null, [Foundation], "all");
        var manual = Assert.Single(c.Designations, d => d.At == Foundation);
        Assert.False(manual.Auto);
        c.Temporary(pawn, Foundation.ToString(), true, "stray");
        Assert.Single(c.Meta.TempBlocks!).Designation = manual.Id;

        time.Now += TimeSpan.FromSeconds(4);
        c.Tick();
        Assert.Same(manual, Assert.Single(c.Designations, d => d.At == Foundation));
        Assert.False(manual.Auto);
        Assert.DoesNotContain(c.Meta.TempBlocks ?? [], t => t.At == Foundation);
        time.Now += TimeSpan.FromSeconds(2);
        c.Tick();
        Assert.Contains(manual, c.Designations);
    }

    [Fact]
    public void A_real_temporary_block_recorded_as_air_by_a_new_preserve_snapshot_still_gets_cleaned()
    {
        var (c, h, _, time) = Rig(modern: true);
        var pawn = c.P();
        c.Temporary(pawn, Foundation.ToString(), true, "scaffold");
        PreserveFarm(c); // registered access is intentionally captured as air, never as permanent ground
        h.Lives[pawn] = PawnLive.Offline;
        time.Now += TimeSpan.FromSeconds(50);
        c.Tick();
        Assert.Contains(c.Designations, d => d.At == Foundation && d.Auto);
        Assert.Contains(c.Meta.TempBlocks ?? [], t => t.At == Foundation);
    }
}
