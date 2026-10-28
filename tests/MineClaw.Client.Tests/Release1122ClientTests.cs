using System.Collections.Concurrent;
using MineClaw.Client.Versioning;
using MineClaw.Data;
using MineClaw.Protocol.Release1122;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Multi-version P1 end to end: <see cref="MinecraftClient"/> on the 1.12.2 profile against an in-process fake 1.12.2 server
/// that speaks raw protocol 340 frames (compression on): login → join → chunks (a 256-high world, ground above y 128) →
/// spawn teleport and its confirm → keep-alive → chat both ways → block change → an unsupported (P2) action → kick.
/// </summary>
public class Release1122ClientTests
{
    private sealed class Peer(Stream s)
    {
        public readonly Framing Framing = new();

        public (int Id, byte[] Body) Read()
        {
            var payload = Framing.Read(s) ?? throw new EndOfStreamException();
            var r = new WireReader(payload);
            int id = r.ReadVarInt();
            return (id, payload[r.Position..]);
        }

        public byte[] ReadUntil(int id, int max = 2000)
        {
            for (int i = 0; i < max; i++)
            {
                var (got, body) = Read();
                if (got == id) return body;
            }
            throw new InvalidOperationException($"no 0x{id:X2}");
        }

        public void Send(int id, Action<WireWriter> body)
        {
            var w = WireWriter.ForPacket(id);
            body(w);
            lock (this)
            {
                s.Write(Framing.Encode(w.Span));
                s.Flush();
            }
        }
    }

    private static int[] Section(int state) => Enumerable.Repeat(state, ChunkData.SectionCells).ToArray();

