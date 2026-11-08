using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryChestDeconstructionTests
{
    private static readonly P3 Source = new(3, 64, 2), Destination = new(8, 64, 2);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T05:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Rig(bool destination = true, bool registered = true)
    {
        var clock = new Clock();
        var (c, h, w) = StoryRig.Make(time: clock);
        foreach (var type in WorkTypes.All) c.Pawns.Values.Single().Priorities[type] = type == WorkTypes.Build ? 1 : 0;
        h.KnowsChests = true;
        w.Set(Source.X, Source.Y, Source.Z, 54);
        if (registered) c.StorageOp("set", [Source], null, null, null);
        if (destination)
        {
            w.Set(Destination.X, Destination.Y, Destination.Z, 54);
            c.StorageOp("set", [Destination], null, null, null);
        }
        return (c, h, w, clock);
    }

    private static DesignationState Mark(StoryColony c, P3? at = null)
    {
        Assert.Null(c.Designate("add", "deconstruct", null, [at ?? Source], "all").Error);
        return c.Designations.Single(d => d.At == (at ?? Source));
    }

    private static void See(StoryColony c, FakeStoryHost h, Clock t, params Stack[] contents)
    {
        t.Now += TimeSpan.FromSeconds(1);
        h.Seen[Source] = (contents, t.Now);
        if (c.StorageAt(Source) is { } storage) c.RefreshSeen([storage], force: true);
    }

    [Fact]
    public void Unknown_chest_is_inspected_then_emptied_before_the_explicit_empty_only_dig()
    {
        var (c, h, _, t) = Rig(registered: false);
        Mark(c);
        var look = c.Next();
        Assert.Equal("look", look["kind"]!.GetValue<string>());
        See(c, h, t, new Stack(3, 0, 12));
        Assert.True(c.Report(c.P(), look["id"]!.GetValue<string>(), "done", null, null));
        var move = c.Next();
        Assert.Equal("go", move["kind"]!.GetValue<string>());
        Assert.Equal(Source.ToString(), move["fetch"]![0]!["from"]!.GetValue<string>());
        Assert.Equal(Destination.ToString(), move["stash"]![0]!["to"]!.GetValue<string>());
        Assert.Null(move["drop"]);
        Assert.True(c.Report(c.P(), move["id"]!.GetValue<string>(), "done", null, null));
        Assert.Empty(c.StorageAt(Source)!.Contents);
        Assert.Equal(12, c.StorageAt(Destination)!.Contents.Single().Count);
        var dig = c.Next();
        Assert.Equal("dig", dig["kind"]!.GetValue<string>());
        Assert.True(dig["deconstruct"]!.GetValue<bool>());
    }

    [Fact]
    public void A_marked_double_chest_is_not_a_destination_and_cancel_stops_its_transfer()
    {
        var (c, h, w, t) = Rig();
        var half = Source.Add(1, 0, 0);
        w.Set(half.X, half.Y, half.Z, 54);
        var mark = Mark(c, half);
        See(c, h, t, new Stack(3, 0, 12));
        Assert.Single(c.Storages.Where(s => s.Chest == Source || s.Chest == half));
        Assert.Equal(Destination, c.BestChest(3, 0, Source)!.Chest);
        var move = c.Next();
        Assert.Equal("go", move["kind"]!.GetValue<string>());
        Assert.Null(c.Designate("cancel", "deconstruct", null, null, null, [mark.Id]).Error);
        Assert.Null(c.CurrentOf(c.P()));
        Assert.Contains(h.Interrupted, s => s.StartsWith(c.P() + ":"));
        Assert.Equal(Source, c.BestChest(3, 0, Source)!.Chest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_destination_or_full_destination_waits_without_breaking_or_dumping(bool full)
    {
        var (c, h, _, t) = Rig(destination: full);
        if (full) c.AddContents(c.StorageAt(Destination)!, new Stack(4, 0, 27 * 64));
        var mark = Mark(c);
        See(c, h, t, new Stack(3, 0, 12));
        var job = c.Next();
        Assert.NotEqual("dig", job["kind"]!.GetValue<string>());
        Assert.Null(job["drop"]);
        Assert.Contains("another chest", mark.Wait);
        Assert.Equal(12, c.StorageAt(Source)!.Contents.Single().Count);
    }

    [Fact]
    public void Explicit_chest_removal_retires_only_its_older_matching_ghost()
    {
        var (c, _, w, t) = Rig();
        var other = new P3(12, 64, 2);
        w.Set(other.X, other.Y, other.Z, 54);
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "block", ["material"] = "chest",
            ["blocks"] = new JsonArray(Source.Json(), other.Json()) }), null).Error);
        var old = c.Builds.Single();
        t.Now += TimeSpan.FromSeconds(1);
        Mark(c);
        Assert.Single(old.Ghosts);
        Assert.Equal(other, old.Ghosts[0].At);
        w.Set(Source.X, Source.Y, Source.Z, 0);
        c.Reconcile(w);
        Assert.DoesNotContain(c.Builds.SelectMany(b => b.Ghosts), g => g.At == Source);
        Assert.Contains(c.Builds.SelectMany(b => b.Ghosts), g => g.At == other);
    }

    [Fact]
    public void A_fresh_contents_race_waits_for_a_new_look_without_failing_the_designation()
    {
        var (c, h, _, t) = Rig();
        var mark = Mark(c);
        See(c, h, t);
        var dig = c.Next();
        Assert.True(dig["deconstruct"]!.GetValue<bool>());
        Assert.True(c.Report(c.P(), dig["id"]!.GetValue<string>(), "failed", "chest deconstruction waiting: contents arrived", null));
        Assert.Equal(0, mark.Fails);
        Assert.Equal("waiting", mark.Status);
        Assert.Null(c.StorageAt(Source)!.SeenAt);
        t.Now += TimeSpan.FromSeconds(3);
        Assert.Equal("look", c.Next()["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Evacuation_respects_the_destinations_last_partial_stack_space()
    {
        var (c, h, _, t) = Rig();
        c.AddContents(c.StorageAt(Destination)!, new Stack(4, 0, 26 * 64));
        c.AddContents(c.StorageAt(Destination)!, new Stack(3, 0, 60));
        Mark(c);
        See(c, h, t, new Stack(3, 0, 64));
        var move = c.Next();
        Assert.Equal("go", move["kind"]!.GetValue<string>());
        Assert.Equal(4, move["fetch"]![0]!["n"]!.GetValue<int>());
    }

    [Fact]
    public void Ordinary_hauling_cannot_dump_a_marked_chests_disallowed_contents()
    {
        var (c, h, _, t) = Rig(destination: false);
        c.Pawns.Values.Single().Priorities[WorkTypes.Haul] = 1;
        c.SetHaulOptions(null, false, false);
        c.StorageOp("set", [Source], new StorageFilter { Allow = ["food"] }, null, null);
        Mark(c);
        See(c, h, t, new Stack(4, 0, 12));
        var job = c.Next();
        Assert.NotEqual("dump", job["kind"]!.GetValue<string>());
        Assert.Null(job["drop"]);
        Assert.Equal(12, c.StorageAt(Source)!.Contents.Single().Count);
    }

    [Fact]
    public void Waiting_for_space_at_the_nearest_chest_does_not_block_another_empty_chest()
    {
        var (c, h, _, t) = Rig();
        var waiting = Mark(c);
        Mark(c, Destination);
        See(c, h, t, new Stack(3, 0, 12));
        h.Seen[Destination] = (Array.Empty<Stack>(), t.Now);
        c.RefreshSeen([c.StorageAt(Destination)!], force: true);
        var first = c.Next();
        Assert.NotEqual("dig", first["kind"]!.GetValue<string>());
        c.Report(c.P(), first["id"]!.GetValue<string>(), "done", null, null);
        var next = c.Next();
        Assert.Equal("dig", next["kind"]!.GetValue<string>());
        Assert.Equal(Destination.ToString(), next["at"]!.GetValue<string>());
        Assert.Equal(0, waiting.Fails);
        Assert.Equal("waiting", waiting.Status);
    }
}
