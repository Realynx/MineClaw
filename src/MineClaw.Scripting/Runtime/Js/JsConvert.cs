using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;

namespace MineClaw.Scripting.Runtime.Js;

/// <summary>JS values ↔ the session's neutral JSON nodes (bounded: depth 8, 256 array items, 64 keys; functions dropped).</summary>
internal static class JsConvert
{
    /// <param name="args">Tool-argument mode: a nested object's <c>n</c> means <c>count</c> (as in the Lua API).</param>
    public static JsonNode? ToNode(JsValue v, int depth = 0, bool args = true)
    {
        if (depth > 8 || v.IsUndefined() || v.IsNull()) return null;
        if (v.IsString()) return JsonValue.Create(v.AsString());
        if (v.IsBoolean()) return JsonValue.Create(v.AsBoolean());
        if (v.IsNumber()) return NumberNode(v.AsNumber());
        return v switch
        {
            Function => null,
            JsArray arr => ArrayNode(arr, depth, args),
            ObjectInstance o => ObjectNode(o, depth, args),
            _ => JsonValue.Create(v.ToString()),
        };
    }

    /// <summary>Whole numbers below 1e15 as integers, non-finite as 0.</summary>
    private static JsonNode NumberNode(double d)
    {
        if (!double.IsFinite(d)) return JsonValue.Create(0);
        return d == Math.Floor(d) && Math.Abs(d) < 1e15 ? JsonValue.Create((long)d) : JsonValue.Create(d);
    }

    private static JsonArray ArrayNode(JsArray arr, int depth, bool args)
    {
        var ja = new JsonArray();
        var len = Math.Min(arr.Length, 256u);
        for (uint i = 0; i < len; i++) ja.Add(ToNode(arr.Get(i), depth + 1, args));
        return ja;
    }

    /// <summary>Enumerable own string keys (first 64), functions dropped; nested <c>n</c> means count in args mode.</summary>
    private static JsonObject ObjectNode(ObjectInstance o, int depth, bool args)
    {
        var jo = new JsonObject();
        int n = 0;
        foreach (var key in o.GetOwnPropertyKeys(Jint.Runtime.Types.String))
        {
            if (n++ >= 64) break;
            var desc = o.GetOwnProperty(key);
            if (!desc.Enumerable) continue;
            var val = o.Get(key);
            if (val is Function) continue;
            var name = key.ToString();
            if (args && name == "n" && depth >= 1) name = "count";
            jo[name] = ToNode(val, depth + 1, args);
        }
        return jo;
    }

    public static JsValue FromNode(Engine e, JsonNode? n, int depth = 0)
    {
        if (n == null || depth > 10) return JsValue.Null;
        switch (n)
        {
            case JsonArray a:
                return new JsArray(e, a.Select(x => FromNode(e, x, depth + 1)).ToArray());
            case JsonObject o:
            {
                var obj = new JsObject(e);
                foreach (var (k, v) in o) obj.Set(k, FromNode(e, v, depth + 1));
                return obj;
            }
            case JsonValue v:
                return ScalarFrom(v);
            default: return JsValue.Null;
        }
    }

    private static JsValue ScalarFrom(JsonValue v) => v.GetValueKind() switch
    {
        JsonValueKind.String => new JsString(v.GetValue<string>()),
        JsonValueKind.Number => new JsNumber(ScriptSession.AsNumber(v) ?? 0),
        JsonValueKind.True => JsBoolean.True,
        JsonValueKind.False => JsBoolean.False,
        _ => JsValue.Null,
    };

    public static JsValue FromJson(Engine e, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return JsValue.Null;
        try { return FromNode(e, JsonNode.Parse(json)); } catch (JsonException) { return JsValue.Null; }
    }

    /// <summary>A plain data object (not an array, function or position) — a call's named options.</summary>
    public static bool IsPlainObject(JsValue v) => v is ObjectInstance and not JsArray and not Function && !v.IsNull();

    /// <summary>JS text of a value for log(): strings raw, everything else as JSON.</summary>
    public static string Text(JsValue v) =>
        v.IsString() ? v.AsString()
        : v.IsUndefined() ? "undefined"
        : v.IsNumber() ? ScriptSession.Fmt(v.AsNumber())
        : v.IsBoolean() ? (v.AsBoolean() ? "true" : "false")
        : ToNode(v, 0, args: false)?.ToJsonString() ?? "null";

    public static string? Str(JsValue v) => v.IsUndefined() || v.IsNull() ? null : v.IsString() ? v.AsString() : Text(v);

    public static double? Num(JsValue v) => v.IsNumber() ? v.AsNumber() : null;

    public static JsValue Arg(JsValue[] a, int i) => i < a.Length ? a[i] : JsValue.Undefined;
}

/// <summary>
/// The runtime's budget hook: Jint checks constraints between statements; every 16 checks the session's <see cref="ScriptSession.Budget"/>
/// runs (stop requests, instruction limit, busy-loop guard, memory guard between host calls).
/// </summary>
internal sealed class JsBudget(ScriptSession s) : Constraint
{
    private int _n;
    private readonly bool _probe = s.Options.DebugProbe;

    public override void Check()
    {
        if (++_n < 16) return;
        _n = 0;
        s.Budget(16);
        if (_probe) s.DebugTick(); // one volatile read unless a debugger watches (then ≈ 5 Hz samples)
    }

    public override void Reset() { }
}
