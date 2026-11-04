using System.Text.Json;

namespace MineClaw.Data.Versioning;

/// <summary>
/// 1.12.2 crafting recipes (multi-version P5), from minecraft-data pc/1.12.2 <c>recipes.json</c> (vendored, embedded): the
/// 432 recipes of the client jar's <c>assets/minecraft/recipes</c>, one to one (a test checks them against the jar when it is
/// installed). minecraft-data writes an ingredient that lists every variant of one item (the jar's
/// <c>[{"item":"minecraft:planks","data":0} … {"data":5}]</c>, the ore-dictionary-like keys of crafting tables, sticks,
/// chests, beds, torches…) as the bare id: that is a wildcard here (any planks, coal or charcoal), as
/// <c>Ingredient.apply</c> accepts any of them and mixed stacks in one grid. A bare damageable item (the bow of a dispenser)
/// must be undamaged (<c>deserializeItem</c>'s data 0). Matching is <c>ShapedRecipes.checkMatch</c> (anywhere, mirrored) and
/// <c>ShapelessRecipes.matches</c>, the same as b1.8.1. Not included: <c>CraftingManager</c>'s code-only special recipes
/// (armor dyes, banners, fireworks, map cloning/extending, book cloning, tipped arrows, shulker box colouring, repair).
/// </summary>
internal sealed class Release1122Recipes
{
    public readonly Recipe[] All;
    private readonly ILookup<int, Recipe> _byOutput;
    private readonly HashSet<Recipe> _unpack = [];

    public Release1122Recipes(Release1122GameData d)
    {
        var list = new List<Recipe>();
        using (var doc = JsonDocument.Parse(Release1122GameData.Resource("mcdata.1.12.2.recipes.json")))
            foreach (var group in doc.RootElement.EnumerateObject())
                foreach (var r in group.Value.EnumerateArray())
                    list.Add(Parse(d, r));
        All = list.ToArray();
        // Variants in id order (oak planks before spruce), so a planner's tie picks the first variant.
        _byOutput = All.OrderBy(r => r.OutputDamage).ToLookup(r => r.OutputId);
        // An unpack (9 ingots ← 1 block, 9 nuggets ← 1 ingot, 9 slimeballs, wheat, bone meal…) is the reverse of a
        // 3x3 "nine of the output" recipe.
        foreach (var r in All)
            if (r.OutputCount == 9 && r.Ingredients.Count == 1)
            {
                var src = r.Ingredients[0];
                if (_byOutput[src.Id].Any(p => p.Ingredients.Count == 9 && p.Ingredients.All(i => i.Matches(r.OutputId, r.OutputDamage))))
                    _unpack.Add(r);
            }
    }

    public IReadOnlyList<Recipe> ForOutput(int id, int damage) => _byOutput[id].Where(r => damage < 0 || r.OutputDamage == damage).ToArray();

    public bool IsUnpack(Recipe r) => _unpack.Contains(r);

    private const string KeyChars = "#XYZABCDEFGHIJ";

    private static Recipe Parse(Release1122GameData d, JsonElement r)
    {
        var res = r.GetProperty("result");
        int outId = res.GetProperty("id").GetInt32();
        int outDmg = res.TryGetProperty("metadata", out var md) ? md.GetInt32() : 0;
        int count = res.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
        if (r.TryGetProperty("inShape", out var shape))
        {
            var cells = shape.EnumerateArray().Select(row => row.EnumerateArray().Select(x => Cell(d, x)).ToList()).ToList();
            int w = cells.Max(row => row.Count);
            var keys = new List<(char, Ingredient)>();
            var rows = new string[cells.Count];
            for (int y = 0; y < cells.Count; y++)
            {
                var chars = new char[w];
                for (int x = 0; x < w; x++)
                {
                    var ing = x < cells[y].Count ? cells[y][x] : null;
                    if (ing is not { } i) { chars[x] = ' '; continue; }
                    int k = keys.FindIndex(e => e.Item2 == i);
                    if (k < 0) { keys.Add((KeyChars[keys.Count], i)); k = keys.Count - 1; }
                    chars[x] = keys[k].Item1;
                }
                rows[y] = new string(chars);
            }
            return Recipes.Shaped(outId, count, outDmg, rows, keys.ToArray());
        }
        var inputs = r.GetProperty("ingredients").EnumerateArray().Select(x => Cell(d, x) ?? throw new InvalidDataException("empty shapeless ingredient")).ToArray();
        return Recipes.Shapeless(outId, count, outDmg, inputs);
    }

