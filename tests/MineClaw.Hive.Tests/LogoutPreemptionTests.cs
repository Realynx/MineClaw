using MineClaw.Hive.Clone;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Testing;
using MineClaw.World;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 10-29: the DeepSeek hive 420 reported its deforest task done, went to put its logs away before logging out, and every
/// chest was busy: "420 is still stuck on its deposit job and won't take orders from me rn" while Fox wanted it to come. A new
/// order (main's task, or a player addressing it) now pre-empts a logout that only followed the clone's own finished task: the
/// deposit stops, the clone stays online and takes the order (the deposit runs again at its next logout).
/// </summary>
[Collection("processes")]
public class LogoutPreemptionTests
{
    [Fact]
    public async Task A_clone_mid_deposit_at_a_busy_chest_takes_mains_come_to_Fox_order_and_stays_online()
    {
        var pipe = PipeNames.For("maid_chan_420");
        await using var server = PipeNames.Server(pipe);
        var cfg = TestHive.Config();
        cfg.FailureGraceSeconds = 0;
        var port = new FakePort(stacks: 3, chestSlots: 27) { DepositDelay = TimeSpan.FromSeconds(30) }; // the chest is crowded
        var body = new FakeBody("maid_chan_420", port);
        var host = new CloneHost("maid_chan_420", pipe, cfg, body);
        var run = Task.Run(() => host.RunAsync(CancellationToken.None));
        await server.WaitForConnectionAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        var main = new HiveLink(server, "test-main");
        main.Handler = (m, _) => Task.FromResult<object?>(m.T == Msg.Hello
            ? new WelcomeMsg("maid_chan", new TaskMsg("t1", "deforest around log_chest"), new RosterMsg("maid_chan", [], [], new()), new ClaimsMsg([]), false, 0)
            : null);
        main.Start();
        try
        {
            await Wait.Until(() => body.Calls.Contains("task deforest around log_chest"), what: "first task");

            // Its brain says done: the logout gate starts depositing (and sits at the crowded chest).
            _ = host.ReportAsync("done", "forest cleared");
            await Wait.Until(() => host.LoggingOut && port.Deposits == 0 && body.Calls.Contains("stop"), what: "depositing before logout");
            await Task.Delay(300);
            Assert.True(host.LoggingOut);

            // Main relays Fox's order: it pre-empts the deposit.
            await main.SendAsync(Msg.Task, new TaskMsg("t2", "come to PoofimaFox"));
            await Wait.Until(() => body.Calls.Contains("task come to PoofimaFox"), what: "the new order");
            Assert.False(host.LoggingOut);
            Assert.Null(host.LogoutReason);
            Assert.False(body.Disconnected.Task.IsCompleted);
            int stop = body.Calls.LastIndexOf("stop"), start = body.Calls.LastIndexOf("start");
            Assert.True(start > stop, "the brain runs again after the pre-empted deposit");
            Assert.Equal(3, port.Items().Count); // still carrying: the deposit runs at the next logout
        }
        finally
        {
            port.DepositDelay = TimeSpan.Zero;
            await main.DisposeAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task A_players_direct_order_pre_empts_it_too_but_a_recall_is_never_pre_empted()
    {
        var pipe = PipeNames.For("maid_chan_69");
        await using var server = PipeNames.Server(pipe);
        var cfg = TestHive.Config();
        var port = new FakePort(stacks: 2, chestSlots: 27) { DepositDelay = TimeSpan.FromSeconds(30) };
        var body = new FakeBody("maid_chan_69", port);
        var host = new CloneHost("maid_chan_69", pipe, cfg, body);
        var run = Task.Run(() => host.RunAsync(CancellationToken.None));
        await server.WaitForConnectionAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        var main = new HiveLink(server, "test-main");
        main.Handler = (m, _) => Task.FromResult<object?>(m.T == Msg.Hello
            ? new WelcomeMsg("maid_chan", new TaskMsg("t1", "chop"), new RosterMsg("maid_chan", [], [], new()), new ClaimsMsg([]), false, 0)
            : null);
        main.Start();
        try
        {
            await Wait.Until(() => body.Calls.Contains("task chop"), what: "first task");
            _ = host.ReportAsync("done", "chopped");
            await Wait.Until(() => host.LoggingOut && body.Calls.Contains("stop"), what: "depositing");
            var hi = new MineClaw.Agent.Chat.ChatMessage(MineClaw.Agent.Chat.ChatKind.Public, "PoofimaFox", "69 come here", "<PoofimaFox> 69 come here");
            Assert.True(host.Arbiter.Decide(hi, locallyAddressed: false));
            await Wait.Until(() => !host.LoggingOut, what: "pre-empted by Fox");
            Assert.Null(host.LogoutReason);

            // A recall from main is a real logout: an order arriving meanwhile doesn't stop it.
            _ = host.LogoutAsync("recalled", planned: true, TimeSpan.FromSeconds(2));
            await Wait.Until(() => host.LoggingOut, what: "recall");
            Assert.False(host.PreemptLogout("test"));
        }
        finally
        {
            port.DepositDelay = TimeSpan.Zero;
            await main.DisposeAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception) { }
        }
    }
}
