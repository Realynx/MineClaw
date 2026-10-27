using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Bot.GameTools;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public class BrainSplitTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static int Tokens(IEnumerable<LlmToolDefinition> defs) =>
        defs.Sum(d => (int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6);

    [Fact]
    public void BotHost_splits_tools_between_brain_and_worker()
    {
        var (h, _) = TestBot.Create();
        var tools = h.Runtime.Tools;
        var brain = tools.Definitions(ModelTier.Main);
        var worker = tools.Definitions(ModelTier.Worker);
        var cheap = tools.Definitions(ModelTier.Cheap);
        var b = brain.Select(d => d.Name).ToHashSet();
        var w = worker.Select(d => d.Name).ToHashSet();

        // Brain: cheap single-shot tools + do (single-step macros) + delegate/ask_memory; no macro definitions of its own.
        foreach (var n in new[] { "status", "inventory", "look", "where_is", "do", "stop", "delegate", "ask_memory", "ask_player", "ask_stand" })
            Assert.Contains(n, b);
        foreach (var n in new[] { "goto", "follow", "chop_tree", "act", "mine", "craft", "smelt", "deposit", "withdraw", "dig", "place", "organize_chest", "window", "recall", "save_routine" })
            Assert.DoesNotContain(n, b);
        // do reaches every single-step macro that exists here.
        var doDef = Assert.Single(brain, d => d.Name == "do");
        foreach (var n in new[] { "goto", "follow", "mine", "harvest_generator", "chop_tree", "deposit", "withdraw", "move_items", "craft", "act", "see" })
            Assert.Contains($"\"{n}\"", doDef.InputSchema.GetRawText());
        // Worker: every game macro tool except open-ended follow; never player-facing or identity tools.
        foreach (var n in GameToolSet.ToolNames.Where(n => n != "follow")) Assert.Contains(n, w);
        foreach (var n in new[] { "delegate", "ask_memory", "ask_player", "ask_stand", "watch_demo", "identity", "reflect_self", "stop", "follow" })
            Assert.DoesNotContain(n, w);

        var brainSys = h.Runtime.Agent.SystemPrompt[0].Text;
        var workerSys = h.Runtime.Worker!.SystemPrompt[0].Text;
        int bt = Tokens(brain), wt = Tokens(worker);
        output.WriteLine($"brain tools: {brain.Count} ≈{bt} tok [{string.Join(", ", b)}]");
        output.WriteLine($"worker tools: {worker.Count} ≈{wt} tok [{string.Join(", ", w)}]");
        output.WriteLine($"cheap tools: {cheap.Count} ≈{Tokens(cheap)} tok [{string.Join(", ", cheap.Select(d => d.Name))}]");
        output.WriteLine($"brain system ≈{brainSys.Length / 3.5:0} tok (no knowledge), worker system ≈{workerSys.Length / 3.5:0} tok (no knowledge)");
        foreach (var d in brain.OrderByDescending(d => Tokens([d]))) output.WriteLine($"  {d.Name} ≈{Tokens([d])}");
        foreach (var d in worker.OrderByDescending(d => Tokens([d]))) output.WriteLine($"  W {d.Name} ≈{Tokens([d])}");
        // The main process also registers the merged hive tool (≤115 tok with script spawn + profile, pinned by
        // MineClaw.Hive.Tests.HiveToolTests; was 75).
        const int hiveTool = Agent.Tools.ToolBudgets.HiveTool;
        output.WriteLine($"brain tools + hive ≈{bt + hiveTool} tok");
        // Cap raised from 2,000 to 2,300 for act + run_command: cached tool tokens cost ~$0.0001/call; the real limit is bloat.
        Assert.Contains("run_command", b);
        // 2,300 → 2,400 for amend_task (≈40) and the script tool's save/rollback/list ops + args/force fields (≈20);
        // → 2,600 for standing_order (≈130) and ask_player default/risky (≈40). Cached: ≈$0.00005/call more.
        // → 2,700 for see (≈127, brain + worker: pictures for building/layout) and look's map flag (≈5).
        // → 2,800 (ToolBudgets.Brain, shared by every budget test) for do, which replaced goto/follow/chop_tree/act.
        Assert.True(bt + hiveTool <= Agent.Tools.ToolBudgets.Brain, $"brain tools ≈{bt} + hive {hiveTool}");
        Assert.True(wt <= Agent.Tools.ToolBudgets.Worker, $"worker tools ≈{wt}"); // raised for the Lua script tool, then see
    }

    [Fact]
    public async Task Status_and_look_add_landmark_and_asker_hints()
    {
        var (h, c) = TestBot.Create();
        h.Runtime.Memory.SetWaypoint("cobble_gen", -2, 65, 0);
        c.World.Entities.SpawnPlayer(7, "kiwi", new Vec3d(-2.5, 65, 0.5), 0, 0);

        var plain = await h.Run("status", new { });
        Assert.Equal("status-line | you: 2 E of cobble_gen", plain.Text);

        TurnContext.Current = new TurnInfo("kiwi", ["kiwi"], false);
        try
        {
            var st = await h.Run("status", new { });
            Assert.Equal("status-line | you: 2 E of cobble_gen; kiwi 3 W of you", st.Text);
            var look = await h.Run("look", new { detail = true });
            Assert.StartsWith("you: 2 E of cobble_gen; kiwi 3 W of you | look r=12 detail=True max=", look.Text);
            Assert.True(look.Text.Length <= 1200);
        }
        finally { TurnContext.Current = null; }
    }
}
