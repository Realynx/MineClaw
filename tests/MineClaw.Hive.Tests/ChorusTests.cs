using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;
using MineClaw.Hive.Testing;
using MineClaw.Protocol.Packets;

namespace MineClaw.Hive.Tests;

public class ChorusTests
{
    private static HiveSupervisor Supervisor(HiveConfig? cfg = null, TimeProvider? time = null)
    {
        cfg ??= new HiveConfig();
        return new HiveSupervisor(cfg, "maid_chan", Stub.Launcher(), new ClaimBoard(TimeSpan.FromSeconds(60)),
            new BudgetLedger(new BudgetOptions(), cfg.Budget, () => (0m, 0m)), new ChatArbiter("maid_chan", "maid_chan", []),
            null, () => new SlotFacts(30, ["maid_chan"]), [], time: time);
    }

    private static ToolRegistry Tools(HiveSupervisor supervisor)
    {
        var tools = new ToolRegistry();
        HiveTools.RegisterMain(tools, supervisor);
        return tools;
    }

    private static Task<ToolResult> Chorus(ToolRegistry tools, string text = "o7", string name = "all") =>
        tools.ExecuteAsync("hive", JsonSerializer.SerializeToElement(new { op = "chorus", task = text, name }), default);

    private static ChorusSpeaker Speaker(string name, ConcurrentQueue<(string Name, string Text, long At)> said, bool main = false,
        Func<bool>? online = null) => new(name, main, online ?? (() => true), (text, _) =>
        {
            said.Enqueue((name, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Main_tool_fans_out_one_shared_fire_time_to_every_live_clone_and_local_body()
    {
        await using var sup = Supervisor();
        var received = new ConcurrentQueue<(string Name, ChorusMsg Message)>();
        var links = new List<HiveLink>();
        var said = new ConcurrentQueue<(string Name, string Text, long At)>();
        sup.ChorusSpeakers = () => [Speaker("maid_chan", said, main: true), Speaker("maid_chan_101", said)];
        try
        {
            foreach (var name in new[] { "maid_chan_67", "maid_chan_69", "maid_chan_420" })
            {
                var (a, b) = MemoryDuplexStream.CreatePair();
                var main = new HiveLink(a, name);
                var clone = new HiveLink(b, "main") { Handler = (m, _) =>
                {
                    if (m.T == Msg.Chorus) received.Enqueue((name, m.As<ChorusMsg>()!));
                    return Task.FromResult<object?>(null);
                } };
                main.Start(); clone.Start(); links.Add(main); links.Add(clone);
                var slot = sup.AddSlot(name);
                slot.Link = main; slot.Connected = true; slot.State = CloneState.Running;
            }
            sup.AddSlot("maid_chan_999"); // offline: never a recipient
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.True((await Chorus(Tools(sup), "§ao7")).Ok);
            await Wait.Until(() => received.Count == 3, 5, "all live clones receive the chorus");
            var messages = received.ToArray();
            Assert.Equal(new[] { "maid_chan_420", "maid_chan_67", "maid_chan_69" }, messages.Select(m => m.Name).Order().ToArray());
            var fire = Assert.Single(messages.Select(m => m.Message.FireAtMs).Distinct());
            Assert.InRange(fire - before, 1500, 2000);
            Assert.All(messages, m => { Assert.Equal("o7", m.Message.Text); Assert.True(m.Message.IncludeMain); Assert.Null(m.Message.Names); });
            Assert.Empty(said);
            await Wait.Until(() => said.Count == 2, 5, "main and its hosted body speak");
            Assert.All(said, s => { Assert.Equal("o7", s.Text); Assert.InRange(s.At - fire, 0, 1000); });
        }
        finally { foreach (var link in links) await link.DisposeAsync(); }
    }

    [Theory]
    [InlineData("/op someone")]
    [InlineData(" §a/op someone")]
    [InlineData("\u200b/op someone")]
    [InlineData("hi\n/op someone")]
    [InlineData("hi\rthere")]
    [InlineData("hi\u2028there")]
    [InlineData("")]
    public async Task Unsafe_text_is_refused_without_consuming_a_chorus(string text)
    {
        await using var sup = Supervisor();
        var tools = Tools(sup);
        Assert.False((await Chorus(tools, text)).Ok);
        Assert.True((await Chorus(tools)).Ok); // refusal does not spend the rate limit; feature must actually exist
    }

    [Fact]
    public async Task Length_limit_applies_to_raw_and_sanitized_text_and_accepts_exactly_100()
    {
        await using var sup = Supervisor();
        var tools = Tools(sup);
        Assert.False((await Chorus(tools, new string('x', 101))).Ok);
        Assert.False((await Chorus(tools, new string('…', 34))).Ok); // transliteration would split into a second line
        Assert.True((await Chorus(tools, new string('x', 100))).Ok);
    }

    [Fact]
    public async Task Default_rate_limit_is_one_per_20_seconds_and_three_per_rolling_five_minutes()
    {
        var time = new ManualTime();
        var start = time.Now;
        await using var sup = Supervisor(time: time);
        var tools = Tools(sup);
        Assert.True((await Chorus(tools)).Ok);
        time.Now = time.Now.AddSeconds(19.999);
        Assert.False((await Chorus(tools)).Ok);
        time.Now = start.AddSeconds(20);
        Assert.True((await Chorus(tools)).Ok);
        time.Now = start.AddSeconds(40);
        Assert.True((await Chorus(tools)).Ok);
        time.Now = start.AddSeconds(300).AddTicks(-1);
        Assert.False((await Chorus(tools)).Ok);
        time.Now = start.AddSeconds(300);
        Assert.True((await Chorus(tools)).Ok); // oldest entry has just aged out
    }

    [Fact]
    public async Task Rate_limits_read_the_Hive_configuration_and_concurrent_requests_share_the_limit()
    {
        var cfg = new HiveConfig { ChorusIntervalSeconds = 2, ChorusMaxPerWindow = 2, ChorusWindowSeconds = 10 };
        var time = new ManualTime();
        await using var sup = Supervisor(cfg, time);
        var tools = Tools(sup);
        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Chorus(tools)));
        Assert.Single(simultaneous, r => r.Ok);
        time.Now = time.Now.AddSeconds(2);
        Assert.True((await Chorus(tools)).Ok);
        time.Now = time.Now.AddSeconds(2);
        Assert.False((await Chorus(tools)).Ok);
        time.Now = time.Now.AddSeconds(6);
        Assert.True((await Chorus(tools)).Ok);
    }

    [Fact]
    public async Task Clone_tools_cannot_initiate_chorus_even_with_handwritten_input()
    {
        await using var sup = Supervisor();
        var main = Tools(sup);
        Assert.Contains("chorus", main.Definitions(ModelTier.Main).Single().InputSchema.GetRawText());
        Assert.Contains("op=chorus", HiveTools.ChorusHint); // usage is main's cached prompt line (the tool text is budget-pinned)
        var host = new CloneHost("maid_chan_67", "unused", new HiveConfig(), new FakeBody("maid_chan_67", new FakePort()));
        var hiveClone = new ToolRegistry();
        CloneHiveTool.Register(hiveClone, host);
        Assert.DoesNotContain("chorus", hiveClone.Definitions(ModelTier.Main).Single().InputSchema.GetRawText());
        Assert.False((await Chorus(hiveClone)).Ok);
        var scriptClone = new ToolRegistry();
        HiveTools.RegisterClone(scriptClone, host);
        Assert.DoesNotContain(scriptClone.Definitions(ModelTier.Main), d => d.Name == "hive");
        Assert.False((await Chorus(scriptClone)).Ok);
    }

    private sealed class CloneLink : IAsyncDisposable
    {
        public required HiveLink Main { get; init; }
        public required CloneHost Host { get; init; }
        public required FakeBody Body { get; init; }
        public async ValueTask DisposeAsync()
        {
            if (Host.Link is { } link) await link.DisposeAsync();
            await Main.DisposeAsync();
        }

        public static async Task<CloneLink> Open()
        {
            var transport = new MemoryTransport();
            var address = transport.NewAddress("maid_chan_67");
            var endpoint = transport.Listen(address); // the accepted stream belongs to the link, not the endpoint
            var body = new FakeBody("maid_chan_67", new FakePort());
            var host = new CloneHost(body.Name, address, new HiveConfig(), body, transport: transport);
            var connect = host.ConnectParentAsync(TimeSpan.FromSeconds(3), default);
            var main = new HiveLink(await endpoint.AcceptAsync(default), body.Name)
            {
                Handler = (m, _) => Task.FromResult<object?>(m.T == Msg.Hello
                    ? new WelcomeMsg("maid_chan", null, new RosterMsg("maid_chan", [], [], []), new ClaimsMsg([]), false, 0) : null),
            };
            main.Start();
            Assert.NotNull(await connect);
            return new CloneLink { Main = main, Host = host, Body = body };
        }
    }

    [Fact]
    public async Task Clone_receives_IPC_and_says_plain_chat_at_the_shared_time_without_blocking_its_inbox()
    {
        await using var clone = await CloneLink.Open();
        var said = new ConcurrentQueue<(string Name, string Text, long At)>();
        clone.Host.ChorusSpeakers = () => [Speaker(clone.Body.Name, said), Speaker("maid_chan_102", said)];
        var fire = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 500;
        await clone.Main.SendAsync(Msg.Chorus, new ChorusMsg("§ao7", fire));
        await clone.Main.RequestAsync<bool>(Msg.Cmd, new CmdMsg("probe"), TimeSpan.FromSeconds(2));
        Assert.Contains("cmd probe", clone.Body.Calls); // message following chorus has already run
        Assert.Empty(said);
        await Wait.Until(() => said.Count == 2, 5, "clone and its own subhive body speak");
        Assert.All(said, s => { Assert.Equal("o7", s.Text); Assert.InRange(s.At - fire, 0, 1000); });
    }

    [Theory]
    [InlineData("o7", -3001)]
    [InlineData("§a/op someone", 0)]
    [InlineData("hi\nthere", 0)]
    public async Task Clone_drops_late_or_unsafe_IPC_but_still_accepts_a_valid_line(string text, int offset)
    {
        await using var clone = await CloneLink.Open();
        await clone.Main.SendAsync(Msg.Chorus, new ChorusMsg(text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + offset));
        await clone.Main.RequestAsync<bool>(Msg.Cmd, new CmdMsg("barrier"), TimeSpan.FromSeconds(2));
        Assert.Empty(clone.Body.Said);
        await clone.Main.SendAsync(Msg.Chorus, new ChorusMsg("o7", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await Wait.Until(() => clone.Body.Said.Count == 1, 3, "valid chorus");
        Assert.Equal("o7", Assert.Single(clone.Body.Said));
    }

    [Fact]
    public async Task Selected_names_and_main_opt_out_apply_without_waking_offline_or_new_bodies()
    {
        await using var sup = Supervisor();
        var said = new ConcurrentQueue<(string Name, string Text, long At)>();
        var online = true;
        sup.ChorusSpeakers = () => [Speaker("maid_chan", said, main: true), Speaker("maid_chan_67", said),
            Speaker("maid_chan_69", said), Speaker("maid_chan_420", said, online: () => online)];
        // The tool keeps main in (its text is budget-pinned); the opt-out lives on the supervisor API.
        Assert.StartsWith("ok", await sup.ChorusAsync("o7", "67,maid_chan_420", includeMain: false));
        online = false; // disconnected before fire: do not replay it
        await Wait.Until(() => said.Count > 0, 5, "selected body");
        await Task.Delay(100);
        Assert.Equal("maid_chan_67", Assert.Single(said).Name);
    }

    [Fact]
    public async Task Busy_body_says_the_line_at_fire_time_while_its_long_job_keeps_running()
    {
        using var server = new MineClaw.Client.Tests.FakeServer();
        await using var client = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan_67", ConnectionFactory = server.CreateClientConnection,
            TickInterval = TimeSpan.FromMilliseconds(10), ChatInterval = TimeSpan.Zero, HostileAlertRadius = 0,
        });
        using var stop = new CancellationTokenSource();
        await client.ConnectAsync(stop.Token);
        await Wait.Until(() => client.World.Player.OnGround, 5, "fake client settles");
        var tools = new ToolRegistry();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        tools.Register("hunt", "long job", ToolSchema.Empty, async (_, ct) =>
        {
            started.TrySetResult();
            try { await finish.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return ToolResult.Success("finished normally");
        }, longRunning: true);
        var activities = new ActivityManager(tools, new EventQueue());
        var adapter = new MinecraftGameClient(client) { OutgoingFilter = _ => false }; // normal subhive speech is suppressed
        var body = new Body("maid_chan_67", true, adapter, adapter, activities);
        var registry = new BodyRegistry(body);
        var job = activities.RunAsync([(tools.All.Single(t => t.Name == "hunt"), ToolSchema.Empty)], TimeSpan.Zero, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var id = activities.Current!.Id;
        await using var clone = await CloneLink.Open();
        clone.Host.ChorusSpeakers = () => HiveChorus.Speakers(registry, main: false);
        var fire = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 500;
        try
        {
            await clone.Main.SendAsync(Msg.Chorus, new ChorusMsg("o7", fire));
            await Wait.Until(() => server.Got<ChatPacket>().Any(p => p.Message == "o7"), 5, "busy body sends chat");
            Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - fire, 0, 1000);
            Assert.True(activities.IsBusy);
            Assert.Equal(id, activities.Current!.Id);
            Assert.False(cancelled);
            Assert.False(finish.Task.IsCompleted);
            Assert.DoesNotContain(clone.Body.Calls, c => c == "stop");
            finish.TrySetResult();
            await job;
            await Wait.Until(() => !activities.IsBusy, 3, "original job finishes normally");
            Assert.False(cancelled);
        }
        finally
        {
            finish.TrySetResult();
            stop.Cancel();
            await client.DisconnectAsync("test complete");
        }
    }
}
