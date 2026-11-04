using MineClaw.Protocol;
using MineClaw.Protocol.Packets;

namespace MineClaw.Sim;

/// <summary>
/// The server side of a placed block's facing (opt-in, <see cref="OrientBlocks"/>): the meta vanilla gives from the placer's yaw,
/// position, the clicked face and the cursor, written out here from the game's own code (1.12.2 getStateForPlacement /
/// onBlockPlacedBy / getMetaFromState; b1.8.1 mc-dev postPlace), on its own and apart from the client's
/// <c>MineClaw.World.BlockOrientation</c>, so a test checks the client against the rules rather than against itself. Covers the
/// blocks the Story acceptance tests build: furnaces, chests, dispensers, pistons, pumpkins, stairs, slabs, logs, pillars, torches,
/// ladders, trapdoors, fence gates. The placer's look and feet are the last ones the client sent.
/// </summary>
public partial class FakeServer
{
    /// <summary>Placed blocks take the placer's facing, axis or half (else the old rule: meta 0, a torch from its face).</summary>
    public bool OrientBlocks { get; set; }

    private float _placerYaw;
    private (double X, double Y, double Z)? _placerFeet;

    /// <summary>The server's copy of the player's look and feet (processPlayer), at the moment the next packet is handled.</summary>
    private void TrackPlacer(IPacket p)
    {
        switch (p)
        {
            case PlayerPositionLookPacket pl: _placerFeet = (pl.X, pl.Y, pl.Z); _placerYaw = pl.Yaw; break;
            case PlayerPositionPacket pp: _placerFeet = (pp.X, pp.Y, pp.Z); break;
            case PlayerLookPacket l: _placerYaw = l.Yaw; break;
        }
    }

    /// <summary>The meta a block placed at (x, y, z) from this packet gets; null = the old rule.</summary>
    private int? OrientedMeta(PlayerBlockPlacementPacket place, int id, int damage, int x, int y, int z) =>
        OrientBlocks ? VanillaMeta(Modern, id, damage, place.Direction, place.EffectiveCursor.Y, _placerYaw, _placerFeet ?? SpawnAt, x, y, z) : null;

    /// <summary>The vanilla meta of block <paramref name="id"/> placed at (x, y, z) by clicking face <paramref name="dir"/> (0 down, 1 up,
    /// 2 north, 3 south, 4 west, 5 east) at cursor height <paramref name="hitY"/>, the placer looking <paramref name="yaw"/> with its feet
    /// at <paramref name="feet"/>; null = a block this model leaves alone.</summary>
    public static int? VanillaMeta(bool modern, int id, int damage, int dir, float hitY, float yaw, (double X, double Y, double Z) feet, int x, int y, int z) =>
        PlacementRules.TryGetValue(id, out var rule) ? rule(new Placing(modern, damage, dir, hitY, yaw, feet, x, y, z)) : null;

    /// <summary>One placement as the rules read it, with the placer's yaw quarter and facings worked out.</summary>
    private readonly struct Placing
    {
        public readonly bool Modern;
        public readonly int Damage, Dir, X, Y, Z;
        public readonly float HitY;
        public readonly (double X, double Y, double Z) Feet;
        /// <summary>The yaw quarter: 0 south, 1 west, 2 north, 3 east.</summary>
        public readonly int Quarter;
        /// <summary>EnumFacing indexes: the way the player looks, and its opposite (toward the player).</summary>
        public readonly int Look, Toward;
        public readonly bool TopHalf;

        public Placing(bool modern, int damage, int dir, float hitY, float yaw, (double X, double Y, double Z) feet, int x, int y, int z)
        {
            Modern = modern;
            Damage = damage;
            Dir = dir;
            HitY = hitY;
            Feet = feet;
            X = x;
            Y = y;
            Z = z;
            Quarter = (int)Math.Floor(yaw * 4.0f / 360.0f + 0.5) & 3;
            Look = Quarter switch { 0 => 3, 1 => 4, 2 => 2, _ => 5 };
            Toward = Look ^ 1;
            TopHalf = dir != 1 && (dir == 0 || hitY > 0.5f);
        }

        /// <summary>The axis of the clicked face (BlockRotatedPillar / BlockLog: 4 x, 8 z, 0 y).</summary>
        public int Axis => Dir is 4 or 5 ? 4 : Dir is 2 or 3 ? 8 : 0;
    }

