using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using MineClaw.Data.Versioning;

namespace MineClaw.Bot.Blueprints;

/// <summary>One palette button: a block state as the palette offers it.</summary>
/// <param name="Spec">The canonical spelling the AI tools and the DSL resolve back to exactly this state ("wool:red").</param>
/// <param name="Word">The variant word after the base name (<c>red</c>), or null.</param>
/// <param name="Icon">An icon hint for the browser: 1.12.2 the item model ("oak_door"), b1.8.1 "item:N" (items.png tile); null =
/// drawn from the block's own textures.</param>
public sealed record DesignBlock(int Id, int Meta, string Name, string Spec, string Category, DesignFamily Family, string? Word, string? Icon)
{
    public bool Facing => DesignFacing.HasFacing(Family);
    public string Base => Spec.Split(':')[0];
}

/// <summary>
/// Every block of a game version a design may hold (docs/web/api.md "Design Studio", "The palette"): the Design Studio's palette,
/// the AI's vocabulary (<see cref="Resolve"/>, <see cref="Find"/>) and the rule of what may stand on a canvas or in a versioned
/// design (<see cref="Refusal"/>). Curated from the version's block table (1.12.2: minecraft-data pc/1.12.2 blocks and their
/// variations; b1.8.1: the hand table, MineClaw.Data.Blocks) in creative-tab categories, one entry per placeable variant (every
/// colour, wood, stone kind); facings, halves and runtime states are metas of an entry, not entries. Left out (<see cref="Excluded"/>):
/// technical and unobtainable blocks: air, flowing fluids, piston heads and moving pistons, portals, fire, spawners, command /
/// structure blocks, barriers, double-slab technical variants whose full block exists, runtime states (lit furnace, lit redstone
/// ore and lamp, unlit redstone torch, powered repeater / comparator) and, for b1.8.1, what that version can't obtain (grass, ores
/// that drop items, leaves that decay, ice, cobwebs, huge mushrooms). Builders still build only <see cref="BuildBlocks"/> blocks:
/// the rest of a design is reported as unsupported cells, never placed wrong.
/// </summary>
public sealed partial class DesignPalette
{
    public const string Building = "Building", Decoration = "Decoration", Colored = "Colored", Nature = "Nature", Redstone = "Redstone",
        Transport = "Transport", Misc = "Misc";

    public static readonly IReadOnlyList<string> Categories = [Building, Decoration, Colored, Nature, Redstone, Transport, Misc];

    public string Version { get; }
    public IReadOnlyList<DesignBlock> Entries { get; }
    /// <summary>id → why it isn't a design block in this version.</summary>
    public IReadOnlyDictionary<int, string> Excluded { get; }

    private readonly Dictionary<int, List<DesignBlock>> _byId = new();
    private readonly Dictionary<string, DesignBlock> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _base = new(StringComparer.Ordinal);
    private readonly IGameData _data;

    private static readonly ConcurrentDictionary<string, DesignPalette> Cache = new();

    /// <summary>The palette of a version ("b1.8.1" / "1.12.2"; anything else: the server's).</summary>
    public static DesignPalette For(string? version)
    {
        var v = DesignVersions.Normalize(version) ?? DesignVersions.Server;
        return Cache.GetOrAdd(v, k => new DesignPalette(k));
    }

    /// <summary>The palette of the version in this flow (<see cref="GameData.Current"/>).</summary>
    public static DesignPalette Current => For(GameData.Current.VersionId);

    private DesignPalette(string version)
    {
        Version = version;
        _data = DesignVersions.Data(version);
        var (entries, excluded) = version == DesignVersions.Release ? Release() : Beta();
        Entries = entries;
        Excluded = excluded;
        foreach (var e in entries)
        {
            if (!_byId.TryGetValue(e.Id, out var l)) _byId[e.Id] = l = [];
            l.Add(e);
        }
        foreach (var e in entries) AddExactKeys(version, e);
        // Base names → the id's first entry (variant words and modifiers follow after ':').
        foreach (var (id, list) in _byId)
        {
            BaseKey(list[0].Base, id);
            BaseKey(_data.Block(id).Name, id);
            BaseKey(_data.Block(id).DisplayName, id);
        }
        foreach (var (alias, id) in BaseAliases)
            if (_byId.ContainsKey(id)) BaseKey(alias, id);
    }

    /// <summary>Exact spellings of an entry: its spec, its name, "&lt;word&gt;_&lt;base&gt;" (red_wool), the item model name, the aliases.</summary>
    private void AddExactKeys(string version, DesignBlock e)
    {
        Key(_exact, e.Spec, e);
        Key(_exact, e.Name, e);
        if (e.Word != null)
        {
            Key(_exact, e.Word + "_" + e.Base, e);
            foreach (var w in WordAliases(e.Word)) { Key(_exact, w + "_" + e.Base, e); Key(_exact, e.Base + "_" + w, e); }
        }
        if (e.Icon != null && !e.Icon.StartsWith("item:", StringComparison.Ordinal)) Key(_exact, e.Icon, e);
        foreach (var a in Aliases(version, e)) Key(_exact, a, e);
    }

    private static void Key<T>(Dictionary<string, T> d, string s, T v) => d.TryAdd(N(s), v);
    private void BaseKey(string s, int id) => _base.TryAdd(N(s), id);

