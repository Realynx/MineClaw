using System.Net;
using System.Net.Sockets;
using System.Text;
using MineClaw.Protocol.Versioning;

namespace MineClaw.Protocol.Tests;

/// <summary>Multi-version P0 (docs/design/multi-version.md): version names, the two server-list pings, the resolver.</summary>
public class VersionDetectionTests
{
    // ------------------------------------------------------------------ names

    [Theory]
    [InlineData("b1.8.1", "b1.8.1")]
    [InlineData("B1.8.1", "b1.8.1")]
    [InlineData("beta 1.8.1", "b1.8.1")]
    [InlineData("17", "b1.8.1")]
    [InlineData("1.12.2", "1.12.2")]
    [InlineData("v1.12.2", "1.12.2")]
    [InlineData("340", "1.12.2")]
    public void ParsesKnownVersions(string text, string id)
    {
        Assert.True(GameVersion.TryParse(text, out var v));
        Assert.Equal(id, v!.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData(" AUTO ")]
    public void AutoMeansDetect(string? text)
    {
        Assert.True(GameVersion.TryParse(text, out var v));
        Assert.Null(v);
    }

    [Theory]
    [InlineData("1.13")]
    [InlineData("b1.7.3")]
    [InlineData("47")]
    public void RejectsUnknownVersions(string text) => Assert.False(GameVersion.TryParse(text, out _));

    [Fact]
    public void VersionFacts()
    {
        Assert.Equal(17, GameVersion.Beta181.ProtocolVersion);
        Assert.False(GameVersion.Beta181.Netty);
        Assert.Equal(128, GameVersion.Beta181.WorldHeight);
        Assert.Equal(340, GameVersion.Release1122.ProtocolVersion);
        Assert.True(GameVersion.Release1122.Netty);
        Assert.Equal(256, GameVersion.Release1122.WorldHeight);
        Assert.Same(GameVersion.Release1122, GameVersion.ForNettyProtocol(340));
        Assert.Null(GameVersion.ForNettyProtocol(47));
    }

    // ------------------------------------------------------------------ parsers

    [Fact]
    public void LegacyBetaReplyIsBeta181()
    {
        var r = ServerPing.ParseLegacy("A Minecraft Server§3§20");
        Assert.True(r.Legacy);
        Assert.Null(r.Protocol);
        Assert.Equal("A Minecraft Server", r.Motd);
        Assert.Equal((3, 20), (r.Online, r.Max));
        Assert.Same(GameVersion.Beta181, r.Version);
    }

    [Fact]
    public void LegacyMotdMayContainSectionSigns()
    {
        var r = ServerPing.ParseLegacy("§aGreen§r island§1§7");
        Assert.Equal("§aGreen§r island", r.Motd);
        Assert.Equal((1, 7), (r.Online, r.Max));
    }

    [Fact]
    public void Legacy14PlusReplyNamesTheVersion()
    {
        var r = ServerPing.ParseLegacy("§1\u0000127\u00001.12.2\u0000hello\u00005\u000020");
        Assert.True(r.Legacy);
        Assert.Equal(127, r.Protocol);
        Assert.Equal("1.12.2", r.VersionName);
        Assert.Same(GameVersion.Release1122, r.Version);
        Assert.Null(ServerPing.ParseLegacy("§1\u000061\u00001.5.2\u0000old\u00000\u000010").Version);
    }

    [Fact]
    public void StatusJsonOf1122()
    {
        var r = ServerPing.ParseStatusJson("""{"version":{"name":"1.12.2","protocol":340},"players":{"max":20,"online":2},"description":{"text":"Fox's ","extra":[{"text":"world"}]}}""");
        Assert.False(r.Legacy);
        Assert.Equal(340, r.Protocol);
        Assert.Equal("Fox's world", r.Motd);
        Assert.Equal((2, 20), (r.Online, r.Max));
        Assert.Same(GameVersion.Release1122, r.Version);
        Assert.Null(ServerPing.ParseStatusJson("""{"version":{"name":"1.20.4","protocol":765},"description":"x"}""").Version);
        Assert.Throws<InvalidDataException>(() => ServerPing.ParseStatusJson("""{"description":"no version"}"""));
    }

    [Fact]
    public void ModernRequestIsAHandshakeThenAStatusRequest()
    {
        var bytes = ServerPing.ModernRequest("10.1.2.69", 25565);
        var ms = new MemoryStream(bytes);
        int len = VarInt.Read(ms);
        long start = ms.Position;
        Assert.Equal(0x00, VarInt.Read(ms));
        Assert.Equal(-1, VarInt.Read(ms));
        int hostLen = VarInt.Read(ms);
        var host = new byte[hostLen];
        ms.ReadExactly(host);
        Assert.Equal("10.1.2.69", Encoding.UTF8.GetString(host));
        Assert.Equal(25565, ms.ReadByte() << 8 | ms.ReadByte());
        Assert.Equal(1, VarInt.Read(ms));
        Assert.Equal(len, ms.Position - start);
        Assert.Equal(1, VarInt.Read(ms)); // status request frame: length 1, id 0
        Assert.Equal(0, VarInt.Read(ms));
        Assert.Equal(ms.Length, ms.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void VarIntRoundTrips(int value)
    {
        var ms = new MemoryStream();
        VarInt.Write(ms, value);
        Assert.Equal(VarInt.Size(value), ms.Length);
        ms.Position = 0;
        Assert.Equal(value, VarInt.Read(ms));
    }

    // ------------------------------------------------------------------ pings against fake servers (loopback)

    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(600);

    /// <summary>A b1.8.1 server: the status handshake is garbage to it (a "Protocol error" kick, a close, or silence); a lone
    /// 0xFE gets <c>motd§online§max</c>.</summary>
    [Theory]
    [InlineData("kick")]
    [InlineData("close")]
    [InlineData("silent")]
    public async Task BetaServerIsDetectedByTheLegacyPing(string onGarbage)
    {
        using var server = new LoopbackServer(async (s, ct) =>
        {
            int first = s.ReadByte();
            if (first == 0xFE)
            {
                s.Write(Kick("A Minecraft Server§1§7"));
                return;
            }
            switch (onGarbage)
            {
                case "kick": s.Write(Kick("Protocol error")); break;
                case "silent": await Task.Delay(Timeout.Infinite, ct); break;
            }
        });
        var r = await ServerPing.PingAsync("127.0.0.1", server.Port, Quick);
        Assert.True(r.Legacy);
        Assert.Same(GameVersion.Beta181, r.Version);
        Assert.Equal((1, 7), (r.Online, r.Max));
    }

    /// <summary>A 1.12.2 server answers the status handshake; the JSON is long enough that the frame length's first byte is
    /// 0xFF (never confused with a legacy kick's FF 00).</summary>
    [Fact]
    public async Task ModernServerIsDetectedByTheStatusPing()
    {
        int handshakeProtocol = 0, nextState = 0;
        string? host = null;
        using var server = new LoopbackServer((s, _) =>
        {
            VarInt.Read(s); // frame length
            Assert.Equal(0, VarInt.Read(s));
            handshakeProtocol = VarInt.Read(s);
            var hb = new byte[VarInt.Read(s)];
            s.ReadExactly(hb);
            host = Encoding.UTF8.GetString(hb);
            s.ReadByte(); s.ReadByte();
            nextState = VarInt.Read(s);
            Assert.Equal(1, VarInt.Read(s));
            Assert.Equal(0, VarInt.Read(s));
            var json = """{"version":{"name":"1.12.2","protocol":340},"players":{"max":20,"online":0},"description":{"text":"PAD"}}""";
            // body = id (1) + VarInt(252) (2) + 252 bytes of JSON = 255 → frame length bytes FF 01
            json = json.Replace("PAD", new string('x', 252 - (Encoding.UTF8.GetByteCount(json) - 3)));
            var body = new MemoryStream();
            VarInt.Write(body, 0);
            VarInt.WriteString(body, json);
            Assert.Equal(255, body.Length);
            VarInt.Write(s, (int)body.Length); // FF 01
            s.Write(body.ToArray());
            return Task.CompletedTask;
        });
        var r = await ServerPing.PingAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(5));
        Assert.False(r.Legacy);
        Assert.Same(GameVersion.Release1122, r.Version);
        Assert.Equal(-1, handshakeProtocol);
        Assert.Equal(1, nextState);
        Assert.Equal("127.0.0.1", host);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task NoAnswerAtAllIsAPingFailure()
    {
        using var server = new LoopbackServer(async (_, ct) => await Task.Delay(Timeout.Infinite, ct));
        var ex = await Assert.ThrowsAsync<ServerPingException>(() => ServerPing.PingAsync("127.0.0.1", server.Port, TimeSpan.FromMilliseconds(300)));
        Assert.Contains("neither ping", ex.Message);
    }

    // ------------------------------------------------------------------ resolver

    private static Func<CancellationToken, Task<ServerPingResult>> Answers(ServerPingResult r) => _ => Task.FromResult(r);
    private static Func<CancellationToken, Task<ServerPingResult>> Fails => _ => throw new ServerPingException("down");

    [Fact]
    public async Task ExplicitConfigWinsWithoutAPing()
    {
        bool pinged = false;
        var r = await VersionResolver.ResolveAsync("1.12.2", _ => { pinged = true; throw new ServerPingException("x"); });
        Assert.Same(GameVersion.Release1122, r.Version);
        Assert.False(r.Detected);
        Assert.False(pinged);
    }

    [Fact]
    public async Task AutoFollowsThePing()
    {
        var beta = await VersionResolver.ResolveAsync("auto", Answers(ServerPing.ParseLegacy("m§0§20")));
        Assert.Same(GameVersion.Beta181, beta.Version);
        Assert.True(beta.Detected);
        var modern = await VersionResolver.ResolveAsync(null, Answers(ServerPing.ParseStatusJson("""{"version":{"name":"1.12.2","protocol":340}}""")));
        Assert.Same(GameVersion.Release1122, modern.Version);
    }

    [Fact]
    public async Task ServerDownFallsBackToTheLastKnownThenBeta()
    {
        var last = await VersionResolver.ResolveAsync("auto", Fails, lastKnown: GameVersion.Release1122);
        Assert.Same(GameVersion.Release1122, last.Version);
        Assert.False(last.Detected);
        var none = await VersionResolver.ResolveAsync("auto", Fails);
        Assert.Same(GameVersion.Beta181, none.Version);
        Assert.Contains("didn't answer", none.How);
    }

    [Fact]
    public async Task UnknownServerOrConfigIsRefused()
    {
        await Assert.ThrowsAsync<UnsupportedVersionException>(() =>
            VersionResolver.ResolveAsync("auto", Answers(ServerPing.ParseStatusJson("""{"version":{"name":"1.20.4","protocol":765}}"""))));
        await Assert.ThrowsAsync<UnsupportedVersionException>(() => VersionResolver.ResolveAsync("1.13", Fails));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>b1.8.1 0xFF kick: id, UTF-16 length, UTF-16BE text.</summary>
    private static byte[] Kick(string text)
    {
        var body = Encoding.BigEndianUnicode.GetBytes(text);
        return [0xFF, (byte)(text.Length >> 8), (byte)text.Length, .. body];
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private int _connections;

        public LoopbackServer(Func<Stream, CancellationToken, Task> handle)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch (Exception) { return; }
                    Interlocked.Increment(ref _connections);
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        {
                            try { await handle(c.GetStream(), _cts.Token); }
                            catch (Exception) { /* the client hung up */ }
                        }
                    });
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Connections => Volatile.Read(ref _connections);

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