    /// <summary>The placement rule of each block id this model orients.</summary>
    private static readonly Dictionary<int, Func<Placing, int?>> PlacementRules = BuildPlacementRules();

    private static Dictionary<int, Func<Placing, int?>> BuildPlacementRules()
    {
        var r = new Dictionary<int, Func<Placing, int?>>();
        void Each(Func<Placing, int?> rule, params int[] ids)
        {
            foreach (int id in ids) r.Add(id, rule);
        }
        Each(p => p.Toward, 61, 62, 54); // BlockFurnace / BlockChest (no neighbour chest here): the front toward the player
        Each(p => p.Modern ? PistonFacing(p) : p.Toward, 23); // b1.8.1 BlockDispenser.postPlace: the furnace's rule
        Each(PistonFacing, 29, 33);
        Each(p => (p.Quarter + 2) & 3, 86, 91); // BlockPumpkin: (yaw quarter + 2) & 3
        // BlockStairs: 5 - FACING (the look), + 4 upside down (1.12.2)
        Each(p => (5 - p.Look) | (p.Modern && p.TopHalf ? 4 : 0), 53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203);
        Each(p => p.Modern ? (p.Damage & 7) | (p.TopHalf ? 8 : 0) : null, 44, 126); // BlockSlab: + 8 the top half (1.12.2)
        Each(p => p.Modern ? (p.Damage & 3) | p.Axis : null, 17, 162); // BlockLog: the clicked face's axis (1.12.2)
        Each(p => p.Modern ? p.Axis : null, 170); // hay: BlockRotatedPillar (1.12.2)
        Each(p => p.Dir switch { 1 => 5, 5 => 1, 4 => 2, 3 => 3, 2 => 4, _ => 0 }, 50); // BlockTorch: the face it hangs off (5 the floor)
        Each(p => p.Dir is >= 2 and <= 5 ? p.Dir : 0, 65); // BlockLadder: the clicked side
        Each(TrapDoorMeta, 96);
        Each(p => p.Quarter, 107); // BlockFenceGate: the look's quarter
        return r;
    }

    /// <summary>EnumFacing.getDirectionFromEntityLiving / b1.8.1 BlockPiston.c: up or down when the placer stands close by and its
    /// eyes are well above or below, else toward the player.</summary>
    private static int? PistonFacing(Placing p)
    {
        var (fx, fy, fz) = p.Feet;
        if (p.Modern ? Math.Abs(fx - (p.X + 0.5f)) < 2.0 && Math.Abs(fz - (p.Z + 0.5f)) < 2.0 : Math.Abs((float)fx - p.X) < 2.0f && Math.Abs((float)fz - p.Z) < 2.0f)
        {
            double eye = p.Modern ? fy + 1.62 : fy + 1.82;
            if (eye - p.Y > 2.0) return 1;
            if (p.Y - eye > 0.0) return 0;
        }
        return p.Toward;
    }

    /// <summary>BlockTrapDoor: a side click: the side (N0 S1 W2 E3), + 8 high on it (1.12.2); a top or bottom click (1.12.2): the
    /// side toward the player, + 8 from below.</summary>
    private static int? TrapDoorMeta(Placing p)
    {
        if (p.Dir >= 2) return TrapDoorSide(p.Dir) | (p.Modern && p.HitY > 0.5f ? 8 : 0);
        return p.Modern ? TrapDoorSide(p.Toward) | (p.Dir == 1 ? 0 : 8) : 0;
    }

    /// <summary>A trapdoor's side bits from an EnumFacing index (north 0, south 1, west 2, east 3).</summary>
    private static int TrapDoorSide(int facing) => facing switch { 2 => 0, 3 => 1, 4 => 2, _ => 3 };
}
