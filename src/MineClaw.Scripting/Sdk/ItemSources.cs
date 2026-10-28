using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Scripting.Sdk;

/// <summary>
/// Where an item comes from in the world, for the SDK's <c>gather()</c> source routes (zero tokens, no action): the crop or plant a
/// harvest skill takes it from (replanting), the tree to chop, the blocks that drop it when mined (natural sources first; crafted
/// blocks such as stairs never), the mobs that drop it. Data, not strategy: which route a job prefers stays in the script or memory.
/// Names resolve through this version's <see cref="IGameData"/>; the drop rules are the b1.8.1 tables (<see cref="Drops"/>) until a
/// version brings its own, and a name this version doesn't know is skipped.
/// </summary>
public static class ItemSources
{
    /// <summary>Plants a catalog skill harvests (and replants): block, the meta it drops the item at, the skill.</summary>
    private static readonly (int Block, int Meta, string Skill)[] Harvests =
    [
        (Blocks.Crops, 7, "harvest_wheat"), (Blocks.SugarCane, 0, "harvest_cane"), (Blocks.Cactus, 0, "harvest_cactus"),
        (Blocks.Melon, 0, "harvest_melons"),
    ];

    /// <summary>Mob drops (mc-dev b1.8.1 getDropItemId / dropFewItems), by mob name (<c>MobTypes.Name</c>).</summary>
    private static readonly (string Mob, string[] Items)[] MobDrops =
    [
        ("zombie", ["rotten_flesh"]), ("skeleton", ["bone", "arrow"]), ("spider", ["string"]), ("cave_spider", ["string"]),
        ("creeper", ["gunpowder"]), ("slime", ["slime_ball"]), ("enderman", ["ender_pearl"]), ("ghast", ["gunpowder"]),
        ("zombie_pigman", ["cooked_porkchop"]), ("pig", ["porkchop"]), ("cow", ["leather", "beef"]), ("chicken", ["feather", "chicken"]),
        ("sheep", ["wool"]),
    ];

    /// <summary>[{via:'harvest'|'chop'|'mine'|'kill', block|mob, skill?}] for the item, best route first; empty when none is known.</summary>
    public static JsonArray For(IGameData data, string item)
    {
        var list = new JsonArray();
        var it = data.ItemByName(item);
        if (it == null) return list;
        int id = it.Id;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string via, string key, string name, string? skill = null)
        {
            if (!seen.Add(via + ":" + name)) return;
            var o = new JsonObject { ["via"] = via, [key] = name };
            if (skill != null) o["skill"] = skill;
            list.Add(o);
        }
        var harvested = new HashSet<int>();
        foreach (var (block, meta, skill) in Harvests)
            if (data.BlockExists(block) && Drops.For(block, meta).Any(d => d.ItemId == id))
            {
                harvested.Add(block);
                Add("harvest", "block", data.Block(block).Name, skill);
            }
        if (id == Blocks.Log) Add("chop", "block", data.Block(Blocks.Log).Name);
        // Natural sources before the item's own block (stone before cobblestone), then by expected yield; never farmland, crafted
        // blocks (stairs, furnaces...: someone built them) or the plants above.
        var mined = Drops.SourcesOf(id)
            .Where(b => b != Blocks.Log && b != Blocks.Farmland && !harvested.Contains(b) && data.BlockExists(b)
                        && (b == id || data.RecipesFor(b).Count == 0))
            .Select(b => (Id: b, Yield: Drops.For(b, 0, BestTool(b)).Where(d => d.ItemId == id).Sum(d => d.Expected)))
            .Where(x => x.Yield > 0)
            .OrderBy(x => x.Id == id ? 1 : 0).ThenByDescending(x => x.Yield).ThenBy(x => x.Id);
        foreach (var (b, _) in mined) Add("mine", "block", data.Block(b).Name);
        foreach (var (mob, items) in MobDrops)
            if (items.Any(n => data.ItemByName(n)?.Id == id)) Add("kill", "mob", mob);
        return list;
    }

    private static int BestTool(int block) =>
        Tools.CanHarvest(block, -1) ? -1 : block is Blocks.SnowLayer or Blocks.SnowBlock ? Items.DiamondShovel : Items.DiamondPickaxe;
}
