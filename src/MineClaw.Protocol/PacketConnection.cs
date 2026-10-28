using System.Net.Sockets;
using System.Text;

namespace MineClaw.Protocol;

/// <summary>One packet observed on a <see cref="PacketConnection"/> (for the optional traffic log).</summary>
/// <param name="Direction">Direction the packet travelled.</param>
/// <param name="Packet">The parsed packet.</param>
/// <param name="Raw">Exact wire bytes including the id byte.</param>
public readonly record struct PacketTraffic(PacketDirection Direction, IPacket Packet, ReadOnlyMemory<byte> Raw);

/// <summary>
/// A framed packet stream over a TCP connection (or any duplex <see cref="Stream"/>).
/// <para>Threading: call <see cref="ReadPacket"/> from exactly one dedicated reader thread (it blocks).
/// <see cref="Send"/> is thread-safe: each packet is serialized to a buffer first, then written and flushed under a
/// lock, so packets never interleave and a serialization error never leaves a partial packet on the wire.</para>
/// <para>By default this is the client side: it reads <see cref="PacketDirection.ClientBound"/> packets and writes
/// <see cref="PacketDirection.ServerBound"/> ones. Pass <c>inbound: ServerBound</c> to act as a server (tests).</para>
/// </summary>
public sealed class PacketConnection : IDisposable
{
    private readonly Stream _stream;
    private readonly TcpClient? _tcp;
    private readonly RecordingStream _recorder;
    private readonly MinecraftReader _reader;
    private readonly object _sendLock = new();
    private readonly bool _ownsStream;
    private volatile bool _disposed;

    public PacketConnection(Stream stream, PacketDirection inbound = PacketDirection.ClientBound, bool ownsStream = true)
        : this(stream, null, inbound, ownsStream) { }

    private PacketConnection(Stream stream, TcpClient? tcp, PacketDirection inbound, bool ownsStream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _tcp = tcp;
        _ownsStream = ownsStream;
        Inbound = inbound;
        _recorder = new RecordingStream(new BufferedStream(stream, 64 * 1024));
        _reader = new MinecraftReader(_recorder);
    }

    /// <summary>Opens a TCP connection (Nagle disabled, as vanilla sets low-delay traffic class).</summary>
    public static PacketConnection Connect(string host, int port, TimeSpan? connectTimeout = null)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var cts = new CancellationTokenSource(connectTimeout ?? TimeSpan.FromSeconds(10));
            tcp.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            return new PacketConnection(tcp.GetStream(), tcp, PacketDirection.ClientBound, ownsStream: true);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Direction of packets this connection reads.</summary>
    public PacketDirection Inbound { get; }

    /// <summary>Direction of packets this connection writes.</summary>
    public PacketDirection Outbound => Inbound.Opposite();

    /// <summary>Optional raw traffic hook, invoked on the reading thread for inbound packets and on the sending thread
    /// (inside the send lock) for outbound packets. Keep it fast.</summary>
    public Action<PacketTraffic>? TrafficLog { get; set; }

    public long PacketsReceived { get; private set; }
    public long PacketsSent { get; private set; }

    /// <summary>Last packet ids read (oldest first), for diagnostics.</summary>
    public IReadOnlyList<byte> RecentPacketIds => _reader.RecentPacketIds;

    /// <summary>
    /// Blocks until a full packet has been read. Returns null if the peer closed the connection cleanly at a packet
    /// boundary. Throws <see cref="ProtocolException"/> on malformed data (the connection is then unusable) and
    /// <see cref="IOException"/>/<see cref="ObjectDisposedException"/> if the socket fails or is closed.
    /// </summary>
    public IPacket? ReadPacket()
    {
        _recorder.Reset();
        IPacket packet;
        try
        {
            if (!PacketRegistry.TryRead(_reader, Inbound, out packet)) return null;
        }
        catch (ProtocolException ex)
        {
            throw new ProtocolException($"{ex.Message}; bytes of this packet so far: {_recorder.HexDump(256)}", ex);
        }
        catch (EndOfStreamException ex)
        {
            throw new ProtocolException($"Stream ended mid-packet; recent ids: {_reader.RecentPacketIdsText}; bytes: {_recorder.HexDump(256)}", ex);
        }
        PacketsReceived++;
        TrafficLog?.Invoke(new PacketTraffic(Inbound, packet, _recorder.ToArray()));
        return packet;
    }

    /// <summary>Serializes and sends one packet (thread-safe, flushed immediately).</summary>
    public void Send(IPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ms = new MemoryStream(64);
        PacketRegistry.Write(new MinecraftWriter(ms), packet, Outbound);
        lock (_sendLock)
        {
            // Logged before the bytes leave: a fast server's reply (read on another thread) can't be recorded ahead of the
            // packet it answers (the recorder test saw the handshake reply as frame 0 under full-suite load).
            TrafficLog?.Invoke(new PacketTraffic(Outbound, packet, ms.GetBuffer().AsMemory(0, (int)ms.Length)));
            _stream.Write(ms.GetBuffer(), 0, (int)ms.Length);
            _stream.Flush();
            PacketsSent++;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsStream)
        {
            try { _stream.Dispose(); } catch { /* ignore */ }
            _tcp?.Dispose();
        }
    }

    /// <summary>Read-only pass-through stream that remembers the bytes consumed since the last <see cref="Reset"/>.</summary>
    private sealed class RecordingStream(Stream inner) : Stream
    {
        private readonly MemoryStream _record = new();

        public void Reset() => _record.SetLength(0);
        public byte[] ToArray() => _record.ToArray();

        public string HexDump(int max)
        {
            var buf = _record.GetBuffer();
            int len = (int)Math.Min(_record.Length, max);
            var sb = new StringBuilder();
            for (int i = 0; i < len; i++) sb.Append(buf[i].ToString("X2")).Append(i + 1 < len ? " " : "");
            if (_record.Length > max) sb.Append($" ... ({_record.Length} bytes)");
            return sb.ToString();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            if (n > 0) _record.Write(buffer, offset, n);
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            if (n > 0) _record.Write(buffer[..n]);
            return n;
        }

        public override int ReadByte()
        {
            int b = inner.ReadByte();
            if (b >= 0) _record.WriteByte((byte)b);
            return b;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
