namespace MineClaw.World;

/// <summary>
/// Recent explosions the server reported (b1.8.1 0x3C, 1.12.2 0x1C translated onto it): the blocks they took are the world's
/// doing, never a player's or the bot's (design drift: "−6 planks (an explosion)"). Tiny bounded ring, thread-safe.
/// </summary>
public sealed class BlastLog
{
    private const int Capacity = 16;
    private readonly (Vec3d At, float Radius, long T)[] _ring = new (Vec3d, float, long)[Capacity];
    private int _next, _count;
    private readonly object _lock = new();

    /// <summary>Milliseconds clock. Default: <see cref="Environment.TickCount64"/>.</summary>
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>How long after the packet a block change near it still counts as the blast's.</summary>
    public long WindowMs { get; set; } = 5000;

    public void Note(Vec3d at, float radius)
    {
        long now = Clock();
        lock (_lock)
        {
            _ring[_next] = (at, radius, now);
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>True when an explosion within reach of <paramref name="p"/> (twice its power, +1) went off just now.</summary>
    public bool Near(BlockPos p)
    {
        long now = Clock();
        lock (_lock)
            for (int i = 0; i < _count; i++)
            {
                var (at, r, t) = _ring[i];
                if (now - t > WindowMs || now < t - 50) continue;
                double dx = p.X + 0.5 - at.X, dy = p.Y + 0.5 - at.Y, dz = p.Z + 0.5 - at.Z, reach = 2 * Math.Max(1, r) + 1;
                if (dx * dx + dy * dy + dz * dz <= reach * reach) return true;
            }
        return false;
    }
}
