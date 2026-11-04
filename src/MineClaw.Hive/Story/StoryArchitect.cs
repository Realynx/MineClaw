using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>One placeable of the Architect palette: the item a pawn places, the block it makes, its tab and the build kinds it fits
/// (the first is the default tool).</summary>
public sealed record ArchitectEntry(string Id, string Label, string Tab, int ItemId, int ItemMeta, int BlockId, int BlockMeta, IReadOnlyList<string> Kinds);

/// <summary>
/// The Architect palette (docs/web/story-mode.md "Architect palette and tiers"): every placeable building block, furniture and
/// production bench the version has, grouped Structure, Furniture, Production, Storage, Lighting, Misc. Which ones exist comes from
/// the version's data (an id or variant the version lacks is left out); tiers and stock are the colony's (<see cref="StoryColony.Architect"/>).
/// </summary>
public static class StoryArchitect
{
    public static readonly IReadOnlyList<string> Tabs = ["structure", "furniture", "production", "storage", "lighting", "misc"];

    private static readonly string[] Solid = ["wall", "floor", "block", "roof"];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<ArchitectEntry>> Cache = new();

    public static IReadOnlyList<ArchitectEntry> Entries(IGameData d) => Cache.GetOrAdd(d.VersionId, _ => Build(d));

    private static IReadOnlyList<ArchitectEntry> Build(IGameData d)
    {
        var list = new List<ArchitectEntry>();
        var seen = new HashSet<(int, int)>();
        bool modern = d.BlockExists(126);
        void Add(string tab, int block, int meta, string[] kinds, int? item = null, int? itemMeta = null)
        {
            int it = item ?? block, im = itemMeta ?? meta;
            if (!d.BlockExists(block) || !d.ItemExists(it)) return;
            if (im > 0 && (d.ItemByNameWithDamage(d.VariantName(it, im)) is not { } b2 || b2.Item.Id != it || b2.Damage != im)) return;
            if (!seen.Add((it, im))) return;
            string id = im > 0 ? d.VariantName(it, im) : d.Item(it).Name;
            list.Add(new ArchitectEntry(id, StoryRecipes.Label(d, it, im), tab, it, im, block, meta, kinds));
        }
        // Structure: building blocks, stairs, slabs, fences, doors, glass.
        foreach (var m in Enumerable.Range(0, 6)) Add("structure", 5, m, Solid);
        foreach (var (id, meta) in new[] { (4, 0), (1, 0), (98, 0), (45, 0), (24, 0), (48, 0), (112, 0), (155, 0), (179, 0), (201, 0), (168, 0), (172, 0), (82, 0), (3, 0), (12, 0), (13, 0), (49, 0), (87, 0), (121, 0), (206, 0) })
            Add("structure", id, meta, Solid);
        foreach (var m in Enumerable.Range(1, 6)) Add("structure", 1, m, Solid);
        foreach (var m in Enumerable.Range(1, 3)) Add("structure", 98, m, Solid);
        foreach (var m in Enumerable.Range(0, 4)) Add("structure", 17, m, Solid);
        foreach (var m in Enumerable.Range(0, 2)) Add("structure", 162, m, Solid);
        foreach (var m in Enumerable.Range(0, 16)) Add("structure", 35, m, Solid);
        foreach (var m in Enumerable.Range(0, 16)) Add("structure", 159, m, Solid);
        foreach (var m in Enumerable.Range(0, 16)) Add("structure", 251, m, Solid);
        Add("structure", 20, 0, ["window", "wall", "block"]);
        foreach (var m in Enumerable.Range(0, 16)) Add("structure", 95, m, ["window", "wall", "block"]);
        Add("structure", 102, 0, ["window", "block"]);
        foreach (var m in Enumerable.Range(0, 16)) Add("structure", 160, m, ["window", "block"]);
        foreach (var s in new[] { 53, 134, 135, 136, 163, 164, 67, 108, 109, 114, 128, 156, 180, 203 }) Add("structure", s, 0, ["stairs"]);
        if (modern) foreach (var m in Enumerable.Range(0, 6)) Add("structure", 126, m, ["floor", "block", "roof"]);
        foreach (var m in Enumerable.Range(0, 8)) Add("structure", 44, m, ["floor", "block", "roof"]);
        foreach (var f in new[] { 85, 188, 189, 190, 191, 192, 113 }) Add("structure", f, 0, ["fence"]);
        foreach (var g in new[] { 107, 183, 184, 185, 186, 187 }) Add("structure", g, 0, ["block"]);
        Add("structure", 64, 0, ["door"], 324, 0);
        foreach (var (blk, it) in new[] { (193, 427), (194, 428), (195, 429), (196, 430), (197, 431) }) Add("structure", blk, 0, ["door"], it, 0);
        Add("structure", 71, 0, ["door"], 330, 0);
        Add("structure", 96, 0, ["block"]);
        Add("structure", 167, 0, ["block"]);
        Add("structure", 101, 0, ["window", "block"]);
        // Furniture.
        Add("furniture", 26, 0, ["bed"], 355, 0);
        Add("furniture", 47, 0, Solid);
        foreach (var m in Enumerable.Range(0, 16)) Add("furniture", 171, m, ["floor", "block"]);
        Add("furniture", 84, 0, ["block"]);
        Add("furniture", 25, 0, ["block"]);
        Add("furniture", 140, 0, ["block"], 390, 0);
        // Production.
        Add("production", 58, 0, ["crafting_table"]);
        Add("production", 61, 0, ["furnace"]);
        Add("production", 117, 0, ["block"], 379, 0);
        Add("production", 118, 0, ["block"], 380, 0);
        Add("production", 116, 0, ["block"]);
        Add("production", 145, 0, ["block"]);
        // Storage.
        Add("storage", 54, 0, ["chest"]);
        Add("storage", 146, 0, ["block"]);
        Add("storage", 154, 0, ["block"]);
        // Lighting.
        Add("lighting", 50, 0, ["torch"]);
        Add("lighting", 89, 0, Solid);
        Add("lighting", 91, 0, ["block"]);
        Add("lighting", 123, 0, ["block"]);
        Add("lighting", 169, 0, Solid);
        // Misc.
        Add("misc", 65, 0, ["block"]);
        Add("misc", 170, 0, ["block"]);
        Add("misc", 86, 0, ["block"]);
        Add("misc", 103, 0, ["block"]);
        Add("misc", 80, 0, ["floor", "block"]);
        Add("misc", 79, 0, ["block"]);
        foreach (var id in new[] { 42, 41, 57, 22, 133, 173, 152, 165 }) Add("misc", id, 0, ["block"]);
        return list;
    }
}
