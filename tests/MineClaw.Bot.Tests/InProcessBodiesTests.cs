using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests;

/// <summary>
/// One brain, many bodies, step 2, end to end: main and two clone bodies in ONE process, each a real MinecraftClient on its own
/// (fake) server connection, built by <see cref="BodyFactory.CreateClone"/> and run by the registry. Each acts through its
/// own activity slot (BodyScope pushed) on its own connection, at the same time, with no cross-talk; a clone's chat is dropped
/// except whispers to it (tagged with the body), and a clone never sends chat.
/// </summary>
public class InProcessBodiesTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, (int X, int Y, int Z)> Target = new()
    {
        ["maid_chan"] = (7, 64, 7), ["maid_chan_67"] = (9, 64, 9), ["maid_chan_69"] = (7, 64, 9),
    };

    private static FakeServer Server()
    {
        var s = new FakeServer();
        foreach (var t in Target.Values) s.Blocks[(t.X, t.Y, t.Z)] = Blocks.Dirt; // every server has all three: a wrong dig shows
        s.SpawnAt = (8.5, 64, 8.5);
        return s;
    }

    private static ClientOptions Opts(string name, FakeServer s) => new()
    {
        Username = name, TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero, RespawnDelay = TimeSpan.FromMilliseconds(10),
        ConnectionFactory = s.CreateClientConnection, HostileAlertRadius = 0, AutoReconnect = false,
    };

    private static async Task<bool> Until(Func<bool> cond, int ms = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(5); }
        return cond();
    }

    /// <summary>The registry's host over the fake servers (the App's InProcBodies does the same over the real server).</summary>
    private sealed class ServerHost(BrainContext brain, Dictionary<string, FakeServer> servers, MinecraftClient main, WorldMemoryFlusher flusher) : IBodyHost
    {
        public readonly List<Body> Built = new();
        public readonly List<GameEvent> Posted = new();

        public Body Create(string name)
        {
            var body = BodyFactory.CreateClone(brain, new MinecraftClient(Opts(name, servers[name])), null, flusher);
            lock (Built) Built.Add(body);
            return body;
        }

        public Task ConnectAsync(IBody body, CancellationToken ct) => ClientBodySessions.ConnectAsync(body.Client!, ct);
        public Task<BodyExit> RunAsync(IBody body, CancellationToken ct) => ClientBodySessions.RunAsync(body.Client!, ct);
        public Task DisconnectAsync(IBody body, string reason) => ClientBodySessions.DisconnectAsync(body.Client!, reason);
        public BodySlots Slots() => new(main.World.MaxPlayers, main.World.Entities.OnlinePlayers);
        public BodySlotDecision CanSpawn(int pending) => new(true, "test");
        public IReadOnlyList<string> MustYield(IReadOnlyList<BodyProgress> running) => [];
        public Task<BodyDeposit> DepositAsync(IBody body, TimeSpan timeout, CancellationToken ct) => Task.FromResult(new BodyDeposit(true, 0, "inventory empty"));
        public void Post(GameEvent e) { lock (Posted) Posted.Add(e); }
    }

    [Fact]
    public async Task Main_and_two_clones_act_on_their_own_connections_at_once_with_no_cross_talk()
    {
        var servers = Target.Keys.ToDictionary(n => n, _ => Server(), StringComparer.OrdinalIgnoreCase);
        var mainClient = new MinecraftClient(Opts("maid_chan", servers["maid_chan"]));
        using var flusher = new WorldMemoryFlusher();
        ServerHost? host = null;
        BotHost? h = null;
        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await mainClient.ConnectAsync(cts.Token);
            Assert.True(await Until(() => mainClient.World.Player.OnGround));
            h = BotHost.Create(new MinecraftGameClient(mainClient), TestBot.Options(), null, new NoLlm(), persist: false);
            var rt = h.Runtime;
            var reg = h.Bodies!;

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalMemory(true);
            host = new ServerHost(h.Brain!, servers, mainClient, flusher);
            reg.Configure(host, ["maid_chan_67", "maid_chan_69"], new BodiesOptions { LaunchGapSeconds = 0, YieldCheckSeconds = 0 }, timers: false);
            var b67 = reg.Spawn("67").Body!;
            var b69 = reg.Spawn("69").Body!;
            Assert.True(await reg.WaitOnlineAsync(b67, TimeSpan.FromSeconds(10)), "67 online");
            Assert.True(await reg.WaitOnlineAsync(b69, TimeSpan.FromSeconds(10)), "69 online");
            Assert.True(await Until(() => b67.Adapter.World.Player.OnGround && b69.Adapter.World.Player.OnGround));
            Assert.Equal(3, reg.Count);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long after = GC.GetTotalMemory(true);
            output.WriteLine($"managed heap per in-process clone body: {(after - before) / 2.0 / 1024 / 1024:0.0} MB (before {before / 1024 / 1024} MB, after {after / 1024 / 1024} MB)");

            // All three dig at once, each through its own activity slot: the slot pushes its body, the tool acts on that body.
            Assert.True(rt.Tools.TryGet("dig", out var dig));
            var runs = new[] { (IBody)h.Body!, b67, b69 }.Select(b =>
            {
                var t = Target[b.Name];
                return b.Activities.RunAsync([(dig!, JsonSerializer.SerializeToElement(new { at = $"{t.X},{t.Y},{t.Z}" }))], TimeSpan.FromSeconds(20), CancellationToken.None);
            }).ToList();
            var results = await Task.WhenAll(runs);
            foreach (var r in results) Assert.True(r[0].Ok, r[0].Text);

            foreach (var (name, srv) in servers)
            {
                var dug = srv.Dug.Select(d => (d.X, d.Y, d.Z)).Distinct().ToList();
                Assert.Equal([Target[name]], dug); // only this body's own block, on its own connection
            }
            Assert.Equal(0, h.Brain!.Tools.MissingScopeCount);
            // A background job's [task] event carries its body (nobody waits on it: it reports through the queue).
            var t67 = Target["maid_chan_67"];
            _ = b67.Activities.RunAsync([(dig!, JsonSerializer.SerializeToElement(new { at = $"{t67.X},{t67.Y},{t67.Z}" }))], TimeSpan.Zero, CancellationToken.None);
            Assert.True(await Until(() => rt.Events.Snapshot().Any(e => e.Body == "67" && e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed), 5000),
                "67's task event: " + string.Join(" | ", rt.Events.Snapshot().Select(e => e.Format())));

            // Chat: 67's client drops public chat (main's router hears main's copy) and keeps whispers to 67, for the brain.
            var posted = new List<GameEvent>();
            rt.Events.Posted += e => { lock (posted) posted.Add(e); };
            servers["maid_chan_67"].Send(new ChatPacket("<PoofimaFox> hello from 67's connection only"));
            servers["maid_chan_67"].Send(new ChatPacket("PoofimaFox whispers to you: 67 come here"));
            Assert.True(await Until(() => { lock (posted) return posted.Any(e => e.Kind == EventKind.Whisper); }, 3000), "whisper reached the brain");
            await Task.Delay(200);
            lock (posted)
            {
                var w = Assert.Single(posted, e => e.Kind == EventKind.Whisper);
                Assert.Equal("67", w.Body);
                Assert.Equal("PoofimaFox", w.Player);
                Assert.Equal("67 come here", w.Text);
                Assert.DoesNotContain(posted, e => e.Text.Contains("hello from 67's connection only"));
            }
            // Only main's account sends chat.
            int sentBefore = servers["maid_chan_67"].Got<ChatPacket>().Count();
            var said = await b67.Adapter.SendChatAsync("hi from 67");
            Assert.False(said.Ok);
            await Task.Delay(100);
            Assert.Equal(sentBefore, servers["maid_chan_67"].Got<ChatPacket>().Count());

            // Despawn: 69 logs out, 67 and main stay on their connections.
            var bye = await reg.DespawnAsync("69", "test over");
            Assert.StartsWith("ok: 69 logged out", bye);
            Assert.True(await Until(() => !b69.Adapter.IsPlaying));
            Assert.True(b67.Adapter.IsPlaying && mainClient.IsPlaying);
            Assert.Equal(2, reg.Count);
        }
        finally
        {
            if (h?.Bodies is { } reg) await reg.StopAllAsync();
            if (host != null)
                foreach (var b in host.Built)
                {
                    BodyFactory.DisposeClone(b, flusher);
                    await b.Client!.DisposeAsync();
                }
            h?.Dispose();
            await mainClient.DisposeAsync();
            foreach (var s in servers.Values) s.Dispose();
        }
    }
}
