using MineClaw.Data;

namespace MineClaw.World;

/// <summary>What the server reads when a block is placed: the clicked face (of the block placed against), the cursor on it (1.12.2
/// sends it; b1.8.1 doesn't), the player's yaw and feet (the server's copy, from the last look and movement packets) and whether
/// the player sneaks (1.12.2's eye height drops to 1.54).</summary>
public readonly record struct PlaceInput(BlockFace Face, float Yaw, Vec3d Feet, float CursorX = 0.5f, float CursorY = 0.5f, float CursorZ = 0.5f,
    bool Sneaking = false);

/// <summary>A way to click that gives the wanted meta: the face, the yaw to say right before the click (null = any: the meta doesn't
/// read the yaw, so the aim's own yaw is used) and the cursor.</summary>
public readonly record struct Turn(BlockFace Face, float? Yaw, float CursorX, float CursorY, float CursorZ)
{
    public (float X, float Y, float Z) Cursor => (CursorX, CursorY, CursorZ);
}

/// <summary>
/// Which way a placed block faces, the way the vanilla server decides it (docs/web/story-mode.md "Rotation"), and the inverse: the
/// clicks that give a wanted meta. A forward model (<see cref="Predict"/>: face, cursor, yaw and feet → meta) and a search over it
/// (<see cref="Solve"/>), per version. Sources: 1.12.2 <c>getStateForPlacement</c> / <c>onBlockPlacedBy</c> / <c>getMetaFromState</c>
/// (the local decompile), b1.8.1 mc-dev (218a2a71) <c>postPlace</c> and the item classes. In both versions:
/// <list type="bullet">
/// <item>The player's horizontal facing is <c>floor(yaw·4/360 + 0.5) &amp; 3</c> (0 south, 1 west, 2 north, 3 east).</item>
/// <item>Furnaces, chests (and trapped chests), b1.8.1 dispensers, pumpkins, jack-o'-lanterns, glazed terracotta, repeaters and
/// comparators face the player (the opposite of where they look); stairs, fence gates, doors and beds take the look itself; an anvil
/// the look turned clockwise.</item>
/// <item>Pistons, 1.12.2 dispensers and droppers point at the player in 3D, and observers away: up when the player stands within 2
/// blocks (horizontally) and their eyes are more than 2 above the block's base, down when the eyes are below it, else horizontal
/// from the yaw. The pitch is never read; the stand cell decides up and down. b1.8.1 pistons measure from the block's corner and
/// count the eyes as feet + 1.82.</item>
/// <item>Torches, ladders, buttons, levers, end rods, shulker boxes, tripwire hooks and side-clicked trapdoors take the clicked
/// face; a hopper outputs into the block it was placed against; 1.12.2 logs and pillars lie along the clicked face's axis.</item>
/// <item>1.12.2 slabs, stairs and trapdoors take their half from the face and the cursor height: the top face (or a side below the
/// middle) gives a bottom half, the underside (or a side above the middle) a top half.</item>
/// <item>Doors and beds are placed on the top face of the block below (the second cell comes with them). A 1.12.2 door's hinge follows
/// the cursor (and the neighbours); designs don't keep it, so the left hinge is asked for. A b1.8.1 door may come out "flipped"
/// (meta (f − 1) | 4, the same look as f): both count as f.</item>
/// <item>Not decided by the click: rails (their shape follows the neighbours), a b1.8.1 lever on the floor (5 or 6 at random), a
/// chest next to a chest of its kind (both halves take the newer one's facing: a design's double chest faces one way).</item>
/// </list>
/// </summary>
public static class BlockOrientation
{
    // ------------------------------------------------------------------ the blocks

    private enum Rule
    {
        None, Stairs, Slab, Log, Pillar, Quartz, Door, Bed, Torch, Ladder, Front, Facing6, Observer, OnFace, Hopper, Away, Look, Anvil,
        Trapdoor, Button, Lever, Hook,
    }

    private static Rule RuleOf(int id, bool modern) =>
        (uint)id < RuleTableSize ? (modern ? ModernRules : BetaRules)[id] : Rule.None;

    private const int RuleTableSize = 256;
    private static readonly Rule[] BetaRules = BuildRules(modern: false), ModernRules = BuildRules(modern: true);