    private static Ingredient? Cell(Release1122GameData d, JsonElement x)
    {
        switch (x.ValueKind)
        {
            case JsonValueKind.Null: return null;
            case JsonValueKind.Number:
            {
                int id = x.GetInt32();
                return new Ingredient(id, d.McItemOf(id) is { MaxDurability: > 0 } ? 0 : -1);
            }
            default:
                int iid = x.GetProperty("id").GetInt32();
                return new Ingredient(iid, x.TryGetProperty("metadata", out var m) ? m.GetInt32() : -1);
        }
    }
}

/// <summary>
/// 1.12.2 furnace data (P5): the recipes of <c>FurnaceRecipes</c>' constructor, kept in
/// <c>shards/sources/1.12.2/smelting.json</c> (written from the decompiled source; the knowledge pack reads the same file, so
/// the bot's tables and its knowledge never disagree), and <c>TileEntityFurnace.getItemBurnTime</c> ported below.
/// </summary>
internal sealed class Release1122Smelting
{
    public readonly SmeltingRecipe[] All;
    public readonly int CookTicks;

    public Release1122Smelting(Release1122GameData d)
    {
        var list = new List<SmeltingRecipe>();
        using var doc = JsonDocument.Parse(Release1122GameData.Resource("facts.1.12.2.smelting.json"));
        CookTicks = doc.RootElement.TryGetProperty("cook_ticks", out var ct) ? ct.GetInt32() : 200;
        foreach (var e in doc.RootElement.GetProperty("smelting").EnumerateArray())
        {
            int Id(string prop) => d.IdOf(e.GetProperty(prop).GetString()!) ?? throw new InvalidDataException($"smelting.json: unknown {prop} in {e.GetRawText()}");
            static int Meta(JsonElement m) => m.ValueKind == JsonValueKind.Number ? m.GetInt32() : -1;
            list.Add(new SmeltingRecipe(Id("input"), Id("output"), Math.Max(0, Meta(e.GetProperty("output_meta"))),
                e.TryGetProperty("count", out var n) ? n.GetInt32() : 1)
            {
                InputDamage = Meta(e.GetProperty("input_meta")),
                Experience = e.TryGetProperty("xp", out var xp) ? xp.GetSingle() : 0f,
            });
        }
        All = list.ToArray();
    }

    /// <summary><c>getSmeltingResult</c>: the first entry whose input matches (metadata 32767 = any); an unknown damage (−1)
    /// takes the first recipe of the item.</summary>
    public SmeltingRecipe? For(int id, int damage) => All.FirstOrDefault(r => r.MatchesInput(id, damage));

    /// <summary>The fuels <c>getItemBurnTime</c> names before its wood-material rule (wooden blocks that burn shorter).</summary>
    private static readonly Dictionary<string, int> FuelBeforeWood = new()
    {
        ["wooden_slab"] = 150, ["wool"] = 100, ["carpet"] = 67, ["ladder"] = 300, ["wooden_button"] = 100,
    };

    /// <summary>The fuels after the wood-material rule.</summary>
    private static readonly Dictionary<string, int> FuelAfterWood = new()
    {
        ["coal_block"] = 16000,
        ["wooden_pickaxe"] = 200, ["wooden_axe"] = 200, ["wooden_shovel"] = 200, ["wooden_sword"] = 200, ["wooden_hoe"] = 200,
        ["stick"] = 100, ["bow"] = 300, ["fishing_rod"] = 300, ["sign"] = 200, ["coal"] = 1600, ["lava_bucket"] = 20000,
        ["sapling"] = 100, ["bowl"] = 100, ["blaze_rod"] = 2400,
        ["wooden_door"] = 200, ["spruce_door"] = 200, ["birch_door"] = 200, ["jungle_door"] = 200, ["acacia_door"] = 200, ["dark_oak_door"] = 200,
        ["boat"] = 400, ["spruce_boat"] = 400, ["birch_boat"] = 400, ["jungle_boat"] = 400, ["acacia_boat"] = 400, ["dark_oak_boat"] = 400,
    };

