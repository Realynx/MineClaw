using System.Text.Json;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public class ToolSchemaTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string[] BotTools = [.. GameToolSet.ToolNames, "ask_stand", "watch_demo"];

    [Fact]
    public void All_game_tools_registered_with_valid_compact_schemas()
    {
        var (h, _) = TestBot.Create();
        var defs = h.Runtime.Tools.Definitions().ToDictionary(d => d.Name);
        foreach (var name in BotTools)
        {
            Assert.True(defs.ContainsKey(name), $"missing {name}");
            var d = defs[name];
            Assert.Equal("object", d.InputSchema.GetProperty("type").GetString());
            Assert.True(d.Description.Length <= 125, $"{name} description too long ({d.Description.Length})");
            if (d.InputSchema.TryGetProperty("required", out var req))
                foreach (var r in req.EnumerateArray())
                    Assert.True(d.InputSchema.GetProperty("properties").TryGetProperty(r.GetString()!, out _), $"{name}: required {r} not a property");
        }
        Assert.DoesNotContain("chat", defs.Keys); // plain text output is chat
        Assert.Contains("stop", defs.Keys);       // runtime built-in
    }

    [Fact]
    public void Game_tool_definitions_fit_the_token_budget()
    {
        var (h, _) = TestBot.Create();
        var mine = h.Runtime.Tools.Definitions().Where(d => BotTools.Contains(d.Name)).ToList();
        // Same estimate as the runtime (≈3.5 chars/token over name + description + schema JSON), plus ~6 tokens framing per tool.
        int chars = mine.Sum(d => d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length);
        int tokens = (int)Math.Ceiling(chars / 3.5) + 6 * mine.Count;
        int all = (int)Math.Ceiling(h.Runtime.Tools.Definitions().Sum(d => d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5)
                  + 6 * h.Runtime.Tools.All.Count;
        output.WriteLine($"game tools: {mine.Count} ≈{tokens} tokens; all registered tools: {h.Runtime.Tools.All.Count} ≈{all} tokens");
        foreach (var d in mine) output.WriteLine($"  {d.Name}: ≈{(int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6}");
        Assert.True(tokens <= 1500, $"game tools ≈{tokens} tokens");
    }

    [Fact]
    public void Long_running_flags_match_tool_kind()
    {
        var (h, _) = TestBot.Create();
        foreach (var n in new[] { "status", "inventory", "look", "window" })
        { Assert.True(h.Runtime.Tools.TryGet(n, out var t)); Assert.False(t.IsLongRunning, n); }
        foreach (var n in new[] { "goto", "follow", "mine", "harvest_generator", "collect_drops", "craft", "smelt", "collect_furnace",
                     "deposit", "withdraw", "organize_chest", "place", "dig", "use_block", "eat" })
        { Assert.True(h.Runtime.Tools.TryGet(n, out var t)); Assert.True(t.IsLongRunning, n); }
    }
}

public class ToolMappingTests
{
    [Fact]
    public async Task Goto_coords_waypoint_and_player()
    {
        var (h, c) = TestBot.Create();
        var r = await h.Run("goto", new { to = "3, 65, -2" });
        Assert.True(r.Ok, r.Text);
        Assert.IsType<GoalNear>(c.LastGoal);
        Assert.True(c.LastGoal!.IsReached(3, 65, -2));
        Assert.False(c.LastGotoOptions!.AllowBreak);

        await h.Run("goto", new { to = "3,65,-2", dist = 0, build = true });
        Assert.IsType<GoalBlock>(c.LastGoal);
        Assert.True(c.LastGotoOptions!.AllowBreak && c.LastGotoOptions.AllowPlace);

        h.Runtime.Memory.SetWaypoint("home", 7, 64, 7);
        await h.Run("goto", new { to = "home" });
        Assert.True(c.LastGoal!.IsReached(7, 64, 7));

        c.World.Entities.SpawnPlayer(5, "kiwi", new Vec3d(10.5, 65, 10.5), 0, 0);
        await h.Run("goto", new { to = "kiwi" });
        Assert.IsType<GoalNearPosition>(c.LastGoal);

        var bad = await h.Run("goto", new { to = "narnia" });
        Assert.False(bad.Ok);
        Assert.Contains("narnia", bad.Text);
    }

