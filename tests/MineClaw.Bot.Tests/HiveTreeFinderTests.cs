using MineClaw.Bot.Perception.Vision;
using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;
using MineClaw.World.Chunks;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-29, a DeepSeek hive deforesting around log_chest 152,63,288 with 20 bodies: the brain asked see "list exact coordinates
/// of every tree trunk still standing within 64 blocks of 152,63,288" and got "exact: 1 grown tree" (a 12-block scan around its
/// own body at 181,61,292, in its own loaded chunks only), told its bodies the forest was gone and reported done; Fox: "I see a lot
/// of trees actually". The exact scan now covers the area the question names (≤ 64), a taller band, log2 woods, and the crew's
/// combined view (every body's chunks in the process's shared store).
/// </summary>
public class HiveTreeFinderTests
{
    private static readonly RoleMap NoRoles = (_, _, _) => BlockRole.None;
    private const string Question = "list exact coordinates of every tree trunk still standing within 64 blocks of 152,63,288";

    /// <summary>A 1.12.2 tree as the server sends it: logs axis Y (meta = variant), leaves variant with check_decay (8) on some.</summary>
    private static void Tree(WorldState w, int x, int y, int z, int logId, int variant, int leavesId, int height)
    {
        w.SetBlock(x, y - 1, z, Blocks.Grass, 0);
        for (int i = 0; i < height; i++) w.SetBlock(x, y + i, z, logId, variant);
        int top = y + height - 1;
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        for (int dy = -2; dy <= 1; dy++)
        {
            if (Math.Abs(dx) == 2 && Math.Abs(dz) == 2) continue;
            int lx = x + dx, ly = top + dy, lz = z + dz;
            if (w.GetBlock(lx, ly, lz) != 0) continue;
            w.SetBlock(lx, ly, lz, leavesId, variant | ((dx + dz) % 2 == 0 ? 8 : 0)); // natural (decayable), half flagged check_decay
        }
    }

    private static void Ground(WorldState w, int x0, int z0, int x1, int z1, int y)
    {
        for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) w.SetBlock(x, y, z, Blocks.Grass, 0);
    }

    /// <summary>The live trees: spruce, birch, oak and an acacia (log2/leaves2) 20-50 blocks from the hive's body, on a slope.</summary>
    private static void Forest(WorldState w)
    {
        Tree(w, 141, 68, 276, Blocks.Log, 1, Blocks.Leaves, 7);        // spruce
        Tree(w, 160, 64, 299, Blocks.Log, 2, Blocks.Leaves, 6);        // birch
        Tree(w, 152, 64, 301, Blocks.Log, 0, Blocks.Leaves, 5);        // oak
        Tree(w, 166, 66, 232, Blocks.Log, 1, Blocks.Leaves, 9);        // spruce up the hill (y 66, 60 m away)
        Tree(w, 119, 69, 268, Blocks.Log, 1, Blocks.Leaves, 8);        // spruce west
        Tree(w, 200, 64, 330, Wood.Log2, 0, Wood.Leaves2, 6);          // acacia
    }

    [Fact]
    public void The_scan_covers_the_named_area_and_every_wood_not_12_blocks_round_the_asking_body()
    {
        var w = new WorldState(height: 256);
        w.ApplyChunkRegion(96, 0, 224, 128, 256, 128, new byte[128 * 256 * 128], null, null, null);
        Ground(w, 100, 230, 220, 345, 62);
        Forest(w);
        var a = VisionRouter.Answer(Question, w.PlanningView, NoRoles, (181, 61, 292), 12)!;
        Assert.Contains("6 grown trees", a);
        foreach (var t in new[] { "spruce 141,68,276", "birch 160,64,299", "oak 152,64,301", "spruce 119,69,268" }) Assert.Contains(t, a);
        Assert.Contains("(within 64 of 152,63,288)", a);
        Assert.Matches(@"\+2 \((N|E|S|W) \d", a); // the rest by side, so the brain can spread its bodies

        // A bare look still means "around me": the same world, no area named.
        var near = VisionRouter.Answer("any trees here?", w.PlanningView, NoRoles, (181, 61, 292), 12)!;
        Assert.Contains("no grown tree within 12", near);
    }

    [Fact]
    public void The_hive_body_sees_trees_its_sibling_bodies_loaded_through_the_shared_chunk_store()
    {
        var store = new SharedChunkStore(ChunkColumn.MaxHeight);
        var hive = new WorldState(height: 256, chunkStore: store);   // the brain's own body: view around 181,292 only
        var body = new WorldState(height: 256, chunkStore: store);   // a sibling standing in the forest
        hive.ApplyChunkRegion(176, 0, 288, 16, 256, 16, new byte[16 * 256 * 16], null, null, null);
        body.ApplyChunkRegion(112, 0, 224, 64, 256, 96, new byte[64 * 256 * 96], null, null, null);
        Ground(body, 112, 224, 175, 319, 62);
        Tree(body, 141, 68, 276, Blocks.Log, 1, Blocks.Leaves, 7);
        Tree(body, 160, 64, 299, Blocks.Log, 2, Blocks.Leaves, 6);
        Tree(body, 152, 64, 301, Blocks.Log, 0, Blocks.Leaves, 5);

        Assert.False(hive.IsLoaded(141, 276));                         // not in the hive body's own view
        Assert.Equal(Blocks.Log, hive.PlanningView.GetBlock(141, 68, 276)); // but in the crew's
        var a = VisionRouter.Answer(Question, hive.PlanningView, NoRoles, (181, 61, 292), 12)!;
        Assert.Contains("3 grown trees", a);
        Assert.Contains("birch 160,64,299", a);
        // 1.12.2 metas (variant logs, check_decay leaves) make living trees for the chop/census scan.
        var trees = TreeScan.FindTrees(body, new Vec3d(152, 64, 288), 40).Where(TreeStewardship.IsLiving).ToList();
        Assert.Equal(3, trees.Count);
        Assert.Equal(new[] { "birch", "oak", "spruce" }, trees.Select(t => t.WoodName).OrderBy(n => n));

        // A sibling leaving gives its chunks back: nothing stale stays visible.
        body.Dispose();
        Assert.Equal(0, hive.PlanningView.GetBlock(141, 68, 276));
    }

    [Theory]
    [InlineData("trees within 64 of 152,63,288", 152, 63, 288, 64)]
    [InlineData("any tree trunks still standing within 64 of 152,63,288? exact coords", 152, 63, 288, 64)]
    [InlineData("trees standing within 128 blocks around 152,63,288", 152, 63, 288, 64)]
    [InlineData("where are trees in 40 blocks", 1, 2, 3, 40)]
    [InlineData("are there trees near here", 1, 2, 3, 12)]
    public void The_question_names_the_area(string q, int x, int y, int z, int r)
    {
        var (c, ar) = VisionRouter.AskedArea(q, (1, 2, 3), 12);
        Assert.Equal((x, y, z), c);
        Assert.Equal(r, ar);
    }
}
