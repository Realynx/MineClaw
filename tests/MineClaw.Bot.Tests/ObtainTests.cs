using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The shared resolver (live 10-27/28: "ensure: no pickaxe (not in inventory or chests, can't craft)" next to a chest of cobble,
/// logs and sticks; "pickaxe 1/131, no cobble or sticks" with 300+ in storage; "Stone slabs need smelted stone" for cobble slabs;
/// "grid didn't produce chest" from 2 logs). Fake chests are real here: a withdraw moves the remembered items into the inventory
/// and a craft consumes exactly what the recipe tree uses, so every test checks the whole chain, not just the calls.
/// </summary>
public class ObtainTests
{
    private sealed class Store(BotHost h)
    {
        public readonly Dictionary<BlockPos, List<ItemCount>> Chests = new();

        public void Chest(int x, int y, int z, params ItemCount[] items)
        {
            var p = new BlockPos(x, y, z);
            Chests[p] = items.ToList();
            h.Client.World.SetBlock(x, y, z, Blocks.Chest, 0);
            Save(p);
        }

        public void Save(BlockPos p) => h.Runtime.Observations.Upsert(new Observation("chest", $"{p.X},{p.Y},{p.Z}", "chest")
        { Bounds = Box.Point(p.X, p.Y, p.Z), Contents = Chests[p] });
    }

    private static ItemCount I(int id, int n, int dmg = 0) => new(id, Items.Get(id).Name, n, dmg);

    /// <summary>Withdraws move items out of the chest memory into the inventory; crafts consume the tree's raw stock.</summary>
    private static void Real(FakeGameClient c, Store s)
    {
        c.Effects["withdraw"] = () =>
        {
            var parts = c.CallList.Last(x => x.StartsWith("withdraw ", StringComparison.Ordinal)).Split(' ');
            int id = int.Parse(parts[1]), n = int.Parse(parts[2]);
            var xyz = parts[3].Split(',').Select(int.Parse).ToArray();
            int? dmg = parts[4] == "dmg=" ? null : int.Parse(parts[4][4..]);
            var p = new BlockPos(xyz[0], xyz[1], xyz[2]);
            if (!s.Chests.TryGetValue(p, out var items)) return;
            for (int i = 0; i < items.Count && n > 0; i++)
            {
                var it = items[i];
                if (it.ItemId != id || (dmg is { } d && it.Damage != d)) continue;
                int k = Math.Min(n, it.Count);
                Put(c, id, it.Damage, k);
                items[i] = it with { Count = it.Count - k };
                n -= k;
            }
            items.RemoveAll(x => x.Count <= 0);
            s.Save(p);
        };
        int made = 0;
        c.Effects["craft"] = () =>
        {
            var parts = c.CallList.Last(x => x.StartsWith("craft ", StringComparison.Ordinal)).Split(' ');
            int id = int.Parse(parts[1]), n = int.Parse(parts[2]), dmg = c.LastCraftDamage;
            var inv = c.World.Inventory.PlayerSlots().Skip(9).Where(st => !st.IsEmpty && !(st.Id == id && (dmg < 0 || st.Damage == dmg))).ToList();
            var plan = RecipePlanner.Plan(id, n, inv, dmg, new PlannerOptions { AllowSmelting = false, HasCraftingTable = true });
            made = 0;
            if (!plan.Complete) return;
            foreach (var u in plan.Used) Take(c, u.Id, u.Damage, u.Count);
            var last = plan.Steps[^1];
            Put(c, last.OutputId, last.OutputDamage, last.OutputCount);
            made = last.OutputCount;
        };
        c.Results["craft"] = () => made > 0 ? ActionResult.Success($"crafted {made}", made) : ActionResult.Fail("missing ingredients");
        c.Effects["place"] = () =>
        {
            var parts = c.CallList.Last(x => x.StartsWith("place ", StringComparison.Ordinal)).Split(' ');
            var q = parts[1].Split(',').Select(int.Parse).ToArray();
            c.World.SetBlock(q[0], q[1], q[2], int.Parse(parts[2]), 0);
            Take(c, int.Parse(parts[2]), 0, 1);
        };
    }

    private static void Put(FakeGameClient c, int id, int dmg, int n)
    {
        var slots = c.World.Inventory.PlayerSlots();
        int max = Math.Max(1, (int)Items.Get(id).MaxStack);
        for (int s = 9; s < 45 && n > 0; s++)
        {
            var st = slots[s];
            if (st.IsEmpty || st.Id != id || st.Damage != dmg || st.Count >= max) continue;
            int k = Math.Min(n, max - st.Count);
            c.Give(s, id, st.Count + k, dmg);
            n -= k;
        }
        for (int s = 9; s < 45 && n > 0; s++)
        {
            if (!c.World.Inventory.PlayerSlots()[s].IsEmpty) continue;
            int k = Math.Min(n, max);
            c.Give(s, id, k, dmg);
            n -= k;
        }
    }

    private static void Take(FakeGameClient c, int id, int dmg, int n)
    {
        for (int s = 9; s < 45 && n > 0; s++)
        {
            var st = c.World.Inventory.PlayerSlots()[s];
            if (st.IsEmpty || st.Id != id || (dmg >= 0 && Items.Get(id).MaxDamage <= 0 && st.Damage != dmg)) continue;
            int k = Math.Min(n, (int)st.Count);
            if (k == st.Count) c.World.Inventory.SetSlot(0, s, ItemStack.Empty); else c.Give(s, id, st.Count - k, st.Damage);
            n -= k;
        }
    }

    private static (BotHost H, FakeGameClient C, Store S) Make(bool table = true)
    {
        var (h, c) = TestBot.Create();
        c.Floor(64, 12, Blocks.Grass);
        if (table) c.World.SetBlock(-2, 65, -2, Blocks.Workbench, 0);
        var s = new Store(h);
        Real(c, s);
        return (h, c, s);
    }

    private static int Inv(FakeGameClient c, int id, int? dmg = null) => c.World.Inventory.CountItem(id, dmg);
    private static string Calls(FakeGameClient c) => string.Join(" | ", c.CallList);

    [Fact]
    public async Task Ensure_pickaxe_crafts_a_stone_one_from_chest_logs_and_cobble()
    {
        var (h, c, s) = Make();
        using var _ = h;
        s.Chest(3, 65, 2, I(Blocks.Log, 40), I(Blocks.Dirt, 5));
        s.Chest(-3, 65, 3, I(Blocks.Cobblestone, 300));
        var r = await h.Run(AcquireTool.ToolName, new { what = "pickaxe" });   // what Lua ensure"pickaxe" calls
        Assert.True(r.Ok, r.Text + " | " + Calls(c));
        Assert.Equal(1, Inv(c, Items.StonePickaxe));
        // Exactly the raw ingredients of the whole tree (1 log → planks → sticks; 3 cobble), one stop per chest.
        Assert.Contains($"withdraw {Blocks.Log} 1 3,65,2 dmg=0", c.CallList);
        Assert.Contains($"withdraw {Blocks.Cobblestone} 3 -3,65,3 dmg=0", c.CallList);
        Assert.Equal(2, c.CallList.Count(x => x.StartsWith("withdraw", StringComparison.Ordinal)));
        Assert.Equal(39, s.Chests[new BlockPos(3, 65, 2)].Single(i => i.ItemId == Blocks.Log).Count);
        Assert.DoesNotContain("chop", Calls(c));
        Assert.DoesNotContain('\n', r.Text);                                  // one line back
        Assert.Contains("took 1 oak wood from 3,65,2", r.Text);
    }

    [Fact]
    public async Task A_worn_pickaxe_is_replaced_from_chest_materials_not_reported_as_already_had()
    {
        var (h, c, s) = Make();
        using var _ = h;
        c.Give(36, Items.StonePickaxe, 1, Items.Get(Items.StonePickaxe).MaxDamage - 1);   // 1/131 left
        s.Chest(3, 65, 2, I(Items.Stick, 10), I(Blocks.Cobblestone, 64));
        var r = await h.Run("craft", new { item = "stone pickaxe" });
        Assert.True(r.Ok, r.Text);
        Assert.Equal(2, Inv(c, Items.StonePickaxe));
        Assert.DoesNotContain("already have", r.Text);
        Assert.Contains($"withdraw {Items.Stick} 2 3,65,2", Calls(c));
    }

    [Fact]
    public async Task Twenty_cobble_slabs_come_from_chest_cobble()
    {
        var (h, c, s) = Make();
        using var _ = h;
        s.Chest(3, 65, 2, I(Blocks.Cobblestone, 64));
        var r = await h.Run("craft", new { item = "20 cobble slabs" });
        Assert.True(r.Ok, r.Text + " | " + Calls(c));
        Assert.True(Inv(c, Blocks.Slab, 3) >= 20, Calls(c));
        Assert.Equal(0, Inv(c, Blocks.Slab, 0));                              // never stone slabs
        Assert.Contains($"withdraw {Blocks.Cobblestone} 21 3,65,2 dmg=0", c.CallList);
        Assert.DoesNotContain("smelt", Calls(c));
    }

    [Fact]
    public async Task An_unnamed_slab_takes_the_recipe_that_is_possible_and_stone_slab_says_what_it_needs()
    {
        var (h, c, s) = Make();
        using var _ = h;
        s.Chest(3, 65, 2, I(Blocks.Cobblestone, 64));
        var any = await h.Run("craft", new { item = "slab", count = 3 });
        Assert.True(any.Ok, any.Text);
        Assert.Equal(3, Inv(c, Blocks.Slab, 3));
        var stone = await h.Run("craft", new { item = "stone slab", count = 3 });   // 44:0 needs smooth stone (no furnace here)
        Assert.False(stone.Ok);
        Assert.Equal("need 3 more stone for 3 stone slabs; none in inventory or the 1 known chest (cobblestone slab can be made now; " +
                     "smelting would do it, but no furnace is in range)", stone.Text);
        Assert.Single(c.CallList, x => x.StartsWith("withdraw", StringComparison.Ordinal));   // nothing taken for the stone slabs
    }

    [Fact]
    public async Task A_chest_from_logs_converts_them_to_planks_first()
    {
        var (h, c, s) = Make();
        using var _ = h;
        s.Chest(3, 65, 2, I(Blocks.Log, 2), I(Blocks.Cobblestone, 64));
        var r = await h.Run("craft", new { item = "chest" });
        Assert.True(r.Ok, r.Text + " | " + Calls(c));
        Assert.Equal(1, Inv(c, Blocks.Chest));
        Assert.Contains($"withdraw {Blocks.Log} 2 3,65,2 dmg=0", c.CallList);
        Assert.Equal(0, Inv(c, Blocks.Log));
    }

    [Fact]
    public async Task Without_a_table_the_table_planks_are_planned_too()
    {
        var (h, c, s) = Make(table: false);
        using var _ = h;
        s.Chest(3, 65, 2, I(Blocks.Log, 5));
        var r = await h.Run("craft", new { item = "chest" });
        Assert.True(r.Ok, r.Text + " | " + Calls(c));
        Assert.Contains($"withdraw {Blocks.Log} 3 3,65,2 dmg=0", c.CallList);   // 8 planks for the chest + 4 for the table
        Assert.Contains("placed crafting table", r.Text);
        Assert.Equal(1, Inv(c, Blocks.Chest));
    }

    [Fact]
    public async Task The_shortfall_is_exact_and_names_the_known_chests()
    {
        var (h, c, s) = Make();
        using var _ = h;
        s.Chest(3, 65, 2, I(Blocks.Cobblestone, 64));
        s.Chest(-3, 65, 3, I(Blocks.Dirt, 9));
        s.Chest(4, 65, -4, I(Blocks.Sand, 9));
        var none = await h.Run("craft", new { item = "chest" });
        Assert.False(none.Ok);
        Assert.Equal("need 2 more logs for a chest; none in inventory or the 3 known chests", none.Text);
        Assert.DoesNotContain("withdraw", Calls(c));                         // nothing moved for a craft that can't finish

        s.Chest(4, 65, -4, I(Blocks.Log, 1));
        var one = await h.Run("craft", new { item = "chest" });
        Assert.Equal("need 1 more log for a chest; inventory + the 3 known chests have only 1 log", one.Text);
        // The same line ends Lua ensure's failure (the acquire tool: no tree to chop, no player answers).
        var ensure = await h.Run(AcquireTool.ToolName, new { what = "chest" });
        Assert.False(ensure.Ok);
        Assert.Contains("need 1 more log for a chest", ensure.Text);
    }

    [Fact]
    public async Task The_last_sapling_and_dirt_in_storage_stay_and_reserved_items_are_never_ingredients()
    {
        var (h, c, s) = Make();
        using var _ = h;
        s.Chest(3, 65, 2, I(Blocks.Sapling, 1), I(Blocks.Dirt, 1));
        var o = new Obtainer(c);
        var r = await o.ObtainAsync(Blocks.Sapling, -1, 1, default);
        Assert.False(r.Ok, r.Text);
        Assert.DoesNotContain("withdraw", Calls(c));

        c.Give(36, Blocks.Planks, 4);
        c.ReservedItems = new HashSet<int> { Blocks.Planks };
        s.Chest(-3, 65, 3, I(Blocks.Planks, 4));
        var sticks = await h.Run("craft", new { item = "stick", count = 4 });
        Assert.True(sticks.Ok, sticks.Text);
        Assert.Contains($"withdraw {Blocks.Planks} 2 -3,65,3", Calls(c));     // the carried (reserved) planks stay out of it
    }

    [Fact]
    public async Task A_clone_body_obtains_through_the_same_resolver_with_its_own_inventory()
    {
        var (h, mainC, s) = Make();
        using var _ = h;
        var cc = new FakeGameClient { Username = "maid_chan_67" };
        cc.Floor(64, 12, Blocks.Grass);
        cc.World.SetBlock(-2, 65, -2, Blocks.Workbench, 0);
        cc.World.SetBlock(3, 65, 2, Blocks.Chest, 0);
        Real(cc, s);
        var clone = new Body(cc.Username, isMain: false, cc, cc, new ActivityManager(h.Runtime.Tools, h.Runtime.Events));
        h.Bodies!.Add(clone);
        BodyFactory.AttachCloneAcquisition(h.Brain!, clone);
        s.Chest(3, 65, 2, I(Blocks.Log, 4), I(Blocks.Cobblestone, 64));

        ToolResult r;
        using (BodyScope.Push(clone)) r = await h.Run(AcquireTool.ToolName, new { what = "pickaxe" });
        Assert.True(r.Ok, r.Text + " | " + Calls(cc));
        Assert.Equal(1, Inv(cc, Items.StonePickaxe));
        Assert.Equal(0, Inv(mainC, Items.StonePickaxe));
        Assert.DoesNotContain("withdraw", Calls(mainC));
        Assert.Contains($"withdraw {Blocks.Cobblestone} 3 3,65,2 dmg=0", cc.CallList);
        Assert.Equal(61, s.Chests[new BlockPos(3, 65, 2)].Single(i => i.ItemId == Blocks.Cobblestone).Count); // the shared memory

        using (BodyScope.Push(clone)) r = await h.Run("craft", new { item = "20 cobble slabs" });
        Assert.True(r.Ok, r.Text);
        Assert.True(Inv(cc, Blocks.Slab, 3) >= 20);
    }
}
