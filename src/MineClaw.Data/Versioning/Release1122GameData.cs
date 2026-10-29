using System.Reflection;
using System.Text.Json;

namespace MineClaw.Data.Versioning;

/// <summary>
/// 1.12.2 game data, multi-version P2: block and item ids, names, variant names, stack sizes, durabilities, hardness and harvest
/// tools from PrismarineJS minecraft-data pc/1.12.2 (vendored at <c>third_party/minecraft-data</c>, embedded in this assembly),
/// and the 1.12.2 dig-speed rule ported from the decompiled client (<see cref="Release1122Dig"/>). The numeric ids are a
/// superset of b1.8.1's, so the <see cref="Blocks"/>/<see cref="Items"/> constants stay valid. Blocks b1.8.1 knows keep its
/// physics flags (solid, collision height, liquid, climbable, interactable) with 1.12.2's name and hardness; newer blocks get
/// flags derived from minecraft-data. P5: crafting recipes (<see cref="Release1122Recipes"/>), smelting and fuel
/// (<see cref="Release1122Smelting"/>), block drops (<see cref="Release1122Drops"/>) and food values
/// (<see cref="Release1122Foods"/>); this table answers the static facades (<see cref="GameData.Current"/>) in a 1.12.2 process.
/// </summary>
public sealed class Release1122GameData : IGameData
{
    public static readonly Release1122GameData Instance = new();

    internal sealed record McBlock(int Id, string Name, string DisplayName, float Hardness, bool FullBox, string? Category,
        IReadOnlySet<int> HarvestTools, bool Transparent, IReadOnlyDictionary<int, string> Variants);

    internal sealed record McItem(int Id, string Name, string DisplayName, int StackSize, int MaxDurability, IReadOnlyDictionary<int, string> Variants);

    private readonly Lazy<Tables> _t = new(Load);
    private readonly Lazy<Release1122Recipes> _recipes;
    private readonly Lazy<Release1122Smelting> _smelting;

    private Release1122GameData()
    {
        _recipes = new(() => new Release1122Recipes(this));
        _smelting = new(() => new Release1122Smelting(this));
    }

    private sealed class Tables
    {
        public readonly Dictionary<int, McBlock> Blocks = new();
        public readonly Dictionary<int, McItem> Items = new();
        public readonly Dictionary<int, BlockInfo> BlockInfos = new();
        public readonly Dictionary<int, ItemInfo> ItemInfos = new();
        public readonly Dictionary<string, (int Id, int Damage)> ItemNames = new();
        public readonly Dictionary<string, int> BlockNames = new();
        public readonly Dictionary<string, int> IdOfName = new(); // exact snake-case name → block or item id
    }

    public string VersionId => "1.12.2";
    public bool HalvesFollowCursor => true;

    private Tables T => _t.Value;

    // ------------------------------------------------------------------ blocks

    public BlockInfo Block(int id) => T.BlockInfos.TryGetValue(id, out var b) ? b : MineClaw.Data.Blocks.GetB(id);
    public bool BlockExists(int id) => T.Blocks.ContainsKey(id);

    public IEnumerable<int> BlockIds => T.Blocks.Keys.Order();