    private static Rule[] BuildRules(bool modern)
    {
        var r = new Rule[RuleTableSize];
        void Set(Rule rule, params int[] ids) { foreach (int id in ids) r[id] = rule; }
        void Range(Rule rule, int first, int last) { for (int id = first; id <= last; id++) r[id] = rule; }
        Set(Rule.Stairs, 53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203);
        if (modern)
        {
            Set(Rule.Slab, 44, 126, 182, 205);
            Set(Rule.Log, 17, 162);
            Set(Rule.Quartz, 155);
        }
        Set(Rule.Pillar, 170, 202, 216);
        Set(Rule.Door, 64, 71, 193, 194, 195, 196, 197);
        Set(Rule.Bed, 26);
        Set(Rule.Torch, 50, 75, 76);
        Set(Rule.Ladder, 65);
        Set(Rule.Front, 54, 146, 61, 62);
        Set(modern ? Rule.Facing6 : Rule.Front, 23); // b1.8.1 BlockDispenser.postPlace: the furnace's rule (no up/down)
        Set(Rule.Facing6, 158, 29, 33);
        Set(Rule.Observer, 218);
        Set(Rule.OnFace, 198);
        Range(Rule.OnFace, 219, 234);
        Set(Rule.Hopper, 154);
        Set(Rule.Away, 86, 91, 93, 94, 149, 150);
        Range(Rule.Away, 235, 250);
        Set(Rule.Look, 107, 183, 184, 185, 186, 187);
        Set(Rule.Anvil, 145);
        Set(Rule.Trapdoor, 96, 167);
        Set(Rule.Button, 77, 143);
        Set(Rule.Lever, 69);
        Set(Rule.Hook, 131);
        return r;
    }

    /// <summary>The block an item places, where they differ (doors, beds, repeaters, comparators, sugar cane), else the id.</summary>
    public static int BlockOf(int itemId) => ItemBlocks.TryGetValue(itemId, out int block) ? block : PlaceRules.BlockOf(itemId);

    private static readonly Dictionary<int, int> ItemBlocks = new()
    {
        [324] = 64, [330] = 71, [427] = 193, [428] = 194, [429] = 195, [430] = 196, [431] = 197, [355] = 26, [356] = 93, [404] = 149,
    };

    /// <summary>The meta bits the placement decides (the facing, axis or half), compared after placing; 0 = none.</summary>
    public static int Mask(int id, bool modern) => RuleOf(id, modern) switch
    {
        Rule.Stairs => modern ? 7 : 3,
        Rule.Trapdoor => modern ? 11 : 3,
        var rule => RuleMasks[(int)rule],
    };

    /// <summary>The meta bits each rule decides (stairs and trapdoors are per version, above).</summary>
    private static readonly int[] RuleMasks = BuildRuleMasks();

    private static int[] BuildRuleMasks()
    {
        var m = new int[(int)Rule.Hook + 1];
        foreach (var r in new[] { Rule.Door, Rule.Bed, Rule.Away, Rule.Look, Rule.Anvil, Rule.Hook }) m[(int)r] = 3;
        foreach (var r in new[] { Rule.Torch, Rule.Ladder, Rule.Front, Rule.Facing6, Rule.Observer, Rule.OnFace, Rule.Hopper, Rule.Button, Rule.Lever })
            m[(int)r] = 7;
        m[(int)Rule.Slab] = 8;
        m[(int)Rule.Log] = m[(int)Rule.Pillar] = 12;
        m[(int)Rule.Quartz] = 15;
        return m;
    }

    /// <summary>Whether placing (id, meta) has to aim for its facing: the click decides some of it, and that state can be reached by a
    /// click (a log's all-bark axis, a b1.8.1 dispenser facing up, a torch meta 0 can't; any click is then as good).</summary>
    public static bool Turns(int id, int meta, bool modern)
    {
        var rule = RuleOf(id, modern);
        if (rule == Rule.None) return false;
        if (rule == Rule.Quartz) return meta is >= 2 and <= 4;
        if (rule == Rule.Door && (meta & 8) != 0) return false; // an upper half comes with its lower
        if (rule == Rule.Bed && (meta & 8) != 0) return false;  // a head comes with its foot
        return Reachable(id, meta, modern);
    }

    /// <summary>Whether a placed meta is the wanted one as far as the placement goes (the variant bits are the caller's): the facing,
    /// axis or half bits agree, a b1.8.1 flipped door counts as its look, a b1.8.1 floor lever as either floor meta.</summary>
    public static bool Same(int id, int want, int got, bool modern)
    {
        var rule = RuleOf(id, modern);
        if (rule == Rule.None || !Turns(id, want, modern)) return true;
        if (rule == Rule.Door && !modern) return DoorLook(want) == DoorLook(got);
        if (rule == Rule.Lever && !modern && (want & 7) is 5 or 6) return (got & 7) is 5 or 6;
        int m = Mask(id, modern);
        return (want & m) == (got & m);
    }