    /// <summary><c>TileEntityFurnace.getItemBurnTime</c>, in its order: wooden slab 150, wool 100, carpet 67, ladder 300, wooden
    /// button 100, any other block item of wood material 300, coal block 16000, wooden tools/swords/hoes 200, stick 100,
    /// bow/fishing rod 300, sign 200, coal/charcoal 1600, lava bucket 20000, sapling/bowl 100, blaze rod 2400, wooden doors
    /// 200, boats 400.</summary>
    public static int FuelTicks(Release1122GameData d, int itemId)
    {
        if (itemId <= 0 || d.NameOf(itemId) is not { } n) return 0;
        if (FuelBeforeWood.TryGetValue(n, out int ticks)) return ticks;
        // Block.getBlockFromItem(item).getDefaultState().getMaterial() == WOOD: block items, and the banner (an ItemBlock of the
        // standing banner past id 255).
        if (itemId < 256 && d.McBlockOf(itemId) is { Category: "wood" } || n == "banner") return 300;
        return FuelAfterWood.GetValueOrDefault(n);
    }
}

/// <summary>
/// 1.12.2 block drops (P5), ported from the decompiled <c>Block*.getItemDropped</c> / <c>quantityDropped</c> /
/// <c>damageDropped</c> / <c>getDrops</c> / <c>dropBlockAsItemWithChance</c> / <c>harvestBlock</c> overrides (fortune and silk
/// touch ignored; shears count where <c>harvestBlock</c> checks them). minecraft-data's <c>drops</c> field is not used: it
/// is wrong for several blocks (reeds, doors, huge brown mushrooms). Random counts are ranges (Min..Max); one-in-n drops
/// carry their chance. The overrides are a table keyed by block name (<see cref="Rules"/>); a block without one drops its own
/// item (<c>Block.getItemDropped</c>, damageDropped 0) when it has one.
/// </summary>
internal static class Release1122Drops
{
    /// <summary>One break: the block, its meta and whether the held item is shears, with the drop builders the rules share.</summary>
    private readonly record struct Ctx(Release1122GameData D, int BlockId, string Name, int Meta, bool Shears)
    {
        public int I(string name) => D.IdOrMinus(name);
        public static Drop One(int id, int dmg = 0) => new(id, dmg, 1, 1);
        public Drop[] Self(int dmg = 0) => [One(BlockId, dmg)];
        public Drop[] Item(string name, int dmg = 0) => [One(I(name), dmg)];
        public Drop[] Range(string name, int dmg, int min, int max) => [new(I(name), dmg, min, max)];
        public Drop Chance(string name, int dmg, float chance) => new(I(name), dmg, 1, 1, chance);
    }

    /// <summary>The overrides by block name; a block can have only one.</summary>
    private sealed class RuleTable : Dictionary<string, Func<Ctx, IReadOnlyList<Drop>>>
    {
        public void Each(Func<Ctx, IReadOnlyList<Drop>> rule, params string[] names)
        {
            foreach (var n in names) Add(n, rule);
        }

        public void ItemOf(string item, params string[] names) => Each(c => c.Item(item), names);
        public void RangeOf(string item, int dmg, int min, int max, params string[] names) => Each(c => c.Range(item, dmg, min, max), names);
    }

    private static readonly RuleTable Rules = BuildRules();

    public static IReadOnlyList<Drop> For(Release1122GameData d, int blockId, int meta, int held)
    {
        if (blockId <= 0 || d.McBlockOf(blockId) is not { } block) return [];
        if (!d.CanHarvest(blockId, held)) return [];
        var c = new Ctx(d, blockId, block.Name, meta, held > 0 && d.NameOf(held) == "shears");
        if (Rules.TryGetValue(block.Name, out var rule)) return rule(c);
        // Block.getItemDropped: the block's own item (damageDropped 0), when it has one.
        return d.ItemExists(blockId) ? c.Self() : [];
    }