    [Fact]
    public async Task Actions_map_to_client_calls()
    {
        var (h, c) = TestBot.Create();
        c.World.SetBlock(2, 65, 0, Blocks.Chest, 0);
        c.Results["withdraw"] = () => ActionResult.Fail("chest empty");

        Assert.True((await h.Run("mine", new { block = "sand", count = 5 })).Ok); // cobble goes to the generator (own test)
        await h.Run("follow", new { player = "poofimafox" });
        await h.Run("collect_drops", new { });
        await h.Run("dig", new { at = "1,64,0" });
        await h.Run("use_block", new { at = "1,65,1" });
        await h.Run("place", new { block = "cobblestone", at = "1,65,2" });
        await h.Run("eat", new { });
        await h.Run("deposit", new { items = new[] { "cobblestone" } });
        var w = await h.Run("withdraw", new { item = "oak planks", count = 10 });
        await h.Run("smelt", new { item = "iron ore", count = 3, fuel = "coal" });
        await h.Run("collect_furnace", new { });

        var calls = c.CallList;
        Assert.Contains("mine sand 5 16", calls);
        Assert.Contains("follow poofimafox 3", calls);
        Assert.Contains("collect 8", calls);
        Assert.Contains("dig 1,64,0", calls);
        Assert.Contains("activate 1,65,1", calls);
        Assert.Contains($"place 1,65,2 {Blocks.Cobblestone}", calls);
        Assert.Contains("eat", calls);
        Assert.Contains("deposit 2,65,0 filter=True", calls);
        Assert.True(c.LastDepositFilter!(new ItemStack((short)Blocks.Cobblestone, 1, 0)));
        Assert.False(c.LastDepositFilter!(new ItemStack((short)Blocks.Dirt, 1, 0)));
        Assert.Contains(calls, s => s.StartsWith($"withdraw {Blocks.Planks} 10 2,65,0"));
        Assert.False(w.Ok);
        Assert.Equal("chest empty", w.Text);
        Assert.Contains($"smelt {Blocks.IronOre} 3 fuel={Items.Coal} wait=False at=,,", calls);
    }

    [Fact]
    public async Task Bad_inputs_are_terse_errors_without_client_calls()
    {
        var (h, c) = TestBot.Create();
        Assert.False((await h.Run("dig", new { at = "over there" })).Ok);
        Assert.False((await h.Run("place", new { block = "unobtainium", at = "1,2,3" })).Ok);
        Assert.False((await h.Run("deposit", new { })).Ok); // no chest nearby
        Assert.False((await h.Run("harvest_generator", new { at = "nope", count = 3 })).Ok);
        Assert.Empty(c.CallList);
    }

    [Fact]
    public async Task Harvest_generator_resolves_structure_key_block()
    {
        var (h, c) = TestBot.Create();
        h.Runtime.Memory.UpsertStructure("gen", s =>
        {
            s.Kind = "cobble_generator";
            s.Min = new MineClaw.Agent.Memory.BlockPos(0, 63, 0);
            s.KeyBlocks.Add(new MineClaw.Agent.Memory.KeyBlock { Label = "mine_here", X = 4, Y = 64, Z = -2 });
        });
        c.Give(36, MineClaw.Data.Items.StonePickaxe, 1); // the gen loop never digs bare-handed
        var r = await h.Run("harvest_generator", new { at = "gen", count = 20 });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("generator 4,64,-2 20", c.CallList);
    }

