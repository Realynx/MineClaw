using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28 13:25: main hit food 1, then 0 ("starving: food 1, no food in inventory"), said "no bread in my inventory or any
/// known chest" and sent clone 1337 for bread, while 420 had stored 30 wheat in the house chest at 06:02 (10 bread by
/// crafting). Food now resolves through the shared resolver: inventory → chest food → craft (bread from chest wheat) → cook,
/// from the eat tool, scripts' eat/keepFed, and the hunger danger as a zero-token reflex. Fake chests are real: a withdraw
/// moves remembered items into the inventory, a craft consumes what the recipe tree uses, eating removes one food item.
/// </summary>
public class FoodSupplyTests
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

    private static ItemCount I(int id, int n) => new(id, Items.Get(id).Name, n, 0);

    private static (BotHost H, FakeGameClient C, Store S) Make(int food = 1)
    {
        var (h, c) = TestBot.Create();
        c.Floor(64, 12, Blocks.Grass);
        c.World.SetBlock(-2, 65, -2, Blocks.Workbench, 0); // the house's table
        c.World.Player.SetHealth(20, food, 0);
        var s = new Store(h);
        Real(c, s);
        return (h, c, s);
    }

    private static void Real(FakeGameClient c, Store s)
    {
        c.Effects["withdraw"] = () =>
        {
            var parts = c.CallList.Last(x => x.StartsWith("withdraw ", StringComparison.Ordinal)).Split(' ');
            int id = int.Parse(parts[1]), n = int.Parse(parts[2]);
            var xyz = parts[3].Split(',').Select(int.Parse).ToArray();
            var p = new BlockPos(xyz[0], xyz[1], xyz[2]);
            if (!s.Chests.TryGetValue(p, out var items)) return;
            for (int i = 0; i < items.Count && n > 0; i++)
            {
                var it = items[i];
                if (it.ItemId != id) continue;
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
            foreach (var u in plan.Used) Take(c, u.Id, u.Count);
            var last = plan.Steps[^1];
            Put(c, last.OutputId, last.OutputDamage, last.OutputCount);
            made = last.OutputCount;
        };
        c.Results["craft"] = () => made > 0 ? ActionResult.Success($"crafted {made}", made) : ActionResult.Fail("missing ingredients");
        c.Effects["smelt"] = () =>
        {
            var parts = c.CallList.Last(x => x.StartsWith("smelt ", StringComparison.Ordinal)).Split(' ');
            int id = int.Parse(parts[1]), n = int.Parse(parts[2]);
            Take(c, id, n);
            Put(c, Smelting.ResultFor(id)!.OutputId, 0, n);
        };
        string ate = "nothing";
        c.Effects["eat"] = () =>
        {
            var pick = MineClaw.Client.Survival.SurvivalReflex.PickFood(c.World.Inventory.PlayerSlots(), c.EatReservedItems,
                c.World.Player.Health, c.World.Player.Food, new MineClaw.Client.Survival.SurvivalOptions());
            if (pick is not { } f) { ate = "nothing"; return; }
            Take(c, f.ItemId, 1);
            c.World.Player.SetHealth(c.World.Player.Health, Math.Min(20, c.World.Player.Food + f.Hunger), 0);
            ate = Items.Get(f.ItemId).Name;
        };
        c.Results["eat"] = () => ate == "nothing" ? ActionResult.Fail("no food (or only reserved food)") : ActionResult.Success($"ate {ate} (food {c.World.Player.Food}/20)");
    }

    private static void Put(FakeGameClient c, int id, int dmg, int n)
    {
        int max = Math.Max(1, (int)Items.Get(id).MaxStack);
        for (int s = 9; s < 45 && n > 0; s++)
        {
            var st = c.World.Inventory.PlayerSlots()[s];
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

    private static void Take(FakeGameClient c, int id, int n)
    {
        for (int s = 9; s < 45 && n > 0; s++)
        {
            var st = c.World.Inventory.PlayerSlots()[s];
            if (st.IsEmpty || st.Id != id) continue;
            int k = Math.Min(n, (int)st.Count);
            if (k == st.Count) c.World.Inventory.SetSlot(0, s, ItemStack.Empty); else c.Give(s, id, st.Count - k, st.Damage);
            n -= k;
        }
    }

    private static int Inv(FakeGameClient c, int id) => c.World.Inventory.CountItem(id);
    private static string Calls(FakeGameClient c) => string.Join(" | ", c.CallList);

    [Fact]
    public async Task Food_1_with_wheat_in_a_chest_withdraws_crafts_bread_and_eats()
    {
        var (h, c, s) = Make(food: 1);
        using var _ = h;
        s.Chest(11, 65, -5, I(Items.Wheat, 30), I(Items.Seeds, 17)); // 420's 06:02 stash
        var look = new FoodSupply(c).Survey();
        Assert.Equal("no ready food; 30 wheat in chest 11,65,-5 → can craft 10 bread (eat will do it)", look.Text);

        var r = await h.Run("eat", new { });
        Assert.True(r.Ok, r.Text + " | " + Calls(c));
        var calls = c.CallList;
        int w = calls.FindIndex(x => x.StartsWith($"withdraw {Items.Wheat} 15 11,65,-5", StringComparison.Ordinal));
        int k = calls.FindIndex(x => x.StartsWith($"craft {Items.Bread} 5", StringComparison.Ordinal));
        int e = calls.IndexOf("eat");
        Assert.True(w >= 0 && k > w && e > k, Calls(c));           // walk + withdraw, craft, eat: in that order
        Assert.Equal(4, Inv(c, Items.Bread));                     // 5 crafted, one eaten
        Assert.Equal(6, c.World.Player.Food);
        Assert.Equal(15, s.Chests[new BlockPos(11, 65, -5)].Single(i => i.ItemId == Items.Wheat).Count);
        Assert.Contains("15 wheat from 11,65,-5", r.Text);
        Assert.Contains("ate bread", r.Text);
    }

    [Fact]
    public async Task The_hunger_danger_feeds_as_a_reflex_without_waking_the_brain()
    {
        var (h, c, s) = Make(food: 1);
        using var _ = h;
        s.Chest(11, 65, -5, I(Items.Wheat, 30));
        c.RaiseDanger(new DangerEvent(DangerKind.Hunger, "starving: food 1, no food in inventory (no sprint, no regen)") { Level = 2 });
        Assert.NotNull(h.Bridge.Feeding);
        await h.Bridge.Feeding!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("eat", c.CallList);
        Assert.Equal(4, Inv(c, Items.Bread));
        var events = h.Runtime.Events.Snapshot();
        Assert.Contains(events, e => e.Text.StartsWith("ate (reflex):", StringComparison.Ordinal) && e.Priority == EventPriority.Background);
        Assert.Contains(events, e => e.Text.Contains("30 wheat in chest 11,65,-5 → can craft 10 bread") && e.Priority == EventPriority.Background);
        Assert.DoesNotContain(events, e => e.Text.Contains("starving") && e.Priority > EventPriority.Background); // no brain turn
    }

    [Fact]
    public async Task With_no_food_anywhere_the_failure_names_the_shortfall()
    {
        var (h, c, s) = Make(food: 1);
        using var _ = h;
        s.Chest(11, 65, -5, I(Items.Wheat, 2), I(Items.Seeds, 17), I(Blocks.Cobblestone, 64));
        var r = await h.Run("eat", new { });
        Assert.False(r.Ok);
        Assert.Contains("no food anywhere: none in inventory or the 1 known chest", r.Text);
        Assert.Contains("only 2 wheat in chest 11,65,-5 (bread needs 3)", r.Text);
        Assert.Contains("17 seeds to plant", r.Text);
        Assert.DoesNotContain("eat", c.CallList);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("withdraw", StringComparison.Ordinal));

        // The danger reaches the brain with the same shortfall (nothing a reflex could do).
        c.RaiseDanger(new DangerEvent(DangerKind.Hunger, "starving: food 1, no food in inventory (no sprint, no regen)") { Level = 2 });
        var ev = Assert.Single(h.Runtime.Events.Snapshot(), e => e.Text.StartsWith("starving", StringComparison.Ordinal));
        Assert.Equal(EventPriority.Normal, ev.Priority);
        Assert.Contains("(bread needs 3)", ev.Text);
        Assert.Null(h.Bridge.Feeding);
    }

    [Fact]
    public async Task Reserved_bread_is_not_eaten_fetched_or_crafted()
    {
        var (h, c, s) = Make(food: 1);
        using var _ = h;
        c.ReservedItems = new HashSet<int> { Items.Bread };
        c.Give(36, Items.Bread, 3);
        s.Chest(11, 65, -5, I(Items.Bread, 5), I(Items.Wheat, 30));
        var r = await h.Run("eat", new { });
        Assert.False(r.Ok, r.Text);
        Assert.Contains("carried bread x3 is reserved", r.Text);
        Assert.Contains("bread is reserved", r.Text);
        Assert.DoesNotContain("eat", c.CallList);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("withdraw", StringComparison.Ordinal) || x.StartsWith("craft", StringComparison.Ordinal));
        Assert.Equal(3, Inv(c, Items.Bread));
    }

    [Fact]
    public async Task Bread_held_for_a_hand_off_stays_and_a_chest_copy_is_fetched_and_eaten()
    {
        var (h, c, s) = Make(food: 1);
        using var _ = h;
        c.Give(36, Items.Bread, 2);
        c.HandoffHolds[Items.Bread] = 2; // "bread for 67": both carried are held
        s.Chest(11, 65, -5, I(Items.Bread, 6));
        Assert.Equal("6 bread in chest 11,65,-5 (eat will fetch it)", new FoodSupply(c).Survey().Text);
        var r = await h.Run("eat", new { });
        Assert.True(r.Ok, r.Text + " | " + Calls(c));
        Assert.Contains($"withdraw {Items.Bread} 5 11,65,-5", Calls(c));
        Assert.Equal(6, Inv(c, Items.Bread)); // 2 held for 67 + 5 fetched - 1 eaten: the held two are still there
    }

    [Fact]
    public async Task Chest_food_comes_before_crafting_and_raw_meat_is_cooked_at_a_furnace_in_reach()
    {
        var (h, c, s) = Make(food: 10);
        using var _ = h;
        s.Chest(11, 65, -5, I(Items.CookedFish, 2), I(Items.Wheat, 30));
        var r = await h.Run("eat", new { });
        Assert.True(r.Ok, r.Text);
        Assert.Contains($"withdraw {Items.CookedFish} 2 11,65,-5", Calls(c));
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("craft", StringComparison.Ordinal));

        var (h2, c2, s2) = Make(food: 4);
        using var __ = h2;
        c2.World.SetBlock(2, 65, 2, Blocks.Furnace, 0);
        s2.Chest(11, 65, -5, I(Items.RawChicken, 3), I(Items.Coal, 4));
        Assert.Contains("3 raw chicken in chest 11,65,-5 → can cook 3 cooked chicken at the furnace 2,65,2", new FoodSupply(c2).Survey().Text);
        var r2 = await h2.Run("eat", new { });
        Assert.True(r2.Ok, r2.Text + " | " + Calls(c2));
        Assert.Contains($"smelt {Items.RawChicken} 3", Calls(c2));
        Assert.Equal(2, Inv(c2, Items.CookedChicken));
    }

    [Fact]
    public void Raw_chicken_without_a_furnace_is_reported_not_eaten()
    {
        var (h, c, s) = Make(food: 4);
        using var _ = h;
        s.Chest(11, 65, -5, I(Items.RawChicken, 3));
        var look = new FoodSupply(c).Survey();
        Assert.False(look.CanObtain);
        Assert.Contains("3 raw chicken in chest 11,65,-5, but no furnace in reach to cook it", look.Text);
    }
}
