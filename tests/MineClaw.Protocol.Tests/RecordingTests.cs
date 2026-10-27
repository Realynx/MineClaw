using System.Diagnostics;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Recording;

namespace MineClaw.Protocol.Tests;

public class RecordingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static byte[] Wire(IPacket p, PacketDirection dir)
    {
        var ms = new MemoryStream();
        PacketRegistry.Write(new MinecraftWriter(ms), p, dir);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Round_trip_keeps_order_direction_time_and_bytes(bool gzip)
    {
        var ms = new MemoryStream();
        long now = 0;
        var start = new DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);
        var frames = new List<(long, PacketDirection, byte[])>
        {
            (0, PacketDirection.ServerBound, Wire(new HandshakePacket("maid_chan"), PacketDirection.ServerBound)),
            (12, PacketDirection.ClientBound, Wire(new ChatPacket("<kiwi> hi maid"), PacketDirection.ClientBound)),
            (12, PacketDirection.ClientBound, Wire(new BlockChangePacket { X = 3, Y = 64, Z = -1, BlockId = 17, Metadata = 2 }, PacketDirection.ClientBound)),
            (70_000, PacketDirection.ServerBound, Wire(new PlayerDiggingPacket { Status = 2, X = 3, Y = 64, Z = -1, Face = 1 }, PacketDirection.ServerBound)),
        };
        using (var rec = new PacketRecorder(ms, gzip, "user=maid_chan;host=test", start, leaveOpen: true) { Clock = () => now })
            foreach (var (t, dir, raw) in frames)
            {
                now = t;
                rec.Record(dir, raw);
            }
        ms.Position = 0;
        var r = PacketRecording.Read(ms);
        Assert.False(r.Truncated);
        Assert.Equal(gzip, r.Gzip);
        Assert.Equal(start, r.Start);
        Assert.Equal("maid_chan", r.MetaValue("user"));
        Assert.Equal(frames.Count, r.Frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].Item1, r.Frames[i].OffsetMs);
            Assert.Equal(frames[i].Item2, r.Frames[i].Direction);
            Assert.Equal(frames[i].Item3, r.Frames[i].Raw);
        }
        Assert.Equal("<kiwi> hi maid", Assert.IsType<ChatPacket>(r.Frames[1].Parse()).Message);
        Assert.Equal(TimeSpan.FromSeconds(70), r.Duration);
    }

    [Fact]
    public void Truncated_tail_keeps_every_complete_frame()
    {
        var ms = new MemoryStream();
        using (var rec = new PacketRecorder(ms, gzip: false, leaveOpen: true) { Clock = () => 5 })
            for (int i = 0; i < 10; i++) rec.Record(PacketDirection.ClientBound, Wire(new KeepAlivePacket(i), PacketDirection.ClientBound));
        var cut = new MemoryStream(ms.ToArray()[..^3]);
        var r = PacketRecording.Read(cut);
        Assert.True(r.Truncated);
        Assert.Equal(9, r.Frames.Count);
    }

    [Fact]
    public void Attached_to_a_connection_records_both_directions_cheaply()
    {
        var (clientEnd, serverEnd) = DuplexStream.CreatePair();
        using var client = new PacketConnection(clientEnd);
        using var server = new PacketConnection(serverEnd, inbound: PacketDirection.ServerBound);
        var file = new MemoryStream();
        var rec = new PacketRecorder(file, gzip: true, leaveOpen: true);
        rec.Attach(client);

        var t = Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++) Assert.IsType<PlayerPositionPacket>(server.ReadPacket());
            for (int i = 0; i < 2000; i++) server.Send(new EntityRelativeMovePacket { EntityId = i, DX = 1, DY = 0, DZ = -1 });
        });
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 2000; i++) client.Send(new PlayerPositionPacket { X = i, Y = 64, Stance = 65.62, Z = 0, OnGround = true });
        for (int i = 0; i < 2000; i++) Assert.IsType<EntityRelativeMovePacket>(client.ReadPacket());
        t.Wait();
        rec.Dispose();
        output.WriteLine($"4000 packets, {rec.Bytes} payload bytes → {file.Length} bytes gzip; recorder time {rec.TimeSpent.TotalMilliseconds:0.0} ms of {sw.ElapsedMilliseconds} ms");
        Assert.True(rec.TimeSpent.TotalMilliseconds / 4000 < 0.05, "recorder must stay well under 50 µs/packet");
        file.Position = 0;
        var r = PacketRecording.Read(file);
        Assert.Equal(4000, r.Frames.Count);
        Assert.Equal(2000, r.Frames.Count(f => f.Direction == PacketDirection.ServerBound && f.Id == PlayerPositionPacket.PacketId));
        Assert.Equal(2000, r.Frames.Count(f => f.Direction == PacketDirection.ClientBound && f.Id == EntityRelativeMovePacket.PacketId));
        Assert.True(file.Length < rec.Bytes, "gzip should shrink the stream");
    }

    [Fact]
    public void CreateFile_names_the_recording_by_date_and_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcrec-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var when = new DateTimeOffset(2026, 10, 26, 21, 5, 9, TimeSpan.Zero).ToLocalTime();
            string p1, p2;
            using (var a = PacketRecorder.CreateFile(dir, gzip: true, now: when)) { a.Record(PacketDirection.ClientBound, [0x00, 0, 0, 0, 1]); p1 = a.Path!; }
            using (var b = PacketRecorder.CreateFile(dir, gzip: true, now: when)) p2 = b.Path!;
            Assert.Equal(when.ToString("yyyyMMdd-HHmmss") + ".mcrec", Path.GetFileName(p1));
            Assert.EndsWith("-2.mcrec", p2);
            Assert.Single(PacketRecording.Load(p1).Frames);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
