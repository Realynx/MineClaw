using System.Text;

namespace MineClaw.Protocol.Versioning;

/// <summary>The 1.7+ VarInt and length-prefixed UTF-8 string (wire format of the netty protocols, 1.12.2 included).</summary>
public static class VarInt
{
    public static void Write(Stream s, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    public static int Size(int value)
    {
        uint v = (uint)value;
        int n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }

    /// <summary>Reads a VarInt (at most 5 bytes). Throws <see cref="EndOfStreamException"/> / <see cref="InvalidDataException"/>.</summary>
    public static int Read(Stream s)
    {
        int result = 0;
        for (int i = 0; i < 5; i++)
        {
            int b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException("stream ended inside a VarInt");
            result |= (b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("VarInt longer than 5 bytes");
    }

    /// <summary>Async VarInt read (cancellable, for network pings).</summary>
    public static async Task<int> ReadAsync(Stream s, CancellationToken ct)
    {
        var one = new byte[1];
        int result = 0;
        for (int i = 0; i < 5; i++)
        {
            if (await s.ReadAsync(one, ct).ConfigureAwait(false) == 0) throw new EndOfStreamException("stream ended inside a VarInt");
            result |= (one[0] & 0x7F) << (7 * i);
            if ((one[0] & 0x80) == 0) return result;
        }
        throw new InvalidDataException("VarInt longer than 5 bytes");
    }

    public static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Write(s, bytes.Length);
        s.Write(bytes);
    }
}
