using System.Text.Json;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Routines;

/// <summary>
/// Replays a saved <see cref="Routine"/> through the <see cref="ToolRegistry"/> with zero LLM calls. Stops on the
/// first error, on cancellation, or when the stop condition matches; returns one summary line.
/// </summary>
public sealed class RoutineExecutor
{
    /// <summary>Tools a routine may not call (recursion / human interaction / meta tools).</summary>
    public static readonly IReadOnlySet<string> Forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "run_routine", "save_routine", "ask_player", "escalate", "stop", "watch_demo",
    };

    private readonly ToolRegistry _registry;

    public RoutineExecutor(ToolRegistry registry, int maxIterations = 500)
    {
        _registry = registry;
        MaxIterations = maxIterations;
    }

    /// <summary>Safety cap for "repeat until stopped" routines.</summary>
    public int MaxIterations { get; set; }

    /// <summary>Optional progress hook (iteration, step, result) — e.g. to post background progress events.</summary>
    public Action<int, int, ToolResult>? OnStep { get; set; }

    /// <summary>Validates steps against the registry; returns an error message or null.</summary>
    public string? Validate(Routine routine)
    {
        if (routine.Steps.Count == 0) return "routine has no steps";
        foreach (var s in routine.Steps)
        {
            if (Forbidden.Contains(s.Tool)) return $"'{s.Tool}' can't be used in a routine";
            if (!_registry.Contains(s.Tool)) return $"unknown tool '{s.Tool}'";
        }
        return null;
    }

    public async Task<ToolResult> RunAsync(Routine routine, int? repeatOverride, CancellationToken ct)
    {
        var invalid = Validate(routine);
        if (invalid != null) return ToolResult.Error($"routine {routine.Name}: {invalid}");

        int repeat = repeatOverride ?? routine.Repeat;
        int iterations = repeat <= 0 ? MaxIterations : Math.Min(repeat, MaxIterations);
        int steps = 0;
        ToolResult? last = null;

        for (int i = 1; i <= iterations; i++)
        {
            for (int j = 0; j < routine.Steps.Count; j++)
            {
                var step = routine.Steps[j];
                if (ct.IsCancellationRequested)
                    return ToolResult.Error($"routine {routine.Name} cancelled at iter {i} ({steps} steps done)");

                var args = step.Args.ValueKind == JsonValueKind.Undefined ? ToolSchema.Empty : step.Args;
                last = await _registry.ExecuteAsync(step.Tool, args, ct).ConfigureAwait(false);
                steps++;
                OnStep?.Invoke(i, j, last);

                if (ct.IsCancellationRequested)
                    return ToolResult.Error($"routine {routine.Name} cancelled at iter {i} step {j + 1} ({step.Tool})");
                if (!last.Ok)
                    return ToolResult.Error($"routine {routine.Name} stopped at iter {i} step {j + 1} ({step.Tool}): {last.Text}");
                if (!string.IsNullOrEmpty(routine.StopWhen) && last.Text.Contains(routine.StopWhen, StringComparison.OrdinalIgnoreCase))
                    return ToolResult.Success($"routine {routine.Name}: stop condition '{routine.StopWhen}' met at iter {i} ({step.Tool}: {last.Text})");
            }
        }
        return ToolResult.Success($"routine {routine.Name}: {iterations} iteration(s) ok, {steps} steps; last: {last?.Text}");
    }
}
