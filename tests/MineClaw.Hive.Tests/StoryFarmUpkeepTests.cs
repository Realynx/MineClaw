using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The farm kept (docs/web/story-mode.md "Farming", "Storage"). Fox 11-06: "The farm looks really bad. I don't know why it's not being
/// maintained." Live (story-1, 1,565-cell wheat field): 142 cells grass or dirt for 9 hours (no hoe: the farm's hoe never became a
/// craft task while an old "wooden hoe" bill sat in the table's list), "chest at 216,64,400 didn't open (blocked above)" ×146 (planks
/// on the chest, chosen again for every fetch), "harvest failed: wheat … not ripe (0/7)" ×175 and "seeds … had no effect" (main's
/// view behind the pawns'), a till under tall grass that did nothing every 10 minutes, every chest full, and the chests Fox placed
/// for room waiting behind every other craft.
/// </summary>
public sealed class StoryFarmUpkeepTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T01:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();

    private void Later(int s = 6) => _clock.Now += TimeSpan.FromSeconds(s);

    private (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Farm(IGameData? data = null, params Stack[] inv)
    {
        var (c, h, w) = StoryRig.Make(data: data, time: _clock);
        Only(c, WorkTypes.Farm);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: inv);
        return (c, h, w);
    }

    private static void Only(StoryColony c, params string[] work)
    {
        var p = c.Pawns.Values.First();
        foreach (var t in WorkTypes.All) p.Priorities[t] = work.Contains(t) ? 1 : 0;
    }

    private static string AddZone(StoryColony c, Area a, string plant)
    {
        var (res, err) = c.ZoneOp("add", "farm", a, grow: new JsonObject { ["plant"] = plant });
        Assert.True(err == null, err);
        return res!["id"]!.GetValue<string>();
    }

    private static JsonObject Zone(StoryColony c, string id) =>
        c.State()["zones"]!.AsArray().Select(z => z!.AsObject()).Single(z => z["id"]!.GetValue<string>() == id);

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static string At(JsonObject j) => j["at"]!.GetValue<string>();

    private static P3 Pos(string s) { var p = s.Split(',').Select(int.Parse).ToArray(); return new P3(p[0], p[1], p[2]); }

    private static void Done(StoryColony c, JsonObject j) => Assert.True(c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null));

    private static void Store(StoryColony c, P3 at, params Stack[] stock)
    {
        Assert.Null(c.StorageOp("set", [at], null, null, null).Error);
        foreach (var s in stock) c.AddContents(c.StorageAt(at)!, s);
    }

    /// <summary>A row of farmland at y 63 from x 10 (water two blocks off), each cell's crop at y 64 (-1: bare).</summary>
    private static void Field(FakeStoryWorld w, params int[] stages)
    {
        for (int i = 0; i < stages.Length; i++)
        {
            w.Set(10 + i, 63, 0, 60);
            if (stages[i] >= 0) w.Set(10 + i, 64, 0, 59, stages[i]);
        }
        w.Set(10, 63, 2, 9);
    }

    [Fact]
    public void An_old_hoe_bill_in_the_tables_list_doesnt_keep_the_farm_from_its_hoe()
    {
        // Live: bill35339 "wooden hoe, count 1, 0 done" sat in the table's own list; the farm's need saw it and made no task, the bill
        // waited behind the owner's other bills and every build, and no hoe was made for 9 hours.
        var (c, h, w) = Farm(null, new Stack(295, 0, 16));
        Only(c, WorkTypes.Farm, WorkTypes.Craft);
        c.Pawns.Values.First().Priorities[WorkTypes.Craft] = 3;
        w.Set(3, 64, 0, 58);
        Assert.Null(c.WorkbenchOp("designate", new P3(3, 64, 0)).Error);
        var hoe = StoryRecipes.For(w.Data, "crafting_table").First(r => r.Output.Id == 290).Id;
        Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = new P3(3, 64, 0).Json(), ["bill"] = new JsonObject { ["recipe"] = hoe, ["mode"] = "count", ["target"] = 1 } }).Error);
        Store(c, new P3(0, 64, 4), new Stack(5, 0, 8), new Stack(280, 0, 8));
        Field(w, -1);                          // bare farmland: there is always something to sow
        w.Set(11, 63, 0, 2);                   // and grass to till, with no hoe anywhere
        AddZone(c, Area.Of(10, 63, 0, 11, 63, 0), "wheat");
        var kinds = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var j = c.Next();
            kinds.Add(Kind(j) + " " + j["item"]);
            if (Kind(j) == "craft") break;
            Done(c, j); // the seed the script sowed: the cell reads bare again (another bare cell, as far as the colony knows)
        }
        Assert.Contains(kinds, k => k.StartsWith("craft") && k.Contains("hoe")); // the hoe, before more sowing
        Assert.Contains(c.Benches.Single().Tasks, t => t.Recipe == hoe && c.Immediate(t));
    }

    [Fact]
    public void An_unfinished_hoe_bill_the_colony_made_becomes_the_farms_task()
    {
        var (c, _, w) = Farm();
        w.Set(3, 64, 0, 58);
        Assert.Null(c.WorkbenchOp("designate", new P3(3, 64, 0)).Error);
        var hoe = StoryRecipes.For(w.Data, "crafting_table").First(r => r.Output.Id == 290).Id;
        var bench = c.Benches.Single();
        bench.Bills.Add(new BillState { Id = "bill35339", Recipe = hoe, Mode = "count", Target = 1, Done = 0, Radius = 32, Pawn = "any", Ingredients = new StorageFilter { Allow = ["*"] } });
        bench.Bills.Add(new BillState { Id = "bill3650", Recipe = hoe.Replace("wooden", "stone"), Mode = "until", Target = 1, Radius = 86, Pawn = "any" }); // the owner's own
        Assert.True(c.MigrateCraftTasks());
        var task = Assert.Single(bench.Tasks);
        Assert.Equal("bill35339", task.Id);
        Assert.True(c.Immediate(task));
        Assert.Equal("bill3650", Assert.Single(bench.Bills).Id); // the owner's bill stays the owner's
    }

    [Fact]
    public void Tall_grass_on_soil_to_till_is_cut_first()
    {
        var (c, _, w) = Farm(null, new Stack(290, 0, 1));
        w.Set(10, 63, 0, 2); w.Set(10, 64, 0, 31, 1); // grass with tall grass on it
        w.Set(10, 63, 2, 9);
        AddZone(c, Area.Of(10, 63, 0, 10, 63, 0), "wheat");
        var till = c.Next();
        Assert.Equal("till", Kind(till));
        Assert.Equal("10,63,0", At(till));
        Assert.Equal("10,64,0", till["clear_at"]!.GetValue<string>());
    }

    [Fact]
    public void A_seed_chest_with_a_block_on_it_is_left_alone_and_said_once()
    {
        var near = new P3(0, 64, 2);
        var far = new P3(0, 64, 9);
        var (c, _, w) = Farm();
        Field(w, -1);
        AddZone(c, Area.Of(10, 63, 0, 10, 63, 0), "wheat");
        w.Set(near.X, near.Y, near.Z, 54); w.Set(far.X, far.Y, far.Z, 54);
        w.Set(near.X, near.Y + 1, near.Z, 5); // oak planks on the near chest
        Store(c, near, new Stack(295, 0, 32));
        Store(c, far, new Stack(295, 0, 32));
        var sow = c.Next();
        Assert.Equal("place", Kind(sow));
        Assert.Equal(far.ToString(), sow["fetch"]![0]!["from"]!.GetValue<string>());
        Done(c, sow);
        w.Set(10, 64, 0, 0); // still bare: another sowing, another look at the chests
        Later();
        Assert.Equal(far.ToString(), c.Next()["fetch"]![0]!["from"]!.GetValue<string>());
        var said = Assert.Single(c.Alerts, a => a.Text.Contains("can't open"));
        Assert.Contains($"Chest at {near}", said.Text);
        Assert.Contains("planks", said.Text);
        Assert.Contains($"{near.X},{near.Y + 1},{near.Z}", said.Text);
        // The block goes: the chest is in the plans again.
        if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
        w.Set(near.X, near.Y + 1, near.Z, 0);
        Later();
        Assert.Equal(near.ToString(), c.Next()["fetch"]![0]!["from"]!.GetValue<string>());
    }

    [Fact]
    public void A_chest_that_didnt_open_is_left_out_of_that_pawns_plans_not_the_cell()
    {
        var near = new P3(0, 64, 2);
        var far = new P3(0, 64, 9);
        var (c, _, w) = Farm();
        Field(w, -1);
        AddZone(c, Area.Of(10, 63, 0, 10, 63, 0), "wheat");
        Store(c, near, new Stack(295, 0, 32));
        Store(c, far, new Stack(295, 0, 32));
        var sow = c.Next();
        Assert.Equal(near.ToString(), sow["fetch"]![0]!["from"]!.GetValue<string>());
        Assert.True(c.Report(c.P(), sow["id"]!.GetValue<string>(), "failed", $"chest at {near} didn't open (blocked above, or protected?)", null));
        var again = c.Next(); // the cell isn't to blame: sown at once, from the other chest
        Assert.Equal("place", Kind(again));
        Assert.Equal(far.ToString(), again["fetch"]![0]!["from"]!.GetValue<string>());
        // The only pawn failed it: the colony hears it.
        Assert.Contains(c.Alerts, a => a.Text.Contains($"Chest at {near} won't open"));
    }

    [Fact]
    public void A_crop_the_pawns_own_view_sees_young_or_sown_is_not_offered()
    {
        // Main's view lags the pawns' (live 11-05: "wheat … not ripe (0/7)" ×175 and "seeds … had no effect").
        var (c, h, w) = Farm(null, new Stack(295, 0, 8));
        Field(w, 7, 3, 7, 3, -1);              // main: ripe at 10 and 12, bare at 14
        AddZone(c, Area.Of(10, 63, 0, 14, 63, 0), "wheat");
        var own = new FakeStoryWorld();
        foreach (var kv in w.Blocks) own.Blocks[kv.Key] = kv.Value;
        own.Set(10, 64, 0, 59, 0);             // the pawn: 10 was harvested and sown again
        own.Set(14, 64, 0, 59, 0);             // and 14 sown
        h.Views[c.P()] = own;
        var first = c.Next();
        Assert.Equal("harvest", Kind(first));
        Assert.Equal("12,64,0", At(first));    // never 10 (nearer, but young where the pawn stands)
        w.Set(12, 64, 0, 59, 0); own.Set(12, 64, 0, 59, 0);
        Done(c, first);
        var next = c.Next();
        Assert.False(Kind(next) is "harvest" or "place", $"{Kind(next)} at {next["at"]}"); // 10 and 14 rest until main's view agrees
    }

    [Theory]
    [InlineData("carrots", 141, 6, 7)]
    [InlineData("potatoes", 142, 6, 7)]
    [InlineData("beetroot", 207, 2, 3)]
    [InlineData("wheat", 59, 6, 7)]
    public void Each_crop_is_ripe_at_its_own_last_stage(string plant, int block, int young, int ripe)
    {
        var (c, _, w) = Farm(Release1122GameData.Instance);
        w.Set(10, 63, 0, 60); w.Set(11, 63, 0, 60); w.Set(10, 63, 2, 9);
        w.Set(10, 64, 0, block, young); w.Set(11, 64, 0, block, ripe);
        var id = AddZone(c, Area.Of(10, 63, 0, 11, 63, 0), plant);
        Assert.StartsWith("2/2 sown, 1 ripe", Zone(c, id)["status"]!.GetValue<string>());
        var j = c.Next();
        Assert.Equal("harvest", Kind(j));
        Assert.Equal("11,64,0", At(j)); // never the young one beside it
    }

    [Fact]
    public void Full_storage_leaves_ripe_crops_standing_while_the_field_is_kept_and_says_which_chests_could_help()
    {
        var chest = new P3(0, 64, 4);
        var (c, _, w) = Farm(null, new Stack(295, 0, 8));
        Field(w, 7, -1);
        var id = AddZone(c, Area.Of(10, 63, 0, 11, 63, 0), "wheat");
        Store(c, chest, new Stack(4, 0, 27 * 64)); // the only storage chest, full of cobblestone
        w.Set(2, 64, 2, 54);                       // a chest by home that isn't storage
        var j = c.Next();
        Assert.Equal("place", Kind(j));            // sowing goes on
        Assert.Equal("11,64,0", At(j));
        Assert.Contains("storage full", Zone(c, id)["status"]!.GetValue<string>());
        var said = Assert.Single(c.Alerts, a => a.Text.StartsWith("Storage is full"));
        Assert.Contains("Wheat", said.Text);
        Assert.Contains("2,64,2", said.Text);
        w.Set(11, 64, 0, 59, 0);
        Done(c, j);
        Assert.NotEqual("harvest", Kind(c.Next())); // still nowhere for the wheat, said once
        Assert.Single(c.Alerts, a => a.Text.StartsWith("Storage is full"));
        // Room again: the ripe wheat is harvested.
        if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
        c.TakeContents(c.StorageAt(chest)!, 4, 0, 64);
        Later();
        var harvest = c.Next();
        Assert.Equal("harvest", Kind(harvest));
        Assert.Equal("10,64,0", At(harvest));
    }

    [Fact]
    public void A_field_is_kept_over_several_growth_cycles_with_its_farmer_never_idle_while_there_is_work()
    {
        var (c, _, w) = Farm(null, new Stack(290, 0, 1), new Stack(295, 0, 32));
        for (int x = 10; x <= 12; x++) w.Set(x, 63, 0, 2); // grass
        w.Set(11, 63, 2, 9);
        AddZone(c, Area.Of(10, 63, 0, 12, 63, 0), "wheat");
        var did = new Dictionary<string, int>();
        void Work(int jobs)
        {
            for (int i = 0; i < jobs; i++)
            {
                var j = c.Next();
                Assert.Equal(WorkTypes.Farm, j["type"]?.GetValue<string>()); // never idle while the field has work
                var at = Pos(At(j));
                switch (Kind(j))
                {
                    case "till": w.Set(at.X, at.Y, at.Z, 60); if (j["clear_at"] != null) w.Set(at.X, at.Y + 1, at.Z, 0); break;
                    case "place": w.Set(at.X, at.Y, at.Z, 59, 0); break;
                    case "harvest": w.Set(at.X, at.Y, at.Z, 59, 0); break; // dug and sown again
                    default: Assert.Fail($"{Kind(j)} at {at}"); break;
                }
                did[Kind(j)] = did.GetValueOrDefault(Kind(j)) + 1;
                Done(c, j);
            }
        }
        Work(6); // till and sow the three cells
        Assert.Equal(3, did["till"]);
        Assert.Equal(3, did["place"]);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            for (int x = 10; x <= 12; x++) w.Set(x, 64, 0, 59, 7); // grown
            Later();
            Work(3);
        }
        Assert.Equal(9, did["harvest"]);
        // A cell trampled back to dirt, tall grass on it: tilled (the grass cut), sown, kept.
        w.Set(11, 63, 0, 3); w.Set(11, 64, 0, 31, 1);
        Later();
        Work(2);
        Assert.Equal(4, did["till"]);
        Assert.Equal(4, did["place"]);
        Assert.StartsWith("3/3 sown", Zone(c, c.Zones.Single().Id)["status"]!.GetValue<string>());
    }

    [Fact]
    public void A_chest_the_owner_places_is_crafted_first_and_is_storage_once_built()
    {
        // Live 11-06: Fox placed 14 chests for room; their crafts waited behind a "forever" bread bill and every build.
        var (c, h, w) = Farm(null, new Stack(295, 0, 16));
        Only(c, WorkTypes.Farm, WorkTypes.Craft, WorkTypes.Build);
        var p = c.Pawns.Values.First();
        p.Priorities[WorkTypes.Craft] = 3; p.Priorities[WorkTypes.Build] = 3;
        w.Set(3, 64, 0, 58);
        Assert.Null(c.WorkbenchOp("designate", new P3(3, 64, 0)).Error);
        Store(c, new P3(0, 64, 9), new Stack(5, 0, 16));
        Field(w, -1); // endless sowing at the farmer's first priority
        AddZone(c, Area.Of(10, 63, 0, 10, 63, 0), "wheat");
        var add = c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "chest", ["blocks"] = new JsonArray(new JsonArray(2, 64, 2)) }), null);
        Assert.Null(add.Error);
        c.State(); // the web polls the state: the builds' materials and their craft tasks are made there (and by any build or craft pick)
        JsonObject? craft = null;
        for (int i = 0; i < 3 && craft == null; i++)
        {
            var j = c.Next();
            if (Kind(j) == "craft") craft = j; else Done(c, j);
        }
        Assert.NotNull(craft);
        Assert.Contains("chest", craft!["item"]!.GetValue<string>());
        Done(c, craft);
        // Built: it is storage, taking anything.
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(54, 0, 1)]);
        Only(c, WorkTypes.Build);
        JsonObject? place = null;
        for (int i = 0; i < 4 && place == null; i++)
        {
            var j = c.Next();
            if (Kind(j) == "place" && j["cells"]?.AsArray().Any(x => x!.GetValue<string>() == "2,64,2") == true) place = j;
            else if (c.CurrentOf(c.P()) is { } cur) c.Report(c.P(), cur.Id, "done", null, null);
        }
        Assert.NotNull(place);
        w.Set(2, 64, 2, 54);
        Done(c, place!);
        var st = c.StorageAt(new P3(2, 64, 2));
        Assert.NotNull(st);
        Assert.Equal(["*"], st!.Filter.Allow);
        Assert.Contains(c.Alerts, a => a.Text.Contains("2,64,2 is storage now"));
    }
}
