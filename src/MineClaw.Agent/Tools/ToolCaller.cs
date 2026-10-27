using System.Text.Json;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tools;

/// <summary>Which model tier (and model id) issued the tool call being executed.</summary>
public sealed record ToolCallerInfo(ModelTier Tier, string Model)
{
    /// <summary>The Opus-class brain (main tier on the main model; a clone's brain too). Economy mode (brain on the worker
    /// model) is NOT the brain for authorship purposes.</summary>
    /// <remarks>Keyed to the brain tier, so a brain configured on another model keeps authorship (startup warns); a hive clone whose
    /// brain runs on a non-Opus profile (<see cref="LlmOptions.BrainAuthorsScripts"/> false) never has it.</remarks>
    public bool IsBrainModel(LlmOptions o) => Tier == ModelTier.Main && o.BrainAuthorsScripts && string.Equals(Model, o.MainModel, StringComparison.OrdinalIgnoreCase);

    /// <summary>"main/claude-opus-5-5" — the authorship tag used by telemetry.</summary>
    public string Tag => $"{Tier.ToString().ToLowerInvariant()}/{Model}";
}

/// <summary>
/// Ambient caller identity for tool execution: set by the brain loop (BotAgent), the worker loop and the memory loop around
/// the tools they run, and flowing through Task.Run / background activities (AsyncLocal). Null = not a model call
/// (C# code, tests, routines replayed from memory, standing orders resumed by C#).
/// </summary>
public static class ToolCaller
{
    private static readonly AsyncLocal<ToolCallerInfo?> _current = new();

    public static ToolCallerInfo? Current => _current.Value;

    /// <summary>Sets the caller for the current async flow until disposed.</summary>
    public static IDisposable Push(ModelTier tier, string model)
    {
        var prev = _current.Value;
        _current.Value = new ToolCallerInfo(tier, model);
        return new Restore(prev);
    }

    private sealed class Restore(ToolCallerInfo? prev) : IDisposable
    {
        public void Dispose() => _current.Value = prev;
    }
}

/// <summary>
/// A registry-level gate on a tool: (input, caller) → refusal text, or null to allow. Enforced in
/// <see cref="ToolRegistry.ExecuteAsync(IAgentTool, JsonElement, CancellationToken)"/> for every path (brain, worker,
/// scripts, routines), so a policy can't be bypassed by a prompt.
/// </summary>
public delegate string? ToolCallerPolicy(JsonElement input, ToolCallerInfo? caller);