    private static RuleTable BuildRules()
    {
        var r = new RuleTable();

        // Nothing (quantityDropped 0, Items.AIR, BlockEmptyDrops, liquids, silk-touch-only blocks).
        r.Each(_ => [], "flowing_water", "water", "flowing_lava", "lava", "fire", "portal", "end_portal", "end_gateway", "piston_head",
            "piston_extension", "mob_spawner", "barrier", "command_block", "repeating_command_block", "chain_command_block",
            "structure_block", "structure_void", "glass", "stained_glass", "glass_pane", "stained_glass_pane", "ice", "packed_ice",
            "frosted_ice", "monster_egg", "cake", "chorus_flower", "end_portal_frame", "bedrock");

        r.Each(c => (c.Meta & 7) == 0 ? c.Item("cobblestone") : c.Self(c.Meta & 7), "stone");
        r.ItemOf("dirt", "grass", "mycelium", "grass_path", "farmland");
        r.Each(c => c.Self((c.Meta & 3) == 2 ? 0 : c.Meta & 3), "dirt"); // podzol drops dirt
        r.Each(c => [new(c.BlockId, 0, 1, 1, 0.9f), c.Chance("flint", 0, 0.1f)], "gravel");
        r.ItemOf("coal", "coal_ore");
        r.ItemOf("diamond", "diamond_ore");
        r.ItemOf("emerald", "emerald_ore");
        r.ItemOf("quartz", "quartz_ore");
        r.RangeOf("dye", 4, 4, 8, "lapis_ore");
        r.RangeOf("redstone", 0, 4, 5, "redstone_ore", "lit_redstone_ore");
        r.RangeOf("glowstone_dust", 0, 2, 4, "glowstone");
        r.RangeOf("prismarine_crystals", 0, 2, 3, "sea_lantern");
        r.RangeOf("melon", 0, 3, 7, "melon_block");
        r.RangeOf("book", 0, 3, 3, "bookshelf");
        r.RangeOf("clay_ball", 0, 4, 4, "clay");
        r.RangeOf("snowball", 0, 4, 4, "snow");
        r.Each(c => c.Range("snowball", 0, (c.Meta & 7) + 2, (c.Meta & 7) + 2), "snow_layer"); // BlockSnow.harvestBlock: layers + 1
        r.RangeOf("obsidian", 0, 8, 8, "ender_chest");
        r.RangeOf("brown_mushroom", 0, 0, 2, "brown_mushroom_block");
        r.RangeOf("red_mushroom", 0, 0, 2, "red_mushroom_block");
        r.RangeOf("chorus_fruit", 0, 0, 1, "chorus_plant");

        AddPlants(r);
        AddVariants(r);
        AddItemForms(r);
        AddCrops(r);
        return r;
    }

    private static void AddPlants(RuleTable r)
    {
        r.Each(c => c.Self(c.Meta & 3), ["log", "log2"]);
        r.Each(c => Leaves(c, saplingDamage: c.Meta & 3, appleVariant: 0, rareVariant: 3), ["leaves"]);
        r.Each(c => Leaves(c, saplingDamage: (c.Meta & 3) + 4, appleVariant: 1, rareVariant: -1), ["leaves2"]);
        r.Each(c => c.Shears ? c.Self(c.Meta & 3) : [c.Chance("wheat_seeds", 0, 1f / 8f)], ["tallgrass"]);
        r.Each(c => c.Shears ? c.Self() : c.Range("stick", 0, 0, 2), ["deadbush"]);
        r.Each(c => c.Shears ? c.Self() : c.Item("string"), ["web"]);
        r.Each(c => c.Shears ? c.Self() : [], ["vine"]);
        r.Each(DoublePlant, ["double_plant"]);
    }

    /// <summary>Leaves: themselves with shears, else a 1-in-20 sapling (1-in-40 for jungle) and, for oak / dark oak, a 1-in-200 apple.</summary>
    private static IReadOnlyList<Drop> Leaves(Ctx c, int saplingDamage, int appleVariant, int rareVariant)
    {
        int v = c.Meta & 3;
        if (c.Shears) return c.Self(v);
        var l = new List<Drop> { c.Chance("sapling", saplingDamage, v == rareVariant ? 1f / 40f : 1f / 20f) };
        if (v == appleVariant) l.Add(c.Chance("apple", 0, 1f / 200f));
        return l;
    }

