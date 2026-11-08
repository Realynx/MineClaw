using System.Collections.Concurrent;
using MineClaw.Agent.Rag;
using MineClaw.Bot.WorldIndex;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Client.Versioning;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Bot.Tests.WorldIndex;

public sealed class ContainerAttributionTests
{
    private static readonly BlockPos West = new(6, 64, 8), East = new(10, 64, 8);

    private static void Chest(FakeServer server, BlockPos at, int item)
    {
        server.Blocks[(at.X, at.Y, at.Z)] = Blocks.Chest;
        var slots = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
        slots[0] = new ItemStack((short)item, 5, 0);
        server.Chests[(at.X, at.Y, at.Z)] = slots;
    }

    private static MinecraftClient Client(FakeServer server, bool modern) => new(new ClientOptions
    {
        Username = "maid_chan", ConnectionFactory = server.CreateConnection,
        Profile = modern ? GameVersionProfiles.Java1122 : GameVersionProfiles.Beta181,
        TickInterval = TimeSpan.FromMilliseconds(5), HostileAlertRadius = 0,
    });

    private static async Task Ready(MinecraftClient client, CancellationToken ct)
    {
        await client.ConnectAsync(ct);
        while (!client.World.Player.OnGround || client.World.GetBlock(East) != Blocks.Chest) await Task.Delay(5, ct);
    }

    private static IReadOnlyList<ItemCount> Contents(IndexFixture f, BlockPos at)
    {
        var row = f.Row("chest", ObservationStore.KeyFor(at.X, at.Y, at.Z));
        Assert.NotNull(row);
        return f.Store.Contents(row.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_window_attribution_precedes_observers_and_never_follows_a_later_look(bool modern)
    {
        using var server = new FakeServer(modern);
        Chest(server, West, Items.WoodenHoe);
        Chest(server, East, Items.Seeds);
        await using var client = Client(server, modern);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Ready(client, ct.Token);
        using var fixture = new IndexFixture();
        var atOpen = new ConcurrentQueue<BlockPos?>();
        // A view/automation observer changes the look before the indexer samples it. The already-attributed packet must win.
        client.World.Inventory.WindowOpened += w =>
        {
            atOpen.Enqueue(client.OpenedContainerPosition(w.Id));
            var p = client.World.Player.Position;
            client.World.Player.ServerTeleport(p, p.Y + 1.62, 90, 20, true);
        };
        using var indexer = new WorldIndexer(client.World, fixture.Sink, new ClientIndexFeed(client), fixture.Options).Attach();
        foreach (var at in new[] { West, East })
        {
            var opened = await client.OpenContainerAsync(at.X, at.Y, at.Z, ct.Token);
            Assert.True(opened.Ok, opened.Message);
            await client.CloseWindowAsync(ct.Token);
            indexer.PumpAll();
        }
        Assert.Equal(new BlockPos?[] { West, East }, atOpen.ToArray());
        Assert.Equal(Items.WoodenHoe, Assert.Single(Contents(fixture, West)).ItemId);
        Assert.Equal(Items.Seeds, Assert.Single(Contents(fixture, East)).ItemId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unsolicited_reused_window_id_cannot_overwrite_the_previous_or_nearest_chest(bool modern)
    {
        using var server = new FakeServer(modern);
        Chest(server, West, Items.WoodenHoe);
        Chest(server, East, Items.Seeds);
        await using var client = Client(server, modern);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Ready(client, ct.Token);
        using var fixture = new IndexFixture();
        using var indexer = new WorldIndexer(client.World, fixture.Sink, new ClientIndexFeed(client), fixture.Options).Attach();
        var opened = await client.OpenContainerAsync(East.X, East.Y, East.Z, ct.Token);
        Assert.True(opened.Ok, opened.Message);
        int previousId = client.World.Inventory.OpenWindow!.Id;
        await client.CloseWindowAsync(ct.Token);
        indexer.PumpAll();
        var unknown = new TaskCompletionSource<BlockPos?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.World.Inventory.WindowOpened += w => unknown.TrySetResult(client.OpenedContainerPosition(w.Id));
        // Plugins can show a chest-shaped menu without a block click, reusing a vanilla window ID.
        server.Send(new OpenWindowPacket { WindowId = (sbyte)previousId, InventoryType = 0, Title = "Menu", SlotCount = 27 });
        Assert.Null(await unknown.Task.WaitAsync(ct.Token));
        var slots = Enumerable.Repeat(ItemStack.Empty, 63).ToArray();
        slots[0] = new ItemStack(Items.Diamond, 40, 0);
        server.Send(new WindowItemsPacket { WindowId = (sbyte)previousId, Items = slots });
        while (client.World.Inventory.OpenWindow?.Get(0).Id != Items.Diamond) await Task.Delay(5, ct.Token);
        await client.CloseWindowAsync(ct.Token);
        indexer.PumpAll();
        Assert.Equal(Items.Seeds, Assert.Single(Contents(fixture, East)).ItemId);
        Assert.Empty(Contents(fixture, West));
    }
}
