using System.Diagnostics;
using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class HumanControlTests
{
    private static ClientOptions Quiet(ClientOptions options) => options with
    {
        Survival = new SurvivalOptions { Enabled = false }, Combat = new CombatOptions { DefendSelf = false, GuardEnabled = false, TelemetryDirectory = null },
    };

    private static void Aim(MinecraftClient client, BlockPos block, int? hotbar = null)
    {
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(client.World.Player.EyePosition, block.Center);
        client.SetHumanInput(new MoveInput(Yaw: yaw, Pitch: pitch), hotbar);
    }

    /// <summary>Where a sneaking 1.12.2 player's eye is: 0.08 below the standing one.</summary>
    private static Vec3d SneakingEye(MinecraftClient client)
    {
        var eye = client.World.Player.EyePosition;
        return eye with { Y = eye.Y - Aiming.SneakEyeDrop };
    }

    /// <summary>The sample of a human tick that began after this call, so it has read every input set before it. After an awaited
    /// action it also shows what that action left: the action's last step waits for a running tick to end.</summary>
    private static async Task<HumanControlSample> NextSample(MinecraftClient client)
    {
        var sample = await client.WaitHumanSampleAsync(client.TickCount, TimeSpan.FromSeconds(3));
        Assert.NotNull(sample);
        return sample;
    }

    private static bool Sent(FakeServer server, byte status, BlockPos at) =>
        server.Got<PlayerDiggingPacket>().Any(p => p.Status == status && (p.X, p.Y, p.Z) == (at.X, at.Y, at.Z));

    [Fact]
    public void Lost_input_releases_every_key_but_preserves_look()
    {
        var input = new MoveInput(1, -1, true, true, true, 120, -35);
        Assert.Equal(input, MinecraftClient.HumanInputAt(input, 1000, 1349));
        Assert.Equal(MoveInput.Idle(120, -35), MinecraftClient.HumanInputAt(input, 1000, 1350));
    }

    [Fact]
    public async Task Takeover_cancels_driver_blocks_AI_and_moves_with_vanilla_collisions()
    {
        var (c, s) = await Harness.StartAsync(server =>
        {
            for (int x = 6; x <= 10; x++) { server.Blocks[(x, 64, 10)] = Blocks.Stone; server.Blocks[(x, 65, 10)] = Blocks.Stone; }
        }, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        var walking = c.SpinAsync(5);
        Assert.True(await Harness.WaitAsync(() => c.IsBusy));
        c.BeginHumanControl();
        c.SetHumanInput(new MoveInput(Forward: 1, Yaw: 0));
        Assert.False((await walking).Ok);
        Assert.Contains("human", (await c.GotoAsync(12, 64, 8)).Message);
        Assert.False(c.FaceIdle(new Vec3d(0, 64, 0)));
        Assert.False(c.SwingIdle());
        await Task.Delay(400); // the client deadman, independent of the App's lease timer
        Assert.InRange(c.World.Player.Position.Z, 9.2, 9.701);
        var stopped = c.World.Player.Position;
        await Task.Delay(80);
        Assert.InRange(c.World.Player.Position.DistanceTo(stopped), 0, 0.01);
        Assert.Empty(s.Got<PlayerDiggingPacket>());
        Assert.Empty(s.Got<PlayerBlockPlacementPacket>());
        c.EndHumanControl();
        Assert.False(c.HumanControlled);
    }

    [Fact]
    public void Entity_pick_requires_crosshair_reach_and_clear_line_of_sight()
    {
        using var w = new WorldState();
        w.ApplyChunkRegion(0, 0, 0, 16, 128, 16, new byte[16 * 128 * 16], null, null, null);
        w.Player.EntityId = 1;
        w.Entities.SpawnPlayer(2, "near", new Vec3d(8.5, 64, 10.5), 0, 0, 0);
        w.Entities.SpawnPlayer(3, "far", new Vec3d(8.5, 64, 13), 0, 0, 0);
        var eye = new Vec3d(8.5, 65.62, 8.5);
        Assert.Equal(2, MinecraftClient.HumanPickEntity(w, eye, 0, 0, 3)?.Id);
        Assert.Null(MinecraftClient.HumanPickEntity(w, eye, 90, 0, 3));
        w.SetBlock(8, 65, 9, Blocks.Stone, 0);
        Assert.Null(MinecraftClient.HumanPickEntity(w, eye, 0, 0, 3));
        w.SetBlock(8, 65, 9, 0, 0);
        w.Entities.Destroy(2);
        Assert.Null(MinecraftClient.HumanPickEntity(w, eye, 0, 0, 3));
    }

    [Fact]
    public async Task Primary_attack_digs_aimed_block_without_equipping_another_tool_or_pathing()
    {
        var (c, s) = await Harness.StartAsync(server =>
        {
            server.Blocks[(8, 64, 10)] = Blocks.Dirt;
            server.Inventory[37] = Harness.Stack(Items.DiamondShovel, 1);
        }, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl();
        var before = c.World.Player.Position;
        Aim(c, new BlockPos(8, 64, 10));
        var result = await c.HumanActionAsync("attack");
        Assert.True(result.Ok, result.Message);
        Assert.Equal(0, c.World.GetBlock(8, 64, 10));
        Assert.Equal(0, c.World.Player.HeldSlot);
        Assert.InRange(c.World.Player.Position.DistanceTo(before), 0, 0.01);
        Assert.DoesNotContain(s.Got<HoldingChangePacket>(), packet => packet.Slot == 1);
        c.EndHumanControl();
    }

    [Fact]
    public async Task Looking_away_cancels_long_dig_and_never_sends_finish()
    {
        var (c, s) = await Harness.StartAsync(server => server.Blocks[(8, 64, 10)] = Blocks.Obsidian, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); Aim(c, new BlockPos(8, 64, 10));
        var digging = c.HumanActionAsync("dig", new BlockPos(8, 64, 10));
        Assert.True(await Harness.WaitAsync(() => s.Got<PlayerDiggingPacket>().Any(p => p.Status == PlayerDiggingPacket.StatusStarted)));
        c.SetHumanInput(new MoveInput(Yaw: 180));
        var result = await digging;
        Assert.False(result.Ok);
        Assert.True(await Harness.WaitAsync(() => s.Got<PlayerDiggingPacket>().Any(p => p.Status == PlayerDiggingPacket.StatusCancelled)));
        Assert.DoesNotContain(s.Got<PlayerDiggingPacket>(), p => p.Status == PlayerDiggingPacket.StatusFinished);
        Assert.Equal(Blocks.Obsidian, c.World.GetBlock(8, 64, 10));
        c.EndHumanControl();
    }

    [Fact]
    public async Task Releasing_control_releases_bow_and_old_cleanup_cannot_cancel_new_input()
    {
        var (c, s) = await Harness.StartAsync(server => server.Inventory[36] = Harness.Stack(Items.Bow, 1), Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); c.SetHumanInput(MoveInput.Idle(0, -30));
        var use = c.HumanActionAsync("use");
        Assert.True(await Harness.WaitAsync(() => s.Got<PlayerBlockPlacementPacket>().Any(p => p.Direction == 255)));
        c.EndHumanControl(); c.BeginHumanControl(); c.SetHumanInput(MoveInput.Idle(90, -10));
        await use;
        Assert.True(await Harness.WaitAsync(() => s.Got<PlayerDiggingPacket>().Any(p => p.Status == PlayerDiggingPacket.StatusReleaseUseItem)));
        Assert.True(await Harness.WaitAsync(() => Math.Abs(c.World.Player.Yaw - 90) < 0.1));
        Assert.True(c.HumanControlled);
        c.EndHumanControl();
    }

    [Fact]
    public async Task Inventory_checks_window_id_and_preserves_cursor_when_closed()
    {
        var (c, s) = await Harness.StartAsync(server => server.Inventory[36] = Harness.Stack(Blocks.Dirt, 12), Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl();
        var stale = await c.HumanActionAsync("inventory_click", windowId: 4, slot: 36);
        Assert.False(stale.Ok);
        Assert.Empty(s.Got<WindowClickPacket>());
        var pick = await c.HumanActionAsync("inventory_click", windowId: 0, slot: 36);
        Assert.True(pick.Ok, pick.Message);
        Assert.Equal(12, c.World.Inventory.Cursor.Count);
        var close = await c.HumanActionAsync("close_inventory", windowId: 0);
        Assert.True(close.Ok, close.Message);
        Assert.True(c.World.Inventory.Cursor.IsEmpty);
        Assert.Equal(12, c.World.Inventory.CountItem(Blocks.Dirt));
        c.EndHumanControl();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_brief_right_click_opens_the_chest_even_when_release_precedes_the_window_reply(bool releaseBeforePress)
    {
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (c, s) = await Harness.StartAsync(server =>
        {
            server.Blocks[(8, 64, 10)] = Blocks.Chest;
            server.Hook = packet =>
            {
                if (packet is not PlayerBlockPlacementPacket { X: 8, Y: 64, Z: 10 }) return false;
                clicked.TrySetResult();
                return true; // The test controls the delayed server acknowledgement.
            };
        }, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        using var released = new CancellationTokenSource();
        c.BeginHumanControl(); Aim(c, new BlockPos(8, 64, 10));
        if (releaseBeforePress) released.Cancel();
        var open = c.HumanActionAsync("use", holdRelease: released.Token);
        await clicked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        released.Cancel();
        Assert.False(open.IsCompleted);
        s.Send(new OpenWindowPacket { WindowId = 7, InventoryType = 0, Title = "Chest", SlotCount = 27 });
        var contents = Enumerable.Repeat(ItemStack.Empty, 63).ToArray();
        contents[3] = Harness.Stack(Items.Diamond, 12);
        s.Send(new WindowItemsPacket { WindowId = 7, Items = contents });
        var result = await open;
        Assert.True(result.Ok, result.Message);
        Assert.Equal(7, c.World.Inventory.ActiveWindow.Id);
        Assert.Equal(12, c.World.Inventory.ActiveWindow[3].Count);
        Assert.Single(s.Got<PlayerBlockPlacementPacket>());
        c.EndHumanControl();
    }

    [Fact]
    public async Task An_already_released_bow_click_sends_use_then_release_without_holding_or_losing_the_click()
    {
        var (c, s) = await Harness.StartAsync(server => server.Inventory[36] = Harness.Stack(Items.Bow, 1), Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        using var released = new CancellationTokenSource();
        released.Cancel();
        c.BeginHumanControl(); c.SetHumanInput(MoveInput.Idle(0, -30));
        var result = await c.HumanActionAsync("use", holdRelease: released.Token);
        Assert.True(result.Ok, result.Message);
        Assert.True(await Harness.WaitAsync(() => s.Got<PlayerDiggingPacket>().Any(p => p.Status == PlayerDiggingPacket.StatusReleaseUseItem)));
        var packets = s.Received.ToArray();
        int use = Array.FindIndex(packets, p => p is PlayerBlockPlacementPacket { Direction: 255 });
        int release = Array.FindIndex(packets, p => p is PlayerDiggingPacket { Status: PlayerDiggingPacket.StatusReleaseUseItem });
        Assert.True(use >= 0 && release > use);
        c.EndHumanControl();
    }

    [Fact]
    public async Task A_queued_click_keeps_its_aim_sneak_and_hand_without_replacing_newer_input()
    {
        var (c, s) = await Harness.StartAsync(server =>
        {
            server.Blocks[(8, 64, 10)] = Blocks.Chest;
            server.Chests[(8, 64, 10)] = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
            server.Inventory[37] = Harness.Stack(Blocks.Dirt, 4);
        }, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); Aim(c, new BlockPos(8, 64, 10), 0);
        var click = c.CaptureHumanAim();
        Assert.NotNull(click);
        c.SetHumanInput(new MoveInput(Yaw: 180, Sneak: true), 1); // later heartbeat before the queued action executes
        var result = await c.HumanActionAsync("use", aim: click);
        Assert.True(result.Ok, result.Message);
        Assert.True(c.World.Inventory.ActiveWindow.Id > 0);
        Assert.True(await Harness.WaitAsync(() => s.Got<PlayerBlockPlacementPacket>().Any()));
        var placement = Assert.Single(s.Got<PlayerBlockPlacementPacket>());
        Assert.Equal((8, 64, 10), (placement.X, placement.Y, placement.Z));
        Assert.True(placement.HeldItem.IsEmpty);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.HeldSlot == 1 && Math.Abs(c.World.Player.Yaw - 180) < 0.1));
        Assert.Equal(Blocks.Chest, c.World.GetBlock(8, 64, 10));
        c.EndHumanControl();
    }

    [Fact]
    public async Task A_captured_attack_rechecks_current_line_of_sight_but_keeps_click_time_look()
    {
        var (c, s) = await Harness.StartAsync(opts: Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); c.SetHumanInput(MoveInput.Idle(0, 0));
        c.World.Entities.SpawnPlayer(77, "target", new Vec3d(8.5, 64, 10.5), 0, 0, 0);
        var click = c.CaptureHumanAim();
        c.SetHumanInput(MoveInput.Idle(180, 0));
        s.SetBlock(8, 65, 9, Blocks.Stone);
        Assert.True(await Harness.WaitAsync(() => c.World.GetBlock(8, 65, 9) == Blocks.Stone));
        Assert.False((await c.HumanActionAsync("attack", entityId: 77, aim: click)).Ok);
        Assert.Empty(s.Got<UseEntityPacket>());
        s.SetBlock(8, 65, 9, 0);
        Assert.True(await Harness.WaitAsync(() => c.World.GetBlock(8, 65, 9) == 0));
        Assert.True((await c.HumanActionAsync("attack", entityId: 77, aim: click)).Ok);
        Assert.True(await Harness.WaitAsync(() => s.Got<UseEntityPacket>().Any(p => p.TargetEntityId == 77 && p.LeftClick)));
        c.EndHumanControl();
    }

    [Fact]
    public async Task An_old_captured_click_cannot_act_in_a_new_takeover()
    {
        var (c, s) = await Harness.StartAsync(opts: Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl();
        var old = c.CaptureHumanAim();
        c.EndHumanControl(); c.BeginHumanControl(); c.SetHumanInput(MoveInput.Idle(90, 0));
        Assert.False((await c.HumanActionAsync("use", aim: old)).Ok);
        Assert.Empty(s.Got<PlayerBlockPlacementPacket>());
        Assert.Empty(s.Got<UseEntityPacket>());
        Assert.True(c.HumanControlled);
        c.EndHumanControl();
    }

    [Fact]
    public async Task A_hidden_entity_is_never_attacked_and_the_visible_nearest_entity_is()
    {
        var (c, s) = await Harness.StartAsync(server => server.Blocks[(8, 65, 9)] = Blocks.Stone, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); c.SetHumanInput(MoveInput.Idle(0, 0));
        c.World.Entities.SpawnPlayer(77, "target", new Vec3d(8.5, 64, 10.5), 0, 0, 0);
        var blocked = await c.HumanActionAsync("attack", entityId: 77);
        Assert.False(blocked.Ok);
        Assert.Empty(s.Got<UseEntityPacket>());
        Assert.Empty(s.Got<PlayerDiggingPacket>());
        s.SetBlock(8, 65, 9, 0);
        Assert.True(await Harness.WaitAsync(() => c.World.GetBlock(8, 65, 9) == 0));
        c.SetHumanInput(MoveInput.Idle(0, 0));
        var hit = await c.HumanActionAsync("attack");
        Assert.True(hit.Ok, hit.Message);
        Assert.True(await Harness.WaitAsync(() => s.Got<UseEntityPacket>().Any(p => p.TargetEntityId == 77 && p.LeftClick)));
        c.EndHumanControl();
    }

    [Fact]
    public async Task Sneak_and_orientation_are_sent_before_placing_against_a_chest()
    {
        var (c, s) = await Harness.StartAsync(server =>
        {
            server.Blocks[(8, 64, 10)] = Blocks.Chest;
            server.Inventory[36] = Harness.Stack(53, 3);
            server.OrientBlocks = true;
        }, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl();
        // A sneaking 1.12.2 player aims from the lowered eye, and the click is cast from there.
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(SneakingEye(c), new Vec3d(8.5, 64.75, 10));
        c.SetHumanInput(new MoveInput(Yaw: yaw, Pitch: pitch, Sneak: true), 0);
        var result = await c.HumanActionAsync("use");
        Assert.True(result.Ok, result.Message);
        Assert.True(await Harness.WaitAsync(() => s.Got<PlayerBlockPlacementPacket>().Any(p => p.Direction != 255)));
        var packets = s.Received.ToArray();
        int place = Array.FindIndex(packets, p => p is PlayerBlockPlacementPacket { Direction: not 255 });
        Assert.Contains(packets.Take(place), p => p is EntityActionPacket { Action: 1 });
        var sent = Assert.IsType<PlayerBlockPlacementPacket>(packets[place]);
        Assert.Equal((8, 64, 10), (sent.X, sent.Y, sent.Z));
        Assert.Equal(53, sent.HeldItem.Id);
        Assert.InRange(sent.EffectiveCursor.Y, 0.7f, 0.8f);
        Assert.Empty(s.Got<PlayerDiggingPacket>());
        Assert.Equal(0, c.World.Player.HeldSlot);
        c.EndHumanControl();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_placement_reports_placed_only_once_the_game_server_fills_the_cell(bool accepted)
    {
        var (c, s) = await Harness.StartAsync(server =>
        {
            server.Blocks[(8, 64, 10)] = Blocks.Stone;
            server.Inventory[36] = Harness.Stack(Blocks.Dirt, 4);
            // A refused placement: the game server answers nothing (a body or a mob stands in the cell).
            if (!accepted) server.Hook = packet => packet is PlayerBlockPlacementPacket { Direction: not 255 };
        }, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); Aim(c, new BlockPos(8, 64, 10), 0);
        var result = await c.HumanActionAsync("use");
        Assert.True(result.Ok, result.Message);
        Assert.Equal(accepted ? MinecraftClient.HumanPlacedMessage : "used block", result.Message);
        Assert.Equal(accepted ? Blocks.Dirt : 0, c.World.GetBlock(8, 64, 9));
        c.EndHumanControl();
    }

    [Fact]
    public async Task Death_revokes_control_before_automatic_respawn()
    {
        var (c, s) = await Harness.StartAsync(opts: options => Quiet(options) with { RespawnDelay = TimeSpan.FromSeconds(2) }, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); c.SetHumanInput(new MoveInput(Forward: 1));
        s.Send(new UpdateHealthPacket { Health = 0, Food = 20, FoodSaturation = 5 });
        Assert.True(await Harness.WaitAsync(() => c.World.Player.IsDead));
        Assert.True(await Harness.WaitAsync(() => !c.HumanControlled));
        Assert.Throws<InvalidOperationException>(() => c.SetHumanInput(new MoveInput(Forward: 1)));
        Assert.False((await c.HumanActionAsync("attack")).Ok);
    }

    [Fact]
    public async Task Server_teleport_clears_old_keys_and_look_until_fresh_input_arrives()
    {
        var (c, s) = await Harness.StartAsync(opts: Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); c.SetHumanInput(new MoveInput(Forward: 1));
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Position.Z > 8.7));
        int before = c.PositionCorrections;
        s.Send(new PlayerPositionLookPacket { X = 8.5, Y = 64, Z = 8.5, Stance = 65.62, Yaw = 90, Pitch = 15, OnGround = true });
        Assert.True(await Harness.WaitAsync(() => c.PositionCorrections > before));
        await Task.Delay(80);
        Assert.True(c.HumanControlled);
        Assert.InRange(c.World.Player.Position.DistanceTo(new Vec3d(8.5, 64, 8.5)), 0, 0.01);
        Assert.Equal(90, c.World.Player.Yaw);
        Assert.Equal(15, c.World.Player.Pitch);
        c.EndHumanControl();
    }

    [Fact]
    public async Task Samples_carry_the_tick_the_applied_input_and_its_wait()
    {
        var (c, s) = await Harness.StartAsync(opts: Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        Assert.Null(c.HumanSample());
        c.BeginHumanControl();
        long sent = Stopwatch.GetTimestamp();
        c.SetHumanInput(MoveInput.Idle(45, 0), seq: 4);
        c.SetHumanInput(MoveInput.Idle(90, 10), seq: 5); // both arrive before a tick reads the keys: it uses the newer one
        long before = c.TickCount;
        var first = await NextSample(c);
        double waited = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
        Assert.Equal(5, first.AckSeq);
        Assert.InRange(first.AckWaitMs, 0, waited);
        Assert.InRange(first.Tick, before + 1, c.TickCount + 1); // the client's own tick counter, not one that starts with the takeover
        Assert.Equal((90f, 10f, true), (first.Yaw, first.Pitch, first.OnGround));
        Assert.Equal(c.World.Player.Position, first.Position);
        // Standing still: no horizontal speed, and the fall speed a resting body keeps, in blocks per tick as the tick left it.
        Assert.Equal(c.World.Player.Physics.Velocity, first.Velocity);
        Assert.Equal((0d, 0d), (first.Velocity.X, first.Velocity.Z));
        Assert.InRange(first.Velocity.Y, -0.0785, -0.0783);
        var next = await c.WaitHumanSampleAsync(first.Tick, TimeSpan.FromSeconds(3));
        Assert.NotNull(next);
        Assert.InRange(next.Tick, first.Tick + 1, c.TickCount + 1);
        // The same keys a tick or more later: the same input, which has now waited longer.
        Assert.Equal(5, next.AckSeq);
        Assert.True(next.AckWaitMs > first.AckWaitMs);
        c.EndHumanControl();
        Assert.Null(c.HumanSample());
    }

    [Fact]
    public async Task Samples_say_stopped_when_the_controller_stops_the_pawn_or_its_input_times_out()
    {
        var (c, s) = await Harness.StartAsync(opts: Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl();
        c.SetHumanInput(new MoveInput(Sneak: true, Yaw: 90, Pitch: 10), seq: 3);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Physics.Sneaking));
        c.StopHumanInput();
        var stopped = await NextSample(c);
        Assert.True(stopped.Stopped);
        Assert.Equal((3L, 90f, 10f), (stopped.AckSeq, stopped.Yaw, stopped.Pitch)); // the last input's sequence and look stay
        Assert.False(c.World.Player.Physics.Sneaking); // every key is up
        // Fresh input ends the stop. The sample is polled with the input re-sent, as a browser's heartbeat would.
        Assert.True(await Harness.WaitAsync(() =>
        {
            c.SetHumanInput(MoveInput.Idle(90, 10), seq: 4);
            return c.HumanSample() is { AckSeq: 4, Stopped: false };
        }));
        // Then nothing for longer than the input timeout: the tick lets go of the keys by itself and says so.
        Assert.True(await Harness.WaitAsync(() => c.HumanSample() is { AckSeq: 4, Stopped: true }));
        c.EndHumanControl();
    }

    [Fact]
    public async Task Samples_count_server_teleports_and_knockbacks_and_a_teleport_voids_the_acknowledged_input()
    {
        var (c, s) = await Harness.StartAsync(opts: Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); c.SetHumanInput(MoveInput.Idle(0, 0), seq: 9);
        var before = await NextSample(c);
        Assert.Equal(9, before.AckSeq);
        s.Send(new PlayerPositionLookPacket { X = 9.5, Y = 64, Z = 9.5, Stance = 65.62, Yaw = 90, Pitch = 15, OnGround = true });
        HumanControlSample? latest = null;
        Assert.True(await Harness.WaitAsync(() => (latest = c.HumanSample()) is { } m && m.Teleports > before.Teleports));
        var moved = latest!;
        Assert.Equal(before.Teleports + 1, moved.Teleports);
        // Until the browser sends fresh keys they are the pawn's own: nothing of the old input is acknowledged any more.
        Assert.Equal((-1L, 90f, 15f), (moved.AckSeq, moved.Yaw, moved.Pitch));
        Assert.InRange(moved.Position.DistanceTo(new Vec3d(9.5, 64, 9.5)), 0, 0.01);
        s.Send(new EntityVelocityPacket { EntityId = FakeServer.EntityId, VelocityY = FixedPoint.ToVelocity(0.3) });
        Assert.True(await Harness.WaitAsync(() => (latest = c.HumanSample()) is { } p && p.Impulses > moved.Impulses));
        Assert.Equal(moved.Impulses + 1, latest!.Impulses);
        c.EndHumanControl();
    }

    [Fact]
    public async Task Samples_name_the_block_under_the_crosshair()
    {
        var block = new BlockPos(8, 64, 10);
        var (c, s) = await Harness.StartAsync(server => server.Blocks[(8, 64, 10)] = Blocks.Stone, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); Aim(c, block);
        var aim = (await NextSample(c)).Aim;
        Assert.NotNull(aim);
        Assert.Equal((block, BlockFace.North, Blocks.Stone), (aim.Value.Pos, aim.Value.Face, aim.Value.BlockId));
        c.SetHumanInput(MoveInput.Idle(180, 0)); // the open air behind the pawn
        Assert.Null((await NextSample(c)).Aim);
        c.EndHumanControl();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sneaking_click_is_cast_from_the_eye_of_its_game_version(bool release1122)
    {
        // Seen from a sneaking 1.12.2 eye the look meets the low block's face 0.03 under its top edge. From the standing eye, 0.08
        // higher, the same look clears that edge and lands on the block behind and above. b1.8.1 never lowers the eye.
        var low = new BlockPos(8, 65, 10); var high = new BlockPos(8, 66, 11);
        var (c, s) = await Harness.StartAsync(server =>
        {
            server.Blocks[(8, 65, 10)] = Blocks.Dirt; server.Blocks[(8, 66, 11)] = Blocks.Dirt;
            server.Inventory[36] = Harness.Stack(Items.DiamondShovel, 1);
        }, Quiet, release1122);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl();
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(SneakingEye(c), new Vec3d(8.5, 65.97, 10));
        c.SetHumanInput(new MoveInput(Yaw: yaw, Pitch: pitch, Sneak: true));
        var (hit, missed) = release1122 ? (low, high) : (high, low);
        Assert.Equal(hit, (await NextSample(c)).Aim?.Pos); // what the tick reports is what the click will hit
        var result = await c.HumanActionAsync("attack");
        Assert.True(result.Ok, result.Message);
        Assert.True(Sent(s, PlayerDiggingPacket.StatusStarted, hit));
        Assert.False(Sent(s, PlayerDiggingPacket.StatusStarted, missed));
        Assert.Equal(0, c.World.GetBlock(hit));
        Assert.Equal(Blocks.Dirt, c.World.GetBlock(missed));
        c.EndHumanControl();
    }

    [Fact]
    public async Task A_sneaking_attack_picks_its_entity_from_the_lowered_eye()
    {
        var (c, s) = await Harness.StartAsync(opts: Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl();
        c.World.Entities.SpawnPlayer(77, "target", new Vec3d(8.5, 64, 10.5), 0, 0, 0);
        // 0.03 under the top of the target's hit box as the sneaking eye sees it; the standing eye's ray passes over the box.
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(SneakingEye(c), new Vec3d(8.5, 65.87, 10.1));
        c.SetHumanInput(new MoveInput(Yaw: yaw, Pitch: pitch, Sneak: true));
        var result = await c.HumanActionAsync("attack");
        Assert.True(result.Ok, result.Message);
        Assert.StartsWith("attacked", result.Message);
        Assert.True(await Harness.WaitAsync(() => s.Got<UseEntityPacket>().Any(p => p.TargetEntityId == 77 && p.LeftClick)));
        c.EndHumanControl();
    }

    [Fact]
    public async Task Dig_progress_is_reported_until_break_or_cancel()
    {
        var stone = new BlockPos(8, 64, 10); var obsidian = new BlockPos(9, 64, 10);
        var (c, s) = await Harness.StartAsync(server =>
        {
            server.Blocks[(8, 64, 10)] = Blocks.Stone; server.Blocks[(9, 64, 10)] = Blocks.Obsidian;
            server.BreakOnFinish = false; // the test plays the game server's answer
        }, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); Aim(c, stone);
        var digging = c.HumanActionAsync("attack");
        var seen = new List<double>();
        long tick = c.TickCount;
        // By hand the stone takes 150 ticks. The keys are kept fresh, as a browser's heartbeat does, and each new sample is read.
        while (!digging.IsCompleted && !Sent(s, PlayerDiggingPacket.StatusFinished, stone))
        {
            Aim(c, stone);
            if (await c.WaitHumanSampleAsync(tick, TimeSpan.FromMilliseconds(200)) is not { } sample) continue;
            tick = sample.Tick;
            if (sample.Dig is not { } dig) continue;
            Assert.Equal((stone, BlockFace.North, Blocks.Stone, 0), (dig.Pos, dig.Face, dig.Id, dig.Meta));
            seen.Add(dig.Progress);
        }
        Assert.True(Sent(s, PlayerDiggingPacket.StatusFinished, stone), digging.IsCompleted ? (await digging).Message : "no finish packet");
        Assert.All(seen, progress => Assert.InRange(progress, 0, 1));
        Assert.Equal(seen.Order(), seen); // never backwards
        Assert.Contains(seen, progress => progress is > 0 and < 1); // the cracks have stages to show
        // The finish packet is out and the game server has not answered: the dig stays reported, complete, so its cracks do not
        // vanish before the block does.
        Assert.True(await Harness.WaitAsync(() => c.HumanSample()?.Dig is { Progress: 1, FinishSent: true } waiting && waiting.Pos == stone));
        s.SetBlock(8, 64, 10, 0);
        var result = await digging;
        Assert.True(result.Ok, result.Message);
        Assert.Equal(MinecraftClient.HumanDugMessage, result.Message);
        var broken = await NextSample(c);
        Assert.Null(broken.Dig);
        Assert.Equal(new HumanControlBroke(1, stone, Blocks.Stone, 0), broken.Broke);

        // A dig the player gives up leaves no progress behind and breaks nothing.
        Aim(c, obsidian);
        var abandoned = c.HumanActionAsync("attack");
        Assert.True(await Harness.WaitAsync(() => { Aim(c, obsidian); return c.HumanSample()?.Dig?.Pos == obsidian; }));
        c.SetHumanInput(MoveInput.Idle(180, 0));
        var stopped = await abandoned;
        Assert.False(stopped.Ok);
        Assert.Equal(MinecraftClient.HumanDigStoppedMessage, stopped.Message);
        var idle = await NextSample(c);
        Assert.Null(idle.Dig);
        Assert.Equal(1, idle.Broke?.Count);
        c.EndHumanControl();
    }

    [Fact]
    public async Task Break_counter_advances_once_per_broken_block_and_survives_a_new_click()
    {
        var wool = new BlockPos(8, 64, 10); var flower = new BlockPos(9, 64, 10);
        var (c, s) = await Harness.StartAsync(server =>
        {
            server.Blocks[(8, 64, 10)] = Blocks.Wool; server.Metas[(8, 64, 10)] = 14;
            server.Blocks[(9, 64, 10)] = Blocks.Dandelion;
            server.BreakOnFinish = false; // the test plays the game server's answer
        }, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl();
        Assert.Null((await NextSample(c)).Broke);

        Aim(c, wool);
        var first = c.HumanActionAsync("attack");
        Assert.True(await Harness.WaitAsync(() => { Aim(c, wool); return first.IsCompleted || Sent(s, PlayerDiggingPacket.StatusFinished, wool); }));
        s.SetBlock(8, 64, 10, 0);
        Assert.Equal(MinecraftClient.HumanDugMessage, (await first).Message);
        Assert.Equal(new HumanControlBroke(1, wool, Blocks.Wool, 14), (await NextSample(c)).Broke);

        // A click that breaks nothing is a new action. The count belongs to the takeover, so it is still there.
        c.SetHumanInput(MoveInput.Idle(0, -80));
        Assert.Equal("swung hand", (await c.HumanActionAsync("attack")).Message);
        Assert.Equal(new HumanControlBroke(1, wool, Blocks.Wool, 14), (await NextSample(c)).Broke);

        // A flower breaks on the first hit: it never has progress to show, and it counts all the same.
        Aim(c, flower);
        var second = c.HumanActionAsync("attack");
        Assert.True(await Harness.WaitAsync(() => second.IsCompleted || Sent(s, PlayerDiggingPacket.StatusFinished, flower)));
        Assert.Null((await NextSample(c)).Dig);
        s.SetBlock(9, 64, 10, 0);
        Assert.Equal(MinecraftClient.HumanDugMessage, (await second).Message);
        Assert.Equal(new HumanControlBroke(2, flower, Blocks.Dandelion, 0), (await NextSample(c)).Broke);
        c.EndHumanControl();
    }

    [Fact]
    public async Task Letting_go_after_the_finish_packet_still_counts_the_block_the_server_then_breaks()
    {
        var block = new BlockPos(8, 64, 10);
        var (c, s) = await Harness.StartAsync(server =>
        {
            server.Blocks[(8, 64, 10)] = Blocks.Dirt; server.Inventory[36] = Harness.Stack(Items.DiamondShovel, 1);
            server.BreakOnFinish = false; // the game server's answer comes after the button is up
        }, Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        using var released = new CancellationTokenSource();
        c.BeginHumanControl(); Aim(c, block);
        var digging = c.HumanActionAsync("attack", holdRelease: released.Token);
        Assert.True(await Harness.WaitAsync(() => { Aim(c, block); return digging.IsCompleted || Sent(s, PlayerDiggingPacket.StatusFinished, block); }));
        released.Cancel();
        s.SetBlock(8, 64, 10, 0);
        var result = await digging;
        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, (await NextSample(c)).Broke?.Count);
        c.EndHumanControl();
    }

    [Theory]
    [InlineData(false, 20)] // b1.8.1: 3 + 8 + 6 + 3, undamaged
    [InlineData(true, 15)]  // 1.12.2: iron is 2 + 6 + 5 + 2
    public async Task The_hud_armour_counts_the_worn_pieces_by_the_rules_of_the_version(bool release1122, int full)
    {
        var (c, s) = await Harness.StartAsync(opts: Quiet, release1122: release1122);
        await using var _ = c; using var __ = s;
        Assert.Equal(0, c.HumanArmor());
        c.World.Inventory.SetSlot(0, 5, Harness.Stack(Items.IronHelmet, 1));
        c.World.Inventory.SetSlot(0, 6, Harness.Stack(Items.IronChestplate, 1));
        c.World.Inventory.SetSlot(0, 7, Harness.Stack(Items.IronLeggings, 1));
        c.World.Inventory.SetSlot(0, 8, Harness.Stack(Items.IronBoots, 1));
        Assert.Equal(full, c.HumanArmor());
        // An iron sword in the bag is no armour.
        c.World.Inventory.SetSlot(0, 9, Harness.Stack(Items.IronSword, 1));
        Assert.Equal(full, c.HumanArmor());
    }

    [Fact]
    public async Task Sample_is_not_reused_while_ticks_are_held_back()
    {
        var (c, s) = await Harness.StartAsync(opts: Quiet, release1122: true);
        await using var _ = c; using var __ = s;
        c.BeginHumanControl(); c.SetHumanInput(MoveInput.Idle(0, 0), seq: 1);
        var walking = await NextSample(c);
        // Riding: the game server drives the body, so the client's ticks pass without one under the player's keys.
        s.Send(new AddObjectVehiclePacket { EntityId = 600, Type = 1, X = FixedPoint.ToAbsolute(9.5), Y = FixedPoint.ToAbsolute(64), Z = FixedPoint.ToAbsolute(8.5) });
        s.Send(new AttachEntityPacket { EntityId = FakeServer.EntityId, VehicleId = 600 });
        Assert.True(await Harness.WaitAsync(() => c.RidingEntityId == 600));
        long mounted = c.TickCount;
        Assert.True(await Harness.WaitAsync(() => c.TickCount >= mounted + 2)); // a human tick under way at the mount has ended by now
        Assert.Null(c.HumanSample());
        Assert.Null(await c.WaitHumanSampleAsync(walking.Tick, TimeSpan.FromMilliseconds(60)));
        long held = c.TickCount;
        s.Send(new AttachEntityPacket { EntityId = FakeServer.EntityId, VehicleId = -1 });
        var back = await c.WaitHumanSampleAsync(walking.Tick, TimeSpan.FromSeconds(3));
        Assert.NotNull(back);
        // Numbered by the client's ticks, which went on while the body was held: a later frame is never mistaken for the next one.
        Assert.InRange(back.Tick, held + 1, c.TickCount + 1);
        c.EndHumanControl();
    }
}
