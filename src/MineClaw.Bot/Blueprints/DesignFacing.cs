using MineClaw.Data.Versioning;

namespace MineClaw.Bot.Blueprints;

/// <summary>How a design block's meta behaves: which bits are its variant, its facing, its half, its runtime state.</summary>
public enum DesignFamily
{
    /// <summary>The meta is the variant (wool colour, planks wood, stone kind) or always 0.</summary>
    None,
    /// <summary>meta &amp; 3 = the way up E W S N; 1.12.2: + 4 upside down.</summary>
    Stairs,
    /// <summary>meta &amp; 7 = the material; 1.12.2: + 8 the top half.</summary>
    Slab,
    /// <summary>meta &amp; 3 = the wood; 1.12.2: axis bits 4 = x, 8 = z, 12 = bark all round.</summary>
    Log,
    /// <summary>Hay, bone block, purpur pillar: axis 0 y, 4 x, 8 z.</summary>
    Pillar,
    /// <summary>Quartz block: 0 plain, 1 chiseled, 2 pillar y, 3 pillar x, 4 pillar z.</summary>
    Quartz,
    /// <summary>Leaves: meta &amp; 3 = the tree; 1.12.2 + 4 never decays (a player's), + 8 check.</summary>
    Leaves,
    /// <summary>Two tall: lower meta &amp; 3 = the placer's facing E S W N (+4 open), upper = lower | 8 (the designs system's rule).</summary>
    Door,
    /// <summary>Two tall plant (1.12.2 double plant): lower = the kind 0..5, upper = lower | 8.</summary>
    Tall,
    /// <summary>Two long: foot meta &amp; 3 = the facing S W N E (foot → head), head = foot | 8 one step further.</summary>
    Bed,
    /// <summary>Torches: 1..4 hang on the wall W E N S, 5 stands on the floor.</summary>
    Torch,
    /// <summary>Hung on a wall, meta 2..5 = the front N S W E (the wall behind): ladders, wall signs, wall banners, wall skulls.</summary>
    Wall,
    /// <summary>Front 2..5 = N S W E (chests, furnaces).</summary>
    Front,
    /// <summary>Six facings 0 down, 1 up, 2..5 N S W E (+8 extended/powered/triggered): pistons, dispensers, droppers, observers,
    /// end rods, shulker boxes.</summary>
    Facing6,
    /// <summary>Hopper: output 0 down, 2..5 N S W E (+8 disabled).</summary>
    Hopper,
    /// <summary>meta &amp; 3 = a horizontal facing S W N E, higher bits a variant or state (pumpkins, glazed terracotta, anvils,
    /// repeaters, comparators, cocoa, tripwire hooks).</summary>
    Horizontal,
    /// <summary>Fence gates: meta &amp; 3 = the way through S W N E, + 4 open.</summary>
    Gate,
    /// <summary>Trapdoors: meta &amp; 3 = the wall S N E W, + 4 open, 1.12.2 + 8 top half.</summary>
    Trapdoor,
    /// <summary>Buttons: 1..4 face E W S N (the wall behind); 1.12.2 0 ceiling, 5 floor; + 8 pressed.</summary>
    Button,
    /// <summary>Levers: 1..4 face E W S N; 5/6 floor (z / x), 1.12.2 0/7 ceiling (x / z); + 8 on.</summary>
    Lever,
    /// <summary>Rails: 0 N-S, 1 E-W, 2..5 ascending E W N S, 6..9 curves SE SW NW NE.</summary>
    Rail,
    /// <summary>Powered, detector, activator rails: 0..5 like rails, + 8 powered.</summary>
    PoweredRail,
    /// <summary>Standing signs and banners: 0..15 the rotation in sixteenths (0 faces south, clockwise).</summary>
    Rotation16,
    /// <summary>Skulls: 1 on the floor (its turn is block-entity data), 2..5 hung on a wall like <see cref="Wall"/>.</summary>
    Skull,
    /// <summary>Vines: bits 1 south, 2 west, 4 north, 8 east (the sides they hang on).</summary>
    Vine,
    /// <summary>A growth/runtime counter 0..max (crops, cactus, snow layers, cake, cauldron, wire power).</summary>
    Age,
}