    public BlockInfo? BlockByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = MineClaw.Data.Blocks.Normalize(name);
        foreach (var k in Candidates(key))
            if (T.BlockNames.TryGetValue(k, out int id)) return Block(id);
        return MineClaw.Data.Blocks.ByNameB(name) is { } old && T.Blocks.ContainsKey(old.Id) ? Block(old.Id) : null;
    }

    /// <summary>The minecraft-data snake-case name of a block or item id ("crafting_table", "wooden_pickaxe").</summary>
    public string? NameOf(int id) => T.Items.TryGetValue(id, out var i) ? i.Name : T.Blocks.TryGetValue(id, out var b) ? b.Name : null;

    /// <summary>The id of an exact minecraft-data name, or null.</summary>
    public int? IdOf(string snakeName) => T.IdOfName.TryGetValue(snakeName, out int id) ? id : null;

    // ------------------------------------------------------------------ items

    public ItemInfo Item(int id) => T.ItemInfos.TryGetValue(id, out var i) ? i : MineClaw.Data.Items.GetB(id);
    public bool ItemExists(int id) => T.Items.ContainsKey(id);
    public ItemInfo? ItemByName(string name) => ItemByNameWithDamage(name)?.Item;

    public (ItemInfo Item, int Damage)? ItemByNameWithDamage(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = MineClaw.Data.Blocks.Normalize(name);
        foreach (var k in Candidates(key))
            if (T.ItemNames.TryGetValue(k, out var hit)) return (Item(hit.Id), hit.Damage);
        if (int.TryParse(key, out int n) && T.Items.ContainsKey(n)) return (Item(n), -1);
        // b1.8.1 aliases ("cobble", "workbench", "20 cobble slabs") keep working where the id still means the same thing.
        return MineClaw.Data.Items.ByNameWithDamageB(name) is { } old && T.Items.ContainsKey(old.Item.Id) ? (Item(old.Item.Id), old.Damage) : null;
    }

    public string DisplayName(int id, int damage = 0)
    {
        if (id <= 0) return id == 0 ? "Air" : "(empty)";
        int m = Math.Max(0, damage);
        if (T.Items.TryGetValue(id, out var it))
        {
            if (it.Variants.TryGetValue(m, out var v)) return v;
            if (id < 256 && T.Blocks.TryGetValue(id, out var bl) && bl.Variants.TryGetValue(m, out var bv)) return bv;
            if (it.MaxDurability > 0 && damage > 0) return $"{it.DisplayName} ({it.MaxDurability - damage}/{it.MaxDurability})";
            return it.DisplayName;
        }
        return T.Blocks.TryGetValue(id, out var b) ? b.Variants.GetValueOrDefault(m, b.DisplayName) : MineClaw.Data.Items.DisplayNameB(id, damage);
    }

    /// <summary>The variant's display name in snake case when the id has variants ("birch_wood_planks", "charcoal"), else the
    /// registry name; both resolve back through <see cref="ItemByNameWithDamage"/>.</summary>
    public string VariantName(int id, int meta)
    {
        bool variants = T.Items.TryGetValue(id, out var it) && it.Variants.Count > 1
            || id < 256 && T.Blocks.TryGetValue(id, out var b) && b.Variants.Count(v => v.Key < 16) > 1;
        if (!variants) return NameOf(id) ?? MineClaw.Data.Items.VariantNameB(id, meta);
        return DisplayName(id, Math.Max(0, meta)).ToLowerInvariant().Replace(' ', '_');
    }

    private static IEnumerable<string> Candidates(string key)
    {
        yield return key;
        if (key.EndsWith("es")) yield return key[..^2];
        if (key.EndsWith('s')) yield return key[..^1];
    }

    // ------------------------------------------------------------------ crafting, smelting, drops, food (P5)

    public IReadOnlyList<Recipe> AllRecipes => _recipes.Value.All;
    public Recipe? MatchRecipe(IReadOnlyList<ItemStack> grid, int gridSize) => Recipes.MatchIn(_recipes.Value.All, grid, gridSize);
    public IReadOnlyList<Recipe> RecipesFor(int itemId, int damage = -1) => _recipes.Value.ForOutput(itemId, damage);
    public bool IsUnpack(Recipe r) => _recipes.Value.IsUnpack(r);
    public IReadOnlyList<SmeltingRecipe> AllSmelting => _smelting.Value.All;
    public SmeltingRecipe? SmeltingFor(int inputId) => _smelting.Value.For(inputId, -1);
    public SmeltingRecipe? SmeltingFor(int inputId, int damage) => _smelting.Value.For(inputId, damage);
    public int CookTicks => _smelting.Value.CookTicks;
    public int FuelTicks(int itemId) => Release1122Smelting.FuelTicks(this, itemId);
    public IReadOnlyList<Drop> DropsFor(int blockId, int meta = 0, int heldItemId = -1) => Release1122Drops.For(this, blockId, meta, heldItemId);
    public (int Hunger, float Saturation)? Food(int itemId, int damage = 0) => Release1122Foods.Food(NameOf(itemId), damage);
    public float FoodPoisonChance(int itemId, int damage = 0) => Release1122Foods.PoisonChance(NameOf(itemId), damage);

    // ------------------------------------------------------------------ digging

    public bool CanHarvest(int blockId, int heldItemId) =>
        !T.Blocks.TryGetValue(blockId, out var b) || b.HarvestTools.Count == 0 || heldItemId > 0 && b.HarvestTools.Contains(heldItemId);

    public int DigTicks(int blockId, int heldItemId, bool onGround = true, bool inWater = false) =>
        DigTicks(new DigInput(blockId, 0, heldItemId > 0 ? new ItemStack((short)heldItemId, 1, 0) : ItemStack.Empty, onGround, inWater));

    public int DigTicks(in DigInput input) => Release1122Dig.Ticks(Release1122Dig.RelativeHardness(this, input));

    internal McBlock? McBlockOf(int id) => T.Blocks.GetValueOrDefault(id);
    internal McItem? McItemOf(int id) => T.Items.GetValueOrDefault(id);
    internal int IdOrMinus(string name) => T.IdOfName.TryGetValue(name, out int id) ? id : -1;

    // ------------------------------------------------------------------ loading

    private static readonly HashSet<string> InteractableNames =
    [
        "chest", "trapped_chest", "ender_chest", "furnace", "lit_furnace", "crafting_table", "dispenser", "dropper", "hopper",
        "noteblock", "jukebox", "bed", "cake", "lever", "stone_button", "wooden_button", "trapdoor", "iron_trapdoor", "anvil",
        "enchanting_table", "brewing_stand", "beacon", "cauldron", "flower_pot", "unpowered_repeater", "powered_repeater",
        "unpowered_comparator", "powered_comparator", "daylight_detector", "daylight_detector_inverted", "command_block",
        "repeating_command_block", "chain_command_block", "structure_block", "dragon_egg",
    ];

    private static Tables Load()
    {
        var t = new Tables();
        using (var doc = JsonDocument.Parse(Resource("mcdata.1.12.2.blocks.json")))
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                int id = e.GetProperty("id").GetInt32();
                var tools = new HashSet<int>();
                if (e.TryGetProperty("harvestTools", out var ht))
                    foreach (var p in ht.EnumerateObject()) tools.Add(int.Parse(p.Name));
                float hardness = e.TryGetProperty("hardness", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetSingle() : -1f;
                var b = new McBlock(id, e.GetProperty("name").GetString()!, e.GetProperty("displayName").GetString()!, hardness,
                    e.GetProperty("boundingBox").GetString() == "block",
                    e.TryGetProperty("material", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                    tools, e.TryGetProperty("transparent", out var tr) && tr.GetBoolean(), Variants(e));
                t.Blocks[id] = b;
                t.IdOfName.TryAdd(b.Name, id);
            }
        using (var doc = JsonDocument.Parse(Resource("mcdata.1.12.2.items.json")))
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                int id = e.GetProperty("id").GetInt32();
                var it = new McItem(id, e.GetProperty("name").GetString()!, e.GetProperty("displayName").GetString()!,
                    e.GetProperty("stackSize").GetInt32(), e.TryGetProperty("maxDurability", out var d) ? d.GetInt32() : 0, Variants(e));
                t.Items[id] = it;
                t.IdOfName[it.Name] = id; // the item name wins where a block shares it (e.g. "reeds" item vs block)
            }

        foreach (var b in t.Blocks.Values) t.BlockInfos[b.Id] = ToBlockInfo(b, t);
        foreach (var i in t.Items.Values) t.ItemInfos[i.Id] = ToItemInfo(i);

        // Names: exact names, display names, then the variants ("birch wood planks" → (5, 2)); first writer wins.
        void AddItemName(string n, int id, int dmg) => t.ItemNames.TryAdd(MineClaw.Data.Blocks.Normalize(n), (id, dmg));
        foreach (var i in t.Items.Values.OrderBy(x => x.Id)) { AddItemName(i.Name, i.Id, -1); AddItemName(i.DisplayName, i.Id, -1); }
        foreach (var i in t.Items.Values.OrderBy(x => x.Id))
        {
            foreach (var (meta, n) in i.Variants) AddItemName(n, i.Id, meta);
            if (i.Id < 256 && t.Blocks.TryGetValue(i.Id, out var bl))
                foreach (var (meta, n) in bl.Variants.Where(v => v.Key < 16)) AddItemName(n, i.Id, meta);
        }
        foreach (var b in t.Blocks.Values.OrderBy(x => x.Id))
        {
            t.BlockNames.TryAdd(MineClaw.Data.Blocks.Normalize(b.Name), b.Id);
            t.BlockNames.TryAdd(MineClaw.Data.Blocks.Normalize(b.DisplayName), b.Id);
        }
        return t;
    }

    private static Dictionary<int, string> Variants(JsonElement e)
    {
        var v = new Dictionary<int, string>();
        if (e.TryGetProperty("variations", out var arr))
            foreach (var x in arr.EnumerateArray())
                v.TryAdd(x.GetProperty("metadata").GetInt32(), x.GetProperty("displayName").GetString()!);
        return v;
    }

    private static BlockInfo ToBlockInfo(McBlock b, Tables t)
    {
        var (tool, level) = BestTool(b, t);
        if (MineClaw.Data.Blocks.ExistsB(b.Id) && b.Id <= 109 && b.Id != 95) // 95: b1.8.1 locked chest, 1.12.2 stained glass
        {
            var old = MineClaw.Data.Blocks.GetB(b.Id);
            return old with { Name = b.Name, DisplayName = b.DisplayName, Hardness = b.Hardness, MinHarvestLevel = b.HarvestTools.Count == 0 ? -1 : level };
        }
        bool liquid = b.Name.Contains("water") || b.Name.Contains("lava");
        return new BlockInfo(b.Id, b.Name, b.DisplayName, b.Hardness, b.FullBox, b.FullBox ? 1.0 : 0.0, liquid,
            b.Name is "ladder" or "vine", b.Transparent || !b.FullBox, InteractableNames.Contains(b.Name) || b.Name.EndsWith("_door")
                || b.Name.EndsWith("fence_gate") || b.Name.EndsWith("shulker_box"),
            tool, b.HarvestTools.Count == 0 ? -1 : level, b.Name is "magma" or "fire" || b.Name.Contains("lava"));
    }

    private static (ToolType Tool, int Level) BestTool(McBlock b, Tables t)
    {
        if (b.HarvestTools.Count > 0)
        {
            int lvl = b.HarvestTools.Select(id => t.Items.TryGetValue(id, out var it) ? Tier(it.Name) : 9).Select(x => x == 4 ? 0 : x).DefaultIfEmpty(0).Min();
            var first = t.Items.GetValueOrDefault(b.HarvestTools.First());
            return (first is null ? ToolType.None : ToolOf(first.Name), lvl);
        }
        return (b.Category switch { "rock" => ToolType.Pickaxe, "dirt" => ToolType.Shovel, "wood" => ToolType.Axe, "web" => ToolType.Sword, "wool" => ToolType.Shears, _ => ToolType.None }, -1);
    }

    private static ItemInfo ToItemInfo(McItem i) =>
        new(i.Id, i.Name, i.DisplayName, i.StackSize, i.MaxDurability, ToolOf(i.Name), ToolOf(i.Name) is ToolType.None or ToolType.Shears ? -1 : Tier(i.Name), i.Id < 256);

    internal static ToolType ToolOf(string name) =>
        name.EndsWith("_pickaxe") ? ToolType.Pickaxe : name.EndsWith("_axe") ? ToolType.Axe : name.EndsWith("_shovel") ? ToolType.Shovel
        : name.EndsWith("_sword") ? ToolType.Sword : name.EndsWith("_hoe") ? ToolType.Hoe : name == "shears" ? ToolType.Shears : ToolType.None;

    /// <summary>Item.ToolMaterial order: 0 wood, 1 stone, 2 iron, 3 diamond, 4 gold.</summary>
    internal static int Tier(string name) =>
        name.StartsWith("wooden_") ? 0 : name.StartsWith("stone_") ? 1 : name.StartsWith("iron_") ? 2 : name.StartsWith("diamond_") ? 3
        : name.StartsWith("golden_") ? 4 : -1;

    internal static Stream Resource(string name) =>
        typeof(Release1122GameData).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"embedded resource {name} missing from {typeof(Release1122GameData).Assembly.GetName().Name}");
}

