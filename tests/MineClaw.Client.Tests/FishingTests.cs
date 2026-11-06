using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// The fish primitive (docs/web/story-mode.md "Fishing"): cast, find our bobber (object 90 with our entity id as thrower/owner),
/// a bite (a downward velocity / a drop after it floats), reel in, the catch. Against the fake server on both wires.
/// </summary>
public class FishingTests
{
    public static TheoryData<bool> Versions => new() { false, true };

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Casts_reels_in_on_the_bite_and_counts_the_catch_on_either_version(bool release1122)
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[36] = Harness.Stack(MinecraftClient.FishingRodId, 1);
            srv.Fishing = true;
            srv.BiteAfterMs = 900;
        }, release1122: release1122);
        await using var _ = c;
        using var __ = s;
        var r = await c.FishAsync(new Vec3d(11.5, 63, 8.5), TimeSpan.FromSeconds(6), maxCatches: 2);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, s.FishLanded);
        Assert.Equal(2, c.World.Inventory.CountItem(MinecraftClient.RawFishId));
        Assert.Contains("2 fish caught", r.Message);
    }

    [Fact]
    public async Task No_rod_no_fishing()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Fishing = true);
        await using var _ = c;
        using var __ = s;
        var r = await c.FishAsync(new Vec3d(11.5, 63, 8.5), TimeSpan.FromSeconds(3));
        Assert.False(r.Ok);
        Assert.Contains("no fishing rod", r.Message);
        Assert.Equal(0, s.Casts);
    }

    [Fact]
    public async Task Modern_cast_waits_for_its_bite_sound_not_a_delayed_drop_or_another_anglers_splash()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[36] = Harness.Stack(MinecraftClient.FishingRodId, 1);
            srv.Fishing = true;
            srv.BiteAfterMs = 2200;
        }, release1122: true);
        await using var _ = c;
        using var __ = s;
        var fishing = c.FishAsync(new Vec3d(11.5, 63, 8.5), TimeSpan.FromSeconds(5), maxCatches: 1);
        await WaitFor(() => s.HookId != 0);
        int hook = s.HookId;
        await Task.Delay(750); // old detector has incorrectly settled on the stationary spawn position
        s.Send(new EntityRelativeMovePacket { EntityId = hook, DY = -10 });
        s.Send(new SoundMessage { Sound = SoundMessage.BobberSplash, X = 15.5, Y = 63, Z = 8.5 });
        await Task.Delay(500);
        Assert.Equal(hook, s.HookId); // no premature reel or second cast
        Assert.Equal(1, s.Casts);
        Assert.Equal(0, s.FishLanded);
        var result = await fishing;
        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, s.FishLanded); // server sends only sound for the bite, no synthetic velocity
        Assert.Equal(1, s.Casts);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Cancelled_session_reels_back_its_line_and_next_session_casts_once(bool release1122)
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[36] = Harness.Stack(MinecraftClient.FishingRodId, 1);
            srv.Fishing = true;
            srv.BiteAfterMs = 10_000;
        }, release1122: release1122);
        await using var _ = c;
        using var __ = s;
        using var stop = new CancellationTokenSource();
        var fishing = c.FishAsync(new Vec3d(11.5, 63, 8.5), TimeSpan.FromSeconds(20), ct: stop.Token);
        await WaitFor(() => s.HookId != 0 && c.World.Entities.Get(s.HookId) != null);
        stop.Cancel();
        try { await fishing; } catch (OperationCanceledException) { }
        await WaitFor(() => s.HookId == 0);
        Assert.Equal(1, s.Casts);
        s.BiteAfterMs = 900;
        var result = await c.FishAsync(new Vec3d(11.5, 63, 8.5), TimeSpan.FromSeconds(4), maxCatches: 1);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, s.Casts);
        Assert.Equal(1, s.FishLanded);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public void A_bite_is_a_fresh_downward_velocity_or_a_drop_once_the_bobber_floats()
    {
        // The tracker as the dispatch feeds it: our hook spawns (thrower = our id), flies, lands, floats, bites.
        var t = new EntityTracker();
        const int me = 42, hook = 900;
        t.SpawnObject(hook, 90, new Vec3d(10, 66, 8), new Vec3d(0.4, -0.3, 0), data: me);
        var d = new BiteDetector();
        bool Tick() { var e = t.Get(hook)!; return d.Sample(e.Position.Y, e.Velocity.Y); }
        Assert.Equal(me, t.Get(hook)!.Data);
        // In flight: falling fast, but not a bite (it hasn't floated yet).
        for (int i = 0; i < 4; i++) { t.MoveRelative(hook, 0.4, -0.5, 0); Assert.False(Tick()); }
        // Floating: the cast's velocity is still what we hold (no packet while it floats still): never a bite.
        for (int i = 0; i < 20; i++) Assert.False(Tick());
        Assert.NotNull(d.FloatY);
        // b1.8.1: motionY -= 0.2 on a bite (velocity tracked).
        t.SetVelocity(hook, new Vec3d(0, -0.2, 0));
        Assert.True(Tick());

        // 1.12.2: small bobs after it floats, then motionY = -0.4 · rand(0.6, 1), and a drop.
        var t2 = new EntityTracker();
        t2.SpawnObject(hook, 90, new Vec3d(10, 63.1, 8), default, data: me);
        var d2 = new BiteDetector();
        bool Tick2() { var e = t2.Get(hook)!; return d2.Sample(e.Position.Y, e.Velocity.Y); }
        for (int i = 0; i < 12; i++) Assert.False(Tick2());
        t2.SetVelocity(hook, new Vec3d(0, 0.04, 0));
        Assert.False(Tick2());
        t2.SetVelocity(hook, new Vec3d(0, -0.04, 0));
        Assert.False(Tick2());
        t2.MoveRelative(hook, 0, -0.3, 0);
        Assert.True(Tick2()); // the drop alone (a missed velocity update)
    }
}
