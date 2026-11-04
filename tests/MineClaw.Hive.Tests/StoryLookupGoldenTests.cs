using System.Text;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using static MineClaw.Hive.Tests.StoryGoldenTests;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Behaviour pins for the Story director's small lookups (docs/dev/complexity.md WP01), every input on both versions: what a block
/// needs to dig, an item's tier and armour rank, tree bases, crops and soil. Goldens written by the code before it was reshaped.
/// </summary>
public sealed class StoryLookupGoldenTests
{
    private static readonly IGameData[] Versions = [Beta181GameData.Instance, Release1122GameData.Instance];

    [Fact]
    public void Tool_needs_tiers_and_ranks()
    {
        var sb = new StringBuilder();
        foreach (var d in Versions)
        {
            for (int id = -1; id <= 260; id++)
            {
                var n = StoryTools.For(d, id);
                sb.Append(d.VersionId).Append(" need ").Append(id).Append(' ').Append(n.Kind).Append(' ').Append(n.Floor).Append(' ')
                    .Append(n.Fast).Append(' ').Append(n.Keep).Append(' ').Append(n.Text).Append('\n');
            }
            for (int id = -1; id <= 2300; id++)
                if (StoryTools.ItemTier(d, id) is { } t) sb.Append(d.VersionId).Append(" tier ").Append(id).Append(' ').Append(t).Append('\n');
        }
        for (int id = -1; id <= 4096; id++)
            if (StoryTools.ArmorRank(id) is var r and not 0) sb.Append("rank ").Append(id).Append(' ').Append(r).Append('\n');
        GoldenHash("tool-needs", sb.ToString());
    }

    [Fact]
    public void Produce_table_matches_the_old_switch_for_every_plant()
    {
        static (int, int) Old(StoryPlant p) => p.Id switch
        {
            "wheat" => (296, -1), "carrots" => (391, -1), "potatoes" => (392, -1), "beetroot" => (434, -1), "pumpkin" => (86, -1),
            "melon" => (360, -1), "sugar_cane" => (338, -1), "cactus" => (81, -1), "nether_wart" => (372, -1),
            _ when p.Harvest == "tree" => p.SeedMeta < 4 ? (17, p.SeedMeta) : (162, p.SeedMeta - 4),
            _ => (p.SeedId, -1),
        };
        foreach (var d in Versions)
            foreach (var p in StoryPlants.For(d))
                foreach (var q in new[] { p, p with { Id = "x" }, p with { Harvest = "tree" }, p with { Harvest = "tree", SeedMeta = 5 } })
                    Assert.Equal(Old(q), StoryPlants.Produce(q));
    }

    [Fact]
    public void Enchant_preference_matches_the_old_switch_for_every_block_name()
    {
        static string Old(string material) => material.ToLowerInvariant() switch
        {
            var m when m.Contains("coal") || m.Contains("diamond") || m.Contains("emerald") || m.Contains("lapis") || m.Contains("redstone")
                       || m.Contains("quartz") => "fortune",
            var m when m.Contains("glass") || m.Contains("ice") || m.Contains("glowstone") || m.Contains("ender_chest") || m.Contains("grass")
                       || m.Contains("mycelium") || m.Contains("bookshel") => "silk_touch",
            _ => "efficiency",
        };
        var names = new List<string> { "", "Diamond_Ore", "group:ores", "PACKED_ICE", "x" };
        foreach (var d in Versions)
            for (int id = 0; id <= 2300; id++)
            {
                if (d.BlockExists(id)) names.Add(d.Block(id).Name);
                if (d.ItemExists(id)) names.Add(d.Item(id).Name);
            }
        foreach (var n in names) Assert.Equal(Old(n), StoryEnchanting.PreferFor(n));
    }

    /// <summary>A small forest: plain trees, a 2x2 trunk, a tree with branches and a crown, a floating log, leaves on their own, a
    /// stump over water; every block's tree base, the trees of several boxes, and every crop state.</summary>
    [Fact]
    public void Tree_bases_trees_in_areas_and_crops()
    {
        var w = new FakeStoryWorld();
        w.Tree(0, 0); w.Tree(3, 0, 6); w.Tree(-4, 2, 2);
        foreach (var (x, z) in new[] { (8, 8), (9, 8), (8, 9), (9, 9) }) w.Tree(x, z, 7); // a 2x2 trunk
        for (int y = 64; y < 70; y++) w.Set(0, y, 8, 17); // a tree with branches and a crown
        w.Set(1, 67, 8, 17); w.Set(2, 68, 8, 17); w.Set(-1, 68, 8, 17); w.Set(0, 69, 9, 17);
        for (int dx = -2; dx <= 2; dx++) for (int dz = 6; dz <= 10; dz++) if (w.Block(dx, 70, dz).Id == 0) w.Set(dx, 70, dz, 18);
        w.Set(5, 70, 5, 17); // a floating log
        w.Set(6, 66, -3, 18); w.Set(6, 67, -3, 161); // leaves on their own
        w.Set(-6, 63, -6, 9); w.Set(-6, 64, -6, 17); w.Set(-6, 65, -6, 17); // a stump over water
        var sb = new StringBuilder();
        for (int x = -7; x <= 10; x++)
            for (int z = -4; z <= 10; z++)
                for (int y = 63; y <= 72; y++)
                    if (StoryBlocks.TreeBaseOf(w, new P3(x, y, z)) is { } b) sb.Append("base ").Append(new P3(x, y, z)).Append(" => ").Append(b).Append('\n');
        foreach (var a in new[] { Area.Of(-7, 64, -4, 10, 72, 10), Area.Of(-1, 70, 6, 1, 70, 10), Area.Of(8, 66, 8, 9, 66, 9), Area.Of(4, 60, 4, 6, 72, 6),
                     Area.Of(-7, 64, -7, -5, 66, -5), Area.Of(-3, 71, -3, 3, 71, 3) })
            foreach (var (below, max) in new[] { (32, 512), (0, 512), (32, 2) })
                sb.Append("trees ").Append(a.Json().ToJsonString()).Append(' ').Append(below).Append(' ').Append(max).Append(" => ")
                    .Append(string.Join(" ", StoryBlocks.TreesIn(w, a, below, max))).Append('\n');
        var cw = new FakeStoryWorld(Release1122GameData.Instance);
        foreach (var id in new[] { 0, 1, 59, 141, 142, 207, 115, 127, 103, 86, 83, 81, 17 })
            for (int meta = 0; meta < 16; meta++)
                foreach (var under in new[] { 0, id })
                {
                    cw.Set(0, 64, 0, id, meta);
                    cw.Set(0, 63, 0, under);
                    var c = StoryBlocks.Crop(cw, 0, 64, 0);
                    sb.Append("crop ").Append(id).Append(':').Append(meta).Append(" on ").Append(under).Append(" => ")
                        .Append(c is { } cc ? $"{cc.Ripe} {cc.Replant}" : "-").Append('\n');
                }
        Golden("trees-crops", sb.ToString());
    }
}
