using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Perception;

/// <summary>One look: the contact-sheet PNG, the compact text legend (panels, roles, focus) and the brain's question.</summary>
public sealed record PerceptionQuery(byte[] Png, int Width, int Height, string Question, string Legend)
{
    public int ImageTokens => (Width * Height + 749) / 750;
}

/// <summary>A claim from the perception model: what it saw where. Coordinates are block coords read off the rulers.</summary>
public sealed record PerceptionFact(string Claim, IReadOnlyList<(int X, int Y, int Z)> Coords, string? Role, double Confidence);

/// <summary>What the perception model answered (before verification).</summary>
public sealed record PerceptionResult(IReadOnlyList<PerceptionFact> Facts, string? Answer, string Raw, LlmUsage Usage, string Model,
    string Provider, long LatencyMs, string? Error = null)
{
    public bool Ok => Error == null;
}

/// <summary>
/// The perception brain: any image model behind one call. Claude (Sonnet) is the default; Azure OpenAI, any
/// OpenAI-compatible endpoint and Ollama sit behind the same interface, picked by config alone (<see cref="PerceptionModelFactory"/>).
/// Implementations never throw for model/HTTP failures: they return <see cref="PerceptionResult.Error"/>.
/// </summary>
public interface IPerceptionModel
{
    string Provider { get; }
    string Model { get; }
    Task<PerceptionResult> AskAsync(PerceptionQuery query, CancellationToken ct);
}

/// <summary>The system prompt, user text and strict-JSON answer parsing shared by every provider.</summary>
public static partial class PerceptionPrompt
{
    public const string System =
        "You are the eyes of a Minecraft bot. You get ONE contact-sheet image (several views of the SAME spot), " +
        "a legend and a question. Axes: +x east, -x west, -z north, +z south, +y up. 3D panels look like the game and are titled with " +
        "their viewpoint: EYE = the bot's first person, SHOULDER = behind the bot, ORBIT NW>SE = from the NW corner looking SE, " +
        "HIGH S>N = from high in the south (north up), ISO likewise; a red N arrow sits bottom-right. Players/mobs are drawn as their " +
        "models with name tags, drops as small spinning items with labels. The cyan box is the focus in every panel. The flat TOP map " +
        "(N up, 1 cell = 1 block) carries the coordinates: yellow ruler numbers are the focus coordinates, grey ones every 5 blocks: " +
        "count cells from a labelled tick, never guess. Roles are tints/outlines in 3D and letters on TOP: G cobble-generator cell (MINE HERE " +
        "for cobble), P portal, X never-break, B player build, + placed by the bot, $ protected, H ripe crop, : growing crop " +
        "(crops are never protected). Plain floor/walls are NOT gen cells. Navy hatch = void (a fall kills). " +
        "LAYERS panel (if any): left = blocks AT feet height (solid = a wall beside the bot, NOT floor), right = the layer below; green dot = air with a face to place against. " +
        "ORBIT/HIGH/ISO/TOP cut away roofs above feet height. " +
        "Reply with ONLY this JSON, starting with { (do any counting inside \"look\", never before the JSON): " +
        "{\"look\":\"<=30 words: the ticks/panels you counted from\",\"answer\":\"<=15 words\",\"facts\":[{\"claim\":\"<=10 words\",\"coords\":[[x,y,z]]," +
        "\"role\":\"floor|wall|gen cell|water|lava|void|door|station|player build|own placed|protected|never-break|portal|ripe crop|growing crop|null\"," +
        "\"confidence\":0.0-1.0}]}. 1-4 facts, most useful first, each about specific blocks with their exact coords. " +
        "Say only what you see; omit a fact rather than guess (absence claims like 'no water' need coords too).";

    public static string UserText(PerceptionQuery q) => "legend: " + q.Legend.Trim() + "\nquestion: " + q.Question.Trim();

    [GeneratedRegex(@"-?\d+")]
    private static partial Regex IntRx();

    /// <summary>Lenient parse of the strict schema: code fences, prose around the object, coords as [x,y,z] or "x,y,z".</summary>
    public static (List<PerceptionFact> Facts, string? Answer, string? Error) Parse(string? raw)
    {
        var facts = new List<PerceptionFact>();
        if (string.IsNullOrWhiteSpace(raw)) return (facts, null, "empty answer");
        var t = raw.Trim();
        int a = t.IndexOf('{'), b = t.LastIndexOf('}');
        if (a < 0 || b <= a) return (facts, null, "no JSON object");
        var doc = TryParse(t[a..(b + 1)]) ?? Salvage(t[a..]);
        if (doc == null) return (facts, null, "bad JSON");
        using (doc)
        {
            var root = doc.RootElement;
            string? answer = StringProp(root, "answer");
            if (root.TryGetProperty("facts", out var fs) && fs.ValueKind == JsonValueKind.Array)
                foreach (var f in fs.EnumerateArray())
                    if (FactOf(f) is { } fact) facts.Add(fact);
            return (facts, answer?.Trim(), null);
        }
    }

