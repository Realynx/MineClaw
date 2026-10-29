using MineClaw.Bot.Ledger;
using MineClaw.Bot.Memory;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 2026-10-29 02:01:48 (1.12.2): main placed the log chest at 152,63,288, and the ledger said "-1 Chest: vanished without
/// an action". 1.12.2's processTryUseItemOnBlock answers with the Block Changes only; the Set Slot that takes the block from the
/// hand comes from the next detectAndSendChanges, a tick later (the fake server now models that), after the place action had
/// ended. The placement must be booked as placed, recorded as ours, and treated as ours by protection, on both versions.
/// </summary>
public class Release1122PlacementLedgerTests
{
    private static async Task<(MinecraftClient C, FakeServer S)> StartAsync(Action<FakeServer> setup, bool release1122)
    {
        var server = new FakeServer(release1122);
        setup(server);
        var o = new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), HostileAlertRadius = 0,
            ConnectionFactory = release1122 ? server.CreateConnection : server.CreateClientConnection,
        };
        if (release1122) o = o with { Profile = MineClaw.Client.Versioning.GameVersionProfiles.Java1122 };
        var client = new MinecraftClient(o);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);
        await Until(() => client.World.Player.OnGround, 3000);
        return (client, server);
    }

    private static async Task<bool> Until(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(5);
        }
        return cond();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_placed_chest_is_booked_as_placed_and_recorded_as_ours(bool release1122)
    {
        var (c, s) = await StartAsync(srv =>
        {
            srv.Inventory[36] = new ItemStack(Blocks.Chest, 1, 0);
            srv.Inventory[37] = new ItemStack(Blocks.Planks, 4, 0);
            srv.ConsumeOnPlace = true;
        }, release1122);
        await using var _ = c;
        using var __ = s;
        var ledger = InventoryLedgerTools.Create(c, null);
        using var pump = ledger.Pump;
        ledger.Observe(InventoryLedgerTools.Counts(c.World.Inventory)); // the baseline (live: the ledger exists before the first window items)

        var place = await c.PlaceBlockAsync(9, 64, 8, Blocks.Chest);
        Assert.True(place.Ok, place.Message);
        Assert.True(await Until(() => c.World.Inventory.CountItem(Blocks.Chest) == 0, 2000));
        Assert.True(await Until(() => ledger.Entries.Any(e => e.ItemId == Blocks.Chest), 3000));
        var chest = ledger.Entries.Single(e => e.ItemId == Blocks.Chest);
        Assert.Equal(-1, chest.Delta);
        Assert.Equal("placed as a block", chest.Cause);

        // The placed-block log (protection rule 1, build tracking) holds it.
        var pos = new BlockPos(9, 64, 8);
        Assert.True(await Until(() => c.World.Placed.IsPlaced(pos, c.World.Dimension, Blocks.Chest), 2000));
        Assert.Equal("maid_chan", c.World.Placed.Get(pos, c.World.Dimension)!.Bot);

        // A non-container block placed inside a soft-protected area is ours to take back (containers keep their protection:
        // rule 1 never unlocks a chest, only an exact permit does).
        var planks = await c.PlaceBlockAsync(10, 64, 8, Blocks.Planks);
        Assert.True(planks.Ok, planks.Message);
        var cell = new BlockPos(10, 64, 8);
        Assert.True(await Until(() => c.World.Placed.IsPlaced(cell, c.World.Dimension, Blocks.Planks), 2000));
        var protection = new Protection(new BotOptions(), NoMemory.Instance, NoMemory.Instance)
        {
            IndexerKind = _ => "build", Placed = c.World.Placed, Dimension = () => c.World.Dimension,
        };
        Assert.False(protection.Check(cell, Blocks.Planks).Protected);
        Assert.True(protection.Check(new BlockPos(11, 64, 8), Blocks.Planks).Protected); // not ours: still protected
        Assert.True(await Until(() => ledger.Entries.Any(e => e.ItemId == Blocks.Planks), 3000));
        Assert.Equal("placed as a block", ledger.Entries.Single(e => e.ItemId == Blocks.Planks).Cause);
    }
}
