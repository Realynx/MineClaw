using MineClaw.Agent.Rag;
using MineClaw.Bot.GameTools;
using MineClaw.Data;
using WPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-27 23:06: the gen worker said "Chest 14,64,-5 and all known chests full" while the two chests upstairs held only a
/// few sticks/planks (102 cobble then went in fine). "Full" came from remembered contents: a chest memory calls full is now
/// opened and re-checked, every known chest is tried (upstairs too), and a full answer names each chest tried.
/// </summary>
public class DepositFullTests
{
    private static ItemCount I(int id, int n) => new(id, Items.Exists(id) ? Items.Get(id).Name : Blocks.Get(id).Name, n);

    private static void Chest(BotHost h, WPos p, bool dbl, params ItemCount[] items) =>
        h.Runtime.Observations.Upsert(new Observation("chest", $"{p.X},{p.Y},{p.Z}", "chest")
        {
            Bounds = dbl ? Box.Of(p.X, p.Y, p.Z, p.X + 1, p.Y, p.Z) : Box.Point(p.X, p.Y, p.Z),
            Contents = items,
            ContentsAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(40),
        });

    [Fact]
    public void A_double_chest_with_partial_stacks_and_empty_slots_is_not_full()
    {
        var dbl = Box.Of(14, 64, -5, 15, 64, -5);
        // 30 stacks' worth in 54 slots: cobble x1000 = 16 stacks (15 full + one of 40), logs x64, sticks x5...
        var contents = new[] { I(Blocks.Cobblestone, 1000), I(Blocks.Log, 64), I(Items.Stick, 5) };
        var (free, partial) = ChestRoom.Estimate(dbl, contents, [Blocks.Cobblestone]);
        Assert.Equal(54 - 16 - 1 - 1, free);
        Assert.Equal(24, partial); // the cobble stack of 40 takes 24 more
        Assert.True(ChestRoom.HasRoom(dbl, contents, [Blocks.Cobblestone]));
        // The same items in a single chest's 27 slots still leave 9.
        Assert.Equal(9, ChestRoom.Estimate(Box.Point(14, 64, -5), contents).FreeSlots);
        // No empty slot, but a partial cobble stack: room for cobble, none for logs.
        var packed = new[] { I(Blocks.Cobblestone, 26 * 64 + 40) };
        Assert.True(ChestRoom.HasRoom(Box.Point(1, 64, 1), packed, [Blocks.Cobblestone]));
        Assert.False(ChestRoom.HasRoom(Box.Point(1, 64, 1), packed, [Blocks.Log]));
    }

