using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Tests;

namespace MineClaw.Skills.Tests;

public class CatalogAndBudgetTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly SkillRegistry Full = SkillRegistry.CreateDefault();

    private static int Tokens(IEnumerable<LlmToolDefinition> defs) =>
        defs.Sum(d => (int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6);

    private static int Tok(string s) => (int)Math.Ceiling(s.Length / 3.5);

    [Theory]
    [InlineData("get me some sugar cane", "harvest_cane")]
    [InlineData("make the platform bigger", "expand_island")]
    [InlineData("expand the floor so you can build it", "expand_island")] // live 10-28 14:40: "no matching skill"
    [InlineData("expand the floor", "expand_island")]
    [InlineData("lay a foundation", "expand_island")]
    [InlineData("put a platform under the tower", "expand_island")]
    [InlineData("fill under the tower", "expand_island")]
    [InlineData("fill in the floor underneath the build", "expand_island")]
    [InlineData("open the door", "open_door")]
    [InlineData("close the gate behind you", "close_door")]
    [InlineData("we need an infinite water source", "infinite_water")]
    [InlineData("harvest the wheat farm", "harvest_wheat")]
    [InlineData("go to the nether", "enter_portal")]
    [InlineData("build a nether portal", "build_portal")]
    [InlineData("smelt the cobblestone into stone", "smelt_batch")]
    [InlineData("sort the chests", "organize_storage")]
    [InlineData("tidy up this chest", "arrange_chest")]
    [InlineData("put all the iron in the ore chest", "move_items")]
    [InlineData("go to sleep it's night", "sleep_in_bed")]
    [InlineData("light up the island so mobs don't spawn", "light_area")]
    [InlineData("make some obsidian", "make_obsidian")]
    [InlineData("dump your inventory before you log off", "deposit_everything")]
    [InlineData("give kiwi 10 cobble", "give_items")]
    public void Catalog_search_finds_the_right_skill(string query, string skill)
    {
        var hits = SkillCatalog.Search(Full.Specs, query, 3);
        output.WriteLine($"{query} -> {string.Join(", ", hits.Select(h => h.Name))}");
        Assert.NotEmpty(hits);
        Assert.Equal(skill, hits[0].Name);
    }

    [Fact]
    public void A_floor_request_offers_expand_island_with_its_area_arg()
    {
        foreach (var q in new[] { "floor", "foundation", "platform under", "expand the floor", "fill under the tower" })
        {
            var hits = SkillCatalog.Search(Full.Specs, q, 3);
            output.WriteLine($"{q} -> {string.Join(", ", hits.Select(h => h.Name))}");
            Assert.Contains(hits, h => h.Name == "expand_island");
        }
        var sig = Full.Specs.Single(s => s.Name == "expand_island").Signature;
        output.WriteLine(sig);
        Assert.StartsWith("expand_island(area?,y?,", sig);
        Assert.Contains("x0,z0,x1,z1", sig);
        Assert.Contains("expand_island(area?", SkillTools.HintedTask(Full, "Fox: expand the floor so you can build design #4"));
    }

    [Fact]
    public void Every_skill_has_a_compact_signature_and_a_valid_spec()
    {
        Assert.True(Full.Count >= 60, $"{Full.Count} skills");
        foreach (var s in Full.Specs)
        {
            Assert.Matches("^[a-z][a-z0-9_]+$", s.Spec().Name);
            Assert.True(s.Signature.Length <= SkillCatalog.MaxSignatureChars, s.Signature);
            Assert.True(Tok(s.Signature) <= 35, $"{s.Name}: ≈{Tok(s.Signature)} tok");
            Assert.True(s.Summary.Length <= 60, s.Name);
            Assert.Equal(JsonValueKind.Object, s.Schema.ValueKind);
        }
        output.WriteLine($"{Full.Count} skills; e.g. {Full.Specs[0].Signature}");
        output.WriteLine($"ready {Full.Specs.Count(s => s.Status == SkillStatus.Ready)}, needs-primitive {Full.Specs.Count(s => s.Status == SkillStatus.NeedsPrimitive)}, stubs {Full.Specs.Count(s => s.Status == SkillStatus.Stub)}");
    }

    [Fact]
    public void Find_skill_and_hints_stay_inside_their_budgets()
    {
        foreach (var q in new[] { "door gate trapdoor lever button open close", "farm harvest plant crops wheat cane cactus melon", "chest storage sort move items", "nether portal" })
        {
            var text = SkillCatalog.Format(SkillCatalog.Search(Full.Specs, q));
            Assert.True(text.Split('\n').Length <= 5, text);
            Assert.True(text.Length <= SkillCatalog.MaxResultChars, $"{text.Length} chars");
            var hints = SkillTools.HintedTask(Full, q)[q.Length..];
            Assert.True(hints.Split('\n').Length <= SkillCatalog.MaxHints + 2, hints);
            Assert.True(Tok(hints) <= 200, $"hints ≈{Tok(hints)} tok: {hints}");
        }
        Assert.Equal("say hi to kiwi", SkillTools.HintedTask(Full, "say hi to kiwi"));
    }

    [Fact]
    public void Tool_budgets_hold_and_the_catalog_costs_zero_prefix_tokens()
    {
        var (h, _) = TestBot.Create();
        var tools = h.Runtime.Tools;
        var brain = tools.Definitions(ModelTier.Main);
        var worker = tools.Definitions(ModelTier.Worker);
        int bt = Tokens(brain), wt = Tokens(worker);
        output.WriteLine($"brain {brain.Count} tools ≈{bt} tok; worker {worker.Count} tools ≈{wt} tok; skill+find_skill ≈{Tokens(worker.Where(d => d.Name is "skill" or "find_skill"))} tok");
        Assert.DoesNotContain(brain, d => d.Name is "skill" or "find_skill");
        Assert.Contains(worker, d => d.Name == "skill");
        Assert.Contains(worker, d => d.Name == "find_skill");
        Assert.True(bt <= Agent.Tools.ToolBudgets.Brain, $"brain ≈{bt}"); // the one shared cap (MineClaw.Agent.Tools.ToolBudgets)
        Assert.True(wt <= Agent.Tools.ToolBudgets.Worker, $"worker ≈{wt}"); // cached prefix, ~$0.00004/call

        // Full catalog (wave 1 + wave 2 stubs + learned skills) vs an empty catalog: identical tool prefix.
        string Prefix(SkillRegistry reg)
        {
            var rt = AgentRuntime.Create(TestBot.Options(), (_, _) => Task.CompletedTask, new NoLlm(), persist: false);
            SkillTools.Register(rt, new FakeGameClient(), registry: reg);
            rt.ApplyBrainSplit();
            return JsonSerializer.Serialize(rt.Tools.Definitions(ModelTier.Worker).Concat(rt.Tools.Definitions(ModelTier.Main))
                .Select(d => new { d.Name, d.Description, Schema = d.InputSchema.GetRawText() }));
        }
        var full = SkillRegistry.CreateDefault();
        full.Add(PromotedSkill.FromStructure("mob farm", "mob_grinder", ["stand at 1,64,1", "wait 5 s"], null));
        Assert.Equal(Prefix(new SkillRegistry()), Prefix(full));
    }

    [Fact]
    public void Delegate_keeps_its_definition_and_position_after_the_hint_wrap()
    {
        var rt = AgentRuntime.Create(TestBot.Options(), (_, _) => Task.CompletedTask, new NoLlm(), persist: false);
        var before = rt.Tools.All.Select(t => t.Name).ToList();
        var def = rt.Tools.Definitions(ModelTier.Main).Single(d => d.Name == "delegate");
        SkillTools.Register(rt, new FakeGameClient());
        var after = rt.Tools.All.Select(t => t.Name).ToList();
        Assert.Equal(before.IndexOf("delegate"), after.IndexOf("delegate"));
        var def2 = rt.Tools.Definitions(ModelTier.Main).Single(d => d.Name == "delegate");
        Assert.Equal(def.Description, def2.Description);
        Assert.Equal(def.InputSchema.GetRawText(), def2.InputSchema.GetRawText());
    }

    [Fact]
    public async Task Find_skill_tool_and_skill_tool_round_trip()
    {
        var (h, c) = TestBot.Create();
        var find = await h.Run("find_skill", new { query = "open the door" });
        Assert.True(find.Ok);
        Assert.StartsWith("open_door(", find.Text);
        Assert.True(find.Text.Length <= SkillCatalog.MaxResultChars);
        var run = await h.Run("skill", new { name = "open_door", args = new { pos = "5,64,5" } });
        Assert.False(run.Ok);
        Assert.Contains("no door", run.Text);
        var stub = await h.Run("skill", new { name = "go_fishing" });
        Assert.Contains("not implemented", stub.Text);
    }

    [Fact]
    public async Task Promoted_routine_and_taught_structure_become_skills()
    {
        var (h, _) = TestBot.Create();
        h.Runtime.Memory.SaveRoutine(new Agent.Memory.Routine { Name = "cobble_loop", Steps = [new() { Tool = "status", Args = JsonSerializer.SerializeToElement(new { }) }] });
        var p = await h.Run("skill", new { name = "promote_routine", args = new { routine = "cobble_loop", summary = "mine the cobble generator" } });
        Assert.True(p.Ok, p.Text);
        var find = await h.Run("find_skill", new { query = "cobble loop routine" });
        Assert.Contains("cobble_loop(", find.Text);
        var r = await h.Run("skill", new { name = "cobble_loop" });
        Assert.True(r.Ok, r.Text);
        h.Runtime.Memory.UpsertStructure("mob farm", s => { s.Kind = "mob_grinder"; s.Steps = ["stand at 0,64,0", "flip lever"]; });
        var f2 = await h.Run("find_skill", new { query = "use the mob farm" });
        Assert.Contains("use_mob_farm(", f2.Text);
        var u = await h.Run("skill", new { name = "use_mob_farm" });
        Assert.Contains("needs_input", u.Text); // "flip lever" has no position: ask instead of guessing
    }
}

internal static class SpecExt
{
    public static SkillSpec Spec(this SkillSpec s) => s;
}
