using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryHaulingWorkTests
{
    private static readonly P3 Source = new(2, 64, 0), Target = new(8, 64, 0);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Rig(int pawns = 1)
    {
        var clock = new Clock();
        var (c, h, w) = StoryRig.Make(pawns, data: Release1122GameData.Instance, time: clock);
        c.DoneGrace = TimeSpan.Zero;
        foreach (var pawn in c.Pawns.Values)
            foreach (var type in WorkTypes.All) pawn.Priorities[type] = type == WorkTypes.Haul ? 1 : 0;
        foreach (var at in new[] { Source, Target }) w.Set(at.X, at.Y, at.Z, 54);
        c.StorageOp("set", [Source], new StorageFilter { Allow = ["food"] }, "normal", null);
        c.StorageOp("set", [Target], new StorageFilter { Allow = ["stone_building"] }, "important", null);
        return (c, h, w, clock);
    }

    private static StoryEntity Drop(Clock clock, int id = 1, bool old = false) =>
        new(id, "item", "cobblestone", 5.5, 64, 3.5, ItemId: 4, Count: 3,
            FirstSeen: clock.Now - (old ? TimeSpan.FromMinutes(4) : TimeSpan.FromSeconds(20)));
    private static void Done(StoryColony c, JsonObject j) => Assert.True(c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null));

    [Fact]
    public void Worn_tools_use_a_durability_independent_transfer_name()
    {
        var (c, _, _, _) = Rig();
        c.StorageOp("set", [Target], new StorageFilter { Allow = ["*"] }, "important", null);
        c.AddContents(c.StorageAt(Source)!, new Stack(290, 34, 1));
        var job = c.Next();
        Assert.Equal("wooden_hoe", job["fetch"]![0]!["item"]!.GetValue<string>());
        Assert.Equal("wooden_hoe", job["stash"]![0]!["items"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Failed_sorting_fetch_does_not_reopen_the_same_chest_in_a_tight_loop()
    {
        var (c, h, _, clock) = Rig(2);
        c.StorageOp("set", [Target], new StorageFilter { Allow = ["*"] }, "important", null);
        c.AddContents(c.StorageAt(Source)!, new Stack(290, 0, 1));
        var job = c.Next();
        Assert.Equal("go", job["kind"]!.GetValue<string>());
        Assert.True(c.Report(c.P(), job["id"]!.GetValue<string>(), "failed",
            $"no wooden hoe in the chest at {Source} (it had: wooden hoe x1)", null));
        foreach (var p in c.Pawns.Values) Assert.Null(c.RebalanceJob(p, h.Live(p.Name)));
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.NotNull(c.RebalanceJob(c.Pawns[c.P(1)], h.Live(c.P(1))));
    }

    [Fact]
    public void Continuous_fresh_drops_share_hauling_with_organization_and_probes_do_not_consume_the_turn()
    {
        var (c, h, w, clock) = Rig();
        c.AddContents(c.StorageAt(Source)!, new Stack(4, 0, 30));
        w.Ents.Add(Drop(clock));
        for (int i = 0; i < 6; i++) Assert.Equal("drop", c.Candidate(WorkTypes.Haul, c.Pawns[c.P()], h.Live(c.P()))!.Source);
        var first = c.Next();
        Assert.Equal("collect", first["kind"]!.GetValue<string>());
        Done(c, first); // another fresh drop is still available at the same work site
        for (int i = 0; i < 6; i++) Assert.Equal("move", c.Candidate(WorkTypes.Haul, c.Pawns[c.P()], h.Live(c.P()))!.Source);
        var move = c.Next();
        Assert.Equal(Source.ToString(), move["fetch"]![0]!["from"]!.GetValue<string>());
        Assert.Equal(Target.ToString(), move["stash"]![0]!["to"]!.GetValue<string>());
        Done(c, move);
        c.AddContents(c.StorageAt(Source)!, new Stack(4, 0, 10));
        Assert.Equal("collect", c.Next()["kind"]!.GetValue<string>()); // neither stream monopolizes the pawn
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_and_endangered_pickups_still_come_before_the_organization_turn(bool manual)
    {
        var (c, _, w, clock) = Rig();
        c.AddContents(c.StorageAt(Source)!, new Stack(4, 0, 30));
        w.Ents.Add(Drop(clock));
        Done(c, c.Next());
        w.Ents.Clear();
        w.Ents.Add(Drop(clock, 2, old: !manual));
        if (manual) Assert.Null(c.Designate("add", "haul", Area.Of(4, 63, 2, 6, 65, 4), null, null).Error);
        Assert.Equal("collect", c.Next()["kind"]!.GetValue<string>());
        Assert.Equal(manual ? "designation" : "drop", c.CurrentOf(c.P())!.Source);
        Assert.Equal(30, c.StorageAt(Source)!.Contents.Single().Count);
    }

    [Fact]
    public void A_hauler_inspects_unknown_storage_and_organizes_new_player_contents_without_a_crafting_bill()
    {
        var (c, h, _, clock) = Rig();
        h.KnowsChests = true;
        var look = c.Next();
        Assert.Equal("look", look["kind"]!.GetValue<string>());
        Assert.Equal(Source.ToString(), look["at"]!.GetValue<string>());
        clock.Now += TimeSpan.FromSeconds(1);
        h.Seen[Source] = ([new Stack(4, 0, 19)], clock.Now);
        Done(c, look);
        Assert.Empty(c.Benches);
        var move = c.Next();
        Assert.Equal("go", move["kind"]!.GetValue<string>());
        Assert.Equal(19, move["fetch"]![0]!["n"]!.GetValue<int>());
        Assert.Equal(Target.ToString(), move["stash"]![0]!["to"]!.GetValue<string>());
    }

    [Fact]
    public void Inspection_is_leased_and_recent_chests_are_not_reopened_until_due()
    {
        var (c, h, w, clock) = Rig(2);
        h.KnowsChests = true;
        w.Unloaded.Add((Target.X, Target.Z));
        var look = c.Next(0);
        Assert.Equal("look", look["kind"]!.GetValue<string>());
        Assert.Null(c.Candidate(WorkTypes.Haul, c.Pawns[c.P(1)], h.Live(c.P(1))));
        clock.Now += TimeSpan.FromSeconds(1);
        h.Seen[Source] = ([], clock.Now);
        Assert.True(c.Report(c.P(0), look["id"]!.GetValue<string>(), "done", null, null));
        Assert.Null(c.Candidate(WorkTypes.Haul, c.Pawns[c.P(0)], h.Live(c.P(0))));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal("look", c.Candidate(WorkTypes.Haul, c.Pawns[c.P(0)], h.Live(c.P(0)))!.Kind);
    }

    [Fact]
    public void Accepted_stock_moves_to_higher_priority_storage_and_stays_after_arrival()
    {
        var (c, _, _, _) = Rig();
        c.StorageOp("set", [Source], new StorageFilter(), "normal", null);
        c.AddContents(c.StorageAt(Source)!, new Stack(4, 0, 21));
        var job = c.Next();
        Assert.Equal(Target.ToString(), job["stash"]![0]!["to"]!.GetValue<string>());
        Done(c, job);
        Assert.Empty(c.StorageAt(Source)!.Contents);
        Assert.Equal(21, c.StorageAt(Target)!.Contents.Single().Count);
        Assert.Null(c.RebalanceJob(c.Pawns[c.P()], c.Host.Live(c.P())));
    }

    [Fact]
    public void One_remaining_destination_slot_limits_the_trip_and_is_not_promised_to_another_pawn()
    {
        var (c, h, w, _) = Rig(2);
        var secondSource = new P3(2, 64, 3);
        w.Set(secondSource.X, secondSource.Y, secondSource.Z, 54);
        c.StorageOp("set", [secondSource], new StorageFilter { Allow = ["food"] }, null, null);
        c.StorageOp("set", [Target], new StorageFilter(), "important", null);
        c.AddContents(c.StorageAt(Target)!, new Stack(17, 0, 26 * 64));
        c.AddContents(c.StorageAt(Source)!, new Stack(4, 0, 200));
        c.AddContents(c.StorageAt(Source)!, new Stack(3, 0, 64));
        c.AddContents(c.StorageAt(secondSource)!, new Stack(4, 0, 20));
        Assert.Equal(1, c.StorageAt(Target)!.FreeSlots);
        var first = c.Next(0);
        var fetch = Assert.Single(first["fetch"]!.AsArray());
        Assert.Equal(64, fetch!["n"]!.GetValue<int>());
        Assert.Null(c.Candidate(WorkTypes.Haul, c.Pawns[c.P(1)], h.Live(c.P(1))));
        Assert.Equal(200, c.StorageAt(Source)!.Contents.Single(s => s.Id == 4).Count); // no speculative debit
    }

    [Fact]
    public void A_full_pawn_does_not_accept_a_sorting_withdrawal()
    {
        var (c, h, _, _) = Rig();
        c.AddContents(c.StorageAt(Source)!, new Stack(4, 0, 20));
        h.Lives[c.P()] = h.Live(c.P()) with { FreeSlots = 0 };
        Assert.Null(c.Candidate(WorkTypes.Haul, c.Pawns[c.P()], h.Live(c.P())));
        Assert.Equal(20, c.StorageAt(Source)!.Contents.Single().Count);
    }

    [Theory]
    [InlineData(17, 0, 1)]
    [InlineData(1, 0, 3)]
    [InlineData(35, 0, 14)]
    public void Metadata_specific_filters_use_exact_fetch_and_deposit_selectors(int id, int firstMeta, int secondMeta)
    {
        var (c, _, w, _) = Rig();
        var other = new P3(10, 64, 0);
        w.Set(other.X, other.Y, other.Z, 54);
        c.StorageOp("set", [Target], new StorageFilter { Allow = [$"{id}:{firstMeta}"] }, null, null);
        c.StorageOp("set", [other], new StorageFilter { Allow = [$"{id}:{secondMeta}"] }, null, null);
        c.AddContents(c.StorageAt(Source)!, new Stack(id, firstMeta, 12));
        c.AddContents(c.StorageAt(Source)!, new Stack(id, secondMeta, 24));
        var job = c.Next();
        (int Id, int Meta) Resolve(string name)
        {
            var parts = name.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var item)) return (item, int.Parse(parts[1]));
            var found = w.Data.ItemByNameWithDamage(name)!.Value;
            return (found.Item.Id, found.Damage);
        }
        Assert.Equal([(id, firstMeta), (id, secondMeta)], job["fetch"]!.AsArray().Select(f => Resolve(f!["item"]!.GetValue<string>())));
        var destinations = job["stash"]!.AsArray().ToDictionary(s => s!["to"]!.GetValue<string>(), s => Resolve(s!["items"]![0]!.GetValue<string>()));
        Assert.Equal((id, firstMeta), destinations[Target.ToString()]);
        Assert.Equal((id, secondMeta), destinations[other.ToString()]);
    }
}
