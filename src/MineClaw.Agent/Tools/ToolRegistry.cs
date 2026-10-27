using System.Text.Json;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tools;

/// <summary>
/// Holds the tools the model can call, in registration order (order is part of the cached prompt prefix, so
/// register everything at startup and don't change it afterwards). <see cref="ExecuteAsync"/> never throws for
/// tool failures: exceptions, unknown tools and cancellation become terse error results, and every result is
/// truncated to <see cref="MaxResultLength"/>.
/// </summary>
public sealed class ToolRegistry
{
    private readonly List<IAgentTool> _tools = new();
    private readonly Dictionary<string, IAgentTool> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolTiers> _tiers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _caps = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public ToolRegistry(int maxResultLength = 400) => MaxResultLength = maxResultLength;

    /// <summary>Default hard cap on result text length seen by the model (per-tool caps may raise or lower it).</summary>
    public int MaxResultLength { get; set; }

    /// <summary>Overrides one tool's result cap (e.g. look/window detail up to 1200 while the default stays small).</summary>
    public void SetCap(string name, int maxChars) { lock (_lock) _caps[name] = Math.Max(20, maxChars); }

    /// <summary>Effective result cap for a tool: registry override, else the tool's own, else <see cref="MaxResultLength"/>.</summary>
    public int CapFor(string name)
    {
        lock (_lock)
        {
            if (_caps.TryGetValue(name, out var c)) return c;
            return _byName.TryGetValue(name, out var t) && t.MaxResultChars is { } own ? own : MaxResultLength;
        }
    }

    /// <summary>Overrides which tiers see a tool (applied by the brain/worker split). Changes the cached prefix: startup only.</summary>
    public void SetTiers(string name, ToolTiers tiers)
    {
        lock (_lock) _tiers[name] = tiers;
        Changed?.Invoke();
    }

    /// <summary>Effective tiers of a tool (override, else its own <see cref="IAgentTool.Tiers"/>).</summary>
    public ToolTiers TiersOf(IAgentTool tool)
    {
        lock (_lock) return _tiers.TryGetValue(tool.Name, out var t) ? t : tool.Tiers;
    }

    public bool Allows(IAgentTool tool, ModelTier tier) => (TiersOf(tool) & tier.Flag()) != 0;

    /// <summary>Tool definitions one tier sees, in registration order.</summary>
    public IReadOnlyList<LlmToolDefinition> Definitions(ModelTier tier) => Definitions(t => Allows(t, tier));

    /// <summary>Raised when the tool list changes (the agent rebuilds its cached tool definitions).</summary>
    public event Action? Changed;

    public IReadOnlyList<IAgentTool> All { get { lock (_lock) return _tools.ToArray(); } }

    public ToolRegistry Register(IAgentTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name)) throw new ArgumentException("tool needs a name");
        lock (_lock)
        {
            if (_byName.TryGetValue(tool.Name, out var existing)) _tools.Remove(existing);
            _byName[tool.Name] = tool;
            _tools.Add(tool);
        }
        Changed?.Invoke();
        return this;
    }

    /// <summary>Registers a lambda-backed tool.</summary>
    public ToolRegistry Register(string name, string description, JsonElement schema,
        Func<JsonElement, CancellationToken, Task<ToolResult>> execute, bool longRunning = false, bool cheapTier = false,
        ToolTiers? tiers = null, int? maxResultChars = null) =>
        Register(new DelegateTool(name, description, schema, execute, longRunning, cheapTier, tiers, maxResultChars));

    public bool Remove(string name)
    {
        bool removed;
        lock (_lock)
        {
            removed = _byName.Remove(name, out var t) && _tools.Remove(t);
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public bool TryGet(string name, out IAgentTool tool)
    {
        lock (_lock) return _byName.TryGetValue(name, out tool!);
    }

    public bool Contains(string name) { lock (_lock) return _byName.ContainsKey(name); }

    /// <summary>Tool definitions for the API, in registration order, optionally filtered.</summary>
    public IReadOnlyList<LlmToolDefinition> Definitions(Func<IAgentTool, bool>? filter = null) =>
        All.Where(t => filter == null || filter(t))
           .Select(t => new LlmToolDefinition(t.Name, t.Description, t.InputSchema))
           .ToArray();

    /// <summary>Runs a tool to completion (no activity management). Used by routines and for instant tools.</summary>
    public async Task<ToolResult> ExecuteAsync(string name, JsonElement input, CancellationToken ct)
    {
        if (!TryGet(name, out var tool)) return ToolResult.Error($"unknown tool '{name}'");
        return await ExecuteAsync(tool, input, ct).ConfigureAwait(false);
    }

    public async Task<ToolResult> ExecuteAsync(IAgentTool tool, JsonElement input, CancellationToken ct)
    {
        ToolResult result;
        try
        {
            ct.ThrowIfCancellationRequested();
            result = await tool.ExecuteAsync(input, ct).ConfigureAwait(false) ?? ToolResult.Error("no result");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = ToolResult.Error("cancelled");
        }
        catch (ToolInputException ex)
        {
            result = ToolResult.Error("bad input: " + ex.Message);
        }
        catch (Exception ex)
        {
            result = ToolResult.Error(ex.GetType().Name + ": " + TextUtil.OneLine(ex.Message));
        }
        return result.Truncate(CapFor(tool.Name));
    }
}
