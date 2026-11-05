using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 11-05 story-1 (main 07783272), Fox: "I'm seeing pawns idle during work hours, and they have active tasks, like the quarry."
/// Twelve stone pickaxes, crafted together, wore out within two minutes of each other mid-quarry ("dig … failed: no stone pickaxe
/// carried" ×12) with no spare in storage (the spare count counted the pickaxes in the miners' own hands); the wooden pickaxe asked for
/// was one bill for one pickaxe, "no materials for it" while a worn wooden pickaxe sat in storage (the planner read it as the tool),
/// then "crafting one" for half an hour while every crafter hauled at priority 2. docs/web/story-mode.md "Tools and gear", "Craft tasks".
/// </summary>
public sealed class StoryToolSupplyTests
{
    private static readonly P3 Chest = new(0, 64, 9), Table = new(3, 64, -3);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T03:50:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void Prio(StoryColony c, int i, params (string Type, int P)[] ps)
    {
        var p = c.Pawns.Values.ElementAt(i);
        foreach (var t in WorkTypes.All) p.Priorities[t] = ps.FirstOrDefault(x => x.Type == t).P;
    }

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();
    private static string Name(FakeStoryWorld w, int id) => w.Data.Item(id).Name;
    private static void Done(StoryColony c, int pawn, JsonObject job) => Assert.True(c.Report(c.P(pawn), Id(job), "done", null, null));

    private static int Fresh(StoryColony c, FakeStoryWorld w, int id) =>
        c.StorageAt(Chest)!.Contents.Where(s => s.Id == id && !StoryTools.Worn(w.Data, s.Id, s.Meta)).Sum(s => s.Count);

    private readonly Clock _clock = new();

