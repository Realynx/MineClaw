using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

internal static class Harness
{
    /// <param name="release1122">Run the fake server and the client on 1.12.2 (the server's 1.12.2 codec, the 1.12.2 profile).</param>
    public static async Task<(MinecraftClient Client, FakeServer Server)> StartAsync(Action<FakeServer>? setup = null,
        Func<ClientOptions, ClientOptions>? opts = null, bool release1122 = false)
    {
        var server = new FakeServer(release1122);
        setup?.Invoke(server);
        var o = new ClientOptions
        {
            Username = "maid_chan",
            TickInterval = TimeSpan.FromMilliseconds(5),
            ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10),
            ConnectionFactory = release1122 ? server.CreateConnection : server.CreateClientConnection,
            HostileAlertRadius = 0,
        };
        if (release1122) o = o with { Profile = MineClaw.Client.Versioning.GameVersionProfiles.Java1122 };
        if (opts is not null) o = opts(o);
        var client = new MinecraftClient(o);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);
        // Let the player settle onto the platform.
        await WaitAsync(() => client.World.Player.OnGround, 3000);
        return (client, server);
    }

    public static async Task<bool> WaitAsync(Func<bool> cond, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(5);
        }
        return cond();
    }

    public static ItemStack Stack(int id, int count, int damage = 0) => new((short)id, (byte)count, (short)damage);
}

