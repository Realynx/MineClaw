using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>
/// What a growing zone can grow (docs/web/story-mode.md "Farming"), per version: the plant's block, its seed item (what is sown), the
/// soil it needs, how it is harvested and where its seeds come from.
/// <list type="bullet">
/// <item><c>farmland</c>: dirt or grass is tilled first (a hoe), best within 4 of water.</item>
/// <item><c>sand</c> (cactus: nothing solid beside it), <c>soul_sand</c> (nether wart), <c>dirt</c> (saplings: dirt or grass),
/// <c>cane</c> (sand, dirt or grass with water right beside it).</item>
/// </list>
/// Harvest: <c>crop</c> (ripe at <see cref="StoryPlant.Ripe"/>: dug and sown again), <c>fruit</c> (the stem stays; the pumpkin or melon
/// beside it is the harvest), <c>stalk</c> (sugar cane and cactus: cut above the bottom block, which grows again), <c>tree</c> (a grown
/// sapling is chopped; its leaves give saplings back).
/// </summary>
public sealed record StoryPlant(string Id, string Label, int Block, int SeedId, int SeedMeta, string Soil, string Harvest, int Ripe,
    int Fruit, bool ModernOnly, string Source)
{
    public JsonObject Json(IGameData d) => new()
    {
        ["id"] = Id, ["label"] = Label, ["block"] = Block, ["seed"] = StoryPlants.SeedName(d, this), ["seed_item"] = new JsonArray(SeedId, SeedMeta),
        ["soil"] = Soil, ["harvest"] = Harvest, ["source"] = Source,
    };
}

public static class StoryPlants
{
    public const int Farmland = 60, Sand = 12, SoulSand = 88, Grass = 2, Dirt = 3, Cactus = 81, Cane = 83;

    private static readonly StoryPlant[] All =
    [
        new("wheat", "Wheat", 59, 295, 0, "farmland", "crop", 7, 0, false, "cut tall grass (seeds drop) or harvest wheat"),
        new("carrots", "Carrots", 141, 391, 0, "farmland", "crop", 7, 0, true, "the carrot itself: harvests, zombie drops, village farms and chests"),
        new("potatoes", "Potatoes", 142, 392, 0, "farmland", "crop", 7, 0, true, "the potato itself: harvests, zombie drops, village farms and chests"),
        new("beetroot", "Beetroot", 207, 435, 0, "farmland", "crop", 3, 0, true, "harvest beetroot; village farms and chests"),
        new("pumpkin", "Pumpkin", 104, 361, 0, "farmland", "fruit", 7, 86, false, "craft them from a pumpkin (a bill at a crafting table)"),
        new("melon", "Melon", 105, 362, 0, "farmland", "fruit", 7, 103, false, "craft them from a melon slice (a bill at a crafting table)"),
        new("sugar_cane", "Sugar cane", Cane, 338, 0, "cane", "stalk", 0, 0, false, "the cane itself: found by water"),
        new("cactus", "Cactus", Cactus, 81, 0, "sand", "stalk", 0, 0, false, "the cactus itself: found in deserts"),
        new("nether_wart", "Nether wart", 115, 372, 0, "soul_sand", "crop", 3, 0, true, "nether fortresses"),
        new("oak", "Oak sapling", 6, 6, 0, "dirt", "tree", 0, 0, false, "leaves of chopped oaks drop them"),
        new("spruce", "Spruce sapling", 6, 6, 1, "dirt", "tree", 0, 0, false, "leaves of chopped spruces drop them"),
        new("birch", "Birch sapling", 6, 6, 2, "dirt", "tree", 0, 0, false, "leaves of chopped birches drop them"),
        new("jungle", "Jungle sapling", 6, 6, 3, "dirt", "tree", 0, 0, true, "leaves of jungle trees drop them"),
        new("acacia", "Acacia sapling", 6, 6, 4, "dirt", "tree", 0, 0, true, "leaves of acacias drop them"),
        new("dark_oak", "Dark oak sapling", 6, 6, 5, "dirt", "tree", 0, 0, true, "leaves of dark oaks drop them (plant them 2x2)"),
    ];

    private static bool Modern(IGameData d) => d.VersionId != "b1.8.1";

    /// <summary>The plants this version grows.</summary>
    public static IReadOnlyList<StoryPlant> For(IGameData d) => All.Where(p => !p.ModernOnly || Modern(d)).ToList();

    public static StoryPlant? Get(IGameData d, string? id) =>
        id == null ? null : For(d).FirstOrDefault(p => p.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The seed item's name in this version ("wheat_seeds" / "seeds", "spruce_sapling"...): what the place tool takes.</summary>
    public static string SeedName(IGameData d, StoryPlant p)
    {
        if (p.SeedMeta > 0 && d.VariantName(p.SeedId, p.SeedMeta) is { Length: > 0 } v) return v;
        return d.ItemExists(p.SeedId) ? d.Item(p.SeedId).Name : d.BlockExists(p.SeedId) ? d.Block(p.SeedId).Name : p.SeedId.ToString();
    }

    public static JsonArray Json(IGameData d) => new(For(d).Select(p => (JsonNode)p.Json(d)).ToArray());

    /// <summary>Does this stack sow <paramref name="p"/>?</summary>
    public static bool IsSeed(StoryPlant p, Stack s) => s.Id == p.SeedId && (p.SeedId != 6 || (s.Meta & 7) == p.SeedMeta);

    // ---------------------------------------------------------------- the world

    public static bool Water(int id) => id is 8 or 9;

    /// <summary>Water within 4 blocks sideways at the soil's level or one above (the farmland hydration rule).</summary>
    public static bool WaterNear(IStoryWorld w, P3 soil, int r = 4)
    {
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
                for (int dy = 0; dy <= 1; dy++)
                    if (Water(w.Block(soil.X + dx, soil.Y + dy, soil.Z + dz).Id)) return true;
        return false;
    }

    /// <summary>Water right beside the soil (sugar cane's rule).</summary>
    public static bool WaterBeside(IStoryWorld w, P3 soil) =>
        Water(w.Block(soil.X + 1, soil.Y, soil.Z).Id) || Water(w.Block(soil.X - 1, soil.Y, soil.Z).Id)
        || Water(w.Block(soil.X, soil.Y, soil.Z + 1).Id) || Water(w.Block(soil.X, soil.Y, soil.Z - 1).Id);

    /// <summary>Nothing solid beside the cell (a cactus breaks otherwise).</summary>
    public static bool ClearBeside(IStoryWorld w, P3 c)
    {
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            if (!StoryWander.Passable(w.Block(c.X + dx, c.Y, c.Z + dz).Id)) return false;
        return true;
    }

    /// <summary>Could this soil block grow <paramref name="p"/> (after tilling, for farmland plants)?</summary>
    public static bool SoilFits(IStoryWorld w, StoryPlant p, P3 soil)
    {
        int id = w.Block(soil.X, soil.Y, soil.Z).Id;
        return p.Soil switch
        {
            "farmland" => id is Farmland or Grass or Dirt,
            "sand" => id == Sand,
            "soul_sand" => id == SoulSand,
            "dirt" => id is Grass or Dirt or Farmland,
            "cane" => id is Sand or Grass or Dirt && WaterBeside(w, soil),
            _ => false,
        };
    }
}
