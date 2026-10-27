using System.Text.Json.Serialization;

namespace MineClaw.Agent.Telemetry;

/// <summary>
/// A small 3D block volume around a bot for the TUI's isometric viewport (additive to <see cref="ViewportGrid"/>). Cells are
/// block ids (byte), x fastest, then z, then y: <c>index = (y * SizeZ + z) * SizeX + x</c> relative to the min corner
/// (<see cref="X0"/>, <see cref="Y0"/>, <see cref="Z0"/>). <see cref="Data"/> is <see cref="VoxelCodec"/> (palette + run-length);
/// a 25×25×16 skyblock island is typically well under 1 KB. Built at most ~2 Hz; <see cref="Seq"/> only changes when the
/// cells (or the window) change, so IPC senders can skip unchanged volumes (<see cref="VoxelSendGate"/>).
/// </summary>
public sealed record VoxelVolume
{
    /// <summary>World coordinates of the min corner.</summary>
    public int X0 { get; init; }
    public int Y0 { get; init; }
    public int Z0 { get; init; }
    public int SizeX { get; init; }
    public int SizeY { get; init; }
    public int SizeZ { get; init; }
    /// <summary>The bot's feet block (world).</summary>
    public int Cx { get; init; }
    public int Cy { get; init; }
    public int Cz { get; init; }
    /// <summary>Encoded cells (System.Text.Json writes byte[] as base64).</summary>
    public byte[] Data { get; init; } = [];
    /// <summary>Changes whenever the cells or the window change (per source).</summary>
    public long Seq { get; init; }
    /// <summary>FNV-1a of the window + raw cells.</summary>
    public uint Hash { get; init; }
    /// <summary>Block the bot is working on (world x,y,z), if known.</summary>
    public int[]? Target { get; init; }
    /// <summary>Planned path as flat world x,y,z triples (capped), if known.</summary>
    public int[]? Path { get; init; }

    [JsonIgnore] public int Count => SizeX * SizeY * SizeZ;

    private byte[]? _cells;

    /// <summary>Decoded cells (cached on this instance; do not mutate).</summary>
    public byte[] Cells() => _cells ??= VoxelCodec.Decode(Data, Count);

    public int Index(int x, int y, int z) => (y * SizeZ + z) * SizeX + x;

    /// <summary>Block id at world coordinates (0 outside the volume).</summary>
    public int At(int wx, int wy, int wz)
    {
        int x = wx - X0, y = wy - Y0, z = wz - Z0;
        if ((uint)x >= (uint)SizeX || (uint)y >= (uint)SizeY || (uint)z >= (uint)SizeZ) return 0;
        return Cells()[Index(x, y, z)];
    }

    /// <summary>Builds a volume from raw cells (encodes + hashes).</summary>
    public static VoxelVolume Create(int x0, int y0, int z0, int sx, int sy, int sz, int cx, int cy, int cz, byte[] cells, long seq,
        int[]? target = null, int[]? path = null)
    {
        if (cells.Length != sx * sy * sz) throw new ArgumentException("cells length mismatch", nameof(cells));
        return new VoxelVolume
        {
            X0 = x0, Y0 = y0, Z0 = z0, SizeX = sx, SizeY = sy, SizeZ = sz, Cx = cx, Cy = cy, Cz = cz,
            Data = VoxelCodec.Encode(cells), Seq = seq, Hash = VoxelCodec.Hash(cells, x0, y0, z0, sx, sy, sz),
            Target = target, Path = path, _cells = cells,
        };
    }
}

/// <summary>
/// Voxel payload codec. Layout: <c>[1 version][1 paletteCount-1][palette ids...][runs...]</c>, each run = palette index byte +
/// LEB128 varint (run length − 1). Worst case (no two neighbours equal) is ≤ 2 + 256 + 2·N bytes for runs under 129.
/// </summary>
public static class VoxelCodec
{
    public const byte Version = 1;

    public static int MaxEncodedSize(int n) => 2 + 256 + n * 2 + 8;

