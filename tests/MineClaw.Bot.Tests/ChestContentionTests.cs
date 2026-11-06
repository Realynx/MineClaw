using MineClaw.Bot.GameTools;
using MineClaw.Data;
using MineClaw.World.Claims;
using WPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-29: a DeepSeek hive's 20 bodies chopped around one log chest; "deposits to log_chest keep failing on occupied", "chest
/// keeps bein busy so some of us are holdin logs". Every deposit held the chest's exclusive lease from wherever the body stood
/// (the walk included), so 19 waited and failed. Now the walk comes first, up to four of our bodies deposit side by side (vanilla
/// lets several players use one chest; the server arbitrates the slots), the rest queue briefly, and a full or still-crowded
/// chest gets a new one placed beside it, said in the result.
/// </summary>
public class ChestContentionTests
{
    private static readonly WPos LogChest = new(5, 65, 5);

    private static FakeGameClient Body(string name)
    {
        var c = new FakeGameClient { Username = name };
        for (int x = -10; x <= 10; x++) for (int z = -10; z <= 10; z++) c.World.SetBlock(x, 64, z, Blocks.Stone, 0);
        c.World.SetBlock(LogChest.X, LogChest.Y, LogChest.Z, Blocks.Chest, 0);
        for (int s = 9; s < 13; s++) c.Give(s, Blocks.Log, 32);
        return c;
    }

    private static void Empty(FakeGameClient c)
    {
        for (int s = 9; s <= 44; s++)
            if (c.World.Inventory.PlayerSlots()[s] is { IsEmpty: false } st && st.Id == Blocks.Log) c.World.Inventory.SetSlot(0, s, ItemStack.Empty);
    }

