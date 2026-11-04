using System.Security.Cryptography;
using System.Text;
using MineClaw.Bot.Blueprints;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the blueprint grammar end to end (parse + normalize) over a corpus that hits every statement, every op and every error
/// text the model reads: the parsed blueprint's fields, every issue verbatim, and a hash of the voxel plan. A refactor of
/// <see cref="BlueprintParser"/> or <see cref="BlueprintNormalizer"/> must leave <see cref="BlueprintGoldenData.Expected"/> unchanged.
/// Regenerate (only for an intended change) by setting WP02_GOLDEN_OUT to a file path and running this test.
/// </summary>
public class BlueprintGoldenTests
{
    private const string Hut = """
        blueprint small cobble hut
        size 5x4x5
        facing south
        palette C=cobblestone P=planks G=glass_pane D=door:south
        layer 0
        CCCCC
        CCCCC
        CCCCC
        CCCCC
        CCCCC
        layer 1-2
        CCCCC
        C...C
        G...G
        C...C
        CCDCC
        layer 3
        PPPPP
        PPPPP
        PPPPP
        PPPPP
        PPPPP
        """;

    public static readonly string[] Corpus =
    [
        Hut,
        Hut.Replace("facing south", "facing north"),
        Hut.Replace("facing south", "facing east"),
        Hut.Replace("facing south", "front: west\nlight"),
        Hut.Replace("facing south", "facing up"),
        Hut.Replace("size 5x4x5", "size: 5 x 4 x 5"),
        Hut.Replace("size 5x4x5", "size WxHxD = 5x4x5"),
        Hut.Replace("size 5x4x5", "5x4x5"),
        Hut.Replace("size 5x4x5", "dimensions 5×4×5"),
        Hut.Replace("size 5x4x5", "size big"),
        Hut.Replace("size 5x4x5", "size 5x4x5\nsize 6x4x6"),
        Hut.Replace("size 5x4x5", "size 5x4x5\nsize oops"),
        Hut.Replace("size 5x4x5", "size 99x4x5"),
        Hut.Replace("size 5x4x5", "size 5x40x5"),
        Hut.Replace("size 5x4x5", "size 5x40x5\nkind mine"),
        Hut.Replace("size 5x4x5", "size 0x4x5"),
        Hut.Replace("size 5x4x5\n", ""),
        "```\nsize 2x1x1\npalette C=cobblestone\nlayer 0\nCC\n```",
        "palette C=cobblestone\nbox C 0,0,0 1,1,1",
        "palette C=cobblestone\nlayer 0\n" + new string('C', 70),
        "blueprint \"quoted name\"\nname 'x'\nname " + new string('n', 60) + "\nsize 1x1x1\npalette C=cobblestone\nlayer 0\nC",
        "blueprint\nsize 1x1x1\npalette C=cobblestone\nlayer 0\nC",
        "size 2x2x2\norigin 10,64,-3\npalette C=cobblestone\nbox C 0,0,0 1,1,1",
        "size 2x2x2\norigin 10,64\npalette C=cobblestone\nbox C 0,0,0 1,1,1",
        "size 3x4x3\nground 2\npalette C=cobblestone\nbox C 0,0,0 2,3,2",
        "size 3x4x3\nground 9\npalette C=cobblestone\nbox C 0,0,0 2,3,2",
        "size 3x4x3\nground deep\npalette C=cobblestone\nbox C 0,0,0 2,3,2",
        "size 3x4x3\nground 40\npalette C=cobblestone\nbox C 0,0,0 2,3,2",
        "size 3x2x3\nlit\npalette C=cobblestone T=torch\nlayer 0\nCCC\nCCC\nCCC\nlayer 1\n...\n.T.\n...",
        "version 1.12.2\nsize 2x1x1\npalette C=cobblestone P=spruce_planks\nlayer 0\nCP",
        "version b1.8.1\nsize 2x1x1\npalette C=cobblestone\nlayer 0\nCC",
        "version 9.9\nsize 2x1x1\npalette C=cobblestone\nlayer 0\nCC",
        "size 3x3x3\nkind mine\nanchor absolute 5,12,7\nentry 1,2,1\nseed abc\npreferred_for b1.8.1\nclear 0,0,0 2,2,2",
        "size 3x3x3\nkind mine\nanchor absolute 12\nclear 0,0,0 2,2,2",
        "size 3x3x3\nkind mine\nanchor absolute 0\nclear 0,0,0 2,2,2",
        "size 3x3x3\nkind mine\nanchor surface\nclear 0,0,0 2,2,2",
        "size 3x3x3\nkind mine\nanchor ground\nentry 1,2\nseed " + new string('s', 40) + "\npreferred x\nclear 0,0,0 2,2,2",
        "size 3x3x3\nkind mine\nanchor nowhere\nclear 0,0,0 2,2,2",
        "size 3x3x3\nkind castle\npalette C=cobblestone\nbox C 0,0,0 2,2,2",
        "size 2x1x1\npalette CC=cobblestone .=dirt _=dirt\nlayer 0\nCC",
        "size 4x1x1\npalette W=wool:red L=log:birch S=slab:cobble T=stone_bricks:mossy\nlayer 0\nWLST",
        "size 4x1x1\npalette W=wool:light_blue L=log:spruce S=slab:wooden_planks T=stone_bricks:cracked\nlayer 0\nWLST",
        "size 3x1x1\npalette W=wool:9 L=log:oak S=slab:3\nlayer 0\nWLS",
        "size 2x1x1\npalette C=cobblestone:north X=wool:plaid\nlayer 0\nCX",
        "size 2x1x1\npalette S=cobblestone_stairs:east D=door\nlayer 0\nSD",
        "size 2x1x1\npalette S=stairs:west I=iron_door:north\nlayer 0\nSI",
        "size 2x1x1\npalette X=unobtainium L=leaves\nlayer 0\nXL",
        "size 1x1x1\npalette W=water\nlayer 0\nW",
        "size 1x1x1\npalette S=wooden_stairs:5\nlayer 0\nS",
        "size 3x1x1\npalette C=cobblestone\nlayer 0\nCC",
        "size 2x1x1\npalette C=cobblestone\nlayer 0\nCQ",
        "size 1x2x1\npalette H=slab:cobble\nlayer 0-1\nH",
        "size 1x3x1\npalette D=door\nlayer 0-2\nD",
        "size 1x4x1\npalette D=door C=cobblestone\nlayer 0\nC\nlayer 1-3\nD",
        "size 1x2x1\npalette D=door C=cobblestone\nlayer 0\nD\nlayer 1\nC",
        "size 1x1x1\npalette D=door\nlayer 0\nD",
        "size 1x3x1\npalette D=door\nlayer 0\n.\nlayer 1\nD",
        "size 1x3x1\npalette D=door G=glass\nlayer 0\nG\nlayer 1\nD",
        "size 2x2x1\npalette C=cobblestone\nlayer 0\nCC\nlayer 0\nCC\nlayer 5\nCC",
        "size 2x2x3\npalette C=cobblestone\nlayer 0\nCC\nCC\nlayer 1\n\nCC\n\nCC\n\nlayer x",
        "size 2x1x1\npalette C=cobblestone G=glass\nlayer 0\nCC",
        "size 2x1x1\nlayer 0\n..",
        "size 2x1x1\nkind mine\nlayer 0\n..",
        "size 5x5x5\npalette C=cobblestone G=glass\nbox C 0,0,0 4,0,4\nfill C 0,4,0 4,4,4\nhollow C 0,1,0 4,3,4\nwalls G 1,1,1 3,3,3\nwall C 0,1,0 4,3,0\nwindow G 1,2,0 3,2,0\nclear 2,1,2 2,3,2\nair 1,1,1 1,1,1",
        "size 5x5x5\npalette C=cobblestone\nbox C 0,0,0 9,0,4\nhollow 0,0,0 1,1,1\nbox CC 0,0,0 1,1,1\nclear 0,0,0\nwalls C 0,0,0",
        "size 5x5x5\npalette C=cobblestone\nfloor C 0,0,0 4,0,4\nfloor C 0,1,0 4,2,4\nfloor C 0,0 4,4 3\nfloor C 0,0 4,4\nfloor",
        "size 3x5x3\npalette C=cobblestone\npillar C 1,0,1 4\npillar C 0,0,0 0\npillar C 0,0,0",
        "size 9x5x9\npalette S=sand C=cobblestone\npyramid S 0,0,0 9 core:C",
        "size 9x5x9\npalette S=sand\npyramid S 0,0,0 9 core:Q",
        "size 9x5x9\npalette S=sand\npyramid S 0,0,0 9 heart:C\npyramid S 0,0,0 0\npyramid S 0,0,0 20",
        "size 3x3x3\npalette C=cobblestone D=door\nbox C 0,0,0 2,0,2\ndoor D 1,1,2 south\ndoor D 0,1,0\ndoor D 2,1,0 sideways\ndoor D",
        "size 3x3x3\npalette C=cobblestone S=cobblestone_stairs\nbox C 0,0,0 2,0,2\nstairs S 0,1,0 north\nstairs S 1,1,0 east 1,1,2\nstairs S 2,1,0 up\nstairs S 2,1,0",
        "size 5x4x5\npalette C=cobblestone S=cobblestone_stairs P=planks\nbox C 0,0,0 4,0,4\nroof S gable 0,1,0 4,4 P\nroof P flat 0,3,0 4,4",
        "size 5x4x3\npalette S=cobblestone_stairs\nroof S gable 0,1,0 4,2",
        "size 3x4x5\npalette S=cobblestone_stairs\nroof S gable 0,1,0 2,4",
        "size 5x2x5\npalette S=cobblestone_stairs\nroof S gable 0,0,0 4,4",
        "size 5x5x5\npalette S=cobblestone_stairs C=cobblestone\nroof S slope:north 0,0,0 4,4\nroof C slope:east 0,0,0 4,4\nroof S slope:west 0,0,0 4,4\nroof S slope:south 0,0,0 4,4",
        "size 5x2x5\npalette S=cobblestone_stairs\nroof S slope:north 0,0,0 4,4",
        "size 5x5x5\npalette S=cobblestone_stairs\nroof S slope 0,0,0 4,4\nroof S dome 0,0,0 4,4\nroof S flat 0,0,0 9,9\nroof S flat 0,0\nroof SS flat 0,0,0 4,4",
        "size 2x1x1\npalette C=cobblestone\nlayer 0\nCC\nteleport C 0,0,0\nsummon",
        "size 2x1x1\nlight\npalette C=cobblestone\nlayer 0\nCC\n# a comment\nlayer 0 # again\nC. # trailing\n",
        "size 3x2x3\nfacing north\npalette C=cobblestone D=door\nbox C 0,0,0 2,0,2\ndoor D 1,1,0 north",
        "size 3x2x3\nfacing east\npalette C=cobblestone D=door S=cobblestone_stairs\nbox C 0,0,0 2,0,2\ndoor D 2,1,1 east\nstairs S 0,1,0 north",
        "size 3x2x3\nfacing west\npalette C=cobblestone D=door\nbox C 0,0,0 2,0,2\ndoor D 0,1,1 west\ndoor D 1,1,2 south",
        "size 1x1x1\npalette C=cobblestone\nlayer 0\nC\norigin 0,200,0",
        "size 1x1x1\npalette C=cobblestone\nlayer 0\nC\norigin 0,0,0",
        "size 64x1x64\npalette C=cobblestone\nbox C 0,0,0 63,0,63",
        "",
        "size 2x2x2",
        "size 3x3x3\nversion 1.12.2\npalette B=red_bed:north R=redstone_wire S=oak_stairs:5\nlayer 0\nBRS\n...\n...",
        "size 3x1x1\nversion 1.12.2\npalette S=stone_slab:3 L=log:2 X=nonsense_block\nlayer 0\nSLX",
        "size 1x2x1\nversion 1.12.2\npalette H=stone_slab\nlayer 0-1\nH",
    ];

