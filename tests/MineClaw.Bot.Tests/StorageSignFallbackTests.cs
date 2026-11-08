using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public sealed class StorageSignFallbackTests
{
    [Fact]
    public async Task Full_chest_fallback_skips_incompatible_signed_chest_and_uses_compatible_storage()
    {
        using var server = new FakeServer(release1122: true);
        var full = new BlockPos(7, 64, 8);
        var signed = new BlockPos(9, 64, 8);
        var spare = new BlockPos(8, 64, 10);
        var sign = signed.Offset(0, 0, -1);
        foreach (var p in new[] { full, signed, spare })
        {
            server.Blocks[(p.X, p.Y, p.Z)] = Blocks.Chest;
            server.Chests[(p.X, p.Y, p.Z)] = Enumerable.Repeat(p == full ? new ItemStack(Blocks.Cobblestone, 64, 0) : ItemStack.Empty, 27).ToArray();
        }
        server.Blocks[(sign.X, sign.Y, sign.Z)] = Blocks.WallSign;
        server.Metas[(sign.X, sign.Y, sign.Z)] = 2;
        server.Inventory[36] = new ItemStack(Blocks.Cobblestone, 8, 0);
        await using var client = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", ConnectionFactory = server.CreateConnection,
            Profile = MineClaw.Client.Versioning.GameVersionProfiles.Java1122,
            TickInterval = TimeSpan.FromMilliseconds(5), HostileAlertRadius = 0,
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(timeout.Token);
        string[] lines = ["wood", "", "", ""];
        server.Send(new UpdateSignPacket { X = sign.X, Y = (short)sign.Y, Z = sign.Z, Lines = lines });
        while (!client.World.Player.OnGround || client.World.BlockEntities.Get(sign) == null) await Task.Delay(5, timeout.Token);
        var policy = new StorageSignPolicy([signed], [(sign, lines)], (id, _) => id == Blocks.Log);
        client.StorageSignRules = (dim, at) => dim == 0 && at == signed ? policy : null;
        client.NoOverflowChests = true;
        var result = await ChestStore.StoreAsync(new MinecraftGameClient(client), null, full, _ => true, timeout.Token);
        Assert.True(result.Ok, result.Text);
        while (server.Chests[(spare.X, spare.Y, spare.Z)].Where(x => x.Id == Blocks.Cobblestone).Sum(x => x.Count) != 8)
            await Task.Delay(5, timeout.Token);
        Assert.All(server.Chests[(signed.X, signed.Y, signed.Z)], x => Assert.True(x.IsEmpty));
        Assert.Equal(8, server.Chests[(spare.X, spare.Y, spare.Z)].Where(x => x.Id == Blocks.Cobblestone).Sum(x => x.Count));
        Assert.Equal(0, client.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.Contains(server.Got<PlayerBlockPlacementPacket>(), p => p.X == signed.X && p.Y == signed.Y && p.Z == signed.Z);
    }
}
