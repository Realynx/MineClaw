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
    public static readonly IReadOnlyList<string> Tabs = ["structure", "furniture", "production", "storage", "lighting", "water", "misc"];

    private static readonly string[] Solid = ["wall", "floor", "block", "roof"];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<ArchitectEntry>> Cache = new();

    public static IReadOnlyList<ArchitectEntry> Entries(IGameData d) => Cache.GetOrAdd(d.VersionId, _ => Build(d));

    private static IReadOnlyList<ArchitectEntry> Build(IGameData d)
    {
        var b = new PaletteBuilder(d);
        StructureBlocks(b);
        StructureShapes(b, modern: d.BlockExists(126));
        FurnitureToMisc(b);
        return b.List;
    }

    /// <summary>The palette as it is filled: each placeable once (by item and variant), only what the version has.</summary>
    private sealed class PaletteBuilder(IGameData d)
    {
        public readonly List<ArchitectEntry> List = new();
        private readonly HashSet<(int, int)> _seen = new();

        public void Add(string tab, int block, int meta, string[] kinds, int? item = null, int? itemMeta = null)
        {
            int it = item ?? block, im = itemMeta ?? meta;
            if (!d.BlockExists(block) || !d.ItemExists(it)) return;
            if (!VariantKnown(it, im)) return;
            if (!_seen.Add((it, im))) return;
            string id = im > 0 ? d.VariantName(it, im) : d.Item(it).Name;
            List.Add(new ArchitectEntry(id, StoryRecipes.Label(d, it, im), tab, it, im, block, meta, kinds));
        }

        /// <summary>Metas <paramref name="from"/> … from + count - 1 of one block.</summary>
        public void Metas(string tab, int block, int from, int count, string[] kinds)
        {
            foreach (var m in Enumerable.Range(from, count)) Add(tab, block, m, kinds);
        }

        /// <summary>Plain blocks (meta 0) of one tab and kinds.</summary>
        public void Blocks(string tab, IEnumerable<int> blocks, string[] kinds)
        {
            foreach (var id in blocks) Add(tab, id, 0, kinds);
        }

        /// <summary>A variant the version names (its name reads back to the same item and damage); meta 0 always.</summary>
        private bool VariantKnown(int it, int im) =>
            im <= 0 || d.ItemByNameWithDamage(d.VariantName(it, im)) is { } b2 && b2.Item.Id == it && b2.Damage == im;
    }

    /// <summary>Structure: building blocks, stone and brick variants, logs, wool and clay, glass.</summary>
    private static void StructureBlocks(PaletteBuilder b)
    {
        b.Metas("structure", 5, 0, 6, Solid);
        b.Blocks("structure", [4, 1, 98, 45, 24, 48, 112, 155, 179, 201, 168, 172, 82, 3, 12, 13, 49, 87, 121, 206], Solid);
        b.Metas("structure", 1, 1, 6, Solid);
        b.Metas("structure", 98, 1, 3, Solid);
        b.Metas("structure", 17, 0, 4, Solid);
        b.Metas("structure", 162, 0, 2, Solid);
        b.Metas("structure", 35, 0, 16, Solid);
        b.Metas("structure", 159, 0, 16, Solid);
        b.Metas("structure", 251, 0, 16, Solid);
        b.Add("structure", 20, 0, ["window", "wall", "block"]);
        b.Metas("structure", 95, 0, 16, ["window", "wall", "block"]);
        b.Add("structure", 102, 0, ["window", "block"]);
        b.Metas("structure", 160, 0, 16, ["window", "block"]);
    }

    /// <summary>Structure: stairs, slabs, fences, gates, doors, trapdoors.</summary>
    private static void StructureShapes(PaletteBuilder b, bool modern)
    {
        b.Blocks("structure", [53, 134, 135, 136, 163, 164, 67, 108, 109, 114, 128, 156, 180, 203], ["stairs"]);
        if (modern) b.Metas("structure", 126, 0, 6, ["floor", "block", "roof"]);
        b.Metas("structure", 44, 0, 8, ["floor", "block", "roof"]);
        b.Blocks("structure", [85, 188, 189, 190, 191, 192, 113], ["fence"]);
        b.Blocks("structure", [107, 183, 184, 185, 186, 187], ["block"]);
        b.Add("structure", 64, 0, ["door"], 324, 0);
        foreach (var (blk, it) in new[] { (193, 427), (194, 428), (195, 429), (196, 430), (197, 431) }) b.Add("structure", blk, 0, ["door"], it, 0);
        b.Add("structure", 71, 0, ["door"], 330, 0);
        b.Add("structure", 96, 0, ["block"]);
        b.Add("structure", 167, 0, ["block"]);
        b.Add("structure", 101, 0, ["window", "block"]);
    }

    /// <summary>Furniture, production, storage, lighting and misc.</summary>
    private static void FurnitureToMisc(PaletteBuilder b)
    {
        b.Add("furniture", 26, 0, ["bed"], 355, 0);
        b.Add("furniture", 47, 0, Solid);
        b.Metas("furniture", 171, 0, 16, ["floor", "block"]);
        b.Add("furniture", 84, 0, ["block"]);
        b.Add("furniture", 25, 0, ["block"]);
        b.Add("furniture", 140, 0, ["block"], 390, 0);
        b.Add("production", 58, 0, ["crafting_table"]);
        b.Add("production", 61, 0, ["furnace"]);
        b.Add("production", 117, 0, ["block"], 379, 0);
        b.Add("production", 118, 0, ["block"], 380, 0);
        b.Add("production", 116, 0, ["block"]);
        b.Add("production", 145, 0, ["block"]);
        b.Add("storage", 54, 0, ["chest"]);
        b.Add("storage", 146, 0, ["block"]);
        b.Add("storage", 154, 0, ["block"]);
        b.Add("lighting", 50, 0, ["torch"]);
        b.Add("lighting", 89, 0, Solid);
        b.Add("lighting", 91, 0, ["block"]);
        b.Add("lighting", 123, 0, ["block"]);
        b.Add("lighting", 169, 0, Solid);
        b.Add("misc", 65, 0, ["block"]);
        b.Add("misc", 170, 0, ["block"]);
        b.Add("misc", 86, 0, ["block"]);
        b.Add("misc", 103, 0, ["block"]);
        b.Add("misc", 80, 0, ["floor", "block"]);
        b.Add("misc", 79, 0, ["block"]);
        b.Blocks("misc", [42, 41, 57, 22, 133, 173, 152, 165], ["block"]);
    }
}
