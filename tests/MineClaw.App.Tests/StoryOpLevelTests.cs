using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol.Packets;

namespace MineClaw.App.Tests;

/// <summary>Op detection: a 1.12.2 server tells the account its op level as Entity Status 24..28 on its own player (on login, /op, /deop).</summary>
[Collection("Release1122World")]
public sealed class StoryOpLevelTests
{
    private static async Task<bool> Until(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    [Fact]
    public async Task The_client_learns_its_op_level_from_the_1122_server_and_forgets_it_on_relogin()
    {
        using var data = GameData.Override(Release1122GameData.Instance);
        using var server = new FakeServer(release1122: true);
        var o = new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateConnection, AutoReconnect = false,
            Profile = MineClaw.Client.Versioning.GameVersionProfiles.Java1122,
        };
        await using var client = new MinecraftClient(o);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await client.ConnectAsync(cts.Token);
        Assert.True(await Until(() => client.IsPlaying, 5000), "playing");
        Assert.Null(client.IsOp); // the server hasn't said
        server.Send(new EntityStatusPacket { EntityId = FakeServer.EntityId, Status = 28 }); // /op: level 4
        Assert.True(await Until(() => client.OpLevel == 4, 3000), "op level 4");
        Assert.True(client.IsOp);
        server.Send(new EntityStatusPacket { EntityId = FakeServer.EntityId, Status = 24 }); // /deop
        Assert.True(await Until(() => client.OpLevel == 0, 3000), "op level 0");
        Assert.False(client.IsOp);
        server.Send(new EntityStatusPacket { EntityId = FakeServer.EntityId + 1, Status = 28 }); // another entity's status: not ours
        await Task.Delay(100);
        Assert.Equal(0, client.OpLevel);
    }
}
