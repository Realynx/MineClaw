namespace MineClaw.Scripting.Tests;

public sealed class WatchdogSchedulingTests
{
    [Fact]
    public async Task Overlapping_watchdog_ticks_do_not_queue_more_blocked_conditions_and_later_ticks_resume()
    {
        using var run = await RunningAction.Create();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        run.Session.Signals.AddCond("blocked", () =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            return false;
        }, new object());
        var first = Task.Run(run.Clock.Fire);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var overlap = Enumerable.Range(0, 30).Select(_ => Task.Run(run.Clock.Fire)).ToArray();
            await Task.WhenAll(overlap).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.Set(); await first.WaitAsync(TimeSpan.FromSeconds(2)); }

        run.Clock.Fire();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_throwing_poll_releases_the_gate_for_the_next_tick()
    {
        using var run = await RunningAction.Create();
        int calls = 0;
        run.Session.Signals.AddCond("throws", () =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new ScriptLimitException("condition failed");
            return false;
        }, new object());

        run.Clock.Fire(); // this exception passes through SignalSet.Hit to Watch's timer boundary
        run.Clock.Fire();
        Assert.Equal(2, calls);
        Assert.False(run.Session.StopToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Wall_limit_still_cancels_the_action_while_a_previous_signal_poll_is_blocked()
    {
        using var run = await RunningAction.Create(new ScriptOptions { MaxMinutes = 0.01 });
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Session.Signals.AddCond("blocked", () =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            return false;
        }, new object());
        var first = Task.Run(run.Clock.Fire);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Rig.Until(() => run.Session.Elapsed > TimeSpan.FromMilliseconds(600));
            await Task.Run(run.Clock.Fire).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(run.Session.StopToken.IsCancellationRequested);
            Assert.False(first.IsCompleted);
            await run.Session.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("time limit", run.Session.Outcome);
        }
        finally { release.Set(); await first.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    private sealed class RunningAction : IDisposable
    {
        private readonly Rig _rig = Rig.Js();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ScriptController _controller;
        internal ManualClock Clock { get; } = new();
        internal ScriptSession Session { get; private set; } = null!;

        private RunningAction(ScriptOptions? options) => _controller = new ScriptController(_rig.Host, options, Clock);

        internal static async Task<RunningAction> Create(ScriptOptions? options = null)
        {
            var run = new RunningAction(options);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run._rig.On["goto"] = Rig.Blocking(run._release, () => started.TrySetResult());
            var (session, error) = run._controller.Start(new ScriptRequest { Name = "watchdog", Code = "go('far')", Language = ScriptLanguage.Js });
            Assert.NotNull(session);
            Assert.Null(error);
            run.Session = session;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            return run;
        }

        public void Dispose()
        {
            _controller.Dispose();
            _release.TrySetResult();
            _rig.Dispose();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        internal void Fire() => _callback?.Invoke(_state);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new ManualTimer();
        }
        private sealed class ManualTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
