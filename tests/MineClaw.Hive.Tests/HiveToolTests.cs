using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Hive.Tests;

public class HiveToolTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Same estimate as Bot.Tests' brain budget check (chars/3.5 + 6 per tool).
    private static int Tokens(IEnumerable<LlmToolDefinition> defs) =>
        defs.Sum(d => (int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6);

    [Fact]
    public async Task Merged_hive_tool_is_brain_only_and_cheap()
    {
        await using var h = new TestHive();
        var tools = new ToolRegistry();
        HiveTools.RegisterMain(tools, h.Supervisor);
        var brain = tools.Definitions(ModelTier.Main);
        Assert.Equal(["hive"], brain.Select(d => d.Name));
        Assert.Empty(tools.Definitions(ModelTier.Worker));
        int t = Tokens(brain);
        output.WriteLine($"hive tool ≈{t} tokens");
        Assert.True(t <= 75, $"hive tool ≈{t} tok");

        var list = await tools.ExecuteAsync("hive", System.Text.Json.JsonSerializer.SerializeToElement(new { op = "list" }), default);
        Assert.StartsWith("67 idle | 69 idle | 420 idle | 1337 idle", list.Text);
        var bad = await tools.ExecuteAsync("hive", System.Text.Json.JsonSerializer.SerializeToElement(new { op = "spawn", name = "99", task = "x" }), default);
        Assert.False(bad.Ok);
    }

    [Fact]
    public void Profile_and_script_ride_the_ipc_task_message()
    {
        var t = new Ipc.TaskMsg("t1", "script cobble_loop", Profile: "haiku", Script: "cobble_loop", Args: """{"n":64}""", After: "sleep");
        var back = System.Text.Json.JsonSerializer.Deserialize<Ipc.TaskMsg>(Ipc.HiveJson.ToElement(t)!.Value, Ipc.HiveJson.Options)!;
        Assert.Equal(("haiku", "cobble_loop", """{"n":64}""", "sleep"), (back.Profile, back.Script, back.Args, back.After));
        Assert.True(back.SleepsAfter);
        // The clone applies it to its brain tier before building its LLM client (CloneMode).
        var clone = new LlmOptions { ApiKey = "sk-ant-test" };
        Assert.Null(clone.ApplyBrainProfile(back.Profile!));
        Assert.Equal("claude-haiku-5-5", clone.ModelFor(ModelTier.Main));
        Assert.False(clone.BrainAuthorsScripts);
    }

    [Fact]
    public async Task Hive_tool_with_script_spawn_and_profiles_stays_small_and_rejects_bad_profiles()
    {
        await using var h = new TestHive();
        var llm = new LlmOptions { ApiKey = "sk-ant-test" };
        h.Supervisor.ProfileCheck = llm.CheckProfile;
        h.Supervisor.ScriptPrep = (script, code) => (script, null, code);
        var tools = new ToolRegistry();
        HiveTools.RegisterMain(tools, h.Supervisor, llm);
        var def = tools.Definitions(ModelTier.Main).Single();
        int t = Tokens([def]);
        output.WriteLine($"hive tool with script/code/args/after/profile ≈{t} tokens");
        Assert.True(t <= 115, $"hive tool ≈{t} tok"); // +≈40 over the plain tool; the usage line is cached prompt text instead
        foreach (var p in new[] { "script", "code", "args", "after", "profile" })
            Assert.True(def.InputSchema.GetProperty("properties").TryGetProperty(p, out _), p);

        var bad = await tools.ExecuteAsync("hive", System.Text.Json.JsonSerializer.SerializeToElement(new { op = "spawn", task = "mine", profile = "gpt9" }), default);
        Assert.False(bad.Ok);
        Assert.Contains("no profile 'gpt9'", bad.Text);
        Assert.All(h.Supervisor.Slots, s => Assert.Equal(Supervisor.CloneState.Idle, s.State)); // nothing launched
    }
}
