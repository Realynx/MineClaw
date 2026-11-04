using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Bills at every station (docs/web/story-mode.md "Stations"): brewing stand, enchanting table and anvil recipes per version, the brewing
/// chain from water bottles, the anvil's XP prices, the enchanting offer choice and bookshelves, and the colony's jobs: gathering,
/// filling bottles, XP levels picking the pawn, suspension reasons, a worn-through anvil, the tools hook.
/// </summary>
public class StoryStationTests
{
    private static readonly IGameData Modern = Release1122GameData.Instance;

    private static void Only(StoryColony c, int i, params string[] types)
    {
        var p = c.Pawns.Values.ElementAt(i);
        foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static void Xp(FakeStoryHost h, StoryColony c, int i, int level) => h.Lives[c.P(i)] = h.Lives[c.P(i)] with { XpLevel = level };

    private static string Bill(StoryColony c, int x, JsonObject bill)
    {
        var r = c.BillsOp("add", new JsonObject { ["bench"] = new JsonArray(x, 64, 0), ["bill"] = bill });
        Assert.Null(r.Error);
        return r.Result!["id"]!.GetValue<string>();
    }

    private static StorageState Chest(StoryColony c, int x, int z, params Stack[] items)
    {
        c.StorageOp("set", [new P3(x, 64, z)], null, "normal", null);
        var s = c.StorageAt(new P3(x, 64, z))!;
        foreach (var it in items) c.AddContents(s, it);
        return s;
    }

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();

    // ================================================================ recipes

    [Fact]
    public void Each_station_lists_its_recipes_on_1122_and_none_on_b181()
    {
        foreach (var bench in StoryRecipes.Stations)
        {
            Assert.Empty(StoryRecipes.For(Beta181GameData.Instance, bench));
            Assert.False(StoryRecipes.SupportedIn(Beta181GameData.Instance, bench));
            Assert.True(StoryRecipes.SupportedIn(Modern, bench));
            Assert.NotEmpty(StoryRecipes.For(Modern, bench));
        }
        var brew = StoryRecipes.For(Modern, "brewing_stand").Select(r => r.Id).ToList();
        Assert.Contains("brew:awkward", brew);
        Assert.Contains("brew:swiftness", brew);
        Assert.Contains("brew:strong_swiftness", brew);
        Assert.Contains("brew:splash_healing", brew);
        Assert.Contains("brew:lingering_long_poison", brew);
        Assert.DoesNotContain("brew:splash_awkward", brew);
        Assert.DoesNotContain("brew:water", brew);
        Assert.All(StoryRecipes.For(Modern, "brewing_stand"), r => Assert.Equal(WorkTypes.Cook, r.WorkType));
        var swift = StoryRecipes.Get(Modern, "brew:swiftness")!;
        Assert.Equal(new Stack(Brewing.Potion, Brewing.TypeId("swiftness"), 3), swift.Output);
        Assert.Equal("Brew Potion of Swiftness", swift.Label);
        Assert.Equal("blaze_powder", swift.Json()["requires"]!["fuel_item"]!.GetValue<string>());
        Assert.Equal(2, swift.Json()["chain"]!.AsArray().Count);

        var ench = StoryRecipes.For(Modern, "enchanting_table").Select(r => r.Id).ToList();
        Assert.Contains("enchant:diamond_pickaxe", ench);
        Assert.Contains("enchant:bow", ench);
        Assert.Contains("enchant:book", ench);
        Assert.Contains("enchant:chainmail_boots", ench);
        Assert.DoesNotContain("enchant:diamond_hoe", ench); // a hoe takes enchantments only through an anvil
        var pick = StoryRecipes.Get(Modern, "enchant:diamond_pickaxe")!;
        Assert.Equal(WorkTypes.Craft, pick.WorkType);
        var prefers = pick.Json()["enchantments"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Contains("fortune", prefers);
        Assert.Contains("silk_touch", prefers);
        Assert.DoesNotContain("sharpness", prefers);
        Assert.DoesNotContain("mending", prefers); // treasure: never at the table
        Assert.Equal(["count", "forever"], pick.Json()["modes"]!.AsArray().Select(m => m!.GetValue<string>()));

        var anvil = StoryRecipes.For(Modern, "anvil").Select(r => r.Id).ToList();
        Assert.Contains("repair:iron_pickaxe", anvil);
        Assert.Contains("repair:chainmail_helmet", anvil);
        Assert.Contains("combine:bow", anvil);
        Assert.Contains("book:diamond_sword", anvil);
        Assert.DoesNotContain("repair:bow", anvil); // nothing mends a bow but another bow
        Assert.Contains("Repair Iron Pickaxe (iron ingot)", StoryRecipes.For(Modern, "anvil").Select(r => r.Label));
    }

    [Fact]
    public void The_benches_and_the_rest_of_the_interactive_blocks_are_explicit()
    {
        Assert.Equal(["crafting_table", "furnace", "brewing_stand", "anvil", "enchanting_table"], StoryRecipes.Benches);
        foreach (var other in new[] { "hopper", "dropper", "dispenser", "cauldron", "beacon", "jukebox", "noteblock" })
            Assert.True(StoryRecipes.NotBenches.ContainsKey(other), other);
        Assert.Equal("anvil", StoryRecipes.BenchOf(145));
        Assert.Null(StoryRecipes.BenchOf(118)); // a cauldron is water for brewing, not a bench
    }

    // ================================================================ brewing chain

    [Fact]
    public void A_brew_chain_runs_from_water_bottles_through_awkward_to_the_effect()
    {
        var chain = Brewing.Chain(Brewing.SplashPotion, "strong_swiftness")!;
        Assert.Equal(["nether_wart", "sugar", "glowstone_dust", "gunpowder"], chain.Select(s => s.With.Name));
        Assert.Equal(["water", "awkward", "swiftness", "strong_swiftness"], chain.Select(s => s.FromType));
        Assert.Equal(Brewing.SplashPotion, chain[^1].Form);
        Assert.Equal(["fermented_spider_eye"], Brewing.Chain(Brewing.Potion, "weakness")!.Select(s => s.With.Name)); // straight from water
        Assert.Equal(3, Brewing.Chain(Brewing.Potion, "slowness")!.Count); // awkward → leaping|swiftness → corrupted
        var lingering = Brewing.Chain(Brewing.LingeringPotion, "healing")!;
        Assert.Equal(["nether_wart", "speckled_melon", "gunpowder", "dragon_breath"], lingering.Select(s => s.With.Name));
        Assert.Null(Brewing.Chain(Brewing.Potion, "water")); // filled, not brewed
        Assert.Null(Brewing.Chain(Brewing.Potion, "luck")); // no mix makes it
        Assert.Equal("pufferfish", Brewing.Chain(Brewing.Potion, "water_breathing")![1].With.Name);
        Assert.Equal(3, Brewing.Chain(Brewing.Potion, "water_breathing")![1].With.Meta);
        Assert.Equal("Splash Potion of Swiftness II", Brewing.Label(Brewing.SplashPotion, "strong_swiftness"));
        Assert.Equal("Potion of Fire Resistance (extended)", Brewing.Label(Brewing.Potion, "long_fire_resistance"));
        Assert.Equal("Awkward Potion", Brewing.Label(Brewing.Potion, "awkward"));
        Assert.Equal(14, Brewing.TypeId("minecraft:swiftness"));
    }

    // ================================================================ anvil prices

    [Fact]
    public void Anvil_prices_follow_the_games_rules()
    {
        // An iron pickaxe (250) damaged 200: a quarter (62) per unit, so 4 units; with 2 units 76 is left.
        Assert.Equal((4, 0), Anvil.Repair(250, 200, 9));
        Assert.Equal((2, 76), Anvil.Repair(250, 200, 2));
        Assert.Equal(4, Anvil.UnitsFor(250, 200));
        Assert.Equal(7, Anvil.RepairCost(4, priorWork: 3));
        Assert.Equal(("iron_ingot", 265), (Anvil.RepairMaterial("chainmail_boots")!.Value.Name, Anvil.RepairMaterial("chainmail_boots")!.Value.Id));
        Assert.Equal(-1, Anvil.RepairMaterial("wooden_sword")!.Value.Meta); // any planks
        Assert.Null(Anvil.RepairMaterial("bow"));

        var none = new Dictionary<int, int>();
        // Two worn pickaxes: the merge mends (2), plus Efficiency II from the right (common ×1 × 2); equal levels go up one.
        Assert.Equal(2, Anvil.CombineCost("iron_pickaxe", 250, 100, 100, none, none));
        Assert.Equal(4, Anvil.CombineCost("iron_pickaxe", 250, 100, 100, none, new Dictionary<int, int> { [32] = 2 }));
        Assert.Equal(3, Anvil.CombineCost("iron_pickaxe", 250, 0, 0, new Dictionary<int, int> { [32] = 2 }, new Dictionary<int, int> { [32] = 2 }));
        // A book halves the multiplier: Sharpness III (common) 1 × 3; Fortune III (rare ×4 → 2) 2 × 3; prior work adds.
        Assert.Equal(3, Anvil.BookCost("diamond_sword", none, new Dictionary<int, int> { [16] = 3 }));
        Assert.Equal(6 + 1 + 3, Anvil.BookCost("diamond_pickaxe", none, new Dictionary<int, int> { [35] = 3 }, leftWork: 1, bookWork: 3));
        // Fortune onto a Silk Touch pickaxe: a conflict (1) and nothing carried over.
        var conflict = Anvil.EnchantCost("diamond_pickaxe", new Dictionary<int, int> { [33] = 1 }, new Dictionary<int, int> { [35] = 3 }, fromBook: true);
        Assert.Equal((1, false), conflict);
        Assert.Equal(1, Anvil.NextWork(0, 0));
        Assert.Equal(7, Anvil.NextWork(3, 1));
        Assert.Equal("chipped", Anvil.Condition(4 | 2));
        Assert.Equal("damaged", Anvil.Condition(8));
    }

    // ================================================================ enchanting

    [Fact]
    public void The_bill_takes_the_preferred_offer_else_the_highest_it_can_pay()
    {
        Enchanting.Offer[] offers = [new(0, 3, 34, 1), new(1, 9, 32, 2), new(2, 18, 35, 2)]; // Unbreaking I, Efficiency II, Fortune II
        int? fortune = Enchanting.ByName("Fortune")!.Id;
        Assert.Equal(2, Enchanting.Choose(offers, 30, 3, 3, fortune, false).Pick!.Slot);
        Assert.Equal(0, Enchanting.Choose(offers, 30, 3, 3, Enchanting.ByName("unbreaking")!.Id, false).Pick!.Slot);
        Assert.Equal(2, Enchanting.Choose(offers, 30, 3, 3, null, false).Pick!.Slot);
        Assert.Equal(1, Enchanting.Choose(offers, 10, 3, 3, null, false).Pick!.Slot); // level 10: offer 3 needs 18
        Assert.Equal(0, Enchanting.Choose(offers, 30, 1, 3, null, false).Pick!.Slot); // one lapis
        Assert.Equal(1, Enchanting.Choose(offers, 30, 3, 2, null, false).Pick!.Slot); // the bill's option caps it
        Assert.Equal(1, Enchanting.Choose(offers, 30, 3, 2, fortune, false).Pick!.Slot); // fortune is offer 3: the highest within the option
        var (none, why) = Enchanting.Choose(offers, 30, 3, 2, fortune, true);
        Assert.Null(none);
        Assert.StartsWith("no offer shows Fortune", why);
        var (poor, needs) = Enchanting.Choose(offers, 2, 3, 3, null, false);
        Assert.Null(poor);
        Assert.Equal("needs 3 XP levels (has 2)", needs);
        Assert.Equal("the Fortune offer needs 18 XP levels (has 10)", Enchanting.Choose(offers, 10, 3, 3, fortune, true).Why);
        Assert.True(Enchanting.Fits(Enchanting.ByName("silk touch")!, "iron_shovel"));
        Assert.False(Enchanting.Fits(Enchanting.ByName("sharpness")!, "iron_axe"));
        Assert.True(Enchanting.Fits(Enchanting.ByName("sharpness")!, "iron_axe", anvil: true));
    }

    [Fact]
    public void Bookshelves_count_by_the_tables_walk()
    {
        var blocks = new Dictionary<(int, int, int), int>();
        int At(int x, int y, int z) => blocks.GetValueOrDefault((x, y, z));
        Assert.Equal(0, Enchanting.Bookshelves(At, 0, 64, 0));
        blocks[(2, 64, 0)] = Enchanting.Bookshelf;
        Assert.Equal(1, Enchanting.Bookshelves(At, 0, 64, 0));
        blocks[(1, 65, 0)] = 1; // something between: that side counts nothing
        Assert.Equal(0, Enchanting.Bookshelves(At, 0, 64, 0));
        blocks.Remove((1, 65, 0));
        for (int dx = -2; dx <= 2; dx++)
            for (int dz = -2; dz <= 2; dz++)
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) == 2)
                    for (int y = 64; y <= 65; y++) blocks[(dx, y, dz)] = Enchanting.Bookshelf;
        Assert.Equal(15, Enchanting.Bookshelves(At, 0, 64, 0)); // capped
        Assert.Equal((30, 30), Enchanting.TopOffer(15));
        Assert.Equal((1, 8), Enchanting.TopOffer(0));
    }

