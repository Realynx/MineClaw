using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Recording;

namespace MineClaw.Client.Tests;

public class RecorderTests
{
    [Fact]
    public async Task Client_records_both_directions_of_a_session_to_an_mcrec_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-rec-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var (c, s) = await Harness.StartAsync(srv => srv.Blocks[(10, 64, 8)] = Blocks.Dirt, o => o with { RecordDirectory = dir });
            string? path;
            using (s)
            {
                path = c.RecordingPath;
                Assert.NotNull(path);
                Assert.True((await c.DigBlockAsync(10, 64, 8)).Ok);
                await Harness.WaitAsync(() => c.World.GetBlock(10, 64, 8) == 0);
                await c.DisposeAsync();
            }
            var rec = PacketRecording.Load(path!);
            Assert.False(rec.Truncated);
            Assert.Equal("maid_chan", rec.MetaValue("user"));
            Assert.Equal(PacketDirection.ServerBound, rec.Frames[0].Direction);
            Assert.IsType<HandshakePacket>(rec.Frames[0].Parse());
            Assert.Contains(rec.Frames, f => f.Direction == PacketDirection.ClientBound && f.Id == MapChunkPacket.PacketId);
            var dig = rec.Frames.Where(f => f.Direction == PacketDirection.ServerBound && f.Id == PlayerDiggingPacket.PacketId)
                .Select(f => (PlayerDiggingPacket)f.Parse()).ToList();
            Assert.Contains(dig, d => d.Status == PlayerDiggingPacket.StatusFinished && (d.X, d.Y, d.Z) == (10, 64, 8));
            var change = rec.Frames.Where(f => f.Direction == PacketDirection.ClientBound && f.Id == BlockChangePacket.PacketId)
                .Select(f => (BlockChangePacket)f.Parse()).First(b => (b.X, b.Y, b.Z) == (10, 64, 8));
            Assert.Equal(0, change.BlockId);
            Assert.True(rec.Frames.Zip(rec.Frames.Skip(1)).All(p => p.First.OffsetMs <= p.Second.OffsetMs));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("rec", "1", "rec")]
    [InlineData(null, "0", null)]
    [InlineData(null, "D:/x", "D:/x")]
    public void Record_directory_comes_from_options_then_env(string? configured, string? env, string? expected) =>
        Assert.Equal(expected, MinecraftClient.ResolveRecordDirectory(configured, env));

    [Fact]
    public void Env_1_means_logs_recordings() =>
        Assert.EndsWith(Path.Combine("logs", "recordings"), MinecraftClient.ResolveRecordDirectory(null, "1"));

    [Fact]
    public async Task Own_digs_are_noted_as_self_edits()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Blocks[(10, 64, 8)] = Blocks.Dirt);
        await using var _ = c;
        using var __ = s;
        bool seen = false;
        c.World.BlockChanged += ch => { if (ch.Pos == new MineClaw.World.BlockPos(10, 64, 8)) seen = c.World.SelfEdits.IsRecent(ch.Pos); };
        Assert.True((await c.DigBlockAsync(10, 64, 8)).Ok);
        // The dig returns once the world shows air; the BlockChanged handler runs right after the write on the network
        // thread, so give it a moment instead of racing it.
        for (int i = 0; i < 100 && !Volatile.Read(ref seen); i++) await Task.Delay(10);
        Assert.True(seen);
        Assert.False(c.World.SelfEdits.IsRecent(new MineClaw.World.BlockPos(0, 0, 0)));
    }
}
