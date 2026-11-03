using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Multi-version P2 end to end: <see cref="MinecraftClient"/> against <see cref="FakeServer"/> over the 1.12.2 wire (its
/// server-side codec and its vanilla-like window model), and the same game tests on both versions.
/// </summary>
public class Release1122PlayTests
{
    public static TheoryData<bool> Versions => new() { false, true };

    private static Task<(MinecraftClient C, FakeServer S)> Start(bool release1122, Action<FakeServer>? setup = null) =>
        Harness.StartAsync(setup, release1122: release1122);

    private static void AssertSameAsServer(MinecraftClient c, FakeServer s)
    {
        var local = c.World.Inventory.PlayerSlots();
        for (int i = 9; i < 45; i++) Assert.True(local[i].Equals(s.Inventory[i]), $"slot {i}: local {local[i]} vs server {s.Inventory[i]}");
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Dig_place_and_a_chest_on_either_version(bool release1122)
    {
        var (c, s) = await Start(release1122, srv =>
        {
            srv.Blocks[(9, 64, 8)] = Blocks.Dirt;
            srv.Blocks[(8, 64, 10)] = Blocks.Chest;
            var chest = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
            chest[4] = Harness.Stack(Blocks.Planks, 5);
            srv.Chests[(8, 64, 10)] = chest;
            srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 5);
            srv.ConsumeOnPlace = true;
        });
        await using var _ = c;
        using var __ = s;
        Assert.Equal(release1122 ? 46 : 45, c.World.Inventory.PlayerWindow.SlotCount);

        var dig = await c.DigBlockAsync(9, 64, 8);
        Assert.True(dig.Ok, dig.Message);
        Assert.Contains(s.Dug, d => (d.X, d.Y, d.Z, d.Id) == (9, 64, 8, Blocks.Dirt));

        var place = await c.PlaceBlockAsync(9, 64, 8, Blocks.Cobblestone);
        Assert.True(place.Ok, place.Message);
        Assert.Equal(Blocks.Cobblestone, s.Blocks[(9, 64, 8)]);
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.CountItem(Blocks.Cobblestone) == 4));
        var sent = s.Got<PlayerBlockPlacementPacket>().Last(p => p.Direction != PlayerBlockPlacementPacket.NoDirection);
        if (release1122)
        {
            // 1.12.2's placement carries the hand and the cursor (its own axis on the face, 0 or 1), not the held item.
            Assert.Equal(0, sent.Hand);
            var cur = sent.Cursor!.Value;
            float faceAxis = sent.Direction switch { 0 or 1 => cur.Y, 2 or 3 => cur.Z, _ => cur.X };
            Assert.Equal(sent.Direction is 1 or 3 or 5 ? 1f : 0f, faceAxis);
        }

        var take = await c.WithdrawAsync(Blocks.Planks, 5, new BlockPos(8, 64, 10));
        Assert.True(take.Ok, take.Message);
        Assert.Equal(5, c.World.Inventory.CountItem(Blocks.Planks));
        if (release1122)
        {
            Assert.All(s.Clicks, k => Assert.True(k.Accepted, $"click {k} rejected"));
            Assert.Equal(5, s.Inventory.Where(i => !i.IsEmpty && i.Id == Blocks.Planks).Sum(i => i.Count)); // the server's own model agrees
            AssertSameAsServer(c, s);
        }
    }

    public static TheoryData<bool, bool> VersionsAndSneak => new() { { false, false }, { true, false }, { false, true }, { true, true } };

    [Theory]
    [MemberData(nameof(VersionsAndSneak))]
    public async Task Place_with_a_water_bucket_pours_into_the_dug_hole(bool release1122, bool sneaking)
    {
        // Only the use-item packet pours, along the server's own raytrace of the sent look
        // from its eye (0.08 lower while sneaking on 1.12.2).
        var (c, s) = await Start(release1122, srv =>
        {
            for (int x = 2; x <= 14; x++)
            for (int z = 2; z <= 14; z++)
            {
                srv.Blocks[(x, 62, z)] = Blocks.Dirt;
                srv.Blocks[(x, 63, z)] = Blocks.Grass;
            }
            srv.Blocks.Remove((8, 63, 8)); // the dug hole
            srv.SpawnAt = (6.5, 64, 8.5); // two cells west: the placement aim's sneaking ray clips the rim from here
            srv.Inventory[36] = Harness.Stack(Items.WaterBucket, 1);
            srv.Buckets = true;
        });
        await using var _ = c;
        using var __ = s;
        if (sneaking)
        {
            Assert.True((await c.CrouchAsync(true)).Ok);
            Assert.True(await Harness.WaitAsync(() => s.Got<EntityActionPacket>().Any(a => a.Action == 1)));
        }
        var r = await c.PlaceBlockAsync(8, 63, 8, Items.WaterBucket);
        Assert.True(r.Ok, r.Message);
        Assert.Equal([(8, 63, 8)], s.Pours.ToArray());
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.CountItem(Items.Bucket) == 1));
        Assert.True(BlockShapes.IsWater(c.World.GetBlock(8, 63, 8)));
    }

    [Fact]
    public async Task Clicks_with_confirm_accept_reject_and_the_apology()
    {
        var (c, s) = await Start(true, srv =>
        {
            srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 10);
            srv.Inventory[9] = Harness.Stack(Blocks.Dirt, 3);
        });
        await using var _ = c;
        using var __ = s;

        // A rejected click: the server answers false + the window + the cursor and ignores clicks until the apology.
        for (short a = 1; a < 200; a++) s.RejectActions.Add(a);
        var rejected = await c.ClickSlotAsync(36);
        Assert.False(rejected.Ok);
        s.RejectActions.Clear();
        Assert.True(s.WaitFor(p => p is TransactionPacket { Accepted: true }), "no apology");
        // The server applied its own slotClick anyway (the pickup): the resync hands the client the server's cursor.
        AssertSameAsServer(c, s);
        Assert.Equal(Harness.Stack(Blocks.Cobblestone, 10), s.ServerCursor);
        Assert.Equal(s.ServerCursor, c.World.Inventory.Cursor);

        // Accepted clicks of each type, the server's slotClick agreeing on the expected item every time.
        int before = s.Clicks.Count;
        Assert.True((await c.ClickSlotAsync(36)).Ok);                       // the cobble back into hotbar 0
        Assert.True((await c.ClickSlotAsync(9, ClickMode.Swap, 0)).Ok);      // number key 1: dirt ↔ hotbar 0
        Assert.Equal(Blocks.Dirt, c.World.Inventory.GetPlayerSlot(36).Id);
        Assert.True((await c.ClickSlotAsync(9, ClickMode.QuickMove, 0)).Ok); // shift: cobble back to the hotbar
        Assert.True((await c.ClickSlotAsync(36, ClickMode.Pickup, 1)).Ok);   // right click: half the dirt (2)
        Assert.True((await c.ClickSlotAsync(20, ClickMode.Pickup, 0)).Ok);   // put it down
        Assert.True((await c.ClickSlotAsync(20, ClickMode.Pickup, 0)).Ok);   // pick it up again
        Assert.True((await c.ClickSlotAsync(20, ClickMode.PickupAll, 0)).Ok); // double click: the other dirt joins the cursor
        Assert.Equal(3, c.World.Inventory.Cursor.Count);
        Assert.True((await c.ClickSlotAsync(-999, ClickMode.QuickCraft, 0)).Ok); // drag start
        Assert.True((await c.ClickSlotAsync(30, ClickMode.QuickCraft, 1)).Ok);
        Assert.True((await c.ClickSlotAsync(31, ClickMode.QuickCraft, 1)).Ok);
        Assert.True((await c.ClickSlotAsync(32, ClickMode.QuickCraft, 1)).Ok);
        Assert.True((await c.ClickSlotAsync(-999, ClickMode.QuickCraft, 2)).Ok); // drag end: one each
        Assert.True((await c.ClickSlotAsync(30, ClickMode.Throw, 0)).Ok);    // Q drops one
        var judged = s.Clicks.Skip(before).ToList();
        Assert.Equal(13, judged.Count);
        Assert.All(judged, k => Assert.True(k.Accepted, $"click {k} rejected"));
        AssertSameAsServer(c, s);
        Assert.True(c.World.Inventory.GetPlayerSlot(30).IsEmpty);
        Assert.Equal(1, c.World.Inventory.GetPlayerSlot(31).Count);
        Assert.Equal(new[] { WindowClickPacket.ModePickup, WindowClickPacket.ModeSwap, WindowClickPacket.ModeQuickMove },
            s.Got<WindowClickPacket>().Skip(1).Take(3).Select(k => k.EffectiveMode).ToArray()); // the click types on the wire
    }

    [Fact]
    public async Task Offhand_swap_and_window_slot_45()
    {
        var (c, s) = await Start(true, srv => srv.Inventory[36] = Harness.Stack(Blocks.Torch, 16));
        await using var _ = c;
        using var __ = s;
        Assert.True(c.World.Inventory.Offhand.IsEmpty);

        var r = await c.SwapHandsAsync();
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Harness.Stack(Blocks.Torch, 16), c.World.Inventory.Offhand);
        Assert.True(await Harness.WaitAsync(() => s.Offhand.Equals(Harness.Stack(Blocks.Torch, 16))));
        Assert.True(c.World.Inventory.GetPlayerSlot(36).IsEmpty);
        // The off hand is a window slot like any other: a click takes it back.
        Assert.True((await c.ClickSlotAsync(45)).Ok);
        Assert.Equal(Harness.Stack(Blocks.Torch, 16), c.World.Inventory.Cursor);
        Assert.True((await c.ClickSlotAsync(37)).Ok);
        Assert.True(s.Offhand.IsEmpty);
        Assert.Equal(16, s.Inventory[37].Count);
    }

    [Fact]
    public async Task Crafting_table_flow_on_1122()
    {
        var (c, s) = await Start(true, srv =>
        {
            srv.Blocks[(9, 64, 8)] = Blocks.Workbench;
            srv.Workbenches.Add((9, 64, 8));
            srv.Inventory[12] = Harness.Stack(Blocks.Log, 3);
        });
        await using var _ = c;
        using var __ = s;

        var r = await c.CraftAsync(Items.WoodenPickaxe, -1, 1); // planks and sticks in the 2x2 grid, the pickaxe at the table
        Assert.True(r.Ok, r.Message);
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.PlayerWindow.Get(0).IsEmpty && c.World.Inventory.OpenWindow is null));
        Assert.Equal(1, s.Inventory.Count(i => !i.IsEmpty && i.Id == Items.WoodenPickaxe));
        Assert.Equal(1, c.World.Inventory.CountItem(Items.WoodenPickaxe));
        Assert.Contains(s.Received, p => p is OpenWindowPacket or PlayerBlockPlacementPacket { X: 9, Y: 64, Z: 8 });
        Assert.All(s.Clicks, k => Assert.True(k.Accepted, $"click {k} rejected"));
        AssertSameAsServer(c, s);
    }

    /// <summary>P5: the client predicts the 1.12.2 recipes (P2 predicted b1.8.1's "any log → oak planks" and was rejected once,
    /// then resynced): birch logs give birch planks, a crafting table takes mixed planks, every click is accepted first time.</summary>
    [Fact]
    public async Task Birch_planks_and_a_mixed_plank_table_are_predicted_without_a_resync_on_1122()
    {
        var (c, s) = await Start(true, srv =>
        {
            srv.Inventory[12] = Harness.Stack(Blocks.Log, 1, 2);    // birch
            srv.Inventory[13] = Harness.Stack(Blocks.Planks, 2, 4); // acacia
        });
        await using var _ = c;
        using var __ = s;

        var planks = await c.CraftAsync(Blocks.Planks, 2, 4);
        Assert.True(planks.Ok, planks.Message);
        Assert.Equal(4, c.World.Inventory.CountItem(Blocks.Planks, 2));
        var table = await c.CraftAsync(Blocks.Workbench, -1, 1);
        Assert.True(table.Ok, table.Message);
        Assert.Equal(1, c.World.Inventory.CountItem(Blocks.Workbench));
        Assert.All(s.Clicks, k => Assert.True(k.Accepted, $"click {k} rejected"));
        AssertSameAsServer(c, s);
    }

    [Fact]
    public async Task Chop_a_log_craft_planks_and_place_them_on_1122()
    {
        var spawnedItems = new ConcurrentQueue<Entity>();
        var (c, s) = await Start(true, srv =>
        {
            srv.Blocks[(9, 64, 8)] = Blocks.Log;
            srv.DropOnDig = true;
            srv.ConsumeOnPlace = true;
        });
        await using var _ = c;
        using var __ = s;
        c.World.Entities.Spawned += e => { if (e.Kind == EntityKind.Item) spawnedItems.Enqueue(e); };

        // Chop: Player Digging start/finish; the log drops as Spawn Object 2 + its metadata and is picked up (Collect Item).
        var chop = await c.DigBlockAsync(9, 64, 8);
        Assert.True(chop.Ok, chop.Message);
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.CountItem(Blocks.Log) == 1, 5000), "log not picked up");
        var drop = Assert.Single(spawnedItems);
        Assert.Equal(Blocks.Log, drop.Item.Id);

        // Craft planks in the inventory's 2x2 grid.
        var craft = await c.CraftAsync(Blocks.Planks, -1, 4);
        Assert.True(craft.Ok, craft.Message);
        Assert.Equal(4, c.World.Inventory.CountItem(Blocks.Planks));
        Assert.All(s.Clicks, k => Assert.True(k.Accepted, $"click {k} rejected"));
        AssertSameAsServer(c, s);

        // Place one where the log stood: the server uses its own hotbar (1.12.2 sends no held item).
        var place = await c.PlaceBlockAsync(9, 64, 8, Blocks.Planks);
        Assert.True(place.Ok, place.Message);
        Assert.Equal(Blocks.Planks, s.Blocks[(9, 64, 8)]);
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.CountItem(Blocks.Planks) == 3));
        Assert.Equal(3, s.Inventory.Where(i => !i.IsEmpty && i.Id == Blocks.Planks).Sum(i => i.Count));
    }

    [Fact]
    public async Task Other_players_dig_progress_signs_and_item_stacks_reach_the_client()
    {
        var (c, s) = await Start(true);
        await using var _ = c;
        using var __ = s;
        var cracks = new ConcurrentQueue<(int, BlockPos, int)>();
        var signs = new ConcurrentQueue<(int, int, int)>();
        c.BlockBreakProgress += (e, p, st) => cracks.Enqueue((e, p, st));
        c.SignUpdated += (x, y, z, _) => signs.Enqueue((x, y, z));

        s.Send(new NamedEntitySpawnPacket { EntityId = 77, PlayerName = "PoofImaFox", X = 320, Y = 2048, Z = 288 });
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.FindPlayer("PoofImaFox") is { Id: 77 }));
        s.Send(new BlockBreakAnimationMessage { EntityId = 77, X = 8, Y = 63, Z = 9, Stage = 3 });
        Assert.True(await Harness.WaitAsync(() => cracks.Contains((77, new BlockPos(8, 63, 9), 3))));

        s.Send(new UpdateSignPacket { X = 7, Y = 64, Z = 7, Lines = ["shop", "", "", ""] });
        Assert.True(await Harness.WaitAsync(() => signs.Contains((7, 64, 7))));
        Assert.Equal("shop", c.Signs[new BlockPos(7, 64, 7)][0]);

        int id = s.SpawnDrop(6.5, 64.1, 6.5, Harness.Stack(Blocks.Sand, 2));
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(id) is { Kind: EntityKind.Item, Item.Count: 2 }));
    }
}
