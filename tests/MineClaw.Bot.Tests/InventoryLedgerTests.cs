using MineClaw.Bot.Ledger;
using MineClaw.Data;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The 10-27 "12 cobblestone slabs are no longer in inventory and aren't in any known chest": the slabs were tossed to
/// PoofimaFox at 07:34:16 (he picked up 12 two seconds later) while the bot's model kept listing them until a chest
/// window's 0x68 replaced it at 07:41. Nothing may leave the inventory without a ledger entry.
/// </summary>
public class InventoryLedgerTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 27, 7, 34, 16, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private const int Slab = Blocks.Slab, Cobble = Blocks.Cobblestone;
    private static Dictionary<(int, int), int> Inv(params (int Id, int Damage, int Count)[] items) =>
        items.Where(i => i.Count > 0).ToDictionary(i => (i.Id, i.Damage), i => i.Count);

    private static (InventoryLedger L, Clock C) New(params (int Id, int Damage, int Count)[] start)
    {
        var c = new Clock();
        var l = new InventoryLedger(c);
        l.Observe(Inv(start));
        return (l, c);
    }

    [Fact]
    public void A_toss_is_recorded_with_who_picked_it_up_and_answers_where_the_slabs_went()
    {
        var (l, c) = New((Cobble, 0, 3), (Slab, 3, 12));
        using (l.Scope("tossed to PoofimaFox"))
        {
            l.Observe(Inv((Cobble, 0, 3)));
        }
        c.Advance(2);
        l.NoteOtherPickup("PoofimaFox", Slab, 12);
        var e = Assert.Single(l.History("slabs"));
        Assert.Equal(-12, e.Delta);
        Assert.Equal("tossed to PoofimaFox", e.Cause);
        Assert.Contains("PoofimaFox picked up 12", e.Detail);
        var why = l.Explain("cobble slabs", haveNow: 0)!;
        Assert.StartsWith("Cobblestone Slab: have 0 now.", why);
        Assert.Contains("-12 Cobblestone Slab: tossed to PoofimaFox (PoofimaFox picked up 12", why);
    }

    [Fact]
    public void A_deposit_inside_the_gen_loop_is_booked_as_deposited_into_that_chest_and_summarised_with_the_haul()
    {
        // Live 10-27: "-64 Cobblestone: harvest_generator -62/-63/-64" while the gen loop stored its haul in 14,64,-5.
        var (l, c) = New((Cobble, 0, 0));
        var t0 = c.Now;
        string? action = "harvest";
        l.CurrentAction = () => action;
        l.CurrentContainer = () => "chest 14,64,-5";
        using (l.Scope("harvest_generator"))
        {
            l.NotePickup(Cobble, 64);
            l.Observe(Inv((Cobble, 0, 64)));
            c.Advance(1);
            l.Flush();
            action = "deposit";
            l.Observe(Inv((Cobble, 0, 0)));
            action = null;
        }
        c.Advance(1);
        l.Flush();
        var e = l.History("cobble");
        Assert.Contains(e, x => x.Delta == 64 && x.Cause == "picked up (harvest_generator)");
        Assert.Contains(e, x => x.Delta == -64 && x.Cause == "deposited (chest 14,64,-5)");
        Assert.DoesNotContain(e, x => x.Delta < 0 && x.Cause == "harvest_generator");
        Assert.Equal("64 Cobblestone; stored 64 Cobblestone in chest 14,64,-5", l.GainsSince(t0));
    }

    [Fact]
    public void Ghost_stacks_removed_by_a_server_window_resync_are_a_correction_not_a_mystery()
    {
        var (l, c) = New((Slab, 3, 12), (Blocks.Log, 0, 11));
        c.Advance(400);
        l.NoteResync(); // a chest opened: 0x68 replaces the model
        l.Observe(Inv((Blocks.Log, 0, 11)));
        var e = Assert.Single(l.History("slab"));
        Assert.Equal(-12, e.Delta);
        Assert.StartsWith("server correction", e.Cause);
    }

    [Fact]
    public void A_bucket_that_fills_or_empties_in_a_1_12_2_resync_is_that_use_not_a_correction()
    {
        // 1.12.2 answers an item use with the whole inventory (sendContainerToPlayer): live 11-04, "server correction" for every pour.
        var (l, c) = New((Items.Bucket, 0, 2), (Cobble, 0, 5));
        c.Advance(1);
        l.NoteResync();
        l.Observe(Inv((Items.Bucket, 0, 1), (Items.WaterBucket, 0, 1), (Cobble, 0, 5)));
        c.Advance(1);
        l.NoteResync();
        l.Observe(Inv((Items.Bucket, 0, 2), (Cobble, 0, 5)));
        c.Advance(1);
        l.Flush();
        Assert.All(l.History("bucket").Concat(l.History("water_bucket")), e => Assert.DoesNotContain("correction", e.Cause));
        // Anything else in the same resync still is one.
        l.NoteResync();
        l.Observe(Inv((Items.Bucket, 0, 2)));
        c.Advance(1);
        Assert.StartsWith("server correction", Assert.Single(l.History("cobblestone")).Cause);
    }

    [Fact]
    public void Pickups_crafting_death_and_unexplained_losses_all_get_a_cause()
    {
        var (l, c) = New((Cobble, 0, 15));
        string? action = null;
        l.CurrentAction = () => action;

        l.NotePickup(Blocks.Log, 1);
        l.Observe(Inv((Cobble, 0, 15), (Blocks.Log, 0, 1)));
        c.Advance(1);

        action = "craft";
        l.Observe(Inv((Cobble, 0, 3), (Blocks.Log, 0, 1), (Slab, 3, 12)));
        action = null;
        c.Advance(20);

        l.Observe(Inv((Blocks.Log, 0, 1), (Slab, 3, 12))); // nothing running, no resync: still recorded, never silent
        c.Advance(20);

        l.NoteDeath("19,65,3");
        l.Observe(Inv());

        var all = l.Entries;
        Assert.Contains(all, e => e.ItemId == Blocks.Log && e.Delta == 1 && e.Cause == "picked up");
        Assert.Contains(all, e => e.ItemId == Cobble && e.Delta == -12 && e.Cause == "used in crafting");
        Assert.Contains(all, e => e.ItemId == Slab && e.Delta == 12 && e.Cause == "crafted");
        Assert.Contains(all, e => e.ItemId == Cobble && e.Delta == -3 && e.Cause.StartsWith("vanished without an action"));
        Assert.Contains(all, e => e.ItemId == Slab && e.Delta == -12 && e.Cause.StartsWith("lost when I died at 19,65,3"));
        Assert.Contains(all, e => e.ItemId == Blocks.Log && e.Delta == -1 && e.Cause.StartsWith("lost when I died"));
    }

    [Fact]
    public void Repeated_changes_of_one_cause_merge_into_one_entry()
    {
        var (l, c) = New((Blocks.Log, 0, 0));
        using (l.Scope("chop_tree"))
            for (int n = 1; n <= 5; n++) { l.Observe(Inv((Blocks.Log, 0, n))); c.Advance(1); }
        var e = Assert.Single(l.Entries);
        Assert.Equal(5, e.Delta);
        Assert.Equal("chop_tree", e.Cause);
    }

    [Theory]
    [InlineData("Cobblestone Slab", "slabs", true)]
    [InlineData("Cobblestone Slab", "cobble slabs", true)]
    [InlineData("Cobblestone Slab", "the cobblestone slabs", true)]
    [InlineData("Oak Sapling", "saplings", true)]
    [InlineData("Oak Wood", "slabs", false)]
    public void Item_names_match_loosely(string name, string query, bool match) =>
        Assert.Equal(match, InventoryLedger.Matches(name, query));

    [Fact]
    public void A_partial_take_that_puts_the_rest_of_the_stack_back_is_one_net_take_not_used_during_withdraw()
    {
        // Live 10-28 06:49:46: "+64 Cobblestone: taken from a chest" then "-58 Cobblestone: used during withdraw" for a take of 6
        // (the whole stack on the cursor, the rest put back; no slab was crafted).
        var (l, c) = New((Cobble, 0, 0));
        string? action = "withdraw";
        l.CurrentAction = () => action;
        l.Observe(Inv((Cobble, 0, 64)));
        c.Advance(0.5);
        l.Flush();
        l.Observe(Inv((Cobble, 0, 6)));
        c.Advance(0.5);
        action = null;
        var e = Assert.Single(l.History("cobble"));
        Assert.Equal(6, e.Delta);
        Assert.Equal("taken from a chest", e.Cause);
        Assert.DoesNotContain(l.Entries, x => x.Cause.Contains("used during withdraw", StringComparison.Ordinal));
    }

    /// <summary>
    /// Live 11-05: "-1 Seeds: placed while moving (bridge/pillar block)" ×176, "-35 Bread: placed while moving", "-1 Wooden Shovel: placed
    /// while moving": 1.12.2 sends a sowing's slot update a tick after its block change, so it landed in the next walk. Only a block a
    /// walk builds with is placed while moving; seeds are booked to the sowing before, bread to the deposit.
    /// </summary>
    [Fact]
    public void Sowing_or_a_deposit_whose_update_lands_during_a_walk_is_not_a_movement_placement()
    {
        const int Seeds = Items.Seeds, Bread = Items.Bread, Dirt = Blocks.Dirt;
        var (l, c) = New((Seeds, 0, 10), (Bread, 0, 35), (Dirt, 0, 8));
        string? action = "place";
        l.CurrentAction = () => action;
        l.Flush();               // the sowing runs (the pump samples it)
        c.Advance(0.3);
        action = "goto";         // it ended; the next walk is under way when the seed's slot update arrives
        l.Observe(Inv((Seeds, 0, 9), (Bread, 0, 35), (Dirt, 0, 8)));
        c.Advance(0.5);
        l.Flush();
        var seed = Assert.Single(l.History("seeds"));
        Assert.Equal(-1, seed.Delta);
        Assert.StartsWith("placed as a block", seed.Cause);
        // A walk that really pillars with dirt is still a movement placement; bread with no action before it is never one.
        using (l.Scope(InventoryLedger.MovingCause))
        {
            l.Observe(Inv((Seeds, 0, 9), (Bread, 0, 35), (Dirt, 0, 6)));
            c.Advance(6);
            l.Observe(Inv((Seeds, 0, 9), (Dirt, 0, 6)));
        }
        c.Advance(0.5);
        l.Flush();
        Assert.Equal(InventoryLedger.MovingCause, Assert.Single(l.History("dirt")).Cause);
        var bread = Assert.Single(l.History("bread"));
        Assert.DoesNotContain("placed while moving", bread.Cause);
        Assert.DoesNotContain(l.Entries, e => e.ItemId != Dirt && e.Cause == InventoryLedger.MovingCause);
    }

    [Theory]
    [InlineData("generator: 64/64 cobblestone in 4m15s (dug 64 at 5,64,7)", 4, "generator: 4/64 cobblestone in 4m15s (dug 64 at 5,64,7)")]
    [InlineData("generator: 7/64 cobblestone in 2m10s (dug 9 at 5,64,7)", 9, "generator: 7/64 cobblestone in 2m10s (dug 9 at 5,64,7)")]
    [InlineData("standing by: no free gen cell", 0, "standing by: no free gen cell")]
    public void The_gen_task_result_states_the_ledgers_cobble_count(string text, int ledger, string expected) =>
        Assert.Equal(expected, InventoryLedgerTools.LedgerCount(text, ledger));
}