    /// <summary>b1.8.1 ItemDoor: a flipped door is ((f − 1) &amp; 3) | 4, which looks like f.</summary>
    private static int DoorLook(int meta) => (meta & 4) != 0 ? ((meta & 3) + 1) & 3 : meta & 3;

    // ------------------------------------------------------------------ the forward model

    /// <summary>The player's horizontal facing (EnumFacing.getHorizontal / mc-dev's yaw quarter): 0 south, 1 west, 2 north, 3 east.</summary>
    public static int Quarter(float yaw) => (int)Math.Floor(yaw * 4.0f / 360.0f + 0.5) & 3;

    /// <summary>The yaw (degrees, Minecraft: 0 south, 90 west, 180 north, 270 east) of a quarter.</summary>
    public static float YawOf(int quarter) => (quarter & 3) * 90f;

    // Horizontal index (S0 W1 N2 E3) ↔ face index (D0 U1 N2 S3 W4 E5).
    private static readonly int[] FaceOfQuarter = [3, 4, 2, 5];
    private static int QuarterOfFace(int face) => face switch { 3 => 0, 4 => 1, 2 => 2, _ => 3 };
    private static int Opp(int face) => face ^ 1; // D↔U, N↔S, W↔E
    private static int Axis(BlockFace f) => f switch { BlockFace.West or BlockFace.East => 4, BlockFace.North or BlockFace.South => 8, _ => 0 };
    private static bool Horizontal(BlockFace f) => f is BlockFace.North or BlockFace.South or BlockFace.West or BlockFace.East;

    /// <summary>Slabs, stairs, 1.12.2 trapdoors clicked on a side: the top half unless the click is on the top face or low on a side.</summary>
    private static bool TopHalf(BlockFace f, float cursorY) => f != BlockFace.Top && (f == BlockFace.Bottom || cursorY > 0.5f);

    /// <summary>
    /// The 3D facing a piston, dispenser or dropper takes (EnumFacing.getDirectionFromEntityLiving; b1.8.1 BlockPiston.c): up, down,
    /// or toward the player from the yaw. Face index.
    /// </summary>
    private static int TowardPlayer3D(int x, int y, int z, PlaceInput i, bool modern)
    {
        int vertical = modern ? VerticalModern(x, y, z, i) : VerticalBeta(x, y, z, i);
        return vertical >= 0 ? vertical : Opp(FaceOfQuarter[Quarter(i.Yaw)]);
    }

    /// <summary>1.12.2: up or down (face index) when the player stands within 2 of the block's centre, else −1 (horizontal).</summary>
    private static int VerticalModern(int x, int y, int z, PlaceInput i)
    {
        if (!(Math.Abs(i.Feet.X - (x + 0.5f)) < 2.0 && Math.Abs(i.Feet.Z - (z + 0.5f)) < 2.0)) return -1;
        double eye = i.Feet.Y + (i.Sneaking ? 1.54 : 1.62);
        return VerticalFromEye(eye, y);
    }

    /// <summary>b1.8.1 BlockPiston.c: measured from the block's corner, the eyes counted as feet + 1.82.</summary>
    private static int VerticalBeta(int x, int y, int z, PlaceInput i)
    {
        if (!(Math.Abs((float)i.Feet.X - x) < 2.0f && Math.Abs((float)i.Feet.Z - z) < 2.0f)) return -1;
        double d0 = i.Feet.Y + 1.82; // locY + 1.82 - height (the server's player has height 0: locY is the feet)
        return VerticalFromEye(d0, y);
    }

    private static int VerticalFromEye(double eye, int y)
    {
        if (eye - y > 2.0) return 1;
        if (y - eye > 0.0) return 0;
        return -1;
    }

    /// <summary>
    /// The meta the server gives <paramref name="id"/> (placed from an item of <paramref name="damage"/>) at <paramref name="at"/> for
    /// this click; null = the click can't place it this way (a door off a top face, a torch under a ceiling) or the click doesn't decide
    /// it (a b1.8.1 floor lever). Neighbours are left out (a chest beside a chest, an end rod on an end rod).
    /// </summary>
    public static int? Predict(int id, int damage, BlockPos at, PlaceInput i, bool modern)
    {
        var rule = RuleOf(id, modern);
        if (rule == Rule.None) return null;
        int q = Quarter(i.Yaw);
        if (rule <= Rule.Bed) return PredictShape(rule, damage, i, q, modern);
        if (rule <= Rule.Hopper) return PredictFacing(rule, at, i, q, modern);
        return PredictTurned(rule, damage, i, q, modern);
    }

