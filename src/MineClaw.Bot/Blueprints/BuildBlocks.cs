using MineClaw.Data;

namespace MineClaw.Bot.Blueprints;

/// <summary>A horizontal direction (blueprint facing, door/stairs/ladder orientation). +x east, +z south.</summary>
public enum Dir { North, East, South, West }

public static class Dirs
{
    public static (int Dx, int Dz) Offset(this Dir d) => d switch
    {
        Dir.North => (0, -1), Dir.East => (1, 0), Dir.South => (0, 1), _ => (-1, 0),
    };

    public static Dir Opposite(this Dir d) => (Dir)(((int)d + 2) & 3);

    /// <summary>Clockwise quarter turns, seen from above with north up: N→E→S→W.</summary>
    public static Dir Turn(this Dir d, int quarters) => (Dir)((((int)d + quarters) % 4 + 4) & 3);

    public static string Name(this Dir d) => d.ToString().ToLowerInvariant();

    private static readonly Dictionary<string, Dir> Names = new(StringComparer.Ordinal)
    {
        ["n"] = Dir.North, ["north"] = Dir.North, ["-z"] = Dir.North,
        ["e"] = Dir.East, ["east"] = Dir.East, ["+x"] = Dir.East,
        ["s"] = Dir.South, ["south"] = Dir.South, ["+z"] = Dir.South,
        ["w"] = Dir.West, ["west"] = Dir.West, ["-x"] = Dir.West,
    };

    public static Dir? Parse(string? s) => s != null && Names.TryGetValue(s.Trim().ToLowerInvariant(), out var d) ? d : null;
}

/// <summary>How a block behaves for planning, support and placement.</summary>
public enum BuildKind
{
    /// <summary>Full solid cube (stone, planks, glass...).</summary>
    Solid,
    /// <summary>Solid cube that falls without support straight below (sand, gravel).</summary>
    Gravity,
    Slab,
    Stairs,
    /// <summary>Two-block door (lower half + upper half with bit 8), placed on the top face of the block below.</summary>
    Door,
    /// <summary>Needs a solid face to hang on (torch, ladder).</summary>
    Attached,
    /// <summary>Thin connecting blocks (fence, pane, bars, gate, trapdoor): placed after the solids they connect to.</summary>
    Thin,
    /// <summary>Directional containers/stations (chest, furnace, dispenser, table, pumpkin...).</summary>
    Station,
}

/// <summary>
/// The blocks a blueprint may use, with b1.8.1 metadata rules taken from mc-dev (commit 218a2a71): ItemDoor (lower half =
/// placer's quarter-turn facing 0..3 + 4 for a flipped hinge, upper half = that + 8, only on a top face), BlockStairs (0 ascends
/// east, 1 west, 2 south, 3 north), BlockStep (6 slab types; a slab placed on a same-type slab merges into a double slab below),
/// BlockLadder (2..5: the solid block is south/north/east/west), BlockFurnace/BlockChest/BlockDispenser (front 2 north, 3 south,
/// 4 west, 5 east), BlockFenceGate (yaw quarter: 0 south, 1 west, 2 north, 3 east; +4 open), BlockTorch (1..5).
/// </summary>
public static class BuildBlocks
{
    private static readonly Dictionary<int, BuildKind> Kinds = new()
    {
        [Blocks.Stone] = BuildKind.Solid, [Blocks.Dirt] = BuildKind.Solid, [Blocks.Cobblestone] = BuildKind.Solid, [Blocks.Planks] = BuildKind.Solid,
        [Blocks.Sand] = BuildKind.Gravity, [Blocks.Gravel] = BuildKind.Gravity, [Blocks.Log] = BuildKind.Solid, [Blocks.Sponge] = BuildKind.Solid,
        [Blocks.Glass] = BuildKind.Solid, [Blocks.LapisBlock] = BuildKind.Solid, [Blocks.Sandstone] = BuildKind.Solid, [Blocks.Wool] = BuildKind.Solid,
        [Blocks.GoldBlock] = BuildKind.Solid, [Blocks.IronBlock] = BuildKind.Solid, [Blocks.Bricks] = BuildKind.Solid, [Blocks.Bookshelf] = BuildKind.Solid,
        [Blocks.MossyCobblestone] = BuildKind.Solid, [Blocks.Obsidian] = BuildKind.Solid, [Blocks.DiamondBlock] = BuildKind.Solid,
        [Blocks.SnowBlock] = BuildKind.Solid, [Blocks.Clay] = BuildKind.Solid, [Blocks.Netherrack] = BuildKind.Solid, [Blocks.SoulSand] = BuildKind.Solid,
        [Blocks.Glowstone] = BuildKind.Solid, [Blocks.StoneBricks] = BuildKind.Solid, [Blocks.Melon] = BuildKind.Solid, [Blocks.NoteBlock] = BuildKind.Solid,
        [Blocks.Jukebox] = BuildKind.Solid,
        [Blocks.Slab] = BuildKind.Slab,
        [Blocks.WoodenStairs] = BuildKind.Stairs, [Blocks.CobblestoneStairs] = BuildKind.Stairs, [Blocks.BrickStairs] = BuildKind.Stairs,
        [Blocks.StoneBrickStairs] = BuildKind.Stairs,
        [Blocks.WoodenDoor] = BuildKind.Door, [Blocks.IronDoor] = BuildKind.Door,
        [Blocks.Torch] = BuildKind.Attached, [Blocks.Ladder] = BuildKind.Attached,
        [Blocks.Fence] = BuildKind.Thin, [Blocks.IronBars] = BuildKind.Thin, [Blocks.GlassPane] = BuildKind.Thin, [Blocks.FenceGate] = BuildKind.Thin,
        [Blocks.Trapdoor] = BuildKind.Thin,
        [Blocks.Chest] = BuildKind.Station, [Blocks.Furnace] = BuildKind.Station, [Blocks.Dispenser] = BuildKind.Station,
        [Blocks.Workbench] = BuildKind.Station, [Blocks.Pumpkin] = BuildKind.Station, [Blocks.JackOLantern] = BuildKind.Station,
    };