    [Fact]
    public async Task Observation_tools_respect_result_cap()
    {
        var (h, c) = TestBot.Create();
        // detail=true may use look's own larger cap (1200); plain look stays at the small default.
        Assert.Equal("look r=6 detail=True max=1200", (await h.Run("look", new { radius = 6, detail = true })).Text);
        Assert.Equal($"look r=12 detail=False max={h.Runtime.Tools.MaxResultLength}", (await h.Run("look", new { })).Text);
        Assert.Equal($"inv detail=False max={h.Runtime.Tools.MaxResultLength}", (await h.Run("inventory", new { })).Text);
        Assert.Equal("status-line", (await h.Run("status", new { })).Text);
    }
}

public class CrafterTests
{
    [Fact]
    public async Task Places_a_crafting_table_crafted_from_planks_when_none_is_near()
    {
        var (h, c) = TestBot.Create();
        c.Floor();
        c.Give(36, Blocks.Planks, 20);
        var r = await h.Run("craft", new { item = "wooden pickaxe" });
        Assert.True(r.Ok, r.Text);
        var calls = c.CallList;
        Assert.Equal($"craft {Blocks.Workbench} 1", calls[0]);
        Assert.StartsWith("place ", calls[1]);
        Assert.EndsWith($" {Blocks.Workbench}", calls[1]);
        Assert.Equal($"craft {Items.WoodenPickaxe} 1", calls[2]);
        Assert.Contains("placed crafting table", r.Text);
    }

    [Fact]
    public async Task Uses_a_nearby_table_without_placing()
    {
        var (h, c) = TestBot.Create();
        c.Floor();
        c.World.SetBlock(3, 65, 3, Blocks.Workbench, 0);
        c.Give(36, Blocks.Planks, 20);
        await h.Run("craft", new { item = "wooden pickaxe" });
        Assert.Equal([$"craft {Items.WoodenPickaxe} 1"], c.CallList);
    }

    [Fact]
    public async Task Smelts_prerequisites_with_a_carried_furnace_first()
    {
        var (h, c) = TestBot.Create();
        c.Floor();
        c.World.SetBlock(-3, 65, 0, Blocks.Workbench, 0);
        c.Give(36, Blocks.IronOre, 3);
        c.Give(37, Items.Stick, 2);
        c.Give(38, Blocks.Furnace, 1);
        c.Give(39, Items.Coal, 4);
        c.Effects["smelt"] = () => { c.Give(36, Items.IronIngot, 3); };
        var r = await h.Run("craft", new { item = "iron pickaxe" });
        Assert.True(r.Ok, r.Text);
        var calls = c.CallList;
        Assert.StartsWith($"place ", calls[0]);
        Assert.EndsWith($" {Blocks.Furnace}", calls[0]);
        Assert.StartsWith($"smelt {Blocks.IronOre} 3 fuel= wait=True", calls[1]);
        Assert.Equal($"craft {Items.IronPickaxe} 1", calls[2]);
        Assert.Contains("smelted iron ingot x3", r.Text);
    }

    [Fact]
    public async Task Reports_missing_materials()
    {
        var (h, c) = TestBot.Create();
        var r = await h.Run("craft", new { item = "iron pickaxe" });
        Assert.False(r.Ok);
        Assert.Contains("missing", r.Text);
        Assert.Empty(c.CallList);
    }
}

