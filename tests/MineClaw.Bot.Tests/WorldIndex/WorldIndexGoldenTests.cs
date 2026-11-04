using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>
/// Pins what the world index writes (WP03 refactor): every observation row's kind, key, text, fields and confidence over a rich
/// skyblock (islands, a generator through a regen, every farm type, a tree, an unknown build, chests, a furnace, a sign, a portal),
/// then again after a round of single block changes. Rows are what the brain recalls, so the text must stay word for word.
/// </summary>
public class WorldIndexGoldenTests
{
    private static void Build(IndexFixture f)
    {
        f.Island(0, 0, 8);
        f.Island(24, 0, 3);
        f.Generator(-6, 64, -6);
        for (int x = 1; x <= 3; x++)
        for (int z = 1; z <= 2; z++) { f.Set(x, 63, z, Blocks.Farmland); f.Set(x, 64, z, Blocks.Crops, x == 1 ? 7 : 3); }
        f.Set(4, 63, 1, Blocks.Water);
        f.Set(-4, 63, -1, Blocks.Sand);
        f.Set(-4, 64, -1, Blocks.SugarCane);
        f.Set(-4, 65, -1, Blocks.SugarCane);
        f.Set(-5, 63, -1, Blocks.Water);
        f.Set(5, 63, -5, Blocks.Sand);
        f.Set(5, 64, -5, Blocks.Cactus);
        f.Set(6, 63, 5, Blocks.Farmland);
        f.Set(6, 64, 5, Blocks.MelonStem, 7);
        f.Set(7, 64, 5, Blocks.Melon);
        f.Set(-6, 63, 6, Blocks.Farmland);
        f.Set(-6, 64, 6, Blocks.PumpkinStem, 3);
        f.Set(-2, 64, -7, Blocks.BrownMushroom);
        f.Set(-1, 64, -7, Blocks.RedMushroom);
        f.Fill(-4, 64, 4, -4, 67, 4, Blocks.Log);
        f.Fill(-5, 67, 3, -3, 68, 5, Blocks.Leaves);
        f.Set(-2, 64, 5, Blocks.Sapling);
        f.Fill(2, 64, 6, 4, 66, 6, Blocks.Cobblestone);
        f.Fill(2, 64, 8, 4, 66, 8, Blocks.Planks);
        f.Set(3, 65, 7, Blocks.Glass);
        f.Set(1, 64, -3, Blocks.Chest);
        f.Set(2, 64, -3, Blocks.Chest);
        f.Set(1, 64, -4, Blocks.WallSign, 2);
        f.Feed.SignTexts[new BlockPos(1, 64, -4)] = ["§1Iron", "storage", "", ""];
        f.Set(4, 64, -3, Blocks.Furnace);
        f.Set(5, 64, -3, Blocks.Workbench);
        f.Set(24, 64, 0, Blocks.Chest);
        f.Fill(22, 64, 2, 25, 68, 2, Blocks.Obsidian);
        f.Fill(23, 65, 2, 24, 67, 2, Blocks.Portal);
    }

