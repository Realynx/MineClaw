using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>Builds craft what they need (docs/web/story-mode.md "Builds craft what they need"), designated benches and the Architect's tiers.</summary>
public sealed class StoryBuildCraftingTests
{
    private static readonly P3 Chest = new(0, 64, 9), Table = new(3, 64, 0), Furnace = new(6, 64, 0);

    private static void Only(StoryColony c, int i, params string[] types)
    {
        var p = c.Pawns.Values.ElementAt(i);
        foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    /// <summary>1.12.2, pawn 0 crafts, pawn 1 builds; a chest with <paramref name="stock"/>; benches designated as asked.</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, FakeClock T) Rig(bool table, bool furnace, params Stack[] stock)
    {
        var clock = new FakeClock();
        var (c, h, w) = StoryRig.Make(pawns: 2, data: Release1122GameData.Instance, time: clock);
        Only(c, 0, WorkTypes.Craft, WorkTypes.Cook);
        Only(c, 1, WorkTypes.Build);
        c.StorageOp("set", [Chest], null, null, null);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        w.Set(Table.X, Table.Y, Table.Z, 58);
        w.Set(Furnace.X, Furnace.Y, Furnace.Z, 61);
        if (table) Assert.Null(c.WorkbenchOp("designate", Table).Error);
        if (furnace) Assert.Null(c.WorkbenchOp("designate", Furnace).Error);
        return (c, h, w, clock);
    }

    private static string Build(StoryColony c, JsonObject item)
    {
        var r = c.BuildsOp("add", new JsonArray(item), null);
        Assert.Null(r.Error);
        return r.Result!["ids"]![0]!.GetValue<string>();
    }

    private static JsonArray Cells(params (int X, int Y, int Z)[] cells) => new(cells.Select(p => (JsonNode)new JsonArray(p.X, p.Y, p.Z)).ToArray());

    private static int Stored(StoryColony c, int id) => c.StorageAt(Chest)!.Contents.Where(s => s.Id == id).Sum(s => s.Count);

    private static void Done(StoryColony c, int pawn, JsonObject job) => Assert.True(c.Report(c.P(pawn), job["id"]!.GetValue<string>(), "done", null, null));

    private static List<BillState> AutoBills(StoryColony c) => c.Benches.SelectMany(b => b.Tasks).Where(b => b.ForBuild != null).ToList();

    [Fact]
    public void Cobblestone_stairs_are_crafted_from_stored_cobblestone_then_placed()
    {
        var (c, h, w, _) = Rig(table: true, furnace: false, new Stack(4, 0, 64));
        var id = Build(c, new JsonObject { ["kind"] = "stairs", ["blocks"] = Cells((0, 64, 5), (1, 64, 5), (2, 64, 5)), ["material"] = "cobblestone", ["facing"] = "n" });
        // The builder waits (being crafted), no alert for it; the crafter gets the auto bill.
        var wait = c.Next(1);
        Assert.NotEqual("place", wait["kind"]!.GetValue<string>());
        var b = c.Builds.Single();
        Assert.Equal("waiting", b.Status);
        Assert.Contains("being crafted", b.Reason);
        var mat = b.Materials.Single();
        Assert.Equal((3, 0, 3, 0, "craft"), (mat.Need, mat.Have, mat.Crafting, mat.Short, mat.Via));
        var bill = AutoBills(c).Single();
        Assert.Equal(id, bill.ForBuild);
        Assert.Equal(Table, c.Benches.Single(x => x.Tasks.Contains(bill)).Pos);
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn");
        var craft = c.Next(0);
        Assert.Equal("craft", craft["kind"]!.GetValue<string>());
        Assert.Equal(Table.ToString(), craft["at"]!.GetValue<string>());
        Assert.Equal(6, craft["fetch"]![0]!["n"]!.GetValue<int>()); // 6 cobblestone → 4 stairs
        Done(c, 0, craft);
        Assert.Equal(4, Stored(c, 67));
        Assert.Equal(58, Stored(c, 4));
        // Nothing left to craft: the auto bill goes; the builder fetches the stairs.
        c.Report(c.P(1), wait["id"]!.GetValue<string>(), "done", null, null);
        var place = c.Next(1);
        Assert.Equal("place", place["kind"]!.GetValue<string>());
        Assert.Equal(Chest.ToString(), place["fetch"]![0]!["from"]!.GetValue<string>());
        Assert.Empty(AutoBills(c));
        Assert.Equal(0, b.Materials.Single().Crafting);
    }

    [Fact]
    public void A_fence_comes_from_logs_through_planks_and_sticks_in_chain_order()
    {
        var (c, h, w, _) = Rig(table: true, furnace: false, new Stack(17, 0, 4));
        Build(c, new JsonObject { ["kind"] = "fence", ["blocks"] = Cells((0, 64, 5), (1, 64, 5), (2, 64, 5)) });
        c.EnsureMaterials();
        var bills = c.Benches.Single().Tasks;
        var outputs = bills.Select(x => StoryRecipes.Get(Release1122GameData.Instance, x.Recipe)!.Output.Id).ToList();
        Assert.Equal([5, 280, 85], outputs); // planks, sticks, fence: ingredients first
        Assert.All(bills, x => Assert.NotNull(x.ForBuild));
        var made = new List<int>();
        for (int i = 0; i < 6 && Stored(c, 85) < 3; i++)
        {
            var j = c.Next(0);
            Assert.Equal("craft", j["kind"]!.GetValue<string>());
            made.Add(c.CurrentOf(c.P(0))!.Count);
            Done(c, 0, j);
        }
        Assert.Equal(3, Stored(c, 85));
        Assert.Equal(3, made.Count);
        c.EnsureMaterials();
        Assert.Empty(AutoBills(c));
        Assert.Equal("place", c.Next(1)["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Glass_panes_are_smelted_from_sand_with_fuel_then_crafted()
    {
        var (c, h, w, clock) = Rig(table: true, furnace: true, new Stack(12, 0, 6), new Stack(263, 0, 2));
        Build(c, new JsonObject { ["kind"] = "block", ["blocks"] = Cells((0, 64, 5), (1, 64, 5), (2, 64, 5)), ["material"] = new JsonArray(102, 0) });
        c.EnsureMaterials();
        var mat = c.Builds.Single().Materials.Single();
        Assert.Equal("smelt", mat.Via);
        Assert.Equal(3, mat.Crafting);
        var smelt = c.Next(0);
        Assert.Equal("smelt", smelt["kind"]!.GetValue<string>());
        Assert.Equal("coal", smelt["fuel"]!.GetValue<string>());
        Done(c, 0, smelt);
        // While it smelts, the panes still count on the glass in the furnace: the bills stay.
        Assert.Contains(AutoBills(c), x => x.Recipe.StartsWith("smelt:"));
        clock.Now += TimeSpan.FromSeconds(70);
        var collect = c.Next(0);
        Assert.Equal("collect_furnace", collect["kind"]!.GetValue<string>());
        Done(c, 0, collect);
        // The glass is in the pawn's pocket now; it stores it, then the pane bill runs.
        h.Lives[c.P(0)] = FakeStoryHost.Live(0, 64, 0, inv: new Stack(20, 0, 6));
        var store = c.Next(0);
        Assert.Equal("go", store["kind"]!.GetValue<string>());
        Done(c, 0, store);
        h.Lives[c.P(0)] = FakeStoryHost.Live(0, 64, 0);
        var panes = c.Next(0);
        Assert.Equal("craft", panes["kind"]!.GetValue<string>());
        Assert.Equal(16, c.CurrentOf(c.P(0))!.Count);
        Done(c, 0, panes);
        Assert.Equal(16, Stored(c, 102));
    }

    [Fact]
    public void Without_a_bench_a_pawn_crafts_a_crafting_table_places_it_near_home_and_then_the_bills_go_there()
    {
        var (c, h, w, _) = Rig(table: false, furnace: false, new Stack(17, 0, 8));
        Assert.True(c.Meta.AutoPlaceBenches);
        Build(c, new JsonObject { ["kind"] = "fence", ["blocks"] = Cells((0, 64, 5), (1, 64, 5), (2, 64, 5)) });
        var j = c.Next(0);
        Assert.Equal("place", j["kind"]!.GetValue<string>());
        Assert.Equal("crafting_table", j["block"]!.GetValue<string>());
        Assert.Equal("crafting_table", j["craft"]![0]!["item"]!.GetValue<string>());
        Assert.Equal("log", j["fetch"]![0]!["item"]!.GetValue<string>());
        var spot = P3.Parse(new JsonArray(j["at"]!.GetValue<string>().Split(',').Select(s => (JsonNode)int.Parse(s)).ToArray()))!.Value;
        Assert.True(spot.Dist(new P3(0, 64, 0)) <= 12);
        Assert.Empty(c.Benches); // the undesignated table at 3,64,0 is never used
        w.Set(spot.X, spot.Y, spot.Z, 58);
        Done(c, 0, j);
        Assert.Equal(spot, c.Benches.Single().Pos);
        c.EnsureMaterials();
        Assert.NotEmpty(c.Benches.Single().Tasks);
        Assert.Equal("craft", c.Next(0)["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Without_auto_placing_the_owner_is_asked_for_a_bench_and_hand_crafts_still_run()
    {
        var (c, h, w, _) = Rig(table: false, furnace: false, new Stack(17, 0, 8));
        c.SetOptions(autoPlaceBenches: false, detectBenches: null);
        Build(c, new JsonObject { ["kind"] = "fence", ["blocks"] = Cells((0, 64, 5), (1, 64, 5), (2, 64, 5)) });
        c.EnsureMaterials();
        Assert.Contains(c.Alerts, a => a.Text.Contains("needs a crafting table: place one or designate an existing one"));
        // Planks are a 2×2 recipe: they're made in the pawn's own grid meanwhile.
        var j = c.Next(0);
        Assert.Equal("craft", j["kind"]!.GetValue<string>());
        Assert.Null(j["at"]);
        Assert.Equal(5, StoryRecipes.For(Release1122GameData.Instance).First(r => r.ToolItem == j["item"]!.GetValue<string>()).Output.Id);
    }

    [Fact]
    public void Short_raw_materials_say_what_is_missing_and_nothing_deadlocks()
    {
        var (c, h, w, _) = Rig(table: true, furnace: false, new Stack(17, 0, 2)); // 2 logs = 8 planks: one fence craft (3), not two (12 planks)
        var id = Build(c, new JsonObject { ["kind"] = "fence", ["blocks"] = Cells((0, 64, 5), (1, 64, 5), (2, 64, 5), (3, 64, 5), (4, 64, 5), (5, 64, 5)) });
        c.EnsureMaterials();
        var mat = c.Builds.Single().Materials.Single();
        Assert.Equal(6, mat.Need);
        Assert.Equal(3, mat.Crafting); // one fence craft from what there is
        Assert.Equal(3, mat.Short);
        Assert.NotEmpty(mat.Raw);
        var alert = Assert.Single(c.Alerts, a => a.Text.Contains("short of materials"));
        Assert.Contains("short of raw materials", alert.Text);
        // The pawns still work through what can be made; no pawn holds a lease forever.
        for (int i = 0; i < 6; i++)
        {
            var j = c.Next(0);
            if (j["kind"]!.GetValue<string>() != "craft") break;
            Done(c, 0, j);
        }
        Assert.Equal(3, Stored(c, 85));
        Assert.Empty(AutoBills(c)); // nothing more can be made: no bill sits waiting for logs
        // Said when it changes, never per pick: more picks add no alert.
        int said = c.Alerts.Count(a => a.Text.Contains("short of materials"));
        for (int i = 0; i < 3; i++) { var j = c.Next(0); c.Report(c.P(0), j["id"]!.GetValue<string>(), "done", null, null); c.Next(1); }
        Assert.Equal(said, c.Alerts.Count(a => a.Text.Contains("short of materials")));
        Assert.Equal(id, c.Builds.Single().Id);
        Assert.Equal(3, c.Builds.Single().Materials.Single().Short);
    }

    [Fact]
    public void Two_builds_never_count_on_the_same_planks_and_a_manual_bill_leaves_them()
    {
        var (c, h, w, _) = Rig(table: true, furnace: false, new Stack(5, 0, 6));
        var floor = Build(c, new JsonObject { ["kind"] = "floor", ["area"] = new JsonArray(0, 64, 5, 2, 64, 6), ["material"] = "planks" });
        var fence = Build(c, new JsonObject { ["kind"] = "fence", ["blocks"] = Cells((5, 64, 5), (6, 64, 5), (7, 64, 5)) });
        c.EnsureMaterials();
        var f = c.Builds.Single(b => b.Id == floor).Materials.Single();
        Assert.Equal((6, 6), (f.Need, f.Have));
        var fe = c.Builds.Single(b => b.Id == fence).Materials.Single();
        Assert.Equal((0, 0, 3), (fe.Have, fe.Crafting, fe.Short)); // the floor's planks aren't the fence's
        Assert.Empty(AutoBills(c));
        // A manual stick bill would eat the reserved planks: it waits, saying why.
        Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = Table.Json(), ["bill"] = new JsonObject { ["recipe"] = "craft:stick", ["mode"] = "count", ["target"] = 4 } }).Error);
        Assert.NotEqual("craft", c.Next(0)["kind"]!.GetValue<string>());
        Assert.Matches(@"^6 oak wood planks reserved for build b\d+ \(.*\) — 0 free$", c.Benches.Single().Bills.Single().SuspendedReason); // names the build and the amounts
        // Reorder: the fence first. Now it gets the planks (crafting), and the floor waits.
        c.BuildsOp("reorder", null, [fence]);
        c.EnsureMaterials();
        Assert.Equal(3, c.Builds.Single(b => b.Id == fence).Materials.Single().Crafting);
        Assert.Equal(0, c.Builds.Single(b => b.Id == floor).Materials.Single().Have);
    }

    [Fact]
    public void An_auto_bill_can_only_be_cancelled_and_then_the_build_waits_for_the_item()
    {
        var (c, h, w, _) = Rig(table: true, furnace: false, new Stack(4, 0, 64));
        var id = Build(c, new JsonObject { ["kind"] = "stairs", ["blocks"] = Cells((0, 64, 5)), ["material"] = "cobblestone", ["facing"] = "n" });
        c.EnsureMaterials();
        var bill = AutoBills(c).Single();
        Assert.NotNull(c.BillsOp("update", new JsonObject { ["id"] = bill.Id, ["target"] = 9 }).Error);
        Assert.NotNull(c.BillsOp("pause", new JsonObject { ["id"] = bill.Id }).Error);
        var st = c.State();
        Assert.Empty(st["workbenches"]!.AsArray().Single()!["bills"]!.AsArray()); // the owner's list shows the owner's bills only
        Assert.Equal(id, st["craft_tasks"]!.AsArray().Single()!["for"]!.GetValue<string>());
        Assert.Null(c.BillsOp("remove", new JsonObject { ["id"] = bill.Id }).Error);
        c.EnsureMaterials();
        Assert.Empty(AutoBills(c));
        var b = c.Builds.Single();
        Assert.Contains(bill.ForItem!, b.NoCraft);
        Assert.Equal(("none", 1), (b.Materials.Single().Via, b.Materials.Single().Short));
        var state = c.State()["builds"]!.AsArray().Single()!;
        Assert.Equal("none", state["materials"]![0]!["via"]!.GetValue<string>());
    }

    [Fact]
    public void Designated_benches_unlock_tiers_and_only_they_get_bills()
    {
        var (c, h, w, _) = Rig(table: false, furnace: false, new Stack(17, 0, 16));
        JsonObject Entry(string id) => c.Architect()["entries"]!.AsArray().Select(e => e!.AsObject()).First(e => e["id"]!.GetValue<string>() == id);
        Assert.Equal("hand", Entry("planks")["tier"]!.GetValue<string>());
        Assert.True(Entry("planks")["unlocked"]!.GetValue<bool>());
        Assert.Equal("workbench", Entry("fence")["tier"]!.GetValue<string>());
        Assert.False(Entry("fence")["unlocked"]!.GetValue<bool>());
        Assert.StartsWith("Needs a crafting table", Entry("fence")["reason"]!.GetValue<string>());
        Assert.Equal("furnace", Entry("glass")["tier"]!.GetValue<string>());
        Assert.Equal("gathered", Entry("dirt")["tier"]!.GetValue<string>());
        Assert.True(Entry("fence")["craftable"]!.GetValue<int>() > 0);
        var tabs = c.Architect()["entries"]!.AsArray().Select(e => e!["tab"]!.GetValue<string>()).Distinct().ToList();
        Assert.Equal(StoryArchitect.Tabs.OrderBy(t => t), tabs.OrderBy(t => t));

        // A table in the world near home isn't the colony's until designated (detection is off by default).
        c.Tick();
        Assert.Empty(c.Benches);
        Assert.Null(c.WorkbenchOp("designate", Table).Error);
        Assert.True(Entry("fence")["unlocked"]!.GetValue<bool>());
        Assert.False(Entry("glass")["unlocked"]!.GetValue<bool>());
        Assert.NotNull(c.WorkbenchOp("designate", new P3(9, 64, 9)).Error); // nothing there
        Assert.Null(c.WorkbenchOp("release", Table).Error);
        Assert.False(Entry("fence")["unlocked"]!.GetValue<bool>());
        Assert.Contains("workbenches", c.State(since: 0)["removed"]!.AsObject().Select(kv => kv.Key));
        Assert.NotNull(c.WorkbenchOp("release", Table).Error);

        // Detection, when the owner turns it on, finds benches near home again.
        c.SetOptions(autoPlaceBenches: null, detectBenches: true);
        Assert.Equal(2, c.Benches.Count);
        Assert.True(c.State()["options"]!["detect_benches"]!.GetValue<bool>());
    }

    /// <summary>A cell a newer build took over, or one a quarry digs, is never placed as things stand, yet it kept its build's share of
    /// the stock (and its auto crafts): a later build waited with "the planks in storage are reserved for an earlier build".</summary>
    [Fact]
    public void Superseded_and_quarry_held_cells_reserve_nothing()
    {
        var (c, _, _, _) = Rig(table: false, furnace: false, new Stack(5, 0, 4));
        JsonObject Of(int id, params (int X, int Y, int Z)[] cells) => new() { ["kind"] = "block", ["blocks"] = Cells(cells), ["material"] = new JsonArray(id, 0) };
        BuildState B(string id) => c.Builds.Single(b => b.Id == id);
        // An older planks build whose two cells a newer cobblestone build took over.
        var old = Build(c, Of(5, (20, 64, 5), (21, 64, 5)));
        Build(c, Of(4, (20, 64, 5), (21, 64, 5)));
        Assert.All(B(old).Ghosts, g => Assert.StartsWith("superseded by build ", g.Reason));
        // Two planks cells in a quarry's columns.
        Assert.Null(c.ZoneOp("add", "mine", Area.Of(30, 63, 30, 33, 63, 33)).Error);
        var dug = Build(c, Of(5, (31, 63, 31), (32, 63, 31)));
        Assert.All(B(dug).Ghosts, g => Assert.StartsWith("conflicts with quarry ", g.Reason));
        // The four planks the colony has go to the build that can use them.
        var last = Build(c, Of(5, (40, 64, 5), (41, 64, 5), (42, 64, 5), (43, 64, 5)));
        c.EnsureMaterials();
        Assert.Empty(B(old).Materials);
        Assert.Empty(B(dug).Materials);
        var m = B(last).Materials.Single();
        Assert.Equal((4, 4, 0), (m.Need, m.Have, m.Short));
    }

    /// <summary>Fetches skip a chest whose lid a solid block shuts, but its contents still counted as stock: the materials list said
    /// "have 4, short 0", no craft was ordered, and the build waited with "need 4 planks" until the block on the lid went.</summary>
    [Fact]
    public void Items_in_a_chest_that_cannot_open_are_not_stock()
    {
        var (c, _, w, clock) = Rig(table: false, furnace: false, new Stack(5, 0, 8));
        w.Set(Chest.X, Chest.Y, Chest.Z, 54, 2);
        w.Set(Chest.X, Chest.Y + 1, Chest.Z, 1); // stone on the lid
        Build(c, new JsonObject { ["kind"] = "block", ["blocks"] = Cells((20, 64, 5), (21, 64, 5), (22, 64, 5), (23, 64, 5)), ["material"] = new JsonArray(5, 0) });
        c.EnsureMaterials();
        var m = c.Builds.Single().Materials.Single();
        Assert.Equal((4, 0, 4), (m.Need, m.Have, m.Short));
        // The lid clear again: the planks are stock.
        w.Set(Chest.X, Chest.Y + 1, Chest.Z, 0);
        clock.Now += TimeSpan.FromSeconds(10);
        c.MaterialsChanged();
        c.EnsureMaterials();
        m = c.Builds.Single().Materials.Single();
        Assert.Equal((4, 4, 0), (m.Need, m.Have, m.Short));
    }

    /// <summary>Only the Architect's chest builds were crafted as immediate tasks; a design's chests (a <c>block</c> build, the
    /// storehouse) waited at priority 5 behind every owner bill, a "forever" one for good ("waiting for N chest (being crafted)").</summary>
    [Fact]
    public void A_designs_chests_are_crafted_before_owner_bills()
    {
        var (c, _, _, _) = Rig(table: true, furnace: false, new Stack(5, 0, 16), new Stack(296, 0, 30));
        Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = Table.Json(), ["bill"] = new JsonObject { ["recipe"] = "craft:bread", ["mode"] = "forever" } }).Error);
        Build(c, new JsonObject { ["kind"] = "block", ["blocks"] = Cells((20, 64, 5)), ["material"] = new JsonArray(54, 2) });
        c.EnsureMaterials();
        Assert.Contains(AutoBills(c), x => x.ForItem == "chest");
        var craft = c.Next(0);
        Assert.Equal("craft", craft["kind"]!.GetValue<string>());
        Assert.Contains("chest", craft["item"]!.GetValue<string>());
    }

    /// <summary>A bench the colony placed near home could land in a finished design's empty space (StationSpot only avoided live
    /// builds): the room dug it out, the spot was free again, and the next bench went there (C11).</summary>
    [Fact]
    public void A_bench_is_never_put_into_a_designs_room()
    {
        var (c, _, w, _) = Rig(table: false, furnace: false, new Stack(17, 0, 8));
        w.Set(2, 65, 2, 4);
        var room = new JsonObject { ["kind"] = "block", ["blocks"] = Cells((2, 65, 2)), ["material"] = new JsonArray(4, 0) };
        room["replace_area"] = new JsonArray(-2, 64, -2, 2, 65, 2); // home's 5×5 around, two high
        Build(c, room);
        c.Reconcile(w);
        var air = c.Builds.Single(b => b.Kind == "remove");
        Assert.Equal("done", air.Status);
        var inRoom = air.Ghosts.Select(g => g.At).ToHashSet();
        Build(c, new JsonObject { ["kind"] = "fence", ["blocks"] = Cells((0, 64, 5), (1, 64, 5), (2, 64, 5)) });
        var j = c.Next(0);
        Assert.Equal("crafting_table", j["block"]!.GetValue<string>());
        var at = j["at"]!.GetValue<string>().Split(',').Select(int.Parse).ToArray();
        Assert.DoesNotContain(new P3(at[0], at[1], at[2]), inRoom);
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
