using System.Globalization;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;

namespace MineClaw.Agent.Admin.Shards.Packs;

/// <summary>
/// Where a Minecraft version's game data comes from. A source turns its tables into short, human-readable RAPTOR leaves (kind
/// <c>gamedata</c>, refs <c>gd:{sub-kind}:...</c>, the fixed seed timestamp, deterministic order), which a version pack carries.
/// </summary>
public interface IGameDataSource
{
    /// <summary>The version tag ("b1.8.1", "1.8.9").</summary>
    string Version { get; }
    /// <summary>One line for the manifest and index.json ("MineClaw.Data b1.8.1 tables", "minecraft-data pc/1.8").</summary>
    string Description { get; }
    /// <summary>The game-data docs, deterministic (same input, same docs in the same order).</summary>
    IReadOnlyList<RagDoc> Docs();
}

/// <summary>
/// b1.8.1: the static tables in <c>MineClaw.Data</c> (Blocks/Items/Recipes/Smelting/Tools/Drops, transcribed from the b1.8.1 client
/// jar's Block/Item static initializers, CraftingManager/FurnaceRecipes, ItemTool tables, cross-checked against Bukkit mc-dev
/// 218a2a71) rendered by <see cref="SeedCorpus"/>: exactly the game data the seed db holds.
/// </summary>
public sealed class MineClawDataSource : IGameDataSource
{
    public string Version => KnowledgePack.DefaultVersion;
    public string Description => "MineClaw.Data b1.8.1 tables (client jar / mc-dev), rendered by SeedCorpus";
    public IReadOnlyList<RagDoc> Docs() => SeedCorpus.Build();
}

