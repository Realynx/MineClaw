using System.Reflection;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public class TemporaryShelterTests
{
    [Fact]
    public async Task Cancelled_shelter_is_remembered_and_cleared_when_safe_without_touching_player_blocks()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 32),
            o => o with { Survival = o.Survival with { SecureOnSpawn = false, ShelterEnabled = false, ShelterMaxMinutes = 1 } });
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.CountItem(Blocks.Cobblestone) == 32), "initial inventory arrived");
        // Automatic boxing is disabled, but idle cleanup stays enabled. Night and a threat hold the explicit wait.
        c.World.Time = 18000;
        c.World.Entities.SpawnMob(901, MobTypes.Zombie, new Vec3d(6.5, 64, 6.5), 0, 0, null);
        using var stop = new CancellationTokenSource();
        var boxed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shelter = c.ShelterAsync("waiting for safety", stop.Token, boxed: _ => boxed.TrySetResult());
        await boxed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await shelter;
        // A player replaces one cell: it must never be dug by the cleanup.
        var playerBlock = new BlockPos(9, 64, 8);
        s.Blocks[(9, 64, 8)] = Blocks.Planks;
        c.World.SetBlock(playerBlock.X, playerBlock.Y, playerBlock.Z, Blocks.Planks, 0);
        c.World.Time = 6000;
        c.World.Entities.Destroy(901);
        Assert.True(await Harness.WaitAsync(() => !s.Blocks.ContainsKey((8, 66, 8)), 15000), "cancelled shelter roof was never cleaned up");
        Assert.Equal(Blocks.Planks, s.Blocks[(9, 64, 8)]);
        Assert.Equal(Blocks.Stone, s.Blocks[(8, 63, 8)]);
        Assert.DoesNotContain(s.Dug, p => p.X == 9 && p.Y == 64 && p.Z == 8);
    }

    [Fact]
    public async Task Unreachable_river_drop_is_not_claimed_to_belong_to_a_generator()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        for (int x = 3; x <= 13; x++)
        for (int z = 3; z <= 13; z++)
        {
            c.World.SetBlock(x, 61, z, Blocks.Stone, 0);
            for (int y = 62; y <= 65; y++) c.World.SetBlock(x, y, z, Blocks.Water, 0);
        }
        var method = typeof(MinecraftClient).GetMethod("LostDropReason", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reason = Assert.IsType<string>(method.Invoke(c, method.GetParameters().Length == 1 ? [new Vec3d(8.5, 63, 8.5)] : [new Vec3d(8.5, 63, 8.5), false]));
        Assert.Contains("water", reason);
        Assert.DoesNotContain("gen's", reason);
        s.StrictPickup = true;
        s.SpawnDrop(11.5, 63, 8.5, Harness.Stack(Items.Wheat, 1));
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.All().Any(e => e.Kind == EntityKind.Item)));
        var result = await c.CollectDropsAsync(8);
        Assert.True(result.Ok, result.Message); // harmless water skips must not trip a worker's repeated-failure guard
        Assert.Contains("1 drop(s) left", result.Message);
    }
}