    /// <summary>Why a b1.8.1 block can't go in a blueprint (null = it can).</summary>
    public static string? Refusal(int id)
    {
        if (!Blocks.Exists(id)) return $"block id {id} doesn't exist in {MineClaw.Data.Versioning.GameData.Current.VersionId}";
        return Kinds.ContainsKey(id) ? null : RefusalOfUnbuildable(id);
    }

    private static string RefusalOfUnbuildable(int id) => id switch
    {
        Blocks.Air => "air is '.' in a layer, not a palette block",
        Blocks.Grass => "grass can't be placed (place dirt: grass spreads onto it)",
        Blocks.DoubleSlab => "double slabs aren't an item: use two slab cells' worth of stone or a full block",
        Blocks.Leaves => MineClaw.Data.Versioning.GameData.Current.VersionId == "b1.8.1" ? "placed leaves decay in b1.8.1" : "leaves aren't buildable in blueprints",
        Blocks.Ice => "ice melts next to light and leaves water",
        _ when Blocks.IsWater(id) || Blocks.IsLava(id) => "liquids aren't buildable",
        _ => $"{Blocks.Get(id).Name} isn't supported in blueprints (only building blocks, stairs, slabs, doors, torches, ladders, fences, panes, stations)",
    };

    public static BuildKind KindOf(int id) => Kinds.TryGetValue(id, out var k) ? k : BuildKind.Solid;

    public static bool Buildable(int id) => Kinds.ContainsKey(id);

    /// <summary>Has a full collision box the bot can stand on / place against (stairs and slabs count: they have a top).</summary>
    public static bool Supports(int id) => id != 0 && Blocks.Get(id).Solid && id is not (Blocks.Torch or Blocks.Ladder);

    /// <summary>A full opaque cube (mc-dev isBlockNormalCube): what a door must stand on.</summary>
    public static bool NormalCube(int id) => id != 0 && Blocks.Get(id) is { Solid: true, CollisionHeight: >= 1.0, Transparent: false } && id != Blocks.Fence
        && id != Blocks.FenceGate;

    /// <summary>The inventory item (id, damage) that places block (id, meta): doors are items 324/330; variants keep their damage.</summary>
    public static (int Item, int Damage) ItemFor(int id, int meta) => id switch
    {
        Blocks.WoodenDoor => (Items.WoodenDoor, 0),
        Blocks.IronDoor => (Items.IronDoor, 0),
        Blocks.Slab => (Blocks.Slab, meta & 7),
        Blocks.Wool => (Blocks.Wool, meta & 15),
        Blocks.Log => (Blocks.Log, meta & 3),
        Blocks.StoneBricks => (Blocks.StoneBricks, meta & 3),
        _ => (Items.ItemFormOf(id), 0),
    };

    /// <summary>Damage values matter for the item (slab/wool/log/stone brick variants).</summary>
    public static bool HasVariants(int id) => id is Blocks.Slab or Blocks.Wool or Blocks.Log or Blocks.StoneBricks;

