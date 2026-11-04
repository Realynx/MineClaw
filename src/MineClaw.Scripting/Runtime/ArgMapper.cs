using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineClaw.Scripting.Runtime;

/// <summary>
/// Script call → tool JSON input, in one place for every runtime. The runtime splits its call into positional values and named
/// ones (already JSON); this fills the tool's params in schema order: a to/at/pos param takes three numbers or a position
/// ("x,y,z", [x,y,z], {x,y,z}); an array param takes an array, or every remaining object (<c>move({item:"log",n:"all"}, ...)</c>);
/// <c>n</c> means count.
/// </summary>
internal static class ArgMapper
{
    public static JsonElement Map(IReadOnlyList<(string Name, string? Type)> ps, IReadOnlyList<JsonNode?> positional, JsonObject? named)
    {
        var o = new JsonObject();
        for (int i = 0, pi = 0; i < positional.Count && pi < ps.Count; pi++)
        {
            var (name, type) = ps[pi];
            i += Fill(o, name, type, positional, i);
        }
        if (named != null) AddNamed(o, ps, named);
        return JsonSerializer.SerializeToElement(o);
    }

    /// <summary>Fills one param from the positional values at <paramref name="i"/>; returns how many it consumed.</summary>
    private static int Fill(JsonObject o, string name, string? type, IReadOnlyList<JsonNode?> positional, int i)
    {
        var v = positional[i];
        if (type == "array" && v is JsonArray arr && !IsPos(arr))
        {
            o[name] = arr.DeepClone();
            return 1;
        }
        if (type == "array" && v is JsonObject obj && !IsPos(obj))
        {
            var all = new JsonArray();
            for (int j = i; j < positional.Count; j++) all.Add(positional[j]?.DeepClone());
            o[name] = all;
            return positional.Count - i;
        }
        if (ScriptSession.PosParams.Contains(name))
        {
            int used = FillPos(o, name, v, positional, i);
            if (used > 0) return used;
        }
        o[name] = v?.DeepClone();
        return 1;
    }

    /// <summary>A to/at/pos param from three numbers (3 consumed) or one position value (1); 0 when neither fits.</summary>
    private static int FillPos(JsonObject o, string name, JsonNode? v, IReadOnlyList<JsonNode?> positional, int i)
    {
        if (i + 2 < positional.Count && ScriptSession.AsNumber(positional[i]) is { } x && ScriptSession.AsNumber(positional[i + 1]) is { } y
            && ScriptSession.AsNumber(positional[i + 2]) is { } z)
        {
            o[name] = $"{ScriptSession.Fmt(x)},{ScriptSession.Fmt(y)},{ScriptSession.Fmt(z)}";
            return 3;
        }
        if (v is JsonObject or JsonArray && ScriptSession.PosOf(v) is { } p)
        {
            o[name] = ScriptSession.FmtPos(p);
            return 1;
        }
        return 0;
    }

    /// <summary>Named values as given; <c>n</c> means count when the tool has one; positions are normalized.</summary>
    private static void AddNamed(JsonObject o, IReadOnlyList<(string Name, string? Type)> ps, JsonObject named)
    {
        foreach (var (k, v) in named)
        {
            var key = k == "n" && ps.Any(p => p.Name == "count") ? "count" : k;
            o[key] = ScriptSession.PosParams.Contains(key) && ScriptSession.PosOf(v) is { } p ? ScriptSession.FmtPos(p) : v?.DeepClone();
        }
    }

    /// <summary>The value as a whole is a position ({x,y,z,...} with numbers, or exactly three numbers).</summary>
    public static bool IsPos(JsonNode? v) => v switch
    {
        JsonObject o => ScriptSession.AsNumber(o["x"]) != null && ScriptSession.PosOf(o) != null,
        JsonArray a => a.Count == 3 && a.All(x => ScriptSession.AsNumber(x) != null),
        _ => false,
    };
}