/// <summary>
/// The design-palette facing rules (the C# side of web <c>features/studio/facing.ts</c>): each id's family, the meta a facing word
/// gives, and how a meta turns 90° and mirrors. <see cref="BuildBlocks"/> keeps its own rules for the blocks it builds and asks
/// this for the rest; both agree on the shared ones.
/// </summary>
public static class DesignFacing
{
    private static readonly Dictionary<int, DesignFamily> Families = Build();

    private static Dictionary<int, DesignFamily> Build()
    {
        var d = new Dictionary<int, DesignFamily>();
        void Set(DesignFamily f, params int[] ids) { foreach (var i in ids) d[i] = f; }
        Set(DesignFamily.Stairs, 53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203);
        Set(DesignFamily.Slab, 44, 126, 182, 205);
        Set(DesignFamily.Log, 17, 162);
        Set(DesignFamily.Pillar, 170, 202, 216);
        Set(DesignFamily.Quartz, 155);
        Set(DesignFamily.Leaves, 18, 161);
        Set(DesignFamily.Door, 64, 71, 193, 194, 195, 196, 197);
        Set(DesignFamily.Tall, 175);
        Set(DesignFamily.Bed, 26);
        Set(DesignFamily.Torch, 50, 75, 76);
        Set(DesignFamily.Wall, 65, 68, 177);
        Set(DesignFamily.Front, 54, 61, 62, 130, 146);
        Set(DesignFamily.Facing6, 23, 29, 33, 158, 198, 218);
        for (int i = 219; i <= 234; i++) d[i] = DesignFamily.Facing6;
        Set(DesignFamily.Hopper, 154);
        Set(DesignFamily.Horizontal, 86, 91, 93, 94, 127, 131, 145, 149, 150);
        for (int i = 235; i <= 250; i++) d[i] = DesignFamily.Horizontal;
        Set(DesignFamily.Gate, 107, 183, 184, 185, 186, 187);
        Set(DesignFamily.Trapdoor, 96, 167);
        Set(DesignFamily.Button, 77, 143);
        Set(DesignFamily.Lever, 69);
        Set(DesignFamily.Rail, 66);
        Set(DesignFamily.PoweredRail, 27, 28, 157);
        Set(DesignFamily.Rotation16, 63, 176);
        Set(DesignFamily.Skull, 144);
        Set(DesignFamily.Vine, 106);
        Set(DesignFamily.Age, 55, 59, 60, 70, 72, 78, 81, 83, 92, 104, 105, 115, 117, 118, 141, 142, 147, 148, 151, 178, 200, 207);
        return d;
    }

    public static DesignFamily FamilyOf(int id) => Families.TryGetValue(id, out var f) ? f : DesignFamily.None;

    /// <summary>A family whose meta says where it faces (placement asks for a direction).</summary>
    public static bool HasFacing(DesignFamily f) => f is DesignFamily.Stairs or DesignFamily.Door or DesignFamily.Bed or DesignFamily.Torch
        or DesignFamily.Wall or DesignFamily.Front or DesignFamily.Facing6 or DesignFamily.Hopper or DesignFamily.Horizontal or DesignFamily.Gate
        or DesignFamily.Trapdoor or DesignFamily.Button or DesignFamily.Lever or DesignFamily.Rail or DesignFamily.PoweredRail
        or DesignFamily.Rotation16 or DesignFamily.Skull or DesignFamily.Vine;

    /// <summary>The bits of a meta that are the block's variant (what a "replace wool:red" compares); 0 = none.</summary>
    public static int VariantMask(int id) => FamilyOf(id) switch
    {
        DesignFamily.None => 15,
        DesignFamily.Slab => 7,
        DesignFamily.Log or DesignFamily.Leaves => 3,
        DesignFamily.Tall => 7,
        DesignFamily.Quartz => id == 155 ? 15 : 0,
        DesignFamily.Horizontal => id == 145 ? 12 : 0,
        _ => 0,
    };

    // ------------------------------------------------------------------ directions

    private static readonly Dir[] SWNE = [Dir.South, Dir.West, Dir.North, Dir.East];
    private static readonly Dir[] StairDirs = [Dir.East, Dir.West, Dir.South, Dir.North];
    private static readonly Dir[] DoorDirs = [Dir.East, Dir.South, Dir.West, Dir.North];
    private static readonly Dir[] FrontDirs = [Dir.North, Dir.South, Dir.West, Dir.East]; // metas 2..5
    private static readonly Dir[] TorchWalls = [Dir.West, Dir.East, Dir.North, Dir.South]; // metas 1..4: the wall
    private static readonly Dir[] TrapWalls = [Dir.South, Dir.North, Dir.East, Dir.West];
    private static readonly Dir[] ButtonFaces = [Dir.East, Dir.West, Dir.South, Dir.North]; // metas 1..4: the way it faces

