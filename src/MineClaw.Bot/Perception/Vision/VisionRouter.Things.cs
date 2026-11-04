using System.Text.RegularExpressions;
using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Perception.Vision;

public static partial class VisionRouter
{
    /// <summary>Trees (TreeScan: logs, leaves, soil), saplings, crops, chests, doors, stations and ores near the focus.</summary>
    private static string Things(IBlockAccess w, (int X, int Y, int Z) f, int r, string question)
    {
        var scan = ThingsScan.Of(w, f, r);
        var q = new ThingsQuestion(question.ToLowerInvariant());
        var parts = new List<string>();
        if (q.Asked("tree", "log", "trunk", "canopy", "wood")) TreeParts(w, scan, f, r, parts);
        if (q.Asked("sapling", "tree")) parts.Add(Saplings(w, scan));
        if (q.Asked("crop", "wheat", "farm", "ripe", "unripe", "grown", "mature", "harvest", "replant"))
            parts.Add(Crops(scan.Crops, scan.Farmland, scan.List));
        if (q.Asked("chest") && Chests(w, scan.Chests, scan.Distance) is { } ch) parts.Add(ch);
        else if (q.Asked("chest") && !q.All) parts.Add("no chest");
        NamedParts(scan, q, parts);
        if (q.AsksChestContents) parts.Add("contents aren't visible from outside: open it or check the chest memory");
        return parts.Count == 0 ? $"nothing of that within {r} of {P(f)}" : string.Join(" | ", parts) + $" (within {r} of {P(f)})";
    }

    /// <summary>Saplings planted on dirt or grass.</summary>
    private static string Saplings(IBlockAccess w, ThingsScan scan)
    {
        var planted = scan.Saplings.Where(s => w.GetBlock(s.X, s.Y - 1, s.Z) is Blocks.Dirt or Blocks.Grass).ToList();
        return planted.Count == 0 ? "no planted sapling" : $"{planted.Count} sapling{(planted.Count == 1 ? "" : "s")}: " + scan.List(planted);
    }

    /// <summary>A Things question: which kinds it names (none named = all of them).</summary>
    private sealed class ThingsQuestion(string lower)
    {
        public string Text { get; } = lower;
        public bool All { get; } = !ThingsRx().IsMatch(lower);

        public bool Asked(params string[] words) => All || words.Any(word => Regex.IsMatch(Text, $@"\b{word}"));

        /// <summary>What's inside a chest: never visible from outside.</summary>
        public bool AsksChestContents =>
            Text.Contains("chest") && (Text.Contains("item") || Text.Contains("in it") || Text.Contains("contain"));
    }

    /// <summary>Grown trees (ours left unfinished first), then bare log columns without a crown or soil.</summary>
    private static void TreeParts(IBlockAccess w, ThingsScan scan, (int X, int Y, int Z) f, int r, List<string> parts)
    {
        var trees = new Dictionary<BlockPos, TreeInfo>();
        var seen = new HashSet<BlockPos>();
        foreach (var l in scan.Logs.OrderBy(p => p.Y))
        {
            if (seen.Contains(l) || TreeScan.Analyze(w, l) is not { } t) continue;
            seen.UnionWith(t.Logs);
            trees.TryAdd(t.Base, t);
        }
        var grown = trees.Values.Where(TreeStewardship.IsLiving).OrderBy(t => scan.Distance(T(t.Base))).ToList();
        var bare = trees.Values.Where(t => !TreeStewardship.IsLiving(t)).OrderBy(t => scan.Distance(T(t.Base))).ToList();
        // A remnant of our own chop (it stopped part-way): ours to finish with chop_tree, not "someone's build".
        var ours = bare.Where(t => TreeScan.Unfinished(w, t) is not null).ToList();
        bare = bare.Except(ours).ToList();
        if (ours.Count > 0)
            parts.Add("our unfinished tree" + (ours.Count == 1 ? "" : "s") + " (chop_tree at= finishes it): "
                      + string.Join("; ", ours.Take(3).Select(t => $"{t.WoodName} {P(T(t.Base))} {t.Logs.Count} logs left")));
        parts.Add(GrownTrees(grown, f, r));
        if (bare.Count > 0) parts.Add("log columns without a crown/soil: " + scan.List(bare.Select(t => T(t.Base)), 3));
    }

    private static string GrownTrees(List<TreeInfo> grown, (int X, int Y, int Z) f, int r) =>
        grown.Count == 0 ? $"no grown tree within {r}"
        : $"{grown.Count} grown tree{(grown.Count == 1 ? "" : "s")}: " + string.Join("; ", grown.Take(4).Select(t => $"{t.WoodName} {P(T(t.Base))} {t.Logs.Count} logs {t.Leaves.Count} leaves"))
          + (grown.Count > 4 ? $" +{grown.Count - 4} ({Sides(grown.Select(t => T(t.Base)), f)})" : "");

    private static (int X, int Y, int Z) T(BlockPos p) => (p.X, p.Y, p.Z);

