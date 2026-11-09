using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.Data;
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
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(c.World.Player.EyePosition, new Vec3d(8.5, 64.75, 10));
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
}
