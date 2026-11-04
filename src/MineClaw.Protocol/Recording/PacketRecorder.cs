using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace MineClaw.Protocol.Recording;

/// <summary>One recorded packet: time since the recording started, direction and the exact wire bytes (id included).</summary>
public readonly record struct RecordedFrame(long OffsetMs, PacketDirection Direction, byte[] Raw)
{
    public byte Id => Raw.Length > 0 ? Raw[0] : (byte)0;

    /// <summary>Parses the frame with the codec (throws <see cref="ProtocolException"/> on malformed bytes).</summary>
    public IPacket Parse() => PacketRegistry.Read(new MinecraftReader(Raw), Direction);
}

/// <summary>
/// <c>.mcrec</c> format (version 1), big-endian where it matters:
/// <code>
/// "MCREC" u8 version=1  u8 flags (bit0 = gzip body)  i64 start unix-ms  u16 metaLen  meta (UTF-8 "k=v;k=v")
/// body (raw or one gzip stream) = frames until EOF:
///   u8 dir (0 = server→client, 1 = client→server)  varint Δms since the previous frame  varint len  len bytes
/// </code>
/// A truncated tail (crash, kill -9) is tolerated by the reader: frames up to the last complete one are returned.
/// </summary>
public static class RecordingFormat
{
    public static ReadOnlySpan<byte> Magic => "MCREC"u8;
    public const byte Version = 1;
    public const byte FlagGzip = 1;
    public const string Extension = ".mcrec";

    internal static void WriteVarInt(Stream s, uint v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    /// <summary>-1 at a clean EOF; throws <see cref="EndOfStreamException"/> mid-number.</summary>
    internal static long ReadVarInt(Stream s, bool eofOk)
    {
        uint v = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            int b = s.ReadByte();
            if (b < 0)
            {
                if (eofOk && shift == 0) return -1;
                throw new EndOfStreamException();
            }
            v |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
        }
        throw new InvalidDataException("varint too long");
    }
}

/// <summary>
/// Records both directions of a <see cref="PacketConnection"/> through its <see cref="PacketConnection.TrafficLog"/>
/// hook. Cost per packet: one lock + a memcpy into a 64 KB buffer (plus gzip "fastest" when enabled); the disk is touched
/// only when the buffer fills or every <see cref="FlushInterval"/>. Thread-safe (reader and sender threads both call in).
/// </summary>
public sealed class PacketRecorder : IDisposable
{
    private readonly object _lock = new();
    private readonly Stream _file;
    private readonly Stream _body;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastMs;
    private long _lastFlushMs;
    private bool _disposed;

    public PacketRecorder(Stream output, bool gzip, string? meta = null, DateTimeOffset? start = null, bool leaveOpen = false)
    {
        _file = output;
        LeaveOpen = leaveOpen;
        Start = start ?? DateTimeOffset.UtcNow;
        Span<byte> head = stackalloc byte[5 + 1 + 1 + 8];
        RecordingFormat.Magic.CopyTo(head);
        head[5] = RecordingFormat.Version;
        head[6] = gzip ? RecordingFormat.FlagGzip : (byte)0;
        long ms = Start.ToUnixTimeMilliseconds();
        for (int i = 0; i < 8; i++) head[7 + i] = (byte)(ms >> (56 - 8 * i));
        _file.Write(head);
        var m = Encoding.UTF8.GetBytes(meta ?? "");
        if (m.Length > ushort.MaxValue) m = m[..ushort.MaxValue];
        _file.WriteByte((byte)(m.Length >> 8));
        _file.WriteByte((byte)m.Length);
        _file.Write(m);
        Stream body = new BufferedStream(leaveOpen ? new KeepOpenStream(_file) : _file, 64 * 1024);
        if (gzip) body = new BufferedStream(new GZipStream(body, CompressionLevel.Fastest, leaveOpen: false), 64 * 1024);
        _body = body;
        Gzip = gzip;
    }