    /// <summary>The horizontal facing a meta encodes (null = none, a floor torch, an upright log).</summary>
    public static Dir? FacingOf(int id, int meta)
    {
        switch (FamilyOf(id))
        {
            case DesignFamily.Stairs: return StairDirs[meta & 3];
            case DesignFamily.Door: return DoorDirs[meta & 3];
            case DesignFamily.Bed: case DesignFamily.Horizontal: case DesignFamily.Gate: return SWNE[meta & 3];
            case DesignFamily.Torch: return meta is >= 1 and <= 4 ? TorchWalls[meta - 1] : null;
            case DesignFamily.Wall: case DesignFamily.Front: case DesignFamily.Facing6: case DesignFamily.Hopper: case DesignFamily.Skull:
                return (meta & 7) is >= 2 and <= 5 ? FrontDirs[(meta & 7) - 2] : null;
            case DesignFamily.Trapdoor: return TrapWalls[meta & 3];
            case DesignFamily.Button: case DesignFamily.Lever: return (meta & 7) is >= 1 and <= 4 ? ButtonFaces[(meta & 7) - 1] : null;
            default: return null;
        }
    }

    /// <summary>The meta with its facing set to <paramref name="d"/> (the family's own sense of a direction, see <see cref="DesignFamily"/>).</summary>
    public static int WithFacing(int id, int meta, Dir d)
    {
        switch (FamilyOf(id))
        {
            case DesignFamily.Stairs: return (meta & ~3) | Array.IndexOf(StairDirs, d);
            case DesignFamily.Door: return (meta & ~3) | Array.IndexOf(DoorDirs, d);
            case DesignFamily.Bed: case DesignFamily.Horizontal: case DesignFamily.Gate: return (meta & ~3) | Array.IndexOf(SWNE, d);
            case DesignFamily.Torch: return meta is >= 1 and <= 4 ? Array.IndexOf(TorchWalls, d) + 1 : meta;
            case DesignFamily.Wall: case DesignFamily.Front: case DesignFamily.Facing6: case DesignFamily.Hopper: case DesignFamily.Skull:
                return (meta & 7) is >= 2 and <= 5 || FamilyOf(id) is DesignFamily.Wall or DesignFamily.Front ? (meta & 8) | (Array.IndexOf(FrontDirs, d) + 2) : meta;
            case DesignFamily.Trapdoor: return (meta & ~3) | Array.IndexOf(TrapWalls, d);
            case DesignFamily.Button: case DesignFamily.Lever:
                return (meta & 7) is >= 1 and <= 4 ? (meta & 8) | (Array.IndexOf(ButtonFaces, d) + 1) : meta;
            default: return meta;
        }
    }

    /// <summary>A meta turned <paramref name="quarters"/> clockwise (seen from above, north up).</summary>
    public static int Rotate(int id, int meta, int quarters)
    {
        int q = ((quarters % 4) + 4) % 4;
        if (q == 0) return meta;
        switch (FamilyOf(id))
        {
            case DesignFamily.Log when q % 2 == 1 && GameData.Current.VersionId == DesignVersions.Release:
            case DesignFamily.Pillar when q % 2 == 1:
                return (meta & 12) switch { 4 => (meta & ~12) | 8, 8 => (meta & ~12) | 4, _ => meta };
            case DesignFamily.Quartz when q % 2 == 1: return meta switch { 3 => 4, 4 => 3, _ => meta };
            case DesignFamily.Rotation16: return (meta + q * 4) & 15;
            case DesignFamily.Rail: return RailTurn(meta, q);
            case DesignFamily.PoweredRail: return (meta & 8) | RailTurn(meta & 7, q);
            case DesignFamily.Vine:
                {
                    // bits S W N E: a clockwise turn moves S→W→N→E→S.
                    int m = meta & 15;
                    for (int i = 0; i < q; i++) m = ((m << 1) | (m >> 3)) & 15;
                    return m;
                }
            case DesignFamily.Lever when q % 2 == 1 && (meta & 7) is 0 or 5 or 6 or 7:
                return (meta & 8) | ((meta & 7) switch { 5 => 6, 6 => 5, 0 => 7, _ => 0 });
        }
        return FacingOf(id, meta) is { } d ? WithFacing(id, meta, d.Turn(q)) : meta;
    }

