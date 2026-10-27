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
}
