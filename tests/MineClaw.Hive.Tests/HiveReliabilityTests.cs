using MineClaw.Agent;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Supervisor;
using MineClaw.Hive.Testing;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Overnight-shift fixes (live 10-27): the clone gave up on a busy main after 16 s ("main silent"), a restarted clone sat idle,
/// the parallel "one clone mines, another chops" spawns both landed on 67 (the tree job was lost), and the retry for 69 was
/// refused as "67 already owns PoofimaFox's request".
/// </summary>
[Collection("processes")]
public class HiveReliabilityTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task A_main_blocked_for_30s_keeps_its_clone()
    {
        var pipe = PipeNames.For("maid_chan_69");
        await using var server = PipeNames.Server(pipe);
        var cfg = TestHive.Config();
        cfg.HeartbeatTimeoutSeconds = 6;  // tolerance well under the stall: only the ping/pong probe keeps the clone
        cfg.PingTimeoutSeconds = 3;
        var body = new FakeBody("maid_chan_69", new FakePort());
        var host = new CloneHost("maid_chan_69", pipe, cfg, body);
        var run = Task.Run(() => host.RunAsync(CancellationToken.None));
        await server.WaitForConnectionAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        // "main": answers hello, then is blocked solid for 30 s (an Opus turn + tools + a synchronous db call on the same
        // thread): every handler call blocks, and it sends NOTHING itself (no heartbeat thread, no pushes).
        var blockedUntil = DateTime.UtcNow.AddSeconds(32);
        var main = new HiveLink(server, "test-main");
        main.Handler = (m, _) =>
        {
            if (m.T == Msg.Hello)
                return Task.FromResult<object?>(new WelcomeMsg("maid_chan", new TaskMsg("t1", "stand by"), new RosterMsg("maid_chan", [], [], new()), new ClaimsMsg([]), false, 0));
            var left = blockedUntil - DateTime.UtcNow;
            if (left > TimeSpan.Zero) Thread.Sleep(left); // synchronous block, like the live stall
            return Task.FromResult<object?>(null);
        };
        main.Start();
        await Wait.Until(() => body.Calls.Contains("start"), what: "clone running");

        await Task.Delay(TimeSpan.FromSeconds(30));
        Assert.False(host.LoggingOut, "the clone gave up on a busy main: " + host.LogoutReason);
        Assert.False(body.Disconnected.Task.IsCompleted);
        Assert.True(host.Link is { IsOpen: true });

        // Once main really is gone, the clone still notices (pipe closed).
        await main.DisposeAsync();
        await body.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("main process lost", host.LogoutReason);
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_supervisor_link_heartbeats_from_its_own_thread_while_the_pool_is_starved()
    {
        var pipe = PipeNames.For("maid_chan_420");
        await using var server = PipeNames.Server(pipe);
        await using var client = PipeNames.Client(pipe);
        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accept;
        var a = new HiveLink(server, "main");
        var b = new HiveLink(client, "clone");
        a.Start();
        b.Start();
        a.StartHeartbeat(TimeSpan.FromMilliseconds(200));
        ThreadPool.GetMinThreads(out var w0, out var io0);
        ThreadPool.SetMinThreads(1, io0);
        try
        {
            // Saturate the pool with blocking work for 4 s: timers and async continuations stall, the link threads don't.
            var hogs = Enumerable.Range(0, Environment.ProcessorCount * 4).Select(_ => Task.Run(() => Thread.Sleep(4000))).ToArray();
            await Task.Run(() => Thread.Sleep(3500));
            Assert.True(DateTimeOffset.UtcNow - b.LastReceived < TimeSpan.FromSeconds(1.5), $"silent {(DateTimeOffset.UtcNow - b.LastReceived).TotalSeconds:0.0}s");
            Assert.True(a.HeartbeatsSent >= 10, $"beats {a.HeartbeatsSent}");
            await Task.WhenAll(hogs);
        }
        finally { ThreadPool.SetMinThreads(w0, io0); }
        Assert.True(await b.ProbeAsync(TimeSpan.FromSeconds(3)));
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task A_crashed_clone_is_restarted_with_its_unfinished_task_loadout_and_claims()
    {
        var cfg = TestHive.Config();
        cfg.BackoffBaseSeconds = 0.3;
        var marker = Path.Combine(Path.GetTempPath(), "mineclaw-hive-tests", Guid.NewGuid().ToString("N") + ".crashed");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await using var h = new TestHive(cfg, mode: "crashonce", stubArgs: ["--marker", marker]);
        Assert.StartsWith("ok", h.Supervisor.Spawn("mine cobble all night", "67"));
        await Wait.Until(() => h.NoticeText().Contains("WELCOME"), 60, "the restarted clone's report");
        var text = h.NoticeText();
        output.WriteLine(text);
        Assert.Contains("67 lost", text);
        Assert.Contains("restarted=True task=mine cobble all night", text);
        Assert.Contains("loadout=pickaxe,food", text);
        Assert.Contains("claims=block:", text);
        Assert.Contains("regranted=True", text);
        Assert.Equal(1, h.Board.CountOf("maid_chan_67"));
        await h.Supervisor.RecallAllAsync("test over", TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_finished_or_recalled_task_is_not_resumed()
    {
        await using var h = new TestHive(mode: "normal");
        Assert.StartsWith("ok", h.Supervisor.Spawn("done quickly", "69"));
        var slot = h.Supervisor.Slot("maid_chan_69")!;
        await Wait.Until(() => slot.State == CloneState.Idle, 30, "logout after done");
        Assert.True(slot.ResultIn);
        await Task.Delay(1000);
        Assert.Equal(CloneState.Idle, slot.State); // no restart for a finished task
    }

    [Fact]
    public async Task Parallel_spawns_of_one_turn_land_on_different_clones_and_the_split_job_is_not_refused()
    {
        await using var h = new TestHive(mode: "normal");
        TurnContext.Current = new TurnInfo("PoofimaFox", ["PoofimaFox"], false);
        try
        {
            // The brain's two parallel tool calls: the cobble job named for 67, the tree job unnamed. Either order.
            var go = new ManualResetEventSlim();
            var named = Task.Run(() => { go.Wait(); return h.Supervisor.Spawn("work the other cell of the dual cobble generator all night", "67"); });
            var unnamed = Task.Run(() => { go.Wait(); return h.Supervisor.Spawn("chop trees all night and replant"); });
            go.Set();
            var r = await Task.WhenAll(named, unnamed);
            output.WriteLine(string.Join(" || ", r));
            Assert.All(r, x => Assert.StartsWith("ok", x));
            var s67 = h.Supervisor.Slot("maid_chan_67")!;
            var s69 = h.Supervisor.Slot("maid_chan_69")!;
            Assert.Contains("cobble", s67.Task!.Text);
            Assert.Contains("chop trees", s69.Task!.Text); // the tree job went to a free clone, never dropped
            Assert.Equal(["pickaxe", "food"], s67.Task.Loadout!);
            Assert.Equal(["axe", "food"], s69.Task.Loadout!);
            await Wait.Until(() => s67.Connected && s69.Connected, 30, "both up");

            // A third, different job for the same player isn't "already owned" by 67.
            Assert.StartsWith("ok", h.Supervisor.Spawn("smelt the cobble into stone", "420"));
            // The same job again, named for another clone, is a helper on it (night review 10-27 B6: ownership covers a
            // conversation, not every task; 67 isn't talking with the player about it).
            Assert.StartsWith("ok", h.Supervisor.Spawn("work the other cell of the dual cobble gen all night", "1337"));
        }
        finally { TurnContext.Current = null; }
        await h.Supervisor.RecallAllAsync("test over", TimeSpan.FromSeconds(20));
        // Every clone launched exactly once (no superseded double launch of 67).
        Assert.DoesNotContain("67 lost", h.NoticeText());
        Assert.DoesNotContain("69 lost", h.NoticeText());
    }

    [Theory]
    [InlineData("build 2 more rows of planks on the west edge", "build 2 more rows of cobble on the west edge", true)]
    [InlineData("Work the OTHER cell of the dual cobble generator all night", "chop trees all night", false)]
    [InlineData("mine cobble at the gen", "mine cobblestone at the gen", true)]
    public void Same_request_is_content_word_overlap(string a, string b, bool same) =>
        Assert.Equal(same, HiveSupervisor.SameRequest(a, b));

    [Theory]
    [InlineData("work the other cell of the dual cobble gen all night", "pickaxe,food")]
    [InlineData("chop trees and replant saplings", "axe,food")]
    [InlineData("say hi to kiwi", "")]
    [InlineData("stay near the house overnight", "food")]
    public void Task_loadout_from_the_task_text(string task, string want) =>
        Assert.Equal(want, string.Join(",", TaskLoadout.For(task) ?? []));

    [Theory]
    [InlineData("lay the standard field at 150,67,255: trench, tilled rows, fence", "hoe,food")]
    [InlineData("till the rows and plant wheat seeds between the trenches", "hoe,food")]
    [InlineData("replant the empty farmland", "hoe,food")]
    [InlineData("chop trees and replant saplings", "axe,food")]
    public void Farm_tasks_fetch_a_hoe_first(string task, string want) =>
        Assert.Equal(want, string.Join(",", TaskLoadout.For(task) ?? []));
}
