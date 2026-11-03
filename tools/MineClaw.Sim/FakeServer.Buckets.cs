using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.Data;

namespace MineClaw.Sim;

/// <summary>
/// Opt-in vanilla buckets (<see cref="Buckets"/>): ItemBucket has no onItemUse, so a block placement (b1.8.1 0x0F with a face,
/// 1.12.2 CPacketPlayerTryUseItemOnBlock) holding a full bucket changes nothing; only the use-item packet (0x0F direction −1 /
/// CPacketPlayerTryUseItem) pours, along the server's own raytrace: from the player's last sent feet + eye height (1.62;
/// 1.12.2 EntityPlayer.getEyeHeight while sneaking 1.62 − 0.08) along the last sent yaw/pitch, 5 blocks, passing air and
/// liquids (and on 1.12.2 blocks without a collision box), into the hit cell offset by the hit face.
/// </summary>
public partial class FakeServer
{
    /// <summary>Serve water/lava buckets like the vanilla server (see the class remarks). Off by default.</summary>
    public bool Buckets { get; set; }

    /// <summary>Every pour: where the liquid went.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<(int X, int Y, int Z)> Pours { get; } = new();

    private (double X, double Y, double Z)? _feet;
    private float _yaw, _pitch;
    private volatile bool _sneaking;

    private bool HandleBucket(IPacket p)
    {
        switch (p)
        {
            case PlayerPositionLookPacket pl: _feet = (pl.X, pl.Y, pl.Z); _yaw = pl.Yaw; _pitch = pl.Pitch; return false;
            case PlayerPositionPacket pp: _feet = (pp.X, pp.Y, pp.Z); return false;
            case PlayerLookPacket l: _yaw = l.Yaw; _pitch = l.Pitch; return false;
            case EntityActionPacket { Action: 1 or 2 } a: _sneaking = a.Action == 1; return false;
            case PlayerBlockPlacementPacket u when u.HeldItem.Id is Items.WaterBucket or Items.LavaBucket:
                if (u.Direction == PlayerBlockPlacementPacket.NoDirection) Pour(u.HeldItem.Id == Items.LavaBucket);
                return true; // with a face: onItemUse passes, nothing happens
            default:
                return false;
        }
    }

    private void Pour(bool lava)
    {
        var (fx, fy, fz) = _feet ?? SpawnAt;
        double ex = fx, ey = fy + (Modern && _sneaking ? 1.62 - 0.08 : 1.62), ez = fz;
        // Entity.getLook(1.0F) from the float angles (MathHelper.cos/sin of -yaw·π/180 − π and -pitch·π/180).
        double yr = -_yaw * 0.017453292f - Math.PI, pr = -_pitch * 0.017453292f;
        double dx = Math.Sin(yr) * -Math.Cos(pr), dy = Math.Sin(pr), dz = Math.Cos(yr) * -Math.Cos(pr);
        var prev = ((int)Math.Floor(ex), (int)Math.Floor(ey), (int)Math.Floor(ez));
        for (double t = 0.001; t <= 5.0; t += 0.001)
        {
            var c = ((int)Math.Floor(ex + dx * t), (int)Math.Floor(ey + dy * t), (int)Math.Floor(ez + dz * t));
            if (c == prev) continue;
            int id = Blocks.GetValueOrDefault(c);
            bool passes = id == 0 || id is >= 8 and <= 11 || (Modern && id is 31 or 37 or 38 or 50); // air, liquids; 1.12.2: no box
            if (!passes)
            {
                // The face the ray came in through = the step from the previous cell (an edge-on step: the axis moved first).
                var cell = (prev.Item1 - c.Item1) != 0 ? (prev.Item1, c.Item2, c.Item3)
                    : (prev.Item2 - c.Item2) != 0 ? (c.Item1, prev.Item2, c.Item3)
                    : (c.Item1, c.Item2, prev.Item3);
                int at = Blocks.GetValueOrDefault(cell);
                if (at != 0 && at is not (>= 8 and <= 11) && at != 31) return; // tryPlaceContainedLiquid: a solid block there
                SetBlock(cell.Item1, cell.Item2, cell.Item3, lava ? 11 : 9);
                Pours.Enqueue(cell);
                Inventory[36 + _heldSlot] = new ItemStack((short)Items.Bucket, 1, 0);
                Send(new SetSlotPacket { WindowId = 0, Slot = (short)(36 + _heldSlot), Item = Inventory[36 + _heldSlot] });
                return;
            }
            prev = c;
        }
    }
}