    /// <summary>The named kinds a question can ask for, with the words that ask for each.</summary>
    private static readonly (string Key, string[] Words)[] NamedKinds =
    [
        ("door", ["door"]), ("furnace", ["furnace"]), ("bench", ["bench", "workbench", "crafting"]),
        ("coal ore", ["ore", "coal"]), ("iron ore", ["ore", "iron"]), ("gold ore", ["ore", "gold"]),
        ("diamond ore", ["ore", "diamond"]), ("lapis ore", ["ore", "lapis"]), ("redstone ore", ["ore", "redstone"]),
    ];

    /// <summary>Doors, stations and ores: where they are, or (when asked by name) that there are none.</summary>
    private static void NamedParts(ThingsScan scan, ThingsQuestion q, List<string> parts)
    {
        foreach (var (key, words) in NamedKinds)
        {
            if (!q.Asked(words)) continue;
            if (scan.Named.TryGetValue(key, out var l)) parts.Add($"{key}{(l.Count == 1 ? "" : "s")} {scan.List(l, 3)}");
            else if (!q.All && !words.Contains("ore")) parts.Add($"no {key}");
            else if (!q.All && key == "coal ore" && !scan.Named.Keys.Any(k => k.EndsWith(" ore"))) parts.Add("no ore");
        }
    }

    /// <summary>Block ids reported under a name (doors are added separately: only their lower half counts).</summary>
    private static readonly Dictionary<int, string> NamedIds = new()
    {
        [Blocks.Furnace] = "furnace", [Blocks.LitFurnace] = "furnace", [Blocks.Workbench] = "bench",
        [Blocks.CoalOre] = "coal ore", [Blocks.IronOre] = "iron ore", [Blocks.GoldOre] = "gold ore", [Blocks.DiamondOre] = "diamond ore",
        [Blocks.LapisOre] = "lapis ore", [Blocks.RedstoneOre] = "redstone ore", [Blocks.LitRedstoneOre] = "redstone ore",
    };

    /// <summary>One sweep of the area a Things question covers: every kind it reports, by position.</summary>
    private sealed class ThingsScan((int X, int Y, int Z) f)
    {
        public readonly List<BlockPos> Logs = new();
        public readonly List<(int X, int Y, int Z)> Saplings = new();
        public readonly List<(int X, int Y, int Z, int Meta)> Crops = new();
        public readonly List<(int X, int Y, int Z)> Farmland = new();
        public readonly Dictionary<string, List<(int X, int Y, int Z)>> Named = new();
        public readonly List<BlockPos> Chests = new();

        public static ThingsScan Of(IBlockAccess w, (int X, int Y, int Z) f, int r)
        {
            var scan = new ThingsScan(f);
            // A wide area spans hills and valleys (1.12.2 terrain): a taller band than a 12-block look.
            int below = r > 12 ? 20 : 8, above = r > 12 ? 28 : 12;
            for (int y = Math.Max(0, f.Y - below), y1 = Math.Min(w.WorldHeight - 1, f.Y + above); y <= y1; y++)
                for (int z = f.Z - r; z <= f.Z + r; z++)
                    for (int x = f.X - r; x <= f.X + r; x++)
                    {
                        int id = w.GetBlock(x, y, z);
                        if (id != 0) scan.Note(w, x, y, z, id);
                    }
            return scan;
        }

        private void Note(IBlockAccess w, int x, int y, int z, int id)
        {
            switch (id)
            {
                case Blocks.Log or Wood.Log2: Logs.Add(new BlockPos(x, y, z)); break;
                case Blocks.Sapling: Saplings.Add((x, y, z)); break;
                case Blocks.Crops: Crops.Add((x, y, z, w.GetMeta(x, y, z))); break;
                case Blocks.Farmland: if (w.GetBlock(x, y + 1, z) == 0) Farmland.Add((x, y, z)); break;
                case ChestPairs.Chest or ChestPairs.TrappedChest: Chests.Add(new BlockPos(x, y, z)); break;
                default: if (NamedAt(w, x, y, z, id) is { } key) Add(key, (x, y, z)); break;
            }
        }

        private static string? NamedAt(IBlockAccess w, int x, int y, int z, int id)
        {
            if (id is Blocks.WoodenDoor or Blocks.IronDoor) return (w.GetMeta(x, y, z) & 8) == 0 ? "door" : null;
            return NamedIds.GetValueOrDefault(id);
        }

        private void Add(string k, (int, int, int) p)
        {
            if (!Named.TryGetValue(k, out var l)) Named[k] = l = new();
            l.Add(p);
        }

        public int Distance((int X, int Y, int Z) c) => Math.Abs(c.X - f.X) + Math.Abs(c.Z - f.Z) + Math.Abs(c.Y - f.Y);

        /// <summary>Nearest first, at most <paramref name="max"/>, then "+N".</summary>
        public string List(IEnumerable<(int X, int Y, int Z)> cs, int max = 4)
        {
            var l = cs.OrderBy(Distance).ToList();
            return string.Join(" ", l.Take(max).Select(P)) + (l.Count > max ? $" +{l.Count - max}" : "");
        }
    }
}
