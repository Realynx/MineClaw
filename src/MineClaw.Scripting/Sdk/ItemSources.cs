using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Scripting.Sdk;

/// <summary>
/// Where an item comes from in the world, for the SDK's <c>gather()</c> source routes (zero tokens, no action): the crop or plant a
/// harvest skill takes it from (replanting), the tree to chop, the blocks that drop it when mined (natural sources first; crafted
/// blocks such as stairs never), the mobs that drop it. Data, not strategy: which route a job prefers stays in the script or memory.
/// Names and drop rules resolve through this version's <see cref="IGameData"/> (<see cref="IGameData.DropsFor"/>), and a name this
/// version doesn't know is skipped.
/// </summary>
public static class ItemSources
{
    /// <summary>Plants a catalog skill harvests (and replants): block, the meta it drops the item at, the skill.
    /// 1.12.2's crops (absent from b1.8.1, so skipped there): carrots/potatoes (ripe 7) and beetroot (ripe 3) go through
    /// harvest_wheat; nether wart (ripe 3) has no skill yet (a plain dig).</summary>
    private static readonly (int Block, int Meta, string? Skill)[] Harvests =
    [
        (Blocks.Crops, 7, "harvest_wheat"), (Blocks.SugarCane, 0, "harvest_cane"), (Blocks.Cactus, 0, "harvest_cactus"),
        (Blocks.Melon, 0, "harvest_melons"), (141, 7, "harvest_wheat"), (142, 7, "harvest_wheat"), (207, 3, "harvest_wheat"),
        (115, 3, null),
    ];

    /// <summary>Mob drops (mc-dev b1.8.1 getDropItemId / dropFewItems), by mob name (<c>MobTypes.Name</c>), plus 1.12.2 drops
    /// whose items only exist there (a version without the item skips the entry). Not modelled: a drop that CHANGED between the
    /// versions (1.12.2 pigmen drop rotten flesh + gold nuggets, not cooked porkchops).</summary>
    private static readonly (string Mob, string[] Items)[] MobDrops =
    [
        ("zombie", ["rotten_flesh"]), ("skeleton", ["bone", "arrow"]), ("spider", ["string"]), ("cave_spider", ["string"]),
        ("creeper", ["gunpowder"]), ("slime", ["slime_ball"]), ("enderman", ["ender_pearl"]), ("ghast", ["gunpowder"]),
        ("zombie_pigman", ["cooked_porkchop"]), ("pig", ["porkchop"]), ("cow", ["leather", "beef"]), ("chicken", ["feather", "chicken"]),
        ("sheep", ["wool"]),
        ("sheep", ["mutton"]), ("blaze", ["blaze_rod"]), ("magma_cube", ["magma_cream"]), ("rabbit", ["rabbit", "rabbit_hide"]),
        ("spider", ["spider_eye"]), ("cave_spider", ["spider_eye"]), ("witch", ["glass_bottle", "spider_eye"]), ("squid", ["dye"]),
        ("guardian", ["prismarine_shard", "prismarine_crystals"]),
    ];

    /// <summary>[{via:'harvest'|'chop'|'mine'|'kill', block|mob, skill?}] for the item, best route first; empty when none is known.</summary>
    public static JsonArray For(IGameData data, string item)
    {
        var list = new JsonArray();
        var it = data.ItemByName(item);
        if (it == null) return list;
        int id = it.Id;
        var routes = new Routes(list);
        var harvested = AddHarvests(data, id, routes);
        if (id is Blocks.Log or Log2 && data.BlockExists(id)) routes.Add("chop", "block", data.Block(id).Name);
        foreach (var b in MinedBlocks(data, id, harvested)) routes.Add("mine", "block", data.Block(b).Name);
        foreach (var (mob, items) in MobDrops)
            if (items.Any(n => data.ItemByName(n)?.Id == id)) routes.Add("kill", "mob", mob);
        return list;
    }

    /// <summary>Route objects, each (via, name) once.</summary>
    private sealed class Routes(JsonArray list)
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public void Add(string via, string key, string name, string? skill = null)
        {
            if (!_seen.Add(via + ":" + name)) return;
            var o = new JsonObject { ["via"] = via, [key] = name };
            if (skill != null) o["skill"] = skill;
            list.Add(o);
        }
    }

    /// <summary>The plants that drop the item (with their harvest skill); returns their blocks.</summary>
    private static HashSet<int> AddHarvests(IGameData data, int id, Routes routes)
    {
        var harvested = new HashSet<int>();
        foreach (var (block, meta, skill) in Harvests)
            if (data.BlockExists(block) && data.DropsFor(block, meta).Any(d => d.ItemId == id))
            {
                harvested.Add(block);
                routes.Add("harvest", "block", data.Block(block).Name, skill);
            }
        return harvested;
    }

    /// <summary>
    /// Natural sources before the item's own block (stone before cobblestone), then by expected yield; never farmland, crafted
    /// blocks (stairs, furnaces...: someone built them) or the plants above.
    /// </summary>
    private static IEnumerable<int> MinedBlocks(IGameData data, int id, HashSet<int> harvested) =>
        data.BlockIds.Where(b => data.DropsFor(b, 0, b == Blocks.Web && !data.CanHarvest(b, -1) ? Items.Shears : BestTool(data, b)).Any(d => d.ItemId == id))
            .Where(b => b != Blocks.Log && b != Log2 && b != Blocks.Farmland && !harvested.Contains(b) && data.BlockExists(b)
                        && (b == id || data.RecipesFor(b).Count == 0))
            .Select(b => (Id: b, Yield: data.DropsFor(b, 0, BestTool(data, b)).Where(d => d.ItemId == id).Sum(d => d.Expected)))
            .Where(x => x.Yield > 0)
            .OrderBy(x => x.Id == id ? 1 : 0).ThenByDescending(x => x.Yield).ThenBy(x => x.Id)
            .Select(x => x.Id);

    /// <summary>1.12.2 acacia / dark oak logs.</summary>
    private const int Log2 = 162;

    private static int BestTool(IGameData data, int block) =>
        data.CanHarvest(block, -1) ? -1 : block is Blocks.SnowLayer or Blocks.SnowBlock ? Items.DiamondShovel : Items.DiamondPickaxe;
}
