using MineClaw.Bot.Perception;
using MineClaw.Bot.Tests.WorldIndex;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live play: Fox said "that way" and the bot answered "I still can't tell which way 'that way' is". The speaker's facing
/// (entity look packets) now rides in the [pointing] turn context while they talk to me or gesture, and "that way" /
/// "over there" / "this direction" resolve deterministically to it.
/// </summary>
public class FacingTests
{
    /// <summary>Stone island x -3..3, z -3..3 at y 63; Fox at its west edge facing west (yaw 90) and looking down.</summary>
    private static Entity FoxAtWestEdge(WorldState w, float pitch = 75)
    {
        for (int x = -3; x <= 3; x++) for (int z = -3; z <= 3; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        return w.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(-2.5, 64, 0.5), 90, pitch, 0);
    }

    [Theory]
    [InlineData(0f, "S")]
    [InlineData(90f, "W")]
    [InlineData(-90f, "E")]
    [InlineData(180f, "N")]
    [InlineData(135f, "NW")]
    [InlineData(-45f, "SE")]
    [InlineData(450f, "W")]
    public void Yaw_maps_to_an_8_way_compass(float yaw, string compass) => Assert.Equal(compass, GestureTracker.Compass(yaw));

    [Fact]
    public void That_way_resolves_to_the_speakers_facing_and_rides_in_the_turn_context()
    {
        var c = new FakeGameClient();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        FoxAtWestEdge(c.World);
        var g = new GestureTracker(c, time: clock);
        Assert.Null(g.Line()); // neither spoke nor gestured: no facing noise

        var r = g.ResolveDirection("poofimafox", "put the cobble that way pls");
        Assert.NotNull(r);
        Assert.Equal(("that way", "W", -1, 0), r.Value);
        Assert.Equal("over there", g.ResolveDirection("PoofimaFox", "Over there!")!.Value.Phrase);
        Assert.Equal("this direction", g.ResolveDirection("PoofimaFox", "build in this direction")!.Value.Phrase);
        Assert.Null(g.ResolveDirection("PoofimaFox", "where is the chest"));
        Assert.Null(g.ResolveDirection("kiwi", "that way")); // not in view

        g.NoteSpoke("PoofimaFox", "maid_chan extend it that way");
        var line = g.Line();
        Assert.Equal("[pointing] \"that way\"=PoofimaFox facing W (down at edge)", line);
        var hint = line!["[pointing] ".Length..];
        Assert.True(Math.Ceiling(hint.Length / 3.5) <= GestureTracker.MaxFacingTokens, $"{hint.Length} chars");

        g.NoteSpoke("PoofimaFox", "ok");
        Assert.Equal("[pointing] PoofimaFox facing W (looking down at the edge)", g.Line());
        clock.Seconds(21);
        Assert.Null(g.Line()); // facing only right after they speak
    }

    [Fact]
    public void Facing_rides_with_a_gesture_and_long_names_stay_within_budget()
    {
        var c = new FakeGameClient();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        FoxAtWestEdge(c.World);
        var g = new GestureTracker(c, time: clock);
        Assert.NotNull(g.Observe(7)); // punches the edge block it looks at
        Assert.Contains("; PoofimaFox facing W (looking down at the edge)", g.Line());

        c.World.Entities.SpawnPlayer(8, "Superlongname_123", new Vec3d(0.5, 64, 0.5), -90, 0, 0);
        g.NoteSpoke("Superlongname_123", "build over there");
        var f = g.FacingHint("Superlongname_123", "build over there")!;
        Assert.Equal("\"over there\"=Superlongname_123 facing E", f);
        Assert.True(Math.Ceiling(f.Length / 3.5) <= GestureTracker.MaxFacingTokens);
    }

    [Fact]
    public void Addressed_chat_through_the_bot_adds_the_speakers_facing_to_the_turn()
    {
        var (h, c) = TestBot.Create();
        FoxAtWestEdge(c.World);
        c.RaiseChat("<PoofimaFox> claudechan that way");
        var line = h.Runtime.Agent.TurnPerception!.Invoke();
        Assert.Equal("[pointing] \"that way\"=PoofimaFox facing W (down at edge)", line);
    }
}