    /// <summary>Stairs, slabs, logs, pillars, quartz, doors and beds: the look's quarter, the clicked face's axis or half.</summary>
    private static int? PredictShape(Rule rule, int damage, PlaceInput i, int q, bool modern) => rule switch
    {
        Rule.Stairs => (5 - FaceOfQuarter[q]) | StairsHalfBit(i, modern),
        Rule.Slab => (damage & 7) | SlabHalfBit(i),
        Rule.Log => (damage & 3) | Axis(i.Face),
        Rule.Pillar => Axis(i.Face),
        Rule.Quartz => damage != 2 ? damage : QuartzAxis(i.Face),
        Rule.Door => OnTop(i, (q + 1) & 3), // E0 S1 W2 N3: the look
        _ => OnTop(i, q),                   // a bed, S0 W1 N2 E3: foot → head along the look
    };

    /// <summary>1.12.2 stairs placed upside down (bit 4) by the face and cursor height; b1.8.1 stairs are never.</summary>
    private static int StairsHalfBit(PlaceInput i, bool modern) => modern && TopHalf(i.Face, i.CursorY) ? 4 : 0;

    private static int SlabHalfBit(PlaceInput i) => TopHalf(i.Face, i.CursorY) ? 8 : 0;

    private static int QuartzAxis(BlockFace f) => Axis(f) switch { 4 => 3, 8 => 4, _ => 2 };

    /// <summary>Doors and beds stand on the top face of the block below: any other face can't place them.</summary>
    private static int? OnTop(PlaceInput i, int meta) => i.Face == BlockFace.Top ? meta : null;

    /// <summary>Torches, ladders, furnace-likes, pistons, observers, on-face blocks and hoppers: the clicked face or the player.</summary>
    private static int? PredictFacing(Rule rule, BlockPos at, PlaceInput i, int q, bool modern)
    {
        int face = (int)i.Face;
        return rule switch
        {
            Rule.Torch => TorchMeta(i.Face),
            Rule.Ladder => Horizontal(i.Face) ? face : null,
            Rule.Front => Opp(FaceOfQuarter[q]),
            Rule.Facing6 => TowardPlayer3D(at.X, at.Y, at.Z, i, modern),
            Rule.Observer => Opp(TowardPlayer3D(at.X, at.Y, at.Z, i, modern)),
            Rule.OnFace => face,
            _ => Opp(face) == 1 ? 0 : Opp(face), // a hopper
        };
    }

    private static int? TorchMeta(BlockFace f) =>
        f switch { BlockFace.Top => 5, BlockFace.East => 1, BlockFace.West => 2, BlockFace.South => 3, BlockFace.North => 4, _ => null };

    /// <summary>Away-facing blocks, gates, anvils, trapdoors, buttons, levers and hooks.</summary>
    private static int? PredictTurned(Rule rule, int damage, PlaceInput i, int q, bool modern) => rule switch
    {
        Rule.Away => (q + 2) & 3,
        Rule.Look => q,
        Rule.Anvil => ((q + 1) & 3) | (damage & 12),
        Rule.Trapdoor => TrapdoorMeta(i, q, modern),
        Rule.Button => ButtonMeta(i, modern),
        Rule.Lever => LeverMeta(i, q, modern),
        _ => Horizontal(i.Face) ? QuarterOfFace((int)i.Face) : null, // a tripwire hook
    };

    private static int? TrapdoorMeta(PlaceInput i, int q, bool modern)
    {
        static int Wall(int f) => f switch { 2 => 0, 3 => 1, 4 => 2, _ => 3 }; // BlockTrapDoor.getMetaForFacing: N0 S1 W2 E3
        if (Horizontal(i.Face)) return Wall((int)i.Face) | (modern && i.CursorY > 0.5f ? 8 : 0);
        if (!modern) return null; // b1.8.1 canPlace: a side face only
        return Wall(Opp(FaceOfQuarter[q])) | (i.Face == BlockFace.Top ? 0 : 8);
    }

    private static int? ButtonMeta(PlaceInput i, bool modern)
    {
        if (Horizontal(i.Face)) return ButtonFace((int)i.Face);
        return modern ? (i.Face == BlockFace.Bottom ? 0 : 5) : null;
    }

