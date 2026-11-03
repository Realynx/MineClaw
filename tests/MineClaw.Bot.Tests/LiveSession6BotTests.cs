using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Brainstem;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>Live 10-27 23:31 (alice CT 114): Fox's tossed planks, pressure plates and 12 fences were never acknowledged; gaze addressing.</summary>
public class LiveSession6BotTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 27, 23, 31, 6, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static readonly Vec3d FoxFeet = new(8.5, 64, 3.5);
    private static HandoverWatch.PlayerView Fox() => new("PoofimaFox", FoxFeet, 0, 0);
    private static Vec3d Toss => FoxFeet + new Vec3d(0, 1.32, 0.1); // EntityHuman.a: locY - 0.3 + 1.62

    [Fact]
    public void Replayed_tosses_wake_once_debounced_even_without_a_pickup()
    {
        var clock = new Clock();
        var watch = new HandoverWatch(clock);
        // 23:31:06.8 hand-over: 5 x1 / 23:31:08.9 72 x10 / 23:31:10.2 85 x12 (thrown by PoofimaFox; the bot, mining in the gen,
        // never picked them up).
        Assert.Equal("PoofimaFox", watch.OnItemSpawn(901, Toss, [Fox()], Blocks.Planks, 1));
        clock.Advance(2.05);
        Assert.Empty(watch.Poll(_ => 0));
        Assert.Equal("PoofimaFox", watch.OnItemSpawn(902, Toss, [Fox()], Blocks.WoodenPressurePlate, 10));
        clock.Advance(1.3);
        Assert.Empty(watch.Poll(_ => 0));
        Assert.Equal("PoofimaFox", watch.OnItemSpawn(903, Toss, [Fox()], Blocks.Fence, 12));
        clock.Advance(2.9);
        Assert.Empty(watch.Poll(_ => 0)); // still inside the 3 s of tosses
        clock.Advance(0.2);
        var (text, player) = Assert.Single(watch.Poll(_ => 0));
        Assert.Equal("PoofimaFox", player);
        // Not picked up, so not "gave you": still one wake, debounced.
        Assert.Equal("PoofimaFox tossed you 1 planks, 10 wooden pressure plate, 12 fence, but it's still on the ground: not in your " +
                     "inventory until you pick it up (collect_drops)" + HandoverWatch.AckHint, text);
        // A later pickup of a counted toss is not a second hand-over.
        Assert.True(watch.OnPickup(903, Blocks.Fence, 12));
        clock.Advance(5);
        Assert.Empty(watch.Poll(_ => 12));
        // 23:31:37.6: another plank, 27 s later: its own (one) wake.
        Assert.Equal("PoofimaFox", watch.OnItemSpawn(904, Toss, [Fox()], Blocks.Planks, 1));
        clock.Advance(3.1);
        Assert.StartsWith("PoofimaFox tossed you 1 planks", Assert.Single(watch.Poll(_ => 0)).Text); // its own wake; still not picked up
    }

    [Fact]
    public void A_toss_wakes_with_what_actually_reached_my_inventory()
    {
        // A different body collects the arrows; only the seeds reach the recipient's inventory.
        var clock = new Clock();
        var watch = new HandoverWatch(clock);
        var player = new HandoverWatch.PlayerView("player_one", FoxFeet, 0, 0);
        Assert.Equal("player_one", watch.OnItemSpawn(911, Toss, [player], Items.Arrow, 2));
        Assert.Equal("player_one", watch.OnItemSpawn(912, Toss, [player], Items.Bone, 1));
        Assert.Equal("player_one", watch.OnItemSpawn(913, Toss, [player], Items.Seeds, 3));
        watch.OnTakenByOther(911);           // the clone's Collect Item
        Assert.True(watch.OnPickup(913, Items.Seeds, 3));
        clock.Advance(3.1);
        var text = Assert.Single(watch.Poll(_ => 0)).Text;
        Assert.StartsWith("player_one gave you 3 wheat seeds; 1 bone still on the ground: not in your inventory until you pick it up", text);
        Assert.Contains("2 arrow was picked up by someone else, not you", text);
        Assert.DoesNotContain("gave you 2 arrow", text);
        Assert.DoesNotContain("1 bone,", text.Split(';')[0]);
    }

    [Fact]
    public void A_far_toss_still_counts_only_on_pickup()
    {
        var clock = new Clock();
        var watch = new HandoverWatch(clock);
        Assert.Equal("PoofimaFox", watch.OnItemSpawn(905, Toss, [Fox()])); // no stack given: the bot layer saw it far away
        clock.Advance(4);
        Assert.Empty(watch.Poll(_ => 0));
        Assert.True(watch.OnPickup(905, Items.Stick, 4));
        clock.Advance(3.1);
        Assert.StartsWith("PoofimaFox gave you 4 stick", Assert.Single(watch.Poll(_ => 4)).Text);
    }

    [Fact]
    public void A_plain_hand_over_is_one_cheap_thank_you_and_a_promise_goes_to_the_brain()
    {
        var triage = new Triage(new TriageOptions());
        var plain = new GameEvent(EventKind.Notice, "PoofimaFox gave you 12 fence" + HandoverWatch.AckHint, "PoofimaFox", EventPriority.Normal);
        var d = triage.Choose(new EventBatch([plain]), BudgetMode.Normal);
        Assert.Equal(ModelTier.Cheap, d.Tier);
        Assert.True(d.CanEscalate);
        var promise = plain with { Text = "PoofimaFox gave you 21 wheat; you said you'd make bread when PoofimaFox handed it over (now 21 wheat)" };
        Assert.Equal(ModelTier.Main, triage.Choose(new EventBatch([promise]), BudgetMode.Normal).Tier);
        Assert.True(plain.Wakes);
        Assert.False(BotAgent.IsQuietBatch(new EventBatch([plain]))); // a player is on the other end: its text is chat
    }

    [Fact]
    public void Gaze_picks_whom_the_speaker_looks_at()
    {
        // Robin at the origin looking east (+X: yaw -90); maid_chan 4 blocks east, robinchan 4 blocks south.
        var robin = new HandoverWatch.PlayerView("kiwi", new Vec3d(0.5, 64, 0.5), -90, 0);
        var maid = ("maid_chan", new Vec3d(4.5, 64, 0.5));
        var other = ("robinchan", new Vec3d(0.5, 64, 4.5));
        Assert.Equal("maid_chan", GazeAddress.Target(robin, [maid, other]));
        Assert.Equal("robinchan", GazeAddress.Target(robin with { Yaw = 0 }, [maid, other])); // yaw 0 = south (+Z)
        Assert.Null(GazeAddress.Target(robin with { Yaw = 180 }, [maid, other]));        // looking north at nobody
        Assert.Null(GazeAddress.Target(robin, [("maid_chan", new Vec3d(20.5, 64, 0.5))]));  // too far
        // Nobody in the look cone, but standing right next to exactly one.
        Assert.Equal("maid_chan", GazeAddress.Target(robin with { Yaw = 180 }, [("maid_chan", new Vec3d(1.6, 64, 0.5)), other]));
    }
}
