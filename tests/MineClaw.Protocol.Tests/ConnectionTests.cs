using MineClaw.Data;
using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol.Tests;

public class ConnectionTests
{
    [Fact]
    public void Loopback_LoginSequence()
    {
        var (clientEnd, serverEnd) = DuplexStream.CreatePair();
        using var client = new PacketConnection(clientEnd);
        using var server = new PacketConnection(serverEnd, inbound: PacketDirection.ServerBound);

        var clientLog = new List<PacketTraffic>();
        client.TrafficLog = t => { lock (clientLog) clientLog.Add(t); };

        var serverTask = Task.Run(() =>
        {
            var hs = Assert.IsType<HandshakePacket>(server.ReadPacket());
            Assert.Equal("MineClaw", hs.Username);
            server.Send(new HandshakePacket(ProtocolInfo.OfflineConnectionHash));

            var login = Assert.IsType<LoginRequestPacket>(server.ReadPacket());
            Assert.Equal(17, login.ProtocolVersion);
            Assert.Equal("MineClaw", login.Username);
            server.Send(new LoginRequestPacket { EntityId = 1234, MapSeed = 99, WorldHeight = 128, MaxPlayers = 8 });
            server.Send(new SpawnPositionPacket { X = 0, Y = 64, Z = 0 });
            server.Send(new PlayerPositionLookPacket { X = 0.5, Y = 65, Stance = 66.62, Z = 0.5, Yaw = 0, Pitch = 0 });
            server.Send(new KeepAlivePacket(777));
            server.Send(new WindowItemsPacket { WindowId = 0, Items = Enumerable.Repeat(ItemStack.Empty, 45).ToArray() });

            var echo = Assert.IsType<PlayerPositionLookPacket>(server.ReadPacket());
            Assert.Equal(0.5, echo.X);
            Assert.Equal(65, echo.Y);
            Assert.Equal(66.62, echo.Stance);
            var ka = Assert.IsType<KeepAlivePacket>(server.ReadPacket());
            Assert.Equal(777, ka.KeepAliveId);
            server.Send(new KickPacket("bye"));
            serverEnd.Dispose(); // close server->client direction
        });

        client.Send(new HandshakePacket("MineClaw"));
        var hash = Assert.IsType<HandshakePacket>(client.ReadPacket());
        Assert.True(hash.IsOfflineMode);
        client.Send(LoginRequestPacket.ForClient("MineClaw"));

        var kinds = new List<byte>();
        string? kick = null;
        while (client.ReadPacket() is { } p)
        {
            kinds.Add(p.Id);
            switch (p)
            {
                case PlayerPositionLookPacket pos: client.Send(pos.EchoForServer()); break;
                case KeepAlivePacket k: client.Send(new KeepAlivePacket(k.KeepAliveId)); break;
                case WindowItemsPacket wi: Assert.Equal(45, wi.Items.Length); break;
                case KickPacket kp: kick = kp.Reason; break;
            }
        }

        Assert.True(serverTask.Wait(TimeSpan.FromSeconds(10)));
        serverTask.GetAwaiter().GetResult();
        Assert.Equal(new byte[] { 0x01, 0x06, 0x0D, 0x00, 0x68, 0xFF }, kinds);
        Assert.Equal("bye", kick);
        Assert.Equal(4, client.PacketsSent);
        Assert.Equal(7, client.PacketsReceived);
        lock (clientLog)
        {
            Assert.Equal(11, clientLog.Count);
            var firstIn = clientLog.First(t => t.Direction == PacketDirection.ClientBound);
            Assert.Equal(0x02, firstIn.Raw.Span[0]);
            Assert.Equal(1 + 2 + 2, firstIn.Raw.Length);
        }
    }

    [Fact]
    public void ReadPacket_MalformedData_IncludesHexDump()
    {
        var ms = new MemoryStream(TestUtil.Hex("00 00 00 00 05 99 01 02"));
        using var conn = new PacketConnection(ms);
        Assert.IsType<KeepAlivePacket>(conn.ReadPacket());
        var ex = Assert.Throws<ProtocolException>(() => conn.ReadPacket());
        Assert.Contains("0x99", ex.Message);
        Assert.Contains("bytes of this packet so far: 99", ex.Message);
    }

    [Fact]
    public void Send_IsThreadSafe()
    {
        var ms = new MemoryStream();
        var conn = new PacketConnection(ms, ownsStream: false);
        Parallel.For(0, 200, i => conn.Send(new ChatPacket($"message {i}")));
        ms.Position = 0;
        var r = new MinecraftReader(ms);
        var seen = new HashSet<string>();
        while (PacketRegistry.TryRead(r, PacketDirection.ServerBound, out var p)) seen.Add(((ChatPacket)p).Message);
        Assert.Equal(200, seen.Count);
    }
}
