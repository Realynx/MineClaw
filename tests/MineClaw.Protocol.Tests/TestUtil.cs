using System.Globalization;

namespace MineClaw.Protocol.Tests;

internal static class TestUtil
{
    /// <summary>Parses "0D 3F F0 ..." (whitespace ignored).</summary>
    public static byte[] Hex(string hex)
    {
        var clean = new string(hex.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (clean.Length % 2 != 0) throw new ArgumentException("odd hex length");
        var bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(clean.AsSpan(2 * i, 2), NumberStyles.HexNumber);
        return bytes;
    }

    public static string ToHex(byte[] data) => string.Join(" ", data.Select(b => b.ToString("X2")));

    /// <summary>Reads a single packet from bytes and asserts that every byte was consumed.</summary>
    public static IPacket ReadExact(byte[] bytes, PacketDirection dir)
    {
        var ms = new MemoryStream(bytes);
        var p = PacketRegistry.Read(new MinecraftReader(ms), dir);
        Assert.Equal(bytes.Length, ms.Position);
        return p;
    }
}

/// <summary>Blocking in-memory byte pipe; one side writes, the other reads.</summary>
internal sealed class BytePipe
{
    private readonly Queue<byte> _queue = new();
    private bool _closed;

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_queue)
        {
            foreach (var b in data) _queue.Enqueue(b);
            Monitor.PulseAll(_queue);
        }
    }

    public int Read(Span<byte> buffer)
    {
        lock (_queue)
        {
            while (_queue.Count == 0 && !_closed)
                if (!Monitor.Wait(_queue, TimeSpan.FromSeconds(10))) throw new TimeoutException("pipe read timed out");
            int n = 0;
            while (n < buffer.Length && _queue.Count > 0) buffer[n++] = _queue.Dequeue();
            return n;
        }
    }

    public void Close()
    {
        lock (_queue)
        {
            _closed = true;
            Monitor.PulseAll(_queue);
        }
    }
}

/// <summary>A full-duplex in-memory stream endpoint.</summary>
internal sealed class DuplexStream(BytePipe readFrom, BytePipe writeTo) : Stream
{
    public static (DuplexStream A, DuplexStream B) CreatePair()
    {
        var ab = new BytePipe();
        var ba = new BytePipe();
        return (new DuplexStream(ba, ab), new DuplexStream(ab, ba));
    }

    public override int Read(byte[] buffer, int offset, int count) => readFrom.Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) => readFrom.Read(buffer);
    public override void Write(byte[] buffer, int offset, int count) => writeTo.Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) => writeTo.Write(buffer);
    public override void Flush() { }

    protected override void Dispose(bool disposing)
    {
        writeTo.Close();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
