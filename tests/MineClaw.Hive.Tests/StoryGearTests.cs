using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>Tools, gear and drafted combat (docs/web/story-mode.md "Tools and gear", "Drafted combat").</summary>
public sealed class StoryGearTests
{
    private static readonly P3 Chest = new(0, 64, 9), Table = new(3, 64, -3);

    private static void Only(StoryColony c, int i, params string[] types)
    {
        var p = c.Pawns.Values.ElementAt(i);
        foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static string? S(JsonNode? n) => n?.GetValue<string>();
    private static void Done(StoryColony c, int pawn, JsonObject job) => Assert.True(c.Report(c.P(pawn), job["id"]!.GetValue<string>(), "done", null, null));

    private static void Store(StoryColony c, params Stack[] stock)
    {
        if (c.StorageAt(Chest) == null) Assert.Null(c.StorageOp("set", [Chest], null, null, null).Error);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
    }

    private static int Stored(StoryColony c, int id) => c.StorageAt(Chest)!.Contents.Where(s => s.Id == id).Sum(s => s.Count);

    private static string Name(FakeStoryWorld w, int id) => w.Data.Item(id).Name;

    // ================================================================ the tool a material takes

    [Fact]
    public void Iron_ore_is_never_handed_to_a_pawn_with_only_a_wooden_pickaxe_it_waits_with_a_reason_and_no_failure()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, 0, WorkTypes.Mine);
        w.Set(3, 64, 3, 15); // iron ore
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(270, 0, 1)); // a wooden pickaxe only
        Assert.Equal(1, c.Designate("add", "mine", null, [new P3(3, 64, 3)], null).Result!["count"]!.GetValue<int>());
        var j = c.Next(0);
        Assert.NotEqual("dig", Kind(j));
        var d = c.Designations.Single();
        Assert.Equal("needs a stone pickaxe", d.Wait);
        Assert.Equal(0, d.Fails);
        Assert.Equal("open", d.Status);
        Assert.Equal("needs a stone pickaxe", S(c.State()["designations"]![0]!["wait"]));
        Assert.Empty(c.Alerts.Where(a => a.Text.Contains("failed")));
    }

    [Fact]
    public void A_pawn_without_the_right_pickaxe_fetches_a_stored_one_with_the_job()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, 0, WorkTypes.Mine);
        w.Set(3, 64, 3, 15);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(270, 0, 1));
        Store(c, new Stack(274, 0, 1)); // a stone pickaxe in storage
        c.Designate("add", "mine", null, [new P3(3, 64, 3)], null);
        var j = c.Next(0);
        Assert.Equal("dig", Kind(j));
        Assert.Equal(Name(w, 274), S(j["tool"]));
        Assert.Equal("none", S(j["tool_ench"]));
        var f = j["fetch"]!.AsArray().Single()!;
        Assert.Equal(Name(w, 274), S(f["item"]));
        Assert.Equal(Chest.ToString(), S(f["from"]));
        Assert.Equal("none", S(f["ench"]));
        Assert.Null(c.Designations.Single().Wait);
        w.Set(3, 64, 3, 0); // dug
        Done(c, 0, j);
        Assert.Equal(0, Stored(c, 274)); // the pickaxe left the chest's record
        Assert.Empty(c.Designations);
    }

    [Fact]
    public void A_missing_tool_gets_an_auto_bill_for_tools_on_the_colony_table()
    {
        var (c, h, w) = StoryRig.Make(2);
        Only(c, 0, WorkTypes.Mine);
        Only(c, 1, WorkTypes.Craft);
        w.Set(3, 64, 3, 15);
        w.Set(Table.X, Table.Y, Table.Z, 58);
        Assert.Null(c.WorkbenchOp("designate", Table).Error);
        h.Lives[c.P(0)] = FakeStoryHost.Bare(0, 64, 0, new Stack(270, 0, 1));
        h.Lives[c.P(1)] = FakeStoryHost.Bare(1, 64, 0);
        Store(c, new Stack(4, 0, 3), new Stack(280, 0, 2)); // cobblestone and sticks
        c.Designate("add", "mine", null, [new P3(3, 64, 3)], null);
        Assert.NotEqual("dig", Kind(c.Next(0)));
        c.State(); // the materials pass
        var bill = c.Benches.Single().Bills.Single();
        Assert.Equal(StoryColony.ToolsTag, bill.ForBuild);
        Assert.Equal(Name(w, 274), bill.ForItem);
        Assert.Equal("craft:" + Name(w, 274), bill.Recipe);
        Assert.Equal("tools", S(c.State()["workbenches"]![0]!["bills"]![0]!["for_build"]));
        var craft = c.Next(1);
        Assert.Equal("craft", Kind(craft));
        // Cancelled by the owner: that tool isn't made by itself again.
        Assert.Null(c.BillsOp("remove", new JsonObject { ["id"] = bill.Id }).Error);
        c.State();
        Assert.Empty(c.Benches.Single().Bills);
        Assert.Contains(Name(w, 274), c.Meta.Gear.NoCraft);
    }

    [Fact]
    public void A_worn_tool_is_swapped_for_a_fresh_stored_one()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, 0, WorkTypes.Mine);
        w.Set(3, 64, 3, 15);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 130, 1)); // 1 of 131 uses left
        Store(c, new Stack(274, 0, 1));
        c.Designate("add", "mine", null, [new P3(3, 64, 3)], null);
        var j = c.Next(0);
        Assert.Contains(Kind(j), new[] { "dig", "equip" }); // its policy (tools for mining) gets there first, else the dig fetches it
        Assert.Equal(Name(w, 274), S(j["fetch"]![0]!["item"]));
        Assert.Equal(0, j["fetch"]![0]!["ench"] is { } e && S(e) == "none" ? 0 : 1);
        // With no fresh one anywhere, the worn one is still better than losing the drop.
        var (c2, h2, w2) = StoryRig.Make(1);
        Only(c2, 0, WorkTypes.Mine);
        w2.Set(3, 64, 3, 15);
        h2.Lives[c2.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 130, 1));
        c2.Designate("add", "mine", null, [new P3(3, 64, 3)], null);
        var j2 = c2.Next(0);
        Assert.Equal("dig", Kind(j2));
        Assert.Null(j2["fetch"]);
    }

    [Fact]
    public void A_dig_cell_of_a_build_waits_for_its_tool()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, 0, WorkTypes.Build);
        w.Set(3, 64, 3, 15);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0);
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "remove", ["blocks"] = new JsonArray(new JsonArray(3, 64, 3)) }), null).Error);
        Assert.NotEqual("dig", Kind(c.Next(0)));
        var b = c.Builds.Single();
        Assert.Equal("waiting", b.Status);
        Assert.Equal("needs a stone pickaxe", b.Reason);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 0, 1));
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        var j = c.Next(0);
        Assert.Equal("dig", Kind(j));
        Assert.Equal(Name(w, 274), S(j["tool"]));
        Assert.NotEqual("waiting", b.Status);
    }

    // ================================================================ the owner's material rules

    [Fact]
    public void A_material_rule_can_never_go_below_the_version_floor()
    {
        var (c, _, _) = StoryRig.Make(1);
        JsonObject Rule(string material, string? tool = null, string? tier = null, string? enchant = null) => new()
        {
            ["op"] = "set", ["rule"] = new JsonObject { ["material"] = material, ["tool"] = tool, ["tier"] = tier, ["enchant"] = enchant },
        };
        Assert.Contains("needs at least a stone pickaxe", c.MaterialRulesOp(Rule("iron_ore", tier: "wood")).Error);
        Assert.Contains("needs at least a diamond pickaxe", c.MaterialRulesOp(Rule("obsidian", tier: "iron")).Error);
        Assert.Contains("can't harvest", c.MaterialRulesOp(Rule("iron_ore", tool: "shovel")).Error);
        Assert.Equal("b1.8.1 has no enchantments", c.MaterialRulesOp(Rule("iron_ore", enchant: "fortune")).Error);
        Assert.Contains("unknown material", c.MaterialRulesOp(Rule("unobtainium")).Error);
        // A group at its lowest member's floor is fine; each member keeps its own.
        var ok = c.MaterialRulesOp(Rule("group:ores", tier: "wood"));
        Assert.Null(ok.Error);
        Assert.Equal("wood", S(ok.Result!["rule"]!["floor"]));
        var ok2 = c.MaterialRulesOp(Rule("iron_ore", tier: "iron"));
        Assert.Null(ok2.Error);
        Assert.Equal("stone", S(ok2.Result!["rule"]!["floor"]));
        var st = c.State();
        Assert.Equal(2, st["material_rules"]!.AsArray().Count);
        var cat = st["materials"]!.AsArray();
        Assert.Contains(cat, m => S(m!["id"]) == "group:ores" && S(m["floor"]) == "wood");
        Assert.Contains(cat, m => S(m!["id"]) == c.Host.World!.Data.Block(15).Name && S(m["floor"]) == "stone" && S(m["tool"]) == "pickaxe");
        Assert.Contains(cat, m => S(m!["id"]) == "group:leaves" && S(m["floor"]) == null && S(m["keep_tool"]) == "shears");
        Assert.True(c.MaterialRulesOp(new JsonObject { ["op"] = "remove", ["material"] = "group:ores" }).Result!["removed"]!.GetValue<bool>());
    }

    [Fact]
    public void A_rule_raises_the_tier_a_stone_pickaxe_no_longer_does_for_iron_when_the_owner_asks_for_iron()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, 0, WorkTypes.Mine);
        w.Set(3, 64, 3, 15);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 0, 1));
        Assert.Null(c.MaterialRulesOp(new JsonObject { ["op"] = "set", ["rule"] = new JsonObject { ["material"] = "iron_ore", ["tier"] = "iron" } }).Error);
        c.Designate("add", "mine", null, [new P3(3, 64, 3)], null);
        Assert.NotEqual("dig", Kind(c.Next(0)));
        Assert.Equal("needs an iron pickaxe", c.Designations.Single().Wait);
    }

    private static PawnLive WithGear(double x, double z, params GearSlot[] gear) =>
        FakeStoryHost.Bare(x, 64, z, gear.Select(g => new Stack(g.Id, g.Meta, g.Count)).ToArray()) with { Gear = gear };

    [Fact]
    public void Marked_ores_take_the_enchanted_pickaxe_and_plain_stone_takes_a_plain_one()
    {
        var fortune = new Dictionary<string, int> { ["fortune"] = 3 };
        var (c, h, w) = StoryRig.Make(1, Release1122GameData.Instance);
        Only(c, 0, WorkTypes.Mine);
        w.Set(3, 64, 3, 56); // diamond ore
        h.Lives[c.P()] = WithGear(0, 0, new GearSlot(36, 257, 0, 1), new GearSlot(37, 257, 5, 1, fortune));
        Assert.Null(c.MaterialRulesOp(new JsonObject { ["op"] = "set", ["rule"] = new JsonObject { ["material"] = "diamond_ore", ["enchant"] = "fortune" } }).Error);
        c.Designate("add", "mine", null, [new P3(3, 64, 3)], null);
        var j = c.Next(0);
        Assert.Equal("dig", Kind(j));
        Assert.Equal("iron_pickaxe", S(j["tool"]));
        Assert.Equal("fortune", S(j["tool_ench"]));

        var (c2, h2, _) = StoryRig.Make(1, Release1122GameData.Instance);
        Only(c2, 0, WorkTypes.Mine);
        h2.Lives[c2.P()] = WithGear(0, 0, new GearSlot(36, 257, 0, 1), new GearSlot(37, 257, 5, 1, fortune));
        c2.Designate("add", "mine", null, [new P3(5, 63, 5)], null); // stone: unmarked
        var j2 = c2.Next(0);
        Assert.Equal("dig", Kind(j2));
        Assert.Equal("none", S(j2["tool_ench"]));
        // The equipment the browser shows: durability and enchantments.
        var eq = c2.State()["pawns"]![0]!["equipment"]!;
        var tools = eq["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t!["ench"]!["fortune"]?.GetValue<int>() == 3 && t["durability"]!.GetValue<double>() < 1);
    }

    [Fact]
    public void A_stored_enchanted_tool_is_fetched_and_wait_for_enchanted_waits_without_one()
    {
        var silk = new Dictionary<string, int> { ["silk_touch"] = 1 };
        var (c, h, w) = StoryRig.Make(1, Release1122GameData.Instance);
        Only(c, 0, WorkTypes.Mine);
        w.Set(3, 64, 3, 56);
        h.Lives[c.P()] = WithGear(0, 0, new GearSlot(36, 257, 0, 1));
        Assert.Null(c.MaterialRulesOp(new JsonObject
        {
            ["op"] = "set", ["rule"] = new JsonObject { ["material"] = "diamond_ore", ["enchant"] = "silk_touch", ["fallback"] = "wait" },
        }).Error);
        c.Designate("add", "mine", null, [new P3(3, 64, 3)], null);
        Assert.NotEqual("dig", Kind(c.Next(0)));
        Assert.StartsWith("waiting for an iron pickaxe with silk touch", c.Designations.Single().Wait);
        // One is put in storage (the colony knows it is enchanted): fetched exactly.
        Store(c, new Stack(278, 0, 1));
        c.StorageAt(Chest)!.Enchanted.Add(new EnchStack(278, 0, silk));
        c.Report(c.P(), c.CurrentOf(c.P())!.Id, "done", null, null);
        var j = c.Next(0);
        Assert.Equal("dig", Kind(j));
        Assert.Equal("diamond_pickaxe", S(j["tool"]));
        Assert.Equal("silk_touch", S(j["tool_ench"]));
        Assert.Equal("silk_touch", S(j["fetch"]![0]!["ench"]));
    }

    // ================================================================ policies

    [Fact]
    public void A_policy_fetches_the_tools_a_miner_needs_and_returns_the_worn_one()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, 0, WorkTypes.Mine);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 130, 1));
        Store(c, new Stack(257, 0, 1), new Stack(273, 0, 1)); // an iron pickaxe and a stone shovel
        var j = c.Next(0);
        Assert.Equal("equip", Kind(j));
        var fetched = j["fetch"]!.AsArray().Select(f => S(f!["item"])).ToList();
        Assert.Contains(Name(w, 257), fetched);
        Assert.Contains(Name(w, 273), fetched);
        Assert.Contains(Name(w, 274), j["stash"]![0]!["items"]!.AsArray().Select(x => S(x)));
        Done(c, 0, j);
        Assert.Equal(0, Stored(c, 257));
        Assert.Equal(1, Stored(c, 274)); // the worn one is in storage now
    }

    [Fact]
    public void Policies_are_set_assigned_and_removed()
    {
        var (c, _, _) = StoryRig.Make(2);
        var r = c.PoliciesOp(new JsonObject
        {
            ["op"] = "set", ["policy"] = new JsonObject { ["name"] = "Soldiers", ["weapon"] = "sword", ["armor"] = "always", ["tools"] = new JsonArray("shears"), ["food"] = 4 },
        });
        Assert.Null(r.Error);
        var id = S(r.Result!["id"])!;
        Assert.Null(c.PoliciesOp(new JsonObject { ["op"] = "assign", ["id"] = id, ["pawns"] = new JsonArray(c.P(1)) }).Error);
        var st = c.State();
        Assert.Equal(2, st["policies"]!.AsArray().Count);
        Assert.True(st["policies"]![0]!["default"]!.GetValue<bool>());
        Assert.Equal(id, S(st["pawns"]!.AsArray().Single(p => S(p!["name"]) == c.P(1))!["policy"]));
        Assert.Contains("weapon must be", c.PoliciesOp(new JsonObject { ["op"] = "set", ["policy"] = new JsonObject { ["name"] = "X", ["weapon"] = "laser" } }).Error);
        Assert.Contains("can't be removed", c.PoliciesOp(new JsonObject { ["op"] = "remove", ["id"] = StoryColony.DefaultPolicy }).Error);
        Assert.Null(c.PoliciesOp(new JsonObject { ["op"] = "remove", ["id"] = id }).Error);
        Assert.Null(c.Pawns[c.P(1)].Gear.Policy);
    }

    // ================================================================ drafting

    [Fact]
    public void Drafting_puts_the_best_weapon_and_armour_on_and_undrafting_puts_the_old_item_back()
    {
        var (c, h, w) = StoryRig.Make(1);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(4, 0, 10), new Stack(272, 0, 1), new Stack(267, 0, 1), new Stack(306, 0, 1)) with { Held = Name(w, 4) };
        var p = c.Pawns[c.P()];
        c.Draft([p], true);
        var j = c.Next(0);
        Assert.Equal("equip", Kind(j));
        Assert.Equal(Name(w, 267), S(j["hold"])); // the iron sword, not the stone one
        Assert.Equal(Name(w, 306), S(j["wear"]![0])); // armour when drafted (the default policy)
        Assert.Equal(Name(w, 4), p.Gear.RestoreHeld);
        Done(c, 0, j);
        Assert.NotEqual("equip", Kind(c.Next(0)));
        c.Draft([p], false);
        var back = c.Next(0);
        Assert.Equal("equip", Kind(back));
        Assert.Equal(Name(w, 4), S(back["hold"]));
        Assert.True(back["unwear"]!.GetValue<bool>());
        Assert.Null(p.Gear.RestoreHeld);
    }

    // ================================================================ attack orders

    [Fact]
    public void An_attack_order_on_a_mob_sends_every_drafted_pawn_at_that_exact_mob()
    {
        var (c, h, w) = StoryRig.Make(2);
        w.Ents.Add(new StoryEntity(50, "mob", "zombie", 5.5, 64, 5.5, Hostile: true));
        var ps = c.Pawns.Values.ToList();
        c.Draft(ps, true);
        var (res, err) = c.GiveOrder(ps, new JsonObject { ["kind"] = "attack", ["target"] = new JsonObject { ["entity"] = 50 } }, false);
        Assert.Null(err);
        Assert.Equal(2, res!["ids"]!.AsArray().Count);
        foreach (var i in new[] { 0, 1 })
        {
            var j = c.Next(i);
            Assert.Equal("attack", Kind(j));
            Assert.Equal(50, j["target"]!.GetValue<int>());
        }
        Assert.Empty(h.Permits); // a mob needs no permit
    }

    [Fact]
    public void Attacking_a_player_or_a_bot_needs_the_owners_confirmation_and_then_a_permit_for_exactly_that_entity()
    {
        var (c, h, w) = StoryRig.Make(2);
        h.Bots.Add("maid_main");
        w.Ents.Add(new StoryEntity(60, "player", "PoofimaFox", 5.5, 64, 5.5));
        w.Ents.Add(new StoryEntity(61, "player", "maid_main", 6.5, 64, 5.5));
        w.Ents.Add(new StoryEntity(62, "player", c.P(1), 1.5, 64, 0.5));
        var p0 = c.Pawns[c.P(0)];
        c.Draft([p0], true);
        JsonObject Attack(int id, bool? confirm = null)
        {
            var o = new JsonObject { ["kind"] = "attack", ["target"] = new JsonObject { ["entity"] = id } };
            if (confirm != null) o["confirm"] = confirm;
            return o;
        }
        var (res, err) = c.GiveOrder([p0], Attack(60), false);
        Assert.StartsWith(StoryColony.ConfirmError + ":", err);
        Assert.Equal("player", S(res!["confirm"]!["kind"]));
        Assert.Equal("PoofimaFox", S(res["confirm"]!["name"]));
        Assert.Empty(p0.Queue);
        var (bres, berr) = c.GiveOrder([p0], Attack(61), false);
        Assert.StartsWith(StoryColony.ConfirmError, berr);
        Assert.Equal("bot", S(bres!["confirm"]!["kind"]));
        Assert.Contains("own pawns", c.GiveOrder([p0], Attack(62, true), false).Error);
        // A bot by name (the map knows bots by name, not entity id): the same confirmation.
        var byName = new JsonObject { ["kind"] = "attack", ["target"] = new JsonObject { ["player"] = "maid_main" } };
        var (nres, nerr) = c.GiveOrder([p0], byName, false);
        Assert.StartsWith(StoryColony.ConfirmError, nerr);
        Assert.Equal(61, nres!["confirm"]!["entity"]!.GetValue<int>());
        Assert.Contains("no player named", c.GiveOrder([p0], new JsonObject { ["kind"] = "attack", ["target"] = new JsonObject { ["player"] = "ghost" } }, false).Error);
        Assert.Null(c.GiveOrder([p0], Attack(60, true), false).Error);
        var j = c.Next(0);
        Assert.Equal("attack", Kind(j));
        Assert.Equal(60, j["target"]!.GetValue<int>());
        Assert.Contains((c.P(0), 60), h.Permits);
        c.Draft([p0], false); // undrafted: the permit goes
        Assert.Empty(h.Permits);
    }

    [Fact]
    public void Drafted_pawns_engage_monsters_near_them_never_players_and_walk_back_to_their_post()
    {
        var (c, h, w) = StoryRig.Make(1);
        var p = c.Pawns[c.P()];
        c.Draft([p], true);
        w.Ents.Add(new StoryEntity(70, "player", "PoofimaFox", 2.5, 64, 0.5));
        var idle = c.Next(0);
        Assert.NotEqual("attack", Kind(idle)); // a player close by is never engaged by itself
        c.Report(c.P(), idle["id"]!.GetValue<string>(), "done", null, null);
        w.Ents.Add(new StoryEntity(71, "mob", "zombie", 4.5, 64, 0.5, Hostile: true));
        var fight = c.Next(0);
        Assert.Equal("attack", Kind(fight));
        Assert.Equal(71, fight["target"]!.GetValue<int>());
        Done(c, 0, fight);
        w.Ents.RemoveAll(e => e.Id == 71);
        // Its post (the last move order's cell): back there after the fight.
        Assert.Null(c.GiveOrder([p], new JsonObject { ["kind"] = "move", ["target"] = new JsonObject { ["x"] = 10, ["y"] = 64, ["z"] = 10 } }, false).Error);
        Assert.Equal(new P3(10, 64, 10), p.Gear.Post);
        var move = c.Next(0);
        Done(c, 0, move);
        var back = c.Next(0);
        Assert.Equal("go", Kind(back));
        Assert.Equal("10,64,10", S(back["at"]));
    }

    // ================================================================ bulk gear

    [Fact]
    public void Arm_spreads_the_best_weapons_one_each_best_fighters_first_and_reserves_them()
    {
        var (c, h, w) = StoryRig.Make(3);
        var ps = c.Pawns.Values.ToList();
        foreach (var (p, lvl) in ps.Zip(new[] { 2, 10, 5 }))
        {
            p.Skills[WorkTypes.Guard] = new SkillState { Level = lvl };
            p.Skills[WorkTypes.Hunt] = new SkillState { Level = 0 }; // the fighter's rank is the better of guard and hunt
        }
        foreach (var (p, i) in ps.Select((p, i) => (p, i))) h.Lives[p.Name] = FakeStoryHost.Bare(i, 64, 0);
        Store(c, new Stack(272, 0, 1), new Stack(267, 0, 1), new Stack(276, 0, 1)); // stone, iron, diamond swords
        var (r, err) = c.GearOp("arm", ps, new JsonObject());
        Assert.Null(err);
        string Got(PawnState p) => S(r!["results"]!.AsArray().Single(x => S(x!["pawn"]) == p.Name)!["got"]![0])!;
        Assert.Contains("Diamond", Got(ps[1]));
        Assert.Contains("Iron", Got(ps[2]));
        Assert.Contains("Stone", Got(ps[0]));
        Assert.All(ps, p => Assert.Single(p.Gear.Tasks.Single().Fetch));
        Assert.StartsWith("Arm: ", S(r!["summary"]));
        // Each pawn's arming job fetches its own sword.
        var j1 = c.Next(1);
        Assert.Equal("equip", Kind(j1));
        Assert.Equal(Name(w, 276), S(j1["hold"]));

        // Two pawns, one sword: one gets it, the other hears it is missing.
        var (c2, h2, _) = StoryRig.Make(2);
        foreach (var p in c2.Pawns.Values) h2.Lives[p.Name] = FakeStoryHost.Bare(0, 64, 0);
        Store(c2, new Stack(267, 0, 1));
        var (r2, _) = c2.GearOp("arm", c2.Pawns.Values.ToList(), new JsonObject());
        var res = r2!["results"]!.AsArray();
        Assert.Equal(1, res.Count(x => x!["got"]!.AsArray().Count == 1));
        Assert.Equal(1, res.Count(x => x!["missing"]!.AsArray().Any(m => S(m) == "a weapon")));
    }

    [Fact]
    public void Armour_goes_round_so_the_best_pieces_land_on_different_pawns()
    {
        var (c, h, w) = StoryRig.Make(2);
        var ps = c.Pawns.Values.ToList();
        ps[0].Skills[WorkTypes.Guard] = new SkillState { Level = 9 };
        ps[1].Skills[WorkTypes.Guard] = new SkillState { Level = 1 };
        foreach (var p in ps) p.Skills[WorkTypes.Hunt] = new SkillState { Level = 0 };
        foreach (var p in ps) h.Lives[p.Name] = FakeStoryHost.Bare(0, 64, 0);
        Store(c, new Stack(310, 0, 1), new Stack(306, 0, 1), new Stack(311, 0, 1), new Stack(307, 0, 1));
        var (r, err) = c.GearOp("armor", ps, new JsonObject());
        Assert.Null(err);
        Assert.Equal([Name(w, 310), Name(w, 307)], ps[0].Gear.Tasks.Single().Wear);
        Assert.Equal([Name(w, 306), Name(w, 311)], ps[1].Gear.Tasks.Single().Wear);
    }

    [Fact]
    public void Pick_up_hands_out_what_storage_has_and_says_what_was_missing()
    {
        var (c, h, w) = StoryRig.Make(2);
        foreach (var p in c.Pawns.Values) h.Lives[p.Name] = FakeStoryHost.Bare(0, 64, 0);
        Store(c, new Stack(297, 0, 4)); // bread
        var (r, err) = c.GearOp("pickup", c.Pawns.Values.ToList(), new JsonObject { ["item"] = new JsonArray(297, 0), ["count"] = 3 });
        Assert.Null(err);
        var res = r!["results"]!.AsArray();
        Assert.Equal("3 Bread", S(res[0]!["got"]![0]));
        Assert.Equal("1 Bread", S(res[1]!["got"]![0]));
        Assert.Equal("2 Bread", S(res[1]!["missing"]![0]));
        Assert.Contains("count must be", c.GearOp("pickup", c.Pawns.Values.ToList(), new JsonObject { ["item"] = new JsonArray(297, 0), ["count"] = 99 }).Error);
    }

    [Fact]
    public void Return_takes_armour_off_and_stores_the_gear()
    {
        var (c, h, w) = StoryRig.Make(1);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0) with { Gear = [new GearSlot(5, 306, 0, 1), new GearSlot(36, 267, 0, 1), new GearSlot(37, 274, 0, 1)] };
        Store(c);
        var (r, err) = c.GearOp("return", c.Pawns.Values.ToList(), new JsonObject());
        Assert.Null(err);
        var t = c.Pawns[c.P()].Gear.Tasks.Single();
        Assert.True(t.Unwear);
        Assert.Equal(3, t.Store.Count);
        var j = c.Next(0);
        Assert.Equal("equip", Kind(j));
        Assert.True(j["unwear"]!.GetValue<bool>());
        Done(c, 0, j);
        Assert.Equal(1, Stored(c, 306));
        Assert.Equal(1, Stored(c, 267));
    }
}
