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

    private static readonly HashSet<string> WoodDoors = ["wooden_door", "spruce_door", "birch_door", "jungle_door", "acacia_door", "dark_oak_door"];
    private static readonly HashSet<string> Boats = ["boat", "spruce_boat", "birch_boat", "jungle_boat", "acacia_boat", "dark_oak_boat"];

    /// <summary><c>TileEntityFurnace.getItemBurnTime</c>, in its order: wooden slab 150, wool 100, carpet 67, ladder 300, wooden
    /// button 100, any other block item of wood material 300, coal block 16000, wooden tools/swords/hoes 200, stick 100,
    /// bow/fishing rod 300, sign 200, coal/charcoal 1600, lava bucket 20000, sapling/bowl 100, blaze rod 2400, wooden doors
    /// 200, boats 400.</summary>
    public static int FuelTicks(Release1122GameData d, int itemId)
    {
        if (itemId <= 0 || d.NameOf(itemId) is not { } n) return 0;
        switch (n)
        {
            case "wooden_slab": return 150;
            case "wool": return 100;
            case "carpet": return 67;
            case "ladder": return 300;
            case "wooden_button": return 100;
        }
        // Block.getBlockFromItem(item).getDefaultState().getMaterial() == WOOD: block items, and the banner (an ItemBlock of the
        // standing banner past id 255).
        if (itemId < 256 && d.McBlockOf(itemId) is { Category: "wood" } || n == "banner") return 300;
        if (n == "coal_block") return 16000;
        if (n is "wooden_pickaxe" or "wooden_axe" or "wooden_shovel" or "wooden_sword" or "wooden_hoe") return 200;
        return n switch
        {
            "stick" => 100,
            "bow" or "fishing_rod" => 300,
            "sign" => 200,
            "coal" => 1600,
            "lava_bucket" => 20000,
            "sapling" or "bowl" => 100,
            "blaze_rod" => 2400,
            _ when WoodDoors.Contains(n) => 200,
            _ when Boats.Contains(n) => 400,
            _ => 0,
        };
    }
}

