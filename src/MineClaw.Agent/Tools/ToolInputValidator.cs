using System.Text.Json;

namespace MineClaw.Agent.Tools;

/// <summary>
/// Compiles the input-schema vocabulary emitted by ToolSchema: types, required properties, enums and nested
/// objects/arrays. Unknown properties remain allowed unless the schema explicitly forbids them (body routing and
/// tool-specific aliases use extra fields). This is argument validation, not a replacement for world/caller policy.
/// </summary>
internal sealed class ToolInputValidator
{
    private readonly string? _type;
    private readonly string[] _required;
    private readonly Dictionary<string, ToolInputValidator> _properties = new(StringComparer.Ordinal);
    private readonly JsonElement[]? _enum;
    private readonly ToolInputValidator? _items;
    private readonly bool _allowAdditional = true;

    public ToolInputValidator(JsonElement schema)
    {
        _type = schema.TryGetProperty("type", out var type) ? type.GetString() : null;
        _required = schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(v => v.GetString()!).ToArray() : [];
        if (schema.TryGetProperty("properties", out var properties))
            foreach (var p in properties.EnumerateObject()) _properties[p.Name] = new(p.Value);
        if (schema.TryGetProperty("enum", out var values)) _enum = values.EnumerateArray().Select(v => v.Clone()).ToArray();
        if (schema.TryGetProperty("items", out var items)) _items = new(items);
        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
            _allowAdditional = false;
    }

    public string? Error(JsonElement value, string path = "$")
    {
        bool matches = _type switch
        {
            null => true,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n) && decimal.Truncate(n) == n,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => throw new InvalidOperationException("unsupported tool schema type " + _type),
        };
        if (!matches) return $"{path}: expected {_type}";
        if (_enum != null && !_enum.Any(v => JsonElement.DeepEquals(v, value))) return $"{path}: value is not in enum";
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in _required)
                if (!value.TryGetProperty(name, out _)) return $"{path}.{name}: required";
            foreach (var p in value.EnumerateObject())
            {
                if (_properties.TryGetValue(p.Name, out var property))
                {
                    if (property.Error(p.Value, path + "." + p.Name) is { } error) return error;
                }
                else if (!_allowAdditional) return $"{path}.{p.Name}: unexpected property";
            }
        }
        if (_items != null && value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var item in value.EnumerateArray())
                if (_items.Error(item, $"{path}[{index++}]") is { } error) return error;
        }
        return null;
    }
}
