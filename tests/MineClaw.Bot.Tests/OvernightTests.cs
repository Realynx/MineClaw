using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception;
using MineClaw.Client;
using MineClaw.Data;
using WPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Night shift 10-27: a restarted clone had no pickaxe (loadout), the gen loop must survive tool breakage / full inventory /
/// a wrong cell on its own, craft must use chest ingredients ("no sticks on me, the chest has planks"), and a player's
/// correction must replace the gen's stale dig cells instead of appending.
/// </summary>
public class OvernightTests
{
    private static void Chest(BotHost h, int x, int y, int z, params ItemCount[] items) =>
        h.Runtime.Observations.Upsert(new Observation("chest", $"{x},{y},{z}", "chest") { Bounds = Box.Point(x, y, z), Contents = items });

    // ---------------------------------------------------------------- loadout

    [Fact]
    public async Task Loadout_takes_a_pickaxe_from_the_chest_that_has_one_and_reports_what_is_missing()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        Chest(h, 2, 64, 0, new ItemCount(Items.StonePickaxe, "stone_pickaxe", 1), new ItemCount(Blocks.Cobblestone, "cobblestone", 64));
        c.Effects["withdraw"] = () => c.Give(36, Items.StonePickaxe, 1);
        var r = await Loadout.FetchAsync(c, h.Runtime.Observations, ["pickaxe", "food"], default);
        Assert.Contains($"withdraw {Items.StonePickaxe} 1 2,64,0", string.Join(" | ", c.CallList));
        Assert.Contains("took stone pickaxe from 2,64,0", r.Text);
        Assert.Equal(["food"], r.Missing);                 // honest: there's no food anywhere
        Assert.Contains("no food in my inventory or storage", r.Text);
        Assert.Empty(Loadout.Lacking(c.World, ["pickaxe"]));
    }

    // ---------------------------------------------------------------- crafting from chests

    [Fact]
    public async Task Craft_withdraws_missing_intermediates_from_a_known_chest_first()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        Chest(h, 3, 64, 1, new ItemCount(Blocks.Planks, "planks", 8));
        int planks = 0;
        c.Effects["withdraw"] = () => c.Give(37, Blocks.Planks, planks = 2);
        var crafter = new Crafter(c) { Observations = h.Runtime.Observations };
        var r = await crafter.CraftAsync("stick", 4, default);
        var calls = string.Join(" | ", c.CallList);
        Assert.True(r.Ok, r.Message);
        Assert.Contains($"withdraw {Blocks.Planks} 2 3,64,1", calls);   // the least that completes the tree
        Assert.Contains($"craft {Items.Stick} 4", calls);
        Assert.StartsWith("took 2 wooden planks from 3,64,1", r.Message);
        Assert.Equal(2, planks);
    }

    [Fact]
    public async Task Craft_without_chest_data_still_reports_what_is_missing()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        var r = await new Crafter(c) { Observations = h.Runtime.Observations }.CraftAsync("stick", 4, default);
        Assert.False(r.Ok);
        Assert.Contains("missing", r.Message);
        Assert.DoesNotContain("withdraw", string.Join(" | ", c.CallList));
    }

    // ---------------------------------------------------------------- the unattended gen loop

    [Fact]
    public async Task Gen_loop_deposits_when_full_replaces_a_broken_pickaxe_and_keeps_going()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        c.Give(36, Items.StonePickaxe, 1);
        Chest(h, 2, 64, 0, new ItemCount(Items.IronPickaxe, "iron_pickaxe", 1));
        int call = 0;
        c.Results["generator"] = () => ++call switch
        {
            1 => ActionResult.Success("generator: dug 40, +40 cobblestone (have 40); inventory full", 40),
            2 => ActionResult.Success("generator: dug 30, +30 cobblestone (have 30); pickaxe broke (no pickaxe left)", 30),
            _ => ActionResult.Success("generator: dug 64, +64 cobblestone", 64),
        };
        c.Effects["generator"] = () =>
        {
            if (call == 0) for (int s = 9; s <= 44; s++) if (s != 36) c.Give(s, Blocks.Cobblestone, 64); // full after batch 1
            if (call == 1) c.World.Inventory.SetSlot(0, 36, ItemStack.Empty);                            // the pickaxe breaks in batch 2
        };
        c.Effects["deposit"] = () => { for (int s = 9; s <= 44; s++) if (c.World.Inventory.PlayerSlots()[s].Id == Blocks.Cobblestone) c.World.Inventory.SetSlot(0, s, ItemStack.Empty); };
        c.Effects["withdraw"] = () => c.Give(36, Items.IronPickaxe, 1);
        var store = new WPos(14, 64, -5);
        var loop = new GeneratorLoop(c, new GeneratorSupport(h.Runtime.Observations)) { RetryPause = TimeSpan.Zero };
        var r = await loop.RunAsync(new WPos(6, 64, 8), 198, store, default);
        var calls = string.Join(" | ", c.CallList);
        Assert.True(r.Ok, r.Message);
        Assert.StartsWith("generator: dug 198/198 at 6,64,8", r.Message);
        Assert.Contains("deposit 14,64,-5 filter=True", calls);            // full → the store chest, then on
        Assert.Contains($"withdraw {Items.IronPickaxe} 1 2,64,0", calls);  // broke → a spare from storage, then on
        Assert.Contains("iron pickaxe from 2,64,0", r.Message);
        Assert.False(c.LastDepositFilter!(new ItemStack((short)Items.IronPickaxe, 1, 0)), "tools stay with the bot");
    }

    [Fact]
    public async Task Gen_loop_stops_at_once_on_a_cell_that_does_not_yield_and_retracts_it()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        c.Give(36, Items.StonePickaxe, 1);
        c.Results["generator"] = () => ActionResult.Fail("6,64,8 isn't regenerating cobble for me (yield 52%: 33 cobblestone from 64 digs), need the right cell (dug 64, +33)", 64);
        WPos? bad = null;
        var loop = new GeneratorLoop(c, new GeneratorSupport(BadCell: (p, _) => bad = p)) { RetryPause = TimeSpan.Zero };
        var r = await loop.RunAsync(new WPos(6, 64, 8), 120, null, default);
        Assert.False(r.Ok);
        Assert.Contains("6,64,8 isn't regenerating cobble for me (yield 52%", r.Message);
        Assert.Single(c.CallList, x => x.StartsWith("generator", StringComparison.Ordinal)); // never digs on
        Assert.Equal(new WPos(6, 64, 8), bad);
    }

    [Fact]
    public async Task Gen_loop_without_any_pickaxe_says_so_instead_of_digging_bare_handed()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        var r = await new GeneratorLoop(c, new GeneratorSupport(h.Runtime.Observations)).RunAsync(new WPos(6, 64, 8), 64, null, default);
        Assert.False(r.Ok);
        Assert.Contains("no pickaxe left: none in storage", r.Message);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("generator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Harvest_generator_refuses_a_block_that_is_not_a_generator_cell()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        c.Floor();
        c.World.SetBlock(3, 65, 3, Blocks.Cobblestone, 0); // plain cobble: no lava, no water
        var r = await h.Run("harvest_generator", new { at = "3,65,3", count = 16 });
        Assert.False(r.Ok);
        Assert.Contains("isn't a generator cell", r.Text);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("generator", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- corrections replace stale dig cells

    private static (DigCells Cells, DigCellCorrections Fix, MemoryStore Mem) Gen(BotHost h)
    {
        var mem = h.Runtime.Memory;
        mem.UpsertStructure("dual cobble generator", s =>
        {
            s.Kind = StructureKinds.CobbleGenerator;
            s.Min = new BlockPos(4, 63, 6);
            s.Max = new BlockPos(8, 64, 8);
            s.KeyBlocks.Add(new KeyBlock { Label = "mine_here", X = 6, Y = 64, Z = 8 });
            s.KeyBlocks.Add(new KeyBlock { Label = "stand", X = 8, Y = 64, Z = 8 });
            s.KeyBlocks.Add(new KeyBlock { Label = "store_chest", X = 14, Y = 64, Z = -5 });
            s.Steps.Add("stand at 8,64,8 facing the gen");
            s.Steps.Add("mine cobble at 6,64,8 repeatedly");
            s.Note = "Fox mined cobble at 6,64,8 repeatedly, then stored in double chest 14,64,-5";
        });
        var cells = new DigCells(mem);
        var fix = new DigCellCorrections(cells, mem);
        cells.TaughtCell += (p, by) => fix.OnTaught(p, by);
        return (cells, fix, mem);
    }

    [Fact]
    public void A_correction_replaces_the_gens_old_dig_cells_and_keeps_the_history()
    {
        var (h, _) = TestBot.Create();
        using var __ = h;
        var (cells, fix, mem) = Gen(h);
        Assert.True(cells.IsDigCell(new WPos(6, 64, 8)));

        Assert.True(fix.OnChat("PoofimaFox", "you're mining the wrong blocks again. Im going to point out both mine blocks 1 last time"));
        cells.Register(new WPos(4, 64, 6), "PoofimaFox");   // punch 1
        cells.Register(new WPos(5, 64, 7), "PoofimaFox");   // punch 2: both stay (the new set)

        var s = mem.GetStructure("dual cobble generator")!;
        var digs = s.KeyBlocks.Where(k => DigCells.IsDigLabel(k.Label)).Select(k => (k.X, k.Y, k.Z)).ToList();
        Assert.DoesNotContain((6, 64, 8), digs);                     // retracted, not appended to
        Assert.Contains((4, 64, 6), digs);
        Assert.Contains((5, 64, 7), digs);
        Assert.Contains(s.KeyBlocks, k => k.Label == "stand");       // non-dig keys untouched
        Assert.Contains(s.KeyBlocks, k => k.Label == "store_chest");
        Assert.Contains(s.RetiredKeys, k => k.X == 6 && k.Z == 8 && k.Label.StartsWith("mine_here [retracted v2", StringComparison.Ordinal) && k.Label.Contains("PoofimaFox"));
        Assert.DoesNotContain(s.Steps, x => x.Contains("6,64,8"));   // the stale instruction is history now
        Assert.Contains(s.History, x => x.Contains("mine cobble at 6,64,8 repeatedly"));
        Assert.Contains(s.History, x => x.StartsWith("v2 ", StringComparison.Ordinal) && x.Contains("retracted 6,64,8"));
        Assert.Contains("6,64,8 (retracted: wrong cell)", s.Note);
        Assert.Contains("retracted (don't use): 6,64,8", MemoryStore.FormatStructure(s));
        Assert.False(cells.IsDigCell(new WPos(6, 64, 8)));
        Assert.True(cells.IsDigCell(new WPos(4, 64, 6)));
    }

    [Fact]
    public void Without_a_correction_a_taught_cell_is_added_and_nothing_is_retracted()
    {
        var (h, _) = TestBot.Create();
        using var __ = h;
        var (cells, fix, mem) = Gen(h);
        Assert.False(fix.OnChat("PoofimaFox", "also 67, this block I'm punching is your cell to mine"));
        cells.Register(new WPos(4, 64, 6), "PoofimaFox");
        var s = mem.GetStructure("dual cobble generator")!;
        Assert.Contains(s.KeyBlocks, k => k.X == 6 && k.Z == 8 && k.Label == "mine_here");
        Assert.Contains(s.KeyBlocks, k => k.X == 4 && k.Z == 6);
        Assert.Empty(s.RetiredKeys);
    }

    [Fact]
    public void The_yield_guard_retracts_a_cell_it_proved_wrong()
    {
        var (h, _) = TestBot.Create();
        using var __ = h;
        var (cells, fix, mem) = Gen(h);
        Assert.NotNull(fix.RetractBad(new WPos(6, 64, 8), "yield 52%"));
        var s = mem.GetStructure("dual cobble generator")!;
        Assert.DoesNotContain(s.KeyBlocks, k => k.X == 6 && k.Z == 8 && DigCells.IsDigLabel(k.Label));
        Assert.Contains(s.RetiredKeys, k => k.X == 6 && k.Z == 8 && k.Label.Contains("yield guard"));
        Assert.False(cells.IsDigCell(new WPos(6, 64, 8)));
    }

    [Theory]
    [InlineData("you're mining the wrong blocks again", true)]
    [InlineData("no, not that one", true)]
    [InlineData("that's not the right block", true)]
    [InlineData("67 you need to be working the other cell of this cobble gen", false)]
    [InlineData("mine more cobble please", false)]
    public void Correction_phrases(string text, bool correction) => Assert.Equal(correction, DigCellCorrections.IsCorrection(text));
}