/// <summary>
/// 1.12.2 block drops (P5), ported from the decompiled <c>Block*.getItemDropped</c> / <c>quantityDropped</c> /
/// <c>damageDropped</c> / <c>getDrops</c> / <c>dropBlockAsItemWithChance</c> / <c>harvestBlock</c> overrides (fortune and silk
/// touch ignored; shears count where <c>harvestBlock</c> checks them). minecraft-data's <c>drops</c> field is not used: it
/// is wrong for several blocks (reeds, doors, huge brown mushrooms). Random counts are ranges (Min..Max); one-in-n drops
/// carry their chance.
/// </summary>
internal static class Release1122Drops
{
    public static IReadOnlyList<Drop> For(Release1122GameData d, int blockId, int meta, int held)
    {
        if (blockId <= 0 || d.McBlockOf(blockId) is not { } block) return [];
        if (!d.CanHarvest(blockId, held)) return [];
        int I(string name) => d.IdOrMinus(name);
        bool shears = held > 0 && d.NameOf(held) == "shears";
        Drop One(int id, int dmg = 0) => new(id, dmg, 1, 1);
        Drop[] Self(int dmg = 0) => [One(blockId, dmg)];
        Drop[] Item(string name, int dmg = 0) => [One(I(name), dmg)];

        switch (block.Name)
        {
            // Nothing (quantityDropped 0, Items.AIR, BlockEmptyDrops, liquids, silk-touch-only blocks).
            case "flowing_water": case "water": case "flowing_lava": case "lava": case "fire": case "portal": case "end_portal":
            case "end_gateway": case "piston_head": case "piston_extension": case "mob_spawner": case "barrier": case "command_block":
            case "repeating_command_block": case "chain_command_block": case "structure_block": case "structure_void":
            case "glass": case "stained_glass": case "glass_pane": case "stained_glass_pane": case "ice": case "packed_ice":
            case "frosted_ice": case "monster_egg": case "cake": case "chorus_flower": case "end_portal_frame": case "bedrock":
                return [];

            case "stone": return (meta & 7) == 0 ? Item("cobblestone") : Self(meta & 7);
            case "grass": case "mycelium": case "grass_path": case "farmland": return Item("dirt");
            case "dirt": return Self((meta & 3) == 2 ? 0 : meta & 3); // podzol drops dirt
            case "gravel": return [new(blockId, 0, 1, 1, 0.9f), new(I("flint"), 0, 1, 1, 0.1f)];
            case "coal_ore": return Item("coal");
            case "diamond_ore": return Item("diamond");
            case "emerald_ore": return Item("emerald");
            case "quartz_ore": return Item("quartz");
            case "lapis_ore": return [new(I("dye"), 4, 4, 8)];
            case "redstone_ore": case "lit_redstone_ore": return [new(I("redstone"), 0, 4, 5)];
            case "glowstone": return [new(I("glowstone_dust"), 0, 2, 4)];
            case "sea_lantern": return [new(I("prismarine_crystals"), 0, 2, 3)];
            case "melon_block": return [new(I("melon"), 0, 3, 7)];
            case "bookshelf": return [new(I("book"), 0, 3, 3)];
            case "clay": return [new(I("clay_ball"), 0, 4, 4)];
            case "snow": return [new(I("snowball"), 0, 4, 4)];
            case "snow_layer": { int n = (meta & 7) + 2; return [new(I("snowball"), 0, n, n)]; } // BlockSnow.harvestBlock: layers + 1
            case "ender_chest": return [new(I("obsidian"), 0, 8, 8)];
            case "brown_mushroom_block": return [new(I("brown_mushroom"), 0, 0, 2)];
            case "red_mushroom_block": return [new(I("red_mushroom"), 0, 0, 2)];
            case "chorus_plant": return [new(I("chorus_fruit"), 0, 0, 1)];

            case "log": case "log2": return Self(meta & 3);
            case "leaves":
            {
                int v = meta & 3;
                if (shears) return Self(v);
                var l = new List<Drop> { new(I("sapling"), v, 1, 1, v == 3 ? 1f / 40f : 1f / 20f) };
                if (v == 0) l.Add(new(I("apple"), 0, 1, 1, 1f / 200f));
                return l;
            }
            case "leaves2":
            {
                int v = meta & 3;
                if (shears) return Self(v);
                var l = new List<Drop> { new(I("sapling"), v + 4, 1, 1, 1f / 20f) };
                if (v == 1) l.Add(new(I("apple"), 0, 1, 1, 1f / 200f));
                return l;
            }
            case "tallgrass": return shears ? Self(meta & 3) : [new(I("wheat_seeds"), 0, 1, 1, 1f / 8f)];
            case "deadbush": return shears ? Self() : [new(I("stick"), 0, 0, 2)];
            case "web": return shears ? Self() : Item("string");
            case "vine": return shears ? Self() : [];
            case "double_plant":
            {
                if ((meta & 8) != 0) return []; // the upper half drops nothing; the lower half drops the plant
                int v = meta & 7;
                if (v == 2) return shears ? [new(I("tallgrass"), 1, 2, 2)] : [new(I("wheat_seeds"), 0, 1, 1, 1f / 8f)];
                if (v == 3) return shears ? [new(I("tallgrass"), 2, 2, 2)] : [];
                return Self(v);
            }

            case "planks": case "sand": case "sandstone": case "red_sandstone": case "stonebrick": case "prismarine": case "wool":
            case "carpet": case "stained_hardened_clay": case "concrete": case "concrete_powder": case "cobblestone_wall": case "sponge":
            case "red_flower":
                return Self(meta & 15);
            case "sapling": return Self(meta & 7);
            case "quartz_block": return Self((meta & 7) is 3 or 4 ? 2 : meta & 7); // pillars drop the upright pillar
            case "anvil": return Self(meta >> 2);
            case "stone_slab": case "wooden_slab": return Self(meta & 7);
            case "double_stone_slab": return [new(I("stone_slab"), meta & 7, 2, 2)];
            case "double_wooden_slab": return [new(I("wooden_slab"), meta & 7, 2, 2)];
            case "stone_slab2": return Self(0);
            case "double_stone_slab2": return [new(I("stone_slab2"), 0, 2, 2)];
            case "purpur_double_slab": return [new(I("purpur_slab"), 0, 2, 2)];

            // The item form differs from the block.
            case "bed": return (meta & 8) != 0 ? [One(I("bed"), 14)] : []; // the head half drops it; colour from the tile entity (default red)
            case "wooden_door": case "iron_door": case "spruce_door": case "birch_door": case "jungle_door": case "acacia_door":
            case "dark_oak_door":
                return (meta & 8) != 0 ? [] : Item(block.Name);
            case "standing_sign": case "wall_sign": return Item("sign");
            case "standing_banner": case "wall_banner": return Item("banner");
            case "redstone_wire": return Item("redstone");
            case "unlit_redstone_torch": case "redstone_torch": return Item("redstone_torch");
            case "unpowered_repeater": case "powered_repeater": return Item("repeater");
            case "unpowered_comparator": case "powered_comparator": return Item("comparator");
            case "lit_redstone_lamp": return Item("redstone_lamp");
            case "daylight_detector_inverted": return Item("daylight_detector");
            case "lit_furnace": return Item("furnace");
            case "reeds": return Item("reeds");
            case "brewing_stand": return Item("brewing_stand");
            case "cauldron": return Item("cauldron");
            case "flower_pot": return Item("flower_pot");
            case "skull": return Item("skull");
            case "tripwire": return Item("string");
            case "nether_wart": return (meta & 3) >= 3 ? [new(I("nether_wart"), 0, 2, 4)] : Item("nether_wart");
            case "cocoa": return (meta >> 2) >= 2 ? [new(I("dye"), 3, 3, 3)] : Item("dye", 3);

            // Crops: the crop when ripe plus 3 tries at a seed (rand(2·maxAge) ≤ age), else one seed.
            case "wheat": return (meta & 7) >= 7 ? [One(I("wheat")), new(I("wheat_seeds"), 0, 0, 3)] : Item("wheat_seeds");
            case "carrots": return (meta & 7) >= 7 ? [new(I("carrot"), 0, 1, 4)] : Item("carrot");
            case "potatoes":
                return (meta & 7) >= 7 ? [new(I("potato"), 0, 1, 4), new(I("poisonous_potato"), 0, 1, 1, 1f / 50f)] : Item("potato");
            case "beetroots": return (meta & 3) >= 3 ? [One(I("beetroot")), new(I("beetroot_seeds"), 0, 0, 3)] : Item("beetroot_seeds");
            case "pumpkin_stem": return [new(I("pumpkin_seeds"), 0, 0, 3)];
            case "melon_stem": return [new(I("melon_seeds"), 0, 0, 3)];
        }
        // Block.getItemDropped: the block's own item (damageDropped 0), when it has one.
        return d.ItemExists(blockId) ? Self() : [];
    }
}

