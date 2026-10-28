using MineClaw.Agent.Events;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>Replay-style fixtures for the danger rules (island r=4: the east edge is x = 4, void from x = 5).</summary>
public class DangerReplayTests(ITestOutputHelper output)
{
    private static void Dump(ITestOutputHelper o, ActivityFixture a)
    {
        foreach (var e in a.Posted) o.WriteLine($"{(e.Wakes ? "WAKE" : "ctx ")} {e.Priority} {e.Text}");
    }

    [Fact]
    public void Edge_building_for_two_minutes_with_sneak_flicker_and_passing_mobs_never_wakes()
    {
        using var a = new ActivityFixture();
        int kiwi = a.Player("kiwi", 4.5, 64, -4.5);
        int zombie = 500;
        a.World.Entities.SpawnMob(zombie, MobTypes.Zombie, new Vec3d(0.5, 64, 6.5), 0, 0);
        a.Step();
        for (int i = 0; i < 480; i++) // 2 minutes at 4 Hz
        {
            double z = -4.5 + (i % 160) * 0.05;
            a.MoveTo(kiwi, 4.5 + (i % 7 == 0 ? 0.3 : 0), 64, z);
            a.World.Entities.SetMetadata(kiwi, new Dictionary<int, object> { [0] = (byte)(i % 9 < 4 ? 2 : 0) }); // sneak on/off flicker
            a.MoveTo(zombie, 0.5 + Math.Sin(i / 20.0) * 3, 64, 6.5 - i * 0.01); // wandering past, never hitting
            if (i % 20 == 0)
            {
                var p = new BlockPos(5, 63, (int)Math.Floor(z));
                a.Look(kiwi, p);
                a.Events.Swing(kiwi);
                a.World.SetBlock(p.X, p.Y, p.Z, Blocks.Cobblestone, 0); // extending the island at the edge
            }
            a.Step();
        }
        Dump(output, a);
        Assert.Equal(0, a.Wakes);
        Assert.Contains(a.Recorded, r => r.Kind == ActivityKinds.Placed && r.Player == "kiwi");
    }

    private static void FallOffEastEdge(ActivityFixture a, int id)
    {
        a.Step();
        a.Step();
        a.MoveTo(id, 5.5, 64, 0.5); // stepped off the east edge
        a.Step();
        double y = 64, v = 0;
        while (y > -8)
        {
            v += 0.08 * 5 * 4; // ~gravity, in blocks/s per 250 ms
            y -= v * 0.25;
            a.MoveTo(id, 5.5, y, 0.5);
            a.Step();
        }
        a.World.Entities.Destroy(id);
        a.Step();
    }

    [Fact]
    public void Void_fall_that_ends_in_death_is_exactly_one_wake_with_both_facts()
    {
        using var a = new ActivityFixture();
        int kiwi = a.Player("kiwi", 4.5, 64, 0.5);
        FallOffEastEdge(a, kiwi);
        a.Events.Chat("kiwi fell out of the world"); // verbatim from the live server
        a.Step();
        a.Step(10);
        Dump(output, a);
        var w = Assert.Single(a.Posted, e => e.Wakes);
        Assert.Equal("kiwi fell into the void and died (east edge)", w.Text);
        Assert.Equal(EventPriority.Normal, w.Priority); // never interrupts our job (live 10-28 03:16); still wakes a sleeping brain
        Assert.True(w.MustWake);
        var death = Assert.Single(a.Recorded, r => r.Kind == ActivityKinds.Died);
        Assert.True(death.Confidence >= 0.9);
        Assert.Equal("kiwi fell into the void (east edge)", death.Text);
    }

    [Fact]
    public void Void_fall_without_a_death_wakes_when_the_window_ends_and_a_late_death_follows_up()
    {
        using var a = new ActivityFixture();
        int kiwi = a.Player("kiwi", 4.5, 64, 0.5);
        FallOffEastEdge(a, kiwi);
        Assert.Equal(0, a.Wakes); // still held
        a.Step(5);
        Dump(output, a);
        var fall = Assert.Single(a.Posted, e => e.Wakes);
        Assert.Equal("kiwi is falling into the void (east edge)", fall.Text);
        a.Step(5);
        a.Events.Chat("kiwi fell out of the world"); // the death line arrives late
        a.Step();
        Dump(output, a);
        var wakes = a.Posted.Where(e => e.Wakes).ToList();
        Assert.Equal(2, wakes.Count);
        Assert.Equal(EventPriority.Normal, wakes[1].Priority);
        Assert.Equal("kiwi fell into the void (east edge)", wakes[1].Text);
    }