public class DispatchTests
{
    [Fact]
    public async Task LoginSyncsWorldPositionAndInventory()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Dirt, 12));
        await using var _ = c;
        using var __ = s;

        Assert.True(c.IsPlaying);
        Assert.Equal(FakeServer.EntityId, c.World.Player.EntityId);
        Assert.Equal(new BlockPos(8, 64, 8), c.World.Spawn);
        Assert.Equal(Blocks.Stone, c.World.GetBlock(8, 63, 8));
        Assert.Equal(12, c.World.Inventory.CountItem(Blocks.Dirt));
        Assert.Equal(64, c.World.Player.Position.Y, 3);
        // The server's 0x0D must be echoed back unchanged before any other movement.
        var echo = s.Got<PlayerPositionLookPacket>().First();
        Assert.Equal(8.5, echo.X);
        Assert.Equal(64, echo.Y);
        Assert.Equal(64 + 1.62, echo.Stance, 6);
        Assert.Equal(0, c.PositionCorrections);
    }

    [Fact]
    public async Task SendsAMovementPacketEveryTick()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        long t0 = c.TickCount;
        int before = s.Received.Count(p => p.Id is >= 0x0A and <= 0x0D);
        await Harness.WaitAsync(() => c.TickCount >= t0 + 40);
        int after = s.Received.Count(p => p.Id is >= 0x0A and <= 0x0D);
        Assert.InRange(after - before, 35, 50);
    }

    [Fact]
    public async Task KeepAliveIsEchoed()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        s.Send(new KeepAlivePacket(777));
        Assert.True(s.WaitFor(p => p is KeepAlivePacket { KeepAliveId: 777 }));
    }

    [Fact]
    public async Task BlockChangesAndEntitiesAreApplied()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        s.Send(new BlockChangePacket { X = 7, Y = 64, Z = 7, BlockId = (byte)Blocks.Chest });
        s.Send(new MultiBlockChangePacket
        {
            ChunkX = 0, ChunkZ = 0,
            Coordinates = [ChunkDataDecoder.EncodeMultiBlockCoordinate(9, 64, 9), ChunkDataDecoder.EncodeMultiBlockCoordinate(10, 64, 10)],
            BlockIds = [(byte)Blocks.Log, (byte)Blocks.Log], Metadata = [1, 2],
        });
        s.Send(new NamedEntitySpawnPacket { EntityId = 7, PlayerName = "partner", X = 10 * 32 + 16, Y = 64 * 32, Z = 9 * 32, Yaw = 64 });
        s.Send(new PickupSpawnPacket { EntityId = 8, ItemId = Blocks.Cobblestone, Count = 3, X = 9 * 32, Y = 64 * 32, Z = 9 * 32 });
        s.Send(new MobSpawnPacket { EntityId = 9, Type = 54, X = 0, Y = 64 * 32, Z = 0 });
        s.Send(new EntityRelativeMovePacket { EntityId = 7, DX = 32, DY = 0, DZ = -16 });
        s.Send(new PreChunkPacket { ChunkX = 1, ChunkZ = 1, Load = false });

        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(9) is not null && !c.World.IsLoaded(20, 20)));
        Assert.Equal(Blocks.Chest, c.World.GetBlock(7, 64, 7));
        Assert.Equal(Blocks.Log, c.World.GetBlock(9, 64, 9));
        Assert.Equal(2, c.World.GetMeta(10, 64, 10));
        var partner = c.World.Entities.FindPlayer("partner")!;
        Assert.Equal(11.5, partner.Position.X, 6);
        Assert.Equal(8.5, partner.Position.Z, 6);
        Assert.Equal(90f, partner.Yaw, 3);
        var drop = c.World.Entities.Get(8)!;
        Assert.Equal(EntityKind.Item, drop.Kind);
        Assert.Equal(3, drop.Item.Count);
        Assert.True(c.World.Entities.Get(9)!.IsHostileMob);

        string near = c.DescribeSurroundings();
        Assert.Contains("chest@7,64,7", near);
        Assert.Contains("partner", near);
        Assert.Contains("!zombie", near);
        Assert.Contains("void", near);
        Assert.True(near.Length <= 600, near);
    }

    [Fact]
    public async Task WindowItemsSetSlotAndCursor()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        s.Send(new SetSlotPacket { WindowId = 0, Slot = 40, Item = Harness.Stack(Items.StonePickaxe, 1, 5) });
        s.Send(new SetSlotPacket { WindowId = -1, Slot = -1, Item = Harness.Stack(Blocks.Dirt, 2) });
        Assert.True(await Harness.WaitAsync(() => !c.World.Inventory.Cursor.IsEmpty));
        Assert.Equal(Items.StonePickaxe, c.World.Inventory.GetPlayerSlot(40).Id);
        string inv = c.DescribeInventory();
        Assert.Contains("stone pickaxe x1", inv);
        s.Send(new OpenWindowPacket { WindowId = 3, InventoryType = 2, Title = "Furnace", SlotCount = 3 });
        s.Send(new UpdateProgressBarPacket { WindowId = 3, ProgressBar = 0, Value = 100 });
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.OpenWindow?.SmeltProgress > 0.4));
        s.Send(new CloseWindowPacket { WindowId = 3 });
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.OpenWindow is null));
    }

    [Fact]
    public async Task EventsForChatHealthDeathRespawnPlayersAndKick()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var chats = new List<string>();
        var joined = new List<string>();
        var left = new List<string>();
        var dangers = new List<DangerKind>();
        int died = 0, respawned = 0;
        string? kicked = null;
        c.ChatReceived += m => { lock (chats) chats.Add(m); };
        c.PlayerJoined += n => { lock (joined) joined.Add(n); };
        c.PlayerLeft += n => { lock (left) left.Add(n); };
        c.Danger += d => { lock (dangers) dangers.Add(d.Kind); };
        c.Died += () => Interlocked.Increment(ref died);
        c.Respawned += () => Interlocked.Increment(ref respawned);
        c.Kicked += r => kicked = r;

        s.Send(new ChatPacket("<tester> hi maid_chan"));
        s.Send(new PlayerListItemPacket { PlayerName = "partner", Online = true });
        s.Send(new PlayerListItemPacket { PlayerName = "tester", Online = true }); // ping refresh: no event
        s.Send(new PlayerListItemPacket { PlayerName = "tester", Online = false });
        s.Send(new UpdateHealthPacket { Health = 6, Food = 20, FoodSaturation = 1 });
        s.Send(new UpdateHealthPacket { Health = 0, Food = 20, FoodSaturation = 0 });
        Assert.True(s.WaitFor(p => p is RespawnPacket), "auto-respawn must send 0x09");
        s.Send(new RespawnPacket { Dimension = 0, Difficulty = 1, GameMode = 0, WorldHeight = 128, MapSeed = 1234 });
        Assert.True(await Harness.WaitAsync(() => respawned == 1));
        Assert.Equal(1, died);
        Assert.Equal(["<tester> hi maid_chan"], chats);
        Assert.True(ChatText.TryParsePlayerChat(chats[0], out var who, out var msg) && who == "tester" && msg == "hi maid_chan");
        Assert.Equal(["partner"], joined);
        Assert.Equal(["tester"], left);
        Assert.Contains(DangerKind.Damage, dangers);
        Assert.Contains(DangerKind.LowHealth, dangers);

        s.Send(new KickPacket("bye"));
        Assert.True(await Harness.WaitAsync(() => kicked == "bye" && c.State == ClientState.Disconnected));
    }
}

