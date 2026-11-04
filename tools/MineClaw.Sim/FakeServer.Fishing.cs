using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;

namespace MineClaw.Sim;

/// <summary>
/// Opt-in vanilla fishing (<see cref="Fishing"/>), enough for the client's fish primitive: a use-item holding a rod with no hook out
/// casts (a Spawn Object of type 90 whose thrower/owner is the player: b1.8.1 0x17, 1.12.2 Spawn Object data), the hook floats,
/// after <see cref="BiteAfterMs"/> a bite pulls it under (an Entity Velocity with a downward y, as EntityFishHook does), and a
/// use-item within <see cref="BiteWindowMs"/> of it lands a raw fish in the inventory and the hook goes; any other reel-in just
/// pulls the hook back.
/// </summary>
public partial class FakeServer
{
    /// <summary>Serve fishing rods (see the class remarks). Off by default.</summary>
    public bool Fishing { get; set; }
    public int BiteAfterMs { get; set; } = 900;
    public int BiteWindowMs { get; set; } = 1500;
    /// <summary>Fish landed (each reel-in inside the bite window).</summary>
    public int FishLanded => _landed;
    /// <summary>Hooks cast.</summary>
    public int Casts => _casts;

    private int _hookId, _landed, _casts, _hookSeq = 9000;
    private DateTime _biteAt = DateTime.MaxValue;
    private Timer? _biteTimer;

    private bool HandleFishing(IPacket p)
    {
        if (p is not PlayerBlockPlacementPacket { Direction: PlayerBlockPlacementPacket.NoDirection } u || u.HeldItem.Id != 346) return false;
        if (_hookId == 0)
        {
            _hookId = Interlocked.Increment(ref _hookSeq);
            Interlocked.Increment(ref _casts);
            var (x, y, z) = SpawnAt;
            Send(new AddObjectVehiclePacket
            {
                EntityId = _hookId, Type = 90, X = (int)Math.Floor((x + 3) * 32), Y = (int)Math.Floor((y - 1) * 32), Z = (int)Math.Floor(z * 32),
                ThrowerEntityId = EntityId, SpeedX = 0, SpeedY = 0, SpeedZ = 0,
            });
            int hook = _hookId;
            _biteAt = DateTime.MaxValue;
            _biteTimer?.Dispose();
            _biteTimer = new Timer(_ =>
            {
                if (_hookId != hook) return;
                _biteAt = DateTime.UtcNow;
                Send(new EntityVelocityPacket { EntityId = hook, VelocityX = 0, VelocityY = -1600, VelocityZ = 0 }); // −0.2 blocks/tick
                Send(new EntityRelativeMovePacket { EntityId = hook, DX = 0, DY = -10, DZ = 0 });
            }, null, BiteAfterMs, Timeout.Infinite);
            return true;
        }
        // Reel in: a fish when it bit a moment ago.
        int gone = _hookId;
        _hookId = 0;
        _biteTimer?.Dispose();
        if ((DateTime.UtcNow - _biteAt).TotalMilliseconds <= BiteWindowMs)
        {
            Interlocked.Increment(ref _landed);
            int slot = Array.FindIndex(Inventory, 9, s => s.Id == 349 && s.Count < 64);
            if (slot < 0) slot = Array.FindIndex(Inventory, 9, s => s.IsEmpty);
            if (slot >= 0)
            {
                Inventory[slot] = new ItemStack(349, (byte)(Inventory[slot].Id == 349 ? Inventory[slot].Count + 1 : 1), 0);
                Send(new SetSlotPacket { WindowId = 0, Slot = (short)slot, Item = Inventory[slot] });
            }
        }
        _biteAt = DateTime.MaxValue;
        Send(new DestroyEntityPacket { EntityId = gone });
        return true;
    }
}