    /// <summary>The upper half drops nothing; the lower half drops the plant (tall grass and large fern: seeds, or two of the
    /// small plant with shears).</summary>
    private static IReadOnlyList<Drop> DoublePlant(Ctx c)
    {
        if ((c.Meta & 8) != 0) return [];
        int v = c.Meta & 7;
        if (v == 2) return c.Shears ? c.Range("tallgrass", 1, 2, 2) : [c.Chance("wheat_seeds", 0, 1f / 8f)];
        if (v == 3) return c.Shears ? c.Range("tallgrass", 2, 2, 2) : [];
        return c.Self(v);
    }

    private static void AddVariants(RuleTable r)
    {
        r.Each(c => c.Self(c.Meta & 15), ["planks", "sand", "sandstone", "red_sandstone", "stonebrick", "prismarine", "wool", "carpet",
            "stained_hardened_clay", "concrete", "concrete_powder", "cobblestone_wall", "sponge", "red_flower"]);
        r.Each(c => c.Self(c.Meta & 7), ["sapling", "stone_slab", "wooden_slab"]);
        r.Each(c => c.Self((c.Meta & 7) is 3 or 4 ? 2 : c.Meta & 7), ["quartz_block"]); // pillars drop the upright pillar
        r.Each(c => c.Self(c.Meta >> 2), ["anvil"]);
        r.Each(c => c.Range("stone_slab", c.Meta & 7, 2, 2), ["double_stone_slab"]);
        r.Each(c => c.Range("wooden_slab", c.Meta & 7, 2, 2), ["double_wooden_slab"]);
        r.Each(c => c.Self(0), ["stone_slab2"]);
        r.RangeOf("stone_slab2", 0, 2, 2, ["double_stone_slab2"]);
        r.RangeOf("purpur_slab", 0, 2, 2, ["purpur_double_slab"]);
    }

    /// <summary>The item form differs from the block.</summary>
    private static void AddItemForms(RuleTable r)
    {
        // The head half drops the bed; colour from the tile entity (default red).
        r.Each(c => (c.Meta & 8) != 0 ? [Ctx.One(c.I("bed"), 14)] : [], ["bed"]);
        r.Each(c => (c.Meta & 8) != 0 ? [] : c.Item(c.Name),
            ["wooden_door", "iron_door", "spruce_door", "birch_door", "jungle_door", "acacia_door", "dark_oak_door"]);
        r.ItemOf("sign", ["standing_sign", "wall_sign"]);
        r.ItemOf("banner", ["standing_banner", "wall_banner"]);
        r.ItemOf("redstone", ["redstone_wire"]);
        r.ItemOf("redstone_torch", ["unlit_redstone_torch", "redstone_torch"]);
        r.ItemOf("repeater", ["unpowered_repeater", "powered_repeater"]);
        r.ItemOf("comparator", ["unpowered_comparator", "powered_comparator"]);
        r.ItemOf("redstone_lamp", ["lit_redstone_lamp"]);
        r.ItemOf("daylight_detector", ["daylight_detector_inverted"]);
        r.ItemOf("furnace", ["lit_furnace"]);
        r.ItemOf("reeds", "reeds");
        r.ItemOf("brewing_stand", "brewing_stand");
        r.ItemOf("cauldron", "cauldron");
        r.ItemOf("flower_pot", "flower_pot");
        r.ItemOf("skull", "skull");
        r.ItemOf("string", ["tripwire"]);
        r.Each(c => (c.Meta & 3) >= 3 ? c.Range("nether_wart", 0, 2, 4) : c.Item("nether_wart"), ["nether_wart"]);
        r.Each(c => (c.Meta >> 2) >= 2 ? c.Range("dye", 3, 3, 3) : c.Item("dye", 3), ["cocoa"]);
    }

