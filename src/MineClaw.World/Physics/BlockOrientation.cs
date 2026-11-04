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

    private static Rule RuleOf(int id, bool modern) => id switch
    {
        53 or 67 or 108 or 109 or 114 or 128 or 134 or 135 or 136 or 156 or 163 or 164 or 180 or 203 => Rule.Stairs,
        44 or 126 or 182 or 205 => modern ? Rule.Slab : Rule.None,
        17 or 162 => modern ? Rule.Log : Rule.None,
        170 or 202 or 216 => Rule.Pillar,
        155 => modern ? Rule.Quartz : Rule.None,
        64 or 71 or 193 or 194 or 195 or 196 or 197 => Rule.Door,
        26 => Rule.Bed,
        50 or 75 or 76 => Rule.Torch,
        65 => Rule.Ladder,
        54 or 146 or 61 or 62 => Rule.Front,
        23 => modern ? Rule.Facing6 : Rule.Front, // b1.8.1 BlockDispenser.postPlace: the furnace's rule (no up/down)
        158 or 29 or 33 => Rule.Facing6,
        218 => Rule.Observer,
        198 => Rule.OnFace,
        >= 219 and <= 234 => Rule.OnFace,
        154 => Rule.Hopper,
        86 or 91 or 93 or 94 or 149 or 150 => Rule.Away,
        >= 235 and <= 250 => Rule.Away,
        107 or 183 or 184 or 185 or 186 or 187 => Rule.Look,
        145 => Rule.Anvil,
        96 or 167 => Rule.Trapdoor,
        77 or 143 => Rule.Button,
        69 => Rule.Lever,
        131 => Rule.Hook,
        _ => Rule.None,
    };

    /// <summary>The block an item places, where they differ (doors, beds, repeaters, comparators, sugar cane), else the id.</summary>
    public static int BlockOf(int itemId) => itemId switch
    {
        324 => 64, 330 => 71, 427 => 193, 428 => 194, 429 => 195, 430 => 196, 431 => 197, 355 => 26, 356 => 93, 404 => 149,
        _ => PlaceRules.BlockOf(itemId),
    };

    /// <summary>The meta bits the placement decides (the facing, axis or half), compared after placing; 0 = none.</summary>
    public static int Mask(int id, bool modern) => RuleOf(id, modern) switch
    {
        Rule.Stairs => modern ? 7 : 3,
        Rule.Slab => 8,
        Rule.Log or Rule.Pillar => 12,
        Rule.Quartz => 15,
        Rule.Door or Rule.Bed or Rule.Away or Rule.Look or Rule.Anvil or Rule.Hook => 3,
        Rule.Trapdoor => modern ? 11 : 3,
        Rule.Torch or Rule.Ladder or Rule.Front or Rule.Facing6 or Rule.Observer or Rule.OnFace or Rule.Hopper or Rule.Button or Rule.Lever => 7,
        _ => 0,
    };

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
        if (modern)
        {
            if (Math.Abs(i.Feet.X - (x + 0.5f)) < 2.0 && Math.Abs(i.Feet.Z - (z + 0.5f)) < 2.0)
            {
                double eye = i.Feet.Y + (i.Sneaking ? 1.54 : 1.62);
                if (eye - y > 2.0) return 1;
                if (y - eye > 0.0) return 0;
            }
        }
        else if (Math.Abs((float)i.Feet.X - x) < 2.0f && Math.Abs((float)i.Feet.Z - z) < 2.0f)
        {
            double d0 = i.Feet.Y + 1.82; // locY + 1.82 - height (the server's player has height 0: locY is the feet)
            if (d0 - y > 2.0) return 1;
            if (y - d0 > 0.0) return 0;
        }
        return Opp(FaceOfQuarter[Quarter(i.Yaw)]);
    }

    /// <summary>
    /// The meta the server gives <paramref name="id"/> (placed from an item of <paramref name="damage"/>) at <paramref name="at"/> for
    /// this click; null = the click can't place it this way (a door off a top face, a torch under a ceiling) or the click doesn't decide
    /// it (a b1.8.1 floor lever). Neighbours are left out (a chest beside a chest, an end rod on an end rod).
    /// </summary>
    public static int? Predict(int id, int damage, BlockPos at, PlaceInput i, bool modern)
    {
        int q = Quarter(i.Yaw), face = (int)i.Face;
        bool side = Horizontal(i.Face);
        switch (RuleOf(id, modern))
        {
            case Rule.Stairs: return (5 - FaceOfQuarter[q]) | (modern && TopHalf(i.Face, i.CursorY) ? 4 : 0);
            case Rule.Slab: return (damage & 7) | (TopHalf(i.Face, i.CursorY) ? 8 : 0);
            case Rule.Log: return (damage & 3) | Axis(i.Face);
            case Rule.Pillar: return Axis(i.Face);
            case Rule.Quartz: return damage != 2 ? damage : Axis(i.Face) switch { 4 => 3, 8 => 4, _ => 2 };
            case Rule.Door: return i.Face == BlockFace.Top ? (q + 1) & 3 : null; // E0 S1 W2 N3: the look
            case Rule.Bed: return i.Face == BlockFace.Top ? q : null;          // S0 W1 N2 E3: foot → head along the look
            case Rule.Torch: return i.Face switch { BlockFace.Top => 5, BlockFace.East => 1, BlockFace.West => 2, BlockFace.South => 3, BlockFace.North => 4, _ => null };
            case Rule.Ladder: return side ? face : null;
            case Rule.Front: return Opp(FaceOfQuarter[q]);
            case Rule.Facing6: return TowardPlayer3D(at.X, at.Y, at.Z, i, modern);
            case Rule.Observer: return Opp(TowardPlayer3D(at.X, at.Y, at.Z, i, modern));
            case Rule.OnFace: return face;
            case Rule.Hopper: return Opp(face) == 1 ? 0 : Opp(face);
            case Rule.Away: return (q + 2) & 3;
            case Rule.Look: return q;
            case Rule.Anvil: return ((q + 1) & 3) | (damage & 12);
            case Rule.Hook: return side ? QuarterOfFace(face) : null;
            case Rule.Trapdoor:
            {
                static int Wall(int f) => f switch { 2 => 0, 3 => 1, 4 => 2, _ => 3 }; // BlockTrapDoor.getMetaForFacing: N0 S1 W2 E3
                if (side) return Wall(face) | (modern && i.CursorY > 0.5f ? 8 : 0);
                if (!modern) return null; // b1.8.1 canPlace: a side face only
                return Wall(Opp(FaceOfQuarter[q])) | (i.Face == BlockFace.Top ? 0 : 8);
            }
            case Rule.Button:
                if (side) return ButtonFace(face);
                return modern ? (i.Face == BlockFace.Bottom ? 0 : 5) : null;
            case Rule.Lever:
                if (side) return ButtonFace(face);
                if (!modern) return null; // b1.8.1: the floor is 5 or 6 at random, never the ceiling
                bool xAxis = q is 1 or 3;
                return i.Face == BlockFace.Top ? (xAxis ? 6 : 5) : (xAxis ? 0 : 7);
            default: return null;
        }
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
        155 => meta is >= 2 and <= 4 ? 2 : meta,
        145 => meta & 12,
        _ => 0,
    };

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
        static string Dir(int face) => face switch { 0 => "down", 1 => "up", 2 => "north", 3 => "south", 4 => "west", _ => "east" };
        static string Q(int q) => (q & 3) switch { 0 => "south", 1 => "west", 2 => "north", _ => "east" };
        switch (RuleOf(id, modern))
        {
            case Rule.Stairs: return $"rising {Q(new[] { 3, 1, 0, 2 }[meta & 3])}" + (modern && (meta & 4) != 0 ? ", upside down" : "");
            case Rule.Slab: return (meta & 8) != 0 ? "top half" : "bottom half";
            case Rule.Log: case Rule.Pillar: return (meta & 12) switch { 0 => "upright", 4 => "along x", 8 => "along z", _ => "bark all round" };
            case Rule.Quartz: return meta switch { 3 => "along x", 4 => "along z", _ => "upright" };
            case Rule.Door: return $"facing {Q(((modern ? meta : DoorLook(meta)) + 3) & 3)}";
            case Rule.Bed: case Rule.Look: case Rule.Away: case Rule.Hook: case Rule.Anvil: return $"facing {Q(meta)}";
            case Rule.Torch: return meta switch { 1 => "on the west wall", 2 => "on the east wall", 3 => "on the north wall", 4 => "on the south wall", _ => "on the floor" };
            case Rule.Button: case Rule.Lever:
                return (meta & 7) switch { 1 => "facing east", 2 => "facing west", 3 => "facing south", 4 => "facing north", 0 or 7 => "on the ceiling", _ => "on the floor" };
            case Rule.Trapdoor: return $"against the {new[] { "south", "north", "east", "west" }[meta & 3]} side" + (modern && (meta & 8) != 0 ? ", top half" : "");
            case Rule.Ladder: case Rule.Front: case Rule.Facing6: case Rule.Observer: case Rule.OnFace: case Rule.Hopper:
                return $"facing {Dir(meta & 7)}";
            default: return null;
        }
    }
}
