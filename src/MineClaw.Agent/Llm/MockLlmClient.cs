using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineClaw.Agent.Llm;

/// <summary>Offline, deterministic replies. First case-insensitive substring rule on the newest user message wins.</summary>
public sealed class MockLlmClient : ILlmClient
{
    private readonly LlmOptions _options;
    private readonly Script _script;
    private long _nextId;

    public MockLlmClient(LlmOptions options)
    {
        _options = options;
        _script = string.IsNullOrWhiteSpace(options.MockScriptPath) ? new Script()
            : JsonSerializer.Deserialize<Script>(File.ReadAllText(options.MockScriptPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Mock script must be a JSON object");
        if (_script.Rules == null || _script.Fallback == null || _script.Rules.Any(r => r == null || string.IsNullOrWhiteSpace(r.Match)
            || r.ToolUse == null || r.ToolUse.Any(t => t == null || string.IsNullOrWhiteSpace(t.Name) || t.Input.ValueKind != JsonValueKind.Object)))
            throw new InvalidDataException("Mock rules need a non-empty match and tool_use entries with name and object input");
    }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var newest = request.Messages.LastOrDefault(m => m.Role == LlmRole.User);
        // Tool-result continuation ends the turn, rather than replaying the original command forever.
        var rule = newest == null || newest.ToolResults.Any() ? null
            : _script.Rules.FirstOrDefault(r => newest.Text.Contains(r.Match, StringComparison.OrdinalIgnoreCase));
        var blocks = new List<LlmBlock>();
        var reply = rule == null ? _script.Fallback : rule.Reply ?? "";
        if (reply.Length > 0) blocks.Add(new LlmText(reply));
        if (rule != null)
            foreach (var tool in rule.ToolUse)
                blocks.Add(new LlmToolUse($"mock_{Interlocked.Increment(ref _nextId)}", tool.Name, tool.Input.Clone()));
        return Task.FromResult(new LlmResponse(blocks, blocks.OfType<LlmToolUse>().Any() ? StopReasons.ToolUse : StopReasons.EndTurn,
            LlmUsage.Zero, request.Model ?? _options.ModelFor(request.Tier)) { Provider = LlmProviders.Mock });
    }

    private sealed class Script
    {
        public string Fallback { get; set; } = "I'm running offline with a scripted brain.";
        public List<Rule> Rules { get; set; } = [];
    }

    private sealed class Rule
    {
        public string Match { get; set; } = "";
        public string? Reply { get; set; }
        [JsonPropertyName("tool_use")]
        public List<Tool> ToolUse { get; set; } = [];
    }

    private sealed class Tool
    {
        public string Name { get; set; } = "";
        public JsonElement Input { get; set; }
    }
}