/// <summary>1.12.2 food values (P5), from the <c>ItemFood</c> registrations in <c>Item.registerItems</c> (heal amount,
/// saturation modifier; <c>ItemSoup</c> = 0.6; <c>ItemFishFood.FishType</c> per damage). minecraft-data's foods.json is not
/// used: it lists the crop blocks (melon_block, carrots, potatoes) instead of the items and a saturation ratio, not the modifier.</summary>
internal static class Release1122Foods
{
    public static (int Hunger, float Saturation)? Food(string? name, int damage) => name switch
    {
        "apple" => (4, 0.3f), "mushroom_stew" => (6, 0.6f), "bread" => (5, 0.6f), "porkchop" => (3, 0.3f), "cooked_porkchop" => (8, 0.8f),
        "golden_apple" => (4, 1.2f),
        "fish" => (damage & 3) switch { 0 or 1 => (2, 0.1f), _ => (1, 0.1f) }, // cod, salmon / clownfish, pufferfish
        "cooked_fish" => (damage & 3) switch { 0 => (5, 0.6f), 1 => (6, 0.8f), _ => null },
        "cookie" => (2, 0.1f), "melon" => (2, 0.3f), "beef" => (3, 0.3f), "cooked_beef" => (8, 0.8f), "chicken" => (2, 0.3f),
        "cooked_chicken" => (6, 0.6f), "rotten_flesh" => (4, 0.1f), "spider_eye" => (2, 0.8f), "carrot" => (3, 0.6f),
        "potato" => (1, 0.3f), "baked_potato" => (5, 0.6f), "poisonous_potato" => (2, 0.3f), "golden_carrot" => (6, 1.2f),
        "pumpkin_pie" => (8, 0.3f), "rabbit" => (3, 0.3f), "cooked_rabbit" => (5, 0.6f), "rabbit_stew" => (10, 0.6f),
        "mutton" => (2, 0.3f), "cooked_mutton" => (6, 0.8f), "chorus_fruit" => (4, 0.3f), "beetroot" => (1, 0.6f),
        "beetroot_soup" => (6, 0.6f),
        _ => null,
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
