using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryCraftFallbackTests
{
    private static readonly P3 Storage = new(0, 64, 9), Table = new(3, 64, 0), Target = new(0, 64, 5);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StoryColony Colony, Clock Time, BillState Bucket) Rig(params Stack[] stock)
    {
        var clock = new Clock();
        var (c, h, w) = StoryRig.Make(data: Release1122GameData.Instance, time: clock);
        c.Meta.Gear.AutoCraftTools = false;
        var pawn = c.Pawns.Values.Single();
        foreach (var work in WorkTypes.All) pawn.Priorities[work] = 0;
        pawn.Priorities[WorkTypes.Build] = 1;
        pawn.Priorities[WorkTypes.Craft] = 3;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0);
        w.Set(Storage.X, Storage.Y, Storage.Z, 54);
        c.StorageOp("set", [Storage], null, null, null);
        foreach (var stack in stock) c.AddContents(c.StorageAt(Storage)!, stack);
        w.Set(Table.X, Table.Y, Table.Z, 58);
        Assert.Null(c.WorkbenchOp("designate", Table).Error);
        var bucket = new BillState
        {
            Id = "lava-bucket", Recipe = "craft:bucket", Mode = "count", Target = 1,
            ForBuild = StoryColony.NeedTag, ForItem = "bucket", Reason = "collect lava from the quarry",
        };
        c.Benches.Single().Tasks.Add(bucket);
        return (c, clock, bucket);
    }

    private static string ChestBuild(StoryColony c)
    {
        // A chest within a design's material group, rather than a standalone urgent Architect chest.
        var result = c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["material"] = "chest", ["blocks"] = new JsonArray(Target.Json()),
        }), null);
        Assert.Null(result.Error);
        return result.Result!["ids"]![0]!.GetValue<string>();
    }

    private static void AssertBucketWaiting(StoryColony c)
    {
        var task = c.State()["craft_tasks"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == "lava-bucket")!;
        Assert.Equal("waiting", task["status"]!.GetValue<string>());
        Assert.Contains("iron ingot", task["blocked"]!.GetValue<string>());
    }

    [Fact]
    public void A_bucket_missing_iron_does_not_hide_a_supported_chest_placement()
    {
        var (c, _, _) = Rig(new Stack(54, 0, 1));
        var build = ChestBuild(c);

        var job = c.Next();

        Assert.Equal("place", job["kind"]!.GetValue<string>());
        Assert.Equal(Target.ToString(), job["at"]!.GetValue<string>());
        Assert.Equal("chest", job["block"]!.GetValue<string>());
        Assert.StartsWith(build + "|", c.CurrentOf(c.P())!.SourceId);
        Assert.Equal(Storage.ToString(), job["fetch"]![0]!["from"]!.GetValue<string>());
        AssertBucketWaiting(c);
    }

    [Fact]
    public void A_bucket_missing_iron_does_not_hide_a_chest_that_can_be_crafted_and_then_placed()
    {
        var (c, _, _) = Rig(new Stack(5, 0, 8));
        ChestBuild(c);

        var craft = c.Next();
        Assert.Equal("craft", craft["kind"]!.GetValue<string>());
        Assert.Equal("craft:chest", craft["recipe"]!.GetValue<string>());
        AssertBucketWaiting(c);
        Assert.True(c.Report(c.P(), craft["id"]!.GetValue<string>(), "done", null, null));

        var place = c.Next();
        Assert.Equal("place", place["kind"]!.GetValue<string>());
        Assert.Equal("chest", place["block"]!.GetValue<string>());
    }

    [Fact]
    public void An_idle_builder_reports_its_higher_priority_construction_shortage_before_the_bucket()
    {
        var (c, clock, _) = Rig();
        var build = ChestBuild(c);

        var job = c.Next();
        Assert.Equal("idle", job["type"]!.GetValue<string>());
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();

        Assert.StartsWith("waiting: build " + build + ":", c.IdleReasonOf(c.P()));
        Assert.Contains("chest", c.IdleReasonOf(c.P()));
        AssertBucketWaiting(c);
    }

    [Fact]
    public void An_idle_crafter_still_reports_the_missing_bucket_ingredient()
    {
        var (c, clock, _) = Rig();
        c.Pawns.Values.Single().Priorities[WorkTypes.Build] = 0;

        var job = c.Next();
        Assert.Equal("idle", job["type"]!.GetValue<string>());
        clock.Now += TimeSpan.FromSeconds(2);
        c.Tick();

        Assert.StartsWith("waiting: Make Bucket", c.IdleReasonOf(c.P()));
        Assert.Contains("iron ingot", c.IdleReasonOf(c.P()));
        AssertBucketWaiting(c);
    }

    [Fact]
    public void A_runnable_urgent_bucket_keeps_precedence_over_supported_building()
    {
        var (c, _, bucket) = Rig(new Stack(54, 0, 1), new Stack(265, 0, 3));
        ChestBuild(c);

        var job = c.Next();

        Assert.Equal("craft", job["kind"]!.GetValue<string>());
        Assert.Equal("craft:bucket", job["recipe"]!.GetValue<string>());
        Assert.Equal(bucket.Id, c.CurrentOf(c.P())!.SourceId);
        Assert.Equal("todo", c.Builds.Single().Ghosts.Single().State);
    }
}
