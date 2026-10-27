using MineClaw.Bot.Brainstem;
using MineClaw.Bot.Ledger;
using MineClaw.Bot.Tests.WorldIndex;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>Live session 5 (2026-10-27 evening): hand-overs and spoken promises wake the brain, reflex meals in the ledger,
/// shared chest memory that an open contradicts.</summary>
public class LiveSession5BotTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 27, 21, 4, 20, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static readonly Vec3d FoxFeet = new(10.5, 64, 2.5);
    private static HandoverWatch.PlayerView Fox(float yaw = 0) => new("PoofimaFox", FoxFeet, yaw, 0);

    // ------------------------------------------------------------------ item 1: the exact live sequence

    [Fact]
    public void Wheat_hand_over_after_the_bread_promise_wakes_once_with_the_original_intent()
    {
        var clock = new Clock();
        var watch = new HandoverWatch(clock);
        int wheat = 0;
        Func<int, int> have = id => id == Items.Wheat ? wheat : 0;

        // 21:04:20 <PoofimaFox> I have wheat come here I'll give it to you, make bread
        watch.OnPlayerLine("PoofimaFox", "I have wheat come here I'll give it to you, make bread");
        clock.Advance(8);
        // 21:04:28 <maid_chan> Thank you so much uwu, I'll make bread the second you hand it over
        var p = watch.OnBotLine("Thank you so much uwu, I'll make bread the second you hand it over", have);
        Assert.NotNull(p);
        Assert.Equal("make bread", p!.Intent);
        Assert.Equal(Items.Wheat, p.ItemId);
        Assert.Equal("PoofimaFox", p.From);
        Assert.True(p.ByHandover);

        // 21:04:29 Fox tosses the wheat: it spawns at his eye height (locY - 0.3 + 1.62), the bot picks it up (+21 wheat).
        clock.Advance(1);
        Assert.Equal("PoofimaFox", watch.OnItemSpawn(501, FoxFeet + new Vec3d(0.1, 1.32, 0.2), [Fox()]));
        Assert.True(watch.OnPickup(501, Items.Wheat, 16));
        clock.Advance(0.5);
        Assert.Equal("PoofimaFox", watch.OnItemSpawn(502, FoxFeet + new Vec3d(0, 1.32, 0.4), [Fox()]));
        Assert.True(watch.OnPickup(502, Items.Wheat, 5));
        wheat = 21;
        Assert.Empty(watch.Poll(have)); // still debouncing: the stacks of one hand-over are one event

        clock.Advance(2.1);
        var (text, player) = Assert.Single(watch.Poll(have));
        Assert.Equal("PoofimaFox", player);
        Assert.Equal("PoofimaFox gave you 21 wheat; you said you'd make bread when PoofimaFox handed it over (now 21 wheat)", text);
        Assert.Empty(watch.Promises);
        clock.Advance(5);
        Assert.Empty(watch.Poll(have)); // once
    }

    [Fact]
    public void Promise_is_kept_from_the_inventory_even_when_the_toss_was_not_seen()
    {
        // The bot walked into range after the toss: the item spawned on the ground, not at Fox's eyes.
        var clock = new Clock();
        var watch = new HandoverWatch(clock);
        int wheat = 0;
        watch.OnPlayerLine("PoofimaFox", "I have wheat come here I'll give it to you, make bread");
        watch.OnBotLine("Thank you so much uwu, I'll make bread the second you hand it over", id => wheat);
        Assert.Null(watch.OnItemSpawn(600, new Vec3d(13.5, 64.1, 6.5), [Fox()]));
        Assert.False(watch.OnPickup(600, Items.Wheat, 21));
        wheat = 21;
        var (text, _) = Assert.Single(watch.Poll(id => id == Items.Wheat ? wheat : 0));
        Assert.Equal("you said you'd make bread when PoofimaFox handed it over; you now have 21 wheat", text);
    }

    [Fact]
    public void A_toss_in_front_of_the_player_counts_and_block_drops_do_not()
    {
        var clock = new Clock();
        var watch = new HandoverWatch(clock);
        // Facing +Z (yaw 0): the item already flew ~1.2 blocks ahead and dropped a little when its spawn packet arrived.
        Assert.Equal("PoofimaFox", watch.OnItemSpawn(1, FoxFeet + new Vec3d(0.1, 0.9, 1.2), [Fox()]));
        // Behind him: not his toss.
        Assert.Null(watch.OnItemSpawn(2, FoxFeet + new Vec3d(0, 1.0, -1.5), [Fox()]));
        // The bot mines a block next to Fox at head height: its drop is a block drop.
        watch.OnBlockGone(new BlockPos(10, 65, 3));
        Assert.Null(watch.OnItemSpawn(3, new Vec3d(10.5, 65.4, 3.4), [Fox()]));
        // Nobody around.
        Assert.Null(watch.OnItemSpawn(4, new Vec3d(0, 64, 0), []));
    }

    [Fact]
    public void A_plain_hand_over_without_a_promise_still_wakes()
    {
        var clock = new Clock();
        var watch = new HandoverWatch(clock);
        watch.OnItemSpawn(7, FoxFeet + new Vec3d(0, 1.32, 0), [Fox()]);
        watch.OnPickup(7, Blocks.Torch, 5);
        clock.Advance(2.5);
        Assert.Equal("PoofimaFox gave you 5 torch", Assert.Single(watch.Poll(_ => 5)).Text);
    }

    [Theory]
    [InlineData("Once I get the logs, I'll build the floor uwu", "build the floor uwu", Blocks.Log)]
    [InlineData("I'll craft the planks as soon as I have 4 logs", "craft the planks", Blocks.Log)]
    public void Item_promises_fire_when_the_bot_has_it(string line, string intent, int item)
    {
        var clock = new Clock();
        var watch = new HandoverWatch(clock);
        int logs = 0;
        var p = watch.OnBotLine(line, id => logs);
        Assert.NotNull(p);
        Assert.Equal(item, p!.ItemId);
        Assert.False(p.ByHandover);
        Assert.Empty(watch.Poll(_ => logs));
        logs = 6;
        var text = Assert.Single(watch.Poll(id => id == item ? logs : 0)).Text;
        Assert.StartsWith($"you said you'd {intent.Replace(" uwu", "")}", text.Replace(" uwu", ""));
        Assert.EndsWith("you now have 6 oak wood", text.Replace("log", "oak wood").Replace("oak oak", "oak"));
    }

    [Theory]
    [InlineData("I'll make bread for you!")]
    [InlineData("Coming, master!")]
    [InlineData("I'll be right there once I'm done here")]
    public void Lines_without_a_watchable_condition_are_not_promises(string line) =>
        Assert.Null(new HandoverWatch(new Clock()).OnBotLine(line, _ => 0));

    [Fact]
    public void Promises_expire()
    {
        var clock = new Clock();
        var watch = new HandoverWatch(clock);
        watch.OnPlayerLine("PoofimaFox", "I have wheat, make bread");
        watch.OnBotLine("I'll make bread when you give it to me", _ => 0);
        clock.Advance(301);
        Assert.Empty(watch.Poll(_ => 21));
    }

    // ------------------------------------------------------------------ item 3: reflex meal in the ledger

    [Fact]
    public void A_reflex_meal_is_ate_reflex_not_vanished()
    {
        var clock = new Clock();
        var l = new InventoryLedger(clock);
        l.Observe(new Dictionary<(int, int), int> { [(Items.Bread, 0)] = 3 });
        l.NoteEat(Items.Bread, auto: true);
        clock.Advance(1.8); // the slot update lands after the eat action ended (no scope, no action)
        l.Observe(new Dictionary<(int, int), int> { [(Items.Bread, 0)] = 2 });
        clock.Advance(1);
        var e = Assert.Single(l.History("bread"));
        Assert.Equal(-1, e.Delta);
        Assert.Equal("ate (reflex)", e.Cause);
        // Without a meal note the same change is still a mystery.
        l.Observe(new Dictionary<(int, int), int> { [(Items.Bread, 0)] = 1 });
        clock.Advance(1);
        Assert.StartsWith("vanished", l.History("bread").Last().Cause);
    }

    // ------------------------------------------------------------------ item 3: shared chest memory vs an open

    private static void OpenChest(IndexFixture f, int window, params ItemStack[] contents)
    {
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(new Vec3d(0.5, 65 + PlayerPhysics.EyeHeight, 0.5), new Vec3d(2.5, 64.5, 0.5));
        f.World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, yaw, pitch, true);
        f.World.Inventory.Open(window, WindowType.Chest, "Chest", 27);
        var items = Enumerable.Repeat(ItemStack.Empty, 27 + 36).ToArray();
        for (int i = 0; i < contents.Length; i++) items[i] = contents[i];
        f.World.Inventory.SetWindowItems(window, items);
    }

    [Fact]
    public void An_open_that_finds_the_bread_gone_updates_memory_and_names_who_may_have_taken_it()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(2, 64, 0, Blocks.Chest);
        f.Pump();
        var seen = new List<ContainerVanished>();
        f.Indexer.ContentsVanished += v => seen.Add(v);

        // 21:05:51 main deposits 5 bread (the chest held logs too).
        OpenChest(f, 1, new ItemStack(Blocks.Log, 40, 0));
        f.World.Inventory.SetSlot(1, 3, new ItemStack(Items.Bread, 5, 0));
        f.World.Inventory.Close(1);
        f.Pump();
        Assert.Empty(seen); // our own deposit is not "gone"
        var row = f.Row("chest", "2,64,0")!;
        Assert.Equal("maid_chan", row.ContentsBy);

        // Fox takes the bread back out (no packets for his clicks). 49 s later the bot opens it again: no bread.
        f.Clock.Seconds(49);
        f.World.Entities.SpawnPlayer(900, "PoofimaFox", new Vec3d(4.5, 64, 1.5), 0, 0, heldItem: Items.Bread);
        OpenChest(f, 2, new ItemStack(Blocks.Log, 40, 0));
        f.World.Inventory.Close(2);
        f.Pump();

        var v = Assert.Single(seen);
        var gone = Assert.Single(v.Gone);
        Assert.Equal((Items.Bread, 5), (gone.ItemId, gone.Count));
        Assert.Equal("maid_chan", v.Before.By);
        Assert.Equal(v, f.Indexer.LastVanished(new BlockPos(2, 64, 0)));
        // Memory now says what the chest really holds, for every hive bot.
        var where = MineClaw.Agent.Rag.WorldTools.WhereIs(f.Store, "bread", null, new MineClaw.Agent.Memory.BlockPos(0, 65, 0), f.Clock.GetUtcNow());
        Assert.StartsWith("no bread in known chests", where.Text);
        var text = ChestChanges.Describe(v, f.World, f.Db, _ => false, f.Clock.GetUtcNow());
        Assert.Equal("chest at 2,64,0: bread x5 gone since maid_chan opened it 49s ago; PoofimaFox may have taken it (holding Bread)", text);
    }

    [Fact]
    public void Taking_items_ourselves_is_not_counted_as_gone()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(2, 64, 0, Blocks.Chest);
        f.Pump();
        var seen = new List<ContainerVanished>();
        f.Indexer.ContentsVanished += v => seen.Add(v);
        OpenChest(f, 1, new ItemStack(Items.Bread, 5, 0), new ItemStack(Items.StonePickaxe, 1, 0));
        f.World.Inventory.Close(1);
        f.Pump();
        f.Clock.Seconds(30);
        // Open (the server's 0x68 shows both), then our own click takes the pickaxe before the close.
        OpenChest(f, 2, new ItemStack(Items.Bread, 5, 0), new ItemStack(Items.StonePickaxe, 1, 0));
        f.World.Inventory.SetSlot(2, 1, ItemStack.Empty);
        f.World.Inventory.Close(2);
        f.Pump();
        Assert.Empty(seen);
    }
}