    [Fact]
    public void Zombie_hitting_four_times_at_the_edge_is_one_wake_plus_one_escalation()
    {
        using var a = new ActivityFixture();
        int kiwi = a.Player("kiwi", 4.5, 64, 0.5);
        a.World.Entities.SpawnMob(600, MobTypes.Zombie, new Vec3d(3.2, 64, 0.5), 0, 0);
        a.Step();
        for (int i = 0; i < 4; i++)
        {
            a.Events.Hurt(kiwi);
            a.Step(1);
        }
        a.World.Entities.Destroy(600);
        a.Step(9);
        Dump(output, a);
        var wakes = a.Posted.Where(e => e.Wakes).ToList();
        Assert.Equal(2, wakes.Count);
        Assert.Contains("attacked by a zombie", wakes[0].Text);
        Assert.Contains("losing a fight", wakes[1].Text);
        Assert.True(wakes[1].Priority > wakes[0].Priority);
        var fought = Assert.Single(a.Recorded, r => r.Kind == ActivityKinds.Fought);
        Assert.Equal("kiwi fought a zombie, 4 hits taken on main island", fought.Text);
    }

    [Fact]
    public void Mob_fight_inland_escalates_only_at_the_third_hit()
    {
        using var a = new ActivityFixture();
        int kiwi = a.Player("kiwi", 0.5, 64, 0.5);
        a.World.Entities.SpawnMob(601, MobTypes.Skeleton, new Vec3d(1.5, 64, 1.5), 0, 0);
        a.Step();
        a.Events.Hurt(kiwi); a.Step(1);
        Assert.Equal(1, a.Wakes);
        a.Events.Hurt(kiwi); a.Step(1);
        Assert.Equal(1, a.Wakes);
        a.Events.Hurt(kiwi); a.Step(1);
        Assert.Equal(2, a.Wakes);
        a.Events.Hurt(kiwi); a.Step(1);
        Assert.Equal(2, a.Wakes);
    }

    [Fact]
    public void Knockback_toward_the_edge_alarms_but_not_while_building_there()
    {
        using var a = new ActivityFixture();
        int kiwi = a.Player("kiwi", 2.5, 64, 0.5);
        a.Step();
        a.Events.Hurt(kiwi);
        a.Step(0.1);
        a.MoveTo(kiwi, 3.8, 64, 0.5); // shoved east fast
        a.Step(0.25);
        Assert.Equal(1, a.Wakes);
        Assert.Contains("knocked toward the void", a.Posted.Last(e => e.Wakes).Text);

        using var b = new ActivityFixture();
        int fox = b.Player("fox", 2.5, 64, 0.5);
        b.Step();
        b.Look(fox, new BlockPos(5, 63, 0));
        b.Events.Swing(fox);
        b.World.SetBlock(5, 63, 0, Blocks.Cobblestone, 0); // working the edge
        b.Step();
        b.Events.Hurt(fox);
        b.Step(0.1);
        b.MoveTo(fox, 3.8, 64, 0.5);
        b.Step(0.25);
        Assert.Equal(0, b.Wakes);
    }
}