/// <summary>
/// The 1.12.2 dig-speed rule, ported from the decompiled client:
/// <list type="bullet">
/// <item><c>Block.getPlayerRelativeBlockHardness</c>: hardness &lt; 0 → 0; <c>getDigSpeed / hardness / 30</c> when
/// <c>EntityPlayer.canHarvestBlock</c>, else <c>/ 100</c> (b1.8.1 used 1 / hardness / 100 there, ignoring the tool).</item>
/// <item><c>EntityPlayer.getDigSpeed</c>: the held stack's <c>getDestroySpeed</c>; when &gt; 1 and the stack has Efficiency n,
/// + n² + 1; Haste a: × (1 + a·0.2); Mining Fatigue: × 0.3 / 0.09 / 0.0027 / 8.1E-4; eyes in water without Aqua Affinity: / 5;
/// not on the ground: / 5.</item>
/// <item><c>getDestroySpeed</c>: <c>ItemPickaxe</c> = efficiency on rock/iron/anvil materials (the blocks whose harvest tools
/// are pickaxes) and its EFFECTIVE_ON set; <c>ItemAxe</c> on wood/plants/vine materials and its set; <c>ItemSpade</c> its set;
/// <c>ItemSword</c> web 15, plants/vine/coral/leaves/gourd 1.5, else 1; <c>ItemShears</c> web/leaves 15, wool 5. Tool
/// material efficiency: wood 2, stone 4, iron 6, diamond 8, gold 12.</item>
/// <item><c>PlayerControllerMP.onPlayerDamageBlock</c>: the progress accumulates as a float and the block breaks at ≥ 1.0;
/// ≥ 1.0 on the first hit breaks it at once (<c>clickBlock</c>).</item>
/// </list>
/// </summary>
public static class Release1122Dig
{
    public const int EfficiencyEnchantment = 32;

