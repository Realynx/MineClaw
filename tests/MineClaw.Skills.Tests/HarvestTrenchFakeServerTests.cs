using System.Collections.Concurrent;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;
using Xunit;

namespace MineClaw.Skills.Tests;

public class HarvestTrenchFakeServerTests
{
    [Theory]
    [InlineData("rim", 0)]
    [InlineData("deep", 2)]
    [InlineData("drift", 0)]
    public async Task Harvest_collects_from_the_rim_or_leaves_drifting_trench_drops_without_failing(string mode, int left)
    {
        using var server = new FakeServer { StrictPickup = true, SpawnAt = (6.5, 64, 8.5) };
        server.Blocks.Clear();
        for (int x = 2; x <= 14; x++)
        for (int z = 2; z <= 14; z++)
        {
            server.Blocks[(x, 62, z)] = Blocks.Dirt;
            server.Blocks[(x, 63, z)] = x == 8 ? Blocks.Water : Blocks.Dirt;
        }
        foreach (int z in new[] { 7, 9 })
        {
            server.Blocks[(7, 63, z)] = Blocks.Farmland;
            server.Blocks[(7, 64, z)] = Blocks.Crops;
            server.Metas[(7, 64, z)] = 7;
        }
        var drift = new ConcurrentBag<Task>();
        server.Hook = packet =>
        {
            if (packet is not PlayerDiggingPacket p || p.Status is not (0 or 2)
                || server.Blocks.GetValueOrDefault((p.X, p.Y, p.Z)) != Blocks.Crops) return false;
            server.SetBlock(p.X, p.Y, p.Z, 0);
            int id = server.SpawnDrop(8.5, mode == "rim" ? 63.95 : 63.1, p.Z + 0.5, new ItemStack(Items.Wheat, 1, 0));
            if (mode == "drift") drift.Add(Task.Run(async () =>
            {
                await Task.Delay(200);
                if (!server.Drops.TryGetValue(id, out var drop)) return;
                // Current carries the drop down the channel, then up against a reachable part of the rim.
                server.Drops[id] = (drop.X, 63.95, drop.Z + 1, drop.Item);
                server.Send(new EntityTeleportPacket { EntityId = id, X = FixedPoint.ToAbsolute(drop.X),
                    Y = FixedPoint.ToAbsolute(63.95), Z = FixedPoint.ToAbsolute(drop.Z + 1) });
            }));
            return true;
        };
        await using var client = new MinecraftClient(new ClientOptions
        {
            Username = "farmer", ConnectionFactory = server.CreateClientConnection,
            TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            HostileAlertRadius = 0, Survival = new() { Enabled = false },
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(limit.Token);
        var end = DateTime.UtcNow.AddSeconds(3);
        while ((!client.World.Player.OnGround || client.World.GetMeta(7, 64, 9) != 7) && DateTime.UtcNow < end)
            await Task.Delay(5, limit.Token);
        var registry = SkillRegistry.CreateDefault();
        var result = await registry.RunAsync(new SkillContext(new ClientGameActions(client), registry), "harvest_wheat",
            FakeGame.J(new { radius = 8, replant = false }), limit.Token);
        await Task.WhenAll(drift);
        Assert.True(result.Ok, result.Text);
        Assert.Contains("harvested 2 wheat", result.Text);
        Assert.Contains($"{left} drop(s) left behind", result.Text);
        Assert.Equal(left, server.Drops.Count);
        Assert.Equal(2 - left, client.World.Inventory.CountItem(Items.Wheat));
        Assert.DoesNotContain(server.Got<PlayerPositionPacket>(), p => (int)Math.Floor(p.X) == 8);
        Assert.Equal(Blocks.Water, server.Blocks[(8, 63, 8)]);
    }
}
