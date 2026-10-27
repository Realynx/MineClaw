using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Brainstem gestures on the wire (vanilla b1.8.1 packets only): nod/shake as 0x0C/0x0D look sequences that end on the
/// real look, wave as 0x12 animation 1, bob/bow as 0x13 start/stop sneak that always end unsneaked (cancel included), hop as
/// a jump; head overlays ride on a walk without changing its heading; a dig/place never goes out under an overlay.
/// </summary>
public class GestureClientTests
{
    private static void BigFloor(FakeServer s)
    {
        for (int x = 2; x <= 14; x++)
        for (int z = 2; z <= 14; z++)
            s.Blocks[(x, 63, z)] = Blocks.Stone;
        s.SpawnAt = (8.5, 64, 8.5);
    }

    private static (float Yaw, float Pitch)? LookOf(IPacket p) => p switch
    {
        PlayerLookPacket l => (l.Yaw, l.Pitch),
        PlayerPositionLookPacket pl => (pl.Yaw, pl.Pitch),
        _ => null,
    };

    private static List<(float Yaw, float Pitch)> Looks(FakeServer s, int from) =>
        s.Snapshot().Skip(from).Select(LookOf).Where(l => l != null).Select(l => l!.Value).ToList();

    private static List<sbyte> Sneaks(FakeServer s, int from) =>
        s.Snapshot().Skip(from).OfType<EntityActionPacket>().Where(a => a.Action is 1 or 2).Select(a => a.Action).ToList();

    private static async Task<(MinecraftClient C, FakeServer S, int From)> Start()
    {
        var (c, s) = await Harness.StartAsync(BigFloor);
        await Task.Delay(50);
        return (c, s, s.Snapshot().Count);
    }

    private static async Task Finish(MinecraftClient c)
    {
        Assert.True(await Harness.WaitAsync(() => c.CurrentGesture == null));
        await Task.Delay(60); // a few more ticks: the restoring packet
    }

    private static float Wrap(float d)
    {
        d %= 360;
        return d > 180 ? d - 360 : d < -180 ? d + 360 : d;
    }

    [Fact]
    public async Task Nod_dips_the_pitch_twice_and_the_head_comes_back()
    {
        var (c, s, from) = await Start();
        await using var _ = c;
        using var __ = s;
        Assert.True(c.PlayGesture(GestureMoves.Nod()));
        await Finish(c);
        var looks = Looks(s, from);
        Assert.Equal(2, looks.Count(l => l.Pitch >= 24.9f)); // two cycles, each well over the 11.25° tracker threshold
        Assert.All(looks, l => Assert.Equal(0, l.Yaw));
        Assert.Equal((0f, 0f), looks[^1]); // restored to the previous look
        Assert.Equal(0, c.World.Player.Pitch); // physics never saw the nod
        Assert.Empty(Sneaks(s, from));
    }

    [Fact]
    public async Task Shake_swings_the_yaw_both_ways_and_comes_back()
    {
        var (c, s, from) = await Start();
        await using var _ = c;
        using var __ = s;
        Assert.True(c.PlayGesture(GestureMoves.Shake()));
        await Finish(c);
        var looks = Looks(s, from);
        Assert.Equal(30, looks.Max(l => l.Yaw), 1);
        Assert.Equal(-30, looks.Min(l => l.Yaw), 1);
        Assert.All(looks, l => Assert.Equal(0, l.Pitch));
        Assert.Equal((0f, 0f), looks[^1]);
    }

    [Fact]
    public async Task Wave_is_three_arm_swings()
    {
        var (c, s, from) = await Start();
        await using var _ = c;
        using var __ = s;
        Assert.True(c.PlayGesture(GestureMoves.Wave()));
        await Finish(c);
        var swings = s.Snapshot().Skip(from).OfType<AnimationPacket>().ToList();
        Assert.Equal(3, swings.Count);
        Assert.All(swings, a => Assert.Equal(1, a.Animation));
    }

    [Fact]
    public async Task Crouch_bob_pulses_sneak_and_ends_unsneaked()
    {
        var (c, s, from) = await Start();
        await using var _ = c;
        using var __ = s;
        Assert.True(c.PlayGesture(GestureMoves.CrouchBob()));
        await Finish(c);
        Assert.Equal(new sbyte[] { 1, 2, 1, 2, 1, 2 }, Sneaks(s, from));
        Assert.False(c.World.Player.Physics.Sneaking);
    }

    [Fact]
    public async Task Cancelling_mid_sneak_still_ends_unsneaked()
    {
        var (c, s, from) = await Start();
        await using var _ = c;
        using var __ = s;
        Assert.True(c.PlayGesture(GestureMoves.CrouchBob(5)));
        Assert.True(await Harness.WaitAsync(() => Sneaks(s, from).Contains(1)));
        c.CancelGesture();
        await Finish(c);
        var sneaks = Sneaks(s, from);
        Assert.Equal(2, sneaks[^1]);
        Assert.True(sneaks.Count < 10); // cut short
        Assert.False(c.World.Player.Physics.Sneaking);
    }