    private static readonly string[] PickaxeOn =
    [
        "activator_rail", "coal_ore", "cobblestone", "detector_rail", "diamond_block", "diamond_ore", "double_stone_slab", "golden_rail",
        "gold_block", "gold_ore", "ice", "iron_block", "iron_ore", "lapis_block", "lapis_ore", "lit_redstone_ore", "mossy_cobblestone",
        "netherrack", "packed_ice", "rail", "redstone_ore", "sandstone", "red_sandstone", "stone", "stone_slab", "stone_button",
        "stone_pressure_plate",
    ];
    private static readonly string[] AxeOn =
        ["planks", "bookshelf", "log", "log2", "chest", "pumpkin", "lit_pumpkin", "melon_block", "ladder", "wooden_button", "wooden_pressure_plate"];
    private static readonly string[] ShovelOn =
        ["clay", "dirt", "farmland", "grass", "gravel", "mycelium", "sand", "snow", "snow_layer", "soul_sand", "grass_path", "concrete_powder"];
    // Material.PLANTS / VINE among minecraft-data's "plant" category (leaves are LEAVES, cactus CACTUS, pumpkin/melon GOURD).
    private static readonly HashSet<string> NotPlantsOrVine = ["leaves", "leaves2", "cactus", "pumpkin", "lit_pumpkin", "melon_block"];
    private static readonly HashSet<int> Pickaxes = [Items.WoodenPickaxe, Items.StonePickaxe, Items.IronPickaxe, Items.DiamondPickaxe, Items.GoldenPickaxe];

