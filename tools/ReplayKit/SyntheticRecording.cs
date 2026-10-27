using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Recording;

namespace MineClaw.Replay;

/// <summary>
/// A hand-built calibration session (no server needed): login onto a 7x7 island with a cobble patch, a tree and a chest;
/// Fox (poofimafox) mines 5 cobblestone, Robin (kiwi) places 3 blocks at the edge, chat + a /me, the bot digs a log while
/// Robin stands facing it (must stay the bot's), Fox writes a sign, Robin falls into the void. Deterministic bytes.
/// </summary>
public static class SyntheticRecording
{
    public const string Self = "maid_chan";
    public static readonly DateTimeOffset Start = new(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);

    /// <summary>Hand-noted ground truth for <see cref="Build"/> (the format CalibrationReport reads).</summary>
    public const string Truth = """
        # synthetic calibration session: t  player  kind  [block]  [xN]
        00:05.3  poofimafox  broke       cobblestone  x5
        00:15.3  kiwi        placed      cobblestone  x3
        00:22.0  kiwi        chat
        00:25.0  poofimafox  chat
        00:28.1  maid_chan   self_dug    log
        00:32.0  poofimafox  wrote_sign
        00:40.0  kiwi        died
        """;

    private const int Fox = 7, Robin = 8;

    public static void Write(Stream output)
    {
        long now = 0;
        using var rec = new PacketRecorder(output, gzip: false, meta: $"user={Self};host=synthetic;protocol=17", start: Start, leaveOpen: true) { Clock = () => now };
        void S(long t, IPacket p) { now = t; rec.Record(PacketDirection.ClientBound, Wire(p, PacketDirection.ClientBound)); }
        void C(long t, IPacket p) { now = t; rec.Record(PacketDirection.ServerBound, Wire(p, PacketDirection.ServerBound)); }

        // ---- login
        C(0, new HandshakePacket(Self));
        S(20, new HandshakePacket(ProtocolInfo.OfflineConnectionHash));
        C(40, LoginRequestPacket.ForClient(Self));
        S(60, new LoginRequestPacket { EntityId = 42, MapSeed = 1234, ServerMode = 0, Dimension = 0, Difficulty = 1, WorldHeight = 128, MaxPlayers = 8 });
        S(61, new SpawnPositionPacket { X = 0, Y = 64, Z = 0 });
        S(62, new TimeUpdatePacket { Time = 6000 });
        S(63, new PlayerListItemPacket { PlayerName = Self, Online = true, Ping = 1 });
        S(64, new PlayerListItemPacket { PlayerName = "poofimafox", Online = true, Ping = 1 });
        S(65, new PlayerListItemPacket { PlayerName = "kiwi", Online = true, Ping = 1 });
        var island = Island();
        for (int cx = -1; cx <= 0; cx++)
        for (int cz = -1; cz <= 0; cz++)
        {
            S(70, new PreChunkPacket { ChunkX = cx, ChunkZ = cz, Load = true });
            S(70, ChunkDataDecoder.Encode(Chunk(island, cx, cz)));
        }
        S(80, new WindowItemsPacket { WindowId = 0, Items = Enumerable.Repeat(ItemStack.Empty, 45).ToArray() });
        S(81, new UpdateHealthPacket { Health = 20, Food = 20, FoodSaturation = 5 });
        S(90, new PlayerPositionLookPacket { X = 0.5, Y = 64, Stance = 65.62, Z = 0.5, Yaw = 0, Pitch = 0, OnGround = true });
        C(100, new PlayerPositionLookPacket { X = 0.5, Y = 64, Stance = 65.62, Z = 0.5, Yaw = 0, Pitch = 0, OnGround = true });

        // ---- players appear
        S(2000, Spawn(Fox, "poofimafox", -1.5, 64, -1.5));
        S(2000, Spawn(Robin, "kiwi", 1.5, 64, -1.5));

        // ---- Fox mines 5 cobblestone at the patch x=-3, z=-2..2 (faces each, swings, block gone)
        for (int i = 0; i < 5; i++)
        {
            long t = 5000 + i * 1500L;
            var b = (-3, 64, -2 + i);
            S(t, Teleport(Fox, -1.5, 64, -2 + i + 0.5, Face((-1.5, 64, -2 + i + 0.5), b)));
            S(t + 100, new AnimationPacket { EntityId = Fox, Animation = 1 });
            S(t + 300, new AnimationPacket { EntityId = Fox, Animation = 1 });
            S(t + 350, new BlockChangePacket { X = b.Item1, Y = b.Item2, Z = b.Item3, BlockId = 0 });
        }

        // ---- Robin places 3 cobblestone at the east edge (x = 3), standing inside: must not alarm
        for (int i = 0; i < 3; i++)
        {
            long t = 15000 + i * 1200L;
            var b = (3, 64, -1 + i);
            S(t, Teleport(Robin, 1.5, 64, -1 + i + 0.5, Face((1.5, 64, -1 + i + 0.5), b)));
            S(t + 200, new AnimationPacket { EntityId = Robin, Animation = 1 });
            S(t + 300, new BlockChangePacket { X = b.Item1, Y = b.Item2, Z = b.Item3, BlockId = (byte)Blocks.Cobblestone });
        }

        // ---- chat + /me
        S(22000, new ChatPacket("<kiwi> hi maid-chan"));
        S(25000, new ChatPacket("* poofimafox waves at everyone"));

        // ---- the bot digs a log at (1,64,1) while Robin stands right there facing it and swinging: still the bot's
        S(27500, Teleport(Robin, 1.5, 64, 2.5, Face((1.5, 64, 2.5), (1, 64, 1))));
        C(27600, new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusStarted, X = 1, Y = 64, Z = 1, Face = 1 });
        S(27700, new AnimationPacket { EntityId = Robin, Animation = 1 });
        C(28100, new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusFinished, X = 1, Y = 64, Z = 1, Face = 1 });
        S(28150, new BlockChangePacket { X = 1, Y = 64, Z = 1, BlockId = 0 });

        // ---- Fox writes a sign at (-1,64,2)
        S(31500, Teleport(Fox, -1.5, 64, 0.5, Face((-1.5, 64, 0.5), (-1, 64, 2))));
        S(31800, new AnimationPacket { EntityId = Fox, Animation = 1 });
        S(32000, new BlockChangePacket { X = -1, Y = 64, Z = 2, BlockId = (byte)Blocks.SignPost, Metadata = 8 });
        S(32400, new UpdateSignPacket { X = -1, Y = 64, Z = 2, Lines = ["fox", "was here", "", ""] });

        // ---- Robin walks off the west edge and falls into the void
        S(38000, Teleport(Robin, -3.4, 64, 0.5, (90f, 0f)));
        S(38500, Teleport(Robin, -4.5, 60, 0.5, (90f, 0f)));
        S(39000, Teleport(Robin, -4.5, 30, 0.5, (90f, 0f)));
        S(40000, new ChatPacket("kiwi fell out of the world"));
        S(40050, new DestroyEntityPacket { EntityId = Robin });
        S(45000, new KeepAlivePacket(1));
    }

    public static byte[] Build()
    {
        var ms = new MemoryStream();
        Write(ms);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Wire(IPacket p, PacketDirection dir)
    {
        var ms = new MemoryStream();
        PacketRegistry.Write(new MinecraftWriter(ms), p, dir);
        return ms.ToArray();
    }

    /// <summary>7x7 grass island at y 63 (dirt 61-62) around 0,0; cobble patch x=-3; a log at 1,64,1; a chest at 2,64,2.</summary>
    private static Dictionary<(int, int, int), int> Island()
    {
        var b = new Dictionary<(int, int, int), int>();
        for (int x = -3; x <= 3; x++)
        for (int z = -3; z <= 3; z++)
        {
            b[(x, 61, z)] = Blocks.Dirt;
            b[(x, 62, z)] = Blocks.Dirt;
            b[(x, 63, z)] = Blocks.Grass;
        }
        for (int z = -2; z <= 2; z++) b[(-3, 64, z)] = Blocks.Cobblestone;
        b[(1, 64, 1)] = Blocks.Log;
        b[(1, 65, 1)] = Blocks.Log;
        b[(2, 64, 2)] = Blocks.Chest;
        return b;
    }

    private static ChunkRegion Chunk(Dictionary<(int, int, int), int> blocks, int cx, int cz)
    {
        var r = new ChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16);
        Array.Fill(r.SkyLight, (byte)15);
        foreach (var ((x, y, z), id) in blocks)
            if (x >> 4 == cx && z >> 4 == cz) r.BlockIds[r.Index(x - cx * 16, y, z - cz * 16)] = (byte)id;
        return r;
    }

    private static NamedEntitySpawnPacket Spawn(int id, string name, double x, double y, double z) => new()
    {
        EntityId = id, PlayerName = name, X = FixedPoint.ToAbsolute(x), Y = FixedPoint.ToAbsolute(y), Z = FixedPoint.ToAbsolute(z),
    };

    private static EntityTeleportPacket Teleport(int id, double x, double y, double z, (float Yaw, float Pitch) look) => new()
    {
        EntityId = id, X = FixedPoint.ToAbsolute(x), Y = FixedPoint.ToAbsolute(y), Z = FixedPoint.ToAbsolute(z),
        Yaw = FixedPoint.ToAngle(look.Yaw), Pitch = FixedPoint.ToAngle(look.Pitch),
    };

    /// <summary>Look from a player's eye (feet + 1.62) at a block centre.</summary>
    private static (float, float) Face((double X, double Y, double Z) feet, (int X, int Y, int Z) block) =>
        MineClaw.World.PlayerPhysics.YawPitchTo(new MineClaw.World.Vec3d(feet.X, feet.Y + MineClaw.World.PlayerPhysics.EyeHeight, feet.Z),
            new MineClaw.World.Vec3d(block.X + 0.5, block.Y + 0.5, block.Z + 0.5));
}