    [Fact]
    public async Task Stale_memory_saying_full_is_re_checked_by_opening_and_the_cobble_goes_in()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        var store = new WPos(14, 64, -5);
        var upstairsA = new WPos(11, 68, -5);
        var upstairsB = new WPos(15, 68, -8);
        // Memory (40 min old) calls every chest full: 54/27 slots of stuff.
        Chest(h, store, dbl: true, I(Blocks.Cobblestone, 54 * 64));
        Chest(h, upstairsA, dbl: false, I(Blocks.Planks, 27 * 64));
        Chest(h, upstairsB, dbl: false, I(Items.Stick, 27 * 64));
        for (int s = 9; s <= 44; s++) c.Give(s, Blocks.Cobblestone, 64);
        c.Give(36, Items.StonePickaxe, 1);
        // Really: the store is full; upstairs A actually holds a few sticks and planks.
        c.Results["deposit"] = () =>
        {
            var last = c.CallList.Last();
            if (last.StartsWith("deposit 14,64,-5", StringComparison.Ordinal)) return MineClaw.Client.ActionResult.Fail("chest is full");
            for (int s = 9; s <= 44; s++) if (c.World.Inventory.PlayerSlots()[s].Id == Blocks.Cobblestone) c.World.Inventory.SetSlot(0, s, MineClaw.Data.ItemStack.Empty);
            return MineClaw.Client.ActionResult.Success("deposited 102 cobblestone", 102);
        };
        var loop = new GeneratorLoop(c, new GeneratorSupport(h.Runtime.Observations));
        var d = await loop.DepositAsync(store, default);
        Assert.True(d.Ok, d.Message);
        Assert.True(d.Message.StartsWith("deposited at 11,68,-5"), d.Message + " / " + string.Join("|", c.CallList));
        Assert.Contains("deposit 14,64,-5", string.Join("|", c.CallList));
        Assert.Contains("deposit 11,68,-5", string.Join("|", c.CallList));
    }

    [Fact]
    public async Task Really_full_everywhere_names_every_chest_it_tried()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        Chest(h, new WPos(14, 64, -5), dbl: true, I(Blocks.Cobblestone, 54 * 64));
        Chest(h, new WPos(11, 68, -5), dbl: false, I(Blocks.Planks, 27 * 64));
        for (int s = 9; s <= 44; s++) c.Give(s, Blocks.Cobblestone, 64);
        c.Results["deposit"] = () => MineClaw.Client.ActionResult.Fail("chest is full");
        c.Results["place"] = () => MineClaw.Client.ActionResult.Fail("no chest");
        var d = await new GeneratorLoop(c, new GeneratorSupport(h.Runtime.Observations)).DepositAsync(null, default);
        Assert.False(d.Ok);
        Assert.Contains("14,64,-5", d.Message);
        Assert.Contains("11,68,-5", d.Message);
        Assert.StartsWith("storage full: tried", d.Message);
    }

    /// <summary>
    /// Live 10-28 11:10/12:05: gen_loop's store(chest:"14,64,-5") died on "chest is full" every run while 5,64,4 had room. The
    /// named chest goes first, the rest goes on to the next known chest with room (a closer "private" one is skipped), and the
    /// result says where it all went.
    /// </summary>
    [Fact]
    public async Task Store_at_a_full_chest_falls_back_to_the_next_known_chest_and_says_where_things_went()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        Chest(h, new WPos(14, 64, -5), dbl: true, I(Blocks.Cobblestone, 50 * 64));
        Chest(h, new WPos(5, 64, 4), dbl: false, I(Items.Stick, 3));
        var priv = h.Runtime.Observations.Upsert(new Observation("chest", "2,64,2", "chest") { Bounds = Box.Point(2, 64, 2), Contents = [] });
        h.Runtime.Observations.AdminEdit(priv.Id, null, "Fox's private stash");
        c.Give(9, Blocks.Cobblestone, 64);
        c.Results["deposit"] = () =>
        {
            var last = c.CallList.Last();
            if (last.StartsWith("deposit 14,64,-5", StringComparison.Ordinal))
            {
                c.Give(9, Blocks.Cobblestone, 34);
                return MineClaw.Client.ActionResult.Success("deposited 30 cobblestone (chest full)", 30);
            }
            c.World.Inventory.SetSlot(0, 9, MineClaw.Data.ItemStack.Empty);
            return MineClaw.Client.ActionResult.Success("deposited 34 cobblestone", 34);
        };
        var r = await h.Run("deposit", new { at = "14,64,-5", items = new[] { "cobblestone" } });
        Assert.True(r.Ok, r.Text);
        Assert.Equal("stored 64 cobblestone: 30 → 14,64,-5 (now full), 34 → 5,64,4", r.Text);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("deposit 2,64,2", StringComparison.Ordinal)); // private: never a fallback
    }

    [Fact]
    public async Task Every_known_chest_full_is_one_actionable_line_and_nothing_is_placed()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        Chest(h, new WPos(14, 64, -5), dbl: true, I(Blocks.Cobblestone, 54 * 64));
        Chest(h, new WPos(5, 64, 4), dbl: false, I(Blocks.Cobblestone, 27 * 64));
        Chest(h, new WPos(11, 68, -5), dbl: false, I(Blocks.Planks, 27 * 64));
        c.Give(9, Blocks.Cobblestone, 64);
        c.Give(10, Blocks.Cobblestone, 64);
        c.Give(11, Blocks.Cobblestone, 13);
        for (int s = 12; s < 16; s++) c.Give(s, Blocks.Log, 64);
        c.Give(16, Blocks.Log, 44);
        c.Results["deposit"] = () => MineClaw.Client.ActionResult.Fail("chest is full");
        var r = await h.Run("deposit", new { at = "14,64,-5", items = new[] { "cobblestone" } });
        Assert.False(r.Ok);
        Assert.Equal("all 3 known chests full (14,64,-5, 5,64,4, …); 141 cobblestone in bag; craft+place a chest (have 300 logs) " +
            "or keep carrying", r.Text);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("place", StringComparison.Ordinal)); // placing a chest is the brain's call
    }

    [Fact]
    public async Task Deposit_tool_without_a_chest_moves_on_from_a_full_nearest_chest()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        // The nearest chest in the world is full; memory knows one upstairs (stale "full").
        c.World.SetBlock(1, 64, 1, Blocks.Chest, 0);
        Chest(h, new WPos(11, 68, -5), dbl: false, I(Blocks.Planks, 27 * 64));
        c.Give(9, Blocks.Cobblestone, 32);
        c.Results["deposit"] = () => c.CallList.Last().StartsWith("deposit 1,64,1", StringComparison.Ordinal)
            ? MineClaw.Client.ActionResult.Fail("chest is full")
            : MineClaw.Client.ActionResult.Success("deposited 32 cobblestone", 32);
        var r = await h.Run("deposit", new { });
        Assert.True(r.Ok, r.Text);
        Assert.Equal("deposited 32 cobblestone in chest 11,68,-5", r.Text);
    }
}
