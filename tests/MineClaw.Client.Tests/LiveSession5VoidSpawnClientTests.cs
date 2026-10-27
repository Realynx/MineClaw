using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-27 07:49 / 21:30: fresh clone maid_chan_420 logged in at -5.5,0.594,-8.5, over the void (b1.8.1 EntityPlayer.java:36-42:
/// a new player lands on the top block of a random column ±10 around the world spawn, y≈0 in a void column). The client must hold
/// still there — no gravity, no movement — without ever confirming the login teleport: b1.8.1 only runs its movement checks
/// (and the "Flying is not enabled on this server" floating counter, NetServerHandler.java:216-226) after the client echoes the
/// 0x0D with the same X/Z (NetServerHandler.java:66-71). Walking jobs fail at once; a /tp onto ground ends the hold.
/// </summary>
public class LiveSession5VoidSpawnClientTests
{
    /// <summary>The server-side state of mc-dev's NetServerHandler that decides the floating kick.</summary>
    private sealed class FloatingKickModel
    {
        private double _m, _n, _o;
        /// <summary>The server's idea of the player position (a bare 0x0A is checked there).</summary>
        public (double X, double Y, double Z) At { get; private set; }
        public bool CheckMovement { get; private set; } = true;
        public int Floating { get; private set; }
        public int MaxFloating { get; private set; }

        public void Teleport(double x, double y, double z) { CheckMovement = false; (_m, _n, _o) = (x, y, z); At = (x, y, z); }

        /// <summary>A C→S flying packet (x/y/z = 0 for a bare 0x0A, Packet10Flying.java:8-10); <paramref name="nearBlock"/> = any
        /// block in the player box grown 0.0625 and lowered 0.55 (World.b(AABB)).</summary>
        public void Flying(bool hasPos, double x, double y, double z, bool nearBlock)
        {
            if (!CheckMovement)
            {
                double d0 = (hasPos ? y : 0) - _n;
                if ((hasPos ? x : 0) == _m && d0 * d0 < 0.01 && (hasPos ? z : 0) == _o) CheckMovement = true;
            }
            if (!CheckMovement) return;
            if (hasPos) At = (x, y, z);
            if (!nearBlock) { Floating++; MaxFloating = Math.Max(MaxFloating, Floating); }
            else Floating = 0;
        }
    }

    [Fact]
    public async Task A_clone_spawned_over_the_void_holds_unconfirmed_never_floats_and_resumes_after_a_tp()
    {
        var model = new FloatingKickModel();
        var held = new List<Vec3d>();
        var ended = new List<Vec3d>();
        var server = new FakeServer { SpawnAt = (-5.5, 0.594, -8.5) }; // the stone platform is at 6..10,63,6..10: void here
        model.Teleport(-5.5, 0.594, -8.5);
        server.Hook = p =>
        {
            switch (p)
            {
                case PlayerPositionLookPacket pl: model.Flying(true, pl.X, pl.Y, pl.Z, NearBlock(server, pl.X, pl.Y, pl.Z)); break;
                case PlayerPositionPacket pp: model.Flying(true, pp.X, pp.Y, pp.Z, NearBlock(server, pp.X, pp.Y, pp.Z)); break;
                case PlayerLookPacket or PlayerOnGroundPacket: model.Flying(false, 0, 0, 0, NearBlock(server, model.At.X, model.At.Y, model.At.Z)); break;
            }
            return false;
        };
        var o = new ClientOptions
        {
            Username = "maid_chan_420", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
        };
        await using var c = new MinecraftClient(o);
        using var _s = server;
        c.HoldingOverVoid += p => { lock (held) held.Add(p); };
        c.VoidHoldEnded += p => { lock (ended) ended.Add(p); };
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await c.ConnectAsync(cts.Token);

        // 120 flying packets (> the 80 that kick a confirmed floater): held still, never confirmed, so never counted.
        Assert.True(await Harness.WaitAsync(() => { lock (held) return held.Count == 1; }, 3000), "void hold raised");
        long t0 = c.TickCount;
        Assert.True(await Harness.WaitAsync(() => c.TickCount > t0 + 120, 5000));
        Assert.True(c.IsFrozenForSafety);
        Assert.Equal(0.594, c.World.Player.Position.Y, 6);
        Assert.Equal(-5.5, c.World.Player.Position.X, 6);
        Assert.NotNull(c.HoldingOverVoidAt);
        Assert.False(model.CheckMovement, "the login teleport must stay unconfirmed over the void");
        Assert.Equal(0, model.MaxFloating);
        Assert.Empty(server.Got<PlayerPositionLookPacket>());
        Assert.Empty(server.Got<PlayerPositionPacket>());
        lock (held) Assert.Single(held); // once per hold

        // No walk back from y 0 (21:30:11 "walking there" then "the walk back failed"): it fails at once with the reason.
        var g = await c.GotoAsync(8, 64, 8);
        Assert.False(g.Ok);
        Assert.Contains("holding still over the void at -6,0,-9", g.Message);

        // Fox: /tp maid_chan_420 maid_chan → a 0x0D onto the platform: confirmed at once, the hold ends, it can move again.
        model.Teleport(8.5, 64, 8.5);
        server.Send(new PlayerPositionLookPacket { X = 8.5, Y = 64, Stance = 65.62, Z = 8.5, OnGround = false });
        Assert.True(await Harness.WaitAsync(() => !c.IsFrozenForSafety && c.World.Player.OnGround, 3000));
        Assert.True(await Harness.WaitAsync(() => { lock (ended) return ended.Count == 1; }, 3000), "hold ended");
        Assert.Null(c.HoldingOverVoidAt);
        var echo = Assert.Single(server.Got<PlayerPositionLookPacket>());
        Assert.Equal((8.5, 64.0, 8.5), (echo.X, echo.Y, echo.Z));
        Assert.True(model.CheckMovement);
        var walk = await c.GotoAsync(10, 64, 8);
        Assert.True(walk.Ok, walk.ToString());
        Assert.Equal(0, model.MaxFloating);
    }

    [Fact]
    public async Task A_login_teleport_onto_loaded_ground_is_still_confirmed_at_once()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _c = c;
        using var _s = s;
        var echo = s.Got<PlayerPositionLookPacket>().First();
        Assert.Equal((8.5, 64.0), (echo.X, echo.Y));
        Assert.Null(c.HoldingOverVoidAt);
        Assert.False(c.IsFrozenForSafety);
    }

    /// <summary>
    /// Live 10-27 22:33 after the session-4 deploy: main and 420 both froze at "chunks … not loaded yet; holding still". The real
    /// server streams the spawn chunks only after the login 0x0D is confirmed, so the confirmation must not wait for them.
    /// </summary>
    [Fact]
    public async Task Login_confirms_before_the_chunks_arrive_like_the_real_server()
    {
        var server = new FakeServer { ChunksAfterConfirm = true };
        var o = new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
        };
        await using var c = new MinecraftClient(o);
        using var _s = server;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await c.ConnectAsync(cts.Token);
        Assert.True(await Harness.WaitAsync(() => !c.IsFrozenForSafety && c.World.Player.OnGround, 3000),
            "login deadlocked: the client withheld the confirmation the server needs before it sends chunks");
        Assert.Null(c.HoldingOverVoidAt);
    }

    /// <summary>The same real-server ordering for a void spawn: confirmed (chunks need it), then silent so it never floats.</summary>
    [Fact]
    public async Task Void_spawn_with_late_chunks_holds_silently_and_never_reaches_the_floating_kick()
    {
        var model = new FloatingKickModel();
        var server = new FakeServer { SpawnAt = (-5.5, 0.594, -8.5), ChunksAfterConfirm = true };
        model.Teleport(-5.5, 0.594, -8.5);
        server.Hook = p =>
        {
            switch (p)
            {
                case PlayerPositionLookPacket pl: model.Flying(true, pl.X, pl.Y, pl.Z, NearBlock(server, pl.X, pl.Y, pl.Z)); break;
                case PlayerPositionPacket pp: model.Flying(true, pp.X, pp.Y, pp.Z, NearBlock(server, pp.X, pp.Y, pp.Z)); break;
                case PlayerLookPacket or PlayerOnGroundPacket: model.Flying(false, 0, 0, 0, NearBlock(server, model.At.X, model.At.Y, model.At.Z)); break;
            }
            return false;
        };
        var o = new ClientOptions
        {
            Username = "maid_chan_420", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
        };
        await using var c = new MinecraftClient(o);
        using var _s = server;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await c.ConnectAsync(cts.Token);
        Assert.True(await Harness.WaitAsync(() => c.HoldingOverVoidAt is not null, 3000), "void hold raised");
        long t0 = c.TickCount;
        Assert.True(await Harness.WaitAsync(() => c.TickCount > t0 + 120, 5000));
        Assert.True(model.CheckMovement); // confirmed: the chunks needed it
        Assert.True(model.MaxFloating < 80, $"floating counter reached {model.MaxFloating}: would be kicked");
        Assert.Equal(0.594, c.World.Player.Position.Y, 6);
    }

    /// <summary>World.b(AABB) over the player box (0.6 × 1.8) grown 0.0625 and moved down 0.55, on the fake server's blocks.</summary>
    private static bool NearBlock(FakeServer s, double x, double y, double z)
    {
        double minX = x - 0.3 - 0.0625, maxX = x + 0.3 + 0.0625, minY = y - 0.0625 - 0.55, maxY = y + 1.8 + 0.0625 - 0.55;
        double minZ = z - 0.3 - 0.0625, maxZ = z + 0.3 + 0.0625;
        for (int bx = (int)Math.Floor(minX); bx < (int)Math.Floor(maxX + 1); bx++)
        for (int by = (int)Math.Floor(minY); by < (int)Math.Floor(maxY + 1); by++)
        for (int bz = (int)Math.Floor(minZ); bz < (int)Math.Floor(maxZ + 1); bz++)
            if (s.Blocks.TryGetValue((bx, by, bz), out int id) && id != 0) return true;
        return false;
    }
}
