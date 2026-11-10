using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Recording;

namespace MineClaw.Replay;

/// <summary>
/// A hand-built hive chat session with made-up chat (no server needed): main (maid_chan) and one player, alex, on the
/// calibration island; clone 67 (maid_chan_67) joins 20 s in. Each bot's recording holds the server chat from its own login
/// on: a request for main, a group ask, lines naming 67 and alex's unnamed follow-ups, a line naming main, the bots' own
/// replies (one wrapped onto a second line) and system lines. alex builds at the edge and falls into the void twice, so the
/// clone's world noticing has something to report. Deterministic bytes.
/// </summary>
public static class SyntheticHiveChat
{
    public const string Main = "maid_chan", Clone = "maid_chan_67", Player = "alex";
    public const string MainFile = "main-maid_chan.mcrec", CloneFile = "clone-maid_chan_67.mcrec";
    public static readonly DateTimeOffset Start = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);

    /// <summary>When the clone's login ends (ms after main's): its recording starts here.</summary>
    public const long CloneOnline = 20_500;

    private const int Alex = 7;

    /// <summary>The server chat (ms after main's login).</summary>
    private static readonly (long At, string Line)[] Chat =
    [
        (4_000, "<alex> hi maid"),
        (8_000, "<alex> chop down one tree pls"),
        (9_500, "<maid_chan> on it, chopping the oak by the chest now"),
        (20_000, "§emaid_chan_67 joined the game."),
        (26_000, "<alex> both of you"),
        (31_000, "<alex> both of you wave pls"),
        (33_000, "<maid_chan> waving at you with 67 uwu"),
        (40_000, "<alex> 67 come w me"),
        (41_500, "<maid_chan_67> omw!"),
        (47_000, "<alex> no"),
        (49_000, "<alex> from above"),
        (55_000, "<alex> 67 i'm here!"),
        (58_000, "<alex> climb to the top"),
        (66_000, "<alex> main do a little hop"),
        (67_000, "<maid_chan> I only hop when I'm pathing or stacking blocks, and I'm out of blocks right now. If you toss me some"),
        (67_001, "cobble I could pillar up for you uwu"),
        (75_000, "alex fell out of the world"),
        (76_000, "<maid_chan> alex noooo, you fell into the void D: tell me what you lost and I'll help get it back"),
        (88_000, "alex fell out of the world"),
        (89_000, "<maid_chan> alex, careful near the edges please D:"),
    ];

    /// <summary>One bot's recording (main's, or the clone's from <see cref="CloneOnline"/> on).</summary>
    public static void Write(Stream output, bool clone)
    {
        string self = clone ? Clone : Main;
        long t0 = clone ? CloneOnline : 0, now = 0;
        using var rec = new PacketRecorder(output, gzip: false, meta: $"user={self};host=synthetic;protocol=17", start: Start + TimeSpan.FromMilliseconds(t0),
            leaveOpen: true) { Clock = () => now };
        void S(long t, IPacket p) { now = t - t0; rec.Record(PacketDirection.ClientBound, SyntheticRecording.Wire(p, PacketDirection.ClientBound)); }
        void C(long t, IPacket p) { now = t - t0; rec.Record(PacketDirection.ServerBound, SyntheticRecording.Wire(p, PacketDirection.ServerBound)); }

        // ---- login (alex and, for the clone, main are already there)
        C(t0, new HandshakePacket(self));
        S(t0 + 20, new HandshakePacket(ProtocolInfo.OfflineConnectionHash));
        C(t0 + 40, LoginRequestPacket.ForClient(self));
        S(t0 + 60, new LoginRequestPacket { EntityId = clone ? 43 : 42, MapSeed = 1234, ServerMode = 0, Dimension = 0, Difficulty = 1, WorldHeight = 128, MaxPlayers = 8 });
        S(t0 + 61, new SpawnPositionPacket { X = 0, Y = 64, Z = 0 });
        S(t0 + 62, new TimeUpdatePacket { Time = 6000 });
        S(t0 + 63, new PlayerListItemPacket { PlayerName = self, Online = true, Ping = 1 });
        S(t0 + 64, new PlayerListItemPacket { PlayerName = Player, Online = true, Ping = 1 });
        if (clone) S(t0 + 65, new PlayerListItemPacket { PlayerName = Main, Online = true, Ping = 1 });
        var island = SyntheticRecording.Island();
        for (int cx = -1; cx <= 0; cx++)
        for (int cz = -1; cz <= 0; cz++)
        {
            S(t0 + 70, new PreChunkPacket { ChunkX = cx, ChunkZ = cz, Load = true });
            S(t0 + 70, ChunkDataDecoder.Encode(SyntheticRecording.Chunk(island, cx, cz)));
        }
        S(t0 + 80, new WindowItemsPacket { WindowId = 0, Items = Enumerable.Repeat(ItemStack.Empty, 45).ToArray() });
        S(t0 + 81, new UpdateHealthPacket { Health = 20, Food = 20, FoodSaturation = 5 });
        double x = clone ? 1.5 : 0.5;
        S(t0 + 90, new PlayerPositionLookPacket { X = x, Y = 64, Stance = 65.62, Z = 0.5, Yaw = 0, Pitch = 0, OnGround = true });
        C(t0 + 100, new PlayerPositionLookPacket { X = x, Y = 64, Stance = 65.62, Z = 0.5, Yaw = 0, Pitch = 0, OnGround = true });
        S(t0 + 200, SyntheticRecording.Spawn(Alex, Player, -1.5, 64, -1.5));

        foreach (var (at, p) in Events())
            if (at >= t0 + 200) S(at, p);
        S(95_000, new KeepAlivePacket(1));
    }

    /// <summary>The session's server packets after the logins, in time order.</summary>
    private static IEnumerable<(long At, IPacket Packet)> Events()
    {
        var all = new List<(long At, IPacket Packet)>();
        all.AddRange(Chat.Select(c => (c.At, (IPacket)new ChatPacket(c.Line))));
        // alex places 3 cobblestone at the east edge (x = 3), standing inside
        for (int i = 0; i < 3; i++)
        {
            long t = 34_000 + i * 1200L;
            var b = (3, 64, -1 + i);
            all.Add((t, SyntheticRecording.Teleport(Alex, 1.5, 64, -1 + i + 0.5, SyntheticRecording.Face((1.5, 64, -1 + i + 0.5), b))));
            all.Add((t + 200, new AnimationPacket { EntityId = Alex, Animation = 1 }));
            all.Add((t + 300, new BlockChangePacket { X = b.Item1, Y = b.Item2, Z = b.Item3, BlockId = (byte)Blocks.Cobblestone }));
        }
        // alex walks off the west edge twice and falls into the void (respawning in between)
        foreach (long fall in new long[] { 73_000, 86_000 })
        {
            all.Add((fall, SyntheticRecording.Teleport(Alex, -3.4, 64, 0.5, (90f, 0f))));
            all.Add((fall + 500, SyntheticRecording.Teleport(Alex, -4.5, 60, 0.5, (90f, 0f))));
            all.Add((fall + 1000, SyntheticRecording.Teleport(Alex, -4.5, 30, 0.5, (90f, 0f))));
            all.Add((fall + 2050, new DestroyEntityPacket { EntityId = Alex }));
        }
        all.Add((80_000, SyntheticRecording.Spawn(Alex, Player, 0.5, 64, -1.5)));
        return all.OrderBy(e => e.At);
    }

    public static byte[] Build(bool clone)
    {
        var ms = new MemoryStream();
        Write(ms, clone);
        return ms.ToArray();
    }
}
