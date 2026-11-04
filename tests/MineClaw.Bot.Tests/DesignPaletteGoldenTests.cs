using System.Security.Cryptography;
using System.Text;
using MineClaw.Bot.Blueprints;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins <see cref="DesignPalette"/> (both versions) over every input that matters: Refusal/Valid for every id × meta, every
/// entry resolved with every modifier, every alias spelling the palette builds, free-form names, did-you-mean, find and the
/// model's summary. The dumps are hashed; set WP02_GOLDEN_OUT to a directory to write them out (to diff after a change).
/// </summary>
public class DesignPaletteGoldenTests
{
    private static readonly string[] Mods =
    [
        "north", "east", "south", "west", "up", "down", "floor", "ground", "standing", "ceiling", "top", "upper", "upside_down", "bottom",
        "lower", "open", "closed", "x", "y", "z", "wall=west", "wall=north", "on=east", "facing=south", "front=west", "dir=north",
        "dir=up", "wall=up", "side=east", "5", "0", "12", "junk",
    ];

    private static readonly string[] Free =
    [
        "", "  ", "minecraft:stone", "Red Wool", "wool:red", "wool:14", "red_wool", "wool:light_gray", "wool:silver", "wool:grey",
        "log:spruce", "spruce_log", "log:dark_oak", "big_oak_log", "planks", "spruce_planks", "oak_door", "door:north", "iron_door:east:open",
        "wooden_door", "repeater", "repeater:west", "comparator", "redstone", "redstone_wire", "sign", "sign:north", "wall_sign:wall=east",
        "bed", "bed:north", "red_bed:north", "cake", "cauldron", "brewing_stand", "flower_pot", "skull:wall=north", "skull:floor", "banner",
        "standing_banner", "wheat", "carrots", "potatoes", "nether_wart", "sugar_cane", "reeds", "cobble", "workbench", "stairs:north:top",
        "oak_stairs:east:upside_down", "slab", "slab:cobble", "slab:cobble:top", "stone_slab:3", "smooth_stone", "torch:wall=west", "torch:floor",
        "lever:ceiling", "lever:wall=south", "button:down", "button:wall=east", "trapdoor:north:open:top", "fence_gate:east:open", "rail:x",
        "rail:z", "powered_rail:y", "quartz_block:2:x", "quartz_block:pillar:x", "log:x", "log:birch:z", "hay:x", "purpur_pillar:z", "vine:wall=south",
        "chest:east", "furnace:west", "dispenser:up", "dispenser:north", "hopper:down", "pumpkin:south", "ladder:wall=north", "redwol",
        "unobtainium", "stained_glass:light_blue", "concrete:white", "glazed_terracotta", "white_glazed_terracotta:east", "anvil:north",
        "tall_grass", "fern", "double_plant:sunflower", "rose_bush", "lilac", "red_sand", "mushroom_stem", "stone:granite", "granite",
        "dirt:coarse", "coarse_dirt", "sandstone:chiseled", "chiseled_sandstone", "stonebrick:mossy", "mossy_stone_bricks", "end_rod:up",
        "shulker_box", "observer:north", "piston:up", "sticky_piston:down", "jack_o_lantern:north", "water", "lava", "fire", "air",
        "251:5", "35:14", "1", "999", "wool:99", "wool:red:north",
    ];

    [Fact]
    public void Palette_outputs_match_the_golden_hashes()
    {
        var outDir = Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT");
        var actual = new List<string>();
        foreach (var v in DesignVersions.All)
        {
            var dump = Dump(DesignPalette.For(v));
            if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, $"palette-{v}.txt"), dump);
            actual.Add($"{v} {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..24]}");
        }
        if (outDir is { Length: > 0 }) File.WriteAllLines(Path.Combine(outDir, "palette-hashes.txt"), actual);
        else Assert.Equal(Expected, actual);
    }

    /// <summary>Generated from the pre-refactor palette (WP02).</summary>
    private static readonly string[] Expected = ["b1.8.1 BBC302561BAA612C89C5DCFE", "1.12.2 F3122A867CFE99BE6685B75F"];

    private static string Dump(DesignPalette p)
    {
        var sb = new StringBuilder();
        for (int id = -1; id <= 260; id++)
            for (int meta = -1; meta <= 16; meta++)
                sb.Append($"valid {id}:{meta} {p.Valid(id, meta)} {p.Refusal(id, meta) ?? "ok"}\n");
        foreach (var e in p.Entries)
        {
            sb.Append($"entry {e}\n");
            foreach (var m in Mods) Resolve(sb, p, e.Spec + ":" + m);
            foreach (var s in Spellings(e)) Resolve(sb, p, s);
        }
        foreach (var s in Free) Resolve(sb, p, s);
        foreach (var s in Free) sb.Append($"unknown {s} => {p.Unknown(s)}\n");
        foreach (var q in new[] { "red wool", "stairs", "251:5", "oak", "glass", "door", "x", "" })
            sb.Append($"find {q} => {string.Join(",", p.Find(q, 12).Select(e => e.Spec))}\n");
        sb.Append(p.Summary());
        return sb.ToString();
    }

    private static void Resolve(StringBuilder sb, DesignPalette p, string spec)
    {
        bool ok = p.TryResolve(spec, out var st, out var why);
        sb.Append($"  {spec} => {(ok ? $"{st.Id}:{st.Meta}" : "no")} {why}\n");
    }

    private static IEnumerable<string> Spellings(DesignBlock e)
    {
        yield return e.Name;
        yield return e.Base;
        if (e.Icon != null) yield return e.Icon;
        if (e.Word is not { } w) yield break;
        foreach (var f in new[]
        {
            "{w}", "{w}_{b}", "{b}_{w}", "{b}:{w}", "log:{w}", "{w}_log", "{w}_wood", "slab:{w}", "{w}_slab", "leaves:{w}", "{w}_leaves",
            "stone_bricks:{w}", "stonebrick:{w}", "{w}_stone_bricks", "{w}_planks", "{w}_wood_planks", "{w}_sapling", "stained_pane:{w}",
            "{w}_glass_pane", "terracotta:{w}", "{w}_terracotta", "stained_clay:{w}", "{w}_glass", "slab:wood", "wood_slab", "wooden_slab", "oak_slab",
        })
            yield return f.Replace("{w}", w).Replace("{b}", e.Base);
    }
}
