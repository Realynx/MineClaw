using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MineClaw.Protocol.Versioning;

/// <summary>What a server-list ping learned.</summary>
/// <param name="Legacy">Answered the pre-netty 0xFE ping (a 0xFF kick carrying the text), not the 1.7+ status JSON.</param>
/// <param name="Protocol">The protocol number the server reported (modern: <c>version.protocol</c>; a 1.4+ legacy reply's
/// first field, 127 when a 1.7+ server answers a legacy ping); null for a b1.8–1.3 reply, which has none.</param>
/// <param name="VersionName">Modern <c>version.name</c> / a 1.4+ legacy reply's version field; null for b1.8–1.3.</param>
public sealed record ServerPingResult(bool Legacy, int? Protocol, string? VersionName, string Motd, int Online, int Max)
{
    /// <summary>The known version this answer means, or null (a version MineClaw has no name for). A plain
    /// <c>motd§online§max</c> legacy reply (b1.8 to 1.3 servers) is b1.8.1, the only pre-netty version MineClaw speaks.</summary>
    public GameVersion? Version =>
        !Legacy ? (Protocol is { } p ? GameVersion.ForNettyProtocol(p) : null)
        : Protocol is null ? GameVersion.Beta181
        : GameVersion.Known.FirstOrDefault(k => k.Id == VersionName);

    public string Describe() =>
        $"{(Legacy ? "legacy ping" : "status ping")}: {VersionName ?? "pre-1.4 server"}{(Protocol is { } p ? $" (protocol {p})" : "")}, " +
        $"{Online}/{Max} online, motd \"{Motd}\"";
}

