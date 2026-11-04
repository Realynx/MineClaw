using System.Text.Json;

namespace MineClaw.Render.Models;

/// <summary>One model reference of a blockstate variant: the model, its x/y rotation and uvlock (<c>Variant</c>).</summary>
public readonly record struct VariantRef(string Model, int X, int Y, bool UvLock);

/// <summary>
/// A 1.12.2 blockstate file (<c>assets/minecraft/blockstates/*.json</c>, <c>ModelBlockDefinition</c>): either
/// <c>variants</c> (a property string such as "facing=east,half=bottom,shape=straight", or "normal", → one model or a
/// weighted list, of which the first is taken: no random choice per position, so pictures are stable) or <c>multipart</c>
/// (cases whose <c>when</c> holds, with <c>OR</c> lists and "a|b" alternatives, each applying its model).
/// <para>Variant matching is by properties, not by string: a variant applies when none of its properties contradicts the
/// state, and the one matching the most properties wins (ties: file order). A property the state doesn't know (an ignored
/// one the mapper dropped, or one MineClaw doesn't compute) therefore never hides a variant.</para>
/// </summary>
public sealed class BlockStateFile
{
    private readonly List<(string Key, Dictionary<string, string> Props, VariantRef Model)> _variants = [];
    private readonly List<(List<Dictionary<string, string[]>>? When, List<VariantRef> Apply)> _parts = [];

    public bool IsMultipart => _parts.Count > 0;
    public int VariantCount => _variants.Count;

    public static BlockStateFile Parse(string json)
    {
        var f = new BlockStateFile();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var r = doc.RootElement;
        if (r.TryGetProperty("variants", out var vs))
            foreach (var v in vs.EnumerateObject())
            {
                var model = Refs(v.Value).FirstOrDefault();
                if (model.Model != null) f._variants.Add((v.Name, Props(v.Name), model));
            }
        if (r.TryGetProperty("multipart", out var mp))
            foreach (var part in mp.EnumerateArray())
            {
                List<Dictionary<string, string[]>>? when = null;
                if (part.TryGetProperty("when", out var w))
                {
                    when = [];
                    if (w.TryGetProperty("OR", out var or)) foreach (var alt in or.EnumerateArray()) when.Add(Condition(alt));
                    else when.Add(Condition(w));
                }
                f._parts.Add((when, part.TryGetProperty("apply", out var ap) ? Refs(ap).Take(1).ToList() : []));
            }
        return f;
    }

    private static Dictionary<string, string[]> Condition(JsonElement e)
    {
        var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
            d[p.Name] = (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.ToString().ToLowerInvariant()).Split('|');
        return d;
    }

    private static IEnumerable<VariantRef> Refs(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) yield return One(x); }
        else if (e.ValueKind == JsonValueKind.Object) yield return One(e);
    }

    private static VariantRef One(JsonElement e) => new(
        e.TryGetProperty("model", out var m) ? m.GetString()! : "missingno",
        e.TryGetProperty("x", out var x) ? x.GetInt32() : 0,
        e.TryGetProperty("y", out var y) ? y.GetInt32() : 0,
        e.TryGetProperty("uvlock", out var u) && u.ValueKind == JsonValueKind.True);

    /// <summary>"facing=east,half=bottom" → its properties; "normal" / "" / "all" → none (seamless double slabs use "all").</summary>
    public static Dictionary<string, string> Props(string key)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in key.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = part.IndexOf('=');
            if (i > 0) d[part[..i]] = part[(i + 1)..];
        }
        return d;
    }

    /// <summary>The models that draw a state (one for variants, every applying case for multipart).</summary>
    /// <param name="key">An exact variant key to prefer ("all" for seamless double slabs), else matched by properties.</param>
    public List<VariantRef> Select(IReadOnlyDictionary<string, string> state, string? key = null)
    {
        if (_parts.Count > 0) return SelectParts(state);
        var list = new List<VariantRef>();
        if (key != null && TryExactVariant(key, out var exact)) { list.Add(exact); return list; }
        if (TryBestVariant(state, out var pick)) list.Add(pick);
        else if (_variants.Count > 0) list.Add(_variants[0].Model);
        return list;
    }

    /// <summary>Multipart: every case whose condition holds (or has none), in file order.</summary>
    private List<VariantRef> SelectParts(IReadOnlyDictionary<string, string> state)
    {
        var list = new List<VariantRef>();
        foreach (var (when, apply) in _parts)
            if (when is null || when.Any(c => c.All(kv => state.TryGetValue(kv.Key, out var v) && kv.Value.Contains(v))))
                list.AddRange(apply);
        return list;
    }

    private bool TryExactVariant(string key, out VariantRef model)
    {
        foreach (var v in _variants)
            if (v.Key == key) { model = v.Model; return true; }
        model = default;
        return false;
    }

    /// <summary>The first variant matching the most of the state's properties (none it contradicts).</summary>
    private bool TryBestVariant(IReadOnlyDictionary<string, string> state, out VariantRef pick)
    {
        int best = -1;
        pick = default;
        for (int i = 0; i < _variants.Count; i++)
        {
            var (_, props, model) = _variants[i];
            int score = VariantScore(props, state);
            if (score > best) { best = score; pick = model; }
        }
        return best >= 0;
    }

    /// <summary>How many of a variant's properties the state shares, or -1 when it contradicts one.</summary>
    private static int VariantScore(Dictionary<string, string> props, IReadOnlyDictionary<string, string> state)
    {
        int score = 0;
        foreach (var (k, v) in props)
        {
            if (!state.TryGetValue(k, out var have)) continue;
            if (have != v) return -1;
            score++;
        }
        return score;
    }
}
