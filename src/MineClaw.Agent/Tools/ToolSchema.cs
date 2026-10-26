using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineClaw.Agent.Tools;

/// <summary>
/// Tiny fluent builder for compact JSON input schemas, e.g.
/// <c>ToolSchema.Create().Str("block", "block name").Int("count", "how many", required: false).Build()</c>.
/// Keep descriptions short — schemas ride along (cached) on every request.
/// </summary>
public sealed class ToolSchema
{
    private readonly JsonObject _props = new();
    private readonly JsonArray _required = new();

    public static ToolSchema Create() => new();

    public static JsonElement Empty { get; } = Create().Build();

    public ToolSchema Str(string name, string? description = null, bool required = true, IEnumerable<string>? enumValues = null)
    {
        var o = Prop("string", description);
        if (enumValues != null) o["enum"] = new JsonArray(enumValues.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        return Add(name, o, required);
    }

    public ToolSchema Int(string name, string? description = null, bool required = true) => Add(name, Prop("integer", description), required);
    public ToolSchema Num(string name, string? description = null, bool required = true) => Add(name, Prop("number", description), required);
    public ToolSchema Bool(string name, string? description = null, bool required = true) => Add(name, Prop("boolean", description), required);

    public ToolSchema StrArray(string name, string? description = null, bool required = true)
    {
        var o = Prop("array", description);
        o["items"] = new JsonObject { ["type"] = "string" };
        return Add(name, o, required);
    }

    /// <summary>Adds an arbitrary property schema (raw JSON).</summary>
    public ToolSchema Raw(string name, string schemaJson, bool required = true) =>
        Add(name, JsonNode.Parse(schemaJson)!.AsObject(), required);

    public JsonElement Build()
    {
        var root = new JsonObject { ["type"] = "object", ["properties"] = _props.DeepClone() };
        if (_required.Count > 0) root["required"] = _required.DeepClone();
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonObject Prop(string type, string? description)
    {
        var o = new JsonObject { ["type"] = type };
        if (!string.IsNullOrEmpty(description)) o["description"] = description;
        return o;
    }

    private ToolSchema Add(string name, JsonObject schema, bool required)
    {
        _props[name] = schema;
        if (required) _required.Add(name);
        return this;
    }
}
