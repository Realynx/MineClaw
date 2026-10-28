using MineClaw.Agent.Telemetry;
using MineClaw.World;

namespace MineClaw.Render;

/// <summary>
/// Builds the isometric viewport's <see cref="VoxelVolume"/> around a bot from its world copy, at most
/// <see cref="ViewportOptions.VoxelHz"/> times a second; in between (and when nothing changed) the same instance is returned,
/// so <see cref="VoxelVolume.Seq"/> only moves on real changes. Thread-safe (one lock; ~10k block reads per rebuild).
/// </summary>
public sealed class VoxelCache
{
    private readonly object _lock = new();
    private VoxelVolume? _last;
    private DateTime _builtAt;
    private long _seq = Random.Shared.NextInt64(1, 1L << 40); // per-process start, so a restarted clone never reuses a Seq

    public VoxelVolume Get(WorldState world, Vec3d feet, ViewportOptions o, int[]? target = null, int[]? path = null, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        var c = feet.ToBlockPos();
        lock (_lock)
        {
            double hz = Math.Clamp(o.VoxelHz, 0.1, 20);
            if (_last != null && t - _builtAt < TimeSpan.FromSeconds(1 / hz) && _last.Cx == c.X && _last.Cy == c.Y && _last.Cz == c.Z
                && SameInts(_last.Target, target) && SameInts(_last.Path, path))
                return _last;
            _builtAt = t;
            var cells = VoxelBuilder.Cells(world, c.X, c.Y, c.Z, o, out int x0, out int y0, out int z0, out int sx, out int sy, out int sz);
            uint hash = VoxelCodec.Hash(cells, x0, y0, z0, sx, sy, sz);
            if (_last != null && _last.Hash == hash && _last.X0 == x0 && _last.Y0 == y0 && _last.Z0 == z0 && _last.Cx == c.X && _last.Cy == c.Y
                && _last.Cz == c.Z && SameInts(_last.Target, target) && SameInts(_last.Path, path))
                return _last;
            _last = VoxelVolume.Create(x0, y0, z0, sx, sy, sz, c.X, c.Y, c.Z, cells, ++_seq, target, path);
            return _last;
        }
    }

    private static bool SameInts(int[]? a, int[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);
}

public static class VoxelBuilder
{
    /// <summary>One-shot build (tests, demo); live sources use <see cref="VoxelCache"/>.</summary>
    public static VoxelVolume Build(WorldState world, Vec3d feet, ViewportOptions? o = null, long seq = 1, int[]? target = null, int[]? path = null)
    {
        o ??= new ViewportOptions();
        var c = feet.ToBlockPos();
        var cells = Cells(world, c.X, c.Y, c.Z, o, out int x0, out int y0, out int z0, out int sx, out int sy, out int sz);
        return VoxelVolume.Create(x0, y0, z0, sx, sy, sz, c.X, c.Y, c.Z, cells, seq, target, path);
    }

    internal static byte[] Cells(WorldState world, int cx, int cy, int cz, ViewportOptions o,
        out int x0, out int y0, out int z0, out int sx, out int sy, out int sz)
    {
        int r = Math.Clamp(o.VoxelRadius, 2, 32);
        int up = Math.Clamp(o.VoxelUp, 1, 32), down = Math.Clamp(o.VoxelDown, 1, 32);
        sx = sz = 2 * r + 1;
        sy = up + down + 1;
        x0 = cx - r; z0 = cz - r; y0 = cy - down;
        var cells = new byte[sx * sy * sz];
        for (int z = 0; z < sz; z++)
        {
            int wz = z0 + z;
            for (int x = 0; x < sx; x++)
            {
                int wx = x0 + x;
                var col = world.GetColumn(wx >> 4, wz >> 4);
                if (col is not { HasData: true }) continue;
                int lx = wx & 15, lz = wz & 15;
                for (int y = 0; y < sy; y++)
                {
                    int wy = y0 + y;
                    if ((uint)wy >= col.Height) continue;
                    int id = col.GetBlock(lx, wy, lz);
                    cells[(y * sz + z) * sx + x] = (byte)Math.Clamp(id, 0, 255);
                }
            }
        }
        return cells;
    }
}
