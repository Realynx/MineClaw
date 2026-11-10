using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MineClaw.ViewerBridge.Supervisor;

namespace MineClaw.ViewerBridge.Tests;

public class SupervisorClientTests
{
    [Theory]
    [InlineData("GET", "api/world/entities?since=4", true)]
    [InlineData("GET", "/api/world/blocks?since=4", true)]
    [InlineData("GET", "api/status", true)]
    [InlineData("POST", "api/world/entities", false)]
    [InlineData("GET", "api/world/entities/other", false)]
    [InlineData("GET", "api/world/chunks?cols=1,2", false)]
    [InlineData("GET", "api/viewer/now?bot=main", false)]
    public void Only_motion_and_health_reads_use_the_reserved_lane(string method, string path, bool expected) =>
        Assert.Equal(expected, SupervisorClient.IsLiveRead(new HttpMethod(method), path));

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    public async Task Motion_and_health_answer_while_every_ordinary_connection_is_held(int total, int ordinary)
    {
        await using var host = await HeldSupervisor.StartAsync();
        using var client = host.Client(total);
        var bulk = Enumerable.Range(0, ordinary).Select(i => client.GetAsync($"api/world/chunks?id=bulk{i}&hold=1", TimeSpan.FromSeconds(15), CancellationToken.None)).ToArray();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, ordinary).Select(i => host.Seen($"bulk{i}")));
            // These must arrive while the earlier chunk bodies are still held, not after a timeout frees a pool slot.
            foreach (var path in new[] { "api/world/entities?since=8", "api/world/blocks?since=9", "api/status" })
            {
                var reply = await client.GetAsync(path, TimeSpan.FromSeconds(3), CancellationToken.None);
                Assert.Equal(200, reply.Status);
                Assert.All(bulk, b => Assert.False(b.IsCompleted));
            }
            Assert.True(host.MaxActive <= total);
        }
        finally { host.Release(); await Task.WhenAll(bulk); }
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(4, 2, 2)]
    public async Task Both_lanes_remain_bounded_within_the_configured_total(int total, int ordinary, int live)
    {
        await using var host = await HeldSupervisor.StartAsync();
        using var client = host.Client(total);
        var held = Enumerable.Range(0, ordinary).Select(i => client.GetAsync($"api/world/chunks?id=bulk{i}&hold=1", TimeSpan.FromSeconds(15), CancellationToken.None))
            .Concat(Enumerable.Range(0, live).Select(i => client.GetAsync($"api/world/entities?id=live{i}&hold=1", TimeSpan.FromSeconds(15), CancellationToken.None))).ToArray();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, ordinary).Select(i => host.Seen($"bulk{i}"))
                .Concat(Enumerable.Range(0, live).Select(i => host.Seen($"live{i}"))));
            Assert.Equal(total, host.MaxActive);
            // Saturating each lane cannot borrow another connection and exceed the supervisor's configured budget.
            foreach (var path in new[] { "api/world/index?id=extra-bulk", "api/world/blocks?id=extra-live" })
                await Assert.ThrowsAsync<SupervisorUnavailableException>(() => client.GetAsync(path, TimeSpan.FromMilliseconds(150), CancellationToken.None));
            Assert.False(host.HasSeen("extra-bulk"));
            Assert.False(host.HasSeen("extra-live"));
            Assert.Equal(total, host.MaxActive);
        }
        finally { host.Release(); await Task.WhenAll(held); }
    }

    [Theory]
    [InlineData("POST", "api/story/colonies/story-1/designations", true)]
    [InlineData("POST", "/api/story/colonies/s/storage", true)]
    [InlineData("POST", "api/story/colonies/s/bills?x=1", true)]
    [InlineData("GET", "api/world/containers?dim=0&x=6&y=74&z=-4", true)]
    [InlineData("GET", "api/world/containers?dim=0&x0=0&y0=60&z0=0&x1=31&y1=80&z1=31", false)] // the badge poll's box
    [InlineData("GET", "api/world/containers?dim=0&x=6&y=74&z=-4&x0=0", false)]
    [InlineData("GET", "api/world/containers", false)]
    [InlineData("GET", "api/viewer/bodies/maid_chan/inventory", true)]
    [InlineData("GET", "api/story/recipes?workbench=crafting", true)]
    [InlineData("POST", "api/story/colonies", false)]             // founding a colony
    [InlineData("POST", "api/story/time", false)]
    [InlineData("POST", "api/story/directives", false)]
    [InlineData("GET", "api/story/colonies/story-1", false)]      // a story read: its own lane at 7
    [InlineData("GET", "api/story/colonies/story-1/architect", false)]
    [InlineData("POST", "api/bodies/maid_chan/control/input", false)] // control input keeps the live lane
    [InlineData("GET", "api/world/chunks?cols=1,2", false)]
    [InlineData("GET", "api/viewer/now?bot=main", false)]
    public void Story_commands_one_chest_inventories_and_recipes_use_the_interactive_lane(string method, string path, bool expected) =>
        Assert.Equal(expected, SupervisorClient.IsInteractive(new HttpMethod(method), path));

    [Theory]
    [InlineData("GET", "api/story", true)]
    [InlineData("GET", "/api/story?x=1", true)]
    [InlineData("GET", "api/story/colonies/story-1", true)]
    [InlineData("GET", "api/story/colonies/story-1?since=40", true)]
    [InlineData("GET", "api/story/colonies/story-1/architect", true)]
    [InlineData("POST", "api/story/colonies/story-1/designations", false)]
    [InlineData("GET", "api/story/recipes", false)]
    [InlineData("GET", "api/story/directives", false)]
    [InlineData("GET", "api/story/colonies/", false)]
    [InlineData("GET", "api/world/index", false)]
    public void The_story_reads_that_wait_on_the_colony_are_told_apart(string method, string path, bool expected) =>
        Assert.Equal(expected, SupervisorClient.IsStoryRead(new HttpMethod(method), path));

    [Theory]
    [InlineData(0, 0, 0, 0, 1)]
    [InlineData(1, 0, 0, 0, 1)]
    [InlineData(2, 1, 0, 0, 1)]
    [InlineData(3, 1, 0, 0, 2)]
    [InlineData(4, 2, 0, 0, 2)]
    [InlineData(5, 2, 0, 0, 3)]
    [InlineData(6, 2, 2, 0, 2)]
    [InlineData(7, 2, 2, 1, 2)]
    [InlineData(12, 2, 2, 1, 2)] // clamped: the supervisor serves 8 at once
    public void The_lanes_split_the_configured_total_and_never_exceed_it(int configured, int live, int interactive, int storyRead, int ordinary)
    {
        var lanes = SupervisorClient.Lanes(configured);
        Assert.Equal(new SupervisorClient.LaneSplit(live, interactive, storyRead, ordinary), lanes);
        Assert.Equal(Math.Clamp(configured, 1, 7), lanes.Total);
    }

    [Fact]
    public void The_default_total_has_an_interactive_lane() =>
        Assert.Equal(new SupervisorClient.LaneSplit(2, 2, 0, 2), SupervisorClient.Lanes(new BridgeOptions().SupervisorConnections));

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task A_story_command_and_a_chest_answer_while_every_ordinary_connection_is_held(int total)
    {
        await using var host = await HeldSupervisor.StartAsync();
        using var client = host.Client(total);
        var bulk = Enumerable.Range(0, 2).Select(i => client.GetAsync($"api/world/chunks?id=bulk{i}&hold=1", TimeSpan.FromSeconds(15), CancellationToken.None)).ToArray();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 2).Select(i => host.Seen($"bulk{i}")));
            using (var command = await client.SendAsync(HttpMethod.Post, "api/story/colonies/s/storage?id=command", new StringContent("{}"), TimeSpan.FromSeconds(3), stream: false, CancellationToken.None))
                Assert.Equal(200, (int)command.StatusCode);
            foreach (var path in new[] { "api/world/containers?dim=0&x=6&y=74&z=-4&id=chest", "api/viewer/bodies/maid_chan/inventory?id=inventory", "api/story/recipes?id=recipes" })
                Assert.Equal(200, (await client.GetAsync(path, TimeSpan.FromSeconds(3), CancellationToken.None)).Status);
            Assert.All(bulk, b => Assert.False(b.IsCompleted));
            // The box read is a badge poll: it stays ordinary, behind the chunks.
            await Assert.ThrowsAsync<SupervisorUnavailableException>(() => client.GetAsync("api/world/containers?dim=0&x0=0&z0=0&x1=31&z1=31&id=box", TimeSpan.FromMilliseconds(150), CancellationToken.None));
            Assert.False(host.HasSeen("box"));
            Assert.True(host.MaxActive <= total);
        }
        finally { host.Release(); await Task.WhenAll(bulk); }
    }

    [Fact]
    public void A_story_command_has_time_for_its_queue_the_slot_and_the_colony_lock_under_nests_fifteen_seconds()
    {
        var command = SupervisorProxy.TimeoutFor("story/colonies/story-1/storage");
        Assert.Equal(SupervisorProxy.StoryCommandTimeout, command);
        // Queue 4 s + the supervisor's slot 2 s + SupervisorStoryRoutes.LockBudget 6 s + a second for the handler.
        using var client = new SupervisorClient(BridgeConfig.ForTests(new BridgeOptions()));
        var queue = client.CommandQueueBudget;
        Assert.True(queue + TimeSpan.FromSeconds(2 + 6 + 1) < command, $"{command}");
        Assert.True(command < TimeSpan.FromSeconds(15), $"{command}"); // web/packages/contracts bridge.timeoutMs default
        // Reads keep 8 s (the Architect has a command's shape but is a GET).
        Assert.Equal(TimeSpan.FromSeconds(8), SupervisorProxy.TimeoutFor("story/colonies/story-1"));
        Assert.Equal(TimeSpan.FromSeconds(8), SupervisorProxy.TimeoutFor("story/colonies/story-1/architect"));
        Assert.Equal(TimeSpan.FromSeconds(8), SupervisorProxy.TimeoutFor("story"));
    }

    [Fact]
    public async Task A_story_command_that_cannot_get_its_turn_is_refused_busy_without_being_sent()
    {
        await using var host = await HeldSupervisor.StartAsync();
        using var client = host.Client(6);
        client.CommandQueueBudget = TimeSpan.FromMilliseconds(200);
        var held = Enumerable.Range(0, 2).Select(i => client.SendAsync(HttpMethod.Post, $"api/story/colonies/s/storage?id=held{i}&hold=1",
            new StringContent("{}"), TimeSpan.FromSeconds(15), stream: false, CancellationToken.None)).ToArray();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 2).Select(i => host.Seen($"held{i}")));
            // The third has a long timeout of its own: it is answered busy at its queue budget, and the bot never saw it.
            var busy = await Assert.ThrowsAsync<SupervisorBusyException>(() =>
                client.SendAsync(HttpMethod.Post, "api/story/colonies/s/beds?id=third", new StringContent("{}"), TimeSpan.FromSeconds(15), stream: false, CancellationToken.None));
            Assert.Contains("busy", busy.Message);
            Assert.False(host.HasSeen("third"));
            // Through the proxy it is a definite refusal: 503 `busy`, which the page reads as "not saved", never as unknown.
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            var body = new MemoryStream();
            ctx.Response.Body = body;
            await SupervisorProxy.ForwardAsync(ctx, client, HttpMethod.Post, "story/colonies/s/beds", "api/story/colonies/s/beds?id=proxied", new StringContent("{}"));
            Assert.Equal(503, ctx.Response.StatusCode);
            Assert.Contains("\"code\":\"busy\"", Encoding.UTF8.GetString(body.ToArray()));
            Assert.False(host.HasSeen("proxied"));
        }
        finally { host.Release(); foreach (var r in await Task.WhenAll(held)) r.Dispose(); }
        // The turns are given back: the next command goes through.
        using var next = await client.SendAsync(HttpMethod.Post, "api/story/colonies/s/beds?id=next", new StringContent("{}"), TimeSpan.FromSeconds(3), stream: false, CancellationToken.None);
        Assert.Equal(200, (int)next.StatusCode);
    }

    [Fact]
    public async Task With_four_connections_a_story_command_waits_on_the_ordinary_lane_as_before()
    {
        await using var host = await HeldSupervisor.StartAsync();
        using var client = host.Client(4);
        var bulk = Enumerable.Range(0, 2).Select(i => client.GetAsync($"api/world/chunks?id=bulk{i}&hold=1", TimeSpan.FromSeconds(15), CancellationToken.None)).ToArray();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 2).Select(i => host.Seen($"bulk{i}")));
            await Assert.ThrowsAsync<SupervisorUnavailableException>(() =>
                client.SendAsync(HttpMethod.Post, "api/story/colonies/s/storage?id=command", new StringContent("{}"), TimeSpan.FromMilliseconds(150), stream: false, CancellationToken.None));
            Assert.False(host.HasSeen("command"));
            Assert.Equal(2, host.MaxActive);
        }
        finally { host.Release(); await Task.WhenAll(bulk); }
    }

    [Fact]
    public async Task With_seven_connections_world_reads_answer_while_a_story_read_waits_on_the_colony()
    {
        await using var host = await HeldSupervisor.StartAsync();
        using var client = host.Client(7);
        var colony = client.GetAsync("api/story/colonies/story-1?since=4&id=state&hold=1", TimeSpan.FromSeconds(15), CancellationToken.None);
        try
        {
            await host.Seen("state");
            foreach (var path in new[] { "api/world/chunks?cols=1,2&id=chunks", "api/world/index?id=index" })
                Assert.Equal(200, (await client.GetAsync(path, TimeSpan.FromSeconds(3), CancellationToken.None)).Status);
            Assert.False(colony.IsCompleted);
            // The story reads have one connection: a second one waits for the first, never for a chunk download's connection.
            await Assert.ThrowsAsync<SupervisorUnavailableException>(() => client.GetAsync("api/story?id=list", TimeSpan.FromMilliseconds(150), CancellationToken.None));
            Assert.False(host.HasSeen("list"));
        }
        finally { host.Release(); await colony; }
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Every_lane_saturated_stays_within_the_configured_total(int total)
    {
        await using var host = await HeldSupervisor.StartAsync();
        using var client = host.Client(total);
        var lanes = SupervisorClient.Lanes(total);
        var holders = new List<(string Id, string Path)>();
        for (int i = 0; i < lanes.Ordinary; i++) holders.Add(($"bulk{i}", "api/world/chunks"));
        for (int i = 0; i < lanes.Live; i++) holders.Add(($"live{i}", "api/world/entities"));
        for (int i = 0; i < lanes.Interactive; i++) holders.Add(($"chest{i}", "api/world/containers?dim=0&x=1&y=64&z=2"));
        for (int i = 0; i < lanes.StoryRead; i++) holders.Add(($"state{i}", "api/story/colonies/story-1"));
        string With(string path, string id, bool hold) => path + (path.Contains('?') ? "&" : "?") + "id=" + id + (hold ? "&hold=1" : "");
        var held = holders.Select(h => client.GetAsync(With(h.Path, h.Id, true), TimeSpan.FromSeconds(15), CancellationToken.None)).ToArray();
        try
        {
            await Task.WhenAll(holders.Select(h => host.Seen(h.Id)));
            Assert.Equal(total, host.MaxActive);
            // No lane borrows another's connection: one more request on any lane waits.
            var extra = new List<string> { With("api/world/index", "extra-bulk", false), With("api/world/blocks", "extra-live", false), With("api/story/recipes", "extra-chest", false) };
            if (lanes.StoryRead > 0) extra.Add(With("api/story", "extra-state", false));
            foreach (var path in extra)
                await Assert.ThrowsAsync<SupervisorUnavailableException>(() => client.GetAsync(path, TimeSpan.FromMilliseconds(150), CancellationToken.None));
            Assert.All(new[] { "extra-bulk", "extra-live", "extra-chest", "extra-state" }, id => Assert.False(host.HasSeen(id)));
            Assert.Equal(total, host.MaxActive);
        }
        finally { host.Release(); await Task.WhenAll(held); }
    }

    [Fact]
    public async Task A_single_configured_connection_stays_shared()
    {
        await using var host = await HeldSupervisor.StartAsync();
        using var client = host.Client(1);
        var held = client.GetAsync("api/world/chunks?id=held&hold=1", TimeSpan.FromSeconds(15), CancellationToken.None);
        try
        {
            await host.Seen("held");
            await Assert.ThrowsAsync<SupervisorUnavailableException>(() => client.GetAsync("api/world/entities?id=queued", TimeSpan.FromMilliseconds(150), CancellationToken.None));
            Assert.False(host.HasSeen("queued"));
            Assert.Equal(1, host.MaxActive);
        }
        finally { host.Release(); await held; }
        Assert.Equal(200, (await client.GetAsync("api/world/entities", TimeSpan.FromSeconds(3), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_streaming_response_keeps_its_lane_until_disposed_and_does_not_block_the_live_lane()
    {
        await using var host = await HeldSupervisor.StartAsync();
        using var client = host.Client(2);
        using var held = await client.SendAsync(HttpMethod.Get, "api/world/chunks?id=stream&stream=1", null, TimeSpan.FromSeconds(3), stream: true, CancellationToken.None);
        await host.Seen("stream");
        Assert.Equal(200, (int)held.StatusCode);
        await Assert.ThrowsAsync<SupervisorUnavailableException>(() => client.GetAsync("api/world/index?id=queued", TimeSpan.FromMilliseconds(150), CancellationToken.None));
        Assert.False(host.HasSeen("queued"));
        Assert.Equal(200, (await client.GetAsync("api/world/entities", TimeSpan.FromSeconds(3), CancellationToken.None)).Status);
        held.Dispose();
        Assert.Equal(200, (await client.GetAsync("api/world/index?id=after-dispose", TimeSpan.FromSeconds(3), CancellationToken.None)).Status);
        await host.Seen("after-dispose");
    }

    /// <summary>Real HTTP/1.1 sockets, with response bodies held open so transport pooling is tested rather than mocked.</summary>
    private sealed class HeldSupervisor(WebApplication app, string url) : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _seen = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active, _maxActive;
        public int MaxActive => Volatile.Read(ref _maxActive);
        public bool HasSeen(string id) => _seen.TryGetValue(id, out var seen) && seen.Task.IsCompleted;
        public Task Seen(string id) => _seen.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Release() => _release.TrySetResult();
        public SupervisorClient Client(int connections) => new(BridgeConfig.ForTests(new BridgeOptions { SupervisorUrl = url, SupervisorConnections = connections }));

        public static async Task<HeldSupervisor> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            HeldSupervisor? host = null;
            app.Run(ctx => host!.Answer(ctx));
            await app.StartAsync();
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return host = new HeldSupervisor(app, url);
        }

        private async Task Answer(HttpContext ctx)
        {
            int active = Interlocked.Increment(ref _active), previous;
            do { previous = Volatile.Read(ref _maxActive); if (active <= previous) break; }
            while (Interlocked.CompareExchange(ref _maxActive, active, previous) != previous);
            if (ctx.Request.Query["id"].FirstOrDefault() is { } id)
                _seen.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            try
            {
                if (ctx.Request.Query["stream"] == "1")
                {
                    // Larger than the transport's drain limit: Dispose closes this held body immediately.
                    ctx.Response.ContentLength = 4 * 1024 * 1024;
                    await ctx.Response.Body.WriteAsync(new byte[] { 1 }, ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestAborted);
                }
                if (ctx.Request.Query["hold"] == "1") await _release.Task.WaitAsync(ctx.RequestAborted);
                await ctx.Response.WriteAsync("{}", ctx.RequestAborted);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
            finally { Interlocked.Decrement(ref _active); }
        }

        public async ValueTask DisposeAsync() { Release(); await app.DisposeAsync(); }
    }
}