    /// <summary>Tool material efficiency (Item.ToolMaterial): wood 2, stone 4, iron 6, diamond 8, gold 12.</summary>
    public static float Efficiency(int tier) => tier switch { 0 => 2f, 1 => 4f, 2 => 6f, 3 => 8f, 4 => 12f, _ => 1f };

    /// <summary><c>ItemStack.getDestroySpeed</c> of the held item against the block (1 for the hand and non-tools).</summary>
    public static float DestroySpeed(Release1122GameData d, int blockId, int heldItemId)
    {
        if (heldItemId <= 0 || d.McItemOf(heldItemId) is not { } item || d.McBlockOf(blockId) is not { } block) return 1f;
        var tool = Release1122GameData.ToolOf(item.Name);
        float eff = Efficiency(Release1122GameData.Tier(item.Name));
        string n = block.Name;
        switch (tool)
        {
            case ToolType.Pickaxe:
                return block.HarvestTools.Overlaps(Pickaxes) || Array.IndexOf(PickaxeOn, n) >= 0 ? eff : 1f;
            case ToolType.Axe:
                return block.Category == "wood" || block.Category == "plant" && !NotPlantsOrVine.Contains(n) || Array.IndexOf(AxeOn, n) >= 0 ? eff : 1f;
            case ToolType.Shovel:
                return Array.IndexOf(ShovelOn, n) >= 0 ? eff : 1f;
            case ToolType.Sword:
                if (n == "web") return 15f;
                return block.Category == "plant" && n != "cactus" ? 1.5f : 1f;
            case ToolType.Shears:
                if (n is "web" or "leaves" or "leaves2") return 15f;
                return n == "wool" ? 5f : 1f;
            default:
                return 1f;
        }
    }

