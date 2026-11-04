using MineClaw.Bot.Perception.Vision;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins <see cref="VisionRouter"/>'s Things answers (WP03 refactor) word for word over a world holding every kind it reports:
/// grown and bare trees, saplings, crops at every stage, empty farmland, single/double/trapped chests, doors, stations and ores.
/// </summary>
public class VisionThingsGoldenTests
{
    private static WorldState World()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-48, 0, -48, 96, 128, 96, new byte[96 * 128 * 96], null, null, null);
        for (int x = -20; x <= 20; x++) for (int z = -20; z <= 20; z++) w.SetBlock(x, 63, z, Blocks.Grass, 0);
        void Tree(int x, int z, int h)
        {
            for (int y = 64; y < 64 + h; y++) w.SetBlock(x, y, z, Blocks.Log, 0);
            for (int dx = -2; dx <= 2; dx++) for (int dz = -2; dz <= 2; dz++) for (int y = 62 + h; y <= 64 + h; y++)
                if (w.GetBlock(x + dx, y, z + dz) == 0) w.SetBlock(x + dx, y, z + dz, Blocks.Leaves, 0);
        }
        Tree(5, 5, 5);
        Tree(-9, 7, 6);
        Tree(12, -10, 5);
        Tree(-14, -14, 4);
        Tree(16, 14, 5);
        for (int y = 64; y <= 66; y++) w.SetBlock(-5, y, -5, Blocks.Log, 0); // a bare log column
        w.SetBlock(1, 64, 1, Blocks.Sapling, 0);
        w.SetBlock(2, 64, -3, Blocks.Sapling, 0);
        w.SetBlock(-3, 65, 9, Blocks.Sapling, 0); // floating: not planted
        for (int i = 0; i < 8; i++)
        {
            w.SetBlock(-10 + i, 63, -2, Blocks.Farmland, 0);
            w.SetBlock(-10 + i, 64, -2, Blocks.Crops, i);
        }
        w.SetBlock(-10, 63, -3, Blocks.Farmland, 0);
        w.SetBlock(-9, 63, -3, Blocks.Farmland, 0);
        w.SetBlock(8, 64, 2, Blocks.Chest, 0);
        w.SetBlock(2, 64, 8, Blocks.Chest, 0);
        w.SetBlock(3, 64, 8, Blocks.Chest, 0);
        w.SetBlock(4, 64, 8, ChestPairs.TrappedChest, 0);
        w.SetBlock(-1, 64, 3, Blocks.WoodenDoor, 0);
        w.SetBlock(-1, 65, 3, Blocks.WoodenDoor, 8);
        w.SetBlock(6, 64, -1, Blocks.Furnace, 0);
        w.SetBlock(7, 64, -1, Blocks.LitFurnace, 0);
        w.SetBlock(9, 64, -1, Blocks.Workbench, 0);
        int[] ores = [Blocks.CoalOre, Blocks.IronOre, Blocks.GoldOre, Blocks.DiamondOre, Blocks.LapisOre, Blocks.RedstoneOre, Blocks.LitRedstoneOre];
        for (int i = 0; i < ores.Length; i++) w.SetBlock(-6 + i, 60, 4, ores[i], 0);
        return w;
    }

    public static readonly string[] Questions =
    [
        "what's around here?",
        "are there grown trees near here?",
        "any logs or trunks?",
        "where are the saplings?",
        "which wheat is ripe?",
        "is the farm ready to harvest?",
        "where is the chest?",
        "what items are in the chest?",
        "any doors?",
        "where's the furnace?",
        "is there a crafting bench?",
        "any ore around?",
        "any iron or coal?",
        "diamonds or gold?",
        "lapis or redstone?",
        "trees within 40 blocks of 0,64,0",
        "chest and furnace and door",
    ];

    [Fact]
    public void Things_answers_match_the_golden_text()
    {
        var w = World();
        RoleMap roles = (_, _, _) => BlockRole.None;
        var actual = new List<string>();
        foreach (var q in Questions)
            foreach (var (focus, r) in new[] { ((0, 64, 0), 6), ((0, 64, 0), 14), ((-20, 64, 20), 8) })
                actual.Add($"{q} @{focus} r{r} => " + VisionRouter.Answer(VisionIntent.Things, q, w.PlanningView, roles, focus, r));
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) { File.WriteAllLines(Path.Combine(outDir, "vision-things.txt"), actual); return; }
        Assert.Equal(Expected, actual);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private static readonly string[] Expected =
    [
        "what's around here? @(0, 64, 0) r6 => [vision] exact: 3 grown trees: oak 5,64,5 5 logs 69 leaves; oak -9,64,7 6 logs 69 leaves; oak 12,64,-10 5 logs 69 leaves | log columns without a crown/soil: -5,64,-5 | 2 saplings: 1,64,1 2,64,-3 | wheat 8: 1 ripe (7/7) -3,64,-2; 7 growing (stage 0-6/7, leave them); empty farmland 2 (plant seeds): -9,63,-3 -10,63,-3; crops are always harvestable when ripe (skill harvest_wheat) | 3 chests: chest 8,64,2, double chest 2…",
        "what's around here? @(0, 64, 0) r14 => [vision] exact: 4 grown trees: oak 5,64,5 5 logs 69 leaves; oak -9,64,7 6 logs 69 leaves; oak 12,64,-10 5 logs 69 leaves; oak -14,64,-14 4 logs 69 leaves | log columns without a crown/soil: -5,64,-5 | 2 saplings: 1,64,1 2,64,-3 | wheat 8: 1 ripe (7/7) -3,64,-2; 7 growing (stage 0-6/7, leave them); empty farmland 2 (plant seeds): -9,63,-3 -10,63,-3; crops are always harvestable when ripe (skill harvest_wheat) | 3 che…",
        "what's around here? @(-20, 64, 20) r8 => [vision] exact: no grown tree within 12 | no planted sapling | no crops (within 12 of -20,64,20)",
        "are there grown trees near here? @(0, 64, 0) r6 => [vision] exact: 3 grown trees: oak 5,64,5 5 logs 69 leaves; oak -9,64,7 6 logs 69 leaves; oak 12,64,-10 5 logs 69 leaves | log columns without a crown/soil: -5,64,-5 | 2 saplings: 1,64,1 2,64,-3 | wheat 8: 1 ripe (7/7) -3,64,-2; 7 growing (stage 0-6/7, leave them); empty farmland 2 (plant seeds): -9,63,-3 -10,63,-3; crops are always harvestable when ripe (skill harvest_wheat) (within 12 of 0,64,0)",
        "are there grown trees near here? @(0, 64, 0) r14 => [vision] exact: 4 grown trees: oak 5,64,5 5 logs 69 leaves; oak -9,64,7 6 logs 69 leaves; oak 12,64,-10 5 logs 69 leaves; oak -14,64,-14 4 logs 69 leaves | log columns without a crown/soil: -5,64,-5 | 2 saplings: 1,64,1 2,64,-3 | wheat 8: 1 ripe (7/7) -3,64,-2; 7 growing (stage 0-6/7, leave them); empty farmland 2 (plant seeds): -9,63,-3 -10,63,-3; crops are always harvestable when ripe (skill harvest_wheat) (within…",
        "are there grown trees near here? @(-20, 64, 20) r8 => [vision] exact: no grown tree within 12 | no planted sapling | no crops (within 12 of -20,64,20)",
        "any logs or trunks? @(0, 64, 0) r6 => [vision] exact: 3 grown trees: oak 5,64,5 5 logs 69 leaves; oak -9,64,7 6 logs 69 leaves; oak 12,64,-10 5 logs 69 leaves | log columns without a crown/soil: -5,64,-5 (within 12 of 0,64,0)",
        "any logs or trunks? @(0, 64, 0) r14 => [vision] exact: 4 grown trees: oak 5,64,5 5 logs 69 leaves; oak -9,64,7 6 logs 69 leaves; oak 12,64,-10 5 logs 69 leaves; oak -14,64,-14 4 logs 69 leaves | log columns without a crown/soil: -5,64,-5 (within 14 of 0,64,0)",
        "any logs or trunks? @(-20, 64, 20) r8 => [vision] exact: no grown tree within 12 (within 12 of -20,64,20)",
        "where are the saplings? @(0, 64, 0) r6 => [vision] exact: 2 saplings: 1,64,1 2,64,-3 (within 12 of 0,64,0)",
        "where are the saplings? @(0, 64, 0) r14 => [vision] exact: 2 saplings: 1,64,1 2,64,-3 (within 14 of 0,64,0)",
        "where are the saplings? @(-20, 64, 20) r8 => [vision] exact: no planted sapling (within 12 of -20,64,20)",
        "which wheat is ripe? @(0, 64, 0) r6 => [vision] exact: wheat 8: 1 ripe (7/7) -3,64,-2; 7 growing (stage 0-6/7, leave them); empty farmland 2 (plant seeds): -9,63,-3 -10,63,-3; crops are always harvestable when ripe (skill harvest_wheat) (within 12 of 0,64,0)",
        "which wheat is ripe? @(0, 64, 0) r14 => [vision] exact: wheat 8: 1 ripe (7/7) -3,64,-2; 7 growing (stage 0-6/7, leave them); empty farmland 2 (plant seeds): -9,63,-3 -10,63,-3; crops are always harvestable when ripe (skill harvest_wheat) (within 14 of 0,64,0)",
        "which wheat is ripe? @(-20, 64, 20) r8 => [vision] exact: no crops (within 12 of -20,64,20)",
        "is the farm ready to harvest? @(0, 64, 0) r6 => [vision] exact: wheat 8: 1 ripe (7/7) -3,64,-2; 7 growing (stage 0-6/7, leave them); empty farmland 2 (plant seeds): -9,63,-3 -10,63,-3; crops are always harvestable when ripe (skill harvest_wheat) (within 12 of 0,64,0)",
        "is the farm ready to harvest? @(0, 64, 0) r14 => [vision] exact: wheat 8: 1 ripe (7/7) -3,64,-2; 7 growing (stage 0-6/7, leave them); empty farmland 2 (plant seeds): -9,63,-3 -10,63,-3; crops are always harvestable when ripe (skill harvest_wheat) (within 14 of 0,64,0)",
        "is the farm ready to harvest? @(-20, 64, 20) r8 => [vision] exact: no crops (within 12 of -20,64,20)",
        "where is the chest? @(0, 64, 0) r6 => [vision] exact: 3 chests: chest 8,64,2, double chest 2,64,8-3,64,8, trapped chest 4,64,8 (within 12 of 0,64,0)",
        "where is the chest? @(0, 64, 0) r14 => [vision] exact: 3 chests: chest 8,64,2, double chest 2,64,8-3,64,8, trapped chest 4,64,8 (within 14 of 0,64,0)",
        "where is the chest? @(-20, 64, 20) r8 => [vision] exact: no chest (within 12 of -20,64,20)",
        "what items are in the chest? @(0, 64, 0) r6 => [vision] exact: 3 chests: chest 8,64,2, double chest 2,64,8-3,64,8, trapped chest 4,64,8 | contents aren't visible from outside: open it or check the chest memory (within 12 of 0,64,0)",
        "what items are in the chest? @(0, 64, 0) r14 => [vision] exact: 3 chests: chest 8,64,2, double chest 2,64,8-3,64,8, trapped chest 4,64,8 | contents aren't visible from outside: open it or check the chest memory (within 14 of 0,64,0)",
        "what items are in the chest? @(-20, 64, 20) r8 => [vision] exact: no chest | contents aren't visible from outside: open it or check the chest memory (within 12 of -20,64,20)",
        "any doors? @(0, 64, 0) r6 => [vision] exact: door -1,64,3 (within 12 of 0,64,0)",
        "any doors? @(0, 64, 0) r14 => [vision] exact: door -1,64,3 (within 14 of 0,64,0)",
        "any doors? @(-20, 64, 20) r8 => [vision] exact: no door (within 12 of -20,64,20)",
        "where's the furnace? @(0, 64, 0) r6 => [vision] exact: furnaces 6,64,-1 7,64,-1 (within 12 of 0,64,0)",
        "where's the furnace? @(0, 64, 0) r14 => [vision] exact: furnaces 6,64,-1 7,64,-1 (within 14 of 0,64,0)",
        "where's the furnace? @(-20, 64, 20) r8 => [vision] exact: no furnace (within 12 of -20,64,20)",
        "is there a crafting bench? @(0, 64, 0) r6 => [vision] exact: 3 grown trees: oak 5,64,5 5 logs 69 leaves; oak -9,64,7 6 logs 69 leaves; oak 12,64,-10 5 logs 69 leaves | log columns without a crown/soil: -5,64,-5 | 2 saplings: 1,64,1 2,64,-3 | wheat 8: 1 ripe (7/7) -3,64,-2; 7 growing (stage 0-6/7, leave them); empty farmland 2 (plant seeds): -9,63,-3 -10,63,-3; crops are always harvestable when ripe (skill harvest_wheat) | 3 chests: chest 8,64,2, double chest 2…",
        "is there a crafting bench? @(0, 64, 0) r14 => [vision] exact: 4 grown trees: oak 5,64,5 5 logs 69 leaves; oak -9,64,7 6 logs 69 leaves; oak 12,64,-10 5 logs 69 leaves; oak -14,64,-14 4 logs 69 leaves | log columns without a crown/soil: -5,64,-5 | 2 saplings: 1,64,1 2,64,-3 | wheat 8: 1 ripe (7/7) -3,64,-2; 7 growing (stage 0-6/7, leave them); empty farmland 2 (plant seeds): -9,63,-3 -10,63,-3; crops are always harvestable when ripe (skill harvest_wheat) | 3 che…",
        "is there a crafting bench? @(-20, 64, 20) r8 => [vision] exact: no grown tree within 12 | no planted sapling | no crops (within 12 of -20,64,20)",
        "any ore around? @(0, 64, 0) r6 => [vision] exact: coal ore -6,60,4 | iron ore -5,60,4 | gold ore -4,60,4 | diamond ore -3,60,4 | lapis ore -2,60,4 | redstone ores 0,60,4 -1,60,4 (within 12 of 0,64,0)",
        "any ore around? @(0, 64, 0) r14 => [vision] exact: coal ore -6,60,4 | iron ore -5,60,4 | gold ore -4,60,4 | diamond ore -3,60,4 | lapis ore -2,60,4 | redstone ores 0,60,4 -1,60,4 (within 14 of 0,64,0)",
        "any ore around? @(-20, 64, 20) r8 => [vision] exact: no ore (within 12 of -20,64,20)",
        "any iron or coal? @(0, 64, 0) r6 => [vision] exact: coal ore -6,60,4 | iron ore -5,60,4 (within 12 of 0,64,0)",
        "any iron or coal? @(0, 64, 0) r14 => [vision] exact: coal ore -6,60,4 | iron ore -5,60,4 (within 14 of 0,64,0)",
        "any iron or coal? @(-20, 64, 20) r8 => [vision] exact: no ore (within 12 of -20,64,20)",
        "diamonds or gold? @(0, 64, 0) r6 => [vision] exact: gold ore -4,60,4 | diamond ore -3,60,4 (within 12 of 0,64,0)",
        "diamonds or gold? @(0, 64, 0) r14 => [vision] exact: gold ore -4,60,4 | diamond ore -3,60,4 (within 14 of 0,64,0)",
        "diamonds or gold? @(-20, 64, 20) r8 => [vision] exact: nothing of that within 12 of -20,64,20",
        "lapis or redstone? @(0, 64, 0) r6 => [vision] exact: lapis ore -2,60,4 | redstone ores 0,60,4 -1,60,4 (within 12 of 0,64,0)",
        "lapis or redstone? @(0, 64, 0) r14 => [vision] exact: lapis ore -2,60,4 | redstone ores 0,60,4 -1,60,4 (within 14 of 0,64,0)",
        "lapis or redstone? @(-20, 64, 20) r8 => [vision] exact: nothing of that within 12 of -20,64,20",
        "trees within 40 blocks of 0,64,0 @(0, 64, 0) r6 => [vision] exact: 5 grown trees: oak 5,64,5 5 logs 69 leaves; oak -9,64,7 6 logs 69 leaves; oak 12,64,-10 5 logs 69 leaves; oak -14,64,-14 4 logs 69 leaves +1 (E 3, W 2) | log columns without a crown/soil: -5,64,-5 | 2 saplings: 1,64,1 2,64,-3 (within 40 of 0,64,0)",
        "trees within 40 blocks of 0,64,0 @(0, 64, 0) r14 => [vision] exact: 5 grown trees: oak 5,64,5 5 logs 69 leaves; oak -9,64,7 6 logs 69 leaves; oak 12,64,-10 5 logs 69 leaves; oak -14,64,-14 4 logs 69 leaves +1 (E 3, W 2) | log columns without a crown/soil: -5,64,-5 | 2 saplings: 1,64,1 2,64,-3 (within 40 of 0,64,0)",
        "trees within 40 blocks of 0,64,0 @(-20, 64, 20) r8 => [vision] exact: 5 grown trees: oak 5,64,5 5 logs 69 leaves; oak -9,64,7 6 logs 69 leaves; oak 12,64,-10 5 logs 69 leaves; oak -14,64,-14 4 logs 69 leaves +1 (E 3, W 2) | log columns without a crown/soil: -5,64,-5 | 2 saplings: 1,64,1 2,64,-3 (within 40 of 0,64,0)",
        "chest and furnace and door @(0, 64, 0) r6 => [vision] exact: 3 chests: chest 8,64,2, double chest 2,64,8-3,64,8, trapped chest 4,64,8 | door -1,64,3 | furnaces 6,64,-1 7,64,-1 (within 12 of 0,64,0)",
        "chest and furnace and door @(0, 64, 0) r14 => [vision] exact: 3 chests: chest 8,64,2, double chest 2,64,8-3,64,8, trapped chest 4,64,8 | door -1,64,3 | furnaces 6,64,-1 7,64,-1 (within 14 of 0,64,0)",
        "chest and furnace and door @(-20, 64, 20) r8 => [vision] exact: no chest | no door | no furnace (within 12 of -20,64,20)",
    ];
}