public class OrganizerTests
{
    [Fact]
    public async Task Sorts_and_merges_a_chest_in_place()
    {
        var (h, c) = TestBot.Create();
        c.World.SetBlock(1, 65, 0, Blocks.Chest, 0);
        c.OnOpen = _ =>
        {
            c.World.Inventory.Open(1, WindowType.Chest, "Chest", 27);
            void S(int slot, int id, int n, int d = 0) => c.World.Inventory.SetSlot(1, slot, new ItemStack((short)id, (byte)n, (short)d));
            S(0, Blocks.Dirt, 10);
            S(2, Items.Coal, 5);
            S(5, Blocks.Dirt, 60);
            S(7, Items.StonePickaxe, 1);
            S(9, Items.Coal, 3);
            S(12, Blocks.Log, 4);
        };
        Window? final = null;
        c.OnClose = w => final = w;
        var r = await h.Run("organize_chest", new { at = "1,65,0" });
        Assert.True(r.Ok, r.Text);
        Assert.NotNull(final);
        string Show(int s) { var st = final!.Get(s); return st.IsEmpty ? "-" : $"{st.Id}x{st.Count}"; }
        // tools, ores, wood, blocks; dirt 10+60 → 64 + 6, coal 5+3 → 8.
        Assert.Equal($"{Items.StonePickaxe}x1", Show(0));
        Assert.Equal($"{Items.Coal}x8", Show(1));
        Assert.Equal($"{Blocks.Log}x4", Show(2));
        Assert.Equal(Blocks.Dirt, final!.Get(3).Id);
        Assert.Equal(Blocks.Dirt, final.Get(4).Id);
        Assert.Equal(70, final.Get(3).Count + final.Get(4).Count);
        Assert.Equal("-", Show(5));
        Assert.True(c.World.Inventory.Cursor.IsEmpty);
    }

    [Fact]
    public void Categories()
    {
        Assert.Equal("tools", ItemCategories.Of(Items.StonePickaxe));
        Assert.Equal("ores", ItemCategories.Of(Items.IronIngot));
        Assert.Equal("ores", ItemCategories.Of(Items.Dye, Items.LapisLazuli));
        Assert.Equal("farm", ItemCategories.Of(Items.Dye, Items.BoneMeal));
        Assert.Equal("wood", ItemCategories.Of(Blocks.Sapling));
        Assert.Equal("blocks", ItemCategories.Of(Blocks.Cobblestone));
        Assert.Equal("food", ItemCategories.Of(Items.Bread));
        Assert.Equal("mob", ItemCategories.Of(Items.RottenFlesh));
    }
}

public class ProtectionTests
{
    [Fact]
    public void Config_blocks_regions_and_memory_structures_are_protected()
    {
        var (h, c) = TestBot.Create(new BotOptions { ProtectedRegions = ["10,60,10:12,70,12"], ReservedItems = ["diamond", "nonsense_item"] });
        Assert.Contains("item nonsense_item", h.Protection.Unknown);
        Assert.DoesNotContain(h.Protection.Unknown, u => u.StartsWith("block"));
        Assert.NotNull(c.IsProtected);
        Assert.True(c.IsProtected!(new MineClaw.World.BlockPos(0, 0, 0), Blocks.Chest));
        Assert.False(c.IsProtected(new MineClaw.World.BlockPos(0, 64, 0), Blocks.Cobblestone));
        Assert.True(c.IsProtected(new MineClaw.World.BlockPos(11, 65, 12), Blocks.Cobblestone));
        Assert.Contains(Items.Diamond, c.ReservedItems);

        h.Runtime.Memory.UpsertStructure("farm", s =>
        {
            s.Kind = "farm";
            s.Min = new MineClaw.Agent.Memory.BlockPos(-5, 60, -5);
            s.Max = new MineClaw.Agent.Memory.BlockPos(-3, 62, -3);
        });
        h.Protection.Invalidate();
        Assert.True(c.IsProtected(new MineClaw.World.BlockPos(-4, 61, -4), Blocks.Dirt));
        Assert.False(c.IsProtected(new MineClaw.World.BlockPos(-2, 61, -4), Blocks.Dirt));
    }

    [Fact]
    public void Coordinate_parsing()
    {
        Assert.Equal(new MineClaw.World.BlockPos(3, 65, -2), Locations.ParseCoords("3,65,-2"));
        Assert.Equal(new MineClaw.World.BlockPos(3, 65, -2), Locations.ParseCoords(" 3 65 -2 "));
        Assert.Equal(new MineClaw.World.BlockPos(3, 65, -2), Locations.ParseCoords("x=3, y=65, z=-2"));
        Assert.Null(Locations.ParseCoords("home"));
    }
}