    [Fact]
    public async Task Bow_dips_with_one_sneak_pulse_and_stands_back_up()
    {
        var (c, s, from) = await Start();
        await using var _ = c;
        using var __ = s;
        Assert.True(c.PlayGesture(GestureMoves.Bow()));
        await Finish(c);
        Assert.Equal(40, Looks(s, from).Max(l => l.Pitch), 1);
        Assert.Equal(new sbyte[] { 1, 2 }, Sneaks(s, from));
        Assert.Equal((0f, 0f), Looks(s, from)[^1]);
    }

    [Fact]
    public async Task Hop_jumps_once_and_lands()
    {
        var (c, s, _) = await Start();
        await using var _c = c;
        using var __ = s;
        double peak = 0;
        Assert.True(c.PlayGesture(GestureMoves.Hop()));
        while (c.CurrentGesture != null) { peak = Math.Max(peak, c.World.Player.Position.Y); await Task.Delay(2); }
        await Task.Delay(60);
        Assert.True(peak > 64.5, $"peak {peak}");
        Assert.True(c.World.Player.OnGround);
        Assert.Equal(64, c.World.Player.Position.Y, 3);
        Assert.Equal(0, c.PositionCorrections);
    }

    [Fact]
    public async Task A_walk_refuses_body_gestures_but_carries_a_head_shake_without_changing_its_heading()
    {
        var (c, s, _) = await Start();
        await using var _c = c;
        using var __ = s;
        var start = c.World.Player.Position;
        var walk = c.GotoAsync(12, 64, 8);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Position.DistanceTo(start) > 0.6));
        Assert.False(c.PlayGesture(GestureMoves.CrouchBob())); // sneak/hop need an idle body
        Assert.False(c.PlayGesture(GestureMoves.Hop()));
        int from = s.Snapshot().Count;
        Assert.True(c.PlayGesture(GestureMoves.Shake(4)));
        var r = await walk;
        Assert.True(r.Ok, r.ToString());
        var p = c.World.Player.Position;
        Assert.Equal(12, (int)Math.Floor(p.X));
        Assert.Equal(8, (int)Math.Floor(p.Z));
        Assert.Equal(0, c.PositionCorrections);
        // While moving (0x0D), the sent yaw swung by the shake: the server/other players saw the head go.
        var moving = s.Snapshot().Skip(from).OfType<PlayerPositionLookPacket>().Select(x => x.Yaw).ToList();
        Assert.NotEmpty(moving);
        Assert.True(moving.Max() - moving.Min() >= 25, string.Join(",", moving));
    }

    [Fact]
    public async Task A_placement_never_goes_out_under_a_gesture_look()
    {
        var (c, s) = await Harness.StartAsync(srv => { BigFloor(srv); srv.Inventory[38] = Harness.Stack(Blocks.Cobblestone, 10); });
        await using var _ = c;
        using var __ = s;
        await Task.Delay(50);
        int from = s.Snapshot().Count;
        Assert.True(c.PlayGesture(GestureMoves.LookAt(90, -30, ticks: 60))); // looking west and up; the block goes east
        Assert.True(await Harness.WaitAsync(() => Looks(s, from).Any(l => Math.Abs(l.Yaw - 90) < 1)));
        var r = await c.PlaceBlockAsync(10, 64, 8, Blocks.Cobblestone);
        Assert.True(r.Ok, r.ToString());
        var all = s.Snapshot();
        int place = all.FindIndex(x => x is PlayerBlockPlacementPacket);
        var before = all.Take(place).Select(LookOf).Last(l => l != null)!.Value;
        Assert.True(Math.Abs(Wrap(before.Yaw - 90)) > 45, $"placed under the gesture look {before}");
        Assert.Null(c.CurrentGesture); // the use cancelled it
    }

    [Fact]
    public async Task Merged_wave_and_look_turns_to_the_target_swings_and_returns()
    {
        var (c, s, from) = await Start();
        await using var _ = c;
        using var __ = s;
        var plan = GestureMoves.Merge(GestureMoves.LookAt(-120, 10), GestureMoves.Wave());
        Assert.False(plan.UsesBody);
        Assert.True(c.PlayGesture(plan));
        await Finish(c);
        var looks = Looks(s, from);
        Assert.Contains(looks, l => Math.Abs(l.Yaw + 120) < 0.5 && Math.Abs(l.Pitch - 10) < 0.5);
        Assert.Equal(3, s.Snapshot().Skip(from).OfType<AnimationPacket>().Count());
        Assert.Equal((0f, 0f), looks[^1]);
    }
}
