using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Gap jumps through the real client (tick loop, EdgeGuard exemption, 0x13 sprint actions, food gating,
/// void-parkour authorization) against the fake server's 5×5 platform (x,z 6..10, top 64).</summary>
public class ParkourTests
{
    /// <summary>The regulars whose chat may ask for void parkour, as a deployment configures them (the code default trusts nobody).</summary>
    private static ClientOptions Trusted(ClientOptions o) => o with { ParkourTrustedPlayers = ["poofimafox", "kiwi"] };

    /// <summary>A landing platform x = 11+gap..15+gap, z 6..10 (top 64); optionally a yard 2 below everything.</summary>
    private static Action<FakeServer> Gap(int gap, bool yard, Action<FakeServer>? more = null) => srv =>
    {
        for (int x = 11 + gap; x <= 15 + gap; x++)
        for (int z = 6; z <= 10; z++)
            srv.Blocks[(x, 63, z)] = Blocks.Stone;
        if (yard)
            for (int x = 2; x <= 24; x++)
            for (int z = 2; z <= 14; z++)
                srv.Blocks[(x, 61, z)] = Blocks.Stone;
        more?.Invoke(srv);
    };

    private static async Task<bool> NeverOverVoidAsync(MinecraftClient c, Task until)
    {
        bool over = false;
        while (!until.IsCompleted)
        {
            over |= c.World.IsPlayerOverVoid(c.World.Player.Position) && !c.World.Player.OnGround;
            await Task.Delay(1);
        }
        return !over;
    }

    [Fact]
    public async Task SprintJump_OverAYardGap_ArrivesAndSaysSo()
    {
        var (c, s) = await Harness.StartAsync(Gap(3, yard: true), Trusted);
        await using var _c = c;
        using var _s = s;
        var r = await c.GotoAsync(16, 64, 8);
        Assert.True(r.Ok, r.ToString());
        Assert.Matches(@"arrived at .* \(1 sprint-jump \d\.\d blocks\)", r.Message);
        Assert.Equal(64, c.World.Player.Position.Y, 2);
        Assert.True(c.World.Player.Position.X > 14);
        var actions = s.Got<EntityActionPacket>().Select(a => (int)a.Action).ToList();
        int start = actions.IndexOf(4);
        Assert.True(start >= 0, "no 0x13 start-sprinting");
        Assert.Contains(5, actions.Skip(start)); // and stopped again
        Assert.True(c.SafetyStats.ParkourExemptTicks > 0);
        Assert.Equal(0, c.SafetyStats.JumpsCancelled);
    }

    [Fact]
    public async Task TooHungry_NoSprint_LongGapRefused_StaysPut()
    {
        var (c, s) = await Harness.StartAsync(Gap(3, yard: true), Trusted);
        await using var _c = c;
        using var _s = s;
        s.SetVitals(20, 6);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Food == 6));
        var r = await c.GotoAsync(16, 64, 8, options: GotoOptions.Default with { AllowPartial = false });
        Assert.False(r.Ok, r.ToString());
        Assert.Contains("no path", r.Message);
        Assert.True(c.World.Player.Position.X < 11);
        Assert.DoesNotContain(s.Got<EntityActionPacket>(), a => a.Action == 4);
    }

    [Fact]
    public async Task VoidGap_RefusedByDefault()
    {
        var (c, s) = await Harness.StartAsync(Gap(2, yard: false, srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 16)), Trusted);
        await using var _c = c;
        using var _s = s;
        var r = await c.GotoAsync(15, 64, 8);
        Assert.False(r.Ok, r.ToString());
        // Asked for, but nobody trusted spoke: still refused.
        var asked = await c.GotoAsync(15, 64, 8, options: GotoOptions.Default with { Parkour = ParkourMode.Void });
        Assert.False(asked.Ok, asked.ToString());
        Assert.True(c.World.Player.Position.X < 11);
    }

    [Fact]
    public async Task VoidGap_AfterATrustedRequest_WithAClutchBlock_IsJumped()
    {
        var (c, s) = await Harness.StartAsync(Gap(2, yard: false, srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 16)), Trusted);
        await using var _c = c;
        using var _s = s;
        s.Send(new ChatPacket("<kiwi> maid-chan jump over to the other island!"));
        await Task.Delay(50);
        var go = c.GotoAsync(15, 64, 8, options: GotoOptions.Default with { Parkour = ParkourMode.Void });
        bool safe = await NeverOverVoidAsync(c, go);
        var r = await go;
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("over the void", r.Message);
        Assert.Matches(@"\((1|2) (sprint|gap)-jump", r.Message);
        Assert.True(c.World.Player.Position.X > 12);
        Assert.Empty(s.Got<PlayerBlockPlacementPacket>()); // no clutch was needed
        _ = safe; // airborne over the void during the jump is expected; it must end on the far island
        Assert.False(c.World.IsPlayerOverVoid(c.World.Player.Position));
    }

    [Fact]
    public async Task VoidGap_TrustedRequestButNoClutchBlock_Refused()
    {
        var (c, s) = await Harness.StartAsync(Gap(2, yard: false), Trusted);
        await using var _c = c;
        using var _s = s;
        s.Send(new ChatPacket("<poofimafox> jump!"));
        await Task.Delay(50);
        var r = await c.GotoAsync(15, 64, 8, options: GotoOptions.Default with { Parkour = ParkourMode.Void });
        Assert.False(r.Ok, r.ToString());
        Assert.True(c.World.Player.Position.X < 11);
    }

    [Fact]
    public async Task ParkourOff_NeverJumps()
    {
        var (c, s) = await Harness.StartAsync(Gap(3, yard: true), Trusted);
        await using var _c = c;
        using var _s = s;
        var r = await c.GotoAsync(16, 64, 8, options: GotoOptions.Default with { Parkour = ParkourMode.Off, AllowPartial = false });
        Assert.False(r.Ok, r.ToString());
        Assert.True(c.World.Player.Position.X < 11);
    }

    [Fact]
    public async Task LandingMinedDuringTheRunUp_AbortsAtTheEdge()
    {
        var (c, s) = await Harness.StartAsync(Gap(2, yard: false, srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 16)), Trusted);
        await using var _c = c;
        using var _s = s;
        s.Send(new ChatPacket("<poofimafox> parkour time"));
        await Task.Delay(50);
        bool fired = false;
        s.Hook = p =>
        {
            // The first start-sprinting packet = the run-up began: the landing edge disappears.
            if (!fired && p is EntityActionPacket { Action: 4 })
            {
                fired = true;
                for (int z = 6; z <= 10; z++)
                for (int x = 13; x <= 14; x++)
                    s.SetBlock(x, 63, z, 0);
            }
            return false;
        };
        var r = await c.GotoAsync(15, 64, 8, options: GotoOptions.Default with { Parkour = ParkourMode.Void, Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(fired, "the run-up never started");
        Assert.False(r.Ok, r.ToString());
        await Task.Delay(100);
        var p = c.World.Player.Position;
        Assert.True(c.World.Player.OnGround, $"at {p}");
        Assert.True(p.X < 11.3, $"ran off the platform: {p} ({r})");
        Assert.Equal(64, p.Y, 2);
    }

    [Fact]
    public async Task StopMidFlight_StillFinishesTheJump()
    {
        // A 3-gap: letting go of the keys mid-air (momentum only) would fall short into the void.
        var (c, s) = await Harness.StartAsync(Gap(3, yard: false, srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 16)), Trusted);
        await using var _c = c;
        using var _s = s;
        s.Send(new ChatPacket("<poofimafox> go on, jump"));
        await Task.Delay(50);
        var go = c.GotoAsync(16, 64, 8, options: GotoOptions.Default with { Parkour = ParkourMode.Void });
        Assert.True(await Harness.WaitAsync(() => !c.World.Player.OnGround && c.World.Player.Position.X > 11.0, 5000), "never took off");
        await c.StopAsync();
        var r = await go;
        Assert.False(r.Ok); // stopped
        Assert.True(await Harness.WaitAsync(() => c.World.Player.OnGround, 2000));
        var p = c.World.Player.Position;
        Assert.True(p.X > 14 && p.Y > 63.9, $"landed at {p}");
        Assert.False(c.World.IsPlayerOverVoid(p));
        Assert.Empty(s.Got<PlayerBlockPlacementPacket>());
        // The planned input carried on (exempt), rather than the guard having to rescue a released jump.
        Assert.Equal(0, c.SafetyStats.AirSteerTicks);
        Assert.Equal(0, c.SafetyStats.ParkourRejections);
    }
}