    [Fact]
    public void Parse_and_normalize_outputs_match_the_golden_dump()
    {
        var actual = Dump();
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path)
        {
            File.WriteAllText(path, actual);
            return;
        }
        Assert.Equal(BlueprintGoldenData.Expected.ReplaceLineEndings("\n"), actual);
    }

    private static string Dump()
    {
        var sb = new StringBuilder();
        for (int k = 0; k < Corpus.Length; k++)
        {
            sb.Append("== case ").Append(k).Append('\n');
            var (bp, issues) = BlueprintParser.Parse(Corpus[k]);
            DumpBlueprint(sb, bp);
            foreach (var i in issues) sb.Append("parse ").Append(Issue(i)).Append('\n');
            var (plan, more) = BlueprintNormalizer.FromText(Corpus[k], new BlockPos(3, 64, -2));
            foreach (var i in more) sb.Append("full ").Append(Issue(i)).Append('\n');
            sb.Append("plan ").Append(PlanHash(plan)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Issue(BlueprintIssue i) => $"{i.Level}/{i.Code}/L{i.Line}: {i.Text}";

    private static void DumpBlueprint(StringBuilder sb, Blueprint bp)
    {
        sb.Append($"bp name={bp.Name} size={bp.W}x{bp.H}x{bp.D} origin={bp.Origin} facing={bp.Facing} drawn={bp.DrawnFront} ground={bp.Ground} light={bp.Light} ");
        sb.Append($"version={bp.Version} kind={bp.Kind} absY={bp.AbsoluteY} plot={bp.PlotAt} entry={bp.Entry} seed={bp.Seed} pref={bp.PreferredFor}\n");
        foreach (var (c, e) in bp.Palette.OrderBy(kv => kv.Key)) sb.Append($"  pal {c} {e}\n");
        foreach (var (y, (rows, line)) in bp.Layers) sb.Append($"  layer {y}@{line} [{string.Join("|", rows)}]\n");
        foreach (var op in bp.Ops) sb.Append($"  op {op}\n");
        sb.Append($"  order {string.Join(",", bp.Order.Select(o => o.What + o.Index))}\n");
    }

    private static string PlanHash(VoxelPlan? plan)
    {
        if (plan == null) return "null";
        var sb = new StringBuilder();
        sb.Append(plan.Origin).Append(';').Append(plan.Blueprint.DrawnFront).Append(';');
        foreach (var b in plan.Blocks.Values.OrderBy(b => b.Pos.X).ThenBy(b => b.Pos.Y).ThenBy(b => b.Pos.Z)) sb.Append(b.Pos).Append(b.Id).Append(':').Append(b.Meta).Append(';');
        sb.Append('|');
        foreach (var a in plan.Air.OrderBy(b => b.X).ThenBy(b => b.Y).ThenBy(b => b.Z)) sb.Append(a).Append(';');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
        return $"{plan.Blocks.Count} blocks, {plan.Air.Count} air, {hash}";
    }
}
