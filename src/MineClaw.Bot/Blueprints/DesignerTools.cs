using System.Globalization;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Blueprints.Mine;
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
        new("find_blocks", "Look up block names (words: name, colour, wood, category). Returns specs to use as b.",
            Obj("\"q\":" + Str0 + ",\"n\":{\"type\":\"integer\"}", "q")),
    ];

    private const string Int0 = "{\"type\":\"integer\"}";

    /// <summary>A mine canvas's tools: the build tools plus the mine templates (docs/web/api.md "Mine designs").</summary>
    public static readonly IReadOnlyList<LlmToolDefinition> Mine =
    [
        .. All,
        new("spiral_shaft", "Dig a walkable spiral staircase shaft (1 block down per step, 3 high) around a solid core; at = its NW corner [x,z]. top defaults to the surface over it.",
            Obj("\"at\":{\"type\":\"array\",\"items\":" + Int0 + ",\"minItems\":2,\"maxItems\":2},\"size\":" + Int0 + ",\"core\":" + Int0 + ",\"top\":" + Int0
                + ",\"bottom\":" + Int0 + ",\"dir\":{\"type\":\"string\",\"enum\":[\"cw\",\"ccw\"]},\"torch_every\":" + Int0
                + ",\"landings\":{\"type\":\"array\",\"items\":" + Int0 + "}", "at", "bottom")),
        new("strip_mine", "Dig a strip mine: a main corridor from at=[x,y,z] (y = feet) along axis, 1x2 branches every spacing+1 blocks on its sides.",
            Obj("\"at\":" + P3 + ",\"axis\":{\"type\":\"string\",\"enum\":[\"x\",\"z\"]},\"length\":" + Int0 + ",\"corridor\":{\"type\":\"string\",\"enum\":[\"1x2\",\"2x3\"]},\"spacing\":"
                + Int0 + ",\"branch_length\":" + Int0 + ",\"sides\":{\"type\":\"string\",\"enum\":[\"both\",\"left\",\"right\",\"none\"]},\"torch_every\":" + Int0, "at")),
        new("mine_preset", "Apply a version-preferred mine preset by id (its shaft + strip mine).", Obj("\"id\":" + Str0, "id")),
    ];

    /// <summary>The tools for a canvas of this kind.</summary>
    public static IReadOnlyList<LlmToolDefinition> For(DesignerCanvas canvas) => canvas.Mine ? Mine : All;

    /// <summary>
    /// The mine designer's cached system prompt (a mine canvas only; compact: the plot, the ore heights of the version, the presets
    /// and the rules of a walkable mine). One text per version, seed and player.
    /// </summary>
    public static string MineSystem(string? player, DesignerCanvas canvas)
    {
        var pal = DesignPalette.For(canvas.Version);
        var (lo, hi) = canvas.Terrain!.SurfaceRange();
        var presets = string.Join(", ", MinePresets.For(canvas.Version).Select(p => p.Id));
        return $"You are the Design Studio builder: you co-design an underground MINE for Minecraft {pal.Version} with {player ?? "the player"} on a sandbox " +
        $"canvas over generated terrain (not the world). Plot x,z 0..63 (+x east, +z south), ABSOLUTE world y 1..127; the surface is y {lo}..{hi} here, " +
        "bedrock y 0-4. The design is the cells to mine out: remove digs a box (never bedrock), \"earth\" fills dug cells back; blocks you " +
        "place (torches, ladders, rails, chests) go in after their cell is dug. Prefer the templates: spiral_shaft (a walkable spiral " +
        "staircase around a core, from the surface down), strip_mine (main corridor + 1x2 branches, spacing solid blocks apart), remove for " +
        "rooms; tunnels 2 high, steps at most 1. Connect everything to the shaft. Ore heights: " + OreTable.Compact(canvas.Version) +
        ". Presets (mine_preset): " + presets + ". Blocks by minecraft name (torch:wall=<side of the supporting block>, ladder:wall=<side>, rail, " +
        "chest:<front>); unsure: find_blocks. Reply after the edits with ONE or two short sentences in plain words (no coordinates unless asked).";
    }

    /// <summary>
    /// The cached system prompt: the plot, the ground, the conventions and the version's vocabulary as a compact category summary
    /// (<see cref="DesignPalette.Summary"/>, ≈200 tokens: never the whole list; find_blocks looks names up). One text per version
    /// and player, so the cached prefix holds.
    /// </summary>
    public static string System(string? player, string? version = null)
    {
        var pal = DesignPalette.For(version ?? GameData.Current.VersionId);
        return $"You are the Design Studio builder: you co-design a Minecraft {pal.Version} build with {player ?? "the player"} on a sandbox " +
        "canvas (not the world; nothing is spent). Plot: x,z 0..63, y -16..47; +x east, +z south (north = -z); the view starts at the " +
        "centre (32,0,32). y=0 is the first layer above flat grass; y=-1 is the grass layer and y -16..-1 is solid ground: dig it with " +
        "remove (that air is dug out when built), then build floors, walls, stairs or ladders down there (basements, cellars, " +
        "foundations); \"earth\" fills dug cells back. Edit only with the tools, coordinates inclusive. Prefer few big calls (box, line) " +
        "over many places. A saved design is at most 32 wide/deep and 32 high, dug layers included: stay inside that.\n" +
        $"Blocks: any {pal.Version} block by its minecraft name (oak_stairs), display name, or name:variant (wool:red, " +
        (pal.Version == DesignVersions.Release ? "stained_glass:light_blue, log:spruce, planks:birch, concrete:white" : "log:spruce, stone_slab:cobblestone, stone_bricks:mossy") +
        "); unsure of a name: find_blocks. Add :<dir> for a facing: stairs = the way up, doors = the side they open to (place the lower " +
        "half, the top is added; on a full block), beds = the head's side (the head is added), chests/furnaces/pumpkins = the front, " +
        "gates = the way through; torch:floor (stands on the block below), torch:wall=<side>, ladder:wall=<side> (<side> = where the " +
        "supporting block is, seen from the torch or ladder: torch:wall=west hangs on the solid block to its west, on that block's east " +
        "face; it must be a full block)" + (pal.Version == DesignVersions.Release ? "; :top for upper slabs and upside-down stairs" : "") +
        ". Doors and windows need openings in walls.\n" + pal.Summary() +
        "When the user names \"the selection\" use its box. Reply after the edits with ONE or two short sentences on what changed, in plain " +
        "words (no coordinates unless asked).";
    }

    /// <summary>One canvas tool: (text for the model, the op applied or null, whether it was an error).</summary>
    private delegate (string Text, DesignerOp? Op, bool Error) CanvasTool(DesignerCanvas canvas, JsonElement input);

    /// <summary>The tools by name (the mine templates refuse a build canvas themselves).</summary>
    private static readonly Dictionary<string, CanvasTool> Tools = new()
    {
        ["find_blocks"] = FindBlocksTool,
        ["describe"] = DescribeTool,
        ["spiral_shaft"] = (c, i) => MineTool(c, "spiral_shaft", i),
        ["strip_mine"] = (c, i) => MineTool(c, "strip_mine", i),
        ["mine_preset"] = (c, i) => MineTool(c, "mine_preset", i),
        ["clear"] = (c, _) => Done(c, "clear", "cleared the canvas", c.Clear(), 0),
        ["remove"] = RemoveTool,
        ["place"] = PlaceTool,
        ["box"] = BoxTool,
        ["line"] = LineTool,
        ["replace"] = ReplaceTool,
    };

    /// <summary>Runs one tool call on the canvas: (text for the model, the op applied or null, whether it was an error).</summary>
    public static (string Text, DesignerOp? Op, bool Error) Run(DesignerCanvas canvas, string name, JsonElement input)
    {
        // Every name, rule and summary in the canvas's own version.
        using var scope = DesignVersions.Use(canvas.Version);
        try
        {
            return Tools.TryGetValue(name, out var tool) ? tool(canvas, input) : ($"error: no tool '{name}'", null, true);
        }
        catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return ("error: " + e.Message, null, true);
        }
    }

    private static (string, DesignerOp?, bool) FindBlocksTool(DesignerCanvas canvas, JsonElement input)
    {
        var q = Str(input, "q");
        int n = input.TryGetProperty("n", out var nv) && nv.ValueKind == JsonValueKind.Number ? Math.Clamp(nv.GetInt32(), 1, 40) : 15;
        var hits = canvas.Palette.Find(q, n);
        if (hits.Count == 0) return ($"no {canvas.Version} block matches '{q}'" + (canvas.Palette.DidYouMean(q) is { Count: > 0 } dm ? "; did you mean " + string.Join(", ", dm) : ""), null, false);
        return (string.Join("\n", hits.Select(e => $"{e.Spec} = {e.Name} ({e.Category}{(e.Facing ? ", facing" : "")})")), null, false);
    }

    private static (string, DesignerOp?, bool) DescribeTool(DesignerCanvas canvas, JsonElement input) =>
        (input.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number ? canvas.Layer(y.GetInt32()) : canvas.Describe(16), null, false);

    private static (string, DesignerOp?, bool) RemoveTool(DesignerCanvas canvas, JsonElement input)
    {
        var (a, b) = (P(input, "from"), P(input, "to"));
        return Done(canvas, "remove", $"removed {Box(a, b)}", canvas.Apply(Cells(a, b, (_, _, _) => true, 0, 0), out var rej), rej);
    }

    private static (string, DesignerOp?, bool) PlaceTool(DesignerCanvas canvas, JsonElement input)
    {
        var (id, meta) = Block(input, "b");
        if (!input.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Array) throw new ArgumentException("at: [[x,y,z],...]");
        var cells = at.EnumerateArray().Select(e => V(e)).Select(p => new CanvasCell(p.X, p.Y, p.Z, id, meta)).ToList();
        return Done(canvas, "place", $"placed {Name(id, meta)}", canvas.Apply(cells, out var rej), rej);
    }

    private static (string, DesignerOp?, bool) BoxTool(DesignerCanvas canvas, JsonElement input)
    {
        var (id, meta) = Block(input, "b");
        var (a, b) = (P(input, "from"), P(input, "to"));
        var mode = OptStr(input, "mode") ?? "fill";
        // hollow/walls: the inside becomes air (an open interior, as the DSL's own primitives draw it); any other mode fills.
        IEnumerable<CanvasCell> cells;
        if (mode is "hollow" or "walls")
        {
            var shell = ShellOf(mode, Norm(a, b));
            cells = Cells(a, b, (_, _, _) => true, 0, 0).Select(c => shell(c.X, c.Y, c.Z) ? c with { Id = id, Meta = meta } : c);
        }
        else cells = Cells(a, b, (_, _, _) => true, id, meta);
        return Done(canvas, mode, $"{(mode == "fill" ? "filled" : mode)} {Box(a, b)} with {Name(id, meta)}", canvas.Apply(cells, out var rej), rej);
    }

    /// <summary>The cells of a hollow box (its shell) or of its walls (4 sides, no floor or roof).</summary>
    private static Func<int, int, int, bool> ShellOf(string mode, ((int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi) box)
    {
        var (lo, hi) = box;
        if (mode == "hollow") return (x, y, z) => x == lo.X || x == hi.X || y == lo.Y || y == hi.Y || z == lo.Z || z == hi.Z;
        return (x, _, z) => x == lo.X || x == hi.X || z == lo.Z || z == hi.Z;
    }

    private static (string, DesignerOp?, bool) LineTool(DesignerCanvas canvas, JsonElement input)
    {
        var (id, meta) = Block(input, "b");
        var (a, b) = (P(input, "from"), P(input, "to"));
        return Done(canvas, "line", $"line of {Name(id, meta)}", canvas.Apply(Line(a, b).Select(p => new CanvasCell(p.X, p.Y, p.Z, id, meta)), out var rej), rej);
    }

    private static (string, DesignerOp?, bool) ReplaceTool(DesignerCanvas canvas, JsonElement input)
    {
        var aSpec = Str(input, "a");
        (int Id, int Meta)? from = aSpec.Equals("any", StringComparison.OrdinalIgnoreCase) ? null : Block(input, "a");
        var (id, meta) = Block(input, "b");
        var (lo, hi) = ReplaceBox(canvas, input);
        var famTo = DesignFacing.FamilyOf(id);
        var cells = canvas.Blocks.Where(kv => InBox(kv.Key, lo, hi))
            .Where(kv => Matches(from, kv.Value.Id, kv.Value.Meta))
            .Select(kv => new CanvasCell(kv.Key.X, kv.Key.Y, kv.Key.Z, id, ReplacedMeta(canvas, kv.Value, id, meta, famTo))).ToList();
        return Done(canvas, "replace", $"replaced {aSpec} with {Name(id, meta)}", canvas.Apply(cells, out var rej), rej);
    }

    /// <summary>The replace box: from..to, else the whole canvas.</summary>
    private static ((int X, int Y, int Z) Lo, (int X, int Y, int Z) Hi) ReplaceBox(DesignerCanvas canvas, JsonElement input) =>
        input.TryGetProperty("from", out _) ? Norm(P(input, "from"), P(input, "to"))
            : ((0, canvas.YRange.Min, 0), (DesignerCanvas.Size - 1, canvas.YRange.Max, DesignerCanvas.Size - 1));

    private static bool InBox(MineClaw.World.BlockPos p, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi) =>
        p.X >= lo.X && p.X <= hi.X && p.Y >= lo.Y && p.Y <= hi.Y && p.Z >= lo.Z && p.Z <= hi.Z;

    /// <summary>Same block = same id and variant (wool colour, slab material; not its facing or half); a two-cell block (door, bed)
    /// is replaced by its first half only (the canvas pairs the second). "any" (null) = every block but a second half.</summary>
    private static bool Matches((int Id, int Meta)? from, int cid, int cmeta) => from is not { } f ? !DesignFacing.TwoCell(cid)
        : cid == f.Id && (cmeta & DesignFacing.VariantMask(cid)) == (f.Meta & DesignFacing.VariantMask(cid)) && !DesignFacing.SecondHalf(cid, cmeta);

    /// <summary>A facing block replaced by one of its family keeps its facing (stairs for stairs, chest for furnace), else the new meta.</summary>
    private static int ReplacedMeta(DesignerCanvas canvas, (int Id, int Meta) old, int id, int meta, DesignFamily famTo)
    {
        int kept = (old.Meta & ~DesignFacing.VariantMask(id)) | (meta & DesignFacing.VariantMask(id));
        return DesignFacing.HasFacing(famTo) && DesignFacing.FamilyOf(old.Id) == famTo && canvas.RefusalOf(id, kept) == null ? kept : meta;
    }

    /// <summary>The mine templates on a mine canvas (spiral shaft, strip mine, a preset).</summary>
    private static (string, DesignerOp?, bool) MineTool(DesignerCanvas canvas, string name, JsonElement input)
    {
        if (!canvas.Mine || canvas.Terrain is not { } terrain) return ($"error: {name} needs a mine canvas (switch the studio to Mine)", null, true);
        MineCells m;
        if (name == "spiral_shaft") m = SpiralOf(input, terrain);
        else if (name == "strip_mine") m = StripOf(input);
        else
        {
            var id = Str(input, "id");
            if (MinePresets.Get(id) is not { } p) return ($"error: no preset '{id}'; {string.Join(", ", MinePresets.For(canvas.Version).Select(x => x.Id))}", null, true);
            m = MinePresets.Cells(p, terrain);
        }
        var changed = canvas.Apply(m.Cells, out var rej);
        var res = Done(canvas, name, m.Label, changed, rej);
        return res.Item2 == null ? res : (res.Item1 + string.Create(CultureInfo.InvariantCulture, $"; entry {m.Entry.X},{m.Entry.Y},{m.Entry.Z}"), res.Item2, res.Item3);
    }

    private static MineCells SpiralOf(JsonElement input, MineTerrain terrain)
    {
        if (!input.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Array || at.GetArrayLength() != 2) throw new ArgumentException("at: [x,z]");
        int x = at[0].GetInt32(), z = at[1].GetInt32();
        int size = Math.Clamp(OptInt(input, "size") ?? 4, MineTemplates.MinSize, MineTemplates.MaxSize);
        int top = OptInt(input, "top") ?? terrain.SurfaceOver(x, z, x + size - 1, z + size - 1);
        var landings = input.TryGetProperty("landings", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToList() : null;
        return MineTemplates.Spiral(new SpiralSpec(x, z, top, OptInt(input, "bottom") ?? 11, size, OptInt(input, "core") ?? 2,
            OptStr(input, "dir") != "ccw", OptInt(input, "torch_every") ?? 0, landings));
    }

    private static MineCells StripOf(JsonElement input)
    {
        var (x, y, z) = P(input, "at");
        bool wide = (OptStr(input, "corridor") ?? "1x2") == "2x3";
        var axis = OptStr(input, "axis") == "z" ? 'z' : 'x';
        var sides = OptStr(input, "sides") ?? "both";
        return MineTemplates.Strip(new StripSpec(x, y, z, axis, OptInt(input, "length") ?? 30, wide ? 2 : 1, wide ? 3 : 2, OptInt(input, "spacing") ?? 3,
            OptInt(input, "branch_length") ?? 12, sides, OptInt(input, "torch_every") ?? 0));
    }

    private static int? OptInt(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>An optional string argument (null when missing or not a string).</summary>
    private static string? OptStr(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static (string, DesignerOp?, bool) Done(DesignerCanvas canvas, string kind, string label, List<CanvasCell> changed, int rejected)
    {
        var tail = rejected > 0 ? $" ({rejected} cells skipped: outside the plot or not placeable)" : "";
        // The attachment rule's corrections, said back so the model learns the convention.
        if (DesignerCanvas.FixText(canvas.LastFixed) is { Length: > 0 } fix) tail += $" (note: {fix}; wall=<side> names the side the supporting block is on)";
        if (changed.Count == 0) return ($"nothing changed{tail}", null, false);
        return ($"ok, {changed.Count} cells changed (r{canvas.Rev}){tail}", new DesignerOp(kind, label, changed), false);
    }

    private static string Name(int id, int meta) => id == DesignerCanvas.Earth ? "ground" : DesignPalette.Current.Name(id, meta).ToLowerInvariant();

    // ------------------------------------------------------------------ argument parsing

    private static string Str(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : throw new ArgumentException($"{k} is required");

    /// <summary>A block spec ("cobblestone_stairs:north") → (id, canonical meta).</summary>
    public static (int Id, int Meta) Block(JsonElement e, string k) => Resolve(Str(e, k));

    /// <summary>
    /// A block spec in the version of this flow → (id, meta): "earth" (a dug cell filled back), else the design palette's spelling
    /// (<see cref="DesignPalette.TryResolve"/>: names, variants, facings, wall sides); a bad one throws with "did you mean …?".
    /// </summary>
    public static (int Id, int Meta) Resolve(string spec)
    {
        var s = spec.Trim().ToLowerInvariant().Replace(' ', '_');
        // Undisturbed ground (below y 0 only): a dug cell filled back in.
        if (s is "earth" or "ground" or "fill_ground") return (DesignerCanvas.Earth, 0);
        if (Attached(s) is { } named) return named;
        var (id, meta) = DesignPalette.Current.Resolve(s);
        if (id == Blocks.Torch && meta == 0) meta = 5;
        return (id, meta);
    }

    /// <summary>
    /// Torches and ladders by the side their supporting block is on: "torch:wall=west" / "torch:on=west" / "ladder:wall=south"
    /// (the block is west / south of it), "torch:floor"; null = not such a spelling (numeric metas go the usual way).
    /// </summary>
    internal static (int Id, int Meta)? Attached(string s)
    {
        var parts = s.Split(':', 2);
        if (parts.Length < 2 || AttachedId(parts[0]) is not { } id) return null;
        var arg = parts[1].Trim();
        if (id == Blocks.Torch && FloorWords.Contains(arg)) return (id, 5);
        if (SideOf(arg) is not { } side) return null;
        if (Dirs.Parse(side) is not { } d) throw new ArgumentException($"'{s}': the side is north, east, south or west (where the supporting block is)");
        return (id, DesignerCanvas.MetaOn(id, WallSide(d)));
    }

    private static readonly HashSet<string> FloorWords = ["floor", "ground", "up", "standing"];
    private static readonly HashSet<string> WallWords = ["wall", "on", "against", "support"];

    private static int? AttachedId(string name) => name switch { "torch" => Blocks.Torch, "ladder" => Blocks.Ladder, _ => null };

    /// <summary>The side named in "wall=west" / "on-west" (any word of <see cref="WallWords"/>) or a bare side ("torch:north" /
    /// "ladder:south": the side the block is on); null = not a side spelling (numeric metas go the usual way).</summary>
    private static string? SideOf(string arg)
    {
        int eq = arg.IndexOfAny(['=', '_', '-']);
        if (eq > 0 && WallWords.Contains(arg[..eq])) return arg[(eq + 1)..];
        return eq > 0 || Dirs.Parse(arg) == null ? null : arg;
    }

    /// <summary>The (dx, dz) of the supporting block on side <paramref name="d"/>.</summary>
    private static (int Dx, int Dz) WallSide(Dir d) => d switch { Dir.West => (-1, 0), Dir.East => (1, 0), Dir.North => (0, -1), _ => (0, 1) };

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