public class DeathAndChatLineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("<poofimafox> kiwi fell out of the world", ChatLineKind.Player)]
    [InlineData("* kiwi fell out of the world", ChatLineKind.Emote)]
    [InlineData("* kiwi test", ChatLineKind.Emote)] // live /me relay
    [InlineData("kiwi whispers: hi", ChatLineKind.Whisper)]
    [InlineData("[Server] restarting soon", ChatLineKind.ServerSay)]
    [InlineData("kiwi fell out of the world", ChatLineKind.System)] // live death line, verbatim
    [InlineData("§eTom joined the game.", ChatLineKind.System)]
    public void Lines_are_classified_by_format(string raw, ChatLineKind kind) =>
        Assert.Equal(kind, new ChatLineClassifier().Classify(raw).Kind);

    [Fact]
    public void Death_patterns_parse_system_lines_only()
    {
        var c = new ChatLineClassifier();
        var p = new DeathLineParser(new ActivityOptions().DeathPatterns);
        Assert.Equal(("kiwi", "fell into the void"), p.Parse(c.Classify("kiwi fell out of the world")));
        Assert.Equal(("robin", "was slain by Zombie"), p.Parse(c.Classify("robin was slain by Zombie")));
        Assert.Null(p.Parse(c.Classify("<poofimafox> kiwi fell out of the world")));
        Assert.Null(p.Parse(c.Classify("* kiwi fell out of the world")));
        Assert.Null(p.Parse(c.Classify("Tom joined the game.")));
    }

    [Fact]
    public void Spoofed_death_lines_are_not_deaths()
    {
        using var a = new ActivityFixture();
        a.Player("kiwi", 0.5, 64, 0.5);
        a.Player("poofimafox", 1.5, 64, 1.5);
        a.Step();
        a.Events.Chat("<poofimafox> kiwi fell out of the world");
        a.Events.Chat("* kiwi fell out of the world");
        a.Step();
        a.Step(10);
        Assert.DoesNotContain(a.Recorded, r => r.Kind == ActivityKinds.Died);
        Assert.Equal(0, a.Wakes);
        Assert.Contains(a.Recorded, r => r.Kind == ActivityKinds.Chat && r.Player == "poofimafox");
    }

    [Fact]
    public void Death_line_for_an_online_player_who_vanishes_is_a_confident_death_and_one_wake()
    {
        using var a = new ActivityFixture();
        int kiwi = a.Player("kiwi", 0.5, 64, 0.5);
        a.Step();
        a.Events.Chat("kiwi fell out of the world");
        a.Step();
        a.World.Entities.Destroy(kiwi);
        a.Events.Status(kiwi, 3); // a second exact source within the dedupe window
        a.Step();
        a.Step(6);
        foreach (var e in a.Posted) output.WriteLine($"{e.Wakes} {e.Priority} {e.Text}");
        Assert.Equal(1, a.Wakes);
        var w = a.Posted.Single(e => e.Wakes);
        Assert.Equal(EventPriority.Normal, w.Priority);
        Assert.True(w.MustWake);
        Assert.Equal("kiwi fell into the void off the main island", w.Text);
        var death = Assert.Single(a.Recorded, r => r.Kind == ActivityKinds.Died);
        Assert.True(death.Confidence >= 0.9);
        Assert.Contains("kiwi fell into the void", a.Tracker.Digest.Since(a.F.Clock.GetUtcNow() - TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Death_line_for_an_offline_player_is_ignored()
    {
        using var a = new ActivityFixture();
        a.Events.Chat("kiwi fell out of the world");
        a.Step();
        a.Step(10);
        Assert.Equal(0, a.Wakes);
        Assert.DoesNotContain(a.Recorded, r => r.Kind == ActivityKinds.Died);
    }

    [Fact]
    public void Uncorroborated_death_of_a_tracked_player_is_probable_and_still_one_wake()
    {
        using var a = new ActivityFixture();
        a.Player("kiwi", 0.5, 64, 0.5); // stays visible: nothing corroborates
        a.Step();
        a.Events.Chat("kiwi was slain by Zombie");
        a.Step();
        a.Step(6);
        Assert.Equal(1, a.Wakes);
        Assert.StartsWith("probably: kiwi was slain by Zombie", a.Posted.Single(e => e.Wakes).Text);
        var d = Assert.Single(a.Recorded, r => r.Kind == ActivityKinds.Died);
        Assert.InRange(d.Confidence, a.Options.MediumConfidence, a.Options.HighConfidence - 0.01);
        var digest = a.Tracker.Digest.Since(a.F.Clock.GetUtcNow() - TimeSpan.FromMinutes(5));
        output.WriteLine(digest);
        Assert.Contains("probably", digest);
        Assert.Contains("was slain by Zombie on main island", digest);
    }

    [Fact]
    public void System_death_line_is_never_addressed_to_the_bot()
    {
        var (h, c) = TestBot.Create();
        h.Runtime.Chat.OnServerChat("kiwi fell out of the world");
        Assert.DoesNotContain(h.Runtime.Events.Snapshot(), e => e.Kind is EventKind.Chat or EventKind.Whisper);
        h.Dispose();
    }
}
