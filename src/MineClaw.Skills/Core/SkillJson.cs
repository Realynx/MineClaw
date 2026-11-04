using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>Arg parsing for skills (lenient: numbers as strings, "x,y,z" or {x,y,z} positions) and tiny schemas.</summary>
public static class SkillJson
{
    public static readonly JsonElement EmptySchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
    public static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Schema from a compact spec: "pos:pos,count:int?,item:str?" (? = optional).</summary>
    public static JsonElement Schema(string spec)
    {
        var props = new JsonObject();
        var req = new JsonArray();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':');
            string name = kv[0], type = kv.Length > 1 ? kv[1] : "str";
            bool opt = type.EndsWith('?');
            type = type.TrimEnd('?');
            props[name] = type switch
            {
                "int" => new JsonObject { ["type"] = "integer" },
                "bool" => new JsonObject { ["type"] = "boolean" },
                "pos" => new JsonObject { ["type"] = "string", ["description"] = "x,y,z" },
                "strs" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                _ => new JsonObject { ["type"] = "string" },
            };
            if (!opt) req.Add(name);
        }
        var root = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (req.Count > 0) root["required"] = req;
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    public static string? Str(this JsonElement e, string name) => Prop(e, name) is { } v
        ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Null => null, _ => v.ToString() }
        : null;

    public static int? Int(this JsonElement e, string name) => Prop(e, name) is { } v
        ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
        : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j) ? j : null
        : null;

    public static bool? Bool(this JsonElement e, string name) => Prop(e, name) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
            _ => null,
        }
        : null;

    public static bool Flag(this JsonElement e, string name, bool fallback = false) => e.Bool(name) ?? fallback;

    public static string[] Strs(this JsonElement e, string name) => Prop(e, name) is { } v
        ? v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.ToString()).ToArray()
        : v.ValueKind == JsonValueKind.String ? v.GetString()!.Split('|', ',').Select(s => s.Trim()).ToArray() : []
        : [];

    /// <summary>A position from "x,y,z", "x y z", [x,y,z] or {x,y,z}.</summary>
    public static BlockPos? Pos(this JsonElement e, string name = "pos")
    {
        if (Prop(e, name) is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => PosFromText(v.GetString()!),
            JsonValueKind.Array => PosFromArray(v),
            JsonValueKind.Object => v.Int("x") is { } ox && v.Int("y") is { } oy && v.Int("z") is { } oz ? new BlockPos(ox, oy, oz) : null,
            _ => null,
        };
    }

    private static BlockPos? PosFromText(string text)
    {
        var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) && int.TryParse(parts[2], out var z)
            ? new BlockPos(x, y, z) : null;
    }

    private static BlockPos? PosFromArray(JsonElement v)
    {
        var a = v.EnumerateArray().Select(n => n.TryGetInt32(out var i) ? (int?)i : null).ToArray();
        return a.Length == 3 && a.All(i => i != null) ? new BlockPos(a[0]!.Value, a[1]!.Value, a[2]!.Value) : null;
    }

    /// <summary>Item id by name ("water bucket", "seeds", "bone meal"), with its damage when it is a variant.</summary>
    public static (int Id, int Damage)? Item(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Items.ByNameWithDamage(name) is { } i) return (i.Item.Id, i.Damage);
        if (Blocks.ByName(name) is { } b) return (Items.ItemFormOf(b.Id), 0);
        return null;
    }

    public static string Fmt(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
}
