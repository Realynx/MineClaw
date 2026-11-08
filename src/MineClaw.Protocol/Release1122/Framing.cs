using System.Buffers;
using System.IO.Compression;

namespace MineClaw.Protocol.Release1122;

/// <summary>
/// The 1.7+ frame codec (decompiled 1.12.2 <c>NettyVarint21FrameDecoder</c>, <c>NettyCompressionDecoder</c>/<c>Encoder</c>):
/// <list type="bullet">
/// <item>Uncompressed: <c>VarInt length | VarInt id | data</c>.</item>
/// <item>After Set Compression (threshold ≥ 0): <c>VarInt packetLength | VarInt dataLength | payload</c>, where
/// dataLength = 0 means the payload (id + data) is raw, otherwise it is zlib of dataLength bytes. The sender compresses
/// every payload at or over the threshold; a receiver rejects a compressed payload below it, and anything over 2 MiB.</item>
/// </list>
/// Frames here are the payload (VarInt id + data). Not thread-safe: one reader, and writers serialize outside.
/// </summary>
public sealed class Framing
{
    /// <summary>NettyCompressionDecoder's limit on the uncompressed size, and the frame limit (21-bit length).</summary>
    public const int MaxPayload = 2097152;

    /// <summary>Compression threshold; negative = compression off (the state before Set Compression).</summary>
    public int Threshold { get; set; } = -1;

    public bool Compressed => Threshold >= 0;

    /// <summary>Reads one frame's payload, or null at a clean end of stream (EOF before the length).</summary>
    public byte[]? Read(Stream s)
    {
        using var payload = ReadPooled(s);
        return payload?.Memory.ToArray();
    }

    /// <summary>A receive buffer owned until synchronous packet translation completes. Public Read still returns an
    /// independent byte array; packet/traffic consumers must never retain this borrowed memory.</summary>
    internal sealed class PooledPayload(byte[] bytes, int offset, int length) : IDisposable
    {
        private byte[]? _bytes = bytes;
        public ReadOnlyMemory<byte> Memory => (_bytes ?? throw new ObjectDisposedException(nameof(PooledPayload))).AsMemory(offset, length);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _bytes, null) is { } b) ArrayPool<byte>.Shared.Return(b);
        }
    }

    internal PooledPayload? ReadPooled(Stream s)
    {
        int first = s.ReadByte();
        if (first < 0) return null;
        int length = ReadVarIntAfter(s, first);
        if (length < 0 || length > MaxPayload + 5) throw new ProtocolException($"frame length {length} out of range");
        byte[]? frame = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            s.ReadExactly(frame.AsSpan(0, length));
            int offset = 0;
            if (Compressed)
            {
                var r = new WireReader(frame.AsSpan(0, length));
                int dataLength = r.ReadVarInt();
                offset = r.Position;
                if (dataLength != 0)
                {
                    if (dataLength < Threshold) throw new ProtocolException($"badly compressed packet: size {dataLength} is below the threshold {Threshold}");
                    if (dataLength > MaxPayload) throw new ProtocolException($"badly compressed packet: size {dataLength} is over {MaxPayload}");
                    return Inflate(frame, offset, length - offset, dataLength);
                }
            }
            var result = new PooledPayload(frame, offset, length - offset);
            frame = null; // Ownership moves to the lease (including raw frames after compression was enabled).
            return result;
        }
        finally { if (frame != null) ArrayPool<byte>.Shared.Return(frame); }
    }

    private static PooledPayload Inflate(byte[] frame, int offset, int length, int dataLength)
    {
        var payload = ArrayPool<byte>.Shared.Rent(dataLength);
        try
        {
            using var z = new ZLibStream(new MemoryStream(frame, offset, length), CompressionMode.Decompress);
            z.ReadExactly(payload.AsSpan(0, dataLength));
            return new PooledPayload(payload, 0, dataLength);
        }
        catch (Exception ex)
        {
            ArrayPool<byte>.Shared.Return(payload);
            if (ex is EndOfStreamException or InvalidDataException)
                throw new ProtocolException($"compressed packet doesn't inflate to {dataLength} bytes: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>Encodes one payload (id + data) into a full frame (length prefix included).</summary>
    public byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var w = new WireWriter(payload.Length + 8);
        if (!Compressed)
        {
            w.WriteVarInt(payload.Length).WriteBytes(payload);
            return w.ToArray();
        }
        var body = new WireWriter(payload.Length + 4);
        if (payload.Length < Threshold)
        {
            body.WriteVarInt(0).WriteBytes(payload);
        }
        else
        {
            body.WriteVarInt(payload.Length);
            var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(payload);
            body.WriteBytes(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        w.WriteVarInt(body.Length).WriteBytes(body.Span);
        return w.ToArray();
    }

    private static int ReadVarIntAfter(Stream s, int first)
    {
        int result = first & 0x7F;
        if ((first & 0x80) == 0) return result;
        for (int i = 1; i < 5; i++)
        {
            int b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException("stream ended inside a frame length");
            result |= (b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return result;
        }
        throw new ProtocolException("frame length VarInt longer than 5 bytes");
    }
}
