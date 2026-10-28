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
        int pi = 0;
        for (int i = 0; i < positional.Count && pi < ps.Count;)
        {
            var (name, type) = ps[pi];
            var v = positional[i];
            if (type == "array" && v is JsonArray arr && !IsPos(arr))
            {
                o[name] = arr.DeepClone();
                i++;
                pi++;
                continue;
            }
            if (type == "array" && v is JsonObject obj && !IsPos(obj))
            {
                var all = new JsonArray();
                for (; i < positional.Count; i++) all.Add(positional[i]?.DeepClone());
                o[name] = all;
                pi++;
                continue;
            }
            if (ScriptSession.PosParams.Contains(name))
            {
                if (i + 2 < positional.Count && ScriptSession.AsNumber(positional[i]) is { } x && ScriptSession.AsNumber(positional[i + 1]) is { } y
                    && ScriptSession.AsNumber(positional[i + 2]) is { } z)
                {
                    o[name] = $"{ScriptSession.Fmt(x)},{ScriptSession.Fmt(y)},{ScriptSession.Fmt(z)}";
                    i += 3;
                    pi++;
                    continue;
                }
                if (v is JsonObject or JsonArray && ScriptSession.PosOf(v) is { } p) { o[name] = ScriptSession.FmtPos(p); i++; pi++; continue; }
            }
            o[name] = v?.DeepClone();
            i++;
            pi++;
        }
        if (named != null)
            foreach (var (k, v) in named)
            {
                var key = k == "n" && ps.Any(p => p.Name == "count") ? "count" : k;
                o[key] = ScriptSession.PosParams.Contains(key) && ScriptSession.PosOf(v) is { } p ? ScriptSession.FmtPos(p) : v?.DeepClone();
            }
        return JsonSerializer.SerializeToElement(o);
    }

    /// <summary>The value as a whole is a position ({x,y,z,...} with numbers, or exactly three numbers).</summary>
    public static bool IsPos(JsonNode? v) => v switch
    {
        JsonObject o => ScriptSession.AsNumber(o["x"]) != null && ScriptSession.PosOf(o) != null,
        JsonArray a => a.Count == 3 && a.All(x => ScriptSession.AsNumber(x) != null),
        _ => false,
    };
}
