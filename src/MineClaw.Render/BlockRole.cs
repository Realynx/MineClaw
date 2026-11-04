namespace MineClaw.Render;

/// <summary>
/// What a block MEANS for the bot's work (vision v2 role layer), not what it is made of: the island floor and a generator's
/// dig cell can both be cobblestone, and only one of them may be mined. Roles come from the protection policy, the world
/// index and the placed-block log (MineClaw.Bot.Perception.Vision.RoleClassifier); the renderer only draws them.
/// </summary>
public enum BlockRole : byte
{
    None,
    /// <summary>Solid with room above: something to stand on.</summary>
    Floor,
    /// <summary>Solid with something solid above (or at body height): blocks the way.</summary>
    Wall,
    /// <summary>A cobble generator's dig cell: MINE HERE (air while it refills).</summary>
    GenCell,
    Water,
    Lava,
    /// <summary>Air with nothing below it down to y=0.</summary>
    Void,
    Door,
    /// <summary>Chest, furnace, crafting table, dispenser...</summary>
    Station,
    /// <summary>Part of a player build the bot doesn't know yet (world index "unknown_structure").</summary>
    PlayerBuild,
    /// <summary>A block a hive bot placed itself: always minable.</summary>
    OwnPlaced,
    /// <summary>Soft-protected for the bot's own choices (generator walls, remembered builds, farms...).</summary>
    SoftProtected,
    /// <summary>A player's "never break" mark.</summary>
    NeverBreak,
    /// <summary>A nether portal sheet or its obsidian frame.</summary>
    Portal,
    /// <summary>A block of a design preview (blueprint): drawn in place, not built yet.</summary>
    Planned,
    /// <summary>A grown crop (wheat stage 7/7, a melon/pumpkin, cane, cactus): always harvestable, whoever planted it. A crop is
    /// never "protected" in the role layer (live 10-28: the field drawn '$' read as "owned by others, needs permission").</summary>
    CropRipe,
    /// <summary>A crop still growing (wheat stage 0-6, a stem): leave it, it ripens.</summary>
    CropGrowing,
    /// <summary>Air a hostile mob can spawn in even by day (b1.8.1 rule over the light model: a normal-cube floor, room, light
    /// ≤ 7 with full daylight): shade under a build, a cave, an unlit room. Torch it.</summary>
    DarkSpawn,
}

/// <summary>The role of the block at (x, y, z) (air included: void, a refilling gen cell).</summary>
public delegate BlockRole RoleMap(int x, int y, int z);

public static class BlockRoles
{
    /// <summary>How a role shows: its text name, overlay colour (0xRRGGBB), overlay letter and whether it gets an overlay.</summary>
    private readonly record struct Look(string Name, uint Colour, char? Glyph = null, bool Highlight = false);

    private static readonly Look Unknown = new("none", 0x808080);

    /// <summary>The look of every role, by its value (roles not listed: <see cref="Unknown"/>).</summary>
    private static readonly Look[] Looks = BuildLooks();

    private static Look[] BuildLooks()
    {
        var t = new Look[Enum.GetValues<BlockRole>().Length];
        Array.Fill(t, Unknown);
        t[(int)BlockRole.Floor] = new("floor", 0x808080);
        t[(int)BlockRole.Wall] = new("wall", 0x808080);
        t[(int)BlockRole.GenCell] = new("gen cell", 0x2cff6a, 'G', true);
        t[(int)BlockRole.Water] = new("water", 0x3a6cff);
        t[(int)BlockRole.Lava] = new("lava", 0xff7a1a);
        t[(int)BlockRole.Void] = new("void", 0x0a1020);
        t[(int)BlockRole.Door] = new("door", 0xfff2b0, null, true);
        t[(int)BlockRole.Station] = new("station", 0xd9b3ff, null, true);
        t[(int)BlockRole.PlayerBuild] = new("player build", 0xff9a2e, 'B', true);
        t[(int)BlockRole.OwnPlaced] = new("own placed", 0x45c8ff, '+', true);
        t[(int)BlockRole.SoftProtected] = new("protected", 0xe8b030, '$', true);
        t[(int)BlockRole.NeverBreak] = new("never-break", 0xff2d2d, 'X', true);
        t[(int)BlockRole.Portal] = new("portal", 0xb24dff, 'P', true);
        t[(int)BlockRole.Planned] = new("planned", 0xff4fd8, '*', true);
        t[(int)BlockRole.CropRipe] = new("ripe crop", 0xf5e04a, 'H', true);
        t[(int)BlockRole.CropGrowing] = new("growing crop", 0x7fd35a, ':', true);
        t[(int)BlockRole.DarkSpawn] = new("dark: mobs spawn", 0xff3b6b, '!', true);
        return t;
    }

    private static Look Of(BlockRole r) => (int)r < Looks.Length ? Looks[(int)r] : Unknown;

    /// <summary>Roles worth an overlay (tint + letter); floor/wall/air read fine from the blocks themselves.</summary>
    public static bool Highlight(BlockRole r) => Of(r).Highlight;

    /// <summary>One-character role glyph shared by the ASCII map and the image chips (null = no overlay letter).</summary>
    public static char? Glyph(BlockRole r) => Of(r).Glyph;

    /// <summary>Overlay colour of a role (0xRRGGBB).</summary>
    public static uint Colour(BlockRole r) => Of(r).Colour;

    /// <summary>Text name (perception legend, answers, verifier).</summary>
    public static string Name(BlockRole r) => Of(r).Name;

    /// <summary>What a model's words mean, checked in order (the first rule with a word in the text wins).</summary>
    private static readonly (string[] Words, BlockRole Role)[] ParseRules =
    [
        (["never"], BlockRole.NeverBreak),
        (["spawn", "dark"], BlockRole.DarkSpawn),
        (["planned", "blueprint"], BlockRole.Planned),
        (["unripe", "not ripe", "growing", "stem"], BlockRole.CropGrowing),
        (["ripe", "crop", "wheat", "grown"], BlockRole.CropRipe),
        (["gen", "mine here", "dig cell"], BlockRole.GenCell),
        (["portal", "obsidian"], BlockRole.Portal),
        (["void", "abyss"], BlockRole.Void),
        (["lava"], BlockRole.Lava),
        (["water"], BlockRole.Water),
        (["door"], BlockRole.Door),
        (["chest", "station", "furnace", "bench", "table"], BlockRole.Station),
        (["own", "placed", "self"], BlockRole.OwnPlaced),
        (["build"], BlockRole.PlayerBuild),
        (["protect"], BlockRole.SoftProtected),
        (["floor", "ground"], BlockRole.Floor),
        (["wall", "solid"], BlockRole.Wall),
    ];

    /// <summary>Lenient parse of a role named by a model ("gen_cell", "generator cell", "MINE HERE", "chest"...).</summary>
    public static BlockRole? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var n = s.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        foreach (var (words, role) in ParseRules)
            foreach (var w in words)
                if (n.Contains(w)) return role;
        if (n is "none" or "air" or "empty") return BlockRole.None;
        return null;
    }

    /// <summary>The role-letter legend for the letters present ("G gen cell=MINE HERE, X never-break").</summary>
    public static string Legend(IEnumerable<BlockRole> present)
    {
        var parts = new List<string>();
        foreach (var r in present.Distinct().OrderBy(r => r))
            if (Glyph(r) is { } g) parts.Add(r switch
            {
                BlockRole.GenCell => $"{g} gen cell=MINE HERE",
                BlockRole.CropRipe => $"{g} ripe crop=HARVEST",
                _ => $"{g} {Name(r)}",
            });
        return string.Join(", ", parts);
    }
}
