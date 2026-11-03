using System.Text.Json;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public class DeathRecoveryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<(MinecraftClient C, FakeServer S)> Start(string name, double x)
    {
        var s = new FakeServer();
        for (int xx = 0; xx <= 25; xx++) for (int z = 0; z <= 16; z++) s.Blocks[(xx, 63, z)] = Blocks.Stone;
        s.SpawnAt = (x, 64, 8.5);
        var c = new MinecraftClient(new ClientOptions
        {
            Username = name, ConnectionFactory = s.CreateConnection, TickInterval = TimeSpan.FromMilliseconds(5),
            HostileAlertRadius = 0, AutoReconnect = false, RespawnDelay = TimeSpan.FromHours(1),
        });
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await c.ConnectAsync(ct.Token);
        Assert.True(await Until(() => c.IsPlaying && c.World.Player.OnGround));
        return (c, s);
    }

    private static async Task<bool> Until(Func<bool> condition, int ms = 6000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(10); }
        return condition();
    }

    [Fact]
    public async Task Death_sends_an_idle_body_to_collect_drops_without_a_brain_call()
    {
        var (dead, deadS) = await Start("maid_chan", 12.5);
        var (helper, helperS) = await Start("maid_chan_103", 5.5);
        await using var _d = dead;
        await using var _r = helper;
        using var _ds = deadS;
        using var _rs = helperS;
        var (far, farS) = await Start("maid_chan_104", 2.5);
        await using var _f = far;
        using var _fs = farS;
        using var h = BotHost.Create(new MinecraftGameClient(dead), TestBot.Options(), null, new NoLlm(), persist: false);
        var farBody = BodyFactory.CreateClone(h.Brain!, far);
        h.Bodies!.Add(farBody); // first in registry, but not the nearest
        var body = BodyFactory.CreateClone(h.Brain!, helper);
        h.Bodies.Add(body);
        var (busy, busyS) = await Start("maid_chan_105", 11.5);
        await using var _b = busy;
        using var _bs = busyS;
        var busyBody = BodyFactory.CreateClone(h.Brain!, busy);
        h.Bodies.Add(busyBody);
        var input = JsonSerializer.SerializeToElement(new { });
        var work = new DelegateTool("busy_probe", "An existing player's job", input, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ToolResult(true, "done");
        }, longRunning: true);
        await busyBody.Activities.RunAsync([(work, input)], TimeSpan.Zero, CancellationToken.None);
        Assert.True(busyBody.Activities.IsBusy);
        try
        {
            helperS.SpawnDrop(12.5, 64.1, 8.5, new ItemStack(Items.Seeds, 7, 0));
            deadS.Send(new UpdateHealthPacket { Health = 0, Food = 20, FoodSaturation = 0 });
            Assert.True(await Until(() => helper.World.Inventory.CountItem(Items.Seeds) == 7),
                $"no automatic recovery: helper at {helper.World.Player.Position}, action {helper.CurrentAction}");
            Assert.True(helper.World.Player.Position.X > 10);
            Assert.True(far.World.Player.Position.X < 3, "selected registry order instead of nearest idle body");
            Assert.True(busyBody.Activities.IsBusy && busy.World.Player.Position.X > 11, "interrupted an existing job on a closer body");
            Assert.True(await Until(() => h.Runtime.Events.Snapshot().Any(e => e.Text.Contains("recovery") && e.Text.Contains("picked up"))),
                "recovery never reported its confirmed pickup");
        }
        finally { BodyFactory.DisposeClone(body); BodyFactory.DisposeClone(farBody); BodyFactory.DisposeClone(busyBody); }
    }

    [Fact]
    public async Task A_clone_death_can_be_recovered_by_the_idle_main_body()
    {
        var (main, mainS) = await Start("maid_chan", 5.5);
        var (clone, cloneS) = await Start("maid_chan_103", 12.5);
        await using var _m = main;
        await using var _c = clone;
        using var _ms = mainS;
        using var _cs = cloneS;
        using var h = BotHost.Create(new MinecraftGameClient(main), TestBot.Options(), null, new NoLlm(), persist: false);
        var body = BodyFactory.CreateClone(h.Brain!, clone);
        h.Bodies!.Add(body);
        try
        {
            mainS.SpawnDrop(12.5, 64.1, 8.5, new ItemStack(Items.Seeds, 7, 0));
            cloneS.Send(new UpdateHealthPacket { Health = 0, Food = 20, FoodSaturation = 0 });
            Assert.True(await Until(() => main.World.Inventory.CountItem(Items.Seeds) == 7), "clone death was not watched");
        }
        finally { BodyFactory.DisposeClone(body); }
    }

    [Fact]
    public async Task Respawned_body_recovers_its_own_drops_if_no_helper_is_available()
    {
        var (c, s) = await Start("maid_chan", 20.5);
        await using var _c = c;
        using var _s = s;
        using var h = BotHost.Create(new MinecraftGameClient(c), TestBot.Options(), null, new NoLlm(), persist: false);
        using var lane = h.Brain!.Main.ScriptBridge!.Controller;
        var (script, error) = lane.Start(new MineClaw.Scripting.ScriptRequest
        {
            Name = "wait_after_death", Code = "wait(60000);", Language = MineClaw.Scripting.ScriptLanguage.Js, Unattended = true,
        });
        Assert.Null(error);
        Assert.NotNull(script);
        Assert.True(await Until(() => script.Status == MineClaw.Scripting.ScriptStatus.Running));
        s.Send(new UpdateHealthPacket { Health = 0, Food = 20, FoodSaturation = 0 });
        Assert.True(await Until(() => c.World.Player.IsDead && script.Status == MineClaw.Scripting.ScriptStatus.Paused),
            $"death pause missing: dead={c.World.Player.IsDead}, script={script.Status}, why={script.PauseReason}; "
            + string.Join(" | ", h.Runtime.Events.Snapshot().Select(e => e.Text)));
        await Task.Delay(100); // the death event captures the old position before the respawn
        s.Send(new RespawnPacket { Dimension = 0, Difficulty = 1, GameMode = 0, WorldHeight = 128 });
        s.Send(new UpdateHealthPacket { Health = 20, Food = 20, FoodSaturation = 5 });
        s.SendChunk(0, 0);
        s.Send(new PlayerPositionLookPacket { X = 5.5, Y = 64, Stance = 65.62, Z = 8.5, OnGround = true });
        s.SpawnDrop(20.5, 64.1, 8.5, new ItemStack(Items.Seeds, 7, 0));
        Assert.True(await Until(() => c.World.Player.Position.X > 7), "did not approach the unloaded death chunk after respawn");
        s.SendChunk(1, 0); // walking towards the corpse brings its chunk back into view
        Assert.True(await Until(() => c.World.Inventory.CountItem(Items.Seeds) == 7),
            $"respawn did not recover drops: {c.DescribeStatus()}; loaded={c.World.IsLoaded(12, 8)}; " + string.Join(" | ", h.Runtime.Events.Snapshot().Select(e => e.Text)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unsafe_recovery_waits_then_expires_without_moving_a_body(bool lava)
    {
        var (dead, deadS) = await Start("maid_chan", 12.5);
        var (helper, helperS) = await Start("maid_chan_103", 5.5);
        await using var _d = dead;
        await using var _r = helper;
        using var _ds = deadS;
        using var _rs = helperS;
        var clock = new Clock();
        using var h = BotHost.Create(new MinecraftGameClient(dead), TestBot.Options(), null, new NoLlm(), time: clock, persist: false);
        var body = BodyFactory.CreateClone(h.Brain!, helper);
        h.Bodies!.Add(body);
        try
        {
            // Lava never; a four-block water column only at full hp.
            if (lava)
            {
                helperS.SetBlock(12, 64, 8, Blocks.Lava);
                deadS.SetBlock(12, 64, 8, Blocks.Lava);
            }
            else
            {
                for (int y = 64; y <= 67; y++) { helperS.SetBlock(12, y, 8, Blocks.Water); deadS.SetBlock(12, y, 8, Blocks.Water); }
                helperS.Send(new UpdateHealthPacket { Health = 18, Food = 20, FoodSaturation = 5 });
            }
            Assert.True(await Until(() => helper.World.GetBlock(12, 64, 8) == (lava ? Blocks.Lava : Blocks.Water)
                && (lava || helper.World.Player.Health == 18)));
            var before = helper.World.Player.Position;
            deadS.Send(new UpdateHealthPacket { Health = 0, Food = 20, FoodSaturation = 0 });
            Assert.True(await Until(() => dead.World.Player.IsDead));
            await Task.Delay(700);
            Assert.True(helper.World.Player.Position.DistanceTo(before) < 0.5);
            if (!lava)
            {
                helperS.SpawnDrop(12.5, 64.1, 8.5, new ItemStack(Items.Seeds, 7, 0));
                helperS.Send(new UpdateHealthPacket { Health = 20, Food = 20, FoodSaturation = 5 });
                Assert.True(await Until(() => helper.World.Inventory.CountItem(Items.Seeds) == 7), "full hp never enabled safe water-side recovery");
                return;
            }
            clock.Now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
            Assert.True(await Until(() => h.Runtime.Events.Snapshot().Any(e => e.Text.Contains("recovery") && e.Text.Contains("expired"))),
                "no clean expiry report");
            helperS.SetBlock(12, 64, 8, 0);
            await Task.Delay(700);
            Assert.True(helper.World.Player.Position.DistanceTo(before) < 0.5);
        }
        finally { BodyFactory.DisposeClone(body); }
    }
}
