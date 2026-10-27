using MineClaw.Agent.Events;
using MineClaw.Bot.Brainstem;

namespace MineClaw.Bot.Tests;

/// <summary>Live 2026-10-27 (task #5): a job that claims to work while the body stands still must wake the brain.</summary>
public class StallWatchdogTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 27, 14, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StallWatchdog Dog, List<GameEvent> Posted, Clock Clock, Func<ClaimedWork?>[] Work, string[] Body) Make()
    {
        var clock = new Clock();
        var posted = new List<GameEvent>();
        var work = new Func<ClaimedWork?>[] { () => new ClaimedWork("goto #16") };
        var body = new[] { "15,64,-2|1|goto" };
        var dog = new StallWatchdog(() => work[0](), () => body[0], () => "goto: walking there", posted.Add, clock);
        return (dog, posted, clock, work, body);
    }

    [Fact]
    public void An_idle_body_under_a_running_job_raises_one_stalled_wake_after_45s()
    {
        var (dog, posted, clock, _, _) = Make();
        dog.Tick();
        clock.Now += TimeSpan.FromSeconds(40);
        dog.Tick();
        Assert.Empty(posted);
        clock.Now += TimeSpan.FromSeconds(6);
        dog.Tick();
        var e = Assert.Single(posted);
        Assert.StartsWith("stalled: goto #16 idle 46s", e.Text);
        Assert.Contains("last: goto: walking there", e.Text);
        Assert.True(e.Wakes && e.MustWake);
        Assert.True(e.Priority < new MineClaw.Agent.AgentOptions().InterruptPriority); // never interrupts the body
        clock.Now += TimeSpan.FromSeconds(60);
        dog.Tick();
        Assert.Single(posted); // once per stall
    }

    [Fact]
    public void Movement_or_a_new_job_rearms_it_and_passive_work_never_stalls()
    {
        var (dog, posted, clock, work, body) = Make();
        dog.Tick();
        for (int i = 0; i < 10; i++)
        {
            clock.Now += TimeSpan.FromSeconds(10);
            body[0] = $"{15 + i},64,-2|1|goto"; // walking
            dog.Tick();
        }
        Assert.Empty(posted);
        work[0] = () => new ClaimedWork("follow #20", Passive: true);
        dog.Tick();
        clock.Now += TimeSpan.FromMinutes(5);
        dog.Tick();
        Assert.Empty(posted);
        work[0] = () => null; // idle: nothing claims to work
        clock.Now += TimeSpan.FromMinutes(5);
        dog.Tick();
        Assert.Empty(posted);
    }
}
