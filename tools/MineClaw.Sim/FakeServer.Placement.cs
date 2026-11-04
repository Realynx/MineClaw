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
    public static int? VanillaMeta(bool modern, int id, int damage, int dir, float hitY, float yaw, (double X, double Y, double Z) feet, int x, int y, int z)
    {
        bool Modern = modern;
        int l = (int)Math.Floor(yaw * 4.0f / 360.0f + 0.5) & 3; // 0 south, 1 west, 2 north, 3 east
        var (fx, fy, fz) = feet;
        // EnumFacing indexes: the way the player looks, and its opposite (toward the player).
        int look = l switch { 0 => 3, 1 => 4, 2 => 2, _ => 5 };
        int toward = look ^ 1;
        bool topHalf = dir != 1 && (dir == 0 || hitY > 0.5f);
        switch (id)
        {
            case 61 or 62 or 54: // BlockFurnace / BlockChest (no neighbour chest here): the front toward the player
                return toward;
            case 23 when !Modern: // b1.8.1 BlockDispenser.postPlace: the furnace's rule
                return toward;
            case 23 or 29 or 33: // EnumFacing.getDirectionFromEntityLiving / b1.8.1 BlockPiston.c
                if (Modern ? Math.Abs(fx - (x + 0.5f)) < 2.0 && Math.Abs(fz - (z + 0.5f)) < 2.0 : Math.Abs((float)fx - x) < 2.0f && Math.Abs((float)fz - z) < 2.0f)
                {
                    double eye = Modern ? fy + 1.62 : fy + 1.82;
                    if (eye - y > 2.0) return 1;
                    if (y - eye > 0.0) return 0;
                }
                return toward;
            case 86 or 91: // BlockPumpkin: (yaw quarter + 2) & 3
                return (l + 2) & 3;
            case 53 or 67 or 108 or 109 or 114 or 128 or 134 or 135 or 136 or 156 or 163 or 164 or 180 or 203:
                // BlockStairs: 5 - FACING (the look), + 4 upside down (1.12.2)
                return (5 - look) | (Modern && topHalf ? 4 : 0);
            case 44 or 126 when Modern: // BlockSlab: + 8 the top half
                return (damage & 7) | (topHalf ? 8 : 0);
            case 17 or 162 when Modern: // BlockLog: the clicked face's axis (4 x, 8 z)
                return (damage & 3) | (dir is 4 or 5 ? 4 : dir is 2 or 3 ? 8 : 0);
            case 170 when Modern: // hay: BlockRotatedPillar
                return dir is 4 or 5 ? 4 : dir is 2 or 3 ? 8 : 0;
            case 50: // BlockTorch: the face it hangs off (5 the floor)
                return dir switch { 1 => 5, 5 => 1, 4 => 2, 3 => 3, 2 => 4, _ => 0 };
            case 65: // BlockLadder: the clicked side
                return dir is >= 2 and <= 5 ? dir : 0;
            case 96: // BlockTrapDoor: a side click: the side (N0 S1 W2 E3), + 8 high on it (1.12.2)
                if (dir >= 2) return (dir switch { 2 => 0, 3 => 1, 4 => 2, _ => 3 }) | (Modern && hitY > 0.5f ? 8 : 0);
                return Modern ? (toward switch { 2 => 0, 3 => 1, 4 => 2, _ => 3 }) | (dir == 1 ? 0 : 8) : 0;
            case 107: // BlockFenceGate: the look's quarter
                return l;
            default:
                return null;
        }
    }
}