public class ChatTests
{
    [Fact]
    public void SanitizeMapsUnicodeAndDropsIllegalCharacters()
    {
        Assert.Equal("It's \"fine\" - cafe... ok", ChatText.Sanitize("It’s “fine” — café… ok"));
        Assert.Equal("red text", ChatText.Sanitize("§cred §rtext"));
        Assert.Equal("a b", ChatText.Sanitize("a\n\n\tb"));
        Assert.Equal("no emoji", ChatText.Sanitize("no emoji \U0001F600"));
        Assert.Equal("it's", ChatText.Sanitize("it`s"));
        Assert.All(ChatText.Sanitize("你好 hello ©®").ToCharArray(), ch => Assert.True(ChatText.IsAllowed(ch)));
    }

    [Fact]
    public void SplitKeepsLinesUnder100AtWordBoundaries()
    {
        var text = string.Join(' ', Enumerable.Range(0, 60).Select(i => $"word{i}"));
        var lines = ChatText.Split(text);
        Assert.True(lines.Count >= 4);
        Assert.All(lines, l => Assert.True(l.Length <= 100));
        Assert.Equal(text, string.Join(' ', lines));
        var hard = ChatText.Split(new string('x', 250));
        Assert.Equal([100, 100, 50], hard.Select(l => l.Length));
    }

    [Fact]
    public async Task SendChatSplitsAndSendsSanitizedLines()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var r = await c.SendChatAsync("hello — " + new string('a', 120));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(2, r.Count);
        Assert.True(s.WaitFor(p => p is ChatPacket { Message: var m } && m.StartsWith("hello - ")));
        Assert.All(s.Got<ChatPacket>(), p => Assert.True(p.Message.Length <= 100));
    }
}

public class ActionTests
{
    private static List<WindowClickPacket> Clicks(FakeServer s) => s.Got<WindowClickPacket>().ToList();