    // ------------------------------------------------------------------ metadata per mc-dev

    public static int StairsMeta(Dir ascend) => ascend switch { Dir.East => 0, Dir.West => 1, Dir.South => 2, _ => 3 };
    public static Dir StairsAscend(int meta) => (meta & 3) switch { 0 => Dir.East, 1 => Dir.West, 2 => Dir.South, _ => Dir.North };

    /// <summary>ItemDoor: the placer's facing quarter (east 0, south 1, west 2, north 3).</summary>
    public static int DoorFacingMeta(Dir placerFaces) => placerFaces switch { Dir.East => 0, Dir.South => 1, Dir.West => 2, _ => 3 };
    public static Dir DoorPlacerFacing(int meta) => (meta & 3) switch { 0 => Dir.East, 1 => Dir.South, 2 => Dir.West, _ => Dir.North };

    /// <summary>Lower-half meta of a door in a wall facing out <paramref name="outward"/>: placed from outside, looking in.</summary>
    public static int DoorMeta(Dir outward) => DoorFacingMeta(outward.Opposite());

    /// <summary>BlockLadder: the wall (solid block) is on side <paramref name="wall"/>.</summary>
    public static int LadderMeta(Dir wall) => wall switch { Dir.South => 2, Dir.North => 3, Dir.East => 4, _ => 5 };

    /// <summary>Chest/furnace/dispenser front facing <paramref name="front"/>.</summary>
    public static int FrontMeta(Dir front) => front switch { Dir.North => 2, Dir.South => 3, Dir.West => 4, _ => 5 };

    /// <summary>Fence gate opening along <paramref name="through"/> (the walking direction).</summary>
    public static int GateMeta(Dir through) => through switch { Dir.South => 0, Dir.West => 1, Dir.North => 2, _ => 3 };

    /// <summary>Applies a facing to a block: the meta for (id, direction), or null when the block has no facing.</summary>
    public static int? MetaFor(int id, Dir d) => KindOf(id) switch
    {
        BuildKind.Stairs => StairsMeta(d),
        BuildKind.Door => DoorMeta(d),
        _ when id == Blocks.Ladder => LadderMeta(d),
        _ when id is Blocks.Chest or Blocks.Furnace or Blocks.Dispenser => FrontMeta(d),
        _ when id == Blocks.FenceGate => GateMeta(d),
        _ => null,
    };

    /// <summary>Whether a block takes a facing word in the palette.</summary>
    public static bool Directional(int id) => MetaFor(id, Dir.North) != null;

    /// <summary>Rotates a meta by <paramref name="quarters"/> clockwise turns (blueprint facing).</summary>
    public static int Rotate(int id, int meta, int quarters)
    {
        if (quarters % 4 == 0) return meta;
        return KindOf(id) switch
        {
            BuildKind.Stairs => StairsMeta(StairsAscend(meta).Turn(quarters)) | (meta & 4), // 1.12.2: keep upside down
            BuildKind.Door => DoorFacingMeta(DoorPlacerFacing(meta).Turn(quarters)) | (meta & 4) | (meta & 8), // hinge, upper half
            // Every other design block (1.12.2 stairs and doors of other woods, beds, signs, rails, logs' axes…): the design rules.
            _ => RotateFronted(id, meta, quarters) ?? RotateOther(id, meta, quarters) ?? DesignFacing.Rotate(id, meta, quarters),
        };
    }

    /// <summary>The wall a ladder hangs on (metas 2..5), a chest/furnace/dispenser's front (metas 2..5), a gate's way through
    /// (meta &amp; 3), a torch's wall (metas 1..4), a trapdoor's wall (meta &amp; 3; mc-dev BlockTrapdoor).</summary>
    internal static readonly Dir[] LadderWalls = [Dir.South, Dir.North, Dir.East, Dir.West], FrontSides = [Dir.North, Dir.South, Dir.West, Dir.East],
        GateWays = [Dir.South, Dir.West, Dir.North, Dir.East], TorchWalls = [Dir.West, Dir.East, Dir.North, Dir.South],
        TrapdoorWalls = [Dir.South, Dir.North, Dir.East, Dir.West];

    internal static bool IsFronted(int id) => id is Blocks.Chest or Blocks.Furnace or Blocks.Dispenser;

    private static int? RotateFronted(int id, int meta, int quarters)
    {
        if (id == Blocks.Ladder && meta is >= 2 and <= 5) return LadderMeta(LadderWalls[meta - 2].Turn(quarters));
        if (IsFronted(id) && meta is >= 2 and <= 5) return FrontMeta(FrontSides[meta - 2].Turn(quarters));
        if (id == Blocks.FenceGate) return GateMeta(GateWays[meta & 3].Turn(quarters)) | (meta & 4);
        return null;
    }