    /// <summary>Lower case, no "minecraft:", no spaces / underscores / dashes / apostrophes / dots / colons.</summary>
    public static string N(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        if (t.StartsWith("minecraft:", StringComparison.Ordinal)) t = t[10..];
        var sb = new StringBuilder(t.Length);
        foreach (var c in t) if (c is not (' ' or '_' or '-' or '\'' or '.' or ':')) sb.Append(c);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ lookups

    /// <summary>Whether the id is a design block of this version (any of its states).</summary>
    public bool Has(int id) => _byId.ContainsKey(id);

    public IReadOnlyList<DesignBlock> Of(int id) => _byId.TryGetValue(id, out var l) ? l : [];

    /// <summary>The entry for exactly this state, or null.</summary>
    public DesignBlock? Entry(int id, int meta) => _byId.TryGetValue(id, out var l) ? l.FirstOrDefault(e => e.Meta == meta) : null;

    /// <summary>The entry a state is a form of (its variant, a second half's first half), or null.</summary>
    public DesignBlock? EntryOf(int id, int meta)
    {
        if (!_byId.TryGetValue(id, out var l)) return null;
        if (l.FirstOrDefault(e => e.Meta == meta) is { } exact) return exact;
        int mask = DesignFacing.VariantMask(id);
        if (DesignFacing.TwoCell(id)) meta &= DesignFacing.FamilyOf(id) == DesignFamily.Bed ? 3 : 7;
        return l.FirstOrDefault(e => (e.Meta & mask) == (meta & mask)) ?? l[0];
    }

    /// <summary>Display name of a state ("Red Wool", "Oak Wood Stairs").</summary>
    public string Name(int id, int meta) => EntryOf(id, meta)?.Name ?? (_data.BlockExists(id) ? _data.Block(id).DisplayName : $"block {id}");

    /// <summary>A spelling that resolves back to exactly (id, meta): the entry's spec, else "registry_name:meta".</summary>
    public string Spec(int id, int meta)
    {
        if (Entry(id, meta) is { } e) return e.Spec;
        var name = _byId.TryGetValue(id, out var l) ? l[0].Base : _data.Block(id).Name;
        return $"{name}:{meta.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The category of a block id ("Building"), or null.</summary>
    public string? CategoryOf(int id) => _byId.TryGetValue(id, out var l) ? l[0].Category : null;

    // ------------------------------------------------------------------ validity

    /// <summary>Why (id, meta) can't stand in a design of this version (null = it can).</summary>
    public string? Refusal(int id, int meta)
    {
        if (id == 0) return null;
        if (id < 0) return $"no block id {id}";
        if (!_byId.TryGetValue(id, out var l))
        {
            if (!_data.BlockExists(id)) return $"block id {id} doesn't exist in {Version}";
            var n = _data.Block(id).DisplayName;
            return Excluded.TryGetValue(id, out var why) ? $"{n} isn't a design block in {Version}: {why}" : $"{n} isn't placeable in {Version}";
        }
        return Valid(id, meta) ? null : $"{l[0].Name} meta {meta} isn't one of its states in {Version}";
    }

    // ------------------------------------------------------------------ names → states

    /// <summary>
    /// A block spelled the way the tools and the DSL accept it → (id, meta), or why not (with "did you mean …?"). Accepted:
    /// minecraft names ("oak_stairs", "minecraft:stained_glass"), display names ("Red Wool"), variants after ':' ("wool:red",
    /// "stained_glass:light_blue", "log:spruce", "concrete:white") or before the name ("red_wool", "spruce_planks"), a raw meta
    /// ("wool:14"), then modifiers after more ':' or ',': a direction (the family's sense: stairs the way up, doors the side they
    /// open to, chests/furnaces/pumpkins the front, beds the head's side, torches/ladders/signs/buttons/vines the side their
    /// supporting block is on, gates the way through, rails the way along), <c>wall=west</c>, <c>floor</c>, <c>ceiling</c>,
    /// <c>up</c>/<c>down</c>, <c>top</c>/<c>bottom</c> (1.12.2 slabs, stairs, trapdoors), <c>open</c>, an axis <c>x</c>/<c>y</c>/<c>z</c>.
    /// </summary>
    public bool TryResolve(string spec, out (int Id, int Meta) state, out string? why)
    {
        state = default;
        why = null;
        var s = (spec ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
        if (s.StartsWith("minecraft:", StringComparison.Ordinal)) s = s[10..];
        if (s.Length == 0) { why = "a block name is required"; return false; }
        var parts = s.Split([':', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var args = parts.Skip(1).ToList();
        if (!TryHead(parts[0], args, spec ?? "", out int id, out int meta, out why)) return false;
        if (!ApplyModifiers(id, ref meta, args, out why)) return false;
        if (Refusal(id, meta) is { } r) { why = r; return false; }
        state = (id, meta);
        return true;
    }

    /// <summary>The block a spelling's head names (taking a variant word off <paramref name="args"/> when it is one).</summary>
    private bool TryHead(string head, List<string> args, string spec, out int id, out int meta, out string? why)
    {
        why = null;
        // "wool:red" / "log:spruce" as one spelling, then the head alone ("red_wool", "oak_door"), then a base name + variant word.
        if (ExactEntry(head, args) is { } entry)
        {
            (id, meta) = (entry.Id, entry.Meta);
            return true;
        }
        if (TryBaseId(head, out id)) return TryVariant(head, id, args, out meta, out why);
        meta = 0;
        why = Unknown(spec);
        return false;
    }

    private DesignBlock? ExactEntry(string head, List<string> args)
    {
        if (args.Count > 0 && _exact.TryGetValue(N(head + "_" + args[0]), out var e2) && !IsModifier(args[0]))
        {
            args.RemoveAt(0);
            return e2;
        }
        return _exact.GetValueOrDefault(N(head));
    }

    private bool TryBaseId(string head, out int id) => _base.TryGetValue(N(head), out id) || NameLookup(head) is { } looked && (id = looked.Id) > 0;

    /// <summary>A base name's state: its first entry, the item damage it was named with, then a variant word after ':'.</summary>
    private bool TryVariant(string head, int id, List<string> args, out int meta, out string? why)
    {
        why = null;
        var list = _byId[id];
        meta = list[0].Meta;
        if (NameLookup(head) is { Meta: >= 0 } hit && hit.Id == id && list.FirstOrDefault(x => x.Meta == hit.Meta) is { } byDamage) meta = byDamage.Meta;
        if (args.Count == 0 || IsModifier(args[0]) || int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)) return true;
        var w = N(args[0]);
        var hitWord = list.FirstOrDefault(x => x.Word != null && (N(x.Word) == w || WordAliases(x.Word).Any(a => N(a) == w)));
        if (hitWord == null) { why = NotAVariant(list, args[0]); return false; }
        meta = hitWord.Meta;
        args.RemoveAt(0);
        return true;
    }

    private string NotAVariant(List<DesignBlock> list, string word)
    {
        var words = list.Where(x => x.Word != null).Select(x => x.Word!).Distinct().ToList();
        return words.Count > 0 ? $"'{word}' isn't a variant of {list[0].Base} in {Version} ({string.Join(", ", words.Take(16))})"
            : $"'{word}' isn't a facing, variant or meta for {list[0].Base}";
    }

    private bool ApplyModifiers(int id, ref int meta, List<string> args, out string? why)
    {
        why = null;
        foreach (var a in args)
        {
            if (!Modify(id, meta, a, out var m2, out why)) return false;
            meta = m2;
        }
        return true;
    }

    /// <summary><see cref="TryResolve"/>, throwing <see cref="ArgumentException"/> with the reason.</summary>
    public (int Id, int Meta) Resolve(string spec) => TryResolve(spec, out var st, out var why) ? st : throw new ArgumentException(why);

    /// <summary>The version's own forgiving names (aliases like "cobble", "workbench"; item names with a damage) → a design block.</summary>
    private (int Id, int Meta)? NameLookup(string head)
    {
        try
        {
            if (_data.ItemByNameWithDamage(head) is { } it)
            {
                int bid = BlockOfItem(it.Item.Id);
                if (_byId.ContainsKey(bid)) return (bid, bid == it.Item.Id ? it.Damage : -1);
            }
            if (_data.BlockByName(head) is { } b && _byId.ContainsKey(b.Id)) return (b.Id, -1);
        }
        catch (Exception) { /* a lookup that throws is a miss */ }
        return null;
    }

    /// <summary>Items that place a different block id (doors, signs, beds, redstone, repeaters, cauldrons…).</summary>
    private static readonly Dictionary<int, int> ItemBlocks = new()
    {
        [324] = 64, [427] = 193, [428] = 194, [429] = 195, [430] = 196, [431] = 197, [330] = 71, [355] = 26, [323] = 63, [331] = 55,
        [338] = 83, [354] = 92, [356] = 93, [404] = 149, [379] = 117, [380] = 118, [390] = 140, [397] = 144, [425] = 176, [287] = 132, [295] = 59,
    };

    private static int BlockOfItem(int item) => ItemBlocks.GetValueOrDefault(item, item);

    /// <summary>"unknown 1.12.2 block 'redwol'; did you mean red_wool (wool:red), …?"</summary>
    public string Unknown(string spec)
    {
        var near = DidYouMean(spec, 3);
        return $"unknown {Version} block '{spec}'" + (near.Count > 0 ? "; did you mean " + string.Join(", ", near) + "?" : "; find_blocks looks names up");
    }

    /// <summary>The specs closest to a misspelling (edit distance over every spelling, then shared words).</summary>
    public List<string> DidYouMean(string spec, int max = 3)
    {
        var q = N(spec.Split(':')[0]);
        if (q.Length == 0) return [];
        var scored = new List<(double Score, string Spec)>();
        foreach (var (key, e) in _exact) scored.Add((Distance(q, key) / (double)Math.Max(q.Length, key.Length), e.Spec));
        foreach (var (key, id) in _base) scored.Add((Distance(q, key) / (double)Math.Max(q.Length, key.Length) + 0.01, _byId[id][0].Spec));
        var byWords = Find(spec.Replace('_', ' ').Replace(':', ' '), max).Select(e => (0.35, e.Spec));
        return scored.Concat(byWords).Where(x => x.Item1 <= 0.45).OrderBy(x => x.Item1).Select(x => x.Item2).Distinct().Take(max).ToList();
    }

    private static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // ------------------------------------------------------------------ search and the model's summary

    /// <summary>
    /// Entries matching every word of the query (a prefix of a word of the name, spec or category, the id or id:meta), best first:
    /// the palette's search and the AI's <c>find_blocks</c>.
    /// </summary>
    public List<DesignBlock> Find(string query, int max = 20)
    {
        // "251:5" stays one word (an id:meta); "wool:red" / "red_wool" are two.
        var words = (query ?? "").ToLowerInvariant().Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(w => w.Contains(':') && w.Split(':').All(p => p.Length > 0 && p.All(char.IsDigit)) ? new[] { w } : w.Split([':', '_'], StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        if (words.Length == 0) return Entries.Take(max).ToList();
        var scored = new List<(double Score, int Index, DesignBlock E)>();
        for (int i = 0; i < Entries.Count; i++)
            if (FindScore(Entries[i], words) is { } score) scored.Add((score, i, Entries[i]));
        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Index).Take(max).Select(s => s.E).ToList();
    }

    /// <summary>How well an entry matches every query word (null = a word doesn't match).</summary>
    private static double? FindScore(DesignBlock e, string[] words)
    {
        var own = (e.Name + " " + e.Spec.Replace(':', ' ').Replace('_', ' ')).ToLowerInvariant();
        var hay = own.Replace(" ", ""); // inside a word: the name and spec only ("red" is not in "Colored")
        var parts = (own + " " + e.Category.ToLowerInvariant()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double score = 0;
        foreach (var w in words)
        {
            double s = WordScore(e, w, parts, hay);
            if (s == 0) return null;
            score += s;
        }
        return score;
    }

    /// <summary>The id or id:meta 3, a whole word 2, a word's prefix 1, inside the name or spec 0.5, else 0.</summary>
    private static double WordScore(DesignBlock e, string w, string[] parts, string hay)
    {
        if (w == e.Id.ToString(CultureInfo.InvariantCulture) || w == $"{e.Id}:{e.Meta}") return 3;
        if (parts.Any(p => p == w)) return 2;
        if (parts.Any(p => p.StartsWith(w, StringComparison.Ordinal))) return 1;
        return w.Length >= 3 && hay.Contains(w, StringComparison.Ordinal) ? 0.5 : 0;
    }

    /// <summary>
    /// The compact vocabulary for the model's cached system prompt: per category its count and the base names, the variant words.
    /// Never the whole list (find_blocks looks names up on demand).
    /// </summary>
    public string Summary()
    {
        // One word per family: "<colour>_glazed_terracotta" for sixteen of them, "<material>_stairs" for every stair.
        static string Generic(string b)
        {
            foreach (var c in Colors.OrderByDescending(c => c.Length))
                if (b.StartsWith(c + "_", StringComparison.Ordinal)) return "<colour>_" + b[(c.Length + 1)..];
            foreach (var w in Wood6.OrderByDescending(w => w.Length))
                if (b.StartsWith(w + "_", StringComparison.Ordinal)) return "<wood>_" + b[(w.Length + 1)..];
            foreach (var suffix in new[] { "_stairs", "_slab", "_fence_gate", "_fence", "_door", "_trapdoor", "_pressure_plate", "_button", "_rail" })
                if (b.EndsWith(suffix, StringComparison.Ordinal)) return "<material>" + suffix;
            return b;
        }
        // Only a real family is folded (red_sandstone and red_mushroom stay themselves).
        var family = Entries.Select(e => e.Base).Distinct().GroupBy(Generic).Where(g => g.Count() >= 3).Select(g => g.Key).ToHashSet();
        string Word(string b) => Generic(b) is var g && family.Contains(g) ? g : b;
        var sb = new StringBuilder();
        foreach (var cat in Categories)
        {
            var bases = Entries.Where(e => e.Category == cat).Select(e => Word(e.Base)).Distinct().ToList();
            if (bases.Count == 0) continue;
            int n = Entries.Count(e => e.Category == cat);
            var shown = bases.Take(18).ToList();
            sb.Append(cat).Append(" (").Append(n).Append("): ").Append(string.Join(" ", shown)).Append(bases.Count > shown.Count ? " …" : "").Append('\n');
        }
        if (Version == DesignVersions.Release)
            sb.Append("colours: white orange magenta light_blue yellow lime pink gray silver(light_gray) cyan purple blue brown green red black; ")
              .Append("woods: oak spruce birch jungle acacia dark_oak\n");
        else
            sb.Append("wool colours: white orange magenta light_blue yellow lime pink gray silver(light_gray) cyan purple blue brown green red black; ")
              .Append("woods: oak spruce birch (logs only)\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the tables

    private static readonly string[] Colors = ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "silver", "cyan", "purple", "blue", "brown", "green", "red", "black"];
    private static readonly string[] Wood6 = ["oak", "spruce", "birch", "jungle", "acacia", "dark_oak"];

    private static readonly Dictionary<string, string[]> WordAliasTable = new(StringComparer.Ordinal)
    {
        ["silver"] = ["light_gray", "light_grey", "lightgray"],
        ["gray"] = ["grey"],
        ["dark_oak"] = ["big_oak", "darkoak"],
        ["light_blue"] = ["lightblue"],
        ["houstonia"] = ["azure_bluet", "bluet"],
        ["syringa"] = ["lilac"],
        ["paeonia"] = ["peony"],
        ["double_rose"] = ["rose_bush"],
        ["double_grass"] = ["tall_grass", "double_tallgrass"],
        ["double_fern"] = ["large_fern"],
        ["plain"] = ["normal", "default"],
        ["stone_brick"] = ["stonebrick", "stone_bricks"],
        ["cobblestone"] = ["cobble"],
        ["brick"] = ["bricks"],
        ["wood"] = ["wooden", "oak", "planks"],
    };

    private static IEnumerable<string> WordAliases(string word) => WordAliasTable.GetValueOrDefault(word, []);

    /// <summary>Base spellings across both versions (the other version's registry name, common words).</summary>
    private static readonly (string Alias, int Id)[] BaseAliases =
    [
        ("stonebrick", 98), ("stone_bricks", 98), ("stone_brick", 98), ("sugar_cane", 83), ("reeds", 83), ("slab", 44), ("step", 44),
        ("wooden_slab", 126), ("wood_slab", 126), ("stairs", 53), ("wooden_stairs", 53), ("cobblestone_stairs", 67),
        ("bricks", 45), ("brick", 45), ("door", 64), ("oak_door", 64), ("trapdoor", 96), ("oak_trapdoor", 96), ("gate", 107), ("oak_fence_gate", 107),
        ("fence", 85), ("oak_fence", 85), ("sign", 63), ("wall_sign", 68), ("banner", 176), ("wall_banner", 177), ("bed", 26), ("chest", 54),
        ("glass_pane", 102), ("pane", 102), ("stained_glass", 95), ("stained_pane", 160), ("terracotta", 172), ("stained_terracotta", 159),
        ("stained_clay", 159), ("wool", 35), ("carpet", 171), ("concrete", 251), ("concrete_powder", 252), ("planks", 5), ("log", 17),
        ("wood", 17), ("leaves", 18), ("sapling", 6), ("torch", 50), ("redstone", 55), ("redstone_dust", 55), ("repeater", 93), ("comparator", 149),
        ("piston", 33), ("lamp", 123), ("jack_o_lantern", 91), ("lit_pumpkin", 91), ("melon", 103), ("glowstone", 89), ("quartz", 155),
        ("purpur", 201), ("end_stone_bricks", 206), ("nether_bricks", 112), ("magma_block", 213), ("slime_block", 165), ("hay", 170),
        ("hay_bale", 170), ("lily_pad", 111), ("cobweb", 30), ("flower", 37), ("dandelion", 37), ("rose", 38), ("poppy", 38), ("flower_pot", 140),
        ("button", 77), ("pressure_plate", 70), ("rails", 66), ("powered_rail", 27), ("crafting_table", 58), ("workbench", 58),
        ("note_block", 25), ("noteblock", 25), ("snow_block", 80), ("snow_layer", 78), ("water", 9), ("lava", 11), ("double_plant", 175),
        ("tall_grass", 31), ("tallgrass", 31), ("fern", 31), ("dead_bush", 32), ("grass_block", 2), ("hardened_clay", 172), ("red_sandstone", 179),
        ("mushroom_block", 99), ("skull", 144), ("head", 144), ("anvil", 145), ("shulker_box", 219), ("glazed_terracotta", 235),
    ];

    /// <summary>Extra exact spellings of an entry (both versions' names for it).</summary>
    private static IEnumerable<string> Aliases(string version, DesignBlock e)
    {
        foreach (var a in WordForms(e)) yield return a;
        foreach (var a in FixedAliases(e)) yield return a;
        foreach (var a in DerivedAliases(e)) yield return a;
        if (version == DesignVersions.Beta && e.Id == 44 && e.Meta == 2) { yield return "wooden_slab"; yield return "oak_slab"; yield return "wood_slab"; }
    }

    /// <summary>Spellings around a variant word ("{w}"), for an entry that has one.</summary>
    private static readonly Dictionary<int, string[]> WordFormsById = new()
    {
        [162] = ["log:{w}", "{w}_log", "{w}_wood"],
        [17] = ["{w}_log", "{w}_wood"],
        [126] = ["slab:{w}", "{w}_slab"],
        [161] = ["leaves:{w}", "{w}_leaves"],
        [18] = ["{w}_leaves"],
        [44] = ["slab:{w}", "{w}_slab"],
        [98] = ["stone_bricks:{w}", "stonebrick:{w}", "{w}_stone_bricks"],
        [5] = ["{w}_planks", "{w}_wood_planks"],
        [6] = ["{w}_sapling"],
        [1] = ["{w}"],
        [3] = ["{w}"],
        [38] = ["{w}"],
        [175] = ["{w}"],
    };

    /// <summary>Spellings around the variant word even without one (the word then reads as "").</summary>
    private static readonly Dictionary<int, string[]> AnyWordFormsById = new()
    {
        [160] = ["stained_pane:{w}", "{w}_glass_pane"],
        [159] = ["terracotta:{w}", "{w}_terracotta", "stained_clay:{w}"],
        [95] = ["{w}_glass"],
    };

    private static IEnumerable<string> WordForms(DesignBlock e)
    {
        var forms = e.Word != null && WordFormsById.TryGetValue(e.Id, out var f) ? f : AnyWordFormsById.GetValueOrDefault(e.Id, []);
        var w = e.Word ?? "";
        return forms.Select(x => x.Replace("{w}", w, StringComparison.Ordinal));
    }

    /// <summary>Other names of one variant (by id + word) or one state (by id + meta).</summary>
    private static readonly Dictionary<(int Id, string Word), string[]> AliasesByWord = new()
    {
        [(126, "oak")] = ["slab:wood", "wood_slab", "wooden_slab"],
        [(12, "red")] = ["red_sand"],
        [(31, "grass")] = ["tall_grass", "short_grass"],
        [(31, "fern")] = ["fern"],
    };

    private static readonly Dictionary<(int Id, int Meta), string[]> AliasesByMeta = new()
    {
        [(43, 8)] = ["smooth_stone"],
        [(99, 15)] = ["mushroom_stem"],
    };

    private static IEnumerable<string> FixedAliases(DesignBlock e)
    {
        var byWord = e.Word != null && AliasesByWord.TryGetValue((e.Id, e.Word), out var w) ? w : [];
        return byWord.Concat(AliasesByMeta.GetValueOrDefault((e.Id, e.Meta), []));
    }

    /// <summary>A double plant's other names ("lilac"), "chiseled_sandstone" / "smooth_red_sandstone".</summary>
    private static IEnumerable<string> DerivedAliases(DesignBlock e)
    {
        if (e.Id == 175 && e.Word != null) return WordAliases(e.Word);
        if (e.Id is 24 or 179 && e.Word is "chiseled" or "smooth") return [e.Word + "_" + e.Base];
        return [];
    }

    private static string Title(string w) => w switch
    {
        "silver" => "Light Gray",
        "dark_oak" => "Dark Oak",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w.Replace('_', ' ')),
    };

    private sealed class Table
    {
        public readonly List<DesignBlock> Entries = [];
        public readonly Dictionary<int, string> Excluded = [];

        public void A(int id, int meta, string cat, string spec, string name, string? icon = null)
        {
            var word = spec.Contains(':') ? spec[(spec.IndexOf(':') + 1)..] : null;
            Entries.Add(new DesignBlock(id, meta, name, spec, cat, DesignFacing.FamilyOf(id), word, icon));
        }

        /// <summary>One entry per word (meta = its index, or <paramref name="metas"/>).</summary>
        public void V(int id, string cat, string baseName, IReadOnlyList<string> words, Func<string, string> name, Func<string, string?>? icon = null,
            IReadOnlyList<int>? metas = null)
        {
            for (int i = 0; i < words.Count; i++) A(id, metas?[i] ?? i, cat, $"{baseName}:{words[i]}", name(Title(words[i])), icon?.Invoke(words[i]));
        }

        public void X(string why, params int[] ids) { foreach (var i in ids) Excluded[i] = why; }
    }

    private static (List<DesignBlock>, Dictionary<int, string>) Release()
    {
        var t = new Table();
        // Building blocks
        t.A(1, 0, Building, "stone", "Stone");
        t.V(1, Building, "stone", ["granite", "polished_granite", "diorite", "polished_diorite", "andesite", "polished_andesite"], n => n,
            w => w.StartsWith("polished_", StringComparison.Ordinal) ? w[9..] + "_smooth" : w, [1, 2, 3, 4, 5, 6]);
        t.A(4, 0, Building, "cobblestone", "Cobblestone");
        t.A(48, 0, Building, "mossy_cobblestone", "Mossy Cobblestone");
        t.V(5, Building, "planks", Wood6, n => n + " Planks", w => w + "_planks");
        t.V(17, Building, "log", ["oak", "spruce", "birch", "jungle"], n => n + " Log", w => w + "_log");
        t.V(162, Building, "log2", ["acacia", "dark_oak"], n => n + " Log", w => w + "_log");
        t.A(45, 0, Building, "brick_block", "Bricks");
        t.A(98, 0, Building, "stonebrick", "Stone Bricks");
        t.V(98, Building, "stonebrick", ["mossy", "cracked", "chiseled"], n => n + " Stone Bricks", null, [1, 2, 3]);
        t.A(24, 0, Building, "sandstone", "Sandstone");
        t.V(24, Building, "sandstone", ["chiseled", "smooth"], n => n + " Sandstone", w => w + "_sandstone", [1, 2]);
        t.A(179, 0, Building, "red_sandstone", "Red Sandstone");
        t.V(179, Building, "red_sandstone", ["chiseled", "smooth"], n => n + " Red Sandstone", w => w + "_red_sandstone", [1, 2]);
        t.A(43, 0, Building, "double_stone_slab", "Double Stone Slab");
        t.A(43, 8, Building, "double_stone_slab:smooth", "Smooth Stone");
        t.V(44, Building, "stone_slab", ["stone", "sandstone", "cobblestone", "brick", "stone_brick", "nether_brick", "quartz"],
            n => n == "Stone" ? "Stone Slab" : n + " Slab", w => w == "stone" ? "stone_slab" : w + "_slab", [0, 1, 3, 4, 5, 6, 7]);
        t.V(126, Building, "wooden_slab", Wood6, n => n + " Slab", w => w + "_slab");
        t.A(182, 0, Building, "stone_slab2", "Red Sandstone Slab", "red_sandstone_slab");
        t.A(205, 0, Building, "purpur_slab", "Purpur Slab");
        foreach (var (id, spec, name) in new[]
                 {
                     (53, "oak_stairs", "Oak Stairs"), (134, "spruce_stairs", "Spruce Stairs"), (135, "birch_stairs", "Birch Stairs"),
                     (136, "jungle_stairs", "Jungle Stairs"), (163, "acacia_stairs", "Acacia Stairs"), (164, "dark_oak_stairs", "Dark Oak Stairs"),
                     (67, "stone_stairs", "Cobblestone Stairs"), (108, "brick_stairs", "Brick Stairs"), (109, "stone_brick_stairs", "Stone Brick Stairs"),
                     (114, "nether_brick_stairs", "Nether Brick Stairs"), (128, "sandstone_stairs", "Sandstone Stairs"),
                     (180, "red_sandstone_stairs", "Red Sandstone Stairs"), (156, "quartz_stairs", "Quartz Stairs"), (203, "purpur_stairs", "Purpur Stairs"),
                 })
            t.A(id, 0, Building, spec, name);
        t.A(112, 0, Building, "nether_brick", "Nether Bricks");
        t.A(215, 0, Building, "red_nether_brick", "Red Nether Bricks");
        t.A(155, 0, Building, "quartz_block", "Block of Quartz");
        t.A(155, 1, Building, "quartz_block:chiseled", "Chiseled Quartz Block", "chiseled_quartz_block");
        t.A(155, 2, Building, "quartz_block:pillar", "Quartz Pillar", "quartz_column");
        t.A(168, 0, Building, "prismarine", "Prismarine");
        t.V(168, Building, "prismarine", ["bricks", "dark"], n => n == "Dark" ? "Dark Prismarine" : "Prismarine Bricks",
            w => w == "dark" ? "dark_prismarine" : "prismarine_bricks", [1, 2]);
        t.A(201, 0, Building, "purpur_block", "Purpur Block");
        t.A(202, 0, Building, "purpur_pillar", "Purpur Pillar");
        t.A(121, 0, Building, "end_stone", "End Stone");
        t.A(206, 0, Building, "end_bricks", "End Stone Bricks");
        t.A(49, 0, Building, "obsidian", "Obsidian");
        t.A(20, 0, Building, "glass", "Glass");
        t.A(102, 0, Building, "glass_pane", "Glass Pane");
        t.A(101, 0, Building, "iron_bars", "Iron Bars");
        foreach (var (id, spec, name) in new[]
                 {
                     (41, "gold_block", "Block of Gold"), (42, "iron_block", "Block of Iron"), (57, "diamond_block", "Block of Diamond"),
                     (133, "emerald_block", "Block of Emerald"), (22, "lapis_block", "Lapis Lazuli Block"), (173, "coal_block", "Block of Coal"),
                     (14, "gold_ore", "Gold Ore"), (15, "iron_ore", "Iron Ore"), (16, "coal_ore", "Coal Ore"), (56, "diamond_ore", "Diamond Ore"),
                     (21, "lapis_ore", "Lapis Lazuli Ore"), (73, "redstone_ore", "Redstone Ore"), (129, "emerald_ore", "Emerald Ore"),
                     (153, "quartz_ore", "Nether Quartz Ore"), (170, "hay_block", "Hay Bale"), (216, "bone_block", "Bone Block"),
                     (19, "sponge", "Sponge"),
                 })
            t.A(id, 0, Building, spec, name);
        t.A(19, 1, Building, "sponge:wet", "Wet Sponge", "sponge_wet");

        // Colored
        t.V(35, Colored, "wool", Colors, n => n + " Wool", w => w + "_wool");
        t.V(171, Colored, "carpet", Colors, n => n + " Carpet", w => w + "_carpet");
        t.V(95, Colored, "stained_glass", Colors, n => n + " Stained Glass", w => w + "_stained_glass");
        t.V(160, Colored, "stained_glass_pane", Colors, n => n + " Stained Glass Pane", w => w + "_stained_glass_pane");
        t.A(172, 0, Colored, "hardened_clay", "Terracotta");
        t.V(159, Colored, "stained_hardened_clay", Colors, n => n + " Terracotta", w => w + "_stained_hardened_clay");
        t.V(251, Colored, "concrete", Colors, n => n + " Concrete", w => w + "_concrete");
        t.V(252, Colored, "concrete_powder", Colors, n => n + " Concrete Powder", w => w + "_concrete_powder");
        for (int i = 0; i < 16; i++)
            t.A(235 + i, 0, Colored, Colors[i] + "_glazed_terracotta", Title(Colors[i]) + " Glazed Terracotta", Colors[i] + "_glazed_terracotta");
        for (int i = 0; i < 16; i++)
            t.A(219 + i, 1, Colored, Colors[i] + "_shulker_box", Title(Colors[i]) + " Shulker Box", Colors[i] + "_shulker_box");

        // Decoration
        foreach (var (id, spec, name) in new[]
                 {
                     (85, "fence", "Oak Fence"), (188, "spruce_fence", "Spruce Fence"), (189, "birch_fence", "Birch Fence"), (190, "jungle_fence", "Jungle Fence"),
                     (192, "acacia_fence", "Acacia Fence"), (191, "dark_oak_fence", "Dark Oak Fence"), (113, "nether_brick_fence", "Nether Brick Fence"),
                 })
            t.A(id, 0, Decoration, spec, name, id == 85 ? "oak_fence" : null);
        t.A(139, 0, Decoration, "cobblestone_wall", "Cobblestone Wall");
        t.A(139, 1, Decoration, "cobblestone_wall:mossy", "Mossy Cobblestone Wall", "mossy_cobblestone_wall");
        t.A(65, 2, Decoration, "ladder", "Ladder");
        t.A(50, 5, Decoration, "torch", "Torch");
        t.A(26, 0, Decoration, "bed", "Bed", "bed");
        t.A(63, 0, Decoration, "standing_sign", "Sign", "sign");
        t.A(68, 2, Decoration, "wall_sign", "Wall Sign", "sign");
        t.A(176, 0, Decoration, "standing_banner", "Banner", "banner");
        t.A(177, 2, Decoration, "wall_banner", "Wall Banner", "banner");
        t.A(144, 1, Decoration, "skull", "Skull", "skull_skeleton");
        t.A(140, 0, Decoration, "flower_pot", "Flower Pot", "flower_pot");
        t.A(54, 2, Decoration, "chest", "Chest");
        t.A(130, 2, Decoration, "ender_chest", "Ender Chest");
        t.A(58, 0, Decoration, "crafting_table", "Crafting Table");
        t.A(61, 2, Decoration, "furnace", "Furnace");
        t.A(116, 0, Decoration, "enchanting_table", "Enchanting Table");
        t.A(145, 0, Decoration, "anvil", "Anvil", "anvil_intact");
        t.A(145, 4, Decoration, "anvil:slightly_damaged", "Slightly Damaged Anvil", "anvil_slightly_damaged");
        t.A(145, 8, Decoration, "anvil:very_damaged", "Very Damaged Anvil", "anvil_very_damaged");
        t.A(117, 0, Decoration, "brewing_stand", "Brewing Stand", "brewing_stand");
        t.A(118, 0, Decoration, "cauldron", "Cauldron", "cauldron");
        t.A(84, 0, Decoration, "jukebox", "Jukebox");
        t.A(47, 0, Decoration, "bookshelf", "Bookshelf");
        t.A(89, 0, Decoration, "glowstone", "Glowstone");
        t.A(169, 0, Decoration, "sea_lantern", "Sea Lantern");
        t.A(91, 0, Decoration, "lit_pumpkin", "Jack o'Lantern");
        t.A(198, 1, Decoration, "end_rod", "End Rod");
        t.A(30, 0, Decoration, "web", "Cobweb");
        t.A(213, 0, Decoration, "magma", "Magma Block");
        t.A(214, 0, Decoration, "nether_wart_block", "Nether Wart Block");
        t.A(99, 14, Decoration, "brown_mushroom_block", "Brown Mushroom Block");
        t.A(100, 14, Decoration, "red_mushroom_block", "Red Mushroom Block");
        t.A(99, 15, Decoration, "brown_mushroom_block:stem", "Mushroom Stem");
        t.A(92, 0, Decoration, "cake", "Cake", "cake");

        // Nature
        t.A(2, 0, Nature, "grass", "Grass Block");
        t.A(3, 0, Nature, "dirt", "Dirt");
        t.V(3, Nature, "dirt", ["coarse_dirt", "podzol"], n => n, w => w, [1, 2]);
        t.A(110, 0, Nature, "mycelium", "Mycelium");
        t.A(208, 0, Nature, "grass_path", "Grass Path");
        t.A(60, 0, Nature, "farmland", "Farmland");
        t.A(12, 0, Nature, "sand", "Sand");
        t.A(12, 1, Nature, "sand:red", "Red Sand", "red_sand");
        t.A(13, 0, Nature, "gravel", "Gravel");
        t.A(82, 0, Nature, "clay", "Clay");
        t.A(88, 0, Nature, "soul_sand", "Soul Sand");
        t.A(87, 0, Nature, "netherrack", "Netherrack");
        t.A(80, 0, Nature, "snow", "Snow Block");
        t.A(78, 0, Nature, "snow_layer", "Snow Layer");
        t.A(79, 0, Nature, "ice", "Ice");
        t.A(174, 0, Nature, "packed_ice", "Packed Ice");
        t.V(6, Nature, "sapling", Wood6, n => n + " Sapling", w => w + "_sapling");
        t.V(18, Nature, "leaves", ["oak", "spruce", "birch", "jungle"], n => n + " Leaves", w => w + "_leaves", [4, 5, 6, 7]);
        t.V(161, Nature, "leaves2", ["acacia", "dark_oak"], n => n + " Leaves", w => w + "_leaves", [4, 5]);
        t.A(37, 0, Nature, "yellow_flower", "Dandelion", "dandelion");
        t.V(38, Nature, "red_flower", ["poppy", "blue_orchid", "allium", "houstonia", "red_tulip", "orange_tulip", "white_tulip", "pink_tulip", "oxeye_daisy"],
            n => n == "Houstonia" ? "Azure Bluet" : n, w => w);
        t.V(175, Nature, "double_plant", ["sunflower", "syringa", "double_grass", "double_fern", "double_rose", "paeonia"],
            n => n switch { "Syringa" => "Lilac", "Double Grass" => "Tall Grass (double)", "Double Fern" => "Large Fern", "Double Rose" => "Rose Bush", "Paeonia" => "Peony", _ => n },
            w => w);
        t.A(31, 1, Nature, "tallgrass:grass", "Grass", "tall_grass");
        t.A(31, 2, Nature, "tallgrass:fern", "Fern", "fern");
        t.A(32, 0, Nature, "deadbush", "Dead Bush");
        t.A(39, 0, Nature, "brown_mushroom", "Brown Mushroom");
        t.A(40, 0, Nature, "red_mushroom", "Red Mushroom");
        t.A(81, 0, Nature, "cactus", "Cactus");
        t.A(83, 0, Nature, "reeds", "Sugar Cane", "reeds");
        t.A(106, 1, Nature, "vine", "Vines", "vine");
        t.A(111, 0, Nature, "waterlily", "Lily Pad", "waterlily");
        t.A(127, 0, Nature, "cocoa", "Cocoa", "dye_brown");
        t.A(86, 0, Nature, "pumpkin", "Pumpkin");
        t.A(103, 0, Nature, "melon_block", "Melon");
        t.A(200, 0, Nature, "chorus_flower", "Chorus Flower");
        t.A(59, 0, Nature, "wheat", "Wheat Crops", "wheat_seeds");
        t.A(141, 0, Nature, "carrots", "Carrots", "carrot");
        t.A(142, 0, Nature, "potatoes", "Potatoes", "potato");
        t.A(207, 0, Nature, "beetroots", "Beetroots", "beetroot_seeds");
        t.A(115, 0, Nature, "nether_wart", "Nether Wart", "nether_wart");
        t.A(104, 0, Nature, "pumpkin_stem", "Pumpkin Stem", "pumpkin_seeds");
        t.A(105, 0, Nature, "melon_stem", "Melon Stem", "melon_seeds");

        // Redstone
        t.A(55, 0, Redstone, "redstone_wire", "Redstone Wire", "redstone");
        t.A(76, 5, Redstone, "redstone_torch", "Redstone Torch");
        t.A(93, 0, Redstone, "unpowered_repeater", "Redstone Repeater", "repeater");
        t.A(149, 0, Redstone, "unpowered_comparator", "Redstone Comparator", "comparator");
        t.A(152, 0, Redstone, "redstone_block", "Block of Redstone");
        t.A(123, 0, Redstone, "redstone_lamp", "Redstone Lamp");
        t.A(69, 5, Redstone, "lever", "Lever", "lever");
        t.A(77, 1, Redstone, "stone_button", "Stone Button", "stone_button");
        t.A(143, 1, Redstone, "wooden_button", "Wooden Button", "wooden_button");
        t.A(70, 0, Redstone, "stone_pressure_plate", "Stone Pressure Plate");
        t.A(72, 0, Redstone, "wooden_pressure_plate", "Wooden Pressure Plate");
        t.A(147, 0, Redstone, "light_weighted_pressure_plate", "Light Weighted Pressure Plate");
        t.A(148, 0, Redstone, "heavy_weighted_pressure_plate", "Heavy Weighted Pressure Plate");
        t.A(33, 1, Redstone, "piston", "Piston");
        t.A(29, 1, Redstone, "sticky_piston", "Sticky Piston");
        t.A(218, 3, Redstone, "observer", "Observer");
        t.A(23, 3, Redstone, "dispenser", "Dispenser");
        t.A(158, 3, Redstone, "dropper", "Dropper");
        t.A(154, 0, Redstone, "hopper", "Hopper", "hopper");
        t.A(151, 0, Redstone, "daylight_detector", "Daylight Sensor", "daylight_detector");
        t.A(178, 0, Redstone, "daylight_detector_inverted", "Inverted Daylight Sensor", "daylight_detector");
        t.A(131, 0, Redstone, "tripwire_hook", "Tripwire Hook");
        t.A(132, 0, Redstone, "tripwire", "Tripwire", "string");
        t.A(46, 0, Redstone, "tnt", "TNT");
        t.A(25, 0, Redstone, "noteblock", "Note Block");
        t.A(146, 2, Redstone, "trapped_chest", "Trapped Chest");
        t.A(165, 0, Redstone, "slime", "Slime Block");
        foreach (var (id, spec, name, icon) in new[]
                 {
                     (64, "wooden_door", "Oak Door", "oak_door"), (193, "spruce_door", "Spruce Door", "spruce_door"), (194, "birch_door", "Birch Door", "birch_door"),
                     (195, "jungle_door", "Jungle Door", "jungle_door"), (196, "acacia_door", "Acacia Door", "acacia_door"),
                     (197, "dark_oak_door", "Dark Oak Door", "dark_oak_door"), (71, "iron_door", "Iron Door", "iron_door"),
                 })
            t.A(id, 0, Redstone, spec, name, icon);
        t.A(96, 0, Redstone, "trapdoor", "Oak Trapdoor");
        t.A(167, 0, Redstone, "iron_trapdoor", "Iron Trapdoor");
        foreach (var (id, spec, name) in new[]
                 {
                     (107, "fence_gate", "Oak Fence Gate"), (183, "spruce_fence_gate", "Spruce Fence Gate"), (184, "birch_fence_gate", "Birch Fence Gate"),
                     (185, "jungle_fence_gate", "Jungle Fence Gate"), (187, "acacia_fence_gate", "Acacia Fence Gate"),
                     (186, "dark_oak_fence_gate", "Dark Oak Fence Gate"),
                 })
            t.A(id, 0, Redstone, spec, name, id == 107 ? "oak_fence_gate" : null);

        // Transport
        t.A(66, 0, Transport, "rail", "Rail");
        t.A(27, 0, Transport, "golden_rail", "Powered Rail");
        t.A(28, 0, Transport, "detector_rail", "Detector Rail");
        t.A(157, 0, Transport, "activator_rail", "Activator Rail");

        // Misc
        t.A(9, 0, Misc, "water", "Water (source)");
        t.A(11, 0, Misc, "lava", "Lava (source)");
        t.A(138, 0, Misc, "beacon", "Beacon");
        t.A(122, 0, Misc, "dragon_egg", "Dragon Egg");

        t.X("air is empty space ('.' / remove)", 0);
        t.X("bedrock can't be obtained", 7);
        t.X("flowing liquid: place the source (water / lava)", 8, 10);
        t.X("part of an extended or moving piston (place the piston)", 34, 36);
        t.X("fire isn't a block you place in a design", 51);
        t.X("spawners can't be obtained", 52);
        t.X("a runtime state (place the normal block)", 62, 74, 75, 94, 124, 150);
        t.X("portals are made, not placed", 90, 119, 209);
        t.X("end portal frames can't be obtained", 120);
        t.X("silverfish blocks can't be obtained", 97);
        t.X("a technical double slab: use the full block or two slabs", 125, 181, 204);
        t.X("command and structure blocks are operator-only", 137, 210, 211, 255, 217);
        t.X("barriers are operator-only", 166);
        t.X("frosted ice is made by Frost Walker, not placed", 212);
        t.X("chorus plants can't be obtained (place a chorus flower)", 199);
        return (t.Entries, t.Excluded);
    }

    private static (List<DesignBlock>, Dictionary<int, string>) Beta()
    {
        var t = new Table();
        // Building blocks
        t.A(1, 0, Building, "stone", "Stone");
        t.A(4, 0, Building, "cobblestone", "Cobblestone");
        t.A(48, 0, Building, "mossy_cobblestone", "Mossy Cobblestone");
        t.A(5, 0, Building, "planks", "Wooden Planks");
        t.V(17, Building, "log", ["oak", "spruce", "birch"], n => n + " Log");
        t.A(45, 0, Building, "brick_block", "Bricks");
        t.A(98, 0, Building, "stone_bricks", "Stone Bricks");
        t.V(98, Building, "stone_bricks", ["mossy", "cracked"], n => n + " Stone Bricks", null, [1, 2]);
        t.A(24, 0, Building, "sandstone", "Sandstone");
        t.A(43, 0, Building, "double_stone_slab", "Double Stone Slab");
        t.V(44, Building, "stone_slab", ["stone", "sandstone", "wood", "cobblestone", "brick", "stone_brick"],
            n => n switch { "Stone" => "Stone Slab", "Wood" => "Wooden Slab", _ => n + " Slab" });
        t.A(53, 0, Building, "oak_stairs", "Wooden Stairs");
        t.A(67, 0, Building, "stone_stairs", "Cobblestone Stairs");
        t.A(108, 0, Building, "brick_stairs", "Brick Stairs");
        t.A(109, 0, Building, "stone_brick_stairs", "Stone Brick Stairs");
        t.A(49, 0, Building, "obsidian", "Obsidian");
        t.A(20, 0, Building, "glass", "Glass");
        t.A(102, 0, Building, "glass_pane", "Glass Pane");
        t.A(101, 0, Building, "iron_bars", "Iron Bars");
        foreach (var (id, spec, name) in new[]
                 {
                     (41, "gold_block", "Block of Gold"), (42, "iron_block", "Block of Iron"), (57, "diamond_block", "Block of Diamond"),
                     (22, "lapis_block", "Lapis Lazuli Block"), (14, "gold_ore", "Gold Ore"), (15, "iron_ore", "Iron Ore"), (19, "sponge", "Sponge"),
                 })
            t.A(id, 0, Building, spec, name);

        // Colored
        t.V(35, Colored, "wool", Colors, n => n + " Wool");

        // Decoration
        t.A(85, 0, Decoration, "fence", "Fence");
        t.A(65, 2, Decoration, "ladder", "Ladder");
        t.A(50, 5, Decoration, "torch", "Torch");
        t.A(26, 0, Decoration, "bed", "Bed", "item:45");
        t.A(63, 0, Decoration, "standing_sign", "Sign", "item:42");
        t.A(68, 2, Decoration, "wall_sign", "Wall Sign", "item:42");
        t.A(54, 2, Decoration, "chest", "Chest");
        t.A(58, 0, Decoration, "crafting_table", "Crafting Table");
        t.A(61, 2, Decoration, "furnace", "Furnace");
        t.A(84, 0, Decoration, "jukebox", "Jukebox");
        t.A(47, 0, Decoration, "bookshelf", "Bookshelf");
        t.A(89, 0, Decoration, "glowstone", "Glowstone");
        t.A(91, 0, Decoration, "lit_pumpkin", "Jack o'Lantern");
        t.A(92, 0, Decoration, "cake", "Cake", "item:29");

        // Nature
        t.A(3, 0, Nature, "dirt", "Dirt");
        t.A(60, 0, Nature, "farmland", "Farmland");
        t.A(12, 0, Nature, "sand", "Sand");
        t.A(13, 0, Nature, "gravel", "Gravel");
        t.A(82, 0, Nature, "clay", "Clay");
        t.A(88, 0, Nature, "soul_sand", "Soul Sand");
        t.A(87, 0, Nature, "netherrack", "Netherrack");
        t.A(80, 0, Nature, "snow", "Snow Block");
        t.A(78, 0, Nature, "snow_layer", "Snow Layer");
        t.V(6, Nature, "sapling", ["oak", "spruce", "birch"], n => n + " Sapling");
        t.A(37, 0, Nature, "yellow_flower", "Dandelion");
        t.A(38, 0, Nature, "red_flower", "Rose");
        t.A(31, 1, Nature, "tallgrass:grass", "Tall Grass");
        t.A(31, 2, Nature, "tallgrass:fern", "Fern");
        t.A(32, 0, Nature, "deadbush", "Dead Bush");
        t.A(39, 0, Nature, "brown_mushroom", "Brown Mushroom");
        t.A(40, 0, Nature, "red_mushroom", "Red Mushroom");
        t.A(81, 0, Nature, "cactus", "Cactus");
        t.A(83, 0, Nature, "sugar_cane", "Sugar Cane", "item:27");
        t.A(106, 1, Nature, "vine", "Vines");
        t.A(86, 0, Nature, "pumpkin", "Pumpkin");
        t.A(103, 0, Nature, "melon_block", "Melon");
        t.A(59, 0, Nature, "wheat", "Wheat Crops", "item:9");
        t.A(104, 0, Nature, "pumpkin_stem", "Pumpkin Stem", "item:61");
        t.A(105, 0, Nature, "melon_stem", "Melon Stem", "item:62");

        // Redstone
        t.A(55, 0, Redstone, "redstone_wire", "Redstone Wire", "item:56");
        t.A(76, 5, Redstone, "redstone_torch", "Redstone Torch");
        t.A(93, 0, Redstone, "unpowered_repeater", "Redstone Repeater", "item:86");
        t.A(69, 5, Redstone, "lever", "Lever");
        t.A(77, 1, Redstone, "stone_button", "Button");
        t.A(70, 0, Redstone, "stone_pressure_plate", "Stone Pressure Plate");
        t.A(72, 0, Redstone, "wooden_pressure_plate", "Wooden Pressure Plate");
        t.A(33, 1, Redstone, "piston", "Piston");
        t.A(29, 1, Redstone, "sticky_piston", "Sticky Piston");
        t.A(23, 3, Redstone, "dispenser", "Dispenser");
        t.A(25, 0, Redstone, "noteblock", "Note Block");
        t.A(46, 0, Redstone, "tnt", "TNT");
        t.A(64, 0, Redstone, "wooden_door", "Wooden Door", "item:43");
        t.A(71, 0, Redstone, "iron_door", "Iron Door", "item:44");
        t.A(96, 0, Redstone, "trapdoor", "Trapdoor");
        t.A(107, 0, Redstone, "fence_gate", "Fence Gate");

        // Transport
        t.A(66, 0, Transport, "rail", "Rail");
        t.A(27, 0, Transport, "golden_rail", "Powered Rail");
        t.A(28, 0, Transport, "detector_rail", "Detector Rail");

        // Misc
        t.A(9, 0, Misc, "water", "Water (source)");
        t.A(11, 0, Misc, "lava", "Lava (source)");

        t.X("air is empty space ('.' / remove)", 0);
        t.X("bedrock can't be obtained", 7);
        t.X("flowing liquid: place the source (water / lava)", 8, 10);
        t.X("b1.8.1 has no silk touch: grass can't be picked up (place dirt: grass spreads onto it)", 2);
        t.X("placed leaves decay in b1.8.1", 18);
        t.X("b1.8.1 shears turn cobwebs into string", 30);
        t.X("part of an extended or moving piston (place the piston)", 34, 36);
        t.X("fire isn't a block you place in a design", 51);
        t.X("spawners can't be obtained", 52);
        t.X("b1.8.1 has no silk touch: this ore drops items, not itself", 16, 21, 56, 73);
        t.X("a runtime state (place the normal block)", 62, 74, 75, 94);
        t.X("b1.8.1 has no silk touch: ice can't be picked up", 79);
        t.X("portals are made, not placed", 90);
        t.X("the locked chest is a technical block", 95);
        t.X("silverfish blocks can't be obtained", 97);
        t.X("b1.8.1 has no silk touch: huge mushroom blocks drop mushrooms", 99, 100);
        return (t.Entries, t.Excluded);
    }
}
