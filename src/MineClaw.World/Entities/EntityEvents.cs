using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// A one-shot thing an entity did that a renderer shows and a snapshot can't (the browser viewer; the bot never reads it):
/// <see cref="Kind"/> swing | swing_off | hurt | death | crit | magic_crit | eat | teleport | lightning. <see cref="Position"/> is
/// set for lightning (the bolt's spot), sounds, and chest_open/chest_close (integer block coordinates).
/// </summary>
public readonly record struct EntityEvent(int EntityId, string Kind, DateTime At, Vec3d? Position = null,
    string? Sound = null, float? Volume = null, float? Pitch = null, int? Dimension = null);

/// <summary>
/// A short ring of <see cref="EntityEvent"/>s (the last <see cref="Retention"/>, at most <see cref="Capacity"/>): enough for a
/// 5 Hz poll to see each once. Thread-safe; recording is a lock and a struct write.
/// </summary>
public sealed class EntityEventRing
{
    public const int Capacity = 256;
    public static readonly TimeSpan Retention = TimeSpan.FromSeconds(5);

    private readonly EntityEvent[] _ring = new EntityEvent[Capacity];
    private readonly object _lock = new();
    private int _head, _count;

    public void Add(EntityEvent e)
    {
        lock (_lock)
        {
            _ring[(_head + _count) % Capacity] = e;
            if (_count < Capacity) _count++;
            else _head = (_head + 1) % Capacity;
        }
    }

    /// <summary>The events of the last <see cref="Retention"/> (oldest first).</summary>
    public List<EntityEvent> Recent(DateTime nowUtc)
    {
        var list = new List<EntityEvent>();
        lock (_lock)
            for (int i = 0; i < _count; i++)
            {
                var e = _ring[(_head + i) % Capacity];
                if (nowUtc - e.At <= Retention) list.Add(e);
            }
        return list;
    }

    /// <summary>
    /// Animation (canonical = b1.8.1 Packet18ArmAnimation; mc-dev's server sends 1 swing, 3 leave bed and 5 start eating; 1.12.2
    /// SPacketAnimation translated onto it): 1 swing (hand 1 = the off hand), 2 hurt, 5 eat, 6 crit, 7 magic crit. 6 and 7 never
    /// come over the b1.8.1 wire (its crits are client-side particles); they are the canonical values of 1.12.2's 4 and 5. Null
    /// for the others (wake up, crouch…).
    /// </summary>
    public static string? AnimationKind(int animation, int hand) => animation switch
    {
        1 => hand == 1 ? "swing_off" : "swing", 2 => "hurt", 5 => "eat", 6 => "crit", 7 => "magic_crit", _ => null,
    };

    /// <summary>Entity Status (b1.8.1 Packet38: mc-dev's EntityLiving sends 2 hurt, 3 death, EntityPlayer 9 eat finished to the
    /// eater only; 1.12.2 the same, its hurt variants 33/36/37 folded into 2): null for the others. A projectile's 3 (an egg or
    /// snowball breaking) is not a death: the client leaves objects out.</summary>
    public static string? StatusKind(int status) => status switch
    {
        2 => "hurt", 3 => "death", 9 => "eat", _ => null,
    };

    /// <summary>How long an iron golem holds out its flower (<c>EntityIronGolem.holdRoseTick</c> = 400).</summary>
    public const int RoseTicks = 400;

    /// <summary>An iron golem's own statuses (<c>EntityIronGolem.handleStatusUpdate</c>): 4 its attack swing (both arms, the
    /// viewer's <c>swing</c>), 11 offering a flower, 34 taking it back; null for the others.</summary>
    public static string? GolemStatus(int status) => status switch { 4 => "swing", 11 => "rose", 34 => "no_rose", _ => null };

    /// <summary>A position jump at least this far in one update is a teleport (neither version has a status for it: an enderman,
    /// an ender pearl, /tp).</summary>
    public const double TeleportJump = 8;
}