    /// <summary>Dig progress per tick (<c>getPlayerRelativeBlockHardness</c>); 0 = unbreakable, +∞ = instant (hardness 0).</summary>
    public static float RelativeHardness(Release1122GameData d, in DigInput input)
    {
        if (input.BlockId == 0 || d.McBlockOf(input.BlockId) is not { } block) return 0f;
        if (block.Name.Contains("water") || block.Name.Contains("lava")) return 0f;
        float hardness = block.Hardness;
        if (hardness < 0f) return 0f;
        int held = input.Held.IsEmpty ? -1 : input.Held.Id;
        float f = DestroySpeed(d, input.BlockId, held);
        if (f > 1f)
        {
            int eff = input.Held.Tag?.EnchantmentLevel(EfficiencyEnchantment) ?? 0;
            if (eff > 0 && !input.Held.IsEmpty) f += eff * eff + 1;
        }
        if (input.Haste > 0) f *= 1f + input.Haste * 0.2f;
        if (input.Fatigue > 0) f *= input.Fatigue switch { 1 => 0.3f, 2 => 0.09f, 3 => 0.0027f, _ => 8.1E-4f };
        if (input.InWater && !input.AquaAffinity) f /= 5f;
        if (!input.OnGround) f /= 5f;
        return d.CanHarvest(input.BlockId, held) ? f / hardness / 30f : f / hardness / 100f;
    }

    /// <summary>Ticks until the float accumulation reaches 1 (0 = breaks on the first hit, <see cref="int.MaxValue"/> = never).</summary>
    public static int Ticks(float perTick)
    {
        if (perTick <= 0f || float.IsNaN(perTick)) return int.MaxValue;
        if (perTick >= 1f) return 0;
        float acc = 0f;
        int ticks = 0;
        while (acc < 1f) { acc += perTick; ticks++; }
        return ticks;
    }
}