    [Fact]
    public async Task Login_join_keepalive_chat_chunk_blockchange_disconnect()
    {
        var (clientEnd, serverEnd) = DuplexStream.CreatePair();
        var server = new Peer(serverEnd);
        var chat = new ConcurrentQueue<string>();
        var kicked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", Profile = GameVersionProfiles.Java1122,
            ConnectionFactory = _ => new Release1122Connection(clientEnd) { Lang = LangTable.Builtin },
            TickInterval = TimeSpan.FromMilliseconds(10), ChatInterval = TimeSpan.Zero, HostileAlertRadius = 0,
            LoginTimeout = TimeSpan.FromSeconds(20),
        });
        await using var _ = client;
        Assert.Equal(256, client.World.WorldHeight);
        client.ChatReceived += chat.Enqueue;
        client.Kicked += r => kicked.TrySetResult(r);

        var serverTask = Task.Run(() =>
        {
            var hs = server.Read();
            Assert.Equal(0x00, hs.Id);
            Assert.Equal(340, new WireReader(hs.Body).ReadVarInt());
            Assert.Equal("maid_chan", new WireReader(server.Read().Body).ReadString(16));
            server.Send(0x03, w => w.WriteVarInt(256));
            server.Framing.Threshold = 256; // the chunk packets below are compressed, the small ones not
            server.Send(0x02, w => w.WriteString(Wire.OfflineUuid("maid_chan").ToString()).WriteString("maid_chan"));
            server.Send(0x23, w => w.WriteInt(77).WriteUByte(0).WriteInt(0).WriteUByte(2).WriteUByte(20).WriteString("default").WriteBool(false));
            Assert.Equal(0x04, server.Read().Id); // client settings
            Assert.Equal(0x09, server.Read().Id); // MC|Brand
            server.Send(0x46, w => w.WritePosition(8, 144, 8));
            server.Send(0x47, w => w.WriteLong(1000).WriteLong(6000));
            server.Send(0x41, w => w.WriteFloat(20).WriteVarInt(20).WriteFloat(5));
            // 5×5 columns of stone from y 0 to 143 (sections 0..8): the ground is above the b1.8.1 world's top.
            var sections = new int[]?[16];
            for (int s = 0; s <= 8; s++) sections[s] = Section(Blocks.Stone << 4);
            for (int cx = -2; cx <= 2; cx++)
            for (int cz = -2; cz <= 2; cz++)
            {
                var body = ChunkData.EncodeBody(cx, cz, full: true, null, sections, hasSky: true, skyLight: 0);
                server.Send(0x20, w => w.WriteBytes(body));
            }
            server.Send(0x2F, w => w.WriteDouble(8.5).WriteDouble(144).WriteDouble(8.5).WriteFloat(0).WriteFloat(0).WriteUByte(0).WriteVarInt(1));
            var confirm = server.ReadUntil(0x00);
            Assert.Equal(1, new WireReader(confirm).ReadVarInt());
            var echo = server.ReadUntil(0x0E);
            var er = new WireReader(echo);
            Assert.Equal((8.5, 144.0, 8.5), (er.ReadDouble(), er.ReadDouble(), er.ReadDouble()));
        });
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20))) await client.ConnectAsync(cts.Token);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(client.IsPlaying);
        Assert.Equal(77, client.World.Player.EntityId);
        Assert.Equal(new Vec3d(8.5, 144, 8.5), client.World.Player.Position);
        Assert.Equal(Blocks.Stone, client.World.GetBlock(8, 143, 8));
        Assert.Equal(0, client.World.GetBlock(8, 144, 8));
        Assert.Equal(15, client.World.LightAt(new BlockPos(8, 144, 8)).Sky); // the absent sections: open sky
        Assert.Equal(144, client.World.Light.Height(8, 8));
        Assert.Equal(new BlockPos(8, 144, 8), client.World.Spawn);
        Assert.Equal(20, client.World.Player.Health);

        // Keep-alive: answered by the connection with the same long.
        var served = Task.Run(() =>
        {
            server.Send(0x1F, w => w.WriteLong(987654321987L));
            return new WireReader(server.ReadUntil(0x0B)).ReadLong();
        });
        Assert.Equal(987654321987L, await served.WaitAsync(TimeSpan.FromSeconds(10)));

        // Chat in (JSON → legacy) and out (≤ 256 chars on 1.12.2: one line).
        server.Send(0x0F, w => w.WriteString(ChatComponent.Translate("chat.type.text", "kiwi", "hi maid")).WriteSByte(0));
        server.Send(0x0F, w => w.WriteString(ChatComponent.Text("action bar")).WriteSByte(2)); // game info: not chat
        await Until(() => chat.Contains("<kiwi> hi maid"));
        var said = Task.Run(() => new WireReader(server.ReadUntil(0x02)).ReadString(256));
        var longLine = string.Join(" ", Enumerable.Repeat("word", 40)); // 199 chars: two lines on b1.8.1, one here
        var sent = await client.SendChatAsync(longLine);
        Assert.True(sent.Ok, sent.Message);
        Assert.Equal(longLine, await said.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain("action bar", chat);

        // Block change (state = id << 4 | meta) and a multi block change above y 128.
        server.Send(0x0B, w => w.WritePosition(10, 143, 10).WriteVarInt(0));
        server.Send(0x10, w => w.WriteInt(0).WriteInt(0).WriteVarInt(1).WriteUByte(0x23).WriteUByte(150).WriteVarInt(Blocks.Wool << 4 | 5));
        await Until(() => client.World.GetBlock(10, 143, 10) == 0 && client.World.GetBlock(2, 150, 3) == Blocks.Wool);
        Assert.Equal(5, client.World.GetMeta(2, 150, 3));

        // P2 features answer clearly instead of crashing or desyncing.
        var dig = await client.DigBlockAsync(8, 143, 9);
        Assert.False(dig.Ok);
        Assert.Contains("not supported on 1.12.2 yet", dig.Message);
        Assert.True(client.IsPlaying);

        // Disconnect with a JSON reason.
        server.Send(0x1A, w => w.WriteString(ChatComponent.Translate("multiplayer.disconnect.kicked")));
        Assert.Equal("Kicked by an operator.", await kicked.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Until(() => !client.IsPlaying);
        Assert.Equal("Kicked by an operator.", client.LastKickReason);
    }

    private static async Task Until(Func<bool> cond, int ms = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }
}
