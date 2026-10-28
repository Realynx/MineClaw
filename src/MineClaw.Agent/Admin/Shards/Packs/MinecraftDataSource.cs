using System.Globalization;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;

namespace MineClaw.Agent.Admin.Shards.Packs;

/// <summary>
/// A PrismarineJS <c>minecraft-data</c> checkout (never downloaded by MineClaw: point it at a folder you fetched yourself, e.g. the
/// vendored <c>third_party/minecraft-data/pc/1.12.2</c>). Accepts the repo root (<c>data/dataPaths.json</c>), its <c>data</c> folder
/// (<c>dataPaths.json</c>), or one version folder holding <c>items.json</c>. Reads <c>items.json</c>, <c>blocks.json</c>,
/// <c>recipes.json</c>, <c>foods.json</c> and <c>entities.json</c> (the last three optional). Names follow the variants
/// (planks meta 1 = Spruce Wood Planks) and every item/block doc carries the registry name, so "cooked beef" finds Steak. The wording
/// follows <see cref="SeedCorpus"/> so retrieval behaves the same across versions. minecraft-data has no furnace, fuel or mob loot
/// tables: those come from an optional facts folder (<c>shards/sources/&lt;version&gt;</c>: <c>smelting.json</c>, <c>mobs.json</c>, facts
/// written by hand from the decompiled client; see <see cref="FactsDirectory"/>).
/// </summary>
public sealed class MinecraftDataSource : IGameDataSource
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int AnyMeta = 32767;
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public string Version { get; }
    public string Edition { get; }
    public string Description { get; }
    /// <summary>Facts minecraft-data has no tables for (<c>smelting.json</c>: smelting + fuels; <c>mobs.json</c>: health, xp, loot); null = none.</summary>
    public string? FactsDirectory { get; }

    /// <param name="root">minecraft-data repo root, its data folder, or a version folder.</param>
    /// <param name="version">The version tag ("1.8.9"); also the dataPaths.json key unless <paramref name="dataPathsKey"/> is given.</param>
    /// <summary>The bot's own tables for this version (multi-version P5), when given: block drops come from them (ported from
    /// the decompiled client's drop overrides) instead of minecraft-data's <c>drops</c> field, which is wrong for several blocks
    /// (reeds, doors, beds, huge brown mushrooms), so the pack says what the bot's planners believe.</summary>
    public MineClaw.Data.Versioning.IGameData? Tables { get; }

    /// <param name="factsDirectory">Optional folder with <c>smelting.json</c> / <c>mobs.json</c> (a missing file is skipped).</param>
    /// <param name="tables">See <see cref="Tables"/>.</param>
    public MinecraftDataSource(string root, string version, string edition = "pc", string? dataPathsKey = null, string? factsDirectory = null,
        MineClaw.Data.Versioning.IGameData? tables = null)
    {
        Tables = tables;
        if (!KnowledgePack.ValidVersion(version)) throw new ArgumentException($"'{version}' is not a version tag (e.g. 1.8.9)");
        Version = version;
        Edition = edition;
        var full = Path.GetFullPath(root);
        string? dataDir = File.Exists(Path.Combine(full, "dataPaths.json")) ? full
            : File.Exists(Path.Combine(full, "data", "dataPaths.json")) ? Path.Combine(full, "data") : null;
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "items", "blocks", "recipes", "foods", "entities" })
        {
            string? dir = null;
            if (dataDir != null)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "dataPaths.json")));
                if (doc.RootElement.TryGetProperty(edition, out var ed) && ed.TryGetProperty(dataPathsKey ?? version, out var v)
                    && v.TryGetProperty(kind, out var rel) && rel.GetString() is { Length: > 0 } r)
                    dir = Path.Combine(dataDir, r.Replace('/', Path.DirectorySeparatorChar));
                else if (Directory.Exists(Path.Combine(dataDir, edition, version))) dir = Path.Combine(dataDir, edition, version);
                if (dir != null && File.Exists(Path.Combine(dir, kind + ".json"))) used.Add(Path.GetRelativePath(dataDir, dir).Replace('\\', '/'));
            }
            else dir = full;
            var file = dir != null ? Path.Combine(dir, kind + ".json") : null;
            if (file != null && File.Exists(file)) _files[kind] = file;
        }
        if (!_files.ContainsKey("items") || !_files.ContainsKey("blocks"))
            throw new FileNotFoundException($"no minecraft-data items.json/blocks.json for {edition} {dataPathsKey ?? version} under {full}");
        var facts = new List<string>();
        if (factsDirectory != null && Directory.Exists(factsDirectory))
        {
            FactsDirectory = Path.GetFullPath(factsDirectory);
            foreach (var kind in new[] { "smelting", "mobs" })
                if (File.Exists(Path.Combine(FactsDirectory, kind + ".json"))) { _files["facts-" + kind] = Path.Combine(FactsDirectory, kind + ".json"); facts.Add(kind); }
        }
        // Fixed text only (no absolute paths): the description feeds the source hash, which mustn't depend on the checkout location.
        Description = $"minecraft-data {(used.Count > 0 ? string.Join(", ", used) : edition + "/" + version)}" +
                      (facts.Count > 0 ? $" + {string.Join(" + ", facts)} facts (decompiled {version} client)" : "") +
                      (tables != null ? $" + block drops from MineClaw.Data {tables.VersionId}" : "");
    }

    private sealed record Variant(int Meta, string Display);
    private sealed record Item(int Id, string Name, string Display, int Stack, int Durability, IReadOnlyList<Variant> Variants);
    private sealed record Block(int Id, string Name, string Display, double? Hardness, bool Diggable, string? Material, bool Solid,
        bool Transparent, int Light, IReadOnlyList<int> HarvestTools, IReadOnlyList<Ingredient> Drops, IReadOnlyList<Variant> Variants);
    private sealed record Food(int Id, string? Name, string? Display, double Points, double Saturation);
    /// <summary>An item id and its metadata (null = any variant).</summary>
    private readonly record struct Ingredient(int Id, int? Meta);

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

    private static List<Variant> Variants(JsonElement e)
    {
        var list = new List<Variant>();
        if (!e.TryGetProperty("variations", out var vs) || vs.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in vs.EnumerateArray())
            if (Str(v, "displayName") is { Length: > 0 } d && !list.Any(x => x.Meta == Int(v, "metadata")))
                list.Add(new Variant(Int(v, "metadata"), d));
        return list.OrderBy(v => v.Meta).ToList();
    }

    /// <summary>Names by id and variant, registry names → ids (items first, blocks fill the gaps: old versions share ids).</summary>
    private sealed class Names
    {
        private readonly Dictionary<int, string> _plain = new();
        private readonly Dictionary<(int, int), string> _variant = new();
        private readonly Dictionary<int, string> _registry = new();
        private readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);

        public void Add(int id, string registry, string display, IEnumerable<Variant> variants)
        {
            _plain.TryAdd(id, display);
            if (registry.Length > 0) { _registry.TryAdd(id, registry); _ids.TryAdd(registry, id); }
            foreach (var v in variants) _variant.TryAdd((id, v.Meta), v.Display);
        }

        public string Of(int id, int? meta = null) =>
            meta is { } m && _variant.TryGetValue((id, m), out var v) ? v : _plain.TryGetValue(id, out var n) ? n : $"item {id}";

        public string Of(Ingredient i) => Of(i.Id, i.Meta);
        public bool HasVariants(int id) => _variant.Keys.Count(k => k.Item1 == id) > 1;
        public string? Registry(int id) => _registry.TryGetValue(id, out var r) ? r : null;

        public int? Id(string registry) =>
            _ids.TryGetValue(registry.StartsWith("minecraft:", StringComparison.Ordinal) ? registry[10..] : registry, out var i) ? i : null;
    }

    public IReadOnlyList<RagDoc> Docs()
    {
        var items = Load("items").EnumerateArray().Select(e => new Item(Int(e, "id"), Str(e, "name") ?? "", Str(e, "displayName") ?? Str(e, "name") ?? "?",
            Int(e, "stackSize", 64), Int(e, "maxDurability", Int(e, "durability")), Variants(e))).OrderBy(i => i.Id).ToList();
        var blocks = Load("blocks").EnumerateArray().Select(e => new Block(Int(e, "id"), Str(e, "name") ?? "", Str(e, "displayName") ?? Str(e, "name") ?? "?",
            Num(e, "hardness"), !e.TryGetProperty("diggable", out var dg) || dg.ValueKind != JsonValueKind.False, Str(e, "material"),
            Str(e, "boundingBox") is not "empty", e.TryGetProperty("transparent", out var tr) && tr.ValueKind == JsonValueKind.True,
            Int(e, "emitLight"), HarvestTools(e), BlockDrops(e), Variants(e))).OrderBy(b => b.Id).ToList();
        var foods = _files.ContainsKey("foods")
            ? Load("foods").EnumerateArray().Select(e => new Food(Int(e, "id"), Str(e, "name"), Str(e, "displayName"), Num(e, "foodPoints") ?? 0, Num(e, "saturation") ?? 0)).ToList()
            : [];

        var names = new Names();
        foreach (var i in items) names.Add(i.Id, i.Name, i.Display, i.Variants);
        foreach (var b in blocks) names.Add(b.Id, b.Name, b.Display, b.Variants);
        var blockNames = blocks.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var itemKeys = items.Select(i => (i.Id, i.Name)).ToHashSet();

        // A food row by item id (+ name), else by registry name, else by display name: minecraft-data 1.12 lists some foods under their
        // crop block's id (carrots 141 → the Carrot item 391, melon_block 103 → the Melon slice 360).
        Food? FoodOf(Item it) =>
            foods.FirstOrDefault(f => f.Id == it.Id && (f.Name == null || f.Name == it.Name))
            ?? foods.FirstOrDefault(f => f.Name == it.Name)
            ?? foods.FirstOrDefault(f => f.Display != null && f.Display.Equals(it.Display, StringComparison.OrdinalIgnoreCase)
                                         && !(itemKeys.Contains((f.Id, f.Name ?? "")) && !blockNames.Contains(f.Name ?? ""))); // not another item's own row

        var smelting = _files.ContainsKey("facts-smelting") ? SmeltingFacts.Parse(Load("facts-smelting"), names) : null;

        var docs = new List<RagDoc>();
        if (_files.ContainsKey("recipes")) docs.AddRange(RecipeDocs(Load("recipes"), names));
        if (smelting != null) docs.AddRange(smelting.Docs(names, Version));
        foreach (var it in items.Where(i => !blockNames.Contains(i.Name))) // block items get their block doc
        {
            var sb = new StringBuilder($"Item: {it.Display} ({Reg(it.Name)}id {it.Id}). ");
            sb.Append(it.Stack == 1 ? "Does not stack." : $"Stacks to {it.Stack}.");
            if (it.Durability > 0) sb.Append($" Durability {it.Durability} uses.");
            if (FoodOf(it) is { } f)
                sb.Append($" Food: restores {F(f.Points)} hunger ({F(f.Points / 2)} shanks), saturation {F(f.Saturation)}.");
            AppendVariants(sb, it.Display, it.Variants);
            smelting?.AppendFacts(sb, it.Id, names);
            docs.Add(Doc($"gd:item:{it.Id}", "item", $"item {it.Display}", sb.ToString()));
        }
        foreach (var b in blocks.Where(b => b.Id > 0 && b.Name != "air"))
        {
            var sb = new StringBuilder($"Block: {b.Display} ({Reg(b.Name)}id {b.Id}). ");
            sb.Append(!b.Diggable || b.Hardness is < 0 ? "Unbreakable." : b.Hardness is null or 0 ? "Breaks instantly." : $"Hardness {F(b.Hardness.Value)}.");
            if (ToolOf(b.Material) is { } tool) sb.Append($" Best tool: {tool}.");
            if (b.HarvestTools.Count > 0)
            {
                var tools = b.HarvestTools.Select(t => names.Of(t)).OrderBy(TierRank).ThenBy(n => n, StringComparer.Ordinal).ToList();
                var kinds = tools.Select(t => t.Split(' ')[^1]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // One tool kind by tier ("at least a Stone Pickaxe"): short, and "iron pickaxe" stays the name of the iron pickaxe's own docs.
                if (kinds.Count == 1 && tools.All(t => TierRank(t) < 5)) sb.Append($" Needs at least a {tools[0]} to drop anything.");
                else sb.Append(" Drops anything only when mined with: ").Append(string.Join(", ", tools)).Append('.');
            }
            var drops = (Tables != null ? TableDrops(b, names) : b.Drops).Where(d => d.Id != 0).ToList();
            if (drops.Count > 0) sb.Append(" Drops ").Append(string.Join(", ", drops.Select(names.Of))).Append('.');
            else if (b.Diggable && b.Hardness is >= 0) sb.Append(" Drops nothing.");
            if (!b.Solid) sb.Append(" Not solid (walk through).");
            if (b.Light > 0) sb.Append($" Gives light {b.Light}.");
            AppendVariants(sb, b.Display, b.Variants);
            smelting?.AppendFacts(sb, b.Id, names);
            docs.Add(Doc($"gd:block:{b.Id}", "block", $"block {b.Display}", sb.ToString()));
        }
        if (_files.ContainsKey("entities") || _files.ContainsKey("facts-mobs"))
            docs.AddRange(MobDocs(_files.ContainsKey("entities") ? Load("entities") : default,
                _files.ContainsKey("facts-mobs") ? Load("facts-mobs") : default, names));
        return docs;
    }

    /// <summary>The block's drops from <see cref="Tables"/> (by hand, else with the first harvest tool that can) over its variants,
    /// as names: a drop whose variant follows the block's (planks, logs → their own; leaves → that wood's sapling) reads as "any",
    /// a fixed variant of another item keeps it (lapis ore → Lapis Lazuli).</summary>
    private List<Ingredient> TableDrops(Block b, Names names)
    {
        var t = Tables!;
        int held = t.CanHarvest(b.Id, -1) ? -1 : b.HarvestTools.Where(h => t.CanHarvest(b.Id, h)).DefaultIfEmpty(-1).First();
        var metas = b.Variants.Count > 0 ? b.Variants.Select(v => v.Meta).ToList() : [0];
        var seen = new List<(int Id, HashSet<int> Damages)>();
        foreach (int meta in metas)
            foreach (var d in t.DropsFor(b.Id, meta, held))
            {
                int k = seen.FindIndex(x => x.Id == d.ItemId);
                if (k < 0) seen.Add((d.ItemId, [d.Damage]));
                else seen[k].Damages.Add(d.Damage);
            }
        return seen.Select(x => new Ingredient(x.Id, x.Damages.Count == 1 && x.Id != b.Id && names.HasVariants(x.Id) ? x.Damages.First() : null)).ToList();
    }

    /// <summary>"cooked_beef, " (the registry name, so a search for the internal name or another wording finds the doc).</summary>
    private static string Reg(string registry) => registry.Length > 0 ? registry + ", " : "";

    private static void AppendVariants(StringBuilder sb, string display, IReadOnlyList<Variant> variants)
    {
        var distinct = variants.Select(v => v.Display).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
        if (distinct.Count > 1) sb.Append(" Variants: ").Append(string.Join(", ", distinct)).Append('.');
    }

    /// <summary>Tool tier order for harvest lists (wooden, golden, stone, iron, diamond).</summary>
    private static int TierRank(string tool) =>
        tool.StartsWith("Wood", StringComparison.OrdinalIgnoreCase) ? 0 : tool.StartsWith("Gold", StringComparison.OrdinalIgnoreCase) ? 1
        : tool.StartsWith("Stone", StringComparison.OrdinalIgnoreCase) ? 2 : tool.StartsWith("Iron", StringComparison.OrdinalIgnoreCase) ? 3
        : tool.StartsWith("Diamond", StringComparison.OrdinalIgnoreCase) ? 4 : 5;

    private static RagDoc Doc(string reference, string tag, string title, string body) =>
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
    private static List<Ingredient> BlockDrops(JsonElement e)
    {
        var list = new List<Ingredient>();
        if (!e.TryGetProperty("drops", out var d) || d.ValueKind != JsonValueKind.Array) return list;
        foreach (var x in d.EnumerateArray())
        {
            var v = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("drop", out var dr) ? dr : x;
            if (IngredientOf(v) is { } i && !list.Contains(i)) list.Add(i);
        }
        return list;
    }

    /// <summary>An ingredient/result: a number (any variant), {"id": n, "metadata": m} (32767 = any), or null (an empty cell).</summary>
    private static Ingredient? IngredientOf(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt32(out var i) => new Ingredient(i, null),
        JsonValueKind.Object when Int(v, "id", -1) is var i && i >= 0 =>
            new Ingredient(i, v.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Number && m.GetInt32() != AnyMeta ? m.GetInt32() : null),
        _ => null,
    };

    private static List<RagDoc> RecipeDocs(JsonElement recipes, Names names)
    {
        var docs = new List<RagDoc>();
        foreach (var entry in recipes.EnumerateObject().OrderBy(p => int.TryParse(p.Name, NumberStyles.Integer, Inv, out var k) ? k : int.MaxValue).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            var counts = new Dictionary<int, int>();
            foreach (var r in entry.Value.EnumerateArray())
            {
                if (!r.TryGetProperty("result", out var res)) continue;
                int outId = IngredientOf(res)?.Id ?? (int.TryParse(entry.Name, NumberStyles.Integer, Inv, out var k) ? k : -1);
                if (outId < 0) continue;
                int meta = res.ValueKind == JsonValueKind.Object ? Int(res, "metadata") : 0;
                int count = res.ValueKind == JsonValueKind.Object ? Int(res, "count", 1) : 1;
                int n = counts[meta] = counts.GetValueOrDefault(meta) + 1;
                var output = names.Of(outId, meta);
                var sb = new StringBuilder($"Crafting recipe: {output}. Makes {count} per craft. ");
                if (r.TryGetProperty("inShape", out var shape) && shape.ValueKind == JsonValueKind.Array)
                {
                    var rows = shape.EnumerateArray().Select(row => row.EnumerateArray().Select(IngredientOf).ToList()).ToList();
                    int h = rows.Count, w = rows.Count == 0 ? 0 : rows.Max(x => x.Count);
                    sb.Append(w <= 2 && h <= 2 ? "Fits the 2x2 inventory grid (no crafting table needed). " : "Needs a crafting table (3x3 grid). ");
                    var totals = rows.SelectMany(x => x).OfType<Ingredient>().GroupBy(i => i).OrderBy(g => g.Key.Id).ThenBy(g => g.Key.Meta ?? -1)
                        .Select(g => $"{g.Count()} {names.Of(g.Key)}");
                    sb.Append("Ingredients: ").Append(string.Join(", ", totals)).Append(". ");
                    var labels = rows.Select((row, y) => (h == 1 ? "one row" : y == 0 ? "top row" : y == h - 1 ? "bottom row" : "middle row") + ": " +
                        string.Join(", ", Enumerable.Range(0, w).Select(x => x < row.Count && row[x] is { } id ? names.Of(id) : "empty")));
                    sb.Append($"Shape {w} wide by {h} tall; ").Append(string.Join("; ", labels)).Append('.');
                }
                else if (r.TryGetProperty("ingredients", out var ing) && ing.ValueKind == JsonValueKind.Array)
                {
                    var ids = ing.EnumerateArray().Select(IngredientOf).OfType<Ingredient>().ToList();
                    sb.Append(ids.Count <= 4 ? "Fits the 2x2 inventory grid (no crafting table needed). " : "Needs a crafting table (3x3 grid). ");
                    sb.Append("Ingredients: ").Append(string.Join(", ", ids.GroupBy(i => i).OrderBy(g => g.Key.Id).ThenBy(g => g.Key.Meta ?? -1)
                        .Select(g => $"{g.Count()} {names.Of(g.Key)}"))).Append(". ");
                    sb.Append("Shapeless: put the ingredients anywhere in the grid.");
                }
                else continue;
                docs.Add(Doc($"gd:recipe:{outId}:{meta}:{n}", "recipe", $"recipe {output} x{count}", sb.ToString()));
            }
        }
        return docs;
    }

    /// <summary>
    /// smelting.json: <c>{cook_ticks, smelting: [{input, input_meta, output, output_meta, count, xp}], fuels: [{item, meta, ticks, note}]}</c>
    /// with minecraft-data registry names (null meta = any variant; a fuel row with a null item is a rule, e.g. "any wooden slab").
    /// </summary>
    private sealed class SmeltingFacts
    {
        public sealed record Row(Ingredient Input, Ingredient Output, int Count, double Xp);
        public sealed record Fuel(Ingredient? Item, int Ticks, string? Note);

        public int CookTicks { get; private init; } = 200;
        public List<Row> Rows { get; } = [];
        public List<Fuel> Fuels { get; } = [];

        public static SmeltingFacts Parse(JsonElement e, Names names)
        {
            var f = new SmeltingFacts { CookTicks = Int(e, "cook_ticks", 200) };
            static int? Meta(JsonElement x, string p) =>
                x.TryGetProperty(p, out var m) && m.ValueKind == JsonValueKind.Number && m.GetInt32() != AnyMeta ? m.GetInt32() : null;
            Ingredient? Resolve(string? name, int? meta) => name != null && names.Id(name) is { } id ? new Ingredient(id, meta) : null;
            if (e.TryGetProperty("smelting", out var rows) && rows.ValueKind == JsonValueKind.Array)
                foreach (var r in rows.EnumerateArray())
                {
                    var input = Resolve(Str(r, "input"), Meta(r, "input_meta"));
                    var output = Resolve(Str(r, "output"), Meta(r, "output_meta") ?? 0);
                    if (input is { } i && output is { } o) f.Rows.Add(new Row(i, o, Int(r, "count", 1), Num(r, "xp") ?? 0));
                    else throw new InvalidDataException($"smelting.json: unknown item in {r.GetRawText()} (use minecraft-data registry names)");
                }
            if (e.TryGetProperty("fuels", out var fuels) && fuels.ValueKind == JsonValueKind.Array)
                foreach (var r in fuels.EnumerateArray())
                {
                    var name = Str(r, "item");
                    var item = Resolve(name, Meta(r, "meta"));
                    if (name != null && item == null) throw new InvalidDataException($"smelting.json: unknown fuel item '{name}'");
                    f.Fuels.Add(new Fuel(item, Int(r, "ticks"), Str(r, "note")));
                }
            return f;
        }

        private string Items(int ticks) => F(ticks / (double)CookTicks);

        private static string Label(Fuel f, Names names) =>
            f.Item is { } i ? names.Of(i) + (f.Note is { Length: > 0 } n ? $" ({n})" : "") : f.Note ?? "?";

        public IEnumerable<RagDoc> Docs(Names names, string version)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            string Unique(string r) { int n = seen[r] = seen.GetValueOrDefault(r) + 1; return n == 1 ? r : $"{r}:{n}"; }
            foreach (var s in Rows)
            {
                string input = names.Of(s.Input), output = names.Of(s.Output);
                var regIn = names.Registry(s.Input.Id) is { } ri ? $" ({ri})" : "";
                var regOut = names.Registry(s.Output.Id) is { } ro ? $" ({ro})" : "";
                var body = $"Smelting: {input}{regIn} smelts into {output}{regOut} in a furnace ({s.Count} {output} per {input}, {F(s.Xp)} xp). " +
                           $"Each item takes {CookTicks / 20} s ({CookTicks} ticks); any furnace fuel works.";
                var r = s.Input.Meta is { } m ? $"gd:smelt:{s.Input.Id}:{m}" : $"gd:smelt:{s.Input.Id}";
                yield return Doc(Unique(r), "smelting", $"smelt {input} -> {output}", body);
            }
            int rule = 0;
            foreach (var f in Fuels.OrderByDescending(f => f.Ticks).ThenBy(f => f.Item?.Id ?? int.MaxValue).ThenBy(f => f.Note, StringComparer.Ordinal))
            {
                var label = Label(f, names);
                var r = f.Item is { } i ? (i.Meta is { } m ? $"gd:fuel:{i.Id}:{m}" : $"gd:fuel:{i.Id}") : $"gd:fuel:rule:{++rule}";
                yield return Doc(Unique(r), "fuel", $"fuel {label}",
                    $"Furnace fuel: {label} burns {f.Ticks} ticks ({F(f.Ticks / 20.0)} s), enough to smelt {Items(f.Ticks)} items.");
            }
            if (Fuels.Count > 0)
            {
                var groups = Fuels.GroupBy(f => f.Ticks).OrderByDescending(g => g.Key)
                    .Select(g => $"{Items(g.Key)} items per {string.Join("/", g.Take(4).Select(x => x.Item is { } i ? names.Of(i) : x.Note ?? "?"))}{(g.Count() > 4 ? " and similar" : "")}");
                yield return Doc("gd:fuel:overview", "fuel", "furnace fuels overview",
                    $"Furnace fuels in {version} (one item smelts in {CookTicks / 20} s): " + string.Join("; ", groups) + ".");
            }
        }

        /// <summary>" Fuel: smelts N items. Smelts into X. Smelted from Y." on an item or block doc.</summary>
        public void AppendFacts(StringBuilder sb, int id, Names names)
        {
            if (Fuels.FirstOrDefault(f => f.Item?.Id == id) is { } fuel) sb.Append($" Fuel: smelts {Items(fuel.Ticks)} items.");
            var into = Rows.Where(r => r.Input.Id == id).Select(r => names.Of(r.Output)).Distinct().ToList();
            if (into.Count > 0) sb.Append(" Smelts into ").Append(string.Join(", ", into)).Append('.');
            var from = Rows.Where(r => r.Output.Id == id).Select(r => names.Of(r.Input)).Distinct().ToList();
            if (from.Count > 0) sb.Append(" Smelted from ").Append(string.Join(", ", from)).Append('.');
        }
    }

    /// <summary>
    /// One doc per living mob: entities.json (display name, hostile/passive, height) joined by name with mobs.json
    /// (<c>{mobs: [{name, health, attack, xp, drops: [{item, min, max, looting, condition}], rare: [{item, chance, condition}], notes}]}</c>).
    /// </summary>
    private static IEnumerable<RagDoc> MobDocs(JsonElement entities, JsonElement facts, Names names)
    {
        var info = new SortedDictionary<string, (string Display, string? Category, double? Height)>(StringComparer.Ordinal);
        if (entities.ValueKind == JsonValueKind.Array)
            foreach (var e in entities.EnumerateArray())
            {
                var cat = Str(e, "category");
                if (Str(e, "type") != "mob" || cat is not ("Hostile mobs" or "Passive mobs") || Str(e, "name") is not { Length: > 0 } n) continue;
                info.TryAdd(n, (Str(e, "displayName") ?? n, cat, Num(e, "height")));
            }
        var byName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (facts.ValueKind == JsonValueKind.Object && facts.TryGetProperty("mobs", out var mobs) && mobs.ValueKind == JsonValueKind.Array)
            foreach (var m in mobs.EnumerateArray())
                if (Str(m, "name") is { Length: > 0 } n)
                {
                    byName[n] = m;
                    if (!info.ContainsKey(n)) info[n] = (n.Replace('_', ' '), null, null);
                }

        string ItemName(JsonElement d) => Str(d, "item") is { } i && names.Id(i) is { } id
            ? names.Of(id, d.TryGetProperty("meta", out var mm) && mm.ValueKind == JsonValueKind.Number ? mm.GetInt32() : null)
            : (Str(d, "item") ?? "?").Replace('_', ' ');

        foreach (var (name, (display, cat, height)) in info)
        {
            var kind = cat == "Hostile mobs" ? ", hostile" : cat == "Passive mobs" ? ", passive or neutral" : "";
            var sb = new StringBuilder($"Mob: {display} ({name}){kind}.");
            if (byName.TryGetValue(name, out var m))
            {
                if (Num(m, "health") is { } hp) sb.Append($" Health {F(hp)} ({F(hp / 2)} hearts).");
                else if (Str(m, "health") is { Length: > 0 } hps) sb.Append($" Health {hps}.");
                if (Num(m, "attack") is { } atk) sb.Append($" Attack {F(atk)}.");
                else if (Str(m, "attack") is { Length: > 0 } atks) sb.Append($" Attack {atks}.");
                if (Str(m, "xp") is { Length: > 0 } xp) sb.Append($" XP {xp}.");
                if (m.TryGetProperty("drops", out var drops) && drops.ValueKind == JsonValueKind.Array && drops.GetArrayLength() > 0)
                    sb.Append(" Drops: ").Append(string.Join("; ", drops.EnumerateArray().Select(d =>
                    {
                        var range = Num(d, "min") is { } lo && Num(d, "max") is { } hi ? (lo == hi ? $"{F(lo)} " : $"{F(lo)}-{F(hi)} ") : "";
                        var extra = new[]
                        {
                            Num(d, "chance") is { } ch && ch < 1 ? $"{F(ch * 100)}%" : null,
                            Str(d, "looting") is { Length: > 0 } l ? "Looting " + l : null, Str(d, "condition"),
                        }.OfType<string>().ToList();
                        return range + ItemName(d) + (extra.Count > 0 ? $" ({string.Join(", ", extra)})" : "");
                    }))).Append('.');
                else sb.Append(" Drops nothing.");
                if (m.TryGetProperty("rare", out var rare) && rare.ValueKind == JsonValueKind.Array && rare.GetArrayLength() > 0)
                {
                    // Consecutive rares with one condition (zombie: iron ingot, carrot, potato from one roll) share it.
                    var groups = new List<(string? Cond, List<string> Items)>();
                    foreach (var d in rare.EnumerateArray())
                    {
                        var item = ItemName(d) + (Num(d, "chance") is { } c ? $" {F(c * 100)}%" : "");
                        var cond = Str(d, "condition") is { Length: > 0 } cd ? cd : null;
                        if (groups.Count > 0 && groups[^1].Cond == cond) groups[^1].Items.Add(item);
                        else groups.Add((cond, [item]));
                    }
                    sb.Append(" Rare: ").Append(string.Join("; ", groups.Select(g => string.Join(", ", g.Items) + (g.Cond != null ? $" ({g.Cond})" : "")))).Append('.');
                }
                if (Str(m, "notes") is { Length: > 0 } notes) sb.Append(' ').Append(notes.Trim());
            }
            if (height is { } h) sb.Append($" {F(h)} blocks tall.");
            yield return Doc($"gd:mob:{name}", "mob", $"mob {display}", sb.ToString());
        }
        if (facts.ValueKind == JsonValueKind.Object && Str(facts, "attack_note") is { Length: > 0 } note)
            yield return Doc("gd:mob:overview", "mob", "mob attack and xp rules", "Mobs: " + note.Trim());
    }
}