    private static string? StringProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>One fact object (claim, coords, role, confidence); null when it is not an object or says nothing.</summary>
    private static PerceptionFact? FactOf(JsonElement f)
    {
        if (f.ValueKind != JsonValueKind.Object) return null;
        string claim = StringProp(f, "claim") ?? "";
        var coords = new List<(int, int, int)>();
        if (f.TryGetProperty("coords", out var cs)) ReadCoords(cs, coords);
        string? role = StringProp(f, "role");
        if (role is "null" or "none" or "") role = null;
        double conf = Confidence(f);
        if (claim.Length == 0 && coords.Count == 0) return null;
        return new PerceptionFact(claim.Trim(), coords, role, Math.Clamp(conf, 0, 1));
    }

    /// <summary>The confidence as a number or a numeric string; 0.5 when absent or unreadable.</summary>
    private static double Confidence(JsonElement f)
    {
        if (!f.TryGetProperty("confidence", out var cf)) return 0.5;
        if (cf.ValueKind == JsonValueKind.Number) return cf.GetDouble();
        return cf.ValueKind == JsonValueKind.String && double.TryParse(cf.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cv) ? cv : 0.5;
    }

    private static JsonDocument? TryParse(string s)
    {
        try { return JsonDocument.Parse(s); }
        catch (JsonException) { return null; }
    }

    /// <summary>A reply cut off by max_tokens: keep every complete fact (cut after the last '}' and close the array/object).</summary>
    private static JsonDocument? Salvage(string t)
    {
        int end = t.Length;
        for (int tries = 0; tries < 12; tries++)
        {
            end = t.LastIndexOf('}', end - 1);
            if (end <= 0) return null;
            foreach (var suffix in new[] { "", "]}", "}]}", "]}}" })
                if (TryParse(t[..(end + 1)] + suffix) is { } d) return d;
        }
        return null;
    }

    private static void ReadCoords(JsonElement e, List<(int, int, int)> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Array: ReadCoordArray(e, into); return;
            case JsonValueKind.String: ReadCoordString(e, into); return;
            case JsonValueKind.Object: ReadCoordObject(e, into); return;
        }
    }

    /// <summary>[[x,y,z],...] or a single [x,y,z] (rounded).</summary>
    private static void ReadCoordArray(JsonElement e, List<(int, int, int)> into)
    {
        if (e.GetArrayLength() == 3 && e.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number))
        {
            var v = e.EnumerateArray().Select(n => (int)Math.Round(n.GetDouble())).ToArray();
            into.Add((v[0], v[1], v[2]));
            return;
        }
        foreach (var item in e.EnumerateArray()) ReadCoords(item, into);
    }

    /// <summary>"x,y,z" or any text with exactly three integers.</summary>
    private static void ReadCoordString(JsonElement e, List<(int, int, int)> into)
    {
        var m = IntRx().Matches(e.GetString() ?? "");
        if (m.Count == 3) into.Add((int.Parse(m[0].Value), int.Parse(m[1].Value), int.Parse(m[2].Value)));
    }

    /// <summary>{"x":..,"y":..,"z":..} with three numbers (truncated).</summary>
    private static void ReadCoordObject(JsonElement e, List<(int, int, int)> into)
    {
        if (CoordNumber(e, "x") is { } x && CoordNumber(e, "y") is { } y && CoordNumber(e, "z") is { } z)
            into.Add(((int)x.GetDouble(), (int)y.GetDouble(), (int)z.GetDouble()));
    }

    private static JsonElement? CoordNumber(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v : null;

    /// <summary>Builds the result from a model's raw text (shared by providers).</summary>
    public static PerceptionResult Result(string raw, LlmUsage usage, string model, string provider, long ms)
    {
        var (facts, answer, error) = Parse(raw);
        return new PerceptionResult(facts, answer, raw, usage, model, provider, ms, facts.Count == 0 && answer == null ? error ?? "no facts" : null);
    }
}
