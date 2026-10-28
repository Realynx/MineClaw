using System.Text.Json.Nodes;

namespace MineClaw.Scripting.Events;

/// <summary>
/// A game-event filter (<c>on('player.punchBlock', {player:'PoofImaFox', held:'pickaxe'}, fn)</c>), checked in C# before the
/// handler is queued. Keys (all optional, all must hold):
/// <list type="bullet">
/// <item><c>player</c> / <c>from</c>: a name or a list of names (case-insensitive).</item>
/// <item><c>block</c>, <c>item</c>, <c>mob</c> (or <c>kind</c>), <c>cause</c>, <c>intent</c>, <c>target</c> ('me'|'block'), <c>by</c>, <c>to</c>,
/// <c>from_block</c>: a name or list; '_' and ' ' are the same, and a word matches a name ending in it ('log' takes 'log',
/// 'pickaxe' takes 'stone_pickaxe').</item>
/// <item><c>held</c>: a held-item class (pickaxe|axe|shovel|sword|hoe|tool|empty|any) or an item name.</item>
/// <item><c>text</c>: a substring of the line; <c>dist</c>: e.d ≤ dist; <c>minCount</c>: e.count ≥ n.</item>
/// <item><c>pos</c>: that block ({x,y,z} / [x,y,z] / "x,y,z"); <c>near</c>: {x,y,z,r} or [x,y,z,r]; <c>region</c>:
/// {min:{x,y,z}, max:{x,y,z}} or [x1,y1,z1,x2,y2,z2].</item>
/// <item><c>cooldown</c>: seconds between two runs (debounce); <c>once</c>: true = the first match only.</item>
/// </list>
/// Unknown keys are ignored (they may be the handler's own options).
/// </summary>
public sealed class EventFilter
{
    private readonly List<Func<JsonObject, bool>> _checks = new();
    private readonly object _lock = new();
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private bool _spent;

    public TimeSpan Cooldown { get; private init; }
    public bool Once { get; private init; }

    /// <summary>The empty filter: every event passes.</summary>
    public static readonly EventFilter Any = new();

    private static readonly string[] HeldClasses = ["pickaxe", "axe", "shovel", "sword", "hoe"];

