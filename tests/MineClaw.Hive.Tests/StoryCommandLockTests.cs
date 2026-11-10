using System.Diagnostics;
using System.Text.Json.Nodes;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The colony's bounded lock waits (<see cref="StoryColony.TryCommand"/>, <see cref="StoryColony.TryRead"/>): what an HTTP route
/// runs a command or a read in, so that it answers "busy, not applied" while its caller still listens instead of waiting on the
/// director for as long as it takes. The lock is held by a gate (a pick parked in the host), never by a clock.
/// </summary>
public sealed class StoryCommandLockTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);
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

    /// <summary>A pick parked in the host's <c>Live()</c>: another thread holds the colony lock until this is disposed.</summary>
    private sealed class Held : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(), _release = new();
        private readonly GatedHost _host;
        private readonly Task _pick;

        public Held(StoryColony c, GatedHost host)
        {
            _host = host;
            string pawn = c.PawnNames()[0];
            int first = 1;
            host.InLive = () =>
            {
                if (Interlocked.Exchange(ref first, 0) == 0) return; // only the pick's first look parks
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(30));
            };
            _pick = Task.Run(() => c.Next(pawn));
            Assert.True(_entered.Wait(TimeSpan.FromSeconds(30)), "the pick never reached the host");
        }

        public void Dispose()
        {
            _release.Set();
            _pick.Wait(TimeSpan.FromSeconds(30));
            _host.InLive = null;
            _entered.Dispose();
            _release.Dispose();
        }
    }

    /// <summary>One colony with one pawn and a chest block in its world; the caller disposes the colonies.</summary>
    private static StoryColonies Colony(out StoryColony c, out GatedHost host, StoryStore? store = null)
    {
        var world = new FakeStoryWorld();
        world.Set(Chest.X, Chest.Y, Chest.Z, 54);
        host = new GatedHost(world);
        var colonies = new StoryColonies(host, store);
        var (made, err) = colonies.Create("Lock", 1, new P3(0, 64, 0));
        Assert.Null(err);
        c = made!;
        return colonies;
    }

    [Fact]
    public void A_command_that_cannot_take_the_colony_within_its_budget_is_refused_and_nothing_of_it_runs()
    {
        using var colonies = Colony(out var c, out var host);
        bool ran = false;
        using (new Held(c, host))
        {
            Assert.False(c.TryCommand(Short, () => ran = true));
            Assert.False(c.TryCommand(TimeSpan.Zero, () => ran = true)); // a spent budget is one try, never a wait
            Assert.False(c.TryCommand(TimeSpan.FromMilliseconds(-5), () => ran = true));
            Assert.False(c.TryCommand(Short, () => c.StorageOp("set", [Chest], null, "critical", null)));
        }
        // Refused means not applied: nothing of it was left waiting to run once the lock came free.
        Assert.False(ran);
        Assert.Empty(c.Storages);
        Assert.True(c.TryCommand(Short, () => c.StorageOp("set", [Chest], null, "critical", null)));
        Assert.Equal("critical", Assert.Single(c.Storages).Priority);
    }

    [Fact]
    public void A_read_that_cannot_take_the_colony_within_its_budget_reads_nothing()
    {
        using var colonies = Colony(out var c, out var host);
        JsonObject? state = null;
        using (new Held(c, host)) Assert.False(c.TryRead(Short, () => state = c.State()));
        Assert.Null(state);
        Assert.True(c.TryRead(Short, () => state = c.State()));
        Assert.Equal(c.Id, state!["id"]!.GetValue<string>());
    }

    [Fact]
    public void An_op_inside_keeps_its_own_lock_and_the_revision_read_beside_it_covers_its_change()
    {
        using var colonies = Colony(out var c, out _);
        long before = c.Rev, stamped = -1;
        Assert.True(c.TryCommand(Short, () =>
        {
            Assert.Null(c.StorageOp("set", [Chest], null, "critical", "Wood").Error); // the op takes the lock again: the monitor is re-entrant
            stamped = c.RevHint;
        }));
        Assert.Equal(c.Rev, stamped);
        Assert.Equal(c.Rev, c.RevHint);
        long row = Assert.Single(c.Storages).Rev;
        Assert.True(row > before && row <= stamped, $"the row's revision {row} is not in ({before}, {stamped}]");
        // So a reader that was at any revision before the command finds the row in the delta that reaches the stamp.
        var delta = c.State(before);
        Assert.True(delta["rev"]!.GetValue<long>() >= stamped);
        Assert.Equal(Chest.Json().ToJsonString(), Assert.Single(delta["storages"]!.AsArray())!["chest"]!.ToJsonString());
    }

    [Fact]
    public void A_command_that_throws_gives_the_colony_back()
    {
        using var colonies = Colony(out var c, out _);
        Assert.Throws<FormatException>(() => c.TryCommand(Short, () => throw new FormatException("a field of the wrong type")));
        // From another thread: the monitor is re-entrant, so this one would get in even if the lock had leaked.
        bool taken = false, ran = false;
        var other = new Thread(() => taken = c.TryCommand(TimeSpan.Zero, () => ran = true));
        other.Start();
        other.Join();
        Assert.True(taken);
        Assert.True(ran);
    }

    [Fact]
    public void The_perf_figures_count_the_waits_the_refusals_and_what_held_the_colony()
    {
        using var db = AgentDb.Open(null);
        using var colonies = Colony(out var c, out var host, new StoryStore(db));
        Assert.Null(c.StorageOp("set", [Chest], null, null, null).Error);
        StoryPerf.TakeLocks(); // start over: what ran before is another test's
        using (new Held(c, host)) // a pick, as long as the gate holds it
        {
            Assert.False(c.TryCommand(Short, () => { }));
            Assert.False(c.TryRead(Short, () => { }));
        }
        Assert.True(c.TryRead(Short, () => c.State())); // a third wait, and a state build
        c.Save(force: true);
        colonies.Tick(); // its first pass checks each storage record against the bot's chest memory

        // At least: other test classes pick, tick and build states at the same time; only this class takes the figures.
        var s = StoryPerf.TakeLocks();
        Assert.True(s.LockWait.Count >= 3, $"lock waits {s.LockWait.Count}");
        Assert.True(s.BusyCommands >= 1, $"busy commands {s.BusyCommands}");
        Assert.True(s.BusyReads >= 1, $"busy reads {s.BusyReads}");
        Assert.True(s.StateBuilds.Count >= 1, $"state builds {s.StateBuilds.Count}");
        Assert.True(s.Saves.Count >= 1, $"saves {s.Saves.Count}");
        Assert.True(s.SeenChest.Count >= 1, $"chest-memory reads {s.SeenChest.Count}");
        Assert.True(s.LongestPick > TimeSpan.Zero);
        Assert.True(s.LongestTick > TimeSpan.Zero);
    }

    [Fact]
    public void A_tick_reports_its_wait_for_the_lock_and_each_phase_and_a_pick_its_wait()
    {
        using var db = AgentDb.Open(null);
        using var colonies = Colony(out var c, out var host, new StoryStore(db));
        StoryPerf.TakeInside(); // start over
        colonies.Tick();
        c.Next("nobody"); // a pick: it waits for the lock like any other
        var line = StoryPerf.TakeInside();
        Assert.Matches(@"^pick lock-wait [1-9]\d*x \d+ ms max \d+ ms, tick lock-wait [1-9]\d*x \d+ ms max \d+ ms, tick phases: ", line);
        var phases = line[line.IndexOf("tick phases:", StringComparison.Ordinal)..];
        foreach (var phase in new[] { "leases", "reconcile", "idle-audit", "start-pawns", "save" }) Assert.Matches($@"\b{phase} [1-9]\d*x \d+ ms max \d+ ms", phases);
    }

    [Fact]
    public void A_meter_keeps_the_count_the_total_and_the_longest_and_starts_over_when_taken()
    {
        var meter = new StoryPerf.Meter();
        long now = Stopwatch.GetTimestamp();
        meter.Note(now - Stopwatch.Frequency);     // began a second ago
        meter.Note(now - 3 * Stopwatch.Frequency); // began three seconds ago
        var taken = meter.Take();
        Assert.Equal(2, taken.Count);
        Assert.True(taken.Longest >= TimeSpan.FromSeconds(3));
        Assert.True(taken.Total >= TimeSpan.FromSeconds(4));
        Assert.True(taken.Longest < taken.Total);
        Assert.Equal(new StoryPerf.Sample(0, TimeSpan.Zero, TimeSpan.Zero), meter.Take());
    }
}