    [Fact]
    public async Task CraftPlanksInInventoryGridSendsExpectedClicks()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Log, 1));
        await using var _ = c;
        using var __ = s;
        var r = await c.CraftAsync(Blocks.Planks, -1, 4);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(4, c.World.Inventory.CountItem(Blocks.Planks));
        Assert.Equal(0, c.World.Inventory.CountItem(Blocks.Log));
        var clicks = Clicks(s).Select(k => (k.WindowId, k.Slot, k.RightClick, k.Shift)).ToList();
        Assert.Equal([(0, 36, false, false), (0, 1, false, false), (0, 0, false, true)], clicks.Select(k => ((int)k.WindowId, (int)k.Slot, k.RightClick, k.Shift)));
        // The click must carry the predicted slot content (what the server's slotClick returns).
        Assert.Equal(Blocks.Log, s.Got<WindowClickPacket>().First().ClickedItem.Id);
        Assert.Equal(Blocks.Planks, s.Got<WindowClickPacket>().Last().ClickedItem.Id);
        Assert.True(Enumerable.Range(1, 4).All(i => c.World.Inventory.GetPlayerSlot(i).IsEmpty));
    }

    [Fact]
    public async Task CraftSticksUsesRightClicksForPartialStacks()
    {
        // 3 planks, want 4 sticks = 1 craft (2 planks): pick up 3, right-click two cells once each, put 1 back.
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[20] = Harness.Stack(Blocks.Planks, 3));
        await using var _ = c;
        using var __ = s;
        var r = await c.CraftAsync(Items.Stick, -1, 4);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(4, c.World.Inventory.CountItem(Items.Stick));
        Assert.Equal(1, c.World.Inventory.CountItem(Blocks.Planks));
        var seq = Clicks(s).Select(k => ((int)k.Slot, k.RightClick, k.Shift)).ToList();
        Assert.Equal((20, false, false), seq[0]);
        Assert.Equal((1, true, false), seq[1]);
        Assert.Equal((3, true, false), seq[2]);
        Assert.Contains((0, false, true), seq);
    }

    [Fact]
    public async Task CraftReportsMissingMaterials()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var r = await c.CraftAsync("wooden pickaxe");
        Assert.False(r.Ok);
        Assert.Contains("missing", r.Message);
        // Only the resync probe (a no-op shift-click on the empty crafting output): "missing" is checked against the server.
        var probe = Assert.Single(Clicks(s));
        Assert.Equal((0, 0, true), ((int)probe.WindowId, (int)probe.Slot, probe.Shift));
        Assert.Equal(0x7FFF, probe.ClickedItem.Damage);
    }

    [Fact]
    public async Task DepositAllShiftClicksMatchingStacksAndCloses()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
            srv.Inventory[37] = Harness.Stack(Blocks.Cobblestone, 64);
            srv.Inventory[10] = Harness.Stack(Blocks.Dirt, 5);
            srv.Blocks[(8, 64, 10)] = Blocks.Chest;
            srv.Chests[(8, 64, 10)] = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.DepositAllAsync(new BlockPos(8, 64, 10));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(69, r.Count);
        var clicks = Clicks(s);
        Assert.All(clicks, k => Assert.True(k.Shift && k.WindowId == 1));
        // Chest window: player main 9 → window 27+1 = 28; hotbar 37 → 27+27+1 = 55.
        Assert.Equal([28, 55], clicks.Select(k => (int)k.Slot).Order());
        Assert.True(s.WaitFor(p => p is CloseWindowPacket { WindowId: 1 }));
        Assert.Equal(1, c.World.Inventory.CountItem(Items.StonePickaxe));
        Assert.Equal(0, c.World.Inventory.CountItem(Blocks.Cobblestone));
    }

    [Fact]
    public async Task RejectedClickIsAcknowledgedAndReported()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[20] = Harness.Stack(Blocks.Dirt, 5);
            srv.RejectActions.Add(1);
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.ClickSlotAsync(20);
        Assert.False(r.Ok);
        Assert.Contains("rejected", r.Message);
        Assert.True(s.WaitFor(p => p is TransactionPacket { ActionNumber: 1, Accepted: true }));
        // Resync restored the server's view (dirt back in slot 20, cursor empty).
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.GetPlayerSlot(20).Count == 5 && c.World.Inventory.Cursor.IsEmpty));
    }

    [Fact]
    public async Task DigWaitsTheComputedTicksWithBestTool()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[20] = Harness.Stack(Items.StonePickaxe, 1);
            srv.Blocks[(9, 64, 8)] = Blocks.Stone;
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.DigBlockAsync(9, 64, 8);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(0, c.World.GetBlock(9, 64, 8));
        // Tool swapped into the hotbar and selected.
        Assert.Contains(s.Got<WindowClickPacket>(), k => k.Slot == 20);
        var held = s.Got<HoldingChangePacket>().Last();
        Assert.Equal(Items.StonePickaxe, c.World.Inventory.GetPlayerSlot(36 + held.Slot).Id);
        // Ticks between start and finish = number of movement packets in between.
        var all = s.Snapshot();
        int start = all.FindIndex(p => p is PlayerDiggingPacket { Status: 0 });
        int finish = all.FindIndex(p => p is PlayerDiggingPacket { Status: 2 });
        Assert.True(start >= 0 && finish > start);
        int ticks = all.Skip(start).Take(finish - start).Count(p => p.Id is >= 0x0A and <= 0x0D);
        int expected = Tools.DigTicks(Blocks.Stone, Items.StonePickaxe);
        Assert.InRange(ticks, expected, expected + 4);
        Assert.Equal((byte)BlockFace.West, ((PlayerDiggingPacket)all[start]).Face);
    }

    [Fact]
    public async Task A_dirt_block_that_turns_to_grass_mid_dig_is_dug_until_it_is_gone()
    {
        // Live 11-03 (Story, 1.12.2): "mine … reported done, but the dirt is still there": exposed dirt turned to grass while it was
        // dug and the client took "not dirt any more" for the break.
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 8)] = Blocks.Dirt;
            srv.Hook = pkt =>
            {
                if (pkt is PlayerDiggingPacket { Status: PlayerDiggingPacket.StatusStarted, X: 9, Y: 64, Z: 8 })
                {
                    srv.Blocks[(9, 64, 8)] = Blocks.Grass;
                    srv.Send(new BlockChangePacket { X = 9, Y = 64, Z = 8, BlockId = (byte)Blocks.Grass });
                    return true;
                }
                if (pkt is PlayerDiggingPacket { Status: PlayerDiggingPacket.StatusFinished, X: 9, Y: 64, Z: 8 })
                {
                    var late = Task.Run(async () =>
                    {
                        await Task.Delay(200); // the server's answer takes a moment
                        srv.Blocks.Remove((9, 64, 8));
                        srv.Send(new BlockChangePacket { X = 9, Y = 64, Z = 8, BlockId = 0 });
                    });
                    return true;
                }
                return false;
            };
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.DigBlockAsync(9, 64, 8);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(0, c.World.GetBlock(9, 64, 8)); // it said dug only once the block was really gone
        Assert.True(MinecraftClient.SameDigForm(Blocks.Dirt, Blocks.Grass));
        Assert.True(MinecraftClient.SameDigForm(73, 74)); // redstone ore lights up when punched
        Assert.False(MinecraftClient.SameDigForm(Blocks.Dirt, 0));
    }

    [Fact]
    public async Task DigRefusesTheBlockHoldingMeOverTheVoid()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks.Clear();
            srv.Blocks[(8, 63, 8)] = Blocks.Dirt; // a single pillar block over the void
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.DigBlockAsync(8, 63, 8);
        Assert.False(r.Ok);
        Assert.Contains("void", r.Message);
        Assert.Empty(s.Got<PlayerDiggingPacket>());
        Assert.True(c.World.Player.Physics.Sneaking || await Harness.WaitAsync(() => c.World.Player.Physics.Sneaking), "idle on an edge must sneak");
    }

    [Fact]
    public async Task DigRespectsProtection()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Blocks[(9, 64, 8)] = Blocks.Planks);
        await using var _ = c;
        using var __ = s;
        c.IsProtected = (p, id) => id == Blocks.Planks;
        var r = await c.DigBlockAsync(9, 64, 8);
        Assert.False(r.Ok);
        Assert.Contains("protected", r.Message);
    }

    [Fact]
    public async Task PlaceBlockAgainstNeighbour()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[38] = Harness.Stack(Blocks.Cobblestone, 10));
        await using var _ = c;
        using var __ = s;
        var r = await c.PlaceBlockAsync(10, 64, 8, Blocks.Cobblestone);
        Assert.True(r.Ok, r.ToString());
        var place = s.Got<PlayerBlockPlacementPacket>().Single();
        // Clicked the top of the stone below (10,63,8) or a side; the target must be against + face.
        var n = ((BlockFace)place.Direction).Normal();
        Assert.Equal(new BlockPos(10, 64, 8), new BlockPos(place.X + n.X, place.Y + n.Y, place.Z + n.Z));
        Assert.Equal(Blocks.Cobblestone, place.HeldItem.Id);
        Assert.Equal(2, s.Got<HoldingChangePacket>().Last().Slot);
    }

    [Fact]
    public async Task GotoWalksAcrossThePlatformWithoutCorrections()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var r = await c.GotoAsync(6, 64, 10);
        Assert.True(r.Ok, r.ToString());
        var p = c.World.Player.Position;
        Assert.Equal(6, (int)Math.Floor(p.X));
        Assert.Equal(10, (int)Math.Floor(p.Z));
        Assert.Equal(64, p.Y, 3);
        Assert.Equal(0, c.PositionCorrections);
        // The goal is an edge cell: the follower sneaks there and sends 0x13 crouch.
        Assert.Contains(s.Got<EntityActionPacket>(), a => a.Action == 1);
        Assert.Contains(s.Got<PlayerPositionPacket>().Concat<IPacket>(s.Got<PlayerPositionLookPacket>()), _ => true);
    }

    [Fact]
    public async Task GotoRefusesToWalkIntoTheVoid()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var r = await c.GotoAsync(20, 64, 8);
        Assert.False(r.Ok);
        Assert.True(c.World.Player.Position.Y > 63.9);
    }

    [Fact]
    public async Task StopCancelsARunningAction()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Blocks[(30, 0, 30)] = Blocks.Stone);
        await using var _ = c;
        using var __ = s;
        s.Send(new NamedEntitySpawnPacket { EntityId = 7, PlayerName = "partner", X = 9 * 32, Y = 64 * 32, Z = 9 * 32 });
        var follow = c.FollowPlayerAsync("partner", 2);
        await Harness.WaitAsync(() => c.IsBusy);
        var stop = await c.StopAsync();
        Assert.True(stop.Ok);
        var r = await follow;
        Assert.Contains("stopped", r.Message);
        Assert.False(c.IsBusy);
    }

    [Fact]
    public async Task StatusLineIsCompact()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var st = c.DescribeStatus();
        Assert.Contains("hp 20/20", st);
        Assert.Contains("overworld 12:00 day", st);
        Assert.True(st.Length < 200, st);
    }
}