    [Fact]
    public async Task Twenty_bodies_depositing_into_one_chest_all_finish_side_by_side_without_busy_stalls()
    {
        var board = new TestLeases();
        int active = 0, peak = 0;
        var bodies = Enumerable.Range(0, 20).Select(i =>
        {
            var c = Body($"maid_chan_{101 + i}");
            c.Effects["deposit"] = () =>
            {
                int now = Interlocked.Increment(ref active);
                int seen;
                while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                Thread.Sleep(60); // open + shift-clicks
                Empty(c);
                Interlocked.Decrement(ref active);
            };
            return (Fake: c, Game: new ClaimAwareGameClient(c, board.ViewFor(c.Username)));
        }).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await Task.WhenAll(bodies.Select(b => Task.Run(() => ChestStore.StoreAsync(b.Game, null, LogChest, null, CancellationToken.None))));
        sw.Stop();

        Assert.All(results, r => Assert.True(r.Ok, r.Text));
        Assert.All(results, r => Assert.DoesNotContain("busy", r.Text));
        Assert.All(bodies, b => Assert.Equal(0, b.Fake.World.Inventory.CountItem(Blocks.Log)));
        Assert.InRange(peak, 2, 4); // side by side, never a crowd of 20 at the chest
        // The walk happened before any lease: every body walked to the chest first.
        Assert.All(bodies, b => Assert.Contains(b.Fake.CallList, x => x.StartsWith("goto")));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task A_full_drop_off_chest_gets_a_new_chest_placed_beside_it_from_carried_logs_and_the_result_says_so()
    {
        var board = new TestLeases();
        var c = Body("maid_chan_113");
        c.Give(20, Blocks.Chest, 1); // carried (crafting from the logs is the Crafter's own, tested there)
        WPos? placed = null;
        c.Effects["place"] = () =>
        {
            var parts = c.CallList.Last().Split(' ')[1].Split(',').Select(int.Parse).ToArray();
            placed = new WPos(parts[0], parts[1], parts[2]);
            c.World.SetBlock(parts[0], parts[1], parts[2], Blocks.Chest, 0);
            c.World.Inventory.SetSlot(0, 20, ItemStack.Empty); // the carried chest went down
        };
        c.Results["deposit"] = () =>
        {
            var last = c.CallList.Last();
            if (last.StartsWith($"deposit {LogChest.X},{LogChest.Y},{LogChest.Z}")) return MineClaw.Client.ActionResult.Fail("chest is full");
            Empty(c);
            return MineClaw.Client.ActionResult.Success("deposited 128 oak wood", 128);
        };
        var game = new ClaimAwareGameClient(c, board.ViewFor(c.Username));
        var r = await ChestStore.StoreAsync(game, null, LogChest, null, CancellationToken.None);

        Assert.True(r.Ok, r.Text);
        Assert.NotNull(placed);
        Assert.Contains($"chest 5,65,5 full: placed a new chest at {placed!.Value.X},{placed.Value.Y},{placed.Value.Z}", r.Text);
        Assert.InRange(Math.Abs(placed.Value.X - LogChest.X) + Math.Abs(placed.Value.Z - LogChest.Z), 2, 4); // right beside it
        Assert.Equal(0, c.World.Inventory.CountItem(Blocks.Log));
        // Its own chest, never a pair with the old one (no touching sides).
        Assert.True(Math.Abs(placed.Value.X - LogChest.X) >= 2 || Math.Abs(placed.Value.Z - LogChest.Z) >= 2);
    }

    /// <summary>
    /// Live 11-05/06 story-1: pawns' clients put overflow chests down the colony never knew of (223,65,404 on top of a storage chest,
    /// 218,64,395, 239,64,397…). Fox 11-05: "Bots should delete whatever they put down unless it was a directive to build it." A Story
    /// pawn's body (NoOverflowChests) never places one, chest in the bag or not; any other body still may.
    /// </summary>
    [Fact]
    public void A_story_pawn_never_puts_an_overflow_chest_down_by_itself()
    {
        var client = new MineClaw.Client.MinecraftClient(new MineClaw.Client.ClientOptions { Host = "127.0.0.1", Port = 1, Username = "maid_chan_113", AutoReconnect = false });
        client.World.Inventory.SetSlot(0, 36, new ItemStack(Blocks.Chest, 1, 0));
        var game = new MineClaw.Bot.Game.MinecraftGameClient(client);
        Assert.True(ChestStore.MayPlaceChest(game));
        client.NoOverflowChests = true;
        Assert.False(ChestStore.MayPlaceChest(game));
        Assert.False(ChestStore.MayPlaceChest(new ClaimAwareGameClient(game, new TestLeases().ViewFor("maid_chan_113"))));
    }

    [Fact]
    public async Task A_deposit_behind_an_exclusive_user_waits_a_moment_then_reports_busy_with_the_holder()
    {
        var board = new TestLeases();
        var organiser = board.ViewFor("maid_chan_67");
        Assert.True((await organiser.TryClaimAsync(ClaimKey.Chest(LogChest))).Ok);
        var c = Body("maid_chan_101");
        var game = new ClaimAwareGameClient(c, board.ViewFor(c.Username)) { ExclusiveWait = TimeSpan.FromMilliseconds(300) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await game.DepositAllAsync(LogChest);
        Assert.False(r.Ok);
        Assert.Contains("busy: maid_chan_67 is using chest 5,65,5", r.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"waited {sw.Elapsed}");
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("deposit"));
    }

    /// <summary>
    /// Live 11-03 (Story builders): "busy: maid_chan_102 is using chest 218,64,400" failed build jobs outright, though the holder's
    /// session is a few seconds. A withdraw waits its turn (bounded), then says busy with the holder and how long it waited.
    /// </summary>
    [Fact]
    public async Task A_withdraw_behind_another_bodys_chest_session_waits_its_turn_then_says_busy()
    {
        var board = new TestLeases();
        var other = board.ViewFor("maid_chan_102");
        Assert.True((await other.TryClaimAsync(ClaimKey.Chest(LogChest))).Ok);
        var c = Body("maid_chan_109");
        c.Results["withdraw"] = () => MineClaw.Client.ActionResult.Success("took 4 planks", 4);
        var game = new ClaimAwareGameClient(c, board.ViewFor(c.Username)) { WithdrawWait = TimeSpan.FromSeconds(5) };
        _ = Task.Run(async () => { await Task.Delay(300); await other.ReleaseAsync(ClaimKey.Chest(LogChest)); });
        var r = await game.WithdrawAsync(Blocks.Planks, 4, LogChest);
        Assert.True(r.Ok, r.Message);
        Assert.Contains(c.CallList, x => x.StartsWith("withdraw"));

        Assert.True((await other.TryClaimAsync(ClaimKey.Chest(LogChest))).Ok); // held for good this time
        var bounded = new ClaimAwareGameClient(c, board.ViewFor(c.Username)) { WithdrawWait = TimeSpan.FromMilliseconds(300) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var busy = await bounded.WithdrawAsync(Blocks.Planks, 4, LogChest);
        Assert.False(busy.Ok);
        Assert.Contains("busy: maid_chan_102 is using chest 5,65,5; waited", busy.Message);
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 250, 3000);
    }

    /// <summary>
    /// A double chest is one window (user report 10-29): an exclusive user who came in through one half blocks a deposit through
    /// the other, and the four deposit slots are the pair's, not four per half.
    /// </summary>
    [Fact]
    public async Task Both_halves_of_a_double_chest_share_one_lease_and_one_set_of_deposit_slots()
    {
        var board = new TestLeases();
        var east = new WPos(LogChest.X + 1, LogChest.Y, LogChest.Z);
        Assert.True((await board.ViewFor("maid_chan_67").TryClaimAsync(ClaimKey.Chest(east))).Ok);
        var lone = Body("maid_chan_100");
        lone.World.SetBlock(east.X, east.Y, east.Z, Blocks.Chest, 0);
        var blocked = await new ClaimAwareGameClient(lone, board.ViewFor(lone.Username)) { ExclusiveWait = TimeSpan.FromMilliseconds(200) }
            .DepositAllAsync(LogChest);
        Assert.Contains("busy: maid_chan_67 is using chest", blocked.Message);
        await board.ViewFor("maid_chan_67").ReleaseAsync(ClaimKey.Chest(east));

        int active = 0, peak = 0;
        var bodies = Enumerable.Range(0, 8).Select(i =>
        {
            var c = Body($"maid_chan_{101 + i}");
            c.World.SetBlock(east.X, east.Y, east.Z, Blocks.Chest, 0);
            c.Effects["deposit"] = () =>
            {
                int now = Interlocked.Increment(ref active);
                int seen;
                while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                Thread.Sleep(80);
                Empty(c);
                Interlocked.Decrement(ref active);
            };
            return (Fake: c, Game: new ClaimAwareGameClient(c, board.ViewFor(c.Username)), At: i % 2 == 0 ? LogChest : east);
        }).ToList();
        var results = await Task.WhenAll(bodies.Select(b => Task.Run(() => b.Game.DepositAllAsync(b.At))));
        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        Assert.InRange(peak, 1, 4); // was: four per half, eight bodies crowding one window
    }
}

/// <summary>A minimal shared lease board (owner views over one table), as the hive's ClaimBoard behaves for these tests.</summary>
internal sealed class TestLeases
{
    private readonly Dictionary<ClaimKey, string> _held = new();
    public IClaimBoard ViewFor(string owner) => new View(this, owner);

    private sealed class View(TestLeases b, string owner) : IClaimBoard
    {
        public string Owner => owner;
        public bool IsFree(ClaimKey key) { lock (b._held) return !b._held.Any(kv => kv.Value != owner && kv.Key.ConflictsWith(key)); }
        public string? HolderOf(ClaimKey key) { lock (b._held) return b._held.FirstOrDefault(kv => kv.Value != owner && kv.Key.ConflictsWith(key)).Value; }
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
        {
            lock (b._held)
            {
                var other = b._held.FirstOrDefault(kv => kv.Value != owner && kv.Key.ConflictsWith(key));
                if (other.Value != null) return Task.FromResult(ClaimResult.Refused(other.Value, DateTimeOffset.MaxValue));
                b._held[key] = owner;
                return Task.FromResult(ClaimResult.Granted(owner, DateTimeOffset.MaxValue));
            }
        }
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default)
        {
            lock (b._held) if (b._held.TryGetValue(key, out var o) && o == owner) b._held.Remove(key);
            return Task.CompletedTask;
        }
    }
}
