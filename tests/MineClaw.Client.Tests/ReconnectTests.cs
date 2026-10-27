using System.Collections.Concurrent;
using MineClaw.Protocol.Packets;

namespace MineClaw.Client.Tests;

public class ReconnectTests
{
    [Theory]
    [InlineData("kicked: You logged in from another location", true)]
    [InlineData("kicked: you LOGGED IN FROM ANOTHER LOCATION", true)]
    [InlineData("kicked: Server closed", false)]
    [InlineData("kicked: The server is full!", false)]
    [InlineData("connection reset", false)]
    [InlineData(null, false)]
    public void FirstDelayIsLongOnlyForDuplicateLogin(string? reason, bool longWait)
    {
        var o = new ClientOptions { ReconnectInitialDelay = TimeSpan.FromSeconds(5), DuplicateLoginBackoff = TimeSpan.FromMinutes(10) };
        Assert.Equal(longWait ? o.DuplicateLoginBackoff : o.ReconnectInitialDelay, MinecraftClient.FirstReconnectDelay(reason, o));
    }

    [Fact]
    public async Task ReconnectsQuicklyAfterRestartButBacksOffAfterDuplicateLogin()
    {
        var servers = new ConcurrentQueue<FakeServer>();
        var o = new ClientOptions
        {
            Username = "maid_chan",
            TickInterval = TimeSpan.FromMilliseconds(5),
            ChatInterval = TimeSpan.Zero,
            HostileAlertRadius = 0,
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(50),
            DuplicateLoginBackoff = TimeSpan.FromMilliseconds(1500),
            ConnectionFactory = ct =>
            {
                var srv = new FakeServer();
                servers.Enqueue(srv);
                return srv.CreateClientConnection(ct);
            },
        };
        var c = new MinecraftClient(o);
        await using var _ = c;
        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await c.ConnectAsync(cts.Token);
            Assert.True(c.IsPlaying);

            // Normal server restart: back within the short initial delay.
            servers.Last().Send(new KickPacket("Server closed"));
            Assert.True(await Harness.WaitAsync(() => servers.Count == 2 && c.IsPlaying, 3000));

            // Someone else logged in as us: stay offline for the long backoff, then rejoin.
            servers.Last().Send(new KickPacket("You logged in from another location"));
            Assert.True(await Harness.WaitAsync(() => !c.IsPlaying, 1000));
            await Task.Delay(700);
            Assert.Equal(2, servers.Count);
            Assert.False(c.IsPlaying);
            Assert.True(await Harness.WaitAsync(() => servers.Count == 3 && c.IsPlaying, 4000));
        }
        finally
        {
            foreach (var s in servers) s.Dispose();
        }
    }
}