    // BlockTorch: 1..4 = the wall on the west/east/north/south (5 = floor); BlockTrapdoor: 0..3 = the wall south/north/east/
    // west (+4 open); BlockPumpkin: the placer's yaw quarter (+1 per clockwise turn).
    private static int? RotateOther(int id, int meta, int quarters)
    {
        if (id == Blocks.Torch && meta is >= 1 and <= 4) return Array.IndexOf(TorchWalls, TorchWalls[meta - 1].Turn(quarters)) + 1;
        if (id == Blocks.Trapdoor) return Array.IndexOf(TrapdoorWalls, TrapdoorWalls[meta & 3].Turn(quarters)) | (meta & 4);
        if (id is Blocks.Pumpkin or Blocks.JackOLantern) return ((meta + quarters) % 4 + 4) % 4;
        return null;
    }

    /// <summary>A meta range rule of <see cref="MetaProblem"/>: the blocks it covers, the metas it refuses, and why.</summary>
    private sealed record MetaRule(Func<int, BuildKind, bool> Covers, Func<int, bool> Bad, Func<string, int, string> Why);

    /// <summary>In order: the first rule that covers a block and refuses its meta names the problem.</summary>
    private static readonly MetaRule[] MetaRules =
    [
        new((_, k) => k == BuildKind.Stairs, m => m is < 0 or > 3, (n, m) => $"{n} meta {m}: stairs are 0..3 (no upside-down stairs in blueprints)"),
        new((_, k) => k == BuildKind.Slab, m => m is < 0 or > 5, (_, m) => $"slab meta {m}: 0 stone, 1 sandstone, 2 wood, 3 cobble, 4 brick, 5 stone brick"),
        new((_, k) => k == BuildKind.Door, m => m is < 0 or > 15, (_, m) => $"door meta {m} out of range"),
        new((id, _) => id == Blocks.Wool, m => m is < 0 or > 15, (_, m) => $"wool meta {m}: colours are 0..15"),
        new((id, _) => id == Blocks.Log, m => m is < 0 or > 2, (_, m) => $"log meta {m}: 0 oak, 1 spruce, 2 birch"),
        new((id, _) => id == Blocks.StoneBricks, m => m is < 0 or > 2, (_, m) => $"stone brick meta {m}: 0 plain, 1 mossy, 2 cracked"),
        new((id, _) => id == Blocks.Ladder, m => m is < 2 or > 5, (_, m) => $"ladder meta {m}: 2..5 (the wall side)"),
        new((id, _) => id == Blocks.Torch, m => m is < 0 or > 5, (_, m) => $"torch meta {m}: 1..5"),
        new((id, _) => IsFronted(id), m => m is not (0 or >= 2 and <= 5), (n, m) => $"{n} meta {m}: 2..5 (front)"),
        new((id, _) => id is Blocks.FenceGate or Blocks.Trapdoor, m => m is < 0 or > 7, (n, m) => $"{n} meta {m}: 0..7"),
        new((id, _) => id is Blocks.Pumpkin or Blocks.JackOLantern, m => m is < 0 or > 3, (n, m) => $"{n} meta {m}: 0..3"),
        new((id, _) => !TakesMeta(id), m => m != 0, (n, m) => $"{n} has no metadata variants (meta {m})"),
    ];

    /// <summary>A block whose meta means something (a variant, a facing, a torch's wall, a trapdoor's or pumpkin's turn).</summary>
    private static bool TakesMeta(int id) =>
        HasVariants(id) || Directional(id) || id is Blocks.Torch or Blocks.Trapdoor or Blocks.Pumpkin or Blocks.JackOLantern;

    /// <summary>Null = (id, meta) is a valid b1.8.1 state for a blueprint cell; else why not.</summary>
    public static string? MetaProblem(int id, int meta)
    {
        string n = Blocks.Get(id).Name;
        var kind = KindOf(id);
        foreach (var rule in MetaRules)
            if (rule.Covers(id, kind) && rule.Bad(meta)) return rule.Why(n, meta);
        return null;
    }

    /// <summary>Short display name of a placed state ("cobblestone", "oak stairs", "red wool").</summary>
    public static string Name(int id, int meta) => HasVariants(id) ? Items.VariantName(id, meta).Replace('_', ' ') : Blocks.Get(id).Name.Replace('_', ' ');
}
