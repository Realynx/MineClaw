using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>The right-click primitives against the scripted server: face use, face placement, entities, signs, bow,
/// door pathing and the dimension-change event.</summary>
public class InteractTests
{
    /// <summary>A bigger 13×13 stone floor at y=63 (x,z 2..14), player at (4.5,64,8.5).</summary>
    private static void Floor(FakeServer s)
    {
        for (int x = 2; x <= 14; x++)
        for (int z = 2; z <= 14; z++)
            s.Blocks[(x, 63, z)] = Blocks.Stone;
        s.SpawnAt = (4.5, 64, 8.5);
    }

    [Theory]
    [InlineData(true)]
    public async Task Place_seeds_on_farmland_or_the_air_above_plants_them(bool targetSoil)
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 63, 8)] = Blocks.Farmland;
            srv.Inventory[36] = Harness.Stack(Items.Seeds, 3);
            srv.Hook = p =>
            {
                if (p is not PlayerBlockPlacementPacket u) return false;
                if ((u.X, u.Y, u.Z, u.Direction, u.HeldItem.Id) == (9, 63, 8, (byte)1, Items.Seeds))
                    srv.SetBlock(9, 64, 8, Blocks.Crops);
                return true;
            };
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.PlaceBlockAsync(9, targetSoil ? 63 : 64, 8, Items.Seeds);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(Blocks.Crops, c.World.GetBlock(9, 64, 8));
        Assert.Equal(Blocks.Farmland, c.World.GetBlock(9, 63, 8));
        s.SetBlock(9, 64, 8, 0);
        Assert.True(await Harness.WaitAsync(() => c.World.GetBlock(9, 64, 8) == 0));
        Assert.True((await c.PlaceBlockAsync(9, 64, 8, Items.Seeds)).Ok); // air-above targeting still works
    }

    [Fact]
    public async Task Hoe_on_the_top_face_sends_that_face_and_waits_for_the_block_change()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 63, 8)] = Blocks.Dirt;
            srv.Inventory[37] = Harness.Stack(Items.StoneHoe, 1);
            srv.Hook = p =>
            {
                if (p is PlayerBlockPlacementPacket { Direction: 1 } u && u.HeldItem.Id == Items.StoneHoe && srv.Blocks.GetValueOrDefault((u.X, u.Y, u.Z)) == Blocks.Dirt)
                {
                    srv.SetBlock(u.X, u.Y, u.Z, Blocks.Farmland);
                    return true;
                }
                return false;
            };
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.UseItemOnBlockAsync(new BlockPos(9, 63, 8), BlockFace.Top, Items.StoneHoe);
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("farmland", r.Message);
        var use = s.Got<PlayerBlockPlacementPacket>().Single();
        Assert.Equal((9, 63, 8, (byte)1), (use.X, use.Y, use.Z, use.Direction));
        // Looked at the top face (pitch down) before clicking.
        Assert.True(s.Got<PlayerLookPacket>().Any(l => l.Pitch > 20) || s.Got<PlayerPositionLookPacket>().Any(l => l.Pitch > 20));
    }

    [Fact]
    public async Task Use_on_block_without_an_effect_fails_and_never_targets_interactables()
    {
        var (c, s) = await Harness.StartAsync(srv => { srv.Inventory[37] = Harness.Stack(Items.StoneHoe, 1); srv.Chests[(9, 64, 8)] = new ItemStack[27]; srv.Blocks[(9, 64, 8)] = Blocks.Chest; });
        await using var _ = c;
        using var __ = s;
        var none = await c.UseItemOnBlockAsync(new BlockPos(8, 63, 9), BlockFace.Top, Items.StoneHoe);
        Assert.False(none.Ok);
        Assert.Contains("no effect", none.Message);
        var chest = await c.UseItemOnBlockAsync(new BlockPos(9, 64, 8), BlockFace.Top, Items.StoneHoe);
        Assert.False(chest.Ok);
        Assert.Contains("opened/used instead", chest.Message);
    }

    [Fact]
    public async Task Hoe_on_protected_water_is_a_no_op_not_a_refusal_but_protected_dirt_is_refused()
    {
        // ItemHoe never changes water, so only the dirt it would till needs an edit-protection check.
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 8)] = Blocks.Water;
            srv.Blocks[(8, 64, 10)] = Blocks.Dirt;
            srv.Inventory[37] = Harness.Stack(Items.StoneHoe, 1);
        });
        await using var _ = c;
        using var __ = s;
        c.IsProtected = (_, id) => id is Blocks.Water or Blocks.Dirt;
        var water = await c.UseItemOnBlockAsync(new BlockPos(9, 64, 8), BlockFace.Top, Items.StoneHoe);
        Assert.False(water.Ok);
        Assert.Contains("no effect", water.Message);
        Assert.DoesNotContain("protected", water.Message);
        int sent = s.Got<PlayerBlockPlacementPacket>().Count();
        var dirt = await c.UseItemOnBlockAsync(new BlockPos(8, 64, 10), BlockFace.Top, Items.StoneHoe);
        Assert.False(dirt.Ok);
        Assert.Contains("protected", dirt.Message);
        Assert.Equal(sent, s.Got<PlayerBlockPlacementPacket>().Count()); // refused before any click
    }

    [Fact]
    public async Task Bucket_on_a_face_is_followed_by_the_use_item_packet_like_vanilla()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[36] = Harness.Stack(Items.WaterBucket, 1);
            srv.Hook = p =>
            {
                if (p is PlayerBlockPlacementPacket { Direction: 255 } u && u.HeldItem.Id == Items.WaterBucket)
                {
                    srv.SetBlock(9, 64, 9, Blocks.Water);
                    srv.Inventory[36] = Harness.Stack(Items.Bucket, 1);
                    srv.Send(new SetSlotPacket { WindowId = 0, Slot = 36, Item = srv.Inventory[36] });
                }
                return p is PlayerBlockPlacementPacket;
            };
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.UseItemOnBlockAsync(new BlockPos(9, 63, 9), BlockFace.Top, Items.WaterBucket);
        Assert.True(r.Ok, r.ToString());
        var uses = s.Got<PlayerBlockPlacementPacket>().ToList();
        Assert.Equal(2, uses.Count);
        Assert.Equal(1, uses[0].Direction);
        Assert.Equal(PlayerBlockPlacementPacket.NoDirection, uses[1].Direction);
    }

    [Fact]
    public async Task Place_with_explicit_face_clicks_that_wall_and_sneaks()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 7)] = Blocks.Stone; // wall north of the target
            srv.Inventory[36] = Harness.Stack(Blocks.Torch, 4);
        });
        await using var _ = c;
        using var __ = s;
        Assert.Equal(BlockFace.South, c.WallFaceFor(new BlockPos(9, 64, 8)));
        var r = await c.PlaceBlockAsync(9, 64, 8, Blocks.Torch, BlockFace.South);
        Assert.True(r.Ok, r.ToString());
        var place = s.Got<PlayerBlockPlacementPacket>().Single();
        Assert.Equal((9, 64, 7, (byte)BlockFace.South), (place.X, place.Y, place.Z, place.Direction));
        Assert.Contains(s.Got<EntityActionPacket>(), a => a.Action == 1);
        var bad = await c.PlaceBlockAsync(9, 64, 9, Blocks.Torch, BlockFace.West); // nothing at (8,64,9)
        Assert.False(bad.Ok);
        Assert.Contains("nothing solid", bad.Message);
    }

    [Fact]
    public async Task Use_entity_sends_a_right_click_and_needs_the_server_to_react()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[36] = Harness.Stack(Items.Shears, 1);
            srv.Hook = p =>
            {
                if (p is UseEntityPacket { LeftClick: false, TargetEntityId: 500 })
                    srv.Send(new EntityMetadataPacket { EntityId = 500, Metadata = Sheared() });
                return false;
            };
        });
        await using var _ = c;
        using var __ = s;
        s.Send(new MobSpawnPacket { EntityId = 500, Type = (byte)MobTypes.Sheep, X = FixedPoint.ToAbsolute(9.5), Y = FixedPoint.ToAbsolute(64), Z = FixedPoint.ToAbsolute(8.5), Metadata = new EntityMetadata() });
        s.Send(new MobSpawnPacket { EntityId = 501, Type = (byte)MobTypes.Cow, X = FixedPoint.ToAbsolute(7.5), Y = FixedPoint.ToAbsolute(64), Z = FixedPoint.ToAbsolute(8.5), Metadata = new EntityMetadata() });
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(501) is not null));
        var r = await c.UseEntityAsync(500, Items.Shears);
        Assert.True(r.Ok, r.ToString());
        var u = s.Got<UseEntityPacket>().Single();
        Assert.False(u.LeftClick);
        Assert.Equal(FakeServer.EntityId, u.UserEntityId);
        var cow = await c.UseEntityAsync(501, null);
        Assert.False(cow.Ok);
        Assert.Contains("no effect", cow.Message);
    }

    private static EntityMetadata Sheared()
    {
        var m = new EntityMetadata();
        m.Add(MetadataEntry.FromByte(16, 0x10));
        return m;
    }

    [Fact]
    public async Task Riding_enters_by_right_click_and_dismounts_by_sneaking()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Hook = p =>
            {
                if (p is UseEntityPacket { LeftClick: false, TargetEntityId: 600 }) srv.Send(new AttachEntityPacket { EntityId = FakeServer.EntityId, VehicleId = 600 });
                if (p is EntityActionPacket { Action: 1 }) srv.Send(new AttachEntityPacket { EntityId = FakeServer.EntityId, VehicleId = -1 });
                return false;
            };
        });
        await using var _ = c;
        using var __ = s;
        s.Send(new AddObjectVehiclePacket { EntityId = 600, Type = 1, X = FixedPoint.ToAbsolute(9.5), Y = FixedPoint.ToAbsolute(64), Z = FixedPoint.ToAbsolute(8.5) });
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(600) is not null));
        var r = await c.UseEntityAsync(600);
        Assert.True(r.Ok, r.ToString());
        Assert.Contains("riding", r.Message);
        Assert.Equal(600, c.RidingEntityId);
        var off = await c.DismountAsync();
        Assert.True(off.Ok, off.ToString());
        Assert.Equal(-1, c.RidingEntityId);
    }

    [Fact]
    public async Task Write_sign_sanitizes_lines_and_waits_for_the_echo()
    {
        var (c, s) = await Harness.StartAsync(srv => { srv.Blocks[(9, 64, 8)] = Blocks.SignPost; });
        await using var _ = c;
        using var __ = s;
        var r = await c.WriteSignAsync(new BlockPos(9, 64, 8), ["Ores `and` gems", "a very long line of text", "§cred"]);
        Assert.True(r.Ok, r.ToString());
        var sent = s.Got<UpdateSignPacket>().Single();
        Assert.All(sent.Lines, l => Assert.True(l.Length <= 15));
        Assert.DoesNotContain(sent.Lines, l => l.Contains('`') || l.Contains('§'));
        Assert.Equal(4, sent.Lines.Length);
        Assert.Equal(sent.Lines, c.Signs[new BlockPos(9, 64, 8)]);
        s.SignsLocked = true;
        var again = await c.WriteSignAsync(new BlockPos(9, 64, 8), ["new"]);
        Assert.False(again.Ok);
    }

    [Fact]
    public async Task Bow_draws_for_the_charge_then_releases_with_status_5()
    {
        var (c, s) = await Harness.StartAsync(srv => { srv.Inventory[36] = Harness.Stack(Items.Bow, 1); srv.Inventory[10] = Harness.Stack(Items.Arrow, 5); });
        await using var _ = c;
        using var __ = s;
        var r = await c.ShootAsync(new Vec3d(30.5, 64, 8.5), 20);
        Assert.True(r.Ok, r.ToString());
        Assert.True(s.Shots.Single() >= 20, $"held {s.Shots.Single()} server ticks");
        var rel = s.Got<PlayerDiggingPacket>().Single(d => d.Status == PlayerDiggingPacket.StatusReleaseUseItem);
        Assert.Equal(255, rel.Face);
        Assert.Equal(4, c.World.Inventory.CountItem(Items.Arrow));
    }

    [Fact]
    public void Ballistic_aim_lands_on_the_target_with_b18_arrow_physics()
    {
        var eye = new Vec3d(0.5, 65.62, 0.5);
        foreach (var target in new[] { new Vec3d(20.5, 65, 0.5), new Vec3d(-10, 70, 25), new Vec3d(5, 60, 5), new Vec3d(40, 64, 0.5) })
        {
            var aim = ArrowBallistics.Solve(eye, target, ArrowBallistics.FullChargeSpeed);
            Assert.NotNull(aim);
            // Simulate EntityArrow.onUpdate: move, then ×0.99 and −0.05.
            var dir = PlayerPhysics.LookVector(aim.Value.Yaw, aim.Value.Pitch);
            var p = ArrowBallistics.LaunchPoint(eye);
            var v = dir * 3.0;
            double best = double.MaxValue;
            for (int t = 0; t < 100; t++)
            {
                var next = p + v;
                for (int k = 0; k <= 10; k++) best = Math.Min(best, (p + (next - p) * (k / 10.0) - target).Length);
                p = next;
                v = new Vec3d(v.X * 0.99, v.Y * 0.99 - 0.05, v.Z * 0.99);
            }
            Assert.True(best < 0.3, $"missed {target} by {best:0.00}");
        }
        Assert.Null(ArrowBallistics.Solve(eye, new Vec3d(400, 65, 0), 1.0));
        Assert.Equal(0, ArrowBallistics.Power(2));
        Assert.Equal(1, ArrowBallistics.Power(20));
    }

    [Fact]
    public async Task Goto_opens_a_closed_door_passes_and_closes_it_behind()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            for (int z = 2; z <= 14; z++) { srv.Blocks[(8, 64, z)] = Blocks.Stone; srv.Blocks[(8, 65, z)] = Blocks.Stone; }
            srv.Blocks[(8, 64, 8)] = Blocks.WoodenDoor;
            srv.Blocks[(8, 65, 8)] = Blocks.WoodenDoor;
            srv.Metas[(8, 65, 8)] = 8;
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.GotoAsync(11, 64, 8);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(11, (int)Math.Floor(c.World.Player.Position.X));
        var clicks = s.Got<PlayerBlockPlacementPacket>().Where(p => p.X == 8 && p.Z == 8).ToList();
        Assert.Equal(2, clicks.Count); // open + close behind
        Assert.False(Openables.IsOpenAt(c.World, new BlockPos(8, 64, 8)));
        Assert.Equal(0, c.PositionCorrections);
    }

    [Fact]
    public async Task Goto_replans_around_a_locked_door_and_fails_honestly_without_another_way()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Floor(srv);
            for (int z = 2; z <= 14; z++) { srv.Blocks[(8, 64, z)] = Blocks.Stone; srv.Blocks[(8, 65, z)] = Blocks.Stone; }
            srv.Blocks[(8, 64, 8)] = Blocks.WoodenDoor;
            srv.Blocks[(8, 65, 8)] = Blocks.WoodenDoor;
            srv.Metas[(8, 65, 8)] = 8;
            srv.Locked.Add((8, 64, 8));
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.GotoAsync(11, 64, 8, options: GotoOptions.Default with { AllowPartial = false });
        Assert.False(r.Ok);
        Assert.Contains("no path", r.Message);
        Assert.True(c.World.Player.Position.X < 8, "never walked into the locked door");
        int clicks = s.Got<PlayerBlockPlacementPacket>().Count(p => p.X == 8 && p.Z == 8);
        Assert.InRange(clicks, 1, 3); // the follower's click limit, then the re-plan vetoes it
    }

    [Fact]
    public async Task Respawn_into_another_dimension_raises_the_event_cancels_and_clears_state()
    {
        var (c, s) = await Harness.StartAsync(srv => { srv.Blocks[(9, 64, 8)] = Blocks.SignPost; srv.SignsLocked = true; });
        await using var _ = c;
        using var __ = s;
        (int From, int To)? changed = null;
        c.DimensionChanged += (f, t) => changed = (f, t);
        s.Send(new NewStatePacket { Reason = NewStatePacket.ReasonBeginRain });
        s.Send(new UpdateSignPacket { X = 9, Y = 64, Z = 8, Lines = ["a", "b", "", ""] });
        Assert.True(await Harness.WaitAsync(() => c.IsRaining && c.Signs.Count == 1));
        // b1.8.1 has no thunder packet: a lightning bolt while it rains is the storm.
        s.Send(new ThunderboltPacket { EntityId = 4242, Type = 1, X = 9 * 32, Y = 64 * 32, Z = 8 * 32 });
        Assert.True(await Harness.WaitAsync(() => c.World.Thunder == 1f));
        Assert.Equal(1f, c.World.Rain);
        var pending = c.WriteSignAsync(new BlockPos(9, 64, 8), ["never echoed"]); // a 2 s wait the change must cut short
        await Harness.WaitAsync(() => c.CurrentAction == "write_sign");
        s.Send(new RespawnPacket { Dimension = -1, Difficulty = 1, GameMode = 0, WorldHeight = 128, MapSeed = 1234 });
        var r = await pending.WaitAsync(TimeSpan.FromSeconds(1.5));
        Assert.False(r.Ok);
        Assert.Contains("dimension", r.Message);
        Assert.True(await Harness.WaitAsync(() => changed is not null));
        Assert.Equal((0, -1), changed!.Value);
        Assert.False(c.IsRaining);
        Assert.Equal((0f, 0f), (c.World.Rain, c.World.Thunder)); // the overworld's storm doesn't darken the Nether's light maths
        Assert.Empty(c.Signs);
        Assert.Equal(-1, c.World.Dimension);
        // Same-dimension respawn (death) raises nothing.
        changed = null;
        s.Send(new RespawnPacket { Dimension = -1, Difficulty = 1, GameMode = 0, WorldHeight = 128, MapSeed = 1234 });
        await Task.Delay(100);
        Assert.Null(changed);
    }
}
