using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryTickSchedulingTests
{
    private sealed class Host : IStoryHost
    {
        private readonly FakeStoryHost _inner = new(new FakeStoryWorld());
        public Action? ReadingWorld;
        public IStoryWorld? World { get { ReadingWorld?.Invoke(); return _inner.World; } }
        public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer) => _inner.Spawn(colony, n, prefer);
        public int Release(string colony) => _inner.Release(colony);
        public PawnLive Live(string account) => _inner.Live(account);
        public string? StartPawn(string account, string colony) => _inner.StartPawn(account, colony);
        public void Interrupt(string account, string why) => _inner.Interrupt(account, why);
    }

    [Fact]
    public async Task Overlapping_director_ticks_return_without_queuing_on_the_colony_and_the_next_tick_still_runs()
    {
        var host = new Host();
        using var colonies = new StoryColonies(host);
        Assert.Null(colonies.Create("Scheduling", 1, null).Error);
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        int reads = 0;
        host.ReadingWorld = () =>
        {
            if (Interlocked.Increment(ref reads) != 1) return;
            entered.Set();
            proceed.Wait(TimeSpan.FromSeconds(5));
        };
        var first = Task.Run(colonies.Tick);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            // While reconciliation owns its real colony monitor, timer callbacks must complete rather than wait for it.
            await Task.Run(() => { for (int i = 0; i < 30; i++) colonies.Tick(); }).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, Volatile.Read(ref reads));
        }
        finally { proceed.Set(); await first.WaitAsync(TimeSpan.FromSeconds(2)); }
        int before = reads;
        colonies.Tick();
        Assert.True(reads > before);
    }

    /// <summary>A clock whose timers run only when the test says so (no real waiting).</summary>
    private sealed class StepClock : TimeProvider
    {
        public sealed class Pending(TimerCallback run, object? state, TimeSpan due) : ITimer
        {
            public TimeSpan Due { get; private set; } = due;
            public bool Disposed { get; private set; }
            public void Fire() { if (!Disposed) run(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return true; }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }

        public List<Pending> Timers { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new Pending(callback, state, dueTime);
            Timers.Add(t);
            return t;
        }
    }

    [Fact]
    public void A_superseded_pawn_is_restarted_by_the_nudge_before_the_next_tick()
    {
        var clock = new StepClock();
        var host = new FakeStoryHost(new FakeStoryWorld());
        using var colonies = new StoryColonies(host, time: clock);
        var (c, err) = colonies.Create("Nudge", 2, new P3(0, 64, 0));
        Assert.Null(err);
        string mover = c!.PawnNames()[0], other = c.PawnNames()[1];
        c.Next(mover); // at a job: the order cuts its script
        host.Lives[other] = host.Lives[other] with { ScriptRunning = false }; // its script ended on its own: the tick's to restart
        host.Started.Clear();

        long cuts = c.Cuts;
        var order = new System.Text.Json.Nodes.JsonObject { ["kind"] = "move", ["target"] = new P3(5, 64, 5).Json() };
        Assert.Null(c.GiveOrder(c.Resolve([mover]).Pawns, order, queue: false).Error);
        Assert.Equal(cuts + 1, c.Cuts);
        host.Lives[mover] = host.Lives[mover] with { ScriptRunning = false }; // the interrupt stopped the script

        colonies.Nudge(c);
        colonies.Nudge(c); // a second order inside the window shares the one pass
        var timer = Assert.Single(clock.Timers);
        Assert.Equal(StoryColonies.NudgeDelay, timer.Due);
        Assert.Empty(host.Started); // nothing restarts inside the request

        timer.Fire(); // about 150 ms later; no tick has run
        Assert.Contains(mover, host.Started);
        Assert.True(timer.Disposed);
        // The restart pass is the watchdog's own: a pawn whose script ended is started too, and never one twice within 5 s.
        Assert.Equal(2, host.Started.Count);
        colonies.Nudge(c);
        clock.Timers[^1].Fire();
        Assert.Equal(2, host.Started.Count);
    }

    [Fact]
    public void A_failed_reconciliation_releases_the_tick_gate_for_the_next_pass()
    {
        var host = new Host();
        using var colonies = new StoryColonies(host);
        Assert.Null(colonies.Create("Recovery", 1, null).Error);
        int reads = 0;
        host.ReadingWorld = () =>
        {
            if (Interlocked.Increment(ref reads) == 1) throw new InvalidOperationException("world unavailable during this tick");
        };
        colonies.Tick(); // The director logs the source failure and its timer remains usable.
        int before = reads;
        colonies.Tick();
        Assert.True(reads > before);
    }
}