/// <summary>
/// A PrismarineJS <c>minecraft-data</c> checkout (never downloaded by MineClaw: point it at a folder you fetched yourself). Accepts
/// the repo root (<c>data/dataPaths.json</c>), its <c>data</c> folder (<c>dataPaths.json</c>), or one version folder holding
/// <c>items.json</c>. Reads <c>items.json</c>, <c>blocks.json</c>, <c>recipes.json</c> and <c>foods.json</c> (optional); the docs follow
/// <see cref="SeedCorpus"/>'s wording so retrieval behaves the same across versions. minecraft-data has no furnace/fuel tables, so a
/// pack built from it carries no smelting docs (put them in that version's knowledge doc).
/// </summary>
public sealed class MinecraftDataSource : IGameDataSource
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public string Version { get; }
    public string Edition { get; }
    public string Description { get; }

    /// <param name="root">minecraft-data repo root, its data folder, or a version folder.</param>
    /// <param name="version">The version tag ("1.8.9"); also the dataPaths.json key unless <paramref name="dataPathsKey"/> is given.</param>
    public MinecraftDataSource(string root, string version, string edition = "pc", string? dataPathsKey = null)
    {
        if (!KnowledgePack.ValidVersion(version)) throw new ArgumentException($"'{version}' is not a version tag (e.g. 1.8.9)");
        Version = version;
        Edition = edition;
        var full = Path.GetFullPath(root);
        string? dataDir = File.Exists(Path.Combine(full, "dataPaths.json")) ? full
            : File.Exists(Path.Combine(full, "data", "dataPaths.json")) ? Path.Combine(full, "data") : null;
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "items", "blocks", "recipes", "foods" })
        {
            string? dir = null;
            if (dataDir != null)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "dataPaths.json")));
                if (doc.RootElement.TryGetProperty(edition, out var ed) && ed.TryGetProperty(dataPathsKey ?? version, out var v)
                    && v.TryGetProperty(kind, out var rel) && rel.GetString() is { Length: > 0 } r)
                    dir = Path.Combine(dataDir, r.Replace('/', Path.DirectorySeparatorChar));
                else if (Directory.Exists(Path.Combine(dataDir, edition, version))) dir = Path.Combine(dataDir, edition, version);
                if (dir != null) used.Add(Path.GetRelativePath(dataDir, dir).Replace('\\', '/'));
            }
            else dir = full;
            var file = dir != null ? Path.Combine(dir, kind + ".json") : null;
            if (file != null && File.Exists(file)) _files[kind] = file;
        }
        if (!_files.ContainsKey("items") || !_files.ContainsKey("blocks"))
            throw new FileNotFoundException($"no minecraft-data items.json/blocks.json for {edition} {dataPathsKey ?? version} under {full}");
        Description = $"minecraft-data {(used.Count > 0 ? string.Join(", ", used) : edition + "/" + version)}";
    }

    private sealed record Item(int Id, string Name, string Display, int Stack, int Durability);
    private sealed record Block(int Id, string Name, string Display, double? Hardness, bool Diggable, string? Material, bool Solid,
        bool Transparent, int Light, IReadOnlyList<int> HarvestTools, IReadOnlyList<int> Drops);
    private sealed record Food(int Id, double Points, double Saturation);

    private JsonElement Load(string kind)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(_files[kind]));
        return doc.RootElement.Clone();
    }

    private static int Int(JsonElement e, string p, int d = 0) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : d;

    private static double? Num(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string F(double v) => v.ToString("0.##", Inv);

    public IReadOnlyList<RagDoc> Docs()
    {
        var items = Load("items").EnumerateArray().Select(e => new Item(Int(e, "id"), Str(e, "name") ?? "", Str(e, "displayName") ?? Str(e, "name") ?? "?",
            Int(e, "stackSize", 64), Int(e, "maxDurability", Int(e, "durability")))).OrderBy(i => i.Id).ToList();
        var blocks = Load("blocks").EnumerateArray().Select(e => new Block(Int(e, "id"), Str(e, "name") ?? "", Str(e, "displayName") ?? Str(e, "name") ?? "?",
            Num(e, "hardness"), !e.TryGetProperty("diggable", out var dg) || dg.ValueKind != JsonValueKind.False, Str(e, "material"),
            Str(e, "boundingBox") is not "empty", e.TryGetProperty("transparent", out var tr) && tr.ValueKind == JsonValueKind.True,
            Int(e, "emitLight"), HarvestTools(e), BlockDrops(e))).OrderBy(b => b.Id).ToList();
        var foods = _files.ContainsKey("foods")
            ? Load("foods").EnumerateArray().Select(e => new Food(Int(e, "id"), Num(e, "foodPoints") ?? 0, Num(e, "saturation") ?? 0))
                .GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First())
            : new Dictionary<int, Food>();

        // Item ids name recipe slots; old versions share ids between blocks and their items, so blocks fill the gaps.
        var names = new Dictionary<int, string>();
        foreach (var i in items) names.TryAdd(i.Id, i.Display);
        foreach (var b in blocks) names.TryAdd(b.Id, b.Display);
        string Name(int id) => names.TryGetValue(id, out var n) ? n : $"item {id}";
        var blockNames = blocks.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);

        var docs = new List<RagDoc>();
        if (_files.ContainsKey("recipes")) docs.AddRange(RecipeDocs(Load("recipes"), Name));
        foreach (var it in items.Where(i => !blockNames.Contains(i.Name))) // block items get their block doc
        {
            var sb = new StringBuilder($"Item: {it.Display} (id {it.Id}). ");
            sb.Append(it.Stack == 1 ? "Does not stack." : $"Stacks to {it.Stack}.");
            if (it.Durability > 0) sb.Append($" Durability {it.Durability} uses.");
            if (foods.TryGetValue(it.Id, out var f))
                sb.Append($" Food: restores {F(f.Points)} hunger ({F(f.Points / 2)} shanks), saturation {F(f.Saturation)}.");
            docs.Add(Doc($"gd:item:{it.Id}", "item", $"item {it.Display}", sb.ToString()));
        }
        foreach (var b in blocks.Where(b => b.Id > 0 && b.Name != "air"))
        {
            var sb = new StringBuilder($"Block: {b.Display} (id {b.Id}). ");
            sb.Append(!b.Diggable || b.Hardness is < 0 ? "Unbreakable." : b.Hardness is null or 0 ? "Breaks instantly." : $"Hardness {F(b.Hardness.Value)}.");
            if (ToolOf(b.Material) is { } tool) sb.Append($" Best tool: {tool}.");
            if (b.HarvestTools.Count > 0)
                sb.Append(" Drops anything only when mined with: ").Append(string.Join(", ", b.HarvestTools.Select(Name))).Append('.');
            if (b.Drops.Count > 0) sb.Append(" Drops ").Append(string.Join(", ", b.Drops.Select(Name))).Append('.');
            else if (b.Diggable && b.Hardness is >= 0) sb.Append(" Drops nothing.");
            if (!b.Solid) sb.Append(" Not solid (walk through).");
            if (b.Light > 0) sb.Append($" Gives light {b.Light}.");
            docs.Add(Doc($"gd:block:{b.Id}", "block", $"block {b.Display}", sb.ToString()));
        }
        return docs;
    }

    private RagDoc Doc(string reference, string tag, string title, string body) =>
        new(reference, SeedCorpus.Kind, title, body) { Tag = tag, Source = SeedCorpus.Source, At = SeedCorpus.At };

    /// <summary>"rock" (old) / "mineable/pickaxe" (new) → the tool that speeds it up.</summary>
    private static string? ToolOf(string? material) => material switch
    {
        null => null,
        "rock" or "iron" or "anvil" => "pickaxe",
        "dirt" or "sand" or "snow" or "clay" or "grass" => "shovel",
        "wood" or "plant;wood" => "axe",
        "web" or "wool" => "shears or sword",
        _ when material.Contains("mineable/", StringComparison.Ordinal) => material.Split(';')
            .Select(p => p.StartsWith("mineable/", StringComparison.Ordinal) ? p["mineable/".Length..] : null).FirstOrDefault(p => p != null),
        _ => null,
    };

    private static List<int> HarvestTools(JsonElement e) =>
        e.TryGetProperty("harvestTools", out var h) && h.ValueKind == JsonValueKind.Object
            ? h.EnumerateObject().Select(p => int.TryParse(p.Name, NumberStyles.Integer, Inv, out var i) ? i : -1).Where(i => i >= 0).OrderBy(i => i).ToList()
            : [];

    /// <summary>drops: [4] (new) or [{"drop": 4 | {"id": 4, "metadata": 0}, "minCount", "maxCount"}] (old).</summary>
    private static List<int> BlockDrops(JsonElement e)
    {
        var list = new List<int>();
        if (!e.TryGetProperty("drops", out var d) || d.ValueKind != JsonValueKind.Array) return list;
        foreach (var x in d.EnumerateArray())
        {
            var v = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("drop", out var dr) ? dr : x;
            if (IngredientId(v) is { } id && !list.Contains(id)) list.Add(id);
        }
        return list;
    }

    /// <summary>An ingredient/result: a number, {"id": n, "metadata": m}, or null (an empty cell).</summary>
    private static int? IngredientId(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt32(out var i) => i,
        JsonValueKind.Object => Int(v, "id", -1) is var i && i >= 0 ? i : null,
        _ => null,
    };

    private static IEnumerable<RagDoc> RecipeDocs(JsonElement recipes, Func<int, string> name)
    {
        var docs = new List<RagDoc>();
        foreach (var entry in recipes.EnumerateObject().OrderBy(p => int.TryParse(p.Name, NumberStyles.Integer, Inv, out var k) ? k : int.MaxValue).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            var counts = new Dictionary<int, int>();
            foreach (var r in entry.Value.EnumerateArray())
            {
                if (!r.TryGetProperty("result", out var res)) continue;
                int outId = IngredientId(res) ?? (int.TryParse(entry.Name, NumberStyles.Integer, Inv, out var k) ? k : -1);
                if (outId < 0) continue;
                int meta = res.ValueKind == JsonValueKind.Object ? Int(res, "metadata") : 0;
                int count = res.ValueKind == JsonValueKind.Object ? Int(res, "count", 1) : 1;
                int n = counts[meta] = counts.GetValueOrDefault(meta) + 1;
                var output = name(outId);
                var sb = new StringBuilder($"Crafting recipe: {output}. Makes {count} per craft. ");
                if (r.TryGetProperty("inShape", out var shape) && shape.ValueKind == JsonValueKind.Array)
                {
                    var rows = shape.EnumerateArray().Select(row => row.EnumerateArray().Select(IngredientId).ToList()).ToList();
                    int h = rows.Count, w = rows.Count == 0 ? 0 : rows.Max(x => x.Count);
                    sb.Append(w <= 2 && h <= 2 ? "Fits the 2x2 inventory grid (no crafting table needed). " : "Needs a crafting table (3x3 grid). ");
                    var totals = rows.SelectMany(x => x).OfType<int>().GroupBy(i => i).OrderBy(g => g.Key).Select(g => $"{g.Count()} {name(g.Key)}");
                    sb.Append("Ingredients: ").Append(string.Join(", ", totals)).Append(". ");
                    var labels = rows.Select((row, y) => (h == 1 ? "one row" : y == 0 ? "top row" : y == h - 1 ? "bottom row" : "middle row") + ": " +
                        string.Join(", ", Enumerable.Range(0, w).Select(x => x < row.Count && row[x] is { } id ? name(id) : "empty")));
                    sb.Append($"Shape {w} wide by {h} tall; ").Append(string.Join("; ", labels)).Append('.');
                }
                else if (r.TryGetProperty("ingredients", out var ing) && ing.ValueKind == JsonValueKind.Array)
                {
                    var ids = ing.EnumerateArray().Select(IngredientId).OfType<int>().ToList();
                    sb.Append(ids.Count <= 4 ? "Fits the 2x2 inventory grid (no crafting table needed). " : "Needs a crafting table (3x3 grid). ");
                    sb.Append("Ingredients: ").Append(string.Join(", ", ids.GroupBy(i => i).OrderBy(g => g.Key).Select(g => $"{g.Count()} {name(g.Key)}"))).Append(". ");
                    sb.Append("Shapeless: put the ingredients anywhere in the grid.");
                }
                else continue;
                docs.Add(new RagDoc($"gd:recipe:{outId}:{meta}:{n}", SeedCorpus.Kind, $"recipe {output} x{count}", sb.ToString())
                {
                    Tag = "recipe", Source = SeedCorpus.Source, At = SeedCorpus.At,
                });
            }
        }
        return docs;
    }
}
