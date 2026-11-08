using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using MineClaw.App.Supervisor;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.App.Tests;

public sealed class StoryStorageSignGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Main_existing_and_future_bodies_use_published_dimension_and_double_half_rules(bool lateRegistry)
    {
        await using var main = new MinecraftClient(new ClientOptions { Username = "main" });
        var factory = new Bodies();
        var registry = new BodyRegistry(factory.Wrap(main, true));
        registry.Configure(factory, ["pawn_1", "pawn_2"], new BodiesOptions { LaunchGapSeconds = 0 }, timers: false);
        Assert.True(registry.Spawn("pawn_1").Ok);
        var first = ((Body)registry.Find("pawn_1")!).Client!;
        BodyRegistry? available = lateRegistry ? null : registry;
        var host = new StoryHost(null, () => available, () => main.World, Beta181GameData.Instance, () => null);
        host.AttachStorageSignGuards(main);
        Assert.NotNull(main.StorageSignRules);
        available = registry;
        var chest = new P3(15, 64, 8);
        var half = new P3(16, 64, 8);
        var sign = new P3(16, 64, 7);
        host.PublishStorageSignRules("colony", 0, [new(chest, half, [new(sign, ["wood", "", "", ""])], ["wood"], [])]);
        try
        {
            Assert.NotNull(first.StorageSignRules);
            Assert.True(registry.Spawn("pawn_2").Ok);
            var second = ((Body)registry.Find("pawn_2")!).Client!;
            Assert.NotNull(second.StorageSignRules);
            var at = new BlockPos(15, 64, 8);
            var other = new BlockPos(16, 64, 8);
            foreach (var client in new[] { main, first, second })
            {
                Assert.NotNull(client.StorageSignRules!(0, at));
                Assert.Same(main.StorageSignRules!(0, at), client.StorageSignRules!(0, other));
                Assert.Null(client.StorageSignRules!(-1, at));
            }
            host.PublishStorageSignRules("colony", -1, [new(chest, null, [new(sign, ["stone", "", "", ""])], ["stone_stone"], [])]);
            Assert.NotNull(second.StorageSignRules!(-1, at));
            host.PublishStorageSignRules("colony", 0, []);
            Assert.Null(first.StorageSignRules!(0, at));
            Assert.NotNull(first.StorageSignRules!(-1, at));
        }
        finally
        {
            await registry.StopAllAsync();
            foreach (var client in factory.Clients) await client.DisposeAsync();
        }
    }

    private sealed class Bodies : IBodyHost
    {
        public List<MinecraftClient> Clients { get; } = [];
        public Body Wrap(MinecraftClient client, bool main = false)
        {
            var game = new MinecraftGameClient(client);
            return new Body(client.Options.Username, main, game, game, new ActivityManager(new ToolRegistry(), new EventQueue()));
        }
        public Body Create(string name)
        {
            var client = new MinecraftClient(new ClientOptions { Username = name });
            Clients.Add(client);
            return Wrap(client);
        }
        public Task ConnectAsync(IBody body, CancellationToken ct) => Task.CompletedTask;
        public async Task<BodyExit> RunAsync(IBody body, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return BodyExit.Stopped();
        }
        public Task DisconnectAsync(IBody body, string reason) => Task.CompletedTask;
        public BodySlots Slots() => new(100, []);
        public BodySlotDecision CanSpawn(int pending) => new(true, "test");
        public IReadOnlyList<string> MustYield(IReadOnlyList<BodyProgress> running) => [];
        public Task<BodyDeposit> DepositAsync(IBody body, TimeSpan timeout, CancellationToken ct) => Task.FromResult(BodyDeposit.Nothing(0, "empty"));
        public void Post(GameEvent e) { }
    }
}
