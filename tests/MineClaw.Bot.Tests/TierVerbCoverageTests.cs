using MineClaw.Agent.Llm;
using MineClaw.Bot.GameTools;
using MineClaw.Skills;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-27 21:26: a worker task "Make torches for PoofimaFox" ended "failed: no drop/give tool to hand PoofimaFox 5 torches"
/// (give_items existed, but only behind find_skill), and at 21:07 clone 67 said it couldn't eat. Every tier, clones included,
/// must reach every verb its task types need, through its own tool definitions, the brain's do, or the worker's skill tool.
/// </summary>
public class TierVerbCoverageTests
{
    /// <summary>Verb → tool or skill names that perform it.</summary>
    private static readonly Dictionary<string, string[]> Verbs = new()
    {
        ["eat"] = ["eat"], ["give"] = ["give_items"], ["drop"] = ["drop_items"], ["equip"] = ["equip_armor"],
        ["deposit"] = ["deposit"], ["withdraw"] = ["withdraw"], ["craft"] = ["craft"], ["smelt"] = ["smelt"], ["place"] = ["place"],
        ["dig"] = ["dig", "mine"], ["goto"] = ["goto"], ["follow"] = ["follow"], ["use_block"] = ["use_block"], ["look"] = ["look"],
        ["see"] = ["see"],
    };

    /// <summary>The brain does single-step jobs itself (do) and hands the rest to the worker; hand-offs are always its own.</summary>
    private static readonly string[] BrainNeeds = ["eat", "give", "drop", "equip", "deposit", "withdraw", "craft", "goto", "follow", "look", "see"];
    /// <summary>The worker runs gather/craft/build/hand-off tasks to completion (follow is open-ended: the brain's).</summary>
    private static readonly string[] WorkerNeeds = ["eat", "give", "drop", "equip", "deposit", "withdraw", "craft", "smelt", "place", "dig",
        "goto", "use_block", "look", "see"];

    private static (HashSet<string> Brain, HashSet<string> Worker) Reach(BotHost h)
    {
        var tools = h.Runtime.Tools;
        var brainDefs = tools.Definitions(ModelTier.Main);
        var brain = brainDefs.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // do's action enum: what the brain runs itself.
        if (brainDefs.FirstOrDefault(d => d.Name == Agent.Tools.DoTool.ToolName) is { } doDef)
            foreach (var a in Agent.Tools.DoTool.Actions)
                if (doDef.InputSchema.GetRawText().Contains($"\"{a}\"", StringComparison.Ordinal)) brain.Add(a);
        var worker = tools.Definitions(ModelTier.Worker).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (worker.Contains(SkillHub.SkillTool))
            foreach (var s in SkillRegistry.CreateDefault().Specs) worker.Add(s.Name);
        return (brain, worker);
    }

    private static void AssertCovers(HashSet<string> reach, IEnumerable<string> needs, string tier)
    {
        var missing = needs.Where(v => !Verbs[v].Any(reach.Contains)).ToList();
        Assert.True(missing.Count == 0, $"{tier} can't {string.Join(", ", missing)} (reach: {string.Join(", ", reach.OrderBy(x => x))})");
    }

    [Fact]
    public void Main_brain_and_worker_reach_every_verb_their_tasks_need()
    {
        var (h, _) = TestBot.Create();
        using var _h = h;
        var (brain, worker) = Reach(h);
        AssertCovers(brain, BrainNeeds, "brain");
        AssertCovers(worker, WorkerNeeds, "worker");
        // The worker knows hand-overs are skills without searching the catalog first.
        var sys = h.Runtime.Worker!.SystemPrompt[0].Text;
        Assert.Contains("give_items{player,item,count}", sys);
        Assert.Contains("drop_items", sys);
    }

    [Fact]
    public void A_clone_keeps_every_verb_after_its_tool_filtering()
    {
        var (h, _) = TestBot.Create();
        using var _h = h;
        // CloneMode's filtering, verbatim: the identity tool goes (main owns the shared identity row).
        h.Runtime.Tools.Remove(MineClaw.Agent.Memory.IdentityTools.ToolName);
        var (brain, worker) = Reach(h);
        AssertCovers(brain, BrainNeeds, "clone brain");
        AssertCovers(worker, WorkerNeeds, "clone worker");
    }

    [Theory]
    [InlineData("Make torches for PoofimaFox: from chest 14,64,-5 take the coal and sticks, craft a stack of torches and bring them to him")]
    [InlineData("Make torches for PoofimaFox: from chest 14,64,-5 take the coal, craft torches")]
    [InlineData("give kiwi 5 bread from the chest")]
    public void A_hand_off_task_always_carries_the_give_skill(string task)
    {
        var hinted = SkillTools.HintedTask(SkillRegistry.CreateDefault(), task);
        Assert.Contains("give_items", hinted);
    }

    [Fact]
    public void A_plain_job_gets_no_give_hint()
    {
        var hinted = SkillTools.HintedTask(SkillRegistry.CreateDefault(), "mine 32 cobblestone at the gen and store it in the chest");
        Assert.DoesNotContain("give_items", hinted);
    }
}
