using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Item 18 (user design decision 10-27): the one explicit, opt-in way off the island — a generic primitive with guard rails;
/// the void-reset strategy itself is learned from memory, never coded. Platform: stone 6..10 × 6..10 at y 63, void around.
/// </summary>
public class VoidStepTests
{
    [Fact]
    public async Task Without_the_explicit_step_every_walk_keeps_full_void_protection()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var r = await c.GotoAsync(new GoalBlock(13, 64, 8), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(3) });
        Assert.False(c.World.IsPlayerOverVoid(c.World.Player.Position), $"walked over the void: {r}");
        Assert.True(c.World.Player.Position.Y >= 63.9);
        Assert.Null(c.ActiveVoidStep);
    }

    [Fact]
    public async Task Holding_items_is_refused_unless_explicitly_ok()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 13));
        await using var _ = c;
        using var __ = s;
        var r = await c.StepIntoVoidAsync("reset hp and hunger");
        Assert.False(r.Ok);
        Assert.StartsWith("deposit first: holding ", r.Message);
        Assert.Contains("x13", r.Message);
        Assert.True(c.World.Player.Position.Y >= 63.9);
        Assert.False((await c.StepIntoVoidAsync("  ")).Ok); // a reason is required
    }

    [Fact]
    public async Task Empty_inventory_steps_off_dies_quietly_and_reports_the_respawn()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var dangers = new ConcurrentQueue<DangerEvent>();
        var started = new ConcurrentQueue<IntentionalVoidStep>();
        var done = new ConcurrentQueue<IntentionalResetOutcome>();
        c.Danger += dangers.Enqueue;
        c.VoidStepStarted += started.Enqueue;
        c.IntentionalResetDone += done.Enqueue;

        var r = await c.StepIntoVoidAsync("reset hp and hunger after depositing").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("stepped off into the void", r.Message);
        Assert.Single(started);
        Assert.NotNull(c.ActiveVoidStep);
        Assert.True(c.World.Player.Position.Y < 61, $"still at {c.World.Player.Position}");
        Assert.Empty(s.Got<PlayerBlockPlacementPacket>()); // no clutch block

        // The server's void damage kills it; it respawns at spawn with full vitals.
        s.Send(new UpdateHealthPacket { Health = 0, Food = 14, FoodSaturation = 0 });
        Assert.True(s.WaitFor(p => p is RespawnPacket), "auto-respawn must send 0x09");
        s.Send(new RespawnPacket { Dimension = 0, Difficulty = 1, GameMode = 0, WorldHeight = 128, MapSeed = 1234 });
        s.Send(new UpdateHealthPacket { Health = 20, Food = 20, FoodSaturation = 5 });
        s.Send(new PlayerPositionLookPacket { X = 8.5, Y = 64, Stance = 65.62, Z = 8.5, Yaw = 0, Pitch = 0, OnGround = false });
        Assert.True(await Harness.WaitAsync(() => !done.IsEmpty, 5000));
        var o = Assert.Single(done);
        Assert.Equal((20, 20), (o.Health, o.Food));
        Assert.Equal("respawned at 8.5,64,8.5 with hp 20, food 20", o.ToString());
        Assert.Null(c.ActiveVoidStep);
        // The fall, the void and the death were the plan: no danger alarms.
        Assert.DoesNotContain(dangers, d => d.Kind is DangerKind.FallingIntoVoid or DangerKind.LowHealth or DangerKind.Damage);
    }
}