    public static EventFilter Parse(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var str)) node = new JsonObject { ["text"] = str };
        if (node is not JsonObject o) return new EventFilter();
        double cd = Num(o["cooldown"]) ?? 0;
        var f = new EventFilter { Cooldown = TimeSpan.FromSeconds(Math.Clamp(cd, 0, 3600)), Once = o["once"] is JsonValue ov && ov.TryGetValue<bool>(out var once) && once };
        if (Names(o["player"] ?? o["from"]) is { } players) f._checks.Add(d => players.Any(p => Eq(p, Str(d, "player"))));
        foreach (var key in new[] { "block", "item", "mob", "cause", "intent", "target", "by", "to", "slot" })
            if (Names(o[key]) is { } want) { var k = key; f._checks.Add(d => want.Any(w => NameMatch(w, Str(d, k)))); }
        if (Names(o["kind"]) is { } kinds) f._checks.Add(d => kinds.Any(w => NameMatch(w, Str(d, "mob"))));
        if (Names(o["from_block"]) is { } fb) f._checks.Add(d => fb.Any(w => NameMatch(w, Str(d, "from"))));
        if (Names(o["held"]) is { } held) f._checks.Add(d => held.Any(h => HeldMatch(h, Str(d, "held") ?? Str(d, "item"))));
        if (Str(o, "text") is { Length: > 0 } text) f._checks.Add(d => Str(d, "text")?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
        if (Num(o["dist"]) is { } dist) f._checks.Add(d => Num(d["d"]) is { } dd && dd <= dist);
        if (Num(o["minCount"]) is { } mc) f._checks.Add(d => (Num(d["count"]) ?? 0) >= mc);
        if (Pos(o["pos"]) is { } at) f._checks.Add(d => Pos(d["pos"]) is { } p && Same(p, at));
        if (Near(o["near"]) is { } near) f._checks.Add(d => Pos(d["pos"]) is { } p && Dist(p, near.P) <= near.R);
        if (Region(o["region"]) is { } reg) f._checks.Add(d => Pos(d["pos"]) is { } p && Inside(p, reg.Min, reg.Max));
        return f;
    }

    /// <summary>The payload passes every check (no cooldown/once bookkeeping: see <see cref="TryFire"/>).</summary>
    public bool Accepts(JsonObject? data)
    {
        data ??= new JsonObject();
        lock (_lock) if (_spent) return false;
        foreach (var c in _checks)
        {
            bool ok;
            try { ok = c(data); } catch (Exception) { ok = false; }
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Consumes one run: false while cooling down or after a <c>once</c> fired.</summary>
    public bool TryFire(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_spent || now - _last < Cooldown) return false;
            _last = now;
            if (Once) _spent = true;
            return true;
        }
    }

    // ------------------------------------------------------------------ matching

    private static string Norm(string s) => s.Trim().Replace(' ', '_').ToLowerInvariant();

    private static bool Eq(string a, string? b) => b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>"log" takes "log"/"oak_log"; "stone pickaxe" takes "stone_pickaxe"; exact otherwise.</summary>
    public static bool NameMatch(string want, string? have)
    {
        if (have == null) return false;
        var w = Norm(want);
        var h = Norm(have);
        return w == h || h.EndsWith("_" + w, StringComparison.Ordinal) || h.StartsWith(w + "_", StringComparison.Ordinal) || w == "any";
    }

    /// <summary>Held-item classes: pickaxe|axe|shovel|sword|hoe = any tier; tool = any of those; empty = nothing; any = whatever.</summary>
    public static bool HeldMatch(string want, string? held)
    {
        var w = Norm(want);
        var h = held == null ? "" : Norm(held);
        if (w == "any") return true;
        if (w is "empty" or "nothing" or "hand") return h is "" or "nothing" or "air" or "empty";
        if (w == "tool") return HeldClasses.Any(c => IsClass(h, c));
        if (HeldClasses.Contains(w)) return IsClass(h, w);
        return NameMatch(w, h);
    }

    // "pickaxe" is also "...axe": the axe class must not take pickaxes.
    private static bool IsClass(string item, string cls) =>
        (item == cls || item.EndsWith("_" + cls, StringComparison.Ordinal)) && !(cls == "axe" && item.EndsWith("pickaxe", StringComparison.Ordinal));

    private static List<string>? Names(JsonNode? n) => n switch
    {
        JsonArray a => a.Select(x => x is JsonValue xv && xv.TryGetValue<string>(out var s) ? s : x?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList() is { Count: > 0 } l ? l : null,
        JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => [s],
        JsonValue v when v.TryGetValue<double>(out var d) => [d.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        _ => null,
    };

    /// <summary>A payload field as text (numbers/bools formatted), or null.</summary>
    public static string? Str(JsonObject o, string key) => o[key] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when Num(v) is { } d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>A number from any JSON value: parsed JSON (JsonElement), C# ints/longs/doubles/floats, or numeric text.</summary>
    private static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<short>(out var sh)) return sh;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        return v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : null;
    }

    private static (double X, double Y, double Z)? Pos(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o when Num(o["x"]) is { } x && Num(o["y"]) is { } y && Num(o["z"]) is { } z: return (x, y, z);
            case JsonArray a when a.Count >= 3 && Num(a[0]) is { } x && Num(a[1]) is { } y && Num(a[2]) is { } z: return (x, y, z);
            case JsonValue v when v.TryGetValue<string>(out var s):
                var p = s.Split(',', StringSplitOptions.TrimEntries);
                return p.Length >= 3 && double.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out var px)
                    && double.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out var py)
                    && double.TryParse(p[2], System.Globalization.CultureInfo.InvariantCulture, out var pz) ? (px, py, pz) : null;
        }
        return null;
    }

    private static ((double X, double Y, double Z) P, double R)? Near(JsonNode? n) => n switch
    {
        JsonObject o when Pos(o) is { } p => (p, Num(o["r"]) ?? 4),
        JsonArray a when Pos(a) is { } p => (p, a.Count >= 4 ? Num(a[3]) ?? 4 : 4),
        _ => null,
    };

    private static ((double X, double Y, double Z) Min, (double X, double Y, double Z) Max)? Region(JsonNode? n)
    {
        (double, double, double)? a = null, b = null;
        if (n is JsonObject o) { a = Pos(o["min"]); b = Pos(o["max"]); }
        else if (n is JsonArray arr && arr.Count >= 6) { a = Pos(new JsonArray(arr[0]?.DeepClone(), arr[1]?.DeepClone(), arr[2]?.DeepClone())); b = Pos(new JsonArray(arr[3]?.DeepClone(), arr[4]?.DeepClone(), arr[5]?.DeepClone())); }
        if (a is not { } p || b is not { } q) return null;
        return ((Math.Min(p.Item1, q.Item1), Math.Min(p.Item2, q.Item2), Math.Min(p.Item3, q.Item3)), (Math.Max(p.Item1, q.Item1), Math.Max(p.Item2, q.Item2), Math.Max(p.Item3, q.Item3)));
    }

    private static bool Same((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Floor(a.X) == Math.Floor(b.X) && Math.Floor(a.Y) == Math.Floor(b.Y) && Math.Floor(a.Z) == Math.Floor(b.Z);

    private static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static bool Inside((double X, double Y, double Z) p, (double X, double Y, double Z) min, (double X, double Y, double Z) max) =>
        Math.Floor(p.X) >= min.X && Math.Floor(p.X) <= max.X && Math.Floor(p.Y) >= min.Y && Math.Floor(p.Y) <= max.Y && Math.Floor(p.Z) >= min.Z && Math.Floor(p.Z) <= max.Z;
}
