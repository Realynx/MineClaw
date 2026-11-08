using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryWorkFallbackTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Waiting_for_a_pickaxe_does_not_stop_a_lower_priority_cut_and_new_work_wakes_the_idle_hold()
    {
        var clock = new Clock();
        var (c, h, w) = StoryRig.Make(time: clock);
        c.Meta.Home = null; // No wandering destination: exercise the idle hold and its wakeup.
        var p = c.Pawns.Values.Single();
        foreach (var kind in WorkTypes.All) p.Priorities[kind] = 0;
        p.Priorities[WorkTypes.Mine] = 1;
        p.Priorities[WorkTypes.Chop] = 4;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0);
        c.Designate("add", "mine", null, [new P3(5, 63, 5)], null);
        var idle = c.Next();
        Assert.StartsWith("idle: ", idle["label"]!.GetValue<string>());
        Assert.NotEqual(StoryColony.NothingToDo, idle["label"]!.GetValue<string>());

        clock.Now += StoryColony.WakeAfter + TimeSpan.FromSeconds(1);
        w.Set(3, 64, 3, 31, 1);
        c.Designate("add", "cut", null, [new P3(3, 64, 3)], null);
        c.Tick();
        Assert.Contains(h.Interrupted, message => message.Contains("new work"));
        var job = c.Next();
        Assert.Equal("dig", job["kind"]!.GetValue<string>());
        Assert.Equal("3,64,3", job["at"]!.GetValue<string>());
        Assert.Equal("waiting", c.Designations.Single(d => d.Kind == "mine").Status);
    }
}
