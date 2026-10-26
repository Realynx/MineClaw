using System.Text.Json;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Routines;

public static class RoutineTools
{
    private const string StepsSchema =
        """{"type":"array","items":{"type":"object","properties":{"tool":{"type":"string"},"args":{"type":"object"}},"required":["tool"]}}""";

    public static void RegisterAll(ToolRegistry registry, MemoryStore memory, RoutineExecutor executor)
    {
        registry.Register("save_routine",
            "Save a working sequence of tool calls as a named routine to replay later without thinking. repeat 0 = until stopped.",
            ToolSchema.Create().Str("name").Raw("steps", StepsSchema).Int("repeat", required: false)
                .Str("stop_when", "stop when a result contains this text", required: false).Str("note", required: false).Build(),
            (input, _) =>
            {
                var routine = new Routine
                {
                    Name = input.ReqStr("name"),
                    Repeat = input.Int("repeat") ?? 1,
                    StopWhen = input.Str("stop_when"),
                    Note = input.Str("note"),
                };
                if (input.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in steps.EnumerateArray())
                    {
                        var tool = s.Str("tool");
                        if (string.IsNullOrEmpty(tool)) continue;
                        var args = s.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.Clone() : ToolSchema.Empty;
                        routine.Steps.Add(new RoutineStep { Tool = tool, Args = args });
                    }
                }
                var invalid = executor.Validate(routine);
                if (invalid != null) return Task.FromResult(ToolResult.Error(invalid));
                memory.SaveRoutine(routine);
                return Task.FromResult(ToolResult.Success($"routine {routine.Name} saved ({routine.Steps.Count} steps)"));
            });

        registry.Register(new DelegateTool("run_routine", "Run a saved routine (no thinking needed per step). Optional repeat override.",
            ToolSchema.Create().Str("name").Int("repeat", required: false).Build(),
            async (input, ct) =>
            {
                var r = memory.GetRoutine(input.ReqStr("name"));
                if (r == null) return ToolResult.Error("no such routine");
                return await executor.RunAsync(r, input.Int("repeat"), ct).ConfigureAwait(false);
            }, longRunning: true));
    }
}
