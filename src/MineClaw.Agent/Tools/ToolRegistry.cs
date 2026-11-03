using System.Runtime.CompilerServices;
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
    private readonly Dictionary<string, List<ToolCallerPolicy>> _policies = new(StringComparer.OrdinalIgnoreCase);
    // Per instance, not name: replacement/wrapping cannot reuse a stale compiled schema.
    private readonly ConditionalWeakTable<IAgentTool, ToolInputValidator> _validators = new();
    private readonly Dictionary<string, (Func<JsonElement, JsonElement> Normalize, ToolInputValidator? Validator)> _inputAdapters = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Swaps in a tool with the same name at the SAME position (cached-prefix order unchanged). Used to wrap a tool's
    /// execution (e.g. append skill hints to a delegated task) without changing its definition. False if absent.
    /// </summary>
    public bool Replace(IAgentTool tool)
    {
        lock (_lock)
        {
            if (!_byName.TryGetValue(tool.Name, out var old)) return false;
            int i = _tools.IndexOf(old);
            _tools[i] = tool;
            _byName[tool.Name] = tool;
        }
        Changed?.Invoke();
        return true;
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

    /// <summary>
    /// Adds a caller policy to a tool (e.g. "only the Opus brain may pass code to script"): checked on every execution with
    /// the ambient <see cref="ToolCaller.Current"/>; a non-null result is returned as the tool's error without running it.
    /// </summary>
    public void AddPolicy(string name, ToolCallerPolicy policy)
    {
        lock (_lock)
        {
            if (!_policies.TryGetValue(name, out var list)) _policies[name] = list = new List<ToolCallerPolicy>();
            list.Add(policy);
        }
    }

    /// <summary>
    /// Registers explicit legacy spellings/defaults before validation and caller policy. An execution-only schema may
    /// include documented aliases omitted from the compact model definition; it must retain the canonical constraints.
    /// Adapters must not perform effects or coerce arbitrary malformed values. Configure at startup, like caller policies.
    /// </summary>
    public void SetInputAdapter(string name, Func<JsonElement, JsonElement> normalize, JsonElement? executionSchema = null)
    {
        var validator = executionSchema is { } schema ? new ToolInputValidator(schema) : null;
        lock (_lock) _inputAdapters[name] = (normalize, validator);
    }

    /// <summary>The refusal a call would get right now (null = allowed).</summary>
    public string? Refusal(string name, JsonElement input, ToolCallerInfo? caller)
    {
        ToolCallerPolicy[] list;
        lock (_lock) list = _policies.TryGetValue(name, out var l) ? l.ToArray() : [];
        foreach (var p in list)
        {
            string? r;
            try { r = p(input, caller); } catch (Exception ex) { r = "refused: " + ex.GetType().Name; }
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>Runs a tool to completion (no activity management). Used by routines and for instant tools.</summary>
    public async Task<ToolResult> ExecuteAsync(string name, JsonElement input, CancellationToken ct)
    {
        if (!TryGet(name, out var tool)) return ToolResult.Error($"unknown tool '{name}'");
        return await ExecuteAsync(tool, input, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Raised as a tool is about to run (after its caller policies passed): (name, input). Telemetry (first action of a chat
    /// turn, whoever runs it: brain, worker, script, reflex). Handlers must be quick.
    /// </summary>
    public event Action<string, JsonElement>? Executing;

    public async Task<ToolResult> ExecuteAsync(IAgentTool tool, JsonElement input, CancellationToken ct)
    {
        ToolResult result;
        try
        {
            ct.ThrowIfCancellationRequested();
            // A model sent arguments that weren't JSON (even after the client's repair attempt): answer, never run.
            if (Llm.LlmToolArgs.IsInvalid(input, out var raw))
                return ToolResult.Error($"invalid arguments for {tool.Name} (not a JSON object): {TextUtil.Truncate(raw, 120)}; call it again with valid JSON");
            (Func<JsonElement, JsonElement>? Normalize, ToolInputValidator? Validator) adapter;
            lock (_lock) adapter = _inputAdapters.TryGetValue(tool.Name, out var a) ? a : default;
            if (adapter.Normalize != null) input = adapter.Normalize(input);
            if ((adapter.Validator ?? _validators.GetValue(tool, t => new ToolInputValidator(t.InputSchema))).Error(input) is { } invalid)
                return ToolResult.Error("bad input: " + invalid).Truncate(CapFor(tool.Name));
            if (Refusal(tool.Name, input, ToolCaller.Current) is { } refused) return ToolResult.Error(refused).Truncate(CapFor(tool.Name));
            try { Executing?.Invoke(tool.Name, input); } catch (Exception) { /* telemetry only */ }
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
