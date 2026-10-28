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
}

/// <summary>The role of the block at (x, y, z) (air included: void, a refilling gen cell).</summary>
public delegate BlockRole RoleMap(int x, int y, int z);

public static class BlockRoles
{
    /// <summary>Roles worth an overlay (tint + letter); floor/wall/air read fine from the blocks themselves.</summary>
    public static bool Highlight(BlockRole r) => r is BlockRole.GenCell or BlockRole.Portal or BlockRole.NeverBreak or BlockRole.PlayerBuild
        or BlockRole.OwnPlaced or BlockRole.SoftProtected or BlockRole.Station or BlockRole.Door or BlockRole.Planned or BlockRole.CropRipe
        or BlockRole.CropGrowing;

    /// <summary>One-character role glyph shared by the ASCII map and the image chips (null = no overlay letter).</summary>
    public static char? Glyph(BlockRole r) => r switch
    {
        BlockRole.GenCell => 'G',
        BlockRole.Portal => 'P',
        BlockRole.NeverBreak => 'X',
        BlockRole.PlayerBuild => 'B',
        BlockRole.OwnPlaced => '+',
        BlockRole.SoftProtected => '$',
        BlockRole.Planned => '*',
        BlockRole.CropRipe => 'H',
        BlockRole.CropGrowing => ':',
        _ => null,
    };

    /// <summary>Overlay colour of a role (0xRRGGBB).</summary>
    public static uint Colour(BlockRole r) => r switch
    {
        BlockRole.GenCell => 0x2cff6a,
        BlockRole.Portal => 0xb24dff,
        BlockRole.NeverBreak => 0xff2d2d,
        BlockRole.PlayerBuild => 0xff9a2e,
        BlockRole.OwnPlaced => 0x45c8ff,
        BlockRole.SoftProtected => 0xe8b030,
        BlockRole.Station => 0xd9b3ff,
        BlockRole.Door => 0xfff2b0,
        BlockRole.Water => 0x3a6cff,
        BlockRole.Lava => 0xff7a1a,
        BlockRole.Void => 0x0a1020,
        BlockRole.Planned => 0xff4fd8,
        BlockRole.CropRipe => 0xf5e04a,
        BlockRole.CropGrowing => 0x7fd35a,
        _ => 0x808080,
    };

    /// <summary>Text name (perception legend, answers, verifier).</summary>
    public static string Name(BlockRole r) => r switch
    {
        BlockRole.Floor => "floor",
        BlockRole.Wall => "wall",
        BlockRole.GenCell => "gen cell",
        BlockRole.Water => "water",
        BlockRole.Lava => "lava",
        BlockRole.Void => "void",
        BlockRole.Door => "door",
        BlockRole.Station => "station",
        BlockRole.PlayerBuild => "player build",
        BlockRole.OwnPlaced => "own placed",
        BlockRole.SoftProtected => "protected",
        BlockRole.NeverBreak => "never-break",
        BlockRole.Portal => "portal",
        BlockRole.Planned => "planned",
        BlockRole.CropRipe => "ripe crop",
        BlockRole.CropGrowing => "growing crop",
        _ => "none",
    };

    /// <summary>Lenient parse of a role named by a model ("gen_cell", "generator cell", "MINE HERE", "chest"...).</summary>
    public static BlockRole? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var n = s.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        if (n.Contains("never")) return BlockRole.NeverBreak;
        if (n.Contains("planned") || n.Contains("blueprint")) return BlockRole.Planned;
        if (n.Contains("unripe") || n.Contains("not ripe") || n.Contains("growing") || n.Contains("stem")) return BlockRole.CropGrowing;
        if (n.Contains("ripe") || n.Contains("crop") || n.Contains("wheat") || n.Contains("grown")) return BlockRole.CropRipe;
        if (n.Contains("gen") || n.Contains("mine here") || n.Contains("dig cell")) return BlockRole.GenCell;
        if (n.Contains("portal") || n.Contains("obsidian")) return BlockRole.Portal;
        if (n.Contains("void") || n.Contains("abyss")) return BlockRole.Void;
        if (n.Contains("lava")) return BlockRole.Lava;
        if (n.Contains("water")) return BlockRole.Water;
        if (n.Contains("door")) return BlockRole.Door;
        if (n.Contains("chest") || n.Contains("station") || n.Contains("furnace") || n.Contains("bench") || n.Contains("table")) return BlockRole.Station;
        if (n.Contains("own") || n.Contains("placed") || n.Contains("self")) return BlockRole.OwnPlaced;
        if (n.Contains("build")) return BlockRole.PlayerBuild;
        if (n.Contains("protect")) return BlockRole.SoftProtected;
        if (n.Contains("floor") || n.Contains("ground")) return BlockRole.Floor;
        if (n.Contains("wall") || n.Contains("solid")) return BlockRole.Wall;
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
