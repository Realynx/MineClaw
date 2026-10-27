using MineClaw.Agent.Rag;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Self-reliant prerequisite planning (user 10-27: "if I tell it to use the cobble gen it needs a pickaxe, then acquiring a
/// pickaxe should be part of the planning... a stone pickaxe is better"). Synthetic island: grass floor, a cobble gen
/// (cobble with lava and water next to it), optional trees and chests; the fake client's crafts, chops and gen batches put
/// their output in the inventory.
/// </summary>
public class AcquisitionTests
{
    private static readonly BlockPos Gen = new(5, 65, 0);

    private sealed class Island
    {
        public required BotHost H;
        public required FakeGameClient C;
        public List<string> Plans { get; } = new();
        public List<string> Asked { get; } = new();
        public string Calls => string.Join(" | ", C.CallList);
        public AcquisitionSources S => H.Acquisition!;
    }

    private static Island Make(int trees = 3, bool gen = true)
    {
        var (h, c) = TestBot.Create();
        c.Floor(64, 12, Blocks.Grass);
        if (gen)
        {
            c.World.SetBlock(Gen.X, Gen.Y, Gen.Z, Blocks.Cobblestone, 0);
            c.World.SetBlock(Gen.X + 1, Gen.Y, Gen.Z, Blocks.Lava, 0);
            c.World.SetBlock(Gen.X, Gen.Y, Gen.Z + 1, Blocks.Water, 0);
        }
        BlockPos[] spots = [new(-6, 65, -6), new(-6, 65, 6), new(6, 65, -7), new(0, 65, 9)];
        foreach (var t in spots.Take(trees)) Tree(c, t);
        var isl = new Island { H = h, C = c };
        isl.S.Announce = isl.Plans.Add;
        isl.S.AskPlayer = (q, _, _) => { isl.Asked.Add(q); return Task.FromResult<string?>(null); };
        Wire(c);
        return isl;
    }

    private static void Tree(FakeGameClient c, BlockPos b)
    {
        for (int y = 0; y < 4; y++) c.World.SetBlock(b.X, b.Y + y, b.Z, Blocks.Log, 0);
        int top = b.Y + 3;
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) c.World.SetBlock(b.X + dx, top, b.Z + dz, Blocks.Leaves, 0);
        c.World.SetBlock(b.X, top + 1, b.Z, Blocks.Leaves, 0);
    }

    /// <summary>Adds items to the first matching/empty main or hotbar slot.</summary>
    public static void Add(FakeGameClient c, int id, int n)
    {
        var slots = c.World.Inventory.PlayerSlots();
        foreach (int s in Enumerable.Range(36, 9).Concat(Enumerable.Range(9, 27)))
            if (slots[s].Id == id && slots[s].Count + n <= 64) { c.Give(s, id, slots[s].Count + n); return; }
        foreach (int s in Enumerable.Range(36, 9).Concat(Enumerable.Range(9, 27)))
            if (slots[s].IsEmpty) { c.Give(s, id, n); return; }
    }

    private static int Last(FakeGameClient c, string verb, int field) =>
        int.Parse(c.CallList.Last(x => x.StartsWith(verb + " ", StringComparison.Ordinal)).Split(' ')[field]);

    /// <summary>Crafts, chops, gen batches and placements have their real effect on the inventory/world.</summary>
    private static void Wire(FakeGameClient c)
    {
        c.Effects["craft"] = () => Add(c, Last(c, "craft", 1), Math.Max(1, Last(c, "craft", 2)));
        c.Effects["chop"] = () => Add(c, Blocks.Log, 5);
        int dug = 0;
        c.Effects["generator"] = () => { dug = int.Parse(c.CallList.Last(x => x.StartsWith("generator ")).Split(' ')[2]); Add(c, Blocks.Cobblestone, dug); };
        c.Results["generator"] = () => ActionResult.Success($"dug {dug}", dug);
        c.Effects["place"] = () =>
        {
            var parts = c.CallList.Last(x => x.StartsWith("place ")).Split(' ');
            var p = parts[1].Split(',').Select(int.Parse).ToArray();
            c.World.SetBlock(p[0], p[1], p[2], int.Parse(parts[2]), 0);
        };
    }

    private static void Chest(BotHost h, int x, int y, int z, params ItemCount[] items)
    {
        h.Client.World.SetBlock(x, y, z, Blocks.Chest, 0);
        h.Runtime.Observations.Upsert(new Observation("chest", $"{x},{y},{z}", "chest") { Bounds = Box.Point(x, y, z), Contents = items });
    }

    private static int Inv(FakeGameClient c, int id) => c.World.Inventory.CountItem(id);

    // ---------------------------------------------------------------- bootstrap

    [Fact]
    public async Task Empty_inventory_and_a_gen_bootstraps_a_stone_pickaxe_then_mines()
    {
        var i = Make(trees: 3);
        using var _ = i.H;
        var r = await i.H.Run("harvest_generator", new { at = "5,65,0", count = 8 });
        Assert.True(r.Ok, r.Text);
        Assert.True(Inv(i.C, Items.StonePickaxe) > 0, i.Calls);
        // tree → wood pick → 3 cobble at the gen → stone pick, then the job itself.
        var calls = i.C.CallList;
        int chop = calls.FindIndex(x => x.StartsWith("chop")), woodPick = calls.IndexOf($"craft {Items.WoodenPickaxe} 1");
        int cobble = calls.IndexOf("generator 5,65,0 3"), stonePick = calls.IndexOf($"craft {Items.StonePickaxe} 1");
        Assert.True(chop >= 0 && chop < woodPick && woodPick < cobble && cobble < stonePick, i.Calls);
        Assert.Contains("generator 5,65,0 8", calls);   // and then it mines
        Assert.Contains("dug 8/8", r.Text);
        // The plan line, once, and in the result.
        var plan = Assert.Single(i.Plans);
        Assert.Equal("no pick, making one: logs→planks→sticks→table→wood pick→3 cobble→stone pick", plan);
        Assert.Contains(plan, r.Text);
        Assert.Empty(i.Asked);
    }

    [Fact]
    public async Task A_pick_in_a_chest_is_preferred_over_crafting()
    {
        var i = Make();
        using var _ = i.H;
        Add(i.C, Blocks.Planks, 8);   // enough to craft a wooden pickaxe right here
        Chest(i.H, 2, 65, 2, new ItemCount(Items.StonePickaxe, "stone_pickaxe", 1));
        i.C.Effects["withdraw"] = () => Add(i.C, Items.StonePickaxe, 1);
        var r = await Acquisition.Planner(i.C).AcquireAsync(new Need("pickaxe"), default);
        Assert.True(r.Ok, r.Text);
        Assert.Equal("stone pickaxe from 2,65,2", r.Text);
        Assert.DoesNotContain("craft", i.Calls);
        Assert.DoesNotContain("chop", i.Calls);
        Assert.Empty(i.Plans);                       // a one-step fetch is no plan
    }

    [Fact]
    public async Task A_hive_handoff_is_preferred_over_bootstrapping()
    {
        var i = Make(trees: 3);
        using var _ = i.H;
        HandoffAsk? asked = null;
        i.S.RequestHandoff = (a, _) =>
        {
            asked = a;
            Add(i.C, Items.StonePickaxe, 1);   // main had 2 picks while 67 and 69 had none
            return Task.FromResult<string?>("maid_chan");
        };
        var r = await Acquisition.Planner(i.C).AcquireAsync(new Need("pickaxe"), default);
        Assert.True(r.Ok, r.Text);
        Assert.Equal("pickaxe handed over by maid_chan", r.Text);
        Assert.Equal("pickaxe", asked!.Need);
        Assert.Equal(Items.DiamondPickaxe, asked.ItemIds[0]);  // best tier first
        Assert.DoesNotContain("chop", i.Calls);
        Assert.DoesNotContain("craft", i.Calls);
        Assert.DoesNotContain("generator", i.Calls);
    }

    [Fact]
    public async Task With_only_two_trees_left_the_bootstrap_uses_chest_logs_instead_of_chopping()
    {
        var i = Make(trees: 2);
        using var _ = i.H;
        Chest(i.H, 2, 65, 2, new ItemCount(Blocks.Log, "log", 6));
        i.C.Effects["withdraw"] = () => Add(i.C, Blocks.Log, Last(i.C, "withdraw", 2));
        var r = await Acquisition.Planner(i.C).AcquireAsync(new Need("pickaxe"), default);
        Assert.True(r.Ok, r.Text);
        Assert.True(Inv(i.C, Items.StonePickaxe) > 0, i.Calls);
        Assert.Contains($"withdraw {Blocks.Log} 3 2,65,2", i.Calls);
        Assert.DoesNotContain("chop", i.Calls);
    }

    [Fact]
    public async Task With_only_two_trees_and_no_wood_anywhere_it_asks_instead_of_chopping()
    {
        var i = Make(trees: 2);
        using var _ = i.H;
        var r = await Acquisition.Planner(i.C).AcquireAsync(new Need("pickaxe"), default);
        Assert.False(r.Ok, r.Text);
        Assert.DoesNotContain("chop", i.Calls);
        var q = Assert.Single(i.Asked);
        Assert.Contains("pick", q);
        Assert.Contains("last trees", q);
        Assert.Contains("I'll stop", q);               // the default when nobody answers
        Assert.Contains("won't chop", r.Text);
    }

    [Fact]
    public async Task A_sapling_from_storage_makes_one_of_the_last_trees_choppable()
    {
        var i = Make(trees: 2);
        using var _ = i.H;
        Chest(i.H, 2, 65, 2, new ItemCount(Blocks.Sapling, "sapling", 3));
        i.C.Effects["withdraw"] = () => Add(i.C, Blocks.Sapling, 1);
        var r = await Acquisition.Planner(i.C).AcquireAsync(new Need("pickaxe"), default);
        Assert.True(r.Ok, r.Text);
        Assert.Contains($"withdraw {Blocks.Sapling} 1 2,65,2", i.Calls);
        Assert.Contains("chop", i.Calls);             // felled and replanted: the tree count holds
    }

    [Fact]
    public async Task Protected_planks_and_protected_trees_are_never_taken_for_materials()
    {
        var i = Make(trees: 3, gen: true);
        using var _ = i.H;
        // 69 nearly mined the house planks: a plank house right next to the bot, and every tree is inside the build too.
        for (int x = 2; x <= 4; x++)
        for (int y = 65; y <= 67; y++) i.C.World.SetBlock(x, y, -3, Blocks.Planks, 0);
        i.C.IsProtected = (p, id) => id is Blocks.Planks or Blocks.Log;
        var r = await Acquisition.Planner(i.C).AcquireAsync(new Need("pickaxe"), default);
        Assert.False(r.Ok, r.Text);
        Assert.DoesNotContain(i.C.CallList, x => x.StartsWith("dig") || x.StartsWith("mine") || x.StartsWith("chop"));
        Assert.Single(i.Asked);                        // nothing natural left: a player, last
    }

    [Fact]
    public async Task Stone_first_when_cobble_and_sticks_are_on_hand_even_with_a_wood_pick_in_a_chest()
    {
        var i = Make();
        using var _ = i.H;
        Add(i.C, Blocks.Cobblestone, 3);
        Add(i.C, Items.Stick, 2);
        Chest(i.H, 2, 65, 2, new ItemCount(Items.WoodenPickaxe, "wooden_pickaxe", 1));
        var r = await Acquisition.Planner(i.C).AcquireAsync(new Need("pickaxe"), default);
        Assert.True(r.Ok, r.Text);
        Assert.Equal("crafted stone pickaxe", r.Text);
        Assert.DoesNotContain("withdraw", i.Calls);
    }

    [Fact]
    public async Task Mine_gets_the_pickaxe_it_needs_first()
    {
        var i = Make();
        using var _ = i.H;
        Chest(i.H, 2, 65, 2, new ItemCount(Items.StonePickaxe, "stone_pickaxe", 1));
        i.C.Effects["withdraw"] = () => Add(i.C, Items.StonePickaxe, 1);
        var r = await i.H.Run("mine", new { block = "stone", count = 4 });
        Assert.True(r.Ok, r.Text);
        var calls = i.C.CallList;
        // Stone/cobble come from the generator's dig cells only (live 10-27 15:55: "mine cobble" dug the island floor).
        Assert.True(calls.FindIndex(x => x.StartsWith("withdraw")) < calls.FindIndex(x => x.StartsWith("generator")), i.Calls);
        Assert.StartsWith("stone pickaxe from 2,65,2; ", r.Text);
    }

    [Fact]
    public async Task Acquisition_is_bounded_by_steps()
    {
        var i = Make(trees: 3);
        using var _ = i.H;
        i.S.Limits = new AcquisitionLimits { MaxSteps = 2 };
        var r = await Acquisition.Planner(i.C).AcquireAsync(new Need("pickaxe"), default);
        Assert.False(r.Ok);
        Assert.Contains("gave up: over 2 steps", r.Text);
    }

    [Fact]
    public async Task Gen_loop_upgrades_a_wood_pick_to_stone_once_its_cobble_allows()
    {
        var i = Make();
        using var _ = i.H;
        Add(i.C, Items.WoodenPickaxe, 1);
        Add(i.C, Items.Stick, 2);
        Add(i.C, Blocks.Planks, 4);   // for the crafting table
        var r = await i.H.Run("harvest_generator", new { at = "5,65,0", count = 100 });
        Assert.True(r.Ok, r.Text);
        Assert.True(Inv(i.C, Items.StonePickaxe) > 0, i.Calls);
        Assert.Contains("upgraded to stone pickaxe", r.Text);
        Assert.Single(i.C.CallList, x => x.StartsWith($"craft {Items.StonePickaxe}"));
    }
}