    private static List<string> Rows(IndexFixture f, string label)
    {
        var rows = f.Sink.Last.GroupBy(o => (o.Kind, o.Key)).Select(g => g.Last())
            .OrderBy(o => o.Kind, StringComparer.Ordinal).ThenBy(o => o.Key, StringComparer.Ordinal)
            .Select(o => $"{label} {o.Kind} {o.Key} | {o.Text} | {o.FieldsJson} | {o.Confidence:0.###}")
            .ToList();
        // The protection predicate over both islands, every cell with its own block (and as cobble, as a placement would be).
        var sb = new System.Text.StringBuilder();
        foreach (var (x0, x1) in new[] { (-9, 9), (20, 28) })
            for (int x = x0; x <= x1; x++)
            for (int z = -9; z <= 9; z++)
            for (int y = 59; y <= 70; y++)
            {
                var p = new BlockPos(x, y, z);
                int id = f.World.GetBlock(p);
                if (f.Indexer.IsProtected(p, id)) sb.Append(x).Append(',').Append(y).Append(',').Append(z).Append(' ');
                if (f.Indexer.IsProtected(p, Blocks.Cobblestone)) sb.Append('c').Append(x).Append(',').Append(y).Append(',').Append(z).Append(' ');
            }
        var dump = sb.ToString();
        rows.Add($"{label} protected {dump.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} "
                 + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dump)))[..16]);
        return rows;
    }

    private static List<string> Actual()
    {
        using var f = new IndexFixture();
        Build(f);
        f.Pump();
        var actual = Rows(f, "scan");
        f.Sink.Last.Clear();
        // A generator regen, crops growing, a wall block dug, a chest broken and one placed.
        var dig = new BlockPos(-5, 64, -6);
        f.Set(dig.X, dig.Y, dig.Z, 0);
        f.Clock.Seconds(1);
        f.Set(dig.X, dig.Y, dig.Z, Blocks.FlowingLava, 2);
        f.Clock.Seconds(0.5);
        f.Set(dig.X, dig.Y, dig.Z, Blocks.Cobblestone);
        f.Set(2, 64, 1, Blocks.Crops, 7);
        f.Set(3, 66, 6, 0);
        f.Set(2, 64, -3, 0);
        f.Set(-1, 64, 3, Blocks.Chest);
        f.Set(-5, 64, -1, Blocks.SugarCane);
        f.Clock.Seconds(30);
        f.Pump();
        actual.AddRange(Rows(f, "change"));
        return actual;
    }

    [Fact]
    public void Index_rows_match_the_golden_text()
    {
        var actual = Actual();
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) { File.WriteAllLines(Path.Combine(outDir, "world-index.txt"), actual); return; }
        Assert.Equal(Expected, actual);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private static readonly string[] Expected =
    [
        "scan chest 1,64,-3 | chest at 1,64,-3 (double) \"Iron storage\" on main island near furnace | {\"island\":\"main island\",\"label\":\"Iron storage\",\"double\":true,\"protected\":true} | 0.9",
        "scan chest 24,64,0 | chest at 24,64,0 on island 24,0 near portal | {\"island\":\"island 24,0\",\"protected\":true} | 0.9",
        "scan cobble_generator -5,64,-6 | cobble_generator at -5,64,-6 on main island near mushroom farm: makes cobblestone; dig -5,64,-6 stand -3,64,-8; unconfirmed | {\"island\":\"main island\",\"dig\":[[-5,64,-6]],\"stand\":[-3,64,-8],\"makes\":\"cobblestone\",\"confirmed\":false,\"regens\":0,\"lava\":1,\"water\":1,\"protect\":10} | 0.6",
        "scan crafting_table 5,64,-3 | crafting table at 5,64,-3 on main island near furnace | {\"island\":\"main island\",\"protected\":true} | 0.9",
        "scan farm cactus@5,64,-5 | cactus farm 1x1 at 5,64,-5 on main island near crafting table: cactus 1 (max h1), on sand | {\"island\":\"main island\",\"type\":\"cactus\",\"plants\":1,\"tallest\":1,\"soil\":\"sand\",\"water\":0,\"protected\":true} | 0.9",
        "scan farm melon@6,64,5 | melon farm 2x1 at 6,64,5 on main island near unknown build: stems 1, melon 1 | {\"island\":\"main island\",\"type\":\"melon\",\"stems\":1,\"fruit\":1,\"water\":0,\"protected\":true} | 0.9",
        "scan farm mushroom@-2,64,-7 | mushroom farm 2x1 at -2,64,-7 on main island near cobble gen: mushroom 2 | {\"island\":\"main island\",\"type\":\"mushroom\",\"plants\":2,\"water\":0} | 0.9",
        "scan farm pumpkin@-6,64,6 | pumpkin farm 1x1 at -6,64,6 on main island near tree: stems 1 | {\"island\":\"main island\",\"type\":\"pumpkin\",\"stems\":1,\"water\":0,\"protected\":true} | 0.9",
        "scan farm sugar_cane@-4,64,-1 | sugar cane farm 1x1 at -4,64,-1 on main island near cobble gen: sugar cane 2 (max h2), on sand, by water | {\"island\":\"main island\",\"type\":\"sugar_cane\",\"plants\":2,\"tallest\":2,\"soil\":\"sand\",\"water\":1,\"protected\":true} | 0.9",
        "scan farm wheat@1,63,1 | wheat farm 3x2 at 1,63,1 on main island near chest: farmland 6, crops 6 (2 ripe), by water | {\"island\":\"main island\",\"type\":\"wheat\",\"farmland\":6,\"crops\":6,\"ripe\":2,\"water\":1,\"protected\":true} | 0.9",
        "scan furnace 4,64,-3 | furnace at 4,64,-3 on main island near crafting table | {\"island\":\"main island\",\"lit\":false,\"protected\":true} | 0.9",
        "scan island 0,0 | main island 17x17 (289 cols) y60-68 at -8,60,-8..8,68,8: dirt 867 grass 277 cobblestone 22 leaves 18; void edges N17 E17 S17 W17; has chest, furnace, crafting table, cobble gen, wheat farm, sugar cane farm | {\"main\":true,\"columns\":289,\"blocks\":1227,\"top\":{\"dirt\":867,\"grass\":277,\"cobblestone\":22,\"leaves\":18},\"edges\":{\"N\":17,\"E\":17,\"S\":17,\"W\":17}} | 0.9",
        "scan island 24,0 | island 24,0 7x7 (49 cols) y60-68 at 21,60,-3..27,68,3: dirt 147 grass 49 obsidian 14 chest 1; void edges N7 E7 S7 W7; has chest, portal | {\"main\":false,\"columns\":49,\"blocks\":211,\"top\":{\"dirt\":147,\"grass\":49,\"obsidian\":14,\"chest\":1},\"edges\":{\"N\":7,\"E\":7,\"S\":7,\"W\":7}} | 0.9",
        "scan portal 23,65,2 | nether portal at 23,65,2 (frame 22,64,2..25,68,2) on island 24,0 near chest; step in at 23,65,2 | {\"island\":\"island 24,0\",\"stand\":[23,65,2],\"approach\":null,\"frame\":[[22,64,2],[25,68,2]],\"cells\":6,\"protected\":true} | 0.9",
        "scan sign 1,64,-4 | sign at 1,64,-4 \"Iron storage\" on chest@1,64,-3 | {\"island\":\"main island\",\"label\":\"Iron storage\",\"labels\":\"chest@1,64,-3\",\"protected\":true} | 0.9",
        "scan tree -4,64,4 | tree at -4,64,4 on main island near pumpkin farm: 1 trees, 3 logs, 1 saplings | {\"island\":\"main island\",\"trees\":1,\"logs\":3,\"saplings\":1,\"protected\":true} | 0.9",
        "scan unknown_structure 2,64,6 | unknown_structure 3x3x3 at 2,64,6..4,66,8 on main island near wheat farm: cobblestone 9, planks 9, glass 1; ask players what it is | {\"island\":\"main island\",\"blocks\":{\"cobblestone\":9,\"planks\":9,\"glass\":1},\"ask\":true,\"protected\":true} | 0.5",
        "scan protected 156 0A04BD32654D1C7F",
        "change chest -1,64,3 | chest at -1,64,3 on main island near tree | {\"island\":\"main island\",\"protected\":true} | 0.9",
        "change chest 1,64,-3 | chest at 1,64,-3 \"Iron storage\" on main island near furnace | {\"island\":\"main island\",\"label\":\"Iron storage\",\"protected\":true} | 0.9",
        "change cobble_generator -5,64,-6 | cobble_generator at -5,64,-6 on main island near mushroom farm: makes cobblestone; dig -5,64,-6 stand -3,64,-8; confirmed (1 regens) | {\"island\":\"main island\",\"dig\":[[-5,64,-6]],\"stand\":[-3,64,-8],\"makes\":\"cobblestone\",\"confirmed\":true,\"regens\":1,\"lava\":1,\"water\":1,\"protect\":10} | 0.95",
        "change farm sugar_cane@-4,64,-1 | sugar cane farm 2x1 at -5,64,-1 on main island near cobble gen: sugar cane 3 (max h2), on water, by water | {\"island\":\"main island\",\"type\":\"sugar_cane\",\"plants\":3,\"tallest\":2,\"soil\":\"water\",\"water\":1,\"protected\":true} | 0.9",
        "change farm wheat@1,63,1 | wheat farm 3x2 at 1,63,1 on main island near chest: farmland 6, crops 6 (3 ripe), by water | {\"island\":\"main island\",\"type\":\"wheat\",\"farmland\":6,\"crops\":6,\"ripe\":3,\"water\":1,\"protected\":true} | 0.9",
        "change island 0,0 | main island 17x17 (289 cols) y60-68 at -8,60,-8..8,68,8: dirt 867 grass 277 cobblestone 21 leaves 18; void edges N17 E17 S17 W17; has chest x2, furnace, crafting table, cobble gen, wheat farm, sugar cane farm | {\"main\":true,\"columns\":289,\"blocks\":1227,\"top\":{\"dirt\":867,\"grass\":277,\"cobblestone\":21,\"leaves\":18},\"edges\":{\"N\":17,\"E\":17,\"S\":17,\"W\":17}} | 0.9",
        "change unknown_structure 2,64,6 | unknown_structure 3x3x3 at 2,64,6..4,66,8 on main island near wheat farm: planks 9, cobblestone 8, glass 1; ask players what it is | {\"island\":\"main island\",\"blocks\":{\"planks\":9,\"cobblestone\":8,\"glass\":1},\"ask\":true,\"protected\":true} | 0.5",
        "change protected 154 477C035AFAD6F9C0",
    ];
}
