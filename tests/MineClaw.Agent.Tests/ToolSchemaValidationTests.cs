using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

public class ToolSchemaValidationTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly JsonElement Schema = ToolSchema.Create().Str("block", enumValues: ["stone", "dirt"])
        .Int("count", required: false).Num("distance", required: false).Bool("safe", required: false)
        .StrArray("names", required: false)
        .Raw("steps", """{"type":"array","items":{"type":"object","properties":{"to":{"type":"string"}},"required":["to"]}}""", required: false).Build();

    [Theory]
    [InlineData("{}", "$.block")]
    [InlineData("{\"block\":null}", "$.block")]
    [InlineData("{\"block\":12}", "$.block")]
    [InlineData("{\"block\":\"sand\"}", "$.block")]
    [InlineData("{\"block\":\"stone\",\"count\":1.5}", "$.count")]
    [InlineData("{\"block\":\"stone\",\"count\":\"2\"}", "$.count")]
    [InlineData("{\"block\":\"stone\",\"distance\":\"2\"}", "$.distance")]
    [InlineData("{\"block\":\"stone\",\"safe\":\"true\"}", "$.safe")]
    [InlineData("{\"block\":\"stone\",\"names\":[\"a\",2]}", "$.names[1]")]
    [InlineData("{\"block\":\"stone\",\"steps\":[{}]}", "$.steps[0].to")]
    [InlineData("{\"block\":\"stone\",\"steps\":{}}", "$.steps")]
    [InlineData("[]", "$")]
    [InlineData("null", "$")]
    public async Task Invalid_inputs_never_reach_policy_telemetry_or_handler(string json, string path)
    {
        var tools = new ToolRegistry();
        int effects = 0, policies = 0, starts = 0;
        tools.Register("mine", "m", Schema, (_, _) => { effects++; return Task.FromResult(ToolResult.Success("ran")); });
        tools.AddPolicy("mine", (_, _) => { policies++; return null; });
        tools.Executing += (_, _) => starts++;
        var result = await tools.ExecuteAsync("mine", J(json), default);
        Assert.False(result.Ok);
        Assert.StartsWith("bad input", result.Text);
        Assert.Contains(path, result.Text);
        Assert.Equal(0, effects);
        Assert.Equal(0, policies);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Tool_instance_overload_and_replacement_use_their_own_schema()
    {
        var tools = new ToolRegistry();
        int effects = 0;
        var original = new DelegateTool("probe", "p", ToolSchema.Create().Str("old").Build(), (_, _) =>
        { effects++; return Task.FromResult(ToolResult.Success("ran")); });
        tools.Register(original);
        Assert.False((await tools.ExecuteAsync(original, J("{}"), default)).Ok);
        tools.Replace(new DelegateTool("probe", "p", ToolSchema.Create().Bool("new").Build(), (_, _) =>
        { effects++; return Task.FromResult(ToolResult.Success("ran")); }));
        Assert.False((await tools.ExecuteAsync("probe", J("{\"old\":\"yes\"}"), default)).Ok);
        Assert.True((await tools.ExecuteAsync("probe", J("{\"new\":true}"), default)).Ok);
        Assert.Equal(1, effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Brain_calls_return_paired_errors_without_effects(bool streamed)
    {
        var (rt, llm, _) = TestRuntime.Create();
        using (rt)
        {
            int effects = 0;
            var name = streamed ? "look" : "probe";
            rt.Tools.Register(name, "p", Schema, (_, _) => { effects++; return Task.FromResult(ToolResult.Success("ran")); });
            llm.Then(r =>
            {
                var use = new LlmToolUse("invalid_call", name, J("{\"block\":\"sand\"}"));
                if (streamed) r.OnToolUse?.Invoke(use);
                return llm.Resp(r, StopReasons.ToolUse, use);
            }).ThenText("not run");
            await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("previous")]), default);
            var result = llm.Requests[1].Messages.SelectMany(m => m.ToolResults).Single(r => r.ToolUseId == "invalid_call");
            Assert.True(result.IsError);
            Assert.Contains("$.block", result.Content);
            Assert.Equal(0, effects);
        }
    }

    [Fact]
    public async Task Routine_and_compound_steps_cannot_bypass_validation()
    {
        var tools = new ToolRegistry();
        int effects = 0;
        tools.Register("mine", "m", Schema, (_, _) => { effects++; return Task.FromResult(ToolResult.Success("ran")); });
        var routine = new Routine { Name = "bad", Steps = [new() { Tool = "mine", Args = J("{}") }] };
        Assert.False((await new RoutineExecutor(tools).RunAsync(routine, null, default)).Ok);
        DoTool.Register(tools);
        Assert.False((await tools.ExecuteAsync("do", J("{\"mine\":{\"block\":\"sand\"}}"), default)).Ok);
        Assert.Equal(0, effects);
        Assert.True((await tools.ExecuteAsync("do", J("{\"mine\":\"stone\",\"then\":{\"mine\":\"dirt\"}}"), default)).Ok);
        Assert.Equal(2, effects);
    }

    [Fact]
    public async Task Valid_types_and_routing_metadata_survive_validation_but_wrong_types_do_not()
    {
        var tools = new ToolRegistry();
        int effects = 0;
        tools.Register("probe", "p", Schema, (input, _) =>
        {
            effects++;
            Assert.Equal("67", input.GetProperty("body").GetString());
            return Task.FromResult(ToolResult.Success("ran"));
        });
        Assert.False((await tools.ExecuteAsync("probe", J("{\"block\":false,\"body\":\"67\"}"), default)).Ok);
        Assert.True((await tools.ExecuteAsync("probe", J("""{"block":"stone","body":"67","legacy":"kept","count":2.0,"distance":1.5,"safe":false,"names":["a"],"steps":[{"to":"home"}]}"""), default)).Ok);
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task Worker_calls_validate_and_keep_error_pairing()
    {
        var (rt, llm, _) = TestRuntime.Create();
        using (rt)
        {
            int effects = 0;
            rt.Tools.Register("mine", "m", Schema, (_, _) => { effects++; return Task.FromResult(ToolResult.Success("ran")); });
            rt.ApplyBrainSplit();
            llm.ThenTool("mine", new { block = "sand" }, id: "worker_bad").ThenText("unable to mine");
            await rt.Worker!.RunAsync("mine stone", "stone mined", 1, default);
            var result = llm.Requests[1].Messages.SelectMany(m => m.ToolResults).Single(r => r.ToolUseId == "worker_bad");
            Assert.True(result.IsError);
            Assert.Contains("$.block", result.Content);
            Assert.Equal(0, effects);
        }
    }

    [Fact]
    public async Task Activity_steps_validate_before_effects()
    {
        var tools = new ToolRegistry();
        int effects = 0;
        var tool = new DelegateTool("mine", "m", Schema, (_, _) => { effects++; return Task.FromResult(ToolResult.Success("ran")); }, longRunning: true);
        tools.Register(tool);
        var activities = new ActivityManager(tools, new EventQueue());
        var results = await activities.RunAsync([(tool, J("{}"))], TimeSpan.FromSeconds(5), default);
        Assert.False(results[0].Ok);
        Assert.Equal(0, effects);
    }

    [Fact]
    public async Task Explicit_additional_property_restrictions_are_enforced()
    {
        var tools = new ToolRegistry();
        int effects = 0;
        tools.Register("probe", "p", J("""{"type":"object","properties":{"ok":{"type":"boolean"}},"additionalProperties":false}"""),
            (_, _) => { effects++; return Task.FromResult(ToolResult.Success("ran")); });
        Assert.False((await tools.ExecuteAsync("probe", J("{\"extra\":true}"), default)).Ok);
        Assert.True((await tools.ExecuteAsync("probe", J("{\"ok\":true}"), default)).Ok);
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task Validation_errors_respect_result_caps()
    {
        var tools = new ToolRegistry(40);
        tools.Register("probe", "p", ToolSchema.Create().Str(new string('x', 200)).Build(), (_, _) => Task.FromResult(ToolResult.Success("ran")));
        var result = await tools.ExecuteAsync("probe", J("{}"), default);
        Assert.False(result.Ok);
        Assert.Equal(40, result.Text.Length);
    }
}