    private (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(int pawns, params Stack[] stock)
    {
        var (c, h, w) = StoryRig.Make(pawns, Release1122GameData.Instance, _clock);
        for (int i = 0; i < pawns; i++) h.Lives[c.P(i)] = FakeStoryHost.Bare(i, 64, 0);
        Assert.Null(c.StorageOp("set", [Chest], null, null, null).Error);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        w.Set(Table.X, Table.Y, Table.Z, 58);
        Assert.Null(c.WorkbenchOp("designate", Table).Error);
        return (c, h, w);
    }

    /// <summary>Trees to chop (by hand) north of home, each its own target: work for pawns without a pickaxe.</summary>
    private static void Trees(StoryColony c, FakeStoryWorld w, int n)
    {
        for (int i = 0; i < n; i++) w.Tree(-12 + 4 * i, -8);
        Assert.Null(c.Designate("add", "chop", Area.Of(-13, 60, -9, -12 + 4 * n, 75, -7), null, null).Error);
    }

    [Fact]
    public void Six_miners_without_pickaxes_get_one_task_for_all_of_them_a_crafter_makes_it_first_and_they_haul_meanwhile()
    {
        // Planks and sticks in storage, a worn wooden pickaxe beside them, and a big plank floor queued that would take every plank.
        var (c, h, w) = Rig(7, new Stack(5, 0, 40), new Stack(280, 0, 20), new Stack(270, 58, 1));
        for (int i = 0; i < 6; i++) Prio(c, i, (WorkTypes.Mine, 1), (WorkTypes.Chop, 2));
        Prio(c, 6, (WorkTypes.Chop, 2), (WorkTypes.Craft, 3)); // a crafter whose other work comes first (live: haul 2, craft 3)
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "floor", ["area"] = new JsonArray(20, 64, 20, 29, 64, 29), ["material"] = "planks" }), null).Error);
        c.Designate("add", "mine", null, Enumerable.Range(0, 6).Select(i => new P3(10 + 2 * i, 63, 4)).ToList(), null);
        Trees(c, w, 8);

        // No pickaxe: the stone waits (never leased) and each miner takes its next work: the trees.
        var hauls = Enumerable.Range(0, 6).Select(i => c.Next(i)).ToList();
        Assert.All(hauls, j => Assert.Equal("chop", Kind(j)));
        c.State(); // the materials pass

        // One craft task for all six (and a spare), planned from the planks and sticks alone (the worn one is no pickaxe), ahead of
        // the floor's reservation, and never a bill in the owner's list.
        var bench = c.Benches.Single();
        Assert.Empty(bench.Bills);
        var task = Assert.Single(bench.Tasks, t => t.Recipe == "craft:" + Name(w, 270));
        Assert.Equal(StoryColony.ToolsTag, task.ForBuild);
        Assert.Equal(6 + StoryColony.ToolWantSpare, task.Target);
        Assert.StartsWith("for mining:", task.Reason);
        var st = c.State()["craft_tasks"]!.AsArray().Select(x => x!.AsObject()).Single(x => x["id"]!.GetValue<string>() == task.Id);
        Assert.Equal("immediate", st["priority"]!.GetValue<string>());
        Assert.Equal(7, st["count"]!.GetValue<int>());

        // The crafter makes them before its other work (they hold six pawns' work up).
        var craft = c.Next(6);
        Assert.Equal("craft", Kind(craft));
        Assert.Equal(7, c.CurrentOf(c.P(6))!.Count);
        Done(c, 6, craft);
        Assert.Equal(7, Fresh(c, w, 270));

        // Each miner then takes one (claimed: never the same one twice) for its dig.
        for (int i = 0; i < 6; i++) Done(c, i, hauls[i]);
        var fetched = Enumerable.Range(0, 6).Select(i => c.Next(i)).ToList();
        foreach (var j in fetched)
        {
            Assert.Contains(Kind(j), new[] { "dig", "equip" });
            Assert.Equal(Name(w, 270), j["fetch"]![0]!["item"]!.GetValue<string>());
        }
    }

    [Fact]
    public void A_worn_pickaxe_gets_its_replacement_made_before_it_breaks_and_the_break_is_covered_from_storage()
    {
        var (c, h, w) = Rig(2, new Stack(4, 0, 10), new Stack(280, 0, 10));
        Prio(c, 0, (WorkTypes.Mine, 1));
        Prio(c, 1, (WorkTypes.Haul, 1), (WorkTypes.Craft, 2));
        h.Lives[c.P(0)] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 125, 1)); // 6 of 131 uses left
        c.Designate("add", "mine", null, [new P3(5, 63, 5), new P3(6, 63, 5)], null);
        var dig = c.Next(0);
        Assert.Equal("dig", Kind(dig)); // the worn one still digs (never a lost drop, never a wait)
        Assert.Null(dig["fetch"]);
        c.State();
        var task = Assert.Single(c.Benches.Single().Tasks);
        Assert.Equal("craft:" + Name(w, 274), task.Recipe); // stone: there is cobblestone
        Assert.Equal(1 + StoryColony.ToolWantSpare, task.Target);
        var craft = c.Next(1);
        Assert.Equal("craft", Kind(craft));
        Done(c, 1, craft);
        Assert.Equal(2, Fresh(c, w, 274));

        // It breaks mid-quarry: the next dig fetches the replacement at once, and one stays in storage for the next break.
        h.Lives[c.P(0)] = FakeStoryHost.Bare(0, 64, 0);
        Assert.True(c.Report(c.P(0), Id(dig), "failed", "no stone pickaxe carried", null));
        var next = c.Next(0);
        Assert.Contains(Kind(next), new[] { "dig", "equip" });
        Assert.Equal(Name(w, 274), next["fetch"]![0]!["item"]!.GetValue<string>());
        Assert.All(c.Designations, d => Assert.Null(d.Wait));
        if (Kind(next) == "dig" && next["at"]!.GetValue<string>().Split(',').Select(int.Parse).ToArray() is [var x, var y, var z]) w.Set(x, y, z, 0); // dug
        h.Lives[c.P(0)] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 0, 1)); // it carries the replacement now
        Done(c, 0, next);
        Assert.Equal(1, Fresh(c, w, 274));
    }

    [Fact]
    public void A_worn_tool_in_storage_is_no_tool_the_wait_says_crafting_one_not_no_materials()
    {
        var (c, h, w) = Rig(1, new Stack(5, 0, 6), new Stack(280, 0, 4), new Stack(270, 58, 1)); // a worn wooden pickaxe stored
        Prio(c, 0, (WorkTypes.Mine, 1));
        c.Designate("add", "mine", null, [new P3(5, 63, 5)], null);
        var first = c.Next(0);
        Assert.NotEqual("dig", Kind(first));
        Done(c, 0, first);
        c.State();
        Assert.NotEqual("dig", Kind(c.Next(0)));
        Assert.Equal("needs a wooden pickaxe — crafting one", c.Designations.Single().Wait);
        Assert.Single(c.Benches.Single().Tasks, t => t.Recipe == "craft:" + Name(w, 270));
    }

    [Fact]
    public void Idle_for_a_tool_only_when_the_pawn_has_no_other_work()
    {
        var (c, h, w) = Rig(1);
        Prio(c, 0, (WorkTypes.Mine, 1), (WorkTypes.Chop, 2));
        c.Designate("add", "mine", null, [new P3(5, 63, 5)], null);
        Trees(c, w, 1);
        var haul = c.Next(0);
        Assert.Equal("chop", Kind(haul)); // other work there: it works
        c.Tick();
        Assert.Null(c.IdleReasonOf(c.P(0)));
        for (int y = 64; y <= 68; y++) w.Set(-12, y, -8, 0); // chopped
        Done(c, 0, haul);
        c.Designations.RemoveAll(d => d.Kind == "chop");
        var idle = c.Next(0);
        Assert.NotEqual("dig", Kind(idle));
        Assert.NotEqual("chop", Kind(idle));
        _clock.Now += TimeSpan.FromSeconds(2); // the audit runs once a second
        c.Tick();
        var why = c.IdleReasonOf(c.P(0));
        Assert.NotNull(why); // nothing else to do: the tool is the reason
        Assert.True(why == "tool waiting" || why.Contains("pickaxe"), why);
    }

    [Fact]
    public void An_immediate_task_comes_before_a_long_owner_bill_queue_and_build_ingredients_come_after_it()
    {
        var (c, h, w) = Rig(2, new Stack(4, 0, 64), new Stack(5, 0, 64), new Stack(280, 0, 20));
        Prio(c, 0, (WorkTypes.Mine, 1));
        Prio(c, 1, (WorkTypes.Craft, 1));
        for (int i = 0; i < 5; i++)
            Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = Table.Json(), ["bill"] = new JsonObject { ["recipe"] = "craft:stick", ["mode"] = "forever" } }).Error);
        // A build whose stairs are crafted (a build ingredient: after the owner's bills).
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "stairs", ["blocks"] = new JsonArray(new JsonArray(0, 64, 5)), ["material"] = "cobblestone", ["facing"] = "n" }), null).Error);
        c.Designate("add", "mine", null, [new P3(5, 63, 5)], null);
        Assert.NotEqual("dig", Kind(c.Next(0))); // no pickaxe: it waits
        c.State();
        var bench = c.Benches.Single();
        Assert.Equal(5, bench.Bills.Count); // only the owner's
        var order = c.BenchQueue(bench).ToList();
        Assert.True(c.Immediate(order[0]));
        Assert.Equal(StoryColony.ToolsTag, order[0].ForBuild);
        Assert.All(order.Skip(1).Take(5), b => Assert.Null(b.ForBuild));
        Assert.Equal(c.Builds.Single().Id, order[^1].ForBuild);
        var craft = c.Next(1);
        Assert.Equal("craft", Kind(craft));
        Assert.Contains("Pickaxe", craft["label"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Old_auto_bills_move_to_the_craft_tasks_on_load_and_finished_one_off_bills_go()
    {
        var (c, h, w) = Rig(1);
        var bench = c.Benches.Single();
        var hoe = StoryRecipes.For(w.Data, "crafting_table").First(r => r.Output.Id == 290).Id;
        bench.Bills.AddRange([
            new BillState { Id = "bill1", Recipe = "craft:stick", Mode = "until", Target = 64, Radius = 32 },          // the owner's
            new BillState { Id = "bill2", Recipe = "craft:" + Name(w, 270), Target = 2, ForBuild = StoryColony.ToolsTag, ForItem = Name(w, 270) },
            new BillState { Id = "bill3", Recipe = "craft:stick", Target = 4, ForBuild = "b7", ForItem = "fence", Step = 1 },
            new BillState { Id = "bill4", Recipe = hoe, Target = 1, Done = 1, Ingredients = new StorageFilter { Allow = ["*"] } }, // a farm's hoe, made
            new BillState { Id = "bill5", Recipe = hoe, Target = 1, Done = 1, Radius = 40 },                         // the owner's (own radius)
        ]);
        Assert.True(c.MigrateCraftTasks());
        Assert.Equal(["bill1", "bill5"], bench.Bills.Select(b => b.Id).ToArray());
        Assert.Equal(["bill2", "bill3"], bench.Tasks.Select(b => b.Id).ToArray());
        Assert.False(c.MigrateCraftTasks()); // once
        var bills = c.State()["workbenches"]![0]!["bills"]!.AsArray();
        Assert.Equal(["bill1", "bill5"], bills.Select(b => b!["id"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void A_farms_hoe_is_one_craft_task_with_its_reason_and_goes_when_made()
    {
        var (c, h, w) = Rig(2, new Stack(5, 0, 8), new Stack(280, 0, 4));
        Prio(c, 0, (WorkTypes.Farm, 1));
        Prio(c, 1, (WorkTypes.Craft, 1));
        h.Lives[c.P(0)] = FakeStoryHost.Bare(0, 64, 0, new Stack(295, 0, 5));
        w.Set(10, 63, 0, 2); w.Set(10, 63, 2, 9);
        Assert.Null(c.ZoneOp("add", "farm", Area.Of(10, 63, 0, 10, 63, 0), grow: new JsonObject { ["plant"] = "wheat" }).Error);
        c.Next(0);
        c.Next(0);
        var bench = c.Benches.Single();
        Assert.Empty(bench.Bills);
        var task = Assert.Single(bench.Tasks, t => t.Recipe.Contains("hoe"));
        Assert.Contains("needs a hoe", task.Reason);
        Assert.True(c.Immediate(task));
        var craft = c.Next(1);
        Assert.Equal("craft", Kind(craft));
        Done(c, 1, craft);
        c.State();
        Assert.DoesNotContain(bench.Tasks, t => t.Recipe.Contains("hoe")); // made: gone (never a "wooden hoe 1/1" left behind)
    }
}
