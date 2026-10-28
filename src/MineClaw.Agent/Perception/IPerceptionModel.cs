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
        "You are the eyes of a Minecraft Beta 1.8.1 skyblock bot. You get ONE contact-sheet image (several panels of the SAME spot), " +
        "a legend and a question. Axes: +x east, -x west, -z north, +z south, +y up; 1 grid cell = 1 block. The focus is the cyan " +
        "box/ring in every panel; yellow ruler numbers are the focus coordinates, grey ones every 5 blocks: count cells from a " +
        "labelled tick, never guess. Role letters: G gen cell (MINE HERE: the only cobble to mine), P portal, X never-break, " +
        "B player build, + placed by the bot, $ protected, H ripe crop, : growing crop (crops are never protected). Plain floor/walls are NOT gen cells. Navy hatch = void (a fall kills). " +
        "LAYERS panel: left grid = blocks AT feet height (a solid cell there is a wall beside the bot at body height, NOT floor), right grid = the layer below (what it stands on); a green dot = air with a solid side/top face (a block can be placed there). " +
        "TOP and ISO cut away roofs above feet height. Count cells from a labelled tick, never guess. " +
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
            string? answer = root.TryGetProperty("answer", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() : null;
            if (root.TryGetProperty("facts", out var fs) && fs.ValueKind == JsonValueKind.Array)
                foreach (var f in fs.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    string claim = f.TryGetProperty("claim", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                    var coords = new List<(int, int, int)>();
                    if (f.TryGetProperty("coords", out var cs)) ReadCoords(cs, coords);
                    string? role = f.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    if (role is "null" or "none" or "") role = null;
                    double conf = f.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number ? cf.GetDouble()
                        : f.TryGetProperty("confidence", out var cs2) && cs2.ValueKind == JsonValueKind.String
                          && double.TryParse(cs2.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cv) ? cv : 0.5;
                    if (claim.Length == 0 && coords.Count == 0) continue;
                    facts.Add(new PerceptionFact(claim.Trim(), coords, role, Math.Clamp(conf, 0, 1)));
                }
            return (facts, answer?.Trim(), null);
        }
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
            case JsonValueKind.Array:
                // [[x,y,z],...] or a single [x,y,z].
                if (e.GetArrayLength() == 3 && e.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number))
                {
                    var v = e.EnumerateArray().Select(n => (int)Math.Round(n.GetDouble())).ToArray();
                    into.Add((v[0], v[1], v[2]));
                    return;
                }
                foreach (var item in e.EnumerateArray()) ReadCoords(item, into);
                return;
            case JsonValueKind.String:
                var m = IntRx().Matches(e.GetString() ?? "");
                if (m.Count == 3) into.Add((int.Parse(m[0].Value), int.Parse(m[1].Value), int.Parse(m[2].Value)));
                return;
            case JsonValueKind.Object:
                if (e.TryGetProperty("x", out var x) && e.TryGetProperty("y", out var y) && e.TryGetProperty("z", out var z)
                    && x.ValueKind == JsonValueKind.Number && y.ValueKind == JsonValueKind.Number && z.ValueKind == JsonValueKind.Number)
                    into.Add(((int)x.GetDouble(), (int)y.GetDouble(), (int)z.GetDouble()));
                return;
        }
    }

    /// <summary>Builds the result from a model's raw text (shared by providers).</summary>
    public static PerceptionResult Result(string raw, LlmUsage usage, string model, string provider, long ms)
    {
        var (facts, answer, error) = Parse(raw);
        return new PerceptionResult(facts, answer, raw, usage, model, provider, ms, facts.Count == 0 && answer == null ? error ?? "no facts" : null);
    }
}
