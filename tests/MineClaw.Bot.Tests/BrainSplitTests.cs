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

        // Brain: cheap single-shot tools + delegate/ask_memory; no multi-step game tools.
        foreach (var n in new[] { "status", "inventory", "look", "where_is", "goto", "follow", "stop", "delegate", "ask_memory", "ask_player", "ask_stand" })
            Assert.Contains(n, b);
        foreach (var n in new[] { "mine", "craft", "smelt", "deposit", "withdraw", "dig", "place", "organize_chest", "window", "recall", "save_routine" })
            Assert.DoesNotContain(n, b);
        // Worker: every game macro tool except open-ended follow; never player-facing or identity tools.
        foreach (var n in GameToolSet.ToolNames.Where(n => n != "follow")) Assert.Contains(n, w);
        foreach (var n in new[] { "delegate", "ask_memory", "ask_player", "ask_stand", "watch_demo", "set_name", "reflect_self", "stop", "follow" })
            Assert.DoesNotContain(n, w);

        var brainSys = h.Runtime.Agent.SystemPrompt[0].Text;
        var workerSys = h.Runtime.Worker!.SystemPrompt[0].Text;
        int bt = Tokens(brain), wt = Tokens(worker);
        output.WriteLine($"brain tools: {brain.Count} ≈{bt} tok [{string.Join(", ", b)}]");
        output.WriteLine($"worker tools: {worker.Count} ≈{wt} tok [{string.Join(", ", w)}]");
        output.WriteLine($"cheap tools: {cheap.Count} ≈{Tokens(cheap)} tok [{string.Join(", ", cheap.Select(d => d.Name))}]");
        output.WriteLine($"brain system ≈{brainSys.Length / 3.5:0} tok (no knowledge), worker system ≈{workerSys.Length / 3.5:0} tok (no knowledge)");
        foreach (var d in brain.OrderByDescending(d => Tokens([d]))) output.WriteLine($"  {d.Name} ≈{Tokens([d])}");
        Assert.True(bt <= 2000, $"brain tools ≈{bt}");
        Assert.True(wt <= 1800, $"worker tools ≈{wt}");
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