    /// <summary>Crops: the crop when ripe plus 3 tries at a seed (rand(2·maxAge) ≤ age), else one seed.</summary>
    private static void AddCrops(RuleTable r)
    {
        r.Each(c => (c.Meta & 7) >= 7 ? [Ctx.One(c.I("wheat")), new(c.I("wheat_seeds"), 0, 0, 3)] : c.Item("wheat_seeds"), ["wheat"]);
        r.Each(c => (c.Meta & 7) >= 7 ? c.Range("carrot", 0, 1, 4) : c.Item("carrot"), ["carrots"]);
        r.Each(c => (c.Meta & 7) >= 7 ? [new(c.I("potato"), 0, 1, 4), c.Chance("poisonous_potato", 0, 1f / 50f)] : c.Item("potato"), ["potatoes"]);
        r.Each(c => (c.Meta & 3) >= 3 ? [Ctx.One(c.I("beetroot")), new(c.I("beetroot_seeds"), 0, 0, 3)] : c.Item("beetroot_seeds"), ["beetroots"]);
        r.RangeOf("pumpkin_seeds", 0, 0, 3, ["pumpkin_stem"]);
        r.RangeOf("melon_seeds", 0, 0, 3, ["melon_stem"]);
    }
}

/// <summary>1.12.2 food values (P5), from the <c>ItemFood</c> registrations in <c>Item.registerItems</c> (heal amount,
/// saturation modifier; <c>ItemSoup</c> = 0.6; <c>ItemFishFood.FishType</c> per damage). minecraft-data's foods.json is not
/// used: it lists the crop blocks (melon_block, carrots, potatoes) instead of the items and a saturation ratio, not the modifier.</summary>
internal static class Release1122Foods
{
    /// <summary>The foods whose value doesn't depend on the damage (all but the fish types).</summary>
    private static readonly Dictionary<string, (int Hunger, float Saturation)> Plain = new()
    {
        ["apple"] = (4, 0.3f), ["mushroom_stew"] = (6, 0.6f), ["bread"] = (5, 0.6f), ["porkchop"] = (3, 0.3f), ["cooked_porkchop"] = (8, 0.8f),
        ["golden_apple"] = (4, 1.2f),
        ["cookie"] = (2, 0.1f), ["melon"] = (2, 0.3f), ["beef"] = (3, 0.3f), ["cooked_beef"] = (8, 0.8f), ["chicken"] = (2, 0.3f),
        ["cooked_chicken"] = (6, 0.6f), ["rotten_flesh"] = (4, 0.1f), ["spider_eye"] = (2, 0.8f), ["carrot"] = (3, 0.6f),
        ["potato"] = (1, 0.3f), ["baked_potato"] = (5, 0.6f), ["poisonous_potato"] = (2, 0.3f), ["golden_carrot"] = (6, 1.2f),
        ["pumpkin_pie"] = (8, 0.3f), ["rabbit"] = (3, 0.3f), ["cooked_rabbit"] = (5, 0.6f), ["rabbit_stew"] = (10, 0.6f),
        ["mutton"] = (2, 0.3f), ["cooked_mutton"] = (6, 0.8f), ["chorus_fruit"] = (4, 0.3f), ["beetroot"] = (1, 0.6f),
        ["beetroot_soup"] = (6, 0.6f),
    };

    public static (int Hunger, float Saturation)? Food(string? name, int damage) => name switch
    {
        null => null,
        "fish" => (damage & 3) switch { 0 or 1 => (2, 0.1f), _ => (1, 0.1f) }, // cod, salmon / clownfish, pufferfish
        "cooked_fish" => (damage & 3) switch { 0 => (5, 0.6f), 1 => (6, 0.8f), _ => null },
        _ => Plain.TryGetValue(name, out var food) ? food : null,
    };

    /// <summary>The <c>setPotionEffect</c> chances (hunger: raw chicken 0.3, rotten flesh 0.8; poison: spider eye 1.0, poisonous
    /// potato 0.6) and the pufferfish (<c>ItemFishFood.onFoodEaten</c>: poison, hunger and nausea, always).</summary>
    public static float PoisonChance(string? name, int damage) => name switch
    {
        "chicken" => 0.3f, "rotten_flesh" => 0.8f, "spider_eye" => 1f, "poisonous_potato" => 0.6f,
        "fish" when (damage & 3) == 3 => 1f,
        _ => 0f,
    };
}