    /// <summary>A meta mirrored across x ('x': east ↔ west) or z ('z': north ↔ south): the studio's convention.</summary>
    public static int Mirror(int id, int meta, char axis)
    {
        Dir M(Dir d) => axis == 'x' ? d switch { Dir.East => Dir.West, Dir.West => Dir.East, _ => d } : d switch { Dir.North => Dir.South, Dir.South => Dir.North, _ => d };
        switch (FamilyOf(id))
        {
            case DesignFamily.Rotation16: return axis == 'x' ? (16 - meta) & 15 : (8 - meta) & 15;
            case DesignFamily.Rail:
            case DesignFamily.PoweredRail:
                {
                    int shape = meta & (FamilyOf(id) == DesignFamily.Rail ? 15 : 7), rest = meta & ~(FamilyOf(id) == DesignFamily.Rail ? 15 : 7);
                    int m = shape switch
                    {
                        2 or 3 when axis == 'x' => shape ^ 1, 4 or 5 when axis == 'z' => shape ^ 1,
                        6 => axis == 'x' ? 7 : 9, 7 => axis == 'x' ? 6 : 8, 8 => axis == 'x' ? 9 : 7, 9 => axis == 'x' ? 8 : 6,
                        _ => shape,
                    };
                    return rest | m;
                }
            case DesignFamily.Vine:
                {
                    int m = meta & 15;
                    return axis == 'x' ? (m & 5) | ((m & 2) << 2) | ((m & 8) >> 2) : (m & 10) | ((m & 1) << 2) | ((m & 4) >> 2);
                }
        }
        return FacingOf(id, meta) is { } d ? WithFacing(id, meta, M(d)) : meta;
    }

    // Rail shapes: 2..5 ascending E W N S, 6..9 curves SE SW NW NE (clockwise in that order).
    private static int RailTurn(int shape, int q)
    {
        for (int i = 0; i < q; i++)
            shape = shape switch
            {
                0 => 1, 1 => 0,
                2 => 5, 5 => 3, 3 => 4, 4 => 2, // ascending east → south → west → north
                6 => 7, 7 => 8, 8 => 9, 9 => 6,
                _ => shape,
            };
        return shape;
    }

    // ------------------------------------------------------------------ the second cell of a two-cell block

    /// <summary>
    /// The other half a block brings with it on the canvas (a door's or a double plant's upper half above it, a bed's head one
    /// step along its facing), as (dx, dy, dz, meta); null = a one-cell block or itself the second half.
    /// </summary>
    public static (int Dx, int Dy, int Dz, int Meta)? Partner(int id, int meta)
    {
        switch (FamilyOf(id))
        {
            case DesignFamily.Door:
            case DesignFamily.Tall:
                return (meta & 8) == 0 ? (0, 1, 0, (meta & 7) | 8) : null;
            case DesignFamily.Bed:
                {
                    if ((meta & 8) != 0) return null;
                    var (dx, dz) = SWNE[meta & 3].Offset();
                    return (dx, 0, dz, (meta & 3) | 8);
                }
        }
        return null;
    }

    /// <summary>Where the first half of a second half stands, relative to it (an upper half's lower, a bed head's foot); null =
    /// not a second half.</summary>
    public static (int Dx, int Dy, int Dz)? FirstHalf(int id, int meta)
    {
        switch (FamilyOf(id))
        {
            case DesignFamily.Door:
            case DesignFamily.Tall:
                return (meta & 8) != 0 ? (0, -1, 0) : null;
            case DesignFamily.Bed:
                {
                    if ((meta & 8) == 0) return null;
                    var (dx, dz) = SWNE[meta & 3].Offset();
                    return (-dx, 0, -dz);
                }
        }
        return null;
    }

    /// <summary>Two cells of one block (doors, double plants, beds).</summary>
    public static bool TwoCell(int id) => FamilyOf(id) is DesignFamily.Door or DesignFamily.Tall or DesignFamily.Bed;

    /// <summary>The second half of a two-cell block (a door's or double plant's upper, a bed's head).</summary>
    public static bool SecondHalf(int id, int meta) => TwoCell(id) && (meta & 8) != 0;
}
