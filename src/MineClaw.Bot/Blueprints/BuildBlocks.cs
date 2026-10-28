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

    public static Dir? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "n" or "north" or "-z" => Dir.North,
        "e" or "east" or "+x" => Dir.East,
        "s" or "south" or "+z" => Dir.South,
        "w" or "west" or "-x" => Dir.West,
        _ => null,
    };
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
        if (!Blocks.Exists(id)) return $"block id {id} doesn't exist in b1.8.1";
        if (Kinds.ContainsKey(id)) return null;
        return id switch
        {
            Blocks.Air => "air is '.' in a layer, not a palette block",
            Blocks.Grass => "grass can't be placed (place dirt: grass spreads onto it)",
            Blocks.DoubleSlab => "double slabs aren't an item: use two slab cells' worth of stone or a full block",
            Blocks.Leaves => "placed leaves decay in b1.8.1",
            Blocks.Ice => "ice melts next to light and leaves water",
            _ when Blocks.IsWater(id) || Blocks.IsLava(id) => "liquids aren't buildable",
            _ => $"{Blocks.Get(id).Name} isn't supported in blueprints (only building blocks, stairs, slabs, doors, torches, ladders, fences, panes, stations)",
        };
    }

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
        switch (KindOf(id))
        {
            case BuildKind.Stairs: return StairsMeta(StairsAscend(meta).Turn(quarters));
            case BuildKind.Door:
                {
                    int upper = meta & 8, hinge = meta & 4;
                    return DoorFacingMeta(DoorPlacerFacing(meta).Turn(quarters)) | hinge | upper;
                }
        }
        if (id == Blocks.Ladder && meta is >= 2 and <= 5)
            return LadderMeta(((Dir[])[Dir.South, Dir.North, Dir.East, Dir.West])[meta - 2].Turn(quarters));
        if (id is Blocks.Chest or Blocks.Furnace or Blocks.Dispenser && meta is >= 2 and <= 5)
            return FrontMeta(((Dir[])[Dir.North, Dir.South, Dir.West, Dir.East])[meta - 2].Turn(quarters));
        if (id == Blocks.FenceGate)
            return GateMeta(((Dir[])[Dir.South, Dir.West, Dir.North, Dir.East])[meta & 3].Turn(quarters)) | (meta & 4);
        return meta;
    }

    /// <summary>Null = (id, meta) is a valid b1.8.1 state for a blueprint cell; else why not.</summary>
    public static string? MetaProblem(int id, int meta)
    {
        string n = Blocks.Get(id).Name;
        return KindOf(id) switch
        {
            BuildKind.Stairs when meta is < 0 or > 3 => $"{n} meta {meta}: stairs are 0..3 (no upside-down stairs in b1.8.1)",
            BuildKind.Slab when meta is < 0 or > 5 => $"slab meta {meta}: 0 stone, 1 sandstone, 2 wood, 3 cobble, 4 brick, 5 stone brick",
            BuildKind.Door when meta is < 0 or > 15 => $"door meta {meta} out of range",
            _ when id == Blocks.Wool && meta is < 0 or > 15 => $"wool meta {meta}: colours are 0..15",
            _ when id == Blocks.Log && meta is < 0 or > 2 => $"log meta {meta}: 0 oak, 1 spruce, 2 birch",
            _ when id == Blocks.StoneBricks && meta is < 0 or > 2 => $"stone brick meta {meta}: 0 plain, 1 mossy, 2 cracked",
            _ when id == Blocks.Ladder && meta is < 2 or > 5 => $"ladder meta {meta}: 2..5 (the wall side)",
            _ when id == Blocks.Torch && meta is < 0 or > 5 => $"torch meta {meta}: 1..5",
            _ when id is Blocks.Chest or Blocks.Furnace or Blocks.Dispenser && meta is not (0 or >= 2 and <= 5) => $"{n} meta {meta}: 2..5 (front)",
            _ when id is Blocks.FenceGate or Blocks.Trapdoor && meta is < 0 or > 7 => $"{n} meta {meta}: 0..7",
            _ when id is Blocks.Pumpkin or Blocks.JackOLantern && meta is < 0 or > 3 => $"{n} meta {meta}: 0..3",
            _ when meta != 0 && !(HasVariants(id) || Directional(id) || id is Blocks.Torch or Blocks.Trapdoor or Blocks.Pumpkin or Blocks.JackOLantern)
                => $"{n} has no metadata variants (meta {meta})",
            _ => null,
        };
    }

    /// <summary>Short display name of a placed state ("cobblestone", "oak stairs", "red wool").</summary>
    public static string Name(int id, int meta) => HasVariants(id) ? Items.VariantName(id, meta).Replace('_', ' ') : Blocks.Get(id).Name.Replace('_', ' ');
}
