using System.Text.Json;

namespace MineClaw.Agent.Tools;

/// <summary>Convenience <see cref="IAgentTool"/> backed by a lambda — the usual way game tools get registered.</summary>
public sealed class DelegateTool : IAgentTool
{
    private readonly Func<JsonElement, CancellationToken, Task<ToolResult>> _execute;

    /// <param name="tiers">Explicit tier set; null = brain + worker, plus cheap when <paramref name="cheapTier"/>.</param>
    /// <param name="maxResultChars">Per-tool result cap; null = the registry default.</param>
    public DelegateTool(string name, string description, JsonElement inputSchema,
        Func<JsonElement, CancellationToken, Task<ToolResult>> execute,
        bool longRunning = false, bool cheapTier = false, ToolTiers? tiers = null, int? maxResultChars = null)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
        _execute = execute;
        IsLongRunning = longRunning;
        Tiers = tiers ?? (ToolTiers.Default | (cheapTier ? ToolTiers.Cheap : ToolTiers.None));
        AvailableToCheapTier = Tiers.HasFlag(ToolTiers.Cheap);
        MaxResultChars = maxResultChars;
    }

    public string Name { get; }
    public string Description { get; }
    public JsonElement InputSchema { get; }
    public bool IsLongRunning { get; }
    public bool AvailableToCheapTier { get; }
    public ToolTiers Tiers { get; }
    public int? MaxResultChars { get; }

    public Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct) => _execute(input, ct);
}

/// <summary>Helpers for reading tool input without ceremony.</summary>
public static class ToolInput
{
    public static string? Str(this JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => v.GetRawText(),
            }
            : null;

    public static string ReqStr(this JsonElement input, string name) =>
        input.Str(name) is { Length: > 0 } s ? s : throw new ToolInputException($"missing '{name}'");

    public static int? Int(this JsonElement input, string name)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (int)Math.Round(d);
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var i)) return i;
        return null;
    }

    public static double? Num(this JsonElement input, string name)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    public static bool? Bool(this JsonElement input, string name)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
            _ => null,
        };
    }

    public static List<string> StrList(this JsonElement input, string name)
    {
        var list = new List<string>();
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out var v)) return list;
        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())) list.Add(e.GetString()!);
        }
        else if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
        {
            list.Add(v.GetString()!);
        }
        return list;
    }
}

/// <summary>Thrown by tools for bad input; the registry turns it into an error result.</summary>
public sealed class ToolInputException(string message) : Exception(message);
