using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-28 00:08: "not retried: withdraw failed here 13s ago: busy: fighting spider 13.1m (guarding kiwi): 0 hits" and a worker's
/// "harvest_generator failed 2x the same way: ... busy: fighting spider": a combat reflex briefly holding the body is never
/// remembered as the job failing.
/// </summary>
public class CombatBusyFailureTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    [Theory]
    [InlineData("busy: fighting spider 13.1m (guarding kiwi): 0 hits, took 0, hp 20", true)]
    [InlineData("generator: dug 0/300 at 5,64,7 (have 0 cobblestone); busy: fighting spider 13.1m (guarding kiwi): 0 hits", true)]
    [InlineData("interrupted: fighting skeleton 3.9m (skeleton in sight): 0 hits, took 0, hp 20", true)]
    [InlineData("busy: maid_chan_67 is using chest 14,64,-5", false)]
    [InlineData("no path to within 1 of (8, 64, -5)", false)]
    public void Transient_combat_busy_is_recognised(string text, bool transient) =>
        Assert.Equal(transient, ToolCallGuard.IsTransientCombat(text));

    [Fact]
    public void A_busy_fighting_failure_is_not_remembered()
    {
        var tools = new ToolRegistry();
        var time = new ManualTime();
        var acts = new ActivityManager(tools, new EventQueue(), time);
        var guard = new ToolCallGuard(acts, time);
        var args = J(new { item = "cobblestone", x = 14, y = 64, z = -5 });
        guard.RecordFailure("withdraw", args, "busy: fighting spider 13.1m (guarding kiwi): 0 hits, took 0, hp 20");
        Assert.Null(guard.Check("withdraw", args));
        guard.RecordFailure("withdraw", args, "no cobblestone in the chest at 14,64,-5");
        Assert.NotNull(guard.Check("withdraw", args));
    }
}