    // ================================================================ the colony's brewing

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
        public void Pass(int minutes = 20) => Now += TimeSpan.FromMinutes(minutes);
    }

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Brewery(FakeClock? clock = null)
    {
        var (c, h, w) = StoryRig.Make(pawns: 1, data: Modern, time: clock);
        w.Set(3, 64, 0, Brewing.StandBlock);
        w.Set(5, 64, 5, Brewing.WaterSource);
        Chest(c, 3, 2, new Stack(Brewing.GlassBottle, 0, 6), new Stack(Brewing.NetherWart, 0, 2), new Stack(353, 0, 2), new Stack(Brewing.BlazePowder, 0, 2));
        Only(c, 0, WorkTypes.Cook);
        return (c, h, w);
    }

    [Fact]
    public void A_brew_bill_fills_bottles_brings_the_chain_and_stores_the_potions()
    {
        var (c, h, w) = Brewery();
        Bill(c, 3, new JsonObject { ["recipe"] = "brew:swiftness", ["mode"] = "count", ["target"] = 3 });
        var j = c.Next();
        Assert.Equal("brew", Kind(j));
        Assert.Equal("swiftness", j["potion"]!.GetValue<string>());
        Assert.Equal("potion", j["form"]!.GetValue<string>());
        Assert.Equal(3, j["n"]!.GetValue<int>());
        Assert.Equal(2, j["steps"]!.GetValue<int>());
        Assert.Equal("5,64,5", j["fill"]!["at"]!.GetValue<string>());
        Assert.Equal(3, j["fill"]!["n"]!.GetValue<int>());
        var fetched = j["fetch"]!.AsArray().Select(f => (f!["item"]!.GetValue<string>(), f["n"]!.GetValue<int>())).ToList();
        Assert.Equal([("glass_bottle", 3), ("nether_wart", 1), ("sugar", 1), ("blaze_powder", 1)], fetched);
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", "brewed 3 Potion of Swiftness", null);
        var bench = c.Benches.Single();
        Assert.Equal(3, bench.Bills.Single().Done);
        Assert.Equal("brewed 3 Potion of Swiftness", bench.Bills.Single().Last);
        Assert.Equal(18, bench.Fuel); // one powder (20) less two brews
        Assert.Equal(3, c.CountItem(Brewing.Potion, Brewing.TypeId("swiftness")));
        Assert.NotEqual("brew", Kind(c.Next())); // done
    }

    [Fact]
    public void A_brew_starts_from_the_furthest_stage_the_pawn_holds_and_needs_no_powder_when_fueled()
    {
        var (c, h, w) = Brewery();
        h.Lives[c.P()] = h.Lives[c.P()] with { Inventory = [new Stack(Brewing.Potion, Brewing.TypeId("awkward"), 2)] };
        c.Benches.Add(new WorkbenchState { Pos = new P3(3, 64, 0), Kind = "brewing_stand", Fuel = 10 });
        Bill(c, 3, new JsonObject { ["recipe"] = "brew:swiftness", ["mode"] = "until", ["target"] = 5 });
        var j = c.Next();
        Assert.Equal("brew", Kind(j));
        Assert.Equal(2, j["n"]!.GetValue<int>()); // the two awkward potions it holds
        Assert.Equal(1, j["steps"]!.GetValue<int>()); // sugar only
        Assert.Null(j["fill"]);
        Assert.Equal(["sugar"], j["fetch"]!.AsArray().Select(f => f!["item"]!.GetValue<string>()));
    }

    [Fact]
    public void Missing_water_ingredients_or_known_short_fuel_suspend_the_brew_with_why()
    {
        var clock = new FakeClock();
        var (c, h, w) = Brewery(clock);
        w.Set(5, 64, 5, 0);
        var id = Bill(c, 3, new JsonObject { ["recipe"] = "brew:swiftness", ["target"] = 3 });
        Assert.NotEqual("brew", Kind(c.Next()));
        var bill = c.Benches.Single().Bills.Single();
        Assert.StartsWith("no water", bill.SuspendedReason);
        Assert.Contains(c.Alerts, a => a.Text.Contains("no water"));

        w.Set(5, 64, 5, Brewing.CauldronBlock, 3); // a filled cauldron does
        clock.Pass(); // the stand's water is looked up once a minute
        c.BillsOp("resume", new JsonObject { ["id"] = id });
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        var j = c.Next();
        Assert.Equal("5,64,5", j["fill"]!["at"]!.GetValue<string>());
        c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", "something else", null);

        var strong = Bill(c, 3, new JsonObject { ["recipe"] = "brew:strong_strength", ["target"] = 3 }); // needs glowstone: none stored
        c.BillsOp("remove", new JsonObject { ["id"] = id });
        clock.Pass(); // the failed stand rests a while
        c.Report(c.P(), c.CurrentOf(c.P())?.Id ?? "", "done", null, null);
        Assert.NotEqual("brew", Kind(c.Next()));
        Assert.Equal("no glowstone dust within 32 blocks", c.Benches.Single().Bills.Single(b => b.Id == strong).SuspendedReason);
    }

    [Fact]
    public void Brewing_and_the_other_stations_take_no_bills_on_b181()
    {
        var (c, h, w) = StoryRig.Make(pawns: 1);
        w.Set(3, 64, 0, Brewing.StandBlock);
        var r = c.BillsOp("add", new JsonObject { ["bench"] = new JsonArray(3, 64, 0), ["bill"] = new JsonObject { ["recipe"] = "brew:swiftness" } });
        Assert.Contains("b1.8.1 has no brewing stand bills", r.Error);
        Assert.False(c.State()["workbenches"]!.AsArray().Single()!["supported"]!.GetValue<bool>());
    }

    // ================================================================ the colony's enchanting

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Library(FakeClock? clock = null, int pawns = 2)
    {
        var (c, h, w) = StoryRig.Make(pawns: pawns, data: Modern, time: clock);
        foreach (var p in c.Pawns.Keys) h.Lives[p] = FakeStoryHost.Bare(0, 64, 0); // no pickaxe of their own: the chest's diamond ones only
        w.Set(3, 64, 0, Enchanting.TableBlock);
        w.Set(5, 64, 0, Enchanting.Bookshelf);
        w.Set(5, 65, 0, Enchanting.Bookshelf);
        Chest(c, 3, 2, new Stack(278, 0, 2), new Stack(Enchanting.Lapis, Enchanting.LapisMeta, 9));
        for (int i = 0; i < pawns; i++) Only(c, i, WorkTypes.Craft);
        return (c, h, w);
    }

    [Fact]
    public void An_enchant_bill_goes_to_a_pawn_with_the_levels_and_brings_item_and_lapis()
    {
        var clock = new FakeClock();
        var (c, h, w) = Library(clock);
        Xp(h, c, 1, 30);
        var id = Bill(c, 3, new JsonObject { ["recipe"] = "enchant:diamond_pickaxe", ["target"] = 1, ["option"] = 3, ["prefer"] = "Fortune" });
        Assert.NotEqual("enchant", Kind(c.Next(0))); // level 0: the other pawn has the levels
        var j = c.Next(1);
        Assert.Equal("enchant", Kind(j));
        Assert.Equal("diamond_pickaxe", j["item"]!.GetValue<string>());
        Assert.Equal(3, j["option"]!.GetValue<int>());
        Assert.Equal("fortune", j["prefer"]!.GetValue<string>());
        Assert.Equal([("diamond_pickaxe", 1), ("lapis_lazuli", 3)], j["fetch"]!.AsArray().Select(f => (f!["item"]!.GetValue<string>(), f["n"]!.GetValue<int>())));
        Assert.Null(j["stash"]); // a tool stays with the pawn unless the bill names a chest

        // The offer needed more than it had: the bill learns the level and waits for a pawn that has it.
        c.Report(c.P(1), j["id"]!.GetValue<string>(), "failed", "needs 33 XP levels (has 30)", null);
        var bill = c.Benches.Single().Bills.Single();
        Assert.Equal(33, bill.XpNeeded);
        Assert.Null(bill.SuspendedReason); // not a failure of the bill
        clock.Pass();
        Assert.NotEqual("enchant", Kind(c.Next(1)));
        Assert.Equal("needs 33 XP levels: no pawn has them (best: " + c.Pawns[c.P(1)].Display + " 30)", bill.SuspendedReason);

        // Bookshelves on the inspector.
        var station = c.State()["workbenches"]!.AsArray().Single()!["station"]!;
        Assert.Equal(2, station["bookshelves"]!.GetValue<int>());
        Assert.Equal(15, station["max_bookshelves"]!.GetValue<int>());
        Assert.Null(c.BillsOp("remove", new JsonObject { ["id"] = id }).Error);
    }

    [Fact]
    public void Enchant_bills_take_count_or_forever_and_validate_the_preference()
    {
        var (c, h, w) = Library();
        var add = (JsonObject b) => c.BillsOp("add", new JsonObject { ["bench"] = new JsonArray(3, 64, 0), ["bill"] = b }).Error;
        Assert.StartsWith("mode must be count|forever", add(new JsonObject { ["recipe"] = "enchant:diamond_pickaxe", ["mode"] = "until" }));
        Assert.Equal("unknown enchantment 'luckiness'", add(new JsonObject { ["recipe"] = "enchant:diamond_pickaxe", ["prefer"] = "luckiness" }));
        Assert.Equal("Sharpness doesn't go on diamond pickaxe at a table", add(new JsonObject { ["recipe"] = "enchant:diamond_pickaxe", ["prefer"] = "sharpness" }));
        Assert.Null(add(new JsonObject { ["recipe"] = "enchant:book", ["prefer"] = "sharpness" }));
    }

    [Fact]
    public void A_prefer_only_bill_no_pawns_table_shows_waits_with_why()
    {
        var clock = new FakeClock();
        var (c, h, w) = Library(clock);
        Xp(h, c, 0, 30);
        Xp(h, c, 1, 30);
        Bill(c, 3, new JsonObject { ["recipe"] = "enchant:diamond_pickaxe", ["prefer"] = "silk_touch", ["prefer_only"] = true });
        var j0 = c.Next(0);
        Assert.Equal("enchant", Kind(j0));
        Assert.True(j0["prefer_only"]!.GetValue<bool>());
        c.Report(c.P(0), j0["id"]!.GetValue<string>(), "failed", "no offer shows Silk Touch (#1: level 3)", null);
        clock.Pass();
        var j1 = c.Next(1);
        Assert.Equal("enchant", Kind(j1)); // the other pawn's offers differ
        c.Report(c.P(1), j1["id"]!.GetValue<string>(), "failed", "no offer shows Silk Touch (#1: level 3)", null);
        clock.Pass();
        c.Report(c.P(0), c.CurrentOf(c.P(0))?.Id ?? "", "done", null, null);
        Assert.NotEqual("enchant", Kind(c.Next(0)));
        Assert.StartsWith("no pawn's table shows Silk Touch", c.Benches.Single().Bills.Single().SuspendedReason);
    }

    [Fact]
    public void The_tools_hook_adds_enchant_bills_once()
    {
        var (c, h, w) = Library();
        Assert.Equal("no enchanting table: place one and designate it", c.EnchantTools("fortune").Error);
        Assert.Null(c.WorkbenchOp("designate", new P3(3, 64, 0)).Error);
        var (ids, err) = c.EnchantTools("fortune");
        Assert.Null(err);
        var bill = c.Benches.Single().Bills.Single(b => b.Id == ids.Single());
        Assert.Equal(("enchant:diamond_pickaxe", "fortune", 3), (bill.Recipe, bill.Prefer, bill.Option));
        Assert.Empty(c.EnchantTools("fortune").Ids); // not twice
        var r = c.BillsOp("enchant_tools", new JsonObject { ["material"] = "glass", ["items"] = new JsonArray("diamond_shovel") });
        Assert.Null(r.Error);
        Assert.Equal("silk_touch", c.Benches.Single().Bills.Last().Prefer);
        Assert.Equal("fortune", StoryEnchanting.PreferFor("diamond_ore"));
        Assert.Equal("efficiency", StoryEnchanting.PreferFor("stone"));
    }

    // ================================================================ the colony's anvil

    [Fact]
    public void A_repair_bill_brings_the_material_needs_the_levels_and_waits_when_the_anvil_is_gone()
    {
        var (c, h, w) = StoryRig.Make(pawns: 1, data: Modern);
        w.Set(3, 64, 0, Anvil.Block, 4); // chipped
        Chest(c, 3, 2, new Stack(265, 0, 2));
        Only(c, 0, WorkTypes.Craft);
        h.Lives[c.P()] = h.Lives[c.P()] with { Inventory = [new Stack(257, 200, 1)] }; // an iron pickaxe, 200 of 250 used
        var id = Bill(c, 3, new JsonObject { ["recipe"] = "repair:iron_pickaxe", ["target"] = 1 });
        Assert.NotEqual("anvil", Kind(c.Next()));
        var bill = c.Benches.Single().Bills.Single();
        Assert.StartsWith("needs 2 XP levels: no pawn has them", bill.SuspendedReason); // two ingots: two levels at least
        Assert.Equal("chipped", c.State()["workbenches"]!.AsArray().Single()!["station"]!["condition"]!.GetValue<string>());

        Xp(h, c, 0, 5);
        c.BillsOp("resume", new JsonObject { ["id"] = id });
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        var j = c.Next();
        Assert.Equal("anvil", Kind(j));
        Assert.Equal(("iron_pickaxe", "repair"), (j["item"]!.GetValue<string>(), j["mode"]!.GetValue<string>()));
        Assert.Equal([("iron_ingot", 2)], j["fetch"]!.AsArray().Select(f => (f!["item"]!.GetValue<string>(), f["n"]!.GetValue<int>())));
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", "repaired the iron pickaxe with 2 iron ingot for 2 levels; the anvil broke", null);
        Assert.Equal(1, bill.Done);

        w.Set(3, 64, 0, 0);
        Bill(c, 3, new JsonObject { ["recipe"] = "combine:iron_pickaxe", ["target"] = 1 });
        Assert.NotEqual("anvil", Kind(c.Next()));
        Assert.All(c.Benches.Single().Bills, b => Assert.StartsWith("the anvil at 3,64,0 is gone", b.SuspendedReason));
        Assert.Contains(c.Alerts, a => a.Text.StartsWith("The anvil at 3,64,0 is gone"));
    }

    [Fact]
    public void Pawns_show_their_xp_level_and_the_architect_counts_every_station()
    {
        var (c, h, w) = StoryRig.Make(pawns: 1, data: Modern);
        Assert.Empty(c.Benches);
        var pawn = c.P();
        var state = c.State();
        Assert.Equal(0, state["pawns"]!.AsArray().Single()!["xp_level"]!.GetValue<int>());
        Xp(h, c, 0, 12);
        Assert.Equal(12, c.State()["pawns"]!.AsArray().Single()!["xp_level"]!.GetValue<int>());
        var arch = c.Architect();
        Assert.True(arch["benches"]!.AsObject().ContainsKey("enchanting_table"));
        var brewingStand = arch["entries"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == "brewing_stand")!;
        Assert.Equal("workbench", brewingStand["tier"]!.GetValue<string>()); // 3 cobblestone and a blaze rod at a crafting table
        Assert.False(brewingStand["unlocked"]!.GetValue<bool>());
        Assert.Equal(pawn, c.P());
    }
}
