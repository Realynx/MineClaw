using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryMissingStorageTests
{
    private static readonly P3 Chest = new(3, 64, 2);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_removed_chest_loses_its_stock_and_capacity_and_old_memory_cannot_restore_them()
    {
        var clock = new Clock();
        var (c, h, w) = StoryRig.Make(time: clock);
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        c.StorageOp("set", [Chest], null, null, null);
        var st = c.StorageAt(Chest)!;
        st.Label = "Tools";
        c.AddContents(st, new Stack(270, 0, 1));
        h.Seen[Chest] = ([new Stack(270, 0, 1)], clock.Now);
        clock.Now += TimeSpan.FromSeconds(10);
        w.Set(Chest.X, Chest.Y, Chest.Z, 0);
        Assert.True(c.RefreshSeen([st], force: true));
        Assert.True(st.Missing);
        Assert.Empty(st.Contents);
        Assert.Equal(0, st.FreeSlots);
        Assert.Null(c.FindStored(s => s.Id == 270, Chest));
        Assert.Null(c.BestChest(3, 0, Chest));
        Assert.False(c.RefreshSeen([st], force: true));

        // Rebuilding restores the settings, but only a fresh look supplies the inventory.
        clock.Now += TimeSpan.FromSeconds(10);
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        Assert.True(c.RefreshSeen([st], force: true));
        Assert.False(st.Missing);
        Assert.Equal("Tools", st.Label);
        Assert.Equal(27, st.FreeSlots);
        Assert.Empty(st.Contents);
        Assert.Null(st.SeenAt);
        Assert.False(c.RefreshSeen([st], force: true));
        Assert.Null(st.SeenAt);
        clock.Now += TimeSpan.FromSeconds(1);
        h.Seen[Chest] = ([new Stack(5, 0, 8)], clock.Now);
        Assert.True(c.RefreshSeen([st], force: true));
        Assert.Equal(8, Assert.Single(st.Contents).Count);
    }

    [Fact]
    public void A_surviving_half_becomes_the_storage_address_and_its_combined_stock_is_rechecked()
    {
        var (c, h, w) = StoryRig.Make();
        var other = Chest.Add(1, 0, 0);
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        w.Set(other.X, other.Y, other.Z, 54);
        c.StorageOp("set", [Chest], null, null, null);
        var st = c.StorageAt(Chest)!;
        c.AddContents(st, new Stack(5, 0, 128));
        var before = c.Rev;
        w.Set(Chest.X, Chest.Y, Chest.Z, 0);
        Assert.True(c.RefreshSeen([st], force: true));
        Assert.Equal(other, st.Chest);
        Assert.Null(st.DoubleWith);
        Assert.False(st.Missing);
        Assert.Empty(st.Contents);
        Assert.Equal(27, st.Slots);
        Assert.Same(st, c.BestChest(3, 0, Chest));
        Assert.Contains(Chest.ToString(), c.State(before)["removed"]!.ToJsonString());
    }

    [Fact]
    public void An_unloaded_chest_keeps_its_inventory()
    {
        var (c, h, w) = StoryRig.Make();
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        c.StorageOp("set", [Chest], null, null, null);
        var st = c.StorageAt(Chest)!;
        c.AddContents(st, new Stack(5, 0, 8));
        w.Unloaded.Add((Chest.X, Chest.Z));
        Assert.False(c.RefreshSeen([st], force: true));
        Assert.False(st.Missing);
        Assert.Single(st.Contents);
    }

    [Fact]
    public void A_failed_fetch_from_a_missing_chest_does_not_fail_the_mining_target()
    {
        var (c, h, w) = StoryRig.Make();
        foreach (var t in WorkTypes.All) c.Pawns.Values.Single().Priorities[t] = t == WorkTypes.Mine ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0);
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        c.StorageOp("set", [Chest], null, null, null);
        c.AddContents(c.StorageAt(Chest)!, new Stack(270, 0, 1));
        c.Designate("add", "mine", null, [new P3(5, 63, 5)], null);
        var job = c.Next();
        Assert.NotNull(job["fetch"]);
        // The body's explicit observation wins even before the merged world receives the change.
        Assert.True(c.Report(c.P(), job["id"]!.GetValue<string>(), "failed", $"air at {Chest} is not a container", null));
        Assert.True(c.StorageAt(Chest)!.Missing);
        Assert.Equal(0, Assert.Single(c.Designations).Fails);
        Assert.True(StoryColony.PlanningError($"air at {Chest} is not a container"));
        Assert.Null(c.Next()["fetch"]);
    }
}
