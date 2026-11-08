using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public sealed class RemoveStorageSignToolTests
{
    [Fact]
    public async Task Tool_preserves_blank_lines_in_the_exact_server_text_snapshot()
    {
        using var server = new FakeServer();
        server.Blocks[(9, 64, 8)] = Blocks.WallSign;
        server.Metas[(9, 64, 8)] = 2;
        server.Blocks[(9, 64, 9)] = Blocks.Chest;
        await using var client = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", ConnectionFactory = server.CreateClientConnection,
            TickInterval = TimeSpan.FromMilliseconds(5), HostileAlertRadius = 0,
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        var at = new BlockPos(9, 64, 8);
        server.Send(new UpdateSignPacket { X = 9, Y = 64, Z = 8, Lines = ["wood", "", "", ""] });
        while (!client.World.Player.OnGround || client.World.BlockEntities.Get(at) == null) await Task.Delay(5, timeout.Token);
        client.IsProtected = (_, _) => true;
        var tools = new ToolRegistry();
        RemoveStorageSignTool.Register(tools, new MinecraftGameClient(client));
        var result = await tools.ExecuteAsync(RemoveStorageSignTool.ToolName,
            TestBot.J(new { at = "9,64,8", chest = "9,64,9", lines = new[] { "wood", "", "", "" } }), timeout.Token);
        Assert.True(result.Ok, result.Text);
        Assert.Equal(Blocks.Air, client.World.GetBlock(at));
        Assert.Equal(Blocks.Chest, client.World.GetBlock(9, 64, 9));
    }
}
