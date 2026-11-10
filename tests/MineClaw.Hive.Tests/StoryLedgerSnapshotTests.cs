using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The chest panel's copy of the storage ledger (<see cref="StoryColony.ChestView"/>, <see cref="StoryColony.ChestViews"/>): read
/// without the colony's lock, so a chest click never waits for a pick, a tick or a save. A storage command shows at once; a deposit
/// shows after the next tick (about a second).
/// </summary>
public sealed class StoryLedgerSnapshotTests
{
    private static readonly P3 Chest = new(2, 64, 2);

    /// <summary>The fake host with a hook in <c>Live()</c>, which the colony calls under its lock while it picks a job.</summary>
    private sealed class GatedHost(FakeStoryWorld world) : IStoryHost
    {
        private readonly FakeStoryHost _inner = new(world);
        public Action? InLive;
        public IStoryWorld? World => _inner.World;
        public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer) => _inner.Spawn(colony, n, prefer);
        public int Release(string colony) => _inner.Release(colony);
        public PawnLive Live(string account) { InLive?.Invoke(); return _inner.Live(account); }
        public string? StartPawn(string account, string colony) => _inner.StartPawn(account, colony);
        public void Interrupt(string account, string why) => _inner.Interrupt(account, why);
    }

    private static StoryColonies Colony(out StoryColony c, out GatedHost host)
    {
        var world = new FakeStoryWorld();
        world.Set(Chest.X, Chest.Y, Chest.Z, 54);
        host = new GatedHost(world);
        var colonies = new StoryColonies(host);
        var (made, err) = colonies.Create("Ledger", 1, new P3(0, 64, 0));
        Assert.Null(err);
        c = made!;
        return colonies;
    }

    [Fact]
    public async Task A_chest_view_answers_while_another_thread_holds_the_colony()
    {
        using var colonies = Colony(out var c, out var host);
        Assert.Null(c.StorageOp("set", [Chest], null, "critical", "Ore").Error);
        string pawn = c.PawnNames()[0];
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int first = 1;
        host.InLive = () =>
        {
            if (Interlocked.Exchange(ref first, 0) == 0) return; // only the pick's first look parks, holding the colony
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        var pick = Task.Run(() => c.Next(pawn));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the pick never reached the host");
            var view = await Task.Run(() => c.ChestView(Chest)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(("critical", "Ore", c.Id), (view!.Priority, view.Label, view.Colony));
            var all = await Task.Run(c.ChestViews).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Chest, Assert.Single(all).Chest);
            Assert.Null(await Task.Run(() => c.ChestView(new P3(9, 64, 9))).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            await pick.WaitAsync(TimeSpan.FromSeconds(30));
            host.InLive = null;
        }
    }

    [Fact]
    public void A_storage_command_shows_at_once_and_a_deposit_after_the_next_tick()
    {
        var (c, _, w) = StoryRig.Make();
        w.Set(Chest.X, Chest.Y, Chest.Z, 54);
        Assert.Empty(c.ChestViews()); // a colony that never ticked still answers (its first copy)
        Assert.Null(c.StorageOp("set", [Chest], null, "preferred", null).Error);
        Assert.Equal("preferred", c.ChestView(Chest)!.Priority);

        c.AddContents(c.StorageAt(Chest)!, new Stack(4, 0, 10)); // a pawn's deposit, booked by the director
        Assert.Empty(c.ChestView(Chest)!.Contents); // the panel's copy is the last tick's
        c.Tick();
        var after = c.ChestView(Chest)!;
        Assert.Equal(new Stack(4, 0, 10), Assert.Single(after.Contents));
        Assert.NotNull(after.KnownAt);

        Assert.Null(c.StorageOp("remove", [Chest], null, null, null).Error);
        Assert.Null(c.ChestView(Chest));
        Assert.Empty(c.ChestViews());
    }
}
