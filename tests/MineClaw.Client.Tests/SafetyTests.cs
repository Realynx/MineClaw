using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Void safety end-to-end against the fake server's 5×5 platform (x,z 6..10, top y=64) over the void.</summary>
public class SafetyTests
{
    private static async Task<(MinecraftClient C, FakeServer S, ConcurrentQueue<DangerEvent> Dangers)> StartAsync(Action<FakeServer>? setup = null)
    {
        var (c, s) = await Harness.StartAsync(setup);
        var dangers = new ConcurrentQueue<DangerEvent>();
        c.Danger += dangers.Enqueue;
        return (c, s, dangers);
    }

    private static void Knock(FakeServer s, double vx, double vy, double vz) =>
        s.Send(new EntityVelocityPacket
        {
            EntityId = FakeServer.EntityId, VelocityX = FixedPoint.ToVelocity(vx), VelocityY = FixedPoint.ToVelocity(vy),
            VelocityZ = FixedPoint.ToVelocity(vz),
        });

    /// <summary>Samples the bot position until <paramref name="until"/>; returns true if it was ever over the void.</summary>
    private static async Task<bool> WatchAsync(MinecraftClient c, Func<bool> until, int ms)
    {
        bool over = false;
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end && !until())
        {
            over |= c.World.IsPlayerOverVoid(c.World.Player.Position);
            await Task.Delay(2);
        }
        return over;
    }

    [Fact]
    public async Task SprintGotoToTheRim_StopsAtTheEdge()
    {
        var (c, s, _) = await StartAsync();
        await using var _c = c;
        using var _s = s;
        var r = await c.GotoAsync(10, 64, 8, options: GotoOptions.Default with { AllowSprint = true });
        Assert.True(r.Ok, r.ToString());
        Assert.False(await WatchAsync(c, () => false, 300));
        Assert.Equal(64, c.World.Player.Position.Y, 3);
        Assert.True(c.SafetyStats.ForcedSneakTicks > 0);
    }

    [Fact]
    public async Task KnockbackOffTheEdge_ClutchesWithAHotbarBlock_AndReportsIt()
    {
        var (c, s, dangers) = await StartAsync(srv => srv.Inventory[37] = Harness.Stack(Blocks.Cobblestone, 16));
        await using var _c = c;
        using var _s = s;
        Assert.True((await c.GotoAsync(10, 64, 8)).Ok);
        await Task.Delay(100);
        Knock(s, 0.6, 0.4, 0);
        Assert.True(await Harness.WaitAsync(() => dangers.Any(d => d.Kind == DangerKind.Recovered), 5000),
            "no recovery: " + string.Join(" | ", dangers.Select(d => d.Message)));
        var p = c.World.Player.Position;
        Assert.True(p.Y >= 63.99, $"fell to {p}");
        Assert.False(c.World.IsPlayerOverVoid(p));
        Assert.Equal(1, c.KnockbacksReceived);
        var place = s.Got<PlayerBlockPlacementPacket>().First();
        Assert.Equal(Blocks.Cobblestone, place.HeldItem.Id);
        Assert.Contains(dangers, d => d is { Kind: DangerKind.FallingIntoVoid, Critical: true } && d.Message.Contains("placed"));
        Assert.True(c.SafetyStats.ClutchAttempts > 0);
    }

    [Fact]
    public async Task KnockbackClutch_SwapsABlockInFromTheMainInventory()
    {
        var (c, s, dangers) = await StartAsync(srv => srv.Inventory[20] = Harness.Stack(Blocks.Dirt, 5));
        await using var _c = c;
        using var _s = s;
        Assert.True((await c.GotoAsync(8, 64, 10)).Ok);
        await Task.Delay(100);
        Knock(s, 0, 0.4, 0.55);
        Assert.True(await Harness.WaitAsync(() => dangers.Any(d => d.Kind == DangerKind.Recovered), 5000),
            "no recovery: " + string.Join(" | ", dangers.Select(d => d.Message)));
        Assert.True(c.World.Player.Position.Y >= 63.99);
        Assert.Contains(s.Got<WindowClickPacket>(), k => k.Slot == 20);
        Assert.Equal(Blocks.Dirt, s.Got<PlayerBlockPlacementPacket>().First().HeldItem.Id);
    }

    [Fact]
    public async Task WeakKnockback_AirControlAlone_NoPlacement()
    {
        var (c, s, _) = await StartAsync(srv => srv.Inventory[37] = Harness.Stack(Blocks.Cobblestone, 16));
        await using var _c = c;
        using var _s = s;
        Assert.True((await c.GotoAsync(10, 64, 8)).Ok);
        await Task.Delay(100);
        Knock(s, 0.2, 0.4, 0);
        Assert.False(await WatchAsync(c, () => false, 600));
        Assert.Equal(64, c.World.Player.Position.Y, 3);
        Assert.Empty(s.Got<PlayerBlockPlacementPacket>());
    }

    [Fact]
    public async Task KnockbackWithNoBlocks_ReportsHonestlyAndCancelsTheAction()
    {
        var (c, s, dangers) = await StartAsync();
        await using var _c = c;
        using var _s = s;
        Assert.True((await c.GotoAsync(10, 64, 8)).Ok);
        await Task.Delay(100);
        Knock(s, 0.8, 0.4, 0);
        Assert.True(await Harness.WaitAsync(() => dangers.Any(d => d.Kind == DangerKind.FallingIntoVoid), 3000));
        var d = dangers.First(x => x.Kind == DangerKind.FallingIntoVoid);
        Assert.True(d.Critical);
        Assert.Contains("no placeable block", d.Message);
    }

    [Fact]
    public async Task TeleportOverTheVoid_FreezesUntilGroundIsVerified()
    {
        var (c, s, dangers) = await StartAsync();
        await using var _c = c;
        using var _s = s;
        s.Send(new PlayerPositionLookPacket { X = 20.5, Y = 70, Stance = 71.62, Z = 8.5, OnGround = false });
        Assert.True(await Harness.WaitAsync(() => c.IsFrozenForSafety && c.World.Player.Position.X > 20));
        await Task.Delay(400);
        Assert.Equal(70, c.World.Player.Position.Y, 6);
        Assert.True(await Harness.WaitAsync(() => dangers.Any(x => x.Kind == DangerKind.UnsafePosition)));
        // Actions can't move it while frozen.
        var g = await c.GotoAsync(new GoalNear(8, 64, 8, 1), GotoOptions.Default with { Timeout = TimeSpan.FromMilliseconds(300) });
        Assert.False(g.Ok);
        Assert.Equal(70, c.World.Player.Position.Y, 6);

        s.Send(new BlockChangePacket { X = 20, Y = 69, Z = 8, BlockId = (byte)Blocks.Stone });
        Assert.True(await Harness.WaitAsync(() => !c.IsFrozenForSafety && c.World.Player.OnGround));
        Assert.Equal(70, c.World.Player.Position.Y, 6);
    }

    [Fact]
    public async Task Respawn_FreezesUntilChunksArrive()
    {
        var (c, s, _) = await StartAsync();
        await using var _c = c;
        using var _s = s;
        s.Send(new RespawnPacket { Dimension = 0, Difficulty = 1, GameMode = 0, WorldHeight = 128, MapSeed = 1234 });
        s.Send(new PlayerPositionLookPacket { X = 8.5, Y = 64, Stance = 65.62, Z = 8.5, OnGround = false });
        Assert.True(await Harness.WaitAsync(() => c.IsFrozenForSafety));
        await Task.Delay(200);
        Assert.Equal(64, c.World.Player.Position.Y, 6);
        for (int cx = -1; cx <= 1; cx++)
        for (int cz = -1; cz <= 1; cz++)
            s.SendChunk(cx, cz);
        Assert.True(await Harness.WaitAsync(() => !c.IsFrozenForSafety && c.World.Player.OnGround));
        Assert.Equal(64, c.World.Player.Position.Y, 6);
    }

    [Fact]
    public async Task BridgingOutOverTheVoid_SneaksAndNeverFalls()
    {
        var (c, s, _) = await StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 16));
        await using var _c = c;
        using var _s = s;
        var go = c.GotoAsync(14, 64, 8, options: GotoOptions.Default with { AllowPlace = true });
        bool over = await WatchAsync(c, () => go.IsCompleted, 15000);
        var r = await go;
        Assert.True(r.Ok, r.ToString());
        Assert.False(over);
        Assert.Equal(14, (int)Math.Floor(c.World.Player.Position.X));
        Assert.Equal(64, c.World.Player.Position.Y, 3);
        Assert.True(s.Got<PlayerBlockPlacementPacket>().Count() >= 4);
        Assert.Contains(s.Got<EntityActionPacket>(), a => a.Action == 1);
    }

    [Fact]
    public async Task GotoRefusesAGoalOverTheVoid()
    {
        var (c, s, _) = await StartAsync();
        await using var _c = c;
        using var _s = s;
        var r = await c.GotoAsync(14, 64, 8);
        Assert.False(r.Ok);
        Assert.Contains("void", r.Message);
        Assert.True(c.World.Player.Position.X < 9);
    }

    [Fact]
    public async Task CollectDrops_ReportsItemsNearTheVoidAsLost()
    {
        var (c, s, _) = await StartAsync(srv =>
        {
            for (int x = 11; x <= 13; x++) srv.Blocks[(x, 63, 8)] = Blocks.Stone; // 1-wide stub out over the void
        });
        await using var _c = c;
        using var _s = s;
        s.SpawnDrop(13.5, 64.1, 8.5, new ItemStack(Items.Stick, 1, 0));   // on the stub: only reachable from rim cells
        s.SpawnDrop(10.5, 64.1, 10.5, new ItemStack(Items.Stick, 2, 0));  // platform corner: pickable from (9,9)
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Count >= 2));
        var watch = WatchAsync(c, () => false, 3000);
        var r = await c.CollectDropsAsync(8);
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("left near the void", r.Message);
        Assert.Equal(2, c.World.Inventory.CountItem(Items.Stick));
        Assert.True(c.World.Player.Position.X < 11, $"chased onto the stub: {c.World.Player.Position}");
        var p = c.World.Player.Position;
        Assert.True(Safety.EdgeDistance(c.World, (int)Math.Floor(p.X), 64, (int)Math.Floor(p.Z), 2) >= 2);
        Assert.False(await watch);
    }

    [Fact]
    public async Task Dig_RefusesSupportBlocksAndLava()
    {
        var (c, s, _) = await StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 9)] = Blocks.Stone;
            srv.Blocks[(10, 64, 9)] = Blocks.Lava;
        });
        await using var _c = c;
        using var _s = s;
        var r = await c.DigBlockAsync(8, 63, 8);
        Assert.False(r.Ok);
        Assert.Contains("standing on", r.Message);
        var r2 = await c.DigBlockAsync(9, 64, 9);
        Assert.False(r2.Ok);
        Assert.Contains("lava", r2.Message);
        Assert.Empty(s.Got<PlayerDiggingPacket>());
    }

    [Fact]
    public async Task Follow_StaysOffTheRimWhileThePlayerWalksAlongIt()
    {
        var (c, s, _) = await StartAsync();
        await using var _c = c;
        using var _s = s;
        s.Send(new NamedEntitySpawnPacket { EntityId = 7, PlayerName = "partner", X = 10 * 32 + 16, Y = 64 * 32, Z = 10 * 32 + 16 });
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.FindPlayer("partner") is not null));
        var follow = c.FollowPlayerAsync("partner", 1);
        bool over = false;
        // Walk the partner along the east rim: z 10 → 6.
        for (double z = 10.5; z >= 6.5; z -= 0.5)
        {
            s.Send(new EntityTeleportPacket { EntityId = 7, X = 10 * 32 + 16, Y = 64 * 32, Z = (int)(z * 32) });
            over |= await WatchAsync(c, () => false, 120);
        }
        over |= await WatchAsync(c, () => false, 800);
        var p = c.World.Player.Position;
        await c.StopAsync();
        await follow;
        Assert.False(over);
        int fx = (int)Math.Floor(p.X), fz = (int)Math.Floor(p.Z);
        Assert.True(Safety.EdgeDistance(c.World, fx, 64, fz, 2) >= 2, $"ended on the rim at {p}");
        Assert.Equal(9, fx); // as close to the partner as the margin allows
    }

    [Fact]
    public async Task HostileNearTheEdge_StepsInward()
    {
        var (c, s, _) = await StartAsync();
        await using var _c = c;
        using var _s = s;
        Assert.True((await c.GotoAsync(10, 64, 8)).Ok);
        s.Send(new MobSpawnPacket { EntityId = 9, Type = (byte)MobTypes.Zombie, X = 9 * 32, Y = 64 * 32, Z = 6 * 32 });
        Assert.True(await Harness.WaitAsync(() =>
        {
            var p = c.World.Player.Position;
            return Safety.EdgeDistance(c.World, (int)Math.Floor(p.X), 64, (int)Math.Floor(p.Z), 2) >= 2;
        }, 3000), $"still on the rim at {c.World.Player.Position}");
    }
}
