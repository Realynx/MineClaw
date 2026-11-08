using System.Collections.Concurrent;
using System.Net;
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