    public static byte[] Encode(ReadOnlySpan<byte> cells)
    {
        Span<short> slot = stackalloc short[256];
        slot.Fill(-1);
        Span<byte> palette = stackalloc byte[256];
        int pc = 0;
        foreach (var b in cells)
            if (slot[b] < 0) { slot[b] = (short)pc; palette[pc++] = b; }
        if (pc == 0) { palette[0] = 0; slot[0] = 0; pc = 1; }

        var buf = new byte[2 + pc + cells.Length * 2 + 8];
        int o = 0;
        buf[o++] = Version;
        buf[o++] = (byte)(pc - 1);
        palette[..pc].CopyTo(buf.AsSpan(o));
        o += pc;
        int i = 0;
        while (i < cells.Length)
        {
            byte v = cells[i];
            int j = i + 1;
            while (j < cells.Length && cells[j] == v) j++;
            buf[o++] = (byte)slot[v];
            uint len = (uint)(j - i - 1);
            while (len >= 0x80) { buf[o++] = (byte)(len | 0x80); len >>= 7; }
            buf[o++] = (byte)len;
            i = j;
        }
        return buf.AsSpan(0, o).ToArray();
    }

    public static byte[] Decode(ReadOnlySpan<byte> data, int count)
    {
        var cells = new byte[count];
        if (data.Length < 2 || data[0] != Version) return cells;
        int pc = data[1] + 1, o = 2;
        if (data.Length < o + pc) return cells;
        var palette = data.Slice(o, pc);
        o += pc;
        int at = 0;
        while (o < data.Length && at < count)
        {
            int idx = data[o++];
            uint len = 0;
            int shift = 0;
            while (o < data.Length)
            {
                byte b = data[o++];
                len |= (uint)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 28) return cells;
            }
            int n = (int)Math.Min(len + 1, (uint)(count - at));
            byte id = idx < pc ? palette[idx] : (byte)0;
            if (id != 0) cells.AsSpan(at, n).Fill(id);
            at += n;
        }
        return cells;
    }

    public static uint Hash(ReadOnlySpan<byte> cells, int x0, int y0, int z0, int sx, int sy, int sz)
    {
        uint h = 2166136261;
        void Mix(int v) { unchecked { for (int k = 0; k < 4; k++) { h ^= (byte)(v >> (8 * k)); h *= 16777619; } } }
        Mix(x0); Mix(y0); Mix(z0); Mix(sx); Mix(sy); Mix(sz);
        unchecked { foreach (var b in cells) { h ^= b; h *= 16777619; } }
        return h;
    }
}

/// <summary>
/// IPC sender side (hive clone → main): drops <see cref="BotSnapshot.Voxels"/> when the volume hasn't changed since the last
/// send (still re-sent every <see cref="Keyframe"/>), leaving <see cref="BotSnapshot.VoxelsSeq"/> so the receiver keeps its
/// copy (<see cref="Merge"/>). Not thread-safe: one per stream loop.
/// </summary>
public sealed class VoxelSendGate
{
    private long _lastSeq = long.MinValue;
    private DateTimeOffset _lastFull;

    public TimeSpan Keyframe { get; init; } = TimeSpan.FromSeconds(5);

    public BotSnapshot Thin(BotSnapshot s, DateTimeOffset? now = null)
    {
        if (s.Voxels is not { } v) { _lastSeq = long.MinValue; return s; }
        var t = now ?? DateTimeOffset.UtcNow;
        if (v.Seq == _lastSeq && t - _lastFull < Keyframe) return s with { Voxels = null, VoxelsSeq = v.Seq };
        _lastSeq = v.Seq;
        _lastFull = t;
        return s;
    }

    /// <summary>Receiver side: re-attaches the previous volume when <paramref name="next"/> elided an unchanged one.</summary>
    public static BotSnapshot Merge(BotSnapshot? prev, BotSnapshot next)
    {
        if (next.Voxels == null && next.VoxelsSeq is { } q && prev?.Voxels is { } pv && pv.Seq == q)
            return next with { Voxels = pv, VoxelsSeq = null };
        return next;
    }
}