/// <summary>A server answered neither ping.</summary>
public sealed class ServerPingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Server-list ping for version detection (docs/design/multi-version.md, "Version detection"). Two attempts, each on its
/// own connection:
/// <list type="number">
/// <item><b>Modern status</b> (1.7+): handshake (protocol -1, host, port, next state 1) + status request, VarInt framed; the
/// reply is JSON with <c>version.protocol</c> (340 = 1.12.2). A b1.8.1 server reads the frame as garbage and either kicks
/// ("Protocol error": the reply starts <c>FF 00</c>, which a canonical VarInt never does), closes, or waits (timeout).</item>
/// <item><b>Legacy</b>: a lone <c>0xFE</c>. b1.8.1 (NetLoginHandler, packet 254) answers a 0xFF kick
/// <c>motd§online§max</c>; a 1.4+ server answers <c>§1\0protocol\0version\0motd\0online\0max</c>.</item>
/// </list>
/// Read-only for the server (no login, no player slot); the modern attempt costs a b1.8.1 server one "Protocol error" log line.
/// </summary>
public static class ServerPing
{
    /// <summary>Pings host:port, modern first, then legacy. Each attempt gets <paramref name="timeout"/>.</summary>
    public static Task<ServerPingResult> PingAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default) =>
        PingAsync(c => OpenTcpAsync(host, port, c), host, port, timeout, ct);

    /// <summary>Same with an injectable connection opener (tests). The opener is called once per attempt.</summary>
    public static async Task<ServerPingResult> PingAsync(Func<CancellationToken, Task<Stream>> open, string host, int port, TimeSpan timeout,
        CancellationToken ct = default)
    {
        string modernWhy;
        try
        {
            if (await AttemptAsync(open, timeout, ct, (s, c) => ModernAsync(s, host, port, c)).ConfigureAwait(false) is { } modern) return modern;
            modernWhy = "legacy server (kicked the status handshake)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            modernWhy = Short(ex);
        }
        try
        {
            return await AttemptAsync(open, timeout, ct, LegacyAsync).ConfigureAwait(false)
                ?? throw new ServerPingException("legacy ping: no answer");
        }
        catch (Exception ex) when (ex is not ServerPingException && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            throw new ServerPingException($"{host}:{port} answered neither ping (status: {modernWhy}; legacy: {Short(ex)})", ex);
        }
    }

    private static async Task<ServerPingResult?> AttemptAsync(Func<CancellationToken, Task<Stream>> open, TimeSpan timeout, CancellationToken ct,
        Func<Stream, CancellationToken, Task<ServerPingResult?>> run)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await using var s = await open(cts.Token).ConfigureAwait(false);
            return await run(s, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer within {timeout.TotalSeconds:0.#}s");
        }
    }

    private static string Short(Exception ex) => ex is TimeoutException or EndOfStreamException or InvalidDataException or IOException or SocketException
        ? ex.Message : ex.GetType().Name + ": " + ex.Message;

    private static async Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return new OwningStream(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ modern (1.7+ status)

    /// <summary>Handshake (id 0, protocol -1, host, port, next state 1) + status request (id 0), each VarInt-length framed.</summary>
    public static byte[] ModernRequest(string host, int port)
    {
        var body = new MemoryStream();
        VarInt.Write(body, 0x00);
        VarInt.Write(body, -1);
        VarInt.WriteString(body, host);
        body.WriteByte((byte)(port >> 8));
        body.WriteByte((byte)port);
        VarInt.Write(body, 1);
        var frame = new MemoryStream();
        VarInt.Write(frame, (int)body.Length);
        body.Position = 0;
        body.CopyTo(frame);
        VarInt.Write(frame, 1); // status request: length 1, id 0
        VarInt.Write(frame, 0x00);
        return frame.ToArray();
    }

    /// <returns>The parsed status, or null when the reply is a legacy 0xFF kick (a pre-netty server).</returns>
    private static async Task<ServerPingResult?> ModernAsync(Stream s, string host, int port, CancellationToken ct)
    {
        await s.WriteAsync(ModernRequest(host, port), ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
        var head = new byte[2];
        await s.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        if (head[0] == 0xFF && head[1] == 0x00) return null; // a b1.8.1 kick ("Protocol error"): never a canonical VarInt
        int length = DecodeVarInt(head, out int used);
        var rest = new MemoryStream();
        rest.Write(head, used, head.Length - used);
        if (length < 0)
        {
            // The length VarInt runs past our 2 bytes: finish it from the stream.
            var all = new MemoryStream(head);
            var chained = new ConcatStream(all, s);
            length = await VarInt.ReadAsync(chained, ct).ConfigureAwait(false);
            rest.SetLength(0);
        }
        if (length is <= 0 or > 1 << 21) throw new InvalidDataException($"status frame length {length}");
        var frame = new byte[length];
        int have = (int)rest.Length;
        rest.ToArray().CopyTo(frame, 0);
        await s.ReadExactlyAsync(frame.AsMemory(have), ct).ConfigureAwait(false);
        var ms = new MemoryStream(frame);
        int id = VarInt.Read(ms);
        if (id != 0x00) throw new InvalidDataException($"status reply id 0x{id:X2}");
        int strLen = VarInt.Read(ms);
        if (strLen < 0 || strLen > ms.Length - ms.Position) throw new InvalidDataException("status JSON length");
        var json = Encoding.UTF8.GetString(frame, (int)ms.Position, strLen);
        return ParseStatusJson(json);
    }

    /// <summary>Decodes a VarInt from the start of <paramref name="b"/>; -1 when it isn't complete in these bytes.</summary>
    private static int DecodeVarInt(byte[] b, out int used)
    {
        int result = 0;
        for (int i = 0; i < b.Length; i++)
        {
            result |= (b[i] & 0x7F) << (7 * i);
            if ((b[i] & 0x80) == 0) { used = i + 1; return result; }
        }
        used = b.Length;
        return -1;
    }

    /// <summary>Parses the 1.7+ status JSON (<c>version.name</c>, <c>version.protocol</c>, <c>players</c>, <c>description</c>
    /// as a string or a chat component).</summary>
    public static ServerPingResult ParseStatusJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int? protocol = null;
        string? name = null;
        if (root.TryGetProperty("version", out var v))
        {
            if (v.TryGetProperty("protocol", out var p) && p.TryGetInt32(out var pi)) protocol = pi;
            if (v.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
        }
        int online = 0, max = 0;
        if (root.TryGetProperty("players", out var pl))
        {
            if (pl.TryGetProperty("online", out var o) && o.TryGetInt32(out var oi)) online = oi;
            if (pl.TryGetProperty("max", out var m) && m.TryGetInt32(out var mi)) max = mi;
        }
        string motd = root.TryGetProperty("description", out var d) ? ComponentText(d) : "";
        if (protocol is null) throw new InvalidDataException("status JSON has no version.protocol");
        return new ServerPingResult(false, protocol, name, motd, online, max);
    }

    /// <summary>Plain text of a chat component (string, {text, extra[]}, or an array).</summary>
    private static string ComponentText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Array => string.Concat(e.EnumerateArray().Select(ComponentText)),
        JsonValueKind.Object => (e.TryGetProperty("text", out var t) ? ComponentText(t) : "")
                                + (e.TryGetProperty("extra", out var x) ? ComponentText(x) : ""),
        _ => "",
    };

    // ------------------------------------------------------------------ legacy (0xFE)

    private static async Task<ServerPingResult?> LegacyAsync(Stream s, CancellationToken ct)
    {
        await s.WriteAsync(new byte[] { 0xFE }, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
        var head = new byte[3];
        await s.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        if (head[0] != 0xFF) throw new InvalidDataException($"legacy ping reply starts 0x{head[0]:X2}, not a 0xFF kick");
        int chars = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1));
        if (chars > 512) throw new InvalidDataException($"legacy ping reply of {chars} chars");
        var text = new byte[chars * 2];
        await s.ReadExactlyAsync(text, ct).ConfigureAwait(false);
        return ParseLegacy(Encoding.BigEndianUnicode.GetString(text));
    }

    /// <summary>Parses a legacy kick text: <c>§1\0protocol\0version\0motd\0online\0max</c> (1.4+ servers) or
    /// <c>motd§online§max</c> (b1.8 to 1.3; the motd itself may contain §, so the counts are the last two fields).</summary>
    public static ServerPingResult ParseLegacy(string text)
    {
        if (text.StartsWith("§1\0", StringComparison.Ordinal))
        {
            var f = text.Split('\0');
            if (f.Length < 6) throw new InvalidDataException("1.4+ legacy ping reply with too few fields");
            return new ServerPingResult(true, int.TryParse(f[1], out var p) ? p : null, f[2], f[3], Int(f[4]), Int(f[5]));
        }
        var parts = text.Split('§');
        if (parts.Length < 3) throw new InvalidDataException($"legacy ping reply \"{text}\" isn't motd§online§max");
        return new ServerPingResult(true, null, null, string.Join('§', parts[..^2]), Int(parts[^2]), Int(parts[^1]));
    }

    private static int Int(string s) => int.TryParse(s, out var v) ? v : 0;

    // ------------------------------------------------------------------ helpers

    /// <summary>A stream that also owns (disposes) its TcpClient.</summary>
    private sealed class OwningStream(TcpClient tcp) : Stream
    {
        private readonly NetworkStream _s = tcp.GetStream();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _s.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _s.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _s.ReadAsync(buffer, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _s.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _s.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _s.Dispose(); tcp.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Reads <paramref name="first"/> to its end, then <paramref name="second"/> (read-only).</summary>
    private sealed class ConcatStream(Stream first, Stream second) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = first.Read(buffer, offset, count);
            return n > 0 ? n : second.Read(buffer, offset, count);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = await first.ReadAsync(buffer, ct).ConfigureAwait(false);
            return n > 0 ? n : await second.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
