using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// A one-shot thing an entity did that a renderer shows and a snapshot can't (the browser viewer; the bot never reads it):
/// <see cref="Kind"/> swing | swing_off | hurt | death | crit | magic_crit | eat | teleport | lightning. <see cref="Position"/> is
/// set for lightning (the bolt's spot).
/// </summary>
public readonly record struct EntityEvent(int EntityId, string Kind, DateTime At, Vec3d? Position = null);

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

    /// <summary>Animation (b1.8.1 Packet18Animation; 1.12.2 translated onto it): 1 swing (hand 1 = the off hand), 2 hurt, 6 crit,
    /// 7 magic crit; null for the others (wake up, crouch…).</summary>
    public static string? AnimationKind(int animation, int hand) => animation switch
    {
        1 => hand == 1 ? "swing_off" : "swing", 2 => "hurt", 6 => "crit", 7 => "magic_crit", _ => null,
    };

    /// <summary>Entity Status (b1.8.1 Packet38; 1.12.2 with its hurt variants folded into 2): 2 hurt, 3 death, 9 eat (item use
    /// finished), 46 teleport (1.12.2 chorus fruit / enderman particles); null for the others.</summary>
    public static string? StatusKind(int status) => status switch
    {
        2 => "hurt", 3 => "death", 9 => "eat", 46 => "teleport", _ => null,
    };
}
