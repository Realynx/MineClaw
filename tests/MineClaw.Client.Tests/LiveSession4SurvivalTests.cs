using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 2026-10-27. 15:48: kiwi dropped bread for clone 67; main ("I'll pass it to 67") picked it up and the survival reflex ate
/// it on the next tick (food 10 → "Oops, I picked up the bread and auto-ate it"). 15:51: a server-side move went unexplained
/// ("I somehow ended up back at spawn").
/// </summary>
public class LiveSession4SurvivalTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 27, 15, 48, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Bread_picked_up_for_67_is_not_auto_eaten_at_hp_10_food_10()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 7));
        await using var _ = c;
        using var __ = s;
        s.SetVitals(10, 10); // main's vitals at 15:48:06
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Food == 10));
        c.Handoffs.Open("maid_chan_67", Items.Bread); // "Give the bread to me and I'll pass it to 67"
        s.SpawnDrop(8.5, 64.1, 8.5, Harness.Stack(Items.Bread, 1)); // "breads on the floor" (at my feet)
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.CountItem(Items.Bread) == 1, 4000), "picked up");
        await Task.Delay(400); // many survival checks
        Assert.Empty(s.Eaten);
        Assert.Contains("Bread x1 for maid_chan_67", c.Handoffs.Describe());
    }

    [Fact]
    public async Task Without_a_hand_off_the_same_pickup_is_eaten_as_before()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 7));
        await using var _ = c;
        using var __ = s;
        s.SetVitals(10, 10);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Food == 10));
        s.SpawnDrop(8.5, 64.1, 8.5, Harness.Stack(Items.Bread, 1));
        Assert.True(await Harness.WaitAsync(() => s.Eaten.Contains(Items.Bread), 4000), "control: hungry bot eats bread");
    }

    [Fact]
    public async Task Held_bread_is_eaten_anyway_at_critical_hp()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 7));
        await using var _ = c;
        using var __ = s;
        s.SetVitals(10, 10);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Food == 10));
        c.Handoffs.Open("maid_chan_67", Items.Bread);
        s.SpawnDrop(8.5, 64.1, 8.5, Harness.Stack(Items.Bread, 1));
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.CountItem(Items.Bread) == 1, 4000));
        await Task.Delay(200);
        Assert.Empty(s.Eaten);
        s.SetVitals(4, 10); // hp ≤ 4: staying alive comes first
        Assert.True(await Harness.WaitAsync(() => s.Eaten.Contains(Items.Bread), 4000), "critical hp eats held food");
    }

    [Fact]
    public void Holds_end_on_delivery_or_timeout_and_only_catch_the_named_item()
    {
        var clock = new Clock();
        var h = new HandoffReservations(clock);
        int bread = 1;
        h.Open("maid_chan_67", Items.Bread);
        Assert.False(h.OnPickup(Blocks.Sapling, 3)); // not the named item
        Assert.True(h.OnPickup(Items.Bread, 1));
        Assert.Contains(Items.Bread, h.Blocked(10, _ => bread));
        Assert.Empty(h.Blocked(HandoffReservations.CriticalHp, _ => bread));
        bread = 3; // more bread of my own: the extra may be eaten
        Assert.DoesNotContain(Items.Bread, h.Blocked(10, _ => bread));
        bread = 0; // tossed to 67
        Assert.Contains("Bread for maid_chan_67 handed over", h.Reconcile(_ => bread));
        Assert.Empty(h.Describe());

        h.Reserve(Items.Bread, 1, "maid_chan_67"); // a give-to-clone job
        bread = 1;
        clock.Now += h.HoldFor + TimeSpan.FromSeconds(1);
        Assert.Contains(h.Reconcile(_ => bread), l => l.Contains("timeout"));
        Assert.DoesNotContain(Items.Bread, h.Blocked(10, _ => bread));

        clock.Now += h.IntentFor + TimeSpan.FromSeconds(1); // the intent expired too: later pickups are mine
        Assert.False(h.OnPickup(Items.Bread, 1));
    }

    [Fact]
    public async Task A_far_server_teleport_raises_ServerMoved_but_the_login_position_does_not()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var moves = new List<(Vec3d From, Vec3d To)>();
        c.ServerMoved += (a, b) => { lock (moves) moves.Add((a, b)); };
        await Task.Delay(100);
        Assert.Empty(moves); // the login 0x0D answers AwaitingTeleport
        s.Blocks[(-39, 64, 6)] = Blocks.Stone;
        s.Send(new PlayerPositionLookPacket { X = -38.5, Y = 65, Stance = 66.62, Z = 6.5, OnGround = true });
        Assert.True(await Harness.WaitAsync(() => { lock (moves) return moves.Count == 1; }, 3000));
        Assert.Equal(8, (int)Math.Floor(moves[0].From.X));
        Assert.Equal(-39, (int)Math.Floor(moves[0].To.X));
        // A short correction (rubber-band) is not a server move.
        s.Send(new PlayerPositionLookPacket { X = -37.5, Y = 65, Stance = 66.62, Z = 6.5, OnGround = true });
        await Task.Delay(150);
        Assert.Single(moves);
    }
}