    /// <summary>Creates <c>&lt;dir&gt;/yyyyMMdd-HHmmss.mcrec</c> (a <c>-2</c>, <c>-3</c> suffix if taken).</summary>
    public static PacketRecorder CreateFile(string directory, bool gzip, string? meta = null, DateTimeOffset? now = null)
    {
        Directory.CreateDirectory(directory);
        var t = (now ?? DateTimeOffset.Now).ToLocalTime();
        string stem = t.ToString("yyyyMMdd-HHmmss");
        string path = System.IO.Path.Combine(directory, stem + RecordingFormat.Extension);
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(directory, $"{stem}-{i}{RecordingFormat.Extension}");
        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096);
        return new PacketRecorder(fs, gzip, meta, now) { Path = path };
    }

    public string? Path { get; private init; }
    public DateTimeOffset Start { get; }
    public bool Gzip { get; }
    public bool LeaveOpen { get; }
    public long Frames { get; private set; }
    /// <summary>Payload bytes recorded (before framing/compression).</summary>
    public long Bytes { get; private set; }
    /// <summary>Total time spent inside <see cref="Record(PacketDirection, ReadOnlySpan{byte})"/> (overhead check).</summary>
    public TimeSpan TimeSpent => TimeSpan.FromTicks(_spentTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
    private long _spentTicks;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Clock override (ms since start) for tests and synthetic fixtures.</summary>
    public Func<long>? Clock { get; init; }

    /// <summary>Chains the recorder onto the connection's traffic hook (keeps any existing hook).</summary>
    public void Attach(Versioning.IGameConnection conn)
    {
        var prev = conn.TrafficLog;
        conn.TrafficLog = prev is null ? Record : t => { prev(t); Record(t); };
    }

    public void Record(PacketTraffic t) => Record(t.Direction, t.Raw.Span);

    public void Record(PacketDirection dir, ReadOnlySpan<byte> raw)
    {
        long t0 = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                long now = Clock?.Invoke() ?? _clock.ElapsedMilliseconds;
                long delta = Math.Max(0, now - _lastMs);
                _lastMs = Math.Max(_lastMs, now);
                _body.WriteByte(dir == PacketDirection.ClientBound ? (byte)0 : (byte)1);
                RecordingFormat.WriteVarInt(_body, (uint)Math.Min(delta, uint.MaxValue));
                RecordingFormat.WriteVarInt(_body, (uint)raw.Length);
                _body.Write(raw);
                Frames++;
                Bytes += raw.Length;
                if (Clock is null && now - _lastFlushMs >= FlushInterval.TotalMilliseconds)
                {
                    _lastFlushMs = now;
                    _body.Flush();
                }
                _spentTicks += Stopwatch.GetTimestamp() - t0;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _disposed = true; // disk full / closed: stop recording, never break the connection
            }
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_disposed) return;
            try { _body.Flush(); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = _disposed = true;
            try { _body.Dispose(); } // flushes the buffers and writes the gzip trailer
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private bool _closed;

    /// <summary>Flushes but never closes the caller's stream.</summary>
    private sealed class KeepOpenStream(Stream inner) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override void Flush() => inner.Flush();
        protected override void Dispose(bool disposing) => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Reads <c>.mcrec</c> recordings.</summary>
public sealed class PacketRecording
{
    public required DateTimeOffset Start { get; init; }
    public required bool Gzip { get; init; }
    public required string Meta { get; init; }
    public required IReadOnlyList<RecordedFrame> Frames { get; init; }
    /// <summary>True when the file ended mid-frame (the partial frame was dropped).</summary>
    public bool Truncated { get; init; }

    public TimeSpan Duration => Frames.Count == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Frames[^1].OffsetMs);

    /// <summary>Meta value ("user=maid_chan;host=..." → "maid_chan" for "user").</summary>
    public string? MetaValue(string key)
    {
        foreach (var part in Meta.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return part[(eq + 1)..].Trim();
        }
        return null;
    }

    public static PacketRecording Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    public static PacketRecording Read(Stream input)
    {
        Span<byte> head = stackalloc byte[15];
        input.ReadExactly(head);
        if (!head[..5].SequenceEqual(RecordingFormat.Magic)) throw new InvalidDataException("not an .mcrec recording");
        if (head[5] != RecordingFormat.Version) throw new InvalidDataException($"unsupported .mcrec version {head[5]}");
        bool gzip = (head[6] & RecordingFormat.FlagGzip) != 0;
        long ms = 0;
        for (int i = 0; i < 8; i++) ms = (ms << 8) | head[7 + i];
        int hi = input.ReadByte(), lo = input.ReadByte();
        if (hi < 0 || lo < 0) throw new EndOfStreamException();
        var meta = new byte[(hi << 8) | lo];
        input.ReadExactly(meta);

        Stream body = gzip ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true) : input;
        body = new BufferedStream(body, 64 * 1024);
        var frames = new List<RecordedFrame>();
        bool truncated = !ReadFrames(body, frames);
        return new PacketRecording
        {
            Start = DateTimeOffset.FromUnixTimeMilliseconds(ms), Gzip = gzip, Meta = Encoding.UTF8.GetString(meta), Frames = frames,
            Truncated = truncated,
        };
    }

    /// <summary>Reads frames to the end of <paramref name="body"/>; false when it was cut off mid-frame (the frames before stay).</summary>
    private static bool ReadFrames(Stream body, List<RecordedFrame> frames)
    {
        long t = 0;
        try
        {
            while (ReadFrame(body, ref t) is { } frame) frames.Add(frame);
            return true;
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException)
        {
            return false;
        }
    }

    /// <summary>One frame (direction byte, time delta, length, bytes), or null at a clean end.</summary>
    private static RecordedFrame? ReadFrame(Stream body, ref long t)
    {
        int dir = body.ReadByte();
        if (dir < 0) return null;
        long delta = RecordingFormat.ReadVarInt(body, eofOk: false);
        long len = RecordingFormat.ReadVarInt(body, eofOk: false);
        if (len > 16 * 1024 * 1024) throw new InvalidDataException("frame too large");
        var raw = new byte[len];
        body.ReadExactly(raw);
        t += delta;
        return new RecordedFrame(t, dir == 0 ? PacketDirection.ClientBound : PacketDirection.ServerBound, raw);
    }
}
