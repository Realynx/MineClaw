using System.Globalization;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Bot.Blueprints;

/// <summary>One applied canvas op: what the AI (or the user) did and the cells it changed.</summary>
public sealed record DesignerOp(string Kind, string Label, IReadOnlyList<CanvasCell> Cells, string By = "ai");

/// <summary>
/// The Design Studio AI's tool set over the sandbox canvas (never the world): place, box (fill / hollow / walls), line, remove,
/// replace, clear, describe. Below the grass (y &lt; 0) the canvas is solid ground: remove (air) digs it out, the block "earth"
/// fills it back, any block placed there replaces the ground. Blocks are spelled as in a blueprint palette ("planks", "cobblestone_stairs:north", "wooden_door:south",
/// "wool:red", "log:spruce", "torch:5") and resolved by <see cref="BlueprintParser.Resolve"/>, so whatever lands on the canvas is
/// something a design can hold. Descriptions are short on purpose (they ride in the cached prefix of every call).
/// </summary>
public static class DesignerTools
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string P3 = """{"type":"array","items":{"type":"integer"},"minItems":3,"maxItems":3}""";

    /// <summary>An object schema from "name": schema pairs (the required names first in <paramref name="required"/>).</summary>
    private static JsonElement Obj(string props, params string[] required) =>
        Schema("{\"type\":\"object\",\"properties\":{" + props + "}" + (required.Length > 0 ? ",\"required\":[" + string.Join(",", required.Select(r => "\"" + r + "\"")) + "]" : "") + "}");

    private const string Str0 = "{\"type\":\"string\"}";

    public static readonly IReadOnlyList<LlmToolDefinition> All =
    [
        new("place", "Set single blocks.", Obj("\"b\":{\"type\":\"string\",\"description\":\"block spec\"},\"at\":{\"type\":\"array\",\"items\":" + P3 + ",\"maxItems\":512}", "b", "at")),
        new("box", "Box from..to (inclusive). mode fill (default) | hollow (shell, inside air) | walls (4 sides, inside air, no floor/roof).",
            Obj("\"b\":" + Str0 + ",\"from\":" + P3 + ",\"to\":" + P3 + ",\"mode\":{\"type\":\"string\",\"enum\":[\"fill\",\"hollow\",\"walls\"]}", "b", "from", "to")),
        new("line", "Straight line of blocks from..to.", Obj("\"b\":" + Str0 + ",\"from\":" + P3 + ",\"to\":" + P3, "b", "from", "to")),
        new("remove", "Set a box to air (below ground: dig it out).", Obj("\"from\":" + P3 + ",\"to\":" + P3, "from", "to")),
        new("replace", "Replace block a (or \"any\") with b, in a box (default: everything).",
            Obj("\"a\":" + Str0 + ",\"b\":" + Str0 + ",\"from\":" + P3 + ",\"to\":" + P3, "a", "b")),
        new("clear", "Empty the whole canvas.", Obj("")),
        new("describe", "Canvas summary, or one layer y as rows with a legend.", Obj("\"y\":{\"type\":\"integer\"}")),
    ];

    /// <summary>The cached system prompt (≈400 tokens): the plot, the ground, the conventions and the palette.</summary>
    public static string System(string? player) =>
        $"You are the Design Studio builder: you co-design a Minecraft {GameData.Current.VersionId} build with {player ?? "the player"} on a sandbox " +
        "canvas (not the world; nothing is spent). Plot: x,z 0..63, y -16..47; +x east, +z south (north = -z); the view starts at the " +
        "centre (32,0,32). y=0 is the first layer above flat grass; y=-1 is the grass layer and y -16..-1 is solid ground: dig it with " +
        "remove (that air is dug out when built), then build floors, walls, stairs or ladders down there (basements, cellars, " +
        "foundations); \"earth\" fills dug cells back. Edit only with the tools, coordinates inclusive. Prefer few big calls (box, line) " +
        "over many places. A saved design is at most 32 wide/deep and 32 high, dug layers included: stay inside that. Blocks (design palette only): stone cobblestone " +
        "mossy_cobblestone planks log:oak|spruce|birch sandstone bricks stone_bricks:mossy|cracked obsidian glass glass_pane wool:<colour> " +
        "glowstone bookshelf clay netherrack snow_block fence fence_gate iron_bars ladder:<wall side> torch:1-4 (on a wall: 1 west 2 east 3 north " +
        "4 south of it) torch:5 (floor) trapdoor slab:stone|sandstone|wood|cobble|brick|stonebrick wooden_stairs cobblestone_stairs brick_stairs " +
        "stone_brick_stairs (:dir = the way up) wooden_door iron_door (:dir = the side it opens to; place the lower half, the top is added; it " +
        "stands on a full block) chest furnace dispenser (:dir = front) crafting_table pumpkin jack_o_lantern sand gravel. No grass, leaves, " +
        "liquids, beds, signs. Doors and windows need openings in walls. When the user names \"the selection\" use its box. Reply after the " +
        "edits with ONE or two short sentences on what changed, in plain words (no coordinates unless asked).";

    /// <summary>Runs one tool call on the canvas: (text for the model, the op applied or null, whether it was an error).</summary>
    public static (string Text, DesignerOp? Op, bool Error) Run(DesignerCanvas canvas, string name, JsonElement input)
    {
        try
        {
            switch (name)
            {
                case "describe":
                    return (input.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number ? canvas.Layer(y.GetInt32()) : canvas.Describe(16), null, false);
                case "clear":
                    return Done(canvas, "clear", "cleared the canvas", canvas.Clear(), 0);
                case "remove":
                    {
                        var (a, b) = (P(input, "from"), P(input, "to"));
                        return Done(canvas, "remove", $"removed {Box(a, b)}", canvas.Apply(Cells(a, b, (_, _, _) => true, 0, 0), out var rej), rej);
                    }
                case "place":
                    {
                        var (id, meta) = Block(input, "b");
                        if (!input.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Array) throw new ArgumentException("at: [[x,y,z],...]");
                        var cells = at.EnumerateArray().Select(e => V(e)).Select(p => new CanvasCell(p.X, p.Y, p.Z, id, meta)).ToList();
                        return Done(canvas, "place", $"placed {Name(id, meta)}", canvas.Apply(cells, out var rej), rej);
                    }
                case "box":
                    {
                        var (id, meta) = Block(input, "b");
                        var (a, b) = (P(input, "from"), P(input, "to"));
                        var mode = input.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "fill" : "fill";
                        var (lo, hi) = Norm(a, b);
                        Func<int, int, int, bool> shell = mode switch
                        {
                            "hollow" => (x, yy, z) => x == lo.X || x == hi.X || yy == lo.Y || yy == hi.Y || z == lo.Z || z == hi.Z,
                            "walls" => (x, _, z) => x == lo.X || x == hi.X || z == lo.Z || z == hi.Z,
                            _ => (_, _, _) => true,
                        };
                        // hollow/walls: the inside becomes air (an open interior, as the DSL's own primitives draw it).
                        var cells = mode is "hollow" or "walls"
                            ? Cells(a, b, (x, yy, z) => true, 0, 0).Select(c => shell(c.X, c.Y, c.Z) ? c with { Id = id, Meta = meta } : c)
                            : Cells(a, b, shell, id, meta);
                        return Done(canvas, mode == "fill" ? "fill" : mode, $"{(mode == "fill" ? "filled" : mode)} {Box(a, b)} with {Name(id, meta)}", canvas.Apply(cells, out var rej), rej);
                    }
                case "line":
                    {
                        var (id, meta) = Block(input, "b");
                        var (a, b) = (P(input, "from"), P(input, "to"));
                        return Done(canvas, "line", $"line of {Name(id, meta)}", canvas.Apply(Line(a, b).Select(p => new CanvasCell(p.X, p.Y, p.Z, id, meta)), out var rej), rej);
                    }
                case "replace":
                    {
                        var aSpec = Str(input, "a");
                        (int Id, int Meta)? from = aSpec.Equals("any", StringComparison.OrdinalIgnoreCase) ? null : Block(input, "a");
                        var (id, meta) = Block(input, "b");
                        ((int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi) box = input.TryGetProperty("from", out _) ? Norm(P(input, "from"), P(input, "to")) : ((0, -DesignerCanvas.Depth, 0), (DesignerCanvas.Size - 1, DesignerCanvas.Height - 1, DesignerCanvas.Size - 1));
                        var (lo, hi) = (box.Lo, box.Hi);
                        var cells = canvas.Blocks.Where(kv => kv.Key.X >= lo.X && kv.Key.X <= hi.X && kv.Key.Y >= lo.Y && kv.Key.Y <= hi.Y && kv.Key.Z >= lo.Z && kv.Key.Z <= hi.Z)
                            .Where(kv => from == null ? !(BuildBlocks.KindOf(kv.Value.Id) == BuildKind.Door) : kv.Value.Id == from.Value.Id && (BuildBlocks.HasVariants(kv.Value.Id) ? kv.Value.Meta == from.Value.Meta : true))
                            .Select(kv => new CanvasCell(kv.Key.X, kv.Key.Y, kv.Key.Z, id, BuildBlocks.Directional(id) && BuildBlocks.KindOf(kv.Value.Id) == BuildBlocks.KindOf(id) ? kv.Value.Meta : meta)).ToList();
                        return Done(canvas, "replace", $"replaced {aSpec} with {Name(id, meta)}", canvas.Apply(cells, out var rej), rej);
                    }
            }
            return ($"error: no tool '{name}'", null, true);
        }
        catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return ("error: " + e.Message, null, true);
        }
    }

    private static (string, DesignerOp?, bool) Done(DesignerCanvas canvas, string kind, string label, List<CanvasCell> changed, int rejected)
    {
        var tail = rejected > 0 ? $" ({rejected} cells skipped: outside the plot or not placeable)" : "";
        if (changed.Count == 0) return ($"nothing changed{tail}", null, false);
        return ($"ok, {changed.Count} cells changed (r{canvas.Rev}){tail}", new DesignerOp(kind, label, changed), false);
    }

    private static string Name(int id, int meta) => id == DesignerCanvas.Earth ? "ground" : BuildBlocks.Name(id, meta);

    // ------------------------------------------------------------------ argument parsing

    private static string Str(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : throw new ArgumentException($"{k} is required");

    /// <summary>A block spec ("cobblestone_stairs:north") → (id, canonical meta).</summary>
    public static (int Id, int Meta) Block(JsonElement e, string k) => Resolve(Str(e, k));

    public static (int Id, int Meta) Resolve(string spec)
    {
        var s = spec.Trim().ToLowerInvariant().Replace(' ', '_');
        // Undisturbed ground (below y 0 only): a dug cell filled back in.
        if (s is "earth" or "ground" or "fill_ground") return (DesignerCanvas.Earth, 0);
        var parts = s.Split(':', 2);
        var (entry, why) = BlueprintParser.Resolve('X', parts[0], parts.Length > 1 ? parts[1] : null, s);
        if (entry == null) throw new ArgumentException(why ?? $"unknown block '{spec}'");
        int meta = entry.CanonicalMeta;
        if (entry.Id == Blocks.Torch && meta == 0) meta = 5;
        return (entry.Id, meta);
    }

    private static (int X, int Y, int Z) V(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 3) throw new ArgumentException("a position is [x,y,z]");
        return (a[0].GetInt32(), a[1].GetInt32(), a[2].GetInt32());
    }

    private static (int X, int Y, int Z) P(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) ? V(v) : throw new ArgumentException($"{k} is required ([x,y,z])");

    private static ((int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi) Norm((int X, int Y, int Z) a, (int X, int Y, int Z) b) =>
        ((Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), (Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));

    private static string Box((int X, int Y, int Z) a, (int X, int Y, int Z) b)
    {
        var (lo, hi) = Norm(a, b);
        return string.Create(CultureInfo.InvariantCulture, $"{hi.X - lo.X + 1}×{hi.Y - lo.Y + 1}×{hi.Z - lo.Z + 1}");
    }

    /// <summary>The cells of a box where <paramref name="keep"/> holds, set to (id, meta). Refuses boxes over the op cap.</summary>
    public static IEnumerable<CanvasCell> Cells((int X, int Y, int Z) a, (int X, int Y, int Z) b, Func<int, int, int, bool> keep, int id, int meta)
    {
        var (lo, hi) = Norm(a, b);
        long n = (long)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1);
        if (n > DesignerCanvas.MaxCellsPerOp) throw new ArgumentException($"box of {n} cells is too big (max {DesignerCanvas.MaxCellsPerOp})");
        var list = new List<CanvasCell>((int)n);
        for (int y = lo.Y; y <= hi.Y; y++)
            for (int z = lo.Z; z <= hi.Z; z++)
                for (int x = lo.X; x <= hi.X; x++)
                    if (keep(x, y, z)) list.Add(new CanvasCell(x, y, z, id, meta));
        return list;
    }

    /// <summary>A 3D DDA line from a to b (both ends included).</summary>
    public static List<(int X, int Y, int Z)> Line((int X, int Y, int Z) a, (int X, int Y, int Z) b)
    {
        int n = Math.Max(Math.Abs(b.X - a.X), Math.Max(Math.Abs(b.Y - a.Y), Math.Abs(b.Z - a.Z)));
        var list = new List<(int, int, int)>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double t = n == 0 ? 0 : (double)i / n;
            list.Add(((int)Math.Round(a.X + (b.X - a.X) * t), (int)Math.Round(a.Y + (b.Y - a.Y) * t), (int)Math.Round(a.Z + (b.Z - a.Z) * t)));
        }
        return list;
    }
}
