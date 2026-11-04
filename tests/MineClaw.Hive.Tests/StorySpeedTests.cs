using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>Story Mode's Fast / Faster (StoryColony.Speed.cs): the option, the pawn bodies' multiplier, the step-down alert, the
/// Speed effects on 1.12.2.</summary>
public class StorySpeedTests
{
    [Fact]
    public void Speed_defaults_to_normal_and_only_the_colony_pawns_get_the_multiplier()
    {
        var (c, h, _) = StoryRig.Make(2);
        Assert.Equal("normal", (string?)c.OptionsJson()["speed"]);
        Assert.True((bool)c.OptionsJson()["speed_effect"]!);
        c.Tick();
        Assert.All(c.Pawns.Keys, p => Assert.Equal(1, h.Speeds[p]));

        Assert.Null(c.SetSpeed("fast", null));
        Assert.Equal("fast", (string?)c.OptionsJson()["speed"]);
        Assert.All(c.Pawns.Keys, p => Assert.Equal(2, h.Speeds[p]));
        Assert.Null(c.SetSpeed("faster", null));
        Assert.All(c.Pawns.Keys, p => Assert.Equal(3, h.Speeds[p]));
        Assert.Equal(2, h.Speeds.Count); // nobody else (main, other hives) is ever touched
        var pawn = c.State()["pawns"]![0]!["speed"]!;
        Assert.Equal("faster", (string?)pawn["level"]);
        Assert.Equal(3, (double)pawn["multiplier"]!);

        Assert.NotNull(c.SetSpeed("ludicrous", null)); // only normal | fast | faster
        Assert.Equal("faster", c.Meta.Speed);
        Assert.Empty(h.Effects); // b1.8.1 / no op: no Speed effect
    }

    [Fact]
    public void A_stepped_down_pawn_raises_one_alert()
    {
        var (c, h, _) = StoryRig.Make(2);
        c.SetSpeed("faster", null);
        h.SpeedCaps[c.P(0)] = 2; // the server kept rejecting it: the body stepped itself down
        c.Tick();
        c.Tick();
        var alerts = c.State()["alerts"]!.AsArray().Select(a => (string?)a!["text"]).Where(t => t!.StartsWith("Server rejected")).ToList();
        Assert.Equal(["Server rejected faster movement — using Fast"], alerts);
        Assert.Equal("fast", (string?)c.State()["pawns"]![0]!["speed"]!["level"]);
        h.SpeedCaps[c.P(1)] = 2; // a second pawn at the same level: no second alert
        c.Tick();
        Assert.Single(c.State()["alerts"]!.AsArray(), a => ((string?)a!["text"])!.StartsWith("Server rejected"));
    }

    [Fact]
    public void Speed_effects_are_renewed_on_1122_with_an_op_and_cleared_back_at_normal()
    {
        var time = new ManualTime();
        var (c, h, _) = StoryRig.Make(1, MineClaw.Data.Versioning.Release1122GameData.Instance, time);
        h.EffectsAvailable = true;
        c.SetSpeed("fast", null);
        Assert.Equal([(c.P(), 1)], h.Effects);              // Speed II for Fast
        c.Tick();
        Assert.Single(h.Effects);                           // not again before the renewal
        time.Now += StoryColony.EffectRenew;
        c.Tick();
        Assert.Equal(2, h.Effects.Count);
        c.SetSpeed("faster", null);
        Assert.Equal((c.P(), 2), h.Effects[^1]);            // Speed III for Faster
        c.SetSpeed("normal", null);
        Assert.Equal((c.P(), -1), h.Effects[^1]);           // cleared
        int n = h.Effects.Count;
        c.Tick();
        Assert.Equal(n, h.Effects.Count);

        // The sub-option off: no effects at all, and the one given is cleared.
        c.SetSpeed("faster", null);
        c.SetSpeed(null, false);
        Assert.Equal((c.P(), -1), h.Effects[^1]);
        Assert.False((bool)c.OptionsJson()["speed_effect"]!);
    }

    [Fact]
    public void A_released_colony_and_a_leaving_pawn_go_back_to_normal()
    {
        var (c, h, _) = StoryRig.Make(2);
        c.SetSpeed("faster", null);
        c.ResetSpeed([c.P(1)]);
        Assert.Equal(1, h.Speeds[c.P(1)]);
        Assert.Equal(3, h.Speeds[c.P(0)]);
        c.ResetSpeed();
        Assert.All(c.Pawns.Keys, p => Assert.Equal(1, h.Speeds[p]));
        Assert.Equal(1, StoryColony.SpeedMultiplier("normal"));
        Assert.Equal("fast", StoryColony.SpeedLevel(2));
    }
}
