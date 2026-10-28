using MineClaw.Bot.Bodies;

namespace MineClaw.Bot.Tests;

/// <summary>
/// One brain, many bodies, step 2: each body runs under its own supervisor loop. A crash restarts only that body (after a
/// backoff) and <see cref="BodiesOptions.MaxCrashes"/> crashes in the window retire it; a duplicate-login kick ends in
/// Occupied without a reconnect; a lost connection reconnects.
/// </summary>
public class BodyRunnerTests
{
    private static BodiesOptions Fast() => new()
    {
        MaxCrashes = 3, CrashWindowMinutes = 10, BackoffBaseSeconds = 0.01, BackoffMaxSeconds = 0.05, ReconnectSeconds = 0, LaunchGapSeconds = 0,
    };

    private static async Task Until(Func<bool> cond, int ms = 5000, string what = "condition")
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return; await Task.Delay(5); }
        Assert.True(cond(), "timed out waiting for " + what);
    }

    /// <summary>A body whose session either throws (a crash) or runs until stopped.</summary>
    private sealed class Probe(bool throws)
    {
        public int Connects, Runs, Resets;
        public volatile bool Throws = throws;

        public BodySession Session => new(
            _ => Task.CompletedTask,
            _ => { Interlocked.Increment(ref Connects); return Task.CompletedTask; },
            async ct =>
            {
                Interlocked.Increment(ref Runs);
                await Task.Yield();
                if (Throws) throw new InvalidOperationException("body wiring blew up");
                await Task.Delay(Timeout.Infinite, ct);
                return BodyExit.Stopped();
            },
            () => { Interlocked.Increment(ref Resets); return Task.CompletedTask; });
    }

    [Fact]
    public async Task A_throwing_body_restarts_alone_and_retires_after_MaxCrashes()
    {
        var bad = new Probe(throws: true);
        var good = new Probe(throws: false);
        await using var r1 = new BodyRunner("maid_chan_67", bad.Session, Fast());
        await using var r2 = new BodyRunner("maid_chan_69", good.Session, Fast());
        var crashes = new List<bool>();
        r1.Crashed += (_, retired) => { lock (crashes) crashes.Add(retired); };
        r2.Start();
        r1.Start();

        await r1.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BodyState.Retired, r1.State);
        Assert.Equal(3, bad.Runs);                 // started, restarted twice, then retired
        Assert.Equal(3, bad.Resets);               // its connection dropped after every crash
        Assert.Equal(3, r1.Crashes.Count);
        lock (crashes) Assert.Equal([false, false, true], crashes);

        // The healthy body never noticed: one connect, one session, still online.
        Assert.Equal(BodyState.Online, r2.State);
        Assert.Equal(1, good.Connects);
        Assert.Equal(1, good.Runs);
        Assert.True(r2.IsRunning);
        await r2.StopAsync();
        Assert.Equal(BodyState.Offline, r2.State);
    }

    [Fact]
    public async Task A_crash_that_stops_recurring_keeps_the_body_running()
    {
        var p = new Probe(throws: true);
        await using var r = new BodyRunner("maid_chan_67", p.Session, Fast());
        r.Crashed += (_, _) => p.Throws = false; // the first restart works
        r.Start();
        await Until(() => r.State == BodyState.Online && p.Runs == 2, what: "restarted session");
        Assert.Single(r.Crashes);
        Assert.True(r.IsRunning);
    }

    [Fact]
    public async Task Fault_from_a_body_part_ends_the_session_as_a_crash()
    {
        var p = new Probe(throws: false);
        await using var r = new BodyRunner("maid_chan_67", p.Session, Fast());
        r.Start();
        await Until(() => p.Runs == 1 && r.State == BodyState.Online, what: "online");
        r.Fault(new Exception("handover pump died"));
        await Until(() => p.Runs == 2, what: "restart after the fault");
        Assert.Single(r.Crashes);
        Assert.Equal(1, p.Resets);
    }

    [Fact]
    public async Task A_duplicate_login_kick_ends_in_Occupied_without_a_reconnect_and_a_lost_connection_reconnects()
    {
        int runs = 0, connects = 0;
        var exits = new Queue<BodyExit>([BodyExit.Lost("kicked: Flying is not enabled"), new BodyExit(BodyExitKind.ExternalLogin, "You logged in from another location")]);
        var session = new BodySession(_ => Task.CompletedTask, _ => { connects++; return Task.CompletedTask; },
            _ => { runs++; return Task.FromResult(exits.Dequeue()); }, () => Task.CompletedTask);
        await using var r = new BodyRunner("maid_chan_1337", session, Fast());
        BodyExit? ended = null;
        r.Ended += e => ended = e;
        r.Start();
        await r.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BodyState.Occupied, r.State);
        Assert.Equal(2, connects); // reconnected once after the plain kick, never after the duplicate login
        Assert.Equal(2, runs);
        Assert.Empty(r.Crashes);
        Assert.Equal(BodyExitKind.ExternalLogin, ended!.Kind);
    }

    [Fact]
    public async Task Connect_failures_are_retried_and_never_counted_as_crashes()
    {
        int tries = 0;
        var session = new BodySession(_ => Task.CompletedTask,
            _ => ++tries < 3 ? throw new IOException("server closed the connection") : Task.CompletedTask,
            ct => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => BodyExit.Stopped()), () => Task.CompletedTask);
        await using var r = new BodyRunner("maid_chan_67", session, Fast());
        r.Start();
        await Until(() => r.State == BodyState.Online, 8000, "online after two failed connects");
        Assert.Equal(3, tries);
        Assert.Empty(r.Crashes);
    }

    [Theory]
    [InlineData("You logged in from another location", true)]
    [InlineData("kicked: you LOGGED IN FROM ANOTHER LOCATION", true)]
    [InlineData("Server closed", false)]
    [InlineData(null, false)]
    public void Only_the_duplicate_login_kick_counts(string? reason, bool dup) => Assert.Equal(dup, BodyExit.IsDuplicateLogin(reason));
}