    private static int? LeverMeta(PlaceInput i, int q, bool modern)
    {
        if (Horizontal(i.Face)) return ButtonFace((int)i.Face);
        if (!modern) return null; // b1.8.1: the floor is 5 or 6 at random, never the ceiling
        bool xAxis = q is 1 or 3;
        return i.Face == BlockFace.Top ? (xAxis ? 6 : 5) : (xAxis ? 0 : 7);
    }

    private static int ButtonFace(int face) => face switch { 5 => 1, 4 => 2, 3 => 3, _ => 4 }; // E1 W2 S3 N4

    // ------------------------------------------------------------------ the inverse

    /// <summary>Whether some stand and click reaches the meta (tried from a few representative stands around the cell).</summary>
    private static bool Reachable(int id, int meta, bool modern) => ReachCache.GetOrAdd((id, meta & 15, modern), k => ReachableCore(k.Id, k.Meta, k.Modern));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Id, int Meta, bool Modern), bool> ReachCache = new();

    private static bool ReachableCore(int id, int meta, bool modern)
    {
        var at = new BlockPos(0, 64, 0);
        foreach (var feet in new Vec3d[] { new(0.5, 64, 3.5), new(1.5, 65, 0.5), new(0.5, 62, 1.5) })
            if (SolveCore(id, Damage(id, meta), meta, at, feet, modern, false, _ => true).Any()) return true;
        return false;
    }

    /// <summary>The item damage that places (id, meta): its variant bits (a log's wood, a slab's material, an anvil's wear).</summary>
    public static int Damage(int id, int meta) => id switch
    {
        17 or 162 => meta & 3,
        44 or 126 or 182 or 205 => meta & 7,
        155 => QuartzDamage(meta),
        145 => meta & 12,
        _ => 0,
    };

    /// <summary>Quartz: the pillar (2..4, any axis) is placed from damage 2; chiseled and plain keep theirs.</summary>
    private static int QuartzDamage(int meta) => meta is >= 2 and <= 4 ? 2 : meta;

    /// <summary>
    /// Every click that gives (id, meta) from a player whose feet are at <paramref name="feet"/>, over the faces
    /// <paramref name="usable"/> allows (the block behind the face is something to click). The yaw is null when the meta doesn't read it.
    /// Side faces are tried low and high (a half), a door's top face at the cursor that asks for the left hinge.
    /// </summary>
    public static IEnumerable<Turn> Solve(int id, int meta, BlockPos at, Vec3d feet, bool modern, Func<BlockFace, bool> usable, bool sneaking = false) =>
        SolveCore(id, Damage(id, meta), meta, at, feet, modern, sneaking, usable);

    private static IEnumerable<Turn> SolveCore(int id, int damage, int meta, BlockPos at, Vec3d feet, bool modern, bool sneaking, Func<BlockFace, bool> usable)
    {
        int mask = Mask(id, modern);
        foreach (var face in Faces)
        {
            if (!usable(face)) continue;
            foreach (var (cx, cy, cz) in Cursors(face))
            {
                var yaws = new List<int>(4);
                for (int q = 0; q < 4; q++)
                {
                    var got = Predict(id, damage, at, new PlaceInput(face, YawOf(q), feet, cx, cy, cz, sneaking), modern);
                    if (got is { } m && SameBits(id, meta, m, mask, modern)) yaws.Add(q);
                }
                if (yaws.Count == 0) continue;
                if (yaws.Count == 4) { yield return new Turn(face, null, cx, cy, cz); continue; }
                int pick = yaws[0];
                var (hx, hz) = RuleOf(id, modern) == Rule.Door ? LeftHinge(pick) : (cx, cz);
                yield return new Turn(face, YawOf(pick), hx, cy, hz);
            }
        }
    }

    private static bool SameBits(int id, int want, int got, int mask, bool modern)
    {
        var rule = RuleOf(id, modern);
        if (rule == Rule.Door && !modern) return DoorLook(want) == DoorLook(got);
        return (want & mask) == (got & mask);
    }

    // Top first (the floor under the cell is the usual support), then the sides, the underside last.
    private static readonly BlockFace[] Faces = [BlockFace.Top, BlockFace.North, BlockFace.South, BlockFace.West, BlockFace.East, BlockFace.Bottom];

    /// <summary>The cursors tried on a face: a side low (bottom half) then high (top half); the top and bottom faces at their centre.</summary>
    private static IEnumerable<(float X, float Y, float Z)> Cursors(BlockFace f)
    {
        if (!Horizontal(f)) { yield return PlaceCursor.For(f); yield break; }
        yield return PlaceCursor.For(f, PlaceHalf.Bottom);
        yield return PlaceCursor.For(f, PlaceHalf.Top);
    }

    /// <summary>
    /// 1.12.2 ItemDoor.onItemUse: the hinge is on the right when (looking along x) the cursor's z is on the look's side… exactly:
    /// <c>i &lt; 0 &amp;&amp; hitZ &lt; 0.5 || i &gt; 0 &amp;&amp; hitZ &gt; 0.5 || j &lt; 0 &amp;&amp; hitX &gt; 0.5 || j &gt; 0 &amp;&amp; hitX &lt; 0.5</c>
    /// with (i, j) the look's step; the top-face cursor (x, z) that asks for the left hinge (neighbours may still turn it).
    /// </summary>
    public static (float X, float Z) LeftHinge(int quarter) => (quarter & 3) switch
    {
        0 => (0.75f, 0.5f), // south: j > 0, right when hitX < 0.5
        1 => (0.5f, 0.75f), // west: i < 0, right when hitZ < 0.5
        2 => (0.25f, 0.5f), // north: j < 0, right when hitX > 0.5
        _ => (0.5f, 0.25f), // east: i > 0, right when hitZ > 0.5
    };

    // ------------------------------------------------------------------ words

    /// <summary>"facing east", "axis x", "top half"… for messages; null when the meta says nothing the placement decides.</summary>
    public static string? Describe(int id, int meta, bool modern)
    {
        var rule = RuleOf(id, modern);
        if (rule == Rule.None) return null;
        if (rule <= Rule.Bed) return DescribeShape(rule, meta, modern);
        return DescribeAttached(rule, meta, modern);
    }

    private static string DirName(int face) => face switch { 0 => "down", 1 => "up", 2 => "north", 3 => "south", 4 => "west", _ => "east" };
    private static string QuarterName(int q) => (q & 3) switch { 0 => "south", 1 => "west", 2 => "north", _ => "east" };
    private static readonly int[] StairRise = [3, 1, 0, 2];
    private static readonly string[] TrapdoorSide = ["south", "north", "east", "west"];

    private static string DescribeShape(Rule rule, int meta, bool modern) => rule switch
    {
        Rule.Stairs => $"rising {QuarterName(StairRise[meta & 3])}" + UpsideDownWords(meta, modern),
        Rule.Slab => SlabWords(meta),
        Rule.Log or Rule.Pillar => AxisWords(meta),
        Rule.Quartz => QuartzWords(meta),
        Rule.Door => $"facing {QuarterName((DoorFacing(meta, modern) + 3) & 3)}",
        _ => $"facing {QuarterName(meta)}", // a bed
    };

    private static string UpsideDownWords(int meta, bool modern) => modern && (meta & 4) != 0 ? ", upside down" : "";
    private static string SlabWords(int meta) => (meta & 8) != 0 ? "top half" : "bottom half";
    private static string AxisWords(int meta) => (meta & 12) switch { 0 => "upright", 4 => "along x", 8 => "along z", _ => "bark all round" };
    private static string QuartzWords(int meta) => meta switch { 3 => "along x", 4 => "along z", _ => "upright" };
    private static int DoorFacing(int meta, bool modern) => modern ? meta : DoorLook(meta);

    private static string DescribeAttached(Rule rule, int meta, bool modern) => rule switch
    {
        Rule.Torch => TorchWords(meta),
        Rule.Button or Rule.Lever => SwitchPlace(meta & 7),
        Rule.Trapdoor => $"against the {TrapdoorSide[meta & 3]} side" + TopHalfWords(meta, modern),
        Rule.Look or Rule.Away or Rule.Hook or Rule.Anvil => $"facing {QuarterName(meta)}",
        _ => $"facing {DirName(meta & 7)}", // ladders, furnace-likes, pistons, observers, on-face blocks, hoppers
    };

    private static string TorchWords(int meta) =>
        meta switch { 1 => "on the west wall", 2 => "on the east wall", 3 => "on the north wall", 4 => "on the south wall", _ => "on the floor" };

    private static string TopHalfWords(int meta, bool modern) => modern && (meta & 8) != 0 ? ", top half" : "";

    private static string SwitchPlace(int m) =>
        m switch { 1 => "facing east", 2 => "facing west", 3 => "facing south", 4 => "facing north", 0 or 7 => "on the ceiling", _ => "on the floor" };
}
